using AiChatAssistant.Api.Dtos.Chat;
using FluentValidation;

namespace AiChatAssistant.Api.Validators;

public class SendMessageRequestValidator : AbstractValidator<SendMessageRequest>
{
    public SendMessageRequestValidator()
    {
        RuleFor(r => r.Content)
            .NotEmpty()
            // Message.Content is a TEXT column (effectively unbounded) - this cap is a sanity/cost
            // guard against an enormous single prompt, not a storage limit.
            .MaximumLength(8000);
    }
}
