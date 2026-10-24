using AiChatAssistant.Api.Messaging;
using AiChatAssistant.Api.Realtime;
using AiChatAssistant.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiChatAssistant.Api.Tests;

/// <summary>Bad configuration must stop the app at start-up with a message that names the setting, not fail later on a user's request. Bare ServiceCollection, no host and no broker.</summary>
public class StartupConfigurationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void AsyncReplies_WithoutABrokerHost_FailsAtStartup_NamingBothSettings()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddBrokerFeatures(Config(("Chat:AsyncReplies", "true"))));

        Assert.Contains("Chat:AsyncReplies", ex.Message);
        Assert.Contains("RabbitMq:Host", ex.Message);
    }

    [Fact]
    public void ABrokerHost_WithoutCredentials_FailsAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEmailDelivery(Config(("RabbitMq:Host", "broker.test"))));

        Assert.Contains("RabbitMq:Username", ex.Message);
    }

    [Fact]
    public void ABrokerHost_WithBlankCredentials_FailsAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEmailDelivery(Config(("RabbitMq:Host", "broker.test"), ("RabbitMq:Username", " "), ("RabbitMq:Password", ""))));
    }

    [Fact]
    public void AnUnknownEmailProvider_FailsAtStartup_ListingTheSupportedOnes()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEmailDelivery(Config(("Email:Provider", "carrier-pigeon"))));

        Assert.Contains("carrier-pigeon", ex.Message);
        Assert.Contains("smtp", ex.Message);
        Assert.Contains("console", ex.Message);
    }

    [Theory]
    [InlineData("smtp", typeof(SmtpEmailSender))]
    [InlineData("console", typeof(ConsoleEmailSender))]
    [InlineData("CONSOLE", typeof(ConsoleEmailSender))]
    public void TheEmailProvider_PicksTheSender(string provider, Type expected)
    {
        var services = new ServiceCollection().AddEmailDelivery(Config(("Email:Provider", provider)));

        Assert.Equal(expected, services.Single(d => d.ServiceType == typeof(IEmailSender)).ImplementationType);
    }

    [Fact]
    public void WithoutABroker_PasswordResetEmailIsSentInline_AndNoWorkerIsRegistered()
    {
        var services = new ServiceCollection().AddEmailDelivery(Config());

        Assert.Equal(typeof(InlineEmailQueue), services.Single(d => d.ServiceType == typeof(IEmailQueue)).ImplementationType);
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(EmailSendWorker));
    }

    [Fact]
    public void WithoutABroker_NoWorkersAreRegistered_AndTheOutboxIsANoOp()
    {
        var services = new ServiceCollection().AddBrokerFeatures(Config());

        Assert.Equal(typeof(NoOpOutbox), services.Single(d => d.ServiceType == typeof(IOutbox)).ImplementationType);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAiReplyQueue));
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(AiReplyWorker) || d.ImplementationType == typeof(OutboxDispatcher));
    }

    [Fact]
    public void WithoutRedis_PresenceStaysInMemory()
    {
        var services = new ServiceCollection().AddRealtime(Config());

        Assert.Equal(typeof(InMemoryPresenceTracker), services.Single(d => d.ServiceType == typeof(IPresenceTracker)).ImplementationType);
    }
}
