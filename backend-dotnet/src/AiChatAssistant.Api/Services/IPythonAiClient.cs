namespace AiChatAssistant.Api.Services;

public record PythonHistoryMessage(string Role, string Content);

public record PythonGenerateResult(string Reply, int TokensUsed);

/// <summary>
/// The .NET side of the `.NET -> Python` contract in CLAUDE.md. This service is stateless and
/// has no user concept - callers always pass the full history + prompt on every call.
/// </summary>
public interface IPythonAiClient
{
    Task<PythonGenerateResult> GenerateAsync(
        Guid sessionId,
        IReadOnlyList<PythonHistoryMessage> history,
        string prompt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Thrown for anything that stops a Python call from producing a reply - unreachable service,
/// timeout, or a non-success response. Callers map this straight to 503 for Angular (never 500),
/// per CLAUDE.md: "A Python failure ... must surface to Angular as 503, never 500."
/// </summary>
public class PythonServiceUnavailableException : Exception
{
    public PythonServiceUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
