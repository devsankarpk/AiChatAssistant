namespace AiChatAssistant.Api.Services;

public record PythonHistoryMessage(string Role, string Content);

public record PythonGenerateResult(string Reply, int TokensUsed);

/// <summary>One piece of a streamed reply: some <see cref="Text"/>, or (last) the <see cref="TokensUsed"/> total.</summary>
public record PythonStreamChunk(string Text, int? TokensUsed = null);

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

    /// <summary>
    /// Streams the reply as it is written. Nothing is requested until the first <c>MoveNextAsync</c>, which throws
    /// <see cref="PythonServiceUnavailableException"/> if the AI could not start; the same exception ends a stream that
    /// fails part-way or outlives <see cref="PythonAiClient.StreamTimeout"/>. A cancelled token ends it with an
    /// <see cref="OperationCanceledException"/> (the caller stopped listening), never an outage.
    /// </summary>
    IAsyncEnumerable<PythonStreamChunk> StreamAsync(
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
