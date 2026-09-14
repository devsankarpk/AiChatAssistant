using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;

namespace AiChatAssistant.Api.Common;

public static class ControllerExtensions
{
    /// <summary>Turns a failed FluentValidation result into the standard `{ error: { code, message } }` 400.</summary>
    public static ActionResult ValidationError(this ControllerBase controller, ValidationResult validation)
    {
        var message = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage));
        return controller.BadRequest(new ErrorResponse("validation_error", message));
    }
}
