namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// The body of a queued password-reset email. It holds the raw reset link, which is a credential
/// for one hour - so the broker must stay internal-only (no published port, its own password), and
/// the message must not outlive the link (<see cref="EmailQueueOptions.MessageTtlMinutes"/>).
/// </summary>
public sealed record PasswordResetEmail(string ToEmail, string ToName, string ResetLink);
