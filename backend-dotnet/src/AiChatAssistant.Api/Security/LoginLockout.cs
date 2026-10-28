using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AiChatAssistant.Api.Security;

/// <param name="Locked">The email may not try to log in right now.</param>
/// <param name="RetryAfter">How long until it may, when locked.</param>
public readonly record struct LockoutState(bool Locked, TimeSpan RetryAfter);

/// <summary>
/// Locks an email after repeated failed logins. Counted per <b>email text</b>, not per account, so an address that has no account
/// locks exactly like one that has: the lockout cannot be used to find out which emails are registered.
/// </summary>
public interface ILoginLockout
{
    Task<LockoutState> CheckAsync(string email);
    Task<LockoutState> RecordFailureAsync(string email);
    Task ClearAsync(string email);
}

public sealed class LoginLockout : ILoginLockout
{
    private readonly IRateLimitCounter _counter;
    private readonly LockoutOptions _options;

    public LoginLockout(IRateLimitCounter counter, IOptions<LockoutOptions> options)
    {
        _counter = counter;
        _options = options.Value;
    }

    public async Task<LockoutState> CheckAsync(string email)
    {
        if (!_options.Enabled)
        {
            return default;
        }

        var current = await _counter.PeekAsync(KeyFor(email));
        return State(current);
    }

    public async Task<LockoutState> RecordFailureAsync(string email)
    {
        if (!_options.Enabled)
        {
            return default;
        }

        var current = await _counter.HitAsync(KeyFor(email), TimeSpan.FromMinutes(_options.LockoutMinutes));
        return State(current);
    }

    public Task ClearAsync(string email) => _options.Enabled ? _counter.ResetAsync(KeyFor(email)) : Task.CompletedTask;

    private LockoutState State(RateLimitCount count) =>
        count.Count >= _options.MaxFailedAttempts ? new LockoutState(true, count.Remaining) : default;

    // Hashed so the shared store never holds an email address.
    private static string KeyFor(string email) =>
        "lockout:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())))[..32];
}
