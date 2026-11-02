namespace AiChatAssistant.Api.Messaging;

/// <summary>
/// The body of a queued password-reset email. It holds the raw reset link, which is a credential
/// for one hour - so the broker must stay internal-only (no published port, its own password), and
/// the message must not outlive the link (<see cref="EmailQueueOptions.MessageTtlMinutes"/>).
/// </summary>
///
/// <para>The same message type also carries the "confirm your email" mail (<see cref="Kind"/> = <see cref="VerifyKind"/>), so the queue,
/// its retries and its dead-letter handling are shared. The field is still called <c>ResetLink</c> on the wire so messages already
/// queued by an older version keep working; for a verification mail it holds the verification link. A message without <c>Kind</c> is a reset.</para>
public sealed record PasswordResetEmail(string ToEmail, string ToName, string ResetLink, string Kind = PasswordResetEmail.ResetKind)
{
    public const string ResetKind = "reset";
    public const string VerifyKind = "verify";
}
