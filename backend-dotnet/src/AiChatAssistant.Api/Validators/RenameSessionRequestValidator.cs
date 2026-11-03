using AiChatAssistant.Api.Dtos.Chat;
using FluentValidation;

namespace AiChatAssistant.Api.Validators;

public class RenameSessionRequestValidator : AbstractValidator<RenameSessionRequest>
{
    public RenameSessionRequestValidator()
    {
        RuleFor(r => r.Title)
            .NotEmpty().WithMessage("Give the chat a name.")
            .Must(t => t is null || t.Trim().Length > 0).WithMessage("Give the chat a name.")
            .MaximumLength(200);
    }
}
