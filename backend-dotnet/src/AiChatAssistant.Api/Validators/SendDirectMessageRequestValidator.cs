using AiChatAssistant.Api.Dtos.Direct;
using FluentValidation;

namespace AiChatAssistant.Api.Validators;

public class SendDirectMessageRequestValidator : AbstractValidator<SendDirectMessageRequest>
{
    public SendDirectMessageRequestValidator()
    {
        RuleFor(r => r.Content)
            .NotEmpty()
            .WithMessage("Write a message before sending.")
            // Same cap as the AI chat: a sanity guard against one enormous message, not a storage limit.
            .MaximumLength(8000)
            .WithMessage("Messages can be at most 8000 characters.");
    }
}
