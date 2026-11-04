using System.Net;
using System.Net.Http.Json;
using System.Text;
using AiChatAssistant.Api.Messaging;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AiChatAssistant.Api.Tests;

/// <summary>Phase 26, cost half: the maths, the usage rows written by every reply path, and the client parsing the new fields.</summary>
public class CostTrackingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CostTrackingTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.WithDb(_ => 0);
        _factory.Python.Reset();
    }

    private static CostCalculator Calc(decimal input = 0.10m, decimal output = 0.40m, Dictionary<string, ModelPrice>? models = null) =>
        new(Options.Create(new PricingOptions { InputPerMillion = input, OutputPerMillion = output, Models = models ?? new() }));

    // ---- the maths ----

    [Fact]
    public void Cost_IsPromptTimesInputRate_PlusCompletionTimesOutputRate_PerMillion()
    {
        // 1,000,000 prompt tokens at $0.10 + 500,000 completion at $0.40 = 0.10 + 0.20
        Assert.Equal(0.30m, Calc().Estimate("m", 1_000_000, 500_000, 1_500_000));
    }

    [Fact]
    public void Cost_OfASmallRequest_IsNotRoundedAwayToZero()
    {
        // 700 + 150 tokens: 0.00007 + 0.00006 = 0.00013. The old decimal(10,4) column would have stored 0.0001; the new one keeps it.
        Assert.Equal(0.00013m, Calc().Estimate("m", 700, 150, 850));
    }

    [Fact]
    public void Cost_UsesTheModelsOwnPrice_IgnoringCase_AndTheDefaultForOthers()
    {
        var calc = Calc(models: new() { ["Big/Model"] = new ModelPrice { InputPerMillion = 1m, OutputPerMillion = 2m } });

        Assert.Equal(3m, calc.Estimate("big/model", 1_000_000, 1_000_000, 2_000_000));
        Assert.Equal(0.5m, calc.Estimate("other", 1_000_000, 1_000_000, 2_000_000));
    }

    [Fact]
    public void Cost_WithOnlyATotal_IsBilledAtTheHigherRate()
    {
        Assert.Equal(0.40m, Calc().Estimate("m", 0, 0, 1_000_000)); // output rate is the higher one
        Assert.Equal(0.90m, Calc(input: 0.90m, output: 0.10m).Estimate("m", 0, 0, 1_000_000));
    }

    [Fact]
    public void Cost_IsZero_WhenNothingIsPriced_OrNothingWasUsed_AndNeverNegative()
    {
        Assert.Equal(0m, CostCalculator.Unpriced.Estimate("m", 500, 500, 1000));
        Assert.Equal(0m, Calc().Estimate("m", 0, 0, 0));
        Assert.Equal(0m, Calc().Estimate("m", -5, -5, -10));
    }

    [Fact]
    public void Cost_IsRoundedToEightDecimals()
    {
        Assert.Equal(0.00000012m, Calc(input: 0.123456789m, output: 0).Estimate("m", 1, 0, 1)); // 0.000000123456789 rounds to 8 places
    }

    [Fact]
    public void CreateLog_KeepsTheSplitAndModel_AndCutsALongModelNameToTheColumn()
    {
        var log = Calc().CreateLog(7, Guid.NewGuid(), 150, 100, 50, "  " + new string('x', 150) + "  ");

        Assert.Equal((7, 150, 100, 50), (log.UserId, log.TokensUsed, log.PromptTokens, log.CompletionTokens));
        Assert.Equal(100, log.Model.Length);
        Assert.True(log.CostEstimate > 0);
    }

    // ---- rows written by each reply path ----

    private (User User, HttpClient Client, Guid Chat) Person()
    {
        var (user, token) = _factory.CreateUser("Payer", $"cost-{Guid.NewGuid():N}@example.com", "User");
        var chat = _factory.WithDb(db =>
        {
            var s = new ChatSession { Id = Guid.NewGuid(), UserId = user.Id, Title = "T" };
            db.ChatSessions.Add(s);
            db.SaveChanges();
            return s.Id;
        });
        return (user, _factory.CreateClient().WithToken(token), chat);
    }

    private static ApiFactory Priced() =>
        new() { Settings = { ["Pricing:InputPerMillion"] = "1", ["Pricing:OutputPerMillion"] = "2" } };

    [Fact]
    public async Task OneShotReply_WritesTheSplit_TheModel_AndTheCost()
    {
        using var factory = Priced();
        var (user, token) = factory.CreateUser("P", $"c-{Guid.NewGuid():N}@example.com", "User");
        var client = factory.CreateClient().WithToken(token);
        var chat = factory.WithDb(db => { var s = new ChatSession { Id = Guid.NewGuid(), UserId = user.Id, Title = "T" }; db.ChatSessions.Add(s); db.SaveChanges(); return s.Id; });
        factory.Python.Setup(p => p.GenerateAsync(chat, It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonGenerateResult("hi", 3_000, 1_000, 2_000, "vendor/model-x"));

        var response = await client.PostAsJsonAsync($"/api/chat/sessions/{chat}/messages", new { content = "q" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var log = factory.WithDb(db => db.UsageLogs.Single(u => u.SessionId == chat));
        Assert.Equal((3_000, 1_000, 2_000, "vendor/model-x"), (log.TokensUsed, log.PromptTokens, log.CompletionTokens, log.Model));
        Assert.Equal(0.005m, log.CostEstimate); // 1000 * $1/M + 2000 * $2/M
    }

    [Fact]
    public async Task OneShotReply_FromAnOlderAiService_WithNoSplit_IsStillPriced_AndStillWorks()
    {
        using var factory = Priced();
        var (user, token) = factory.CreateUser("P", $"c-{Guid.NewGuid():N}@example.com", "User");
        var client = factory.CreateClient().WithToken(token);
        var chat = factory.WithDb(db => { var s = new ChatSession { Id = Guid.NewGuid(), UserId = user.Id, Title = "T" }; db.ChatSessions.Add(s); db.SaveChanges(); return s.Id; });
        factory.Python.Setup(p => p.GenerateAsync(chat, It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonGenerateResult("hi", 1_000_000));

        await client.PostAsJsonAsync($"/api/chat/sessions/{chat}/messages", new { content = "q" });

        var log = factory.WithDb(db => db.UsageLogs.Single(u => u.SessionId == chat));
        Assert.Equal((0, 0, ""), (log.PromptTokens, log.CompletionTokens, log.Model));
        Assert.Equal(2m, log.CostEstimate); // all 1M tokens at the higher ($2) rate
    }

    private static async IAsyncEnumerable<PythonStreamChunk> Stream(params PythonStreamChunk[] chunks)
    {
        foreach (var c in chunks)
        {
            yield return c;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task StreamedReply_WritesTheSplitFromTheDoneChunk()
    {
        using var factory = Priced();
        var (user, token) = factory.CreateUser("P", $"c-{Guid.NewGuid():N}@example.com", "User");
        var client = factory.CreateClient().WithToken(token);
        var chat = factory.WithDb(db => { var s = new ChatSession { Id = Guid.NewGuid(), UserId = user.Id, Title = "T" }; db.ChatSessions.Add(s); db.SaveChanges(); return s.Id; });
        factory.Python.Setup(p => p.StreamAsync(chat, It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Stream(new PythonStreamChunk("a"), new PythonStreamChunk("", 3_000, 1_000, 2_000, "vendor/model-x")));

        await (await client.PostAsJsonAsync($"/api/chat/sessions/{chat}/messages/stream", new { content = "q" })).Content.ReadAsStringAsync();

        var log = factory.WithDb(db => db.UsageLogs.Single(u => u.SessionId == chat));
        Assert.Equal((3_000, 1_000, 2_000, "vendor/model-x", 0.005m), (log.TokensUsed, log.PromptTokens, log.CompletionTokens, log.Model, log.CostEstimate));
    }

    [Fact]
    public async Task AStoppedStream_IsPricedFromTheLengthOfTheWordsWritten()
    {
        using var factory = Priced();
        var (user, token) = factory.CreateUser("P", $"c-{Guid.NewGuid():N}@example.com", "User");
        var client = factory.CreateClient().WithToken(token);
        var chat = factory.WithDb(db => { var s = new ChatSession { Id = Guid.NewGuid(), UserId = user.Id, Title = "T" }; db.ChatSessions.Add(s); db.SaveChanges(); return s.Id; });
        var words = new string('w', 4_000); // about 1000 tokens
        factory.Python.Setup(p => p.StreamAsync(chat, It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, IReadOnlyList<PythonHistoryMessage> _, string _, CancellationToken ct) => StopAfterFirstWord(words, ct));
        using var cts = new CancellationTokenSource();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/chat/sessions/{chat}/messages/stream") { Content = JsonContent.Create(new { content = "q" }) };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        string? line;
        do { line = await reader.ReadLineAsync(cts.Token); } while (line is not null && !line.StartsWith("data: {\"text\""));
        cts.Cancel();
        response.Dispose();

        UsageLog? log = null;
        for (var i = 0; i < 50 && log is null; i++)
        {
            log = factory.WithDb(db => db.UsageLogs.SingleOrDefault(u => u.SessionId == chat));
            if (log is null) await Task.Delay(100);
        }
        Assert.NotNull(log);
        Assert.Equal(1000, log!.CompletionTokens);
        Assert.Equal(0.002m, log.CostEstimate); // 1000 * $2/M
        Assert.Equal(string.Empty, log.Model);

        static async IAsyncEnumerable<PythonStreamChunk> StopAfterFirstWord(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new PythonStreamChunk(text);
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    // ---- the async reply worker ----

    [Fact]
    public async Task AsyncWorker_WritesTheSplitAndCost_LikeTheOtherPaths()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AiChatAssistant.Api.Data.AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AiChatAssistant.Api.Data.AppDbContext(options);
        var user = new User { Name = "W", Email = "w@example.com", PasswordHash = "x" };
        db.Users.Add(user);
        var session = new ChatSession { Id = Guid.NewGuid(), User = user, Title = "T" };
        var question = new Message { SessionId = session.Id, Session = session, Role = MessageRole.User, Content = "q" };
        db.AddRange(session, question);
        await db.SaveChangesAsync();
        var job = new AiReplyJob { Id = Guid.NewGuid(), SessionId = session.Id, UserId = user.Id, UserMessageId = question.Id, ClaimToken = Guid.NewGuid() };
        db.AiReplyJobs.Add(job);
        await db.SaveChangesAsync();
        var python = new Mock<IPythonAiClient>();
        python.Setup(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonGenerateResult("ok", 3_000, 1_000, 2_000, "vendor/model-x"));
        var processor = new AiReplyProcessor(db, python.Object, Mock.Of<IChatNotifier>(), Options.Create(new ChatOptions()),
            TimeProvider.System, NullLogger<AiReplyProcessor>.Instance, Calc(1m, 2m));

        await processor.ProcessAsync(job.Id, CancellationToken.None);

        var log = db.UsageLogs.Single();
        Assert.Equal((1_000, 2_000, "vendor/model-x", 0.005m), (log.PromptTokens, log.CompletionTokens, log.Model, log.CostEstimate));
    }

    // ---- the client ----

    private sealed class StubHandler(string body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) });
    }

    private static PythonAiClient ClientReturning(string body, string contentType) =>
        new(new HttpClient(new StubHandler(body, contentType)) { BaseAddress = new Uri("http://python.test/") }, NullLogger<PythonAiClient>.Instance);

    [Fact]
    public async Task Client_ReadsTheSplitAndModel_FromTheJsonResponse()
    {
        var client = ClientReturning("""{"reply":"x","tokens_used":150,"prompt_tokens":100,"completion_tokens":50,"model":"vendor/m"}""", "application/json");

        var result = await client.GenerateAsync(Guid.NewGuid(), Array.Empty<PythonHistoryMessage>(), "q", CancellationToken.None);

        Assert.Equal(("x", 150, 100, 50, "vendor/m"), (result.Reply, result.TokensUsed, result.PromptTokens, result.CompletionTokens, result.Model));
    }

    [Fact]
    public async Task Client_AcceptsAnOlderResponseWithoutTheNewFields()
    {
        var client = ClientReturning("""{"reply":"x","tokens_used":150}""", "application/json");

        var result = await client.GenerateAsync(Guid.NewGuid(), Array.Empty<PythonHistoryMessage>(), "q", CancellationToken.None);

        Assert.Equal((150, 0, 0, ""), (result.TokensUsed, result.PromptTokens, result.CompletionTokens, result.Model));
    }

    [Fact]
    public async Task Client_ReadsTheSplit_FromTheStreamsDoneEvent_AndToleratesItsAbsence()
    {
        var withSplit = ClientReturning("event: done\ndata: {\"tokens_used\": 9, \"prompt_tokens\": 6, \"completion_tokens\": 3, \"model\": \"m\"}\n\n", "text/event-stream");
        var without = ClientReturning("event: done\ndata: {\"tokens_used\": 9}\n\n", "text/event-stream");

        var a = await Collect(withSplit);
        var b = await Collect(without);

        Assert.Equal((9, 6, 3, "m"), (a.TokensUsed, a.PromptTokens, a.CompletionTokens, a.Model));
        Assert.Equal((9, 0, 0, ""), (b.TokensUsed, b.PromptTokens, b.CompletionTokens, b.Model));

        static async Task<PythonStreamChunk> Collect(PythonAiClient c)
        {
            PythonStreamChunk? last = null;
            await foreach (var chunk in c.StreamAsync(Guid.NewGuid(), Array.Empty<PythonHistoryMessage>(), "q", CancellationToken.None)) last = chunk;
            return last!;
        }
    }
}
