using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AiChatAssistant.Api.Tests;

/// <summary>Phase 25: the streaming client (against stubbed bytes), the two streaming endpoints (through the real pipeline), and the token-aware history.</summary>
public class StreamingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public StreamingTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.WithDb(_ => 0);
        _factory.Python.Reset();
    }

    // ---- helpers ----

    private static string NewEmail() => $"st-{Guid.NewGuid():N}@example.com";

    private (User User, HttpClient Client) NewPerson()
    {
        var (user, token) = _factory.CreateUser("Streamer", NewEmail(), "User");
        return (user, _factory.CreateClient().WithToken(token));
    }

    private Guid NewChat(User owner) => _factory.WithDb(db =>
    {
        var s = new ChatSession { Id = Guid.NewGuid(), UserId = owner.Id, Title = "New chat" };
        db.ChatSessions.Add(s);
        db.SaveChanges();
        return s.Id;
    });

    private static async IAsyncEnumerable<PythonStreamChunk> Chunks(IEnumerable<PythonStreamChunk> items, Exception? thenThrow = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var c in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return c;
            await Task.Yield();
        }
        if (thenThrow is not null)
        {
            throw thenThrow;
        }
    }

    private static PythonStreamChunk[] Words(params string[] words) =>
        words.Select(w => new PythonStreamChunk(w)).Append(new PythonStreamChunk("", 9)).ToArray();

    private void Streams(Func<IReadOnlyList<PythonHistoryMessage>, CancellationToken, IAsyncEnumerable<PythonStreamChunk>> make) =>
        _factory.Python
            .Setup(p => p.StreamAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, IReadOnlyList<PythonHistoryMessage> h, string _, CancellationToken ct) => make(h, ct));

    private static List<(string Name, JsonElement Data)> Parse(string body)
    {
        var events = new List<(string, JsonElement)>();
        foreach (var block in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? name = null;
            JsonElement data = default;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event: ")) name = line[7..];
                else if (line.StartsWith("data: ")) data = JsonDocument.Parse(line[6..]).RootElement.Clone();
            }
            events.Add((name!, data));
        }
        return events;
    }

    private static Task<HttpResponseMessage> PostStream(HttpClient c, Guid id, string content) =>
        c.PostAsJsonAsync($"/api/chat/sessions/{id}/messages/stream", new { content });

    private List<Message> Saved(Guid id) => _factory.WithDb(db => db.Messages.Where(m => m.SessionId == id).OrderBy(m => m.Id).ToList());

    // ---- the stream endpoint ----

    [Fact]
    public async Task Stream_SendsTheSavedQuestion_ThenTheWords_ThenTheSavedAnswer()
    {
        var (me, client) = NewPerson();
        var id = NewChat(me);
        Streams((_, ct) => Chunks(Words("Hel", "lo ", "you"), ct: ct));

        var response = await PostStream(client, id, "  Greet me  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString()); // set on every /api response by the security-headers middleware
        var events = Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "user", "delta", "delta", "delta", "done" }, events.Select(e => e.Name));
        Assert.Equal("Greet me", events[0].Data.GetProperty("content").GetString());
        Assert.Equal("Hello you", string.Concat(events.Where(e => e.Name == "delta").Select(e => e.Data.GetProperty("text").GetString())));
        var done = events[^1].Data.GetProperty("assistantMessage");
        Assert.Equal("Hello you", done.GetProperty("content").GetString());
        Assert.Equal("assistant", done.GetProperty("role").GetString());

        var rows = Saved(id);
        Assert.Equal(new[] { "Greet me", "Hello you" }, rows.Select(m => m.Content));
        Assert.Equal(done.GetProperty("id").GetInt32(), rows[1].Id);
        Assert.Equal(9, _factory.WithDb(db => db.UsageLogs.Single(u => u.SessionId == id).TokensUsed));
    }

    [Fact]
    public async Task Stream_TitlesAFreshChatFromTheQuestion()
    {
        var (me, client) = NewPerson();
        var id = NewChat(me);
        Streams((_, ct) => Chunks(Words("ok"), ct: ct));

        await (await PostStream(client, id, "Plan my week")).Content.ReadAsStringAsync();

        Assert.Equal("Plan my week", _factory.WithDb(db => db.ChatSessions.Single(s => s.Id == id).Title));
    }

    [Fact]
    public async Task Stream_FailingBeforeTheFirstWord_Is503_AndTheQuestionStaysSaved()
    {
        var (me, client) = NewPerson();
        var id = NewChat(me);
        Streams((_, ct) => Chunks(Array.Empty<PythonStreamChunk>(), new PythonServiceUnavailableException("down"), ct));

        var response = await PostStream(client, id, "Hello?");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("ai_service_unavailable", error.GetProperty("code").GetString());
        Assert.Equal(new[] { "Hello?" }, Saved(id).Select(m => m.Content));
        Assert.Empty(_factory.WithDb(db => db.UsageLogs.Where(u => u.SessionId == id).ToList()));
    }

    [Fact]
    public async Task Stream_FailingPartWay_EndsWithAnErrorEvent_SavesNoAnswer_AndKeepsTheQuestion()
    {
        var (me, client) = NewPerson();
        var id = NewChat(me);
        Streams((_, ct) => Chunks(new[] { new PythonStreamChunk("par"), new PythonStreamChunk("tial") }, new PythonServiceUnavailableException("cut"), ct));

        var response = await PostStream(client, id, "Hello?");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the status was already sent with the first word
        var events = Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "user", "delta", "delta", "error" }, events.Select(e => e.Name));
        Assert.Equal("ai_service_unavailable", events[^1].Data.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(new[] { "Hello?" }, Saved(id).Select(m => m.Content));
    }

    [Fact]
    public async Task Stream_WhenTheBrowserStops_KeepsWhatWasWritten_AndRecordsUsage()
    {
        var (me, client) = NewPerson();
        var id = NewChat(me);
        var oneWordSent = new TaskCompletionSource();
        Streams((_, ct) => Slow(oneWordSent, ct));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/chat/sessions/{id}/messages/stream") { Content = JsonContent.Create(new { content = "Tell me a story" }) };
        using var cts = new CancellationTokenSource();

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        string? line;
        do { line = await reader.ReadLineAsync(cts.Token); } while (line is not null && !line.StartsWith("data: {\"text\""));
        oneWordSent.TrySetResult();
        cts.Cancel(); // Stop
        response.Dispose();

        var saved = await Eventually(() => Saved(id).Count == 2);
        Assert.True(saved, "the partial answer should be saved after the browser disconnects");
        Assert.Equal("Once ", Saved(id)[1].Content);
        Assert.Equal(1, _factory.WithDb(db => db.UsageLogs.Count(u => u.SessionId == id)));
        Assert.True(_factory.WithDb(db => db.UsageLogs.Single(u => u.SessionId == id).TokensUsed) >= 1);

        static async IAsyncEnumerable<PythonStreamChunk> Slow(TaskCompletionSource oneWordSent, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new PythonStreamChunk("Once ");
            await oneWordSent.Task;
            await Task.Delay(Timeout.Infinite, ct); // never finishes by itself
        }
    }

    [Fact]
    public async Task Stream_WhenTheBrowserStopsBeforeAnyWord_SavesNoAnswer()
    {
        var (me, client) = NewPerson();
        var id = NewChat(me);
        Streams((_, ct) => Never(ct));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        try { await PostStream(client, id, "Hello?").WaitAsync(cts.Token); } catch (OperationCanceledException) { }
        await Task.Delay(300);

        Assert.Equal(new[] { "Hello?" }, Saved(id).Select(m => m.Content));

        static async IAsyncEnumerable<PythonStreamChunk> Never([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    [Fact]
    public async Task Stream_SomeoneElsesChat_Is404_AndNothingIsSaved()
    {
        var (owner, _) = NewPerson();
        var (_, intruder) = NewPerson();
        var id = NewChat(owner);
        Streams((_, ct) => Chunks(Words("x"), ct: ct));

        var response = await PostStream(intruder, id, "Hi");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(Saved(id));
        _factory.Python.Verify(p => p.StreamAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Stream_RejectsEmptyContent_AndNeedsSignIn()
    {
        var (me, client) = NewPerson();
        var id = NewChat(me);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostStream(client, id, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostStream(_factory.CreateClient(), id, "Hi")).StatusCode);
    }

    [Fact]
    public async Task Stream_ADeletedChat_Is404()
    {
        var (me, client) = NewPerson();
        var id = NewChat(me);
        _factory.WithDb(db => { db.ChatSessions.Single(s => s.Id == id).DeletedAt = DateTime.UtcNow; db.SaveChanges(); return 0; });

        Assert.Equal(HttpStatusCode.NotFound, (await PostStream(client, id, "Hi")).StatusCode);
    }

    // ---- regenerate ----

    private Guid ChatWithExchange(User owner, string question = "Q1", string answer = "Old answer") => _factory.WithDb(db =>
    {
        var s = new ChatSession { Id = Guid.NewGuid(), UserId = owner.Id, Title = "T" };
        db.ChatSessions.Add(s);
        db.Messages.Add(new Message { SessionId = s.Id, Role = MessageRole.User, Content = question });
        if (answer.Length > 0) db.Messages.Add(new Message { SessionId = s.Id, Role = MessageRole.Assistant, Content = answer });
        db.SaveChanges();
        return s.Id;
    });

    [Fact]
    public async Task Regenerate_ReplacesTheLastAnswer_AndAsksTheSameQuestion()
    {
        var (me, client) = NewPerson();
        var id = ChatWithExchange(me);
        string? asked = null;
        _factory.Python
            .Setup(p => p.StreamAsync(id, It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, IReadOnlyList<PythonHistoryMessage> _, string q, CancellationToken ct) => { asked = q; return Chunks(Words("New ", "answer"), ct: ct); });

        var response = await client.PostAsync($"/api/chat/sessions/{id}/regenerate", null);

        var events = Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "delta", "delta", "done" }, events.Select(e => e.Name)); // no `user` event: the client already has the question
        Assert.Equal("Q1", asked);
        Assert.Equal(new[] { "Q1", "New answer" }, Saved(id).Select(m => m.Content));
    }

    [Fact]
    public async Task Regenerate_KeepsTheOldAnswer_WhenTheNewOneCannotStart()
    {
        var (me, client) = NewPerson();
        var id = ChatWithExchange(me);
        Streams((_, ct) => Chunks(Array.Empty<PythonStreamChunk>(), new PythonServiceUnavailableException("down"), ct));

        var response = await client.PostAsync($"/api/chat/sessions/{id}/regenerate", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(new[] { "Q1", "Old answer" }, Saved(id).Select(m => m.Content));
    }

    [Fact]
    public async Task Regenerate_KeepsTheOldAnswer_WhenTheNewOneFailsPartWay()
    {
        var (me, client) = NewPerson();
        var id = ChatWithExchange(me);
        Streams((_, ct) => Chunks(new[] { new PythonStreamChunk("half") }, new PythonServiceUnavailableException("cut"), ct));

        var events = Parse(await (await client.PostAsync($"/api/chat/sessions/{id}/regenerate", null)).Content.ReadAsStringAsync());

        Assert.Equal("error", events[^1].Name);
        Assert.Equal(new[] { "Q1", "Old answer" }, Saved(id).Select(m => m.Content));
    }

    [Fact]
    public async Task Regenerate_AnswersAnUnansweredQuestion()
    {
        var (me, client) = NewPerson();
        var id = ChatWithExchange(me, "Lonely question", answer: "");
        Streams((_, ct) => Chunks(Words("Finally"), ct: ct));

        await (await client.PostAsync($"/api/chat/sessions/{id}/regenerate", null)).Content.ReadAsStringAsync();

        Assert.Equal(new[] { "Lonely question", "Finally" }, Saved(id).Select(m => m.Content));
    }

    [Fact]
    public async Task Regenerate_AnEmptyChat_Is409()
    {
        var (me, client) = NewPerson();
        var id = NewChat(me);

        var response = await client.PostAsync($"/api/chat/sessions/{id}/regenerate", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("nothing_to_regenerate", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Regenerate_SomeoneElsesOrADeletedChat_Is404()
    {
        var (owner, ownerClient) = NewPerson();
        var (_, intruder) = NewPerson();
        var id = ChatWithExchange(owner);

        Assert.Equal(HttpStatusCode.NotFound, (await intruder.PostAsync($"/api/chat/sessions/{id}/regenerate", null)).StatusCode);

        _factory.WithDb(db => { db.ChatSessions.Single(s => s.Id == id).DeletedAt = DateTime.UtcNow; db.SaveChanges(); return 0; });
        Assert.Equal(HttpStatusCode.NotFound, (await ownerClient.PostAsync($"/api/chat/sessions/{id}/regenerate", null)).StatusCode);
    }

    [Fact]
    public async Task Regenerate_SendsOnlyTheMessagesBeforeTheQuestionAsHistory()
    {
        var (me, client) = NewPerson();
        var id = _factory.WithDb(db =>
        {
            var s = new ChatSession { Id = Guid.NewGuid(), UserId = me.Id, Title = "T" };
            db.ChatSessions.Add(s);
            foreach (var (role, text) in new[] { (MessageRole.User, "old q"), (MessageRole.Assistant, "old a"), (MessageRole.User, "Q2"), (MessageRole.Assistant, "A2") })
                db.Messages.Add(new Message { SessionId = s.Id, Role = role, Content = text });
            db.SaveChanges();
            return s.Id;
        });
        IReadOnlyList<PythonHistoryMessage>? seen = null;
        Streams((h, ct) => { seen = h; return Chunks(Words("x"), ct: ct); });

        await (await client.PostAsync($"/api/chat/sessions/{id}/regenerate", null)).Content.ReadAsStringAsync();

        Assert.Equal(new[] { "old q", "old a" }, seen!.Select(h => h.Content));
    }

    // ---- token-aware history ----

    private async Task<List<PythonHistoryMessage>> Load(int count, int length, int budget, string prompt = "")
    {
        var (me, _) = NewPerson();
        var id = _factory.WithDb(db =>
        {
            var s = new ChatSession { Id = Guid.NewGuid(), UserId = me.Id, Title = "T" };
            db.ChatSessions.Add(s);
            for (var i = 1; i <= count; i++)
                db.Messages.Add(new Message { SessionId = s.Id, Role = i % 2 == 1 ? MessageRole.User : MessageRole.Assistant, Content = $"{i:D3}".PadRight(length, 'x') });
            db.SaveChanges();
            return s.Id;
        });
        return await _factory.WithDb(db => ChatHistory.LoadAsync(db, id, int.MaxValue, CancellationToken.None, budget, prompt));
    }

    [Fact]
    public async Task History_KeepsTheNewestMessagesThatFitTheBudget_OldestFirst()
    {
        // 40-char messages cost 14 tokens each; a budget of 60 with an empty prompt (cost 4) leaves 56, so four fit.
        var history = await Load(count: 20, length: 40, budget: 60);

        Assert.Equal(4, history.Count);
        Assert.Equal(new[] { "017", "018", "019", "020" }, history.Select(h => h.Content[..3]));
    }

    [Fact]
    public async Task History_CountsTheNewQuestionAgainstTheBudget()
    {
        var withoutPrompt = await Load(10, 40, 60);
        var withBigPrompt = await Load(10, 40, 60, prompt: new string('p', 80)); // costs 24 instead of 4

        Assert.True(withBigPrompt.Count < withoutPrompt.Count);
    }

    [Fact]
    public async Task History_NeverSkipsOverAMessageThatDoesNotFit()
    {
        // One huge message in the middle: older small ones must not be sent past it, or the chat would have a hole.
        var (me, _) = NewPerson();
        var id = _factory.WithDb(db =>
        {
            var s = new ChatSession { Id = Guid.NewGuid(), UserId = me.Id, Title = "T" };
            db.ChatSessions.Add(s);
            db.Messages.Add(new Message { SessionId = s.Id, Role = MessageRole.User, Content = "small old" });
            db.Messages.Add(new Message { SessionId = s.Id, Role = MessageRole.Assistant, Content = new string('h', 4000) });
            db.Messages.Add(new Message { SessionId = s.Id, Role = MessageRole.User, Content = "small new" });
            db.SaveChanges();
            return s.Id;
        });

        var history = await _factory.WithDb(db => ChatHistory.LoadAsync(db, id, int.MaxValue, CancellationToken.None, tokenBudget: 200));

        Assert.Equal(new[] { "small new" }, history.Select(h => h.Content));
    }

    [Fact]
    public async Task History_SendsNothingWhenEvenTheNewestMessageIsTooBig()
    {
        var history = await Load(count: 3, length: 4000, budget: 100);
        Assert.Empty(history);
    }

    [Fact]
    public async Task History_IsStillCappedAtTheHardLimit_EvenWithAHugeBudget()
    {
        var history = await Load(count: ChatHistory.Limit + 10, length: 5, budget: 1_000_000);

        Assert.Equal(ChatHistory.Limit, history.Count);
        Assert.EndsWith($"{ChatHistory.Limit + 10}", history[^1].Content.TrimEnd('x').TrimStart('0'));
    }

    [Fact]
    public async Task History_TheConfiguredBudgetReachesTheAi()
    {
        var factory = new ApiFactory();
        factory.Settings["Chat:HistoryTokenBudget"] = "60";
        var (user, token) = factory.CreateUser("B", NewEmail(), "User");
        var client = factory.CreateClient().WithToken(token);
        var id = factory.WithDb(db =>
        {
            var s = new ChatSession { Id = Guid.NewGuid(), UserId = user.Id, Title = "T" };
            db.ChatSessions.Add(s);
            for (var i = 1; i <= 20; i++) db.Messages.Add(new Message { SessionId = s.Id, Role = i % 2 == 1 ? MessageRole.User : MessageRole.Assistant, Content = "x".PadRight(40, 'x') });
            db.SaveChanges();
            return s.Id;
        });
        IReadOnlyList<PythonHistoryMessage>? seen = null;
        factory.Python.Setup(p => p.StreamAsync(id, It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, IReadOnlyList<PythonHistoryMessage> h, string _, CancellationToken ct) => { seen = h; return Chunks(Words("ok"), ct: ct); });

        await (await PostStream(client, id, "q")).Content.ReadAsStringAsync();

        Assert.NotNull(seen);
        Assert.InRange(seen!.Count, 1, 5);
    }

    // ---- the client ----

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return respond(request, ct);
        }
    }

    private static PythonAiClient ClientFor(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, out StubHandler handler)
    {
        handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://python.test/") };
        http.DefaultRequestHeaders.Add("X-Internal-Key", "k");
        return new PythonAiClient(http, NullLogger<PythonAiClient>.Instance);
    }

    private static Task<HttpResponseMessage> Sse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });

    private static async Task<List<PythonStreamChunk>> Collect(PythonAiClient c, CancellationToken ct = default)
    {
        var all = new List<PythonStreamChunk>();
        await foreach (var chunk in c.StreamAsync(Guid.NewGuid(), Array.Empty<PythonHistoryMessage>(), "q", ct)) all.Add(chunk);
        return all;
    }

    [Fact]
    public async Task Client_PostsToGenerateStream_WithTheKey_AndParsesDeltasThenDone()
    {
        var client = ClientFor((_, _) => Sse("event: delta\ndata: {\"text\": \"Hi\"}\n\nevent: delta\ndata: {\"text\": \" there\"}\n\nevent: done\ndata: {\"tokens_used\": 7}\n\n"), out var handler);

        var chunks = await Collect(client);

        Assert.Equal("/generate/stream", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("k", handler.Request.Headers.GetValues("X-Internal-Key").Single());
        Assert.Equal(new[] { "Hi", " there", "" }, chunks.Select(c => c.Text));
        Assert.Equal(7, chunks[^1].TokensUsed);
    }

    [Fact]
    public async Task Client_ANonSuccessStatus_IsAnOutage_OnTheFirstRead()
    {
        var client = ClientFor((_, _) => Sse("{\"detail\":{}}", HttpStatusCode.ServiceUnavailable), out _);
        await Assert.ThrowsAsync<PythonServiceUnavailableException>(() => Collect(client));
    }

    [Fact]
    public async Task Client_AnUnreachableService_IsAnOutage()
    {
        var client = ClientFor((_, _) => throw new HttpRequestException("refused"), out _);
        await Assert.ThrowsAsync<PythonServiceUnavailableException>(() => Collect(client));
    }

    [Fact]
    public async Task Client_AnErrorEvent_IsAnOutage_AfterTheTextBeforeIt()
    {
        var client = ClientFor((_, _) => Sse("event: delta\ndata: {\"text\": \"par\"}\n\nevent: error\ndata: {\"error\": {\"code\": \"llm_timeout\", \"message\": \"x\"}}\n\n"), out _);
        var got = new List<string>();

        await Assert.ThrowsAsync<PythonServiceUnavailableException>(async () =>
        {
            await foreach (var c in client.StreamAsync(Guid.NewGuid(), Array.Empty<PythonHistoryMessage>(), "q", CancellationToken.None)) got.Add(c.Text);
        });

        Assert.Equal(new[] { "par" }, got);
    }

    [Fact]
    public async Task Client_AStreamThatEndsWithoutDone_IsAnOutage_NotAShortAnswer()
    {
        var client = ClientFor((_, _) => Sse("event: delta\ndata: {\"text\": \"par\"}\n\n"), out _);
        await Assert.ThrowsAsync<PythonServiceUnavailableException>(() => Collect(client));
    }

    [Fact]
    public async Task Client_ACancelledCaller_IsACancellation_NotAnOutage()
    {
        var client = ClientFor(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); }, out _);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(client, cts.Token));
    }

    [Fact]
    public void Client_StreamDeadline_IsTheOuterBudget_PythonsStreamDeadlineIsInside()
    {
        // Python stops a stream at 50 s (STREAM_DEADLINE_SECONDS) and reports it cleanly; .NET must wait longer than that.
        Assert.True(PythonAiClient.StreamTimeout > TimeSpan.FromSeconds(50));
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return false;
    }
}
