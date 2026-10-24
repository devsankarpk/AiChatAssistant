using System.Net;
using System.Text;
using System.Text.Json;
using AiChatAssistant.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiChatAssistant.Api.Tests;

/// <summary>PythonAiClient against a stubbed HttpMessageHandler: the request is what the Python contract says, and every failure becomes PythonServiceUnavailableException. No network.</summary>
public class PythonAiClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await _respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (PythonAiClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://python.test/"), Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("X-Internal-Key", "secret-key");
        return (new PythonAiClient(http, NullLogger<PythonAiClient>.Instance), handler);
    }

    private static readonly IReadOnlyList<PythonHistoryMessage> History = new[]
    {
        new PythonHistoryMessage("user", "hi"),
        new PythonHistoryMessage("assistant", "hello"),
    };

    [Fact]
    public async Task Request_IsAPostToGenerate_WithSnakeCaseBody_AndTheInternalKey()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"reply":"ok","tokens_used":3}""")));
        var session = Guid.NewGuid();

        await client.GenerateAsync(session, History, "next question", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/generate", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("secret-key", handler.Request.Headers.GetValues("X-Internal-Key").Single());
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal(session.ToString(), doc.RootElement.GetProperty("session_id").GetString());
        Assert.Equal("next question", doc.RootElement.GetProperty("prompt").GetString());
        var history = doc.RootElement.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        Assert.Equal("user", history[0].GetProperty("role").GetString());
        Assert.Equal("hello", history[1].GetProperty("content").GetString());
        Assert.False(doc.RootElement.TryGetProperty("sessionId", out _));
    }

    [Fact]
    public async Task Response_IsMappedFromReplyAndTokensUsed()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"reply":"the answer","tokens_used":123}""")));

        var result = await client.GenerateAsync(Guid.NewGuid(), History, "q", CancellationToken.None);

        Assert.Equal("the answer", result.Reply);
        Assert.Equal(123, result.TokensUsed);
    }

    [Fact]
    public async Task AnEmptyHistory_IsSentAsAnEmptyArray()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"reply":"r","tokens_used":1}""")));

        await client.GenerateAsync(Guid.NewGuid(), Array.Empty<PythonHistoryMessage>(), "q", CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("history").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("history").GetArrayLength());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ANonSuccessStatus_BecomesPythonServiceUnavailable(HttpStatusCode status)
    {
        var (client, _) = Create((_, _) => Task.FromResult(Json(status, """{"detail":"nope"}""")));

        var ex = await Assert.ThrowsAsync<PythonServiceUnavailableException>(() => client.GenerateAsync(Guid.NewGuid(), History, "q", CancellationToken.None));

        Assert.Contains(((int)status).ToString(), ex.Message);
    }

    [Fact]
    public async Task AConnectionFailure_BecomesPythonServiceUnavailable_WithTheCauseAttached()
    {
        var (client, _) = Create((_, _) => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<PythonServiceUnavailableException>(() => client.GenerateAsync(Guid.NewGuid(), History, "q", CancellationToken.None));

        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task OurOwnTimeout_BecomesPythonServiceUnavailable()
    {
        var (client, _) = Create(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return Json(HttpStatusCode.OK, "{}"); }, timeout: TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<PythonServiceUnavailableException>(() => client.GenerateAsync(Guid.NewGuid(), History, "q", CancellationToken.None));
    }

    [Fact]
    public async Task ACallerCancellation_IsNotDisguisedAsAnAiOutage()
    {
        using var cts = new CancellationTokenSource();
        var (client, _) = Create(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return Json(HttpStatusCode.OK, "{}"); });
        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GenerateAsync(Guid.NewGuid(), History, "q", cts.Token));
    }

    [Fact]
    public async Task ANullJsonBody_BecomesPythonServiceUnavailable()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "null")));

        await Assert.ThrowsAsync<PythonServiceUnavailableException>(() => client.GenerateAsync(Guid.NewGuid(), History, "q", CancellationToken.None));
    }
}
