using AiChatAssistant.Api.Messaging;
using AiChatAssistant.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AiChatAssistant.Api.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public ManualTimeProvider(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Password-reset email through the queue (Phase 17): the decision logic, options and wiring.
/// The RabbitMQ glue itself (publish, consume, retry and dead-letter queues) is covered against a real
/// broker in the Docker run, see TESTING.md.</summary>
public class PasswordResetEmailProcessorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private readonly Mock<IEmailSender> _sender = new();
    private readonly ManualTimeProvider _clock = new(Start);

    private PasswordResetEmailProcessor Create(params int[] retryDelays) =>
        new(_sender.Object,
            Options.Create(new EmailQueueOptions { RetryDelaysSeconds = retryDelays }),
            _clock,
            NullLogger<PasswordResetEmailProcessor>.Instance);

    private static readonly PasswordResetEmail Message = new("ada@example.com", "Ada", "http://x/reset-password?token=abc");

    [Fact]
    public async Task Sends_AndReportsSent_WhenTheSenderSucceeds()
    {
        var result = await Create(10, 60).ProcessAsync(Message, 0, Start.AddHours(1), CancellationToken.None);

        Assert.Equal(EmailOutcome.Sent, result.Outcome);
        _sender.Verify(s => s.SendPasswordResetEmailAsync("ada@example.com", "Ada", Message.ResetLink, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 60)]
    public async Task AskForARetryWithTheConfiguredDelay_WhenSendingFailsAndRetriesRemain(int failedSoFar, int expectedDelaySeconds)
    {
        _sender.Setup(s => s.SendPasswordResetEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP is down"));

        var result = await Create(10, 60).ProcessAsync(Message, failedSoFar, Start.AddHours(1), CancellationToken.None);

        Assert.Equal(EmailOutcome.Retry, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(expectedDelaySeconds), result.Delay);
        Assert.Equal("SMTP is down", result.Error);
    }

    [Fact]
    public async Task DeadLetters_WhenSendingFailsAndNoRetriesRemain()
    {
        _sender.Setup(s => s.SendPasswordResetEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP is down"));

        var result = await Create(10, 60).ProcessAsync(Message, 2, Start.AddHours(1), CancellationToken.None);

        Assert.Equal(EmailOutcome.Dead, result.Outcome);
    }

    [Fact]
    public async Task DeadLettersAtOnce_WhenNoRetriesAreConfigured()
    {
        _sender.Setup(s => s.SendPasswordResetEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP is down"));

        var result = await Create().ProcessAsync(Message, 0, Start.AddHours(1), CancellationToken.None);

        Assert.Equal(EmailOutcome.Dead, result.Outcome);
    }

    [Fact]
    public async Task DropsAMessageWhoseLinkHasExpired_WithoutSendingIt()
    {
        _clock.Advance(TimeSpan.FromMinutes(61));

        var result = await Create(10, 60).ProcessAsync(Message, 0, Start.AddHours(1), CancellationToken.None);

        Assert.Equal(EmailOutcome.Expired, result.Outcome);
        _sender.Verify(s => s.SendPasswordResetEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PropagatesCancellation_RatherThanTreatingItAsAFailedSend()
    {
        _sender.Setup(s => s.SendPasswordResetEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Create(10, 60).ProcessAsync(Message, 0, Start.AddHours(1), CancellationToken.None));
    }
}

public class EmailQueueWiringTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static ServiceProvider Build(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration); // the real host registers IConfiguration itself
        services.AddEmailDelivery(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void WithoutABrokerHost_EmailIsSentInline_AndNoWorkerIsRegistered()
    {
        using var provider = Build(Config(("RabbitMq:Host", "")));
        using var scope = provider.CreateScope();

        Assert.IsType<InlineEmailQueue>(scope.ServiceProvider.GetRequiredService<IEmailQueue>());
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), s => s is EmailSendWorker);
    }

    [Fact]
    public async Task WithABrokerHost_EmailIsQueued_AndTheWorkerIsRegistered_WithoutConnectingYet()
    {
        await using var provider = Build(Config(("RabbitMq:Host", "rabbitmq"), ("RabbitMq:Username", "u"), ("RabbitMq:Password", "p")));

        Assert.IsType<RabbitMqEmailQueue>(provider.GetRequiredService<IEmailQueue>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is EmailSendWorker);
    }

    [Theory]
    [InlineData("", "p")]
    [InlineData("u", "")]
    [InlineData("  ", "  ")]
    public void ABrokerHostWithoutCredentials_FailsFastAtStartup_WithAClearMessage(string username, string password)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Config(
            ("RabbitMq:Host", "rabbitmq"), ("RabbitMq:Username", username), ("RabbitMq:Password", password))));

        Assert.Contains("RabbitMq__Password", ex.Message);
    }

    [Theory]
    [InlineData(null, typeof(SmtpEmailSender))]
    [InlineData("smtp", typeof(SmtpEmailSender))]
    [InlineData("Console", typeof(ConsoleEmailSender))]
    public void EmailProvider_PicksTheSender(string? provider, Type expected)
    {
        using var built = Build(provider is null ? Config() : Config(("Email:Provider", provider)));
        using var scope = built.CreateScope();

        Assert.IsType(expected, scope.ServiceProvider.GetRequiredService<IEmailSender>());
    }

    [Fact]
    public void AnUnknownEmailProvider_FailsFast()
    {
        Assert.Throws<InvalidOperationException>(() => Build(Config(("Email:Provider", "carrier-pigeon"))));
    }

    [Fact]
    public void RetryDelaysAreBoundFromConfiguration_AndAnEnvironmentOverrideReplacesByIndex()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:RetryDelaysSeconds:0"] = "10",
                ["Email:RetryDelaysSeconds:1"] = "60",
            })
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Email:RetryDelaysSeconds:0"] = "2" })
            .Build();
        using var provider = Build(config);

        var options = provider.GetRequiredService<IOptions<EmailQueueOptions>>().Value;

        Assert.Equal(new[] { 2, 60 }, options.RetryDelaysSeconds);
    }
}

public class EmailTopologyTests
{
    [Fact]
    public void RetryQueueNamesCarryTheirDelay_SoChangingTheDelaysNeverClashesWithAnExistingQueue()
    {
        Assert.Equal("email.retry.10s", EmailTopology.RetryQueue(10));
        Assert.Equal("email.retry.60s", EmailTopology.RetryQueue(60));
    }

    [Fact]
    public async Task InlineQueue_DeliversThroughTheSender()
    {
        var sender = new Mock<IEmailSender>();

        await new InlineEmailQueue(sender.Object).EnqueuePasswordResetAsync("a@b.c", "A", "http://l", CancellationToken.None);

        sender.Verify(s => s.SendPasswordResetEmailAsync("a@b.c", "A", "http://l", It.IsAny<CancellationToken>()), Times.Once);
    }
}
