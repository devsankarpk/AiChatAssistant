using AiChatAssistant.Api.Dtos.Auth;
using FluentValidation;

namespace AiChatAssistant.Api.Validators;

public class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(r => r.Token)
            .NotEmpty();

        RuleFor(r => r.NewPassword)
            .NotEmpty()
            .MinimumLength(8)
            .WithMessage("Password must be at least 8 characters long.");
    }
}
