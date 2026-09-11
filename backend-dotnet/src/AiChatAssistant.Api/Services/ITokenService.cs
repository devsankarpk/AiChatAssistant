using AiChatAssistant.Api.Models;

namespace AiChatAssistant.Api.Services;

public interface ITokenService
{
    /// <summary>Issues a signed JWT for the given user, carrying <c>sub</c>, <c>email</c>, and one <c>role</c> claim per role.</summary>
    (string Token, DateTime ExpiresAt) CreateToken(User user, IReadOnlyCollection<string> roles);
}
