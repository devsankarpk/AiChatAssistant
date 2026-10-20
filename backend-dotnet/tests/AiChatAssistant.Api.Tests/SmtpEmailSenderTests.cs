using AiChatAssistant.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiChatAssistant.Api.Tests;

/// <summary>
/// Only the configuration checks: they throw before any network connection is attempted, so no real
/// SMTP server is ever contacted. Sending itself is never tested against a real mail server.
/// </summary>
public class SmtpEmailSenderTests
{
    private static SmtpEmailSender Create(string? username, string? password)
    {
        var settings = new Dictionary<string, string?>();
        if (username is not null) settings["Smtp:Username"] = username;
        if (password is not null) settings["Smtp:Password"] = password;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new SmtpEmailSender(configuration, NullLogger<SmtpEmailSender>.Instance);
    }

    private static Task Send(SmtpEmailSender sender) =>
        sender.SendPasswordResetEmailAsync("to@example.com", "To", "http://localhost/reset", CancellationToken.None);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankOrMissingUsername_FailsFastWithAClearMessage_NotAGmailLoginAttempt(string? username)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Send(Create(username, "app-password")));

        Assert.Contains("Smtp:Username", ex.Message);
        Assert.Contains("Smtp__Username", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankOrMissingPassword_FailsFastWithAClearMessage(string? password)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Send(Create("me@example.com", password)));

        Assert.Contains("Smtp:Password", ex.Message);
        Assert.Contains("Smtp__Password", ex.Message);
    }
}
