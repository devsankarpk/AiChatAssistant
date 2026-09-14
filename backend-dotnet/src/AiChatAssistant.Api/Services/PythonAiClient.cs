using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AiChatAssistant.Api.Services;

public class PythonAiClient : IPythonAiClient
{
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // TaskCanceledException here means our own client-side timeout (HttpClient.Timeout) fired,
            // not that the caller cancelled - a genuine cancellationToken cancellation propagates
            // as OperationCanceledException matching the token, which this filter doesn't catch.
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
