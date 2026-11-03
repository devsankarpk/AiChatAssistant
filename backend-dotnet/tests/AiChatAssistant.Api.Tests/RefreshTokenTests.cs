using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Tests;

/// <summary>Phase 23: the rotation rules of refresh tokens, with a real in-memory database and a clock the test moves by hand.</summary>
public class RefreshTokenServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly ManualTimeProvider _clock = new(Start);
    private readonly RefreshTokenOptions _options = new() { RefreshTokenDays = 14, ReuseGraceSeconds = 10 };
    private readonly User _ada;

    public RefreshTokenServiceTests()
    {
        _ada = new User { Name = "Ada", Email = "ada@example.com", PasswordHash = "x" };
        _db.Users.Add(_ada);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private RefreshTokenService Service() => Refresh.Service(_db, _clock, _options);

    [Fact]
    public async Task Issue_StoresOnlyAHash_AndTheTokenLivesForTheConfiguredDays()
    {
        var (token, expires) = await Service().IssueAsync(_ada.Id, default);

        var row = await _db.RefreshTokens.SingleAsync();
        Assert.NotEqual(token, row.TokenHash);
        Assert.DoesNotContain(token, row.TokenHash);
        Assert.Equal(Start.UtcDateTime.AddDays(14), expires);
        Assert.Equal(64, row.TokenHash.Length);
    }

    [Fact]
    public async Task Rotate_GivesANewToken_InTheSameFamily_AndMarksTheOldOneUsed()
    {
        var (first, _) = await Service().IssueAsync(_ada.Id, default);

        var result = await Service().RotateAsync(first, default);

        Assert.Equal(RefreshOutcome.Rotated, result.Outcome);
        Assert.NotEqual(first, result.NewToken);
        Assert.Equal(_ada.Id, result.User!.Id);
        var rows = await _db.RefreshTokens.OrderBy(t => t.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].FamilyId, rows[1].FamilyId);
        Assert.NotNull(rows[0].UsedAt);
        Assert.Null(rows[1].UsedAt);
    }

    [Fact]
    public async Task TheNewToken_WorksAgain_SoAnActivePersonStaysSignedIn()
    {
        var (first, _) = await Service().IssueAsync(_ada.Id, default);
        var second = (await Service().RotateAsync(first, default)).NewToken!;
        _clock.Advance(TimeSpan.FromDays(13));

        var third = await Service().RotateAsync(second, default);

        Assert.Equal(RefreshOutcome.Rotated, third.Outcome);
    }

    [Fact]
    public async Task ReusingAnOldToken_AfterTheGraceWindow_RevokesTheWholeFamily()
    {
        var (first, _) = await Service().IssueAsync(_ada.Id, default);
        var second = (await Service().RotateAsync(first, default)).NewToken!;
        _clock.Advance(TimeSpan.FromSeconds(30));

        var replay = await Service().RotateAsync(first, default);

        Assert.Equal(RefreshOutcome.ReuseDetected, replay.Outcome);
        // The thief and the owner are both signed out: the newest token is now dead too.
        Assert.Equal(RefreshOutcome.Invalid, (await Service().RotateAsync(second, default)).Outcome);
        Assert.All(await _db.RefreshTokens.ToListAsync(), t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task ReusingAnOldToken_InsideTheGraceWindow_IsRefusedButKeepsTheFamily()
    {
        var (first, _) = await Service().IssueAsync(_ada.Id, default);
        var second = (await Service().RotateAsync(first, default)).NewToken!;
        _clock.Advance(TimeSpan.FromSeconds(3)); // the second tab arrives a moment later

        var racer = await Service().RotateAsync(first, default);

        Assert.Equal(RefreshOutcome.Invalid, racer.Outcome);
        Assert.Equal(RefreshOutcome.Rotated, (await Service().RotateAsync(second, default)).Outcome);
    }

    [Fact]
    public async Task AnExpiredToken_IsInvalid()
    {
        var (token, _) = await Service().IssueAsync(_ada.Id, default);
        _clock.Advance(TimeSpan.FromDays(14).Add(TimeSpan.FromSeconds(1)));

        Assert.Equal(RefreshOutcome.Invalid, (await Service().RotateAsync(token, default)).Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task AnUnknownToken_IsInvalid(string token)
    {
        Assert.Equal(RefreshOutcome.Invalid, (await Service().RotateAsync(token, default)).Outcome);
    }

    [Fact]
    public async Task Revoke_EndsThatLoginOnly_AndIsIdempotent()
    {
        var (phone, _) = await Service().IssueAsync(_ada.Id, default);
        var (laptop, _) = await Service().IssueAsync(_ada.Id, default);

        await Service().RevokeAsync(phone, default);
        await Service().RevokeAsync(phone, default);
        await Service().RevokeAsync("never-issued", default);

        Assert.Equal(RefreshOutcome.Invalid, (await Service().RotateAsync(phone, default)).Outcome);
        Assert.Equal(RefreshOutcome.Rotated, (await Service().RotateAsync(laptop, default)).Outcome);
    }

    [Fact]
    public async Task Revoke_AlsoKillsTheNewerTokensOfTheSameLogin()
    {
        var (first, _) = await Service().IssueAsync(_ada.Id, default);
        var second = (await Service().RotateAsync(first, default)).NewToken!;

        await Service().RevokeAsync(first, default); // an old copy is enough to end the login

        Assert.Equal(RefreshOutcome.Invalid, (await Service().RotateAsync(second, default)).Outcome);
    }

    [Fact]
    public async Task RevokeAll_EndsEveryLoginOfThatUser_AndNobodyElses()
    {
        var bob = new User { Name = "Bob", Email = "bob@example.com", PasswordHash = "x" };
        _db.Users.Add(bob);
        await _db.SaveChangesAsync();
        var (a1, _) = await Service().IssueAsync(_ada.Id, default);
        var (a2, _) = await Service().IssueAsync(_ada.Id, default);
        var (b1, _) = await Service().IssueAsync(bob.Id, default);

        await Service().RevokeAllAsync(_ada.Id, default);

        Assert.Equal(RefreshOutcome.Invalid, (await Service().RotateAsync(a1, default)).Outcome);
        Assert.Equal(RefreshOutcome.Invalid, (await Service().RotateAsync(a2, default)).Outcome);
        Assert.Equal(RefreshOutcome.Rotated, (await Service().RotateAsync(b1, default)).Outcome);
    }

    [Fact]
    public async Task EveryIssuedToken_IsDifferent()
    {
        var tokens = new HashSet<string>();
        for (var i = 0; i < 50; i++) tokens.Add((await Service().IssueAsync(_ada.Id, default)).Token);
        Assert.Equal(50, tokens.Count);
    }
}
