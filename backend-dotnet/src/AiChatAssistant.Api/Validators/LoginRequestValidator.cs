using AiChatAssistant.Api.Dtos.Auth;
using FluentValidation;

namespace AiChatAssistant.Api.Validators;

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(r => r.Email)
            .NotEmpty()
            .EmailAddress();

        RuleFor(r => r.Password)
            .NotEmpty();
    }
}
