using AiChatAssistant.Api.Dtos.Auth;
using FluentValidation;

namespace AiChatAssistant.Api.Validators;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(r => r.Name)
            .NotEmpty()
            .MaximumLength(120);

        RuleFor(r => r.Email)
            .NotEmpty()
            .MaximumLength(256)
            .EmailAddress();

        RuleFor(r => r.Password)
            .NotEmpty()
            .MinimumLength(8)
            .WithMessage("Password must be at least 8 characters long.");
    }
}
