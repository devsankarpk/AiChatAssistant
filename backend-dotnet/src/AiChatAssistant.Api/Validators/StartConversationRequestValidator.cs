using AiChatAssistant.Api.Dtos.Direct;
using FluentValidation;

namespace AiChatAssistant.Api.Validators;

public class StartConversationRequestValidator : AbstractValidator<StartConversationRequest>
{
    public StartConversationRequestValidator()
    {
        RuleFor(r => r.UserId)
            .GreaterThan(0)
            .WithMessage("Choose a person to message.");
    }
}
