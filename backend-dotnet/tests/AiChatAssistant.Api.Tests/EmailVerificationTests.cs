using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiChatAssistant.Api.Controllers;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Auth;
using AiChatAssistant.Api.Messaging;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Security;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using AiChatAssistant.Api.Validators;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AiChatAssistant.Api.Tests;

/// <summary>Optional email verification (Account:RequireEmailVerification) and the registration answers that depend on it.</summary>
public class EmailVerificationTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly Mock<ITokenService> _tokens = new();
    private readonly Mock<IEmailQueue> _email = new();
    private readonly List<string> _links = new();

    public EmailVerificationTests()
    {
        _tokens.Setup(t => t.CreateToken(It.IsAny<User>(), It.IsAny<IReadOnlyCollection<string>>())).Returns(("jwt", DateTime.UtcNow.AddHours(1)));
        _email.Setup(e => e.EnqueueEmailVerificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, link, _) => _links.Add(link)).Returns(Task.CompletedTask);
    }

    public void Dispose() => _db.Dispose();

    private AuthController Create(bool required) => new(
        _db, _tokens.Object, _email.Object,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Frontend:BaseUrl"] = "http://app.test/" }).Build(),
        new RegisterRequestValidator(), new LoginRequestValidator(), new ForgotPasswordRequestValidator(), new ResetPasswordRequestValidator(),
        new LoginLockout(new InMemoryRateLimitCounter(TimeProvider.System), Options.Create(new LockoutOptions())),
        new VerifyEmailRequestValidator(), new ResendVerificationRequestValidator(),
        Options.Create(new AccountOptions { RequireEmailVerification = required }),
        Refresh.Service(_db),
        NullLogger<AuthController>.Instance);

    private static RegisterRequest Reg(string email = "new@example.com") => new() { Name = "New Person", Email = email, Password = "long-enough-1" };
    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    private string RawTokenFromLink(string link) => Uri.UnescapeDataString(link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
    private static string Json(object? value) => JsonSerializer.Serialize(((ObjectResult)value!).Value);

    // ---- verification switched off (the default) ----

    [Fact]
    public async Task WhenOff_RegisteringSignsInAtOnce_AndCountsAsVerified_SoTurningItOnLaterLocksNobodyOut()
    {
        var result = await Create(required: false).Register(Reg(), CancellationToken.None);

        Assert.Equal(201, ((ObjectResult)result.Result!).StatusCode);
        Assert.Equal("jwt", Assert.IsType<AuthResponse>(((ObjectResult)result.Result!).Value).Token);
        Assert.NotNull(_db.Users.Single().EmailVerifiedAt);
        _email.Verify(e => e.EnqueueEmailVerificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenOff_ARegisteredEmailIsStillA409()
    {
        await Create(false).Register(Reg(), CancellationToken.None);

        var again = await Create(false).Register(Reg(), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(again.Result);
    }

    // ---- switched on ----

    [Fact]
    public async Task WhenOn_ANewAddress_Gets202_NoToken_AnUnconfirmedAccount_AndAnEmailWithALink()
    {
        var result = await Create(true).Register(Reg(), CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        Assert.IsType<RegistrationPendingResponse>(accepted.Value);
        var user = _db.Users.Single();
        Assert.Null(user.EmailVerifiedAt);
        var link = Assert.Single(_links);
        Assert.StartsWith("http://app.test/verify-email?token=", link);
        var stored = Assert.Single(_db.EmailVerificationTokens);
        Assert.Equal(Hash(RawTokenFromLink(link)), stored.TokenHash); // only the hash is stored
        Assert.InRange(stored.ExpiresAt, DateTime.UtcNow.AddHours(23), DateTime.UtcNow.AddHours(25));
        _tokens.Verify(t => t.CreateToken(It.IsAny<User>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
    }

    [Fact]
    public async Task WhenOn_ANewAndAnExistingAddress_GetExactlyTheSameAnswer_SoRegisteringRevealsNothing()
    {
        var controller = Create(true);
        var fresh = await controller.Register(Reg("fresh@example.com"), CancellationToken.None);
        var confirmed = AddConfirmed("confirmed@example.com");
        var existing = await controller.Register(Reg("confirmed@example.com"), CancellationToken.None);
        var unconfirmed = await controller.Register(Reg("fresh@example.com"), CancellationToken.None);

        Assert.Equal(Json(fresh.Result), Json(existing.Result));
        Assert.Equal(Json(fresh.Result), Json(unconfirmed.Result));
        Assert.All(new[] { fresh.Result, existing.Result, unconfirmed.Result }, r => Assert.Equal(202, ((ObjectResult)r!).StatusCode));
        _ = confirmed;
    }

    [Fact]
    public async Task WhenOn_ARegistrationForAnAlreadyConfirmedAddress_SendsNothingAndChangesNothing()
    {
        AddConfirmed("confirmed@example.com");

        await Create(true).Register(Reg("confirmed@example.com"), CancellationToken.None);

        Assert.Empty(_links);
        Assert.Empty(_db.EmailVerificationTokens);
        Assert.Equal("Confirmed", _db.Users.Single().Name);
    }

    [Fact]
    public async Task WhenOn_RegisteringAgainBeforeConfirming_SendsAFreshLink_AndInvalidatesTheOldOne()
    {
        var controller = Create(true);
        await controller.Register(Reg(), CancellationToken.None);
        await controller.Register(Reg(), CancellationToken.None);

        Assert.Equal(2, _links.Count);
        var stored = Assert.Single(_db.EmailVerificationTokens);
        Assert.Equal(Hash(RawTokenFromLink(_links[1])), stored.TokenHash);
        Assert.Single(_db.Users);
    }

    [Fact]
    public async Task WhenOn_AMailThatCannotBeQueued_DoesNotChangeTheAnswer()
    {
        _email.Setup(e => e.EnqueueEmailVerificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BrokerUnavailableException("silent"));

        var result = await Create(true).Register(Reg(), CancellationToken.None);

        Assert.IsType<AcceptedResult>(result.Result);
    }

    [Fact]
    public async Task WhenOn_InvalidInputIsStillA400()
    {
        var result = await Create(true).Register(new RegisterRequest { Name = "", Email = "bad", Password = "x" }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ---- login ----

    [Fact]
    public async Task WhenOn_AnUnconfirmedPerson_WithTheRightPassword_Gets403_EmailNotVerified()
    {
        await Create(true).Register(Reg(), CancellationToken.None);

        var result = await Create(true).Login(new LoginRequest { Email = "new@example.com", Password = "long-enough-1" }, CancellationToken.None);

        Assert.Equal(403, ((ObjectResult)result.Result!).StatusCode);
        Assert.Equal("email_not_verified", ((Common.ErrorResponse)((ObjectResult)result.Result!).Value!).Error.Code);
    }

    [Fact]
    public async Task WhenOn_AnUnconfirmedPerson_WithAWrongPassword_Gets401_NotTheVerificationMessage()
    {
        await Create(true).Register(Reg(), CancellationToken.None);

        var result = await Create(true).Login(new LoginRequest { Email = "new@example.com", Password = "wrong" }, CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task WhenOn_AConfirmedPersonLogsInNormally()
    {
        AddConfirmed("confirmed@example.com");

        var result = await Create(true).Login(new LoginRequest { Email = "confirmed@example.com", Password = "long-enough-1" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task WhenOff_AnUnconfirmedAccountCanStillLogIn()
    {
        var user = AddConfirmed("old@example.com");
        user.EmailVerifiedAt = null;
        _db.SaveChanges();

        var result = await Create(false).Login(new LoginRequest { Email = "old@example.com", Password = "long-enough-1" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // ---- confirming ----

    [Fact]
    public async Task TheLink_Confirms_TheAccount_AndCanThenLogIn_ButOnlyOnce()
    {
        await Create(true).Register(Reg(), CancellationToken.None);
        var raw = RawTokenFromLink(_links.Single());

        var first = await Create(true).VerifyEmail(new VerifyEmailRequest { Token = raw }, CancellationToken.None);
        var second = await Create(true).VerifyEmail(new VerifyEmailRequest { Token = raw }, CancellationToken.None);
        var login = await Create(true).Login(new LoginRequest { Email = "new@example.com", Password = "long-enough-1" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(first.Result);
        Assert.NotNull(_db.Users.Single().EmailVerifiedAt);
        Assert.IsType<BadRequestObjectResult>(second.Result);
        Assert.IsType<OkObjectResult>(login.Result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    public async Task ABadLink_Is400(string token)
    {
        var result = await Create(true).VerifyEmail(new VerifyEmailRequest { Token = token }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AnExpiredLink_Is400_AndTheAccountStaysUnconfirmed()
    {
        await Create(true).Register(Reg(), CancellationToken.None);
        _db.EmailVerificationTokens.Single().ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        _db.SaveChanges();

        var result = await Create(true).VerifyEmail(new VerifyEmailRequest { Token = RawTokenFromLink(_links.Single()) }, CancellationToken.None);

        Assert.Equal("invalid_token", ((Common.ErrorResponse)((ObjectResult)result.Result!).Value!).Error.Code);
        Assert.Null(_db.Users.Single().EmailVerifiedAt);
    }

    // ---- asking for another link ----

    [Fact]
    public async Task Resend_AnswersTheSameForUnknown_Confirmed_AndUnconfirmedAddresses()
    {
        await Create(true).Register(Reg("pending@example.com"), CancellationToken.None);
        AddConfirmed("confirmed@example.com");
        _links.Clear();
        var controller = Create(true);

        var unknown = await controller.ResendVerification(new ResendVerificationRequest { Email = "nobody@example.com" }, CancellationToken.None);
        var confirmed = await controller.ResendVerification(new ResendVerificationRequest { Email = "confirmed@example.com" }, CancellationToken.None);
        var pending = await controller.ResendVerification(new ResendVerificationRequest { Email = "pending@example.com" }, CancellationToken.None);

        Assert.Equal(Json(unknown.Result), Json(confirmed.Result));
        Assert.Equal(Json(unknown.Result), Json(pending.Result));
        Assert.Single(_links); // only the unconfirmed address got a mail
    }

    [Fact]
    public async Task Resend_WhenVerificationIsOff_SendsNothing()
    {
        var user = AddConfirmed("someone@example.com");
        user.EmailVerifiedAt = null;
        _db.SaveChanges();

        await Create(false).ResendVerification(new ResendVerificationRequest { Email = "someone@example.com" }, CancellationToken.None);

        Assert.Empty(_links);
    }

    [Fact]
    public async Task Resend_ABadEmail_Is400()
    {
        var result = await Create(true).ResendVerification(new ResendVerificationRequest { Email = "nope" }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ---- resetting a password proves the mailbox ----

    [Fact]
    public async Task ResettingThePassword_ConfirmsTheAddress_AndStampsThePasswordChange()
    {
        await Create(true).Register(Reg(), CancellationToken.None);
        var user = _db.Users.Single();
        _db.PasswordResetTokens.Add(new PasswordResetToken { UserId = user.Id, TokenHash = Hash("reset-raw"), ExpiresAt = DateTime.UtcNow.AddHours(1) });
        _db.SaveChanges();

        var result = await Create(true).ResetPassword(new ResetPasswordRequest { Token = "reset-raw", NewPassword = "another-password-2" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(_db.Users.Single().EmailVerifiedAt);
        Assert.NotNull(_db.Users.Single().PasswordChangedAt);
    }

    private User AddConfirmed(string email)
    {
        var user = new User { Name = "Confirmed", Email = email, PasswordHash = BCrypt.Net.BCrypt.HashPassword("long-enough-1", 4), EmailVerifiedAt = DateTime.UtcNow };
        user.UserRoles.Add(new UserRole { Role = _db.Roles.Single(r => r.Name == Role.User) });
        _db.Users.Add(user);
        _db.SaveChanges();
        return user;
    }
}

/// <summary>The "confirm your email" mail travels the same queue as a reset, so the queue must tell them apart and stay compatible with messages already in it.</summary>
public class EmailKindTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private readonly Mock<IEmailSender> _sender = new();

    private PasswordResetEmailProcessor Processor() =>
        new(_sender.Object, Options.Create(new EmailQueueOptions { RetryDelaysSeconds = new[] { 10 } }), new ManualTimeProvider(Start), NullLogger<PasswordResetEmailProcessor>.Instance);

    [Fact]
    public async Task AVerificationMessage_IsSentAsAVerificationMail_NotAReset()
    {
        var result = await Processor().ProcessAsync(new PasswordResetEmail("a@b.c", "A", "http://l", PasswordResetEmail.VerifyKind), 0, Start.AddHours(1), CancellationToken.None);

        Assert.Equal(EmailOutcome.Sent, result.Outcome);
        _sender.Verify(s => s.SendEmailVerificationAsync("a@b.c", "A", "http://l", It.IsAny<CancellationToken>()), Times.Once);
        _sender.Verify(s => s.SendPasswordResetEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AResetMessage_IsStillSentAsAReset()
    {
        await Processor().ProcessAsync(new PasswordResetEmail("a@b.c", "A", "http://l"), 0, Start.AddHours(1), CancellationToken.None);

        _sender.Verify(s => s.SendPasswordResetEmailAsync("a@b.c", "A", "http://l", It.IsAny<CancellationToken>()), Times.Once);
        _sender.Verify(s => s.SendEmailVerificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AFailedVerificationMail_IsRetriedThenDeadLettered_LikeAReset()
    {
        _sender.Setup(s => s.SendEmailVerificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("smtp down"));
        var message = new PasswordResetEmail("a@b.c", "A", "http://l", PasswordResetEmail.VerifyKind);

        var first = await Processor().ProcessAsync(message, 0, Start.AddHours(1), CancellationToken.None);
        var last = await Processor().ProcessAsync(message, 1, Start.AddHours(1), CancellationToken.None);

        Assert.Equal(EmailOutcome.Retry, first.Outcome);
        Assert.Equal(EmailOutcome.Dead, last.Outcome);
    }

    [Fact]
    public void AMessageAlreadyInTheQueueFromBeforeThisChange_HasNoKind_AndIsStillAReset()
    {
        const string oldWireFormat = "{\"ToEmail\":\"a@b.c\",\"ToName\":\"A\",\"ResetLink\":\"http://l\"}";

        var message = JsonSerializer.Deserialize<PasswordResetEmail>(oldWireFormat);

        Assert.Equal(PasswordResetEmail.ResetKind, message!.Kind);
    }

    [Fact]
    public void TheKindSurvivesTheWire()
    {
        var json = JsonSerializer.Serialize(new PasswordResetEmail("a@b.c", "A", "http://l", PasswordResetEmail.VerifyKind));

        Assert.Equal(PasswordResetEmail.VerifyKind, JsonSerializer.Deserialize<PasswordResetEmail>(json)!.Kind);
    }

    [Fact]
    public async Task TheInlineQueue_DeliversAVerificationMailThroughTheSender()
    {
        await new InlineEmailQueue(_sender.Object).EnqueueEmailVerificationAsync("a@b.c", "A", "http://l", CancellationToken.None);

        _sender.Verify(s => s.SendEmailVerificationAsync("a@b.c", "A", "http://l", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TheSmtpSender_FailsFastForAVerificationMail_WhenTheUsernameIsMissing(string? username)
    {
        var settings = new Dictionary<string, string?> { ["Smtp:Password"] = "p" };
        if (username is not null) settings["Smtp:Username"] = username;
        var sender = new SmtpEmailSender(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), NullLogger<SmtpEmailSender>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendEmailVerificationAsync("a@b.c", "A", "http://l", CancellationToken.None));

        Assert.Contains("Smtp:Username", ex.Message);
    }

    [Fact]
    public void TheDefaults_VerificationIsOff_AndALinkLastsADay()
    {
        Assert.False(new AccountOptions().RequireEmailVerification);
        Assert.Equal(24, new AccountOptions().VerificationLinkHours);
    }
}

public class AccountValidatorTests
{
    private static string Long(int n) => new('a', n);

    [Fact]
    public void ProfileName_Rules()
    {
        var v = new UpdateProfileRequestValidator();
        Assert.True(v.Validate(new Dtos.Account.UpdateProfileRequest { Name = "Ada" }).IsValid);
        Assert.False(v.Validate(new Dtos.Account.UpdateProfileRequest { Name = "" }).IsValid);
        Assert.False(v.Validate(new Dtos.Account.UpdateProfileRequest { Name = "  " }).IsValid);
        Assert.True(v.Validate(new Dtos.Account.UpdateProfileRequest { Name = Long(120) }).IsValid);
        Assert.False(v.Validate(new Dtos.Account.UpdateProfileRequest { Name = Long(121) }).IsValid);
    }

    [Theory]
    [InlineData("current", "12345678", true)]
    [InlineData("current", "1234567", false)]
    [InlineData("", "12345678", false)]
    [InlineData("current", "", false)]
    public void ChangePassword_Rules(string current, string next, bool valid) =>
        Assert.Equal(valid, new ChangePasswordRequestValidator().Validate(new Dtos.Account.ChangePasswordRequest { CurrentPassword = current, NewPassword = next }).IsValid);

    [Fact]
    public void ThePasswordUpperLimitIs128_OnEveryPathThatSetsOne()
    {
        Assert.True(new ChangePasswordRequestValidator().Validate(new Dtos.Account.ChangePasswordRequest { CurrentPassword = "c", NewPassword = Long(128) }).IsValid);
        Assert.False(new ChangePasswordRequestValidator().Validate(new Dtos.Account.ChangePasswordRequest { CurrentPassword = "c", NewPassword = Long(129) }).IsValid);
        Assert.False(new RegisterRequestValidator().Validate(new RegisterRequest { Name = "A", Email = "a@b.co", Password = Long(129) }).IsValid);
        Assert.False(new ResetPasswordRequestValidator().Validate(new ResetPasswordRequest { Token = "t", NewPassword = Long(129) }).IsValid);
    }

    [Fact]
    public void DeleteAccount_NeedsThePassword()
    {
        var v = new DeleteAccountRequestValidator();
        Assert.True(v.Validate(new Dtos.Account.DeleteAccountRequest { Password = "p" }).IsValid);
        Assert.False(v.Validate(new Dtos.Account.DeleteAccountRequest { Password = "" }).IsValid);
    }

    [Fact]
    public void VerifyAndResend_Rules()
    {
        Assert.True(new VerifyEmailRequestValidator().Validate(new VerifyEmailRequest { Token = "t" }).IsValid);
        Assert.False(new VerifyEmailRequestValidator().Validate(new VerifyEmailRequest { Token = "" }).IsValid);
        Assert.True(new ResendVerificationRequestValidator().Validate(new ResendVerificationRequest { Email = "a@b.co" }).IsValid);
        Assert.False(new ResendVerificationRequestValidator().Validate(new ResendVerificationRequest { Email = "nope" }).IsValid);
    }
}
