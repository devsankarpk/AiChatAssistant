using AiChatAssistant.Api.Messaging;
using Microsoft.Extensions.Options;

namespace AiChatAssistant.Api.Tests;

/// <summary>
/// A broker that accepts the connection but never answers (paused, partitioned) must turn into "broker unavailable"
/// after a short time. Found by the black-hole test: forgot-password and the async send answered 500 after 20 s,
/// because the library's own timeout surfaced as a cancellation, which the callers deliberately let through.
/// </summary>
public class BrokerTimeoutTests
{
    private static RabbitMqConnection Connection(int seconds = 1) =>
        new(Options.Create(new RabbitMqOptions { Host = "x", Username = "u", Password = "p", PublishTimeoutSeconds = seconds }));

    [Fact]
    public async Task ABrokerCallThatNeverAnswers_BecomesBrokerUnavailable_NotACancellation()
    {
        await using var connection = Connection(1);
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<BrokerUnavailableException>(() =>
            connection.WithTimeoutAsync(token => Task.Delay(Timeout.Infinite, token), CancellationToken.None));

        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 0.8, 3);
        Assert.Contains("did not answer within 1 s", ex.Message);
        Assert.False(ex is OperationCanceledException);
    }

    [Fact]
    public async Task ACallerWhoCancels_StillGetsAnOrdinaryCancellation()
    {
        await using var connection = Connection(30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connection.WithTimeoutAsync(token => Task.Delay(Timeout.Infinite, token), cts.Token));
    }

    [Fact]
    public async Task ALibraryTimeout_IsAlsoReportedAsBrokerUnavailable()
    {
        await using var connection = Connection(5);

        await Assert.ThrowsAsync<BrokerUnavailableException>(() =>
            connection.WithTimeoutAsync(_ => throw new TimeoutException("continuation timed out"), CancellationToken.None));
    }

    [Fact]
    public async Task AFastCall_RunsNormally_AndOtherErrorsPassThroughUnchanged()
    {
        await using var connection = Connection(5);
        var ran = false;

        await connection.WithTimeoutAsync(_ => { ran = true; return Task.CompletedTask; }, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.WithTimeoutAsync(_ => throw new InvalidOperationException("auth failed"), CancellationToken.None));

        Assert.True(ran);
    }

    [Fact]
    public void TheDefaultTimeout_IsShortEnoughToAnswerARequestPromptly()
    {
        Assert.InRange(new RabbitMqOptions().PublishTimeoutSeconds, 1, 10);
    }
}
