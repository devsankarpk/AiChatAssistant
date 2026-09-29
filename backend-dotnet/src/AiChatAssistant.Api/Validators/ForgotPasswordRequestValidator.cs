using AiChatAssistant.Api.Dtos.Auth;
using FluentValidation;

namespace AiChatAssistant.Api.Validators;

public class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(r => r.Email)
            .NotEmpty()
            .EmailAddress();
    }
}
