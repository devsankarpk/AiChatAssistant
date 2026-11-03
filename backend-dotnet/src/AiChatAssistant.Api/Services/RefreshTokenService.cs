using System.Security.Cryptography;
using System.Text;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiChatAssistant.Api.Services;

public class RefreshTokenService : IRefreshTokenService
{
    private readonly AppDbContext _db;
    private readonly RefreshTokenOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RefreshTokenService> _logger;

    public RefreshTokenService(AppDbContext db, IOptions<RefreshTokenOptions> options, TimeProvider time, ILogger<RefreshTokenService> logger)
    {
        _db = db;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<(string Token, DateTime ExpiresAt)> IssueAsync(int userId, CancellationToken cancellationToken)
    {
        var family = Guid.NewGuid().ToString("N");
        var issued = Add(userId, family);
        await _db.SaveChangesAsync(cancellationToken);
        return issued;
    }

    public async Task<RefreshResult> RotateAsync(string rawToken, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var hash = Hash(rawToken);
        var token = await _db.RefreshTokens
            .Include(t => t.User).ThenInclude(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= now)
        {
            return new RefreshResult(RefreshOutcome.Invalid);
        }

        if (token.UsedAt is { } usedAt)
        {
            if (now - usedAt <= TimeSpan.FromSeconds(_options.ReuseGraceSeconds))
            {
                // Two tabs raced; the other one holds the new token. Refuse this one, keep the family.
                return new RefreshResult(RefreshOutcome.Invalid);
            }

            _logger.LogWarning("Refresh token reuse detected for user {UserId}; revoking the whole login", token.UserId);
            await RevokeFamilyAsync(token.FamilyId, now, cancellationToken);
            return new RefreshResult(RefreshOutcome.ReuseDetected);
        }

        token.UsedAt = now;
        var next = Add(token.UserId, token.FamilyId);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request used this token between our read and our write: it lost the race.
            return new RefreshResult(RefreshOutcome.Invalid);
        }

        return new RefreshResult(RefreshOutcome.Rotated, token.User, next.Token, next.ExpiresAt);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken cancellationToken)
    {
        var hash = Hash(rawToken);
        var familyId = await _db.RefreshTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => t.FamilyId)
            .FirstOrDefaultAsync(cancellationToken);
        if (familyId is not null)
        {
            await RevokeFamilyAsync(familyId, _time.GetUtcNow().UtcDateTime, cancellationToken);
        }
    }

    public async Task RevokeAllAsync(int userId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var live = await _db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var t in live) t.RevokedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RevokeFamilyAsync(string familyId, DateTime now, CancellationToken cancellationToken)
    {
        var live = await _db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var t in live) t.RevokedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private (string Token, DateTime ExpiresAt) Add(int userId, string familyId)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var now = _time.GetUtcNow().UtcDateTime;
        var expires = now.AddDays(_options.RefreshTokenDays);
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = Hash(raw),
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = expires,
        });
        return (raw, expires);
    }

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
