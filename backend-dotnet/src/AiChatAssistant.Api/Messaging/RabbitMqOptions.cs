namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// Connection settings for RabbitMQ (config section "RabbitMq", secrets via user-secrets or
/// RabbitMq__Password-style environment variables, like Jwt:Key). A blank Host means "no broker":
/// every feature that can use a queue then falls back to doing the work inline, exactly as before
/// the broker existed, so a developer can still run the API with nothing but MySQL.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string VirtualHost { get; set; } = "/";

    public bool Enabled => !string.IsNullOrWhiteSpace(Host);

    /// <summary>
    /// Fail fast at start-up when a host is set but the credentials are not - containers pass unset
    /// secrets as empty strings, and the alternative is a confusing authentication error on the
    /// first password reset.
    /// </summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            throw new InvalidOperationException(
                "RabbitMq:Host is set but RabbitMq:Username / RabbitMq:Password are not. Set them via "
                + "user-secrets in Development or the RabbitMq__Username / RabbitMq__Password environment "
                + "variables elsewhere, or clear RabbitMq:Host to run without a broker.");
        }
    }
}
