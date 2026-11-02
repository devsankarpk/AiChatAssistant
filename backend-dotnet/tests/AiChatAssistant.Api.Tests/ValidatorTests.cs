using AiChatAssistant.Api.Dtos.Auth;
using AiChatAssistant.Api.Dtos.Chat;
using AiChatAssistant.Api.Dtos.Direct;
using AiChatAssistant.Api.Validators;

namespace AiChatAssistant.Api.Tests;

public class ValidatorTests
{
    private static string Long(int n) => new('a', n);

    // ---- register ----

    private static RegisterRequest Register(string name = "Ada", string email = "ada@example.com", string password = "longenough") =>
        new() { Name = name, Email = email, Password = password };

    [Fact]
    public void Register_AValidRequest_Passes() =>
        Assert.True(new RegisterRequestValidator().Validate(Register()).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_BlankName_Fails(string name) =>
        Assert.False(new RegisterRequestValidator().Validate(Register(name: name)).IsValid);

    [Fact]
    public void Register_NameLimitIs120()
    {
        Assert.True(new RegisterRequestValidator().Validate(Register(name: Long(120))).IsValid);
        Assert.False(new RegisterRequestValidator().Validate(Register(name: Long(121))).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("missing-at.example.com")]
    [InlineData("two@@example.com")]
    public void Register_BadEmail_Fails(string email) =>
        Assert.False(new RegisterRequestValidator().Validate(Register(email: email)).IsValid);

    [Fact]
    public void Register_EmailLimitIs256()
    {
        var local = Long(244);
        Assert.False(new RegisterRequestValidator().Validate(Register(email: $"{local}x@example.com")).IsValid); // 257
    }

    [Fact]
    public void Register_PasswordMinimumIsEight()
    {
        var validator = new RegisterRequestValidator();
        Assert.True(validator.Validate(Register(password: "12345678")).IsValid);
        var short7 = validator.Validate(Register(password: "1234567"));
        Assert.False(short7.IsValid);
        Assert.Contains("at least 8", short7.Errors.Single().ErrorMessage);
    }

    [Fact]
    public void Register_EmptyPassword_Fails() =>
        Assert.False(new RegisterRequestValidator().Validate(Register(password: "")).IsValid);

    // ---- login ----

    [Theory]
    [InlineData("a@b.com", "x", true)]
    [InlineData("", "x", false)]
    [InlineData("bad", "x", false)]
    [InlineData("a@b.com", "", false)]
    public void Login_Cases(string email, string password, bool valid) =>
        Assert.Equal(valid, new LoginRequestValidator().Validate(new LoginRequest { Email = email, Password = password }).IsValid);

    // ---- forgot / reset password ----

    [Theory]
    [InlineData("a@b.com", true)]
    [InlineData("", false)]
    [InlineData("nope", false)]
    public void ForgotPassword_Cases(string email, bool valid) =>
        Assert.Equal(valid, new ForgotPasswordRequestValidator().Validate(new ForgotPasswordRequest { Email = email }).IsValid);

    [Theory]
    [InlineData("token", "12345678", true)]
    [InlineData("", "12345678", false)]
    [InlineData("token", "1234567", false)]
    [InlineData("token", "", false)]
    public void ResetPassword_Cases(string token, string password, bool valid) =>
        Assert.Equal(valid, new ResetPasswordRequestValidator().Validate(new ResetPasswordRequest { Token = token, NewPassword = password }).IsValid);

    // ---- chat ----

    [Fact]
    public void SendMessage_LimitIs8000()
    {
        var validator = new SendMessageRequestValidator();
        Assert.True(validator.Validate(new SendMessageRequest { Content = Long(8000) }).IsValid);
        Assert.False(validator.Validate(new SendMessageRequest { Content = Long(8001) }).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void SendMessage_BlankOrWhitespaceOnly_Fails(string content) =>
        Assert.False(new SendMessageRequestValidator().Validate(new SendMessageRequest { Content = content }).IsValid);

    [Fact]
    public void CreateSession_TitleIsOptional_AndLimitedTo200()
    {
        var validator = new CreateSessionRequestValidator();
        Assert.True(validator.Validate(new CreateSessionRequest { Title = null }).IsValid);
        Assert.True(validator.Validate(new CreateSessionRequest { Title = Long(200) }).IsValid);
        Assert.False(validator.Validate(new CreateSessionRequest { Title = Long(201) }).IsValid);
    }

    // ---- direct chat ----

    [Fact]
    public void SendDirectMessage_LimitIs8000_AndBlankFails()
    {
        var validator = new SendDirectMessageRequestValidator();
        Assert.True(validator.Validate(new SendDirectMessageRequest { Content = Long(8000) }).IsValid);
        Assert.False(validator.Validate(new SendDirectMessageRequest { Content = Long(8001) }).IsValid);
        Assert.False(validator.Validate(new SendDirectMessageRequest { Content = "  " }).IsValid);
    }
}
