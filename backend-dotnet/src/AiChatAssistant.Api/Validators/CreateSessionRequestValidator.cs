using AiChatAssistant.Api.Dtos.Chat;
using FluentValidation;

namespace AiChatAssistant.Api.Validators;

public class CreateSessionRequestValidator : AbstractValidator<CreateSessionRequest>
{
    public CreateSessionRequestValidator()
    {
        RuleFor(r => r.Title)
            .MaximumLength(200)
            .When(r => r.Title is not null);
    }
}
