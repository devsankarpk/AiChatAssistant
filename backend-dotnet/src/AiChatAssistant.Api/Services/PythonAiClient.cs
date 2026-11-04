using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiChatAssistant.Api.Services;

public class PythonAiClient : IPythonAiClient
{
    /// <summary>
    /// The whole of a streamed reply. <c>HttpClient.Timeout</c> stops counting once the headers arrive, so a stream
    /// needs its own deadline. Python's own stream deadline (50 s, <c>STREAM_DEADLINE_SECONDS</c>) is the inner one and
    /// must stay shorter, so its clean error event is what the user sees (the nested-timeouts rule in CLAUDE.md).
    /// </summary>
    public static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly ILogger<PythonAiClient> _logger;

    public PythonAiClient(HttpClient http, ILogger<PythonAiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<PythonGenerateResult> GenerateAsync(
        Guid sessionId,
        IReadOnlyList<PythonHistoryMessage> history,
        string prompt,
        CancellationToken cancellationToken)
    {
        var request = new GenerateRequestDto
        {
            SessionId = sessionId.ToString(),
            History = history.Select(h => new HistoryItemDto { Role = h.Role, Content = h.Content }).ToList(),
            Prompt = prompt,
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("generate", request, cancellationToken);
        }
        catch (Exception ex) when (ex is (HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            // TaskCanceledException here means our own client-side timeout (HttpClient.Timeout) fired,
            // not that the caller cancelled - a genuine cancellation (the caller's token is cancelled, e.g. the
            // browser went away) is let through as an OperationCanceledException, not reported as an AI outage.
            _logger.LogError(ex, "Could not reach the Python AI service");
            throw new PythonServiceUnavailableException("The AI service is unreachable.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Python AI service returned {StatusCode}: {Body}", (int)response.StatusCode, body);
            throw new PythonServiceUnavailableException($"The AI service returned {(int)response.StatusCode}.");
        }

        var result = await response.Content.ReadFromJsonAsync<GenerateResponseDto>(cancellationToken);
        if (result is null)
        {
            throw new PythonServiceUnavailableException("The AI service returned an empty response.");
        }

        return new PythonGenerateResult(result.Reply, result.TokensUsed);
    }

    public async IAsyncEnumerable<PythonStreamChunk> StreamAsync(
        Guid sessionId,
        IReadOnlyList<PythonHistoryMessage> history,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new GenerateRequestDto
        {
            SessionId = sessionId.ToString(),
            History = history.Select(h => new HistoryItemDto { Role = h.Role, Content = h.Content }).ToList(),
            Prompt = prompt,
        };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(StreamTimeout);

        HttpResponseMessage response;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "generate/stream") { Content = JsonContent.Create(request) };
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        }
        catch (Exception ex) when (ex is (HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not reach the Python AI service for a stream");
            throw new PythonServiceUnavailableException("The AI service is unreachable.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Python AI service returned {StatusCode} for a stream: {Body}", (int)response.StatusCode, body);
                throw new PythonServiceUnavailableException($"The AI service returned {(int)response.StatusCode}.");
            }

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
            string? eventName = null;
            var finished = false;
            while (!finished)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(deadline.Token);
                }
                catch (Exception ex) when (ex is (IOException or HttpRequestException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "The Python AI stream broke or ran past its deadline");
                    throw new PythonServiceUnavailableException("The AI service stopped part-way through the reply.", ex);
                }

                if (line is null)
                {
                    // The stream closed without a `done`: the reply is incomplete, so it is a failure, not a short answer.
                    throw new PythonServiceUnavailableException("The AI service ended the reply early.");
                }
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    eventName = line[7..];
                    continue;
                }
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    continue; // blank separator line or comment
                }

                using var data = JsonDocument.Parse(line[6..]);
                switch (eventName)
                {
                    case "delta":
                        yield return new PythonStreamChunk(data.RootElement.GetProperty("text").GetString() ?? string.Empty);
                        break;
                    case "done":
                        yield return new PythonStreamChunk(string.Empty, data.RootElement.GetProperty("tokens_used").GetInt32());
                        finished = true;
                        break;
                    case "error":
                        var code = data.RootElement.GetProperty("error").GetProperty("code").GetString();
                        _logger.LogWarning("Python AI stream failed part-way: {Code}", code);
                        throw new PythonServiceUnavailableException($"The AI service failed part-way through the reply ({code}).");
                }
            }
        }
    }

    // Explicit [JsonPropertyName] on every property so this stays snake_case exactly as the
    // Python contract expects, independent of whatever naming policy the rest of the API uses.
    private class GenerateRequestDto
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("history")]
        public List<HistoryItemDto> History { get; set; } = new();

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;
    }

    private class HistoryItemDto
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private class GenerateResponseDto
    {
        [JsonPropertyName("reply")]
        public string Reply { get; set; } = string.Empty;

        [JsonPropertyName("tokens_used")]
        public int TokensUsed { get; set; }
    }
}
