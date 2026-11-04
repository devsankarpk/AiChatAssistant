using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using Moq;

namespace AiChatAssistant.Api.Tests;

/// <summary>Phase 24 through the real pipeline: rename, soft delete, title search, and paging of chats and messages.</summary>
public class ChatManagementTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ChatManagementTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.WithDb(_ => 0);
        _factory.Python.Reset();
    }

    private static string NewEmail() => $"cm-{Guid.NewGuid():N}@example.com";
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private (User User, HttpClient Client) NewPerson(params string[] roles)
    {
        var (user, token) = _factory.CreateUser("Chat Person", NewEmail(), roles.Length == 0 ? new[] { "User" } : roles);
        return (user, _factory.CreateClient().WithToken(token));
    }

    private Guid Seed(User owner, string title, DateTime? createdAt = null) => _factory.WithDb(db =>
    {
        var s = new ChatSession { Id = Guid.NewGuid(), UserId = owner.Id, Title = title };
        if (createdAt is not null) s.CreatedAt = createdAt.Value;
        db.ChatSessions.Add(s);
        db.SaveChanges();
        return s.Id;
    });

    private void SeedMessages(Guid sessionId, int count) => _factory.WithDb(db =>
    {
        for (var i = 1; i <= count; i++)
        {
            db.Messages.Add(new Message { SessionId = sessionId, Role = i % 2 == 1 ? MessageRole.User : MessageRole.Assistant, Content = $"m{i}" });
        }
        db.SaveChanges();
        return 0;
    });

    private static async Task<string[]> Titles(HttpResponseMessage r) =>
        (await Json(r)).EnumerateArray().Select(e => e.GetProperty("title").GetString()!).ToArray();

    private static async Task<string[]> Contents(HttpResponseMessage r) =>
        (await Json(r)).EnumerateArray().Select(e => e.GetProperty("content").GetString()!).ToArray();

    // ---- rename ----

    [Fact]
    public async Task Rename_TrimsAndSavesTheTitle()
    {
        var (me, client) = NewPerson();
        var id = Seed(me, "Old name");

        var response = await client.PatchAsJsonAsync($"/api/chat/sessions/{id}", new { title = "   Trip to Lisbon  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Trip to Lisbon", (await Json(response)).GetProperty("title").GetString());
        Assert.Equal("Trip to Lisbon", _factory.WithDb(db => db.ChatSessions.Single(s => s.Id == id).Title));
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    public async Task Rename_RejectsAnEmptyTitle_WithTheStandardErrorShape(string title)
    {
        var (me, client) = NewPerson();
        var id = Seed(me, "Keep me");

        var response = await client.PatchAsJsonAsync($"/api/chat/sessions/{id}", new { title });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("error").TryGetProperty("code", out _));
        Assert.Equal("Keep me", _factory.WithDb(db => db.ChatSessions.Single(s => s.Id == id).Title));
    }

    [Fact]
    public async Task Rename_AcceptsExactly200Characters_AndRejects201()
    {
        var (me, client) = NewPerson();
        var id = Seed(me, "x");

        var ok = await client.PatchAsJsonAsync($"/api/chat/sessions/{id}", new { title = new string('a', 200) });
        var tooLong = await client.PatchAsJsonAsync($"/api/chat/sessions/{id}", new { title = new string('a', 201) });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Rename_SomeoneElsesChat_Is404_AndTheTitleIsUntouched()
    {
        var (owner, _) = NewPerson();
        var (_, intruder) = NewPerson();
        var id = Seed(owner, "Private");

        var response = await intruder.PatchAsJsonAsync($"/api/chat/sessions/{id}", new { title = "Hacked" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("session_not_found", (await Json(response)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Private", _factory.WithDb(db => db.ChatSessions.Single(s => s.Id == id).Title));
    }

    [Fact]
    public async Task Rename_UnknownChat_Is404()
    {
        var (_, client) = NewPerson();
        var response = await client.PatchAsJsonAsync($"/api/chat/sessions/{Guid.NewGuid()}", new { title = "x" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rename_WithoutAToken_Is401()
    {
        var response = await _factory.CreateClient().PatchAsJsonAsync($"/api/chat/sessions/{Guid.NewGuid()}", new { title = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ARenamedChat_IsNotRetitledByTheFirstMessage()
    {
        var (me, client) = NewPerson();
        var id = Seed(me, "New chat");
        await client.PatchAsJsonAsync($"/api/chat/sessions/{id}", new { title = "My own name" });
        _factory.Python.Setup(p => p.GenerateAsync(id, It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonGenerateResult("hi", 3));

        await client.PostAsJsonAsync($"/api/chat/sessions/{id}/messages", new { content = "Some question" });

        Assert.Equal("My own name", _factory.WithDb(db => db.ChatSessions.Single(s => s.Id == id).Title));
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_Answers204_AndTheChatLeavesTheList()
    {
        var (me, client) = NewPerson();
        var keep = Seed(me, "Keep");
        var gone = Seed(me, "Gone");

        var response = await client.DeleteAsync($"/api/chat/sessions/{gone}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new[] { "Keep" }, await Titles(await client.GetAsync("/api/chat/sessions")));
        Assert.NotEqual(Guid.Empty, keep);
    }

    [Fact]
    public async Task ADeletedChat_AnswersTheSame404OnEveryEndpoint_AndTheAiIsNeverAsked()
    {
        var (me, client) = NewPerson();
        var id = Seed(me, "Doomed");
        SeedMessages(id, 2);
        await client.DeleteAsync($"/api/chat/sessions/{id}");

        var read = await client.GetAsync($"/api/chat/sessions/{id}/messages");
        var send = await client.PostAsJsonAsync($"/api/chat/sessions/{id}/messages", new { content = "hello?" });
        var jobs = await client.GetAsync($"/api/chat/sessions/{id}/jobs");
        var rename = await client.PatchAsJsonAsync($"/api/chat/sessions/{id}", new { title = "back" });
        var again = await client.DeleteAsync($"/api/chat/sessions/{id}");

        Assert.All(new[] { read, send, jobs, rename, again }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        _factory.Python.Verify(p => p.GenerateAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(2, _factory.WithDb(db => db.Messages.Count(m => m.SessionId == id))); // nothing new was saved
    }

    [Fact]
    public async Task Delete_KeepsTheRowTheMessagesAndTheUsageHistory()
    {
        var (me, client) = NewPerson();
        var id = Seed(me, "Billed");
        SeedMessages(id, 2);
        _factory.WithDb(db => { db.UsageLogs.Add(new UsageLog { UserId = me.Id, SessionId = id, TokensUsed = 42 }); db.SaveChanges(); return 0; });

        await client.DeleteAsync($"/api/chat/sessions/{id}");

        _factory.WithDb(db =>
        {
            var row = db.ChatSessions.Single(s => s.Id == id);
            Assert.NotNull(row.DeletedAt);
            Assert.True((DateTime.UtcNow - row.DeletedAt!.Value).TotalMinutes < 1);
            Assert.Equal(2, db.Messages.Count(m => m.SessionId == id));
            Assert.Equal(42, db.UsageLogs.Single(u => u.SessionId == id).TokensUsed);
            return 0;
        });
    }

    [Fact]
    public async Task Delete_SomeoneElsesChat_Is404_AndTheChatSurvives()
    {
        var (owner, ownerClient) = NewPerson();
        var (_, intruder) = NewPerson();
        var id = Seed(owner, "Mine");

        var response = await intruder.DeleteAsync($"/api/chat/sessions/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(new[] { "Mine" }, await Titles(await ownerClient.GetAsync("/api/chat/sessions")));
    }

    [Fact]
    public async Task Delete_WithoutAToken_Is401()
    {
        var response = await _factory.CreateClient().DeleteAsync($"/api/chat/sessions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheAdminUsageList_StillShowsTheTitleOfADeletedChat()
    {
        var (admin, adminClient) = NewPerson("Admin");
        var (me, client) = NewPerson();
        var id = Seed(me, "Deleted but billed");
        _factory.WithDb(db => { db.UsageLogs.Add(new UsageLog { UserId = me.Id, SessionId = id, TokensUsed = 7 }); db.SaveChanges(); return 0; });
        await client.DeleteAsync($"/api/chat/sessions/{id}");

        var response = await adminClient.GetAsync("/api/admin/usage?pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Deleted but billed", await response.Content.ReadAsStringAsync());
        Assert.NotEqual(0, admin.Id);
    }

    // ---- search ----

    [Fact]
    public async Task Search_MatchesAnyPartOfTheTitle_IgnoringCaseAndSurroundingSpaces()
    {
        var (me, client) = NewPerson();
        Seed(me, "Trip to Lisbon", DateTime.UtcNow.AddMinutes(-3));
        Seed(me, "Cooking pasta", DateTime.UtcNow.AddMinutes(-2));
        Seed(me, "lisbon hotels", DateTime.UtcNow.AddMinutes(-1));

        var hits = await Titles(await client.GetAsync("/api/chat/sessions?search=%20LISBON%20"));

        Assert.Equal(new[] { "lisbon hotels", "Trip to Lisbon" }, hits); // newest first
    }

    [Fact]
    public async Task Search_WithNothingOrOnlySpaces_ReturnsEverything()
    {
        var (me, client) = NewPerson();
        Seed(me, "A"); Seed(me, "B");

        Assert.Equal(2, (await Titles(await client.GetAsync("/api/chat/sessions?search="))).Length);
        Assert.Equal(2, (await Titles(await client.GetAsync("/api/chat/sessions?search=%20%20"))).Length);
    }

    [Fact]
    public async Task Search_NeverReturnsDeletedChatsOrSomeoneElsesChats()
    {
        var (me, client) = NewPerson();
        var (other, _) = NewPerson();
        Seed(me, "Budget plan");
        Seed(other, "Budget secrets");
        var deleted = Seed(me, "Budget draft");
        await client.DeleteAsync($"/api/chat/sessions/{deleted}");

        var hits = await Titles(await client.GetAsync("/api/chat/sessions?search=budget"));

        Assert.Equal(new[] { "Budget plan" }, hits);
    }

    [Theory]
    [InlineData("%25")]       // a literal percent sign must not act as a wildcard
    [InlineData("_")]         // nor an underscore
    [InlineData("%27%20OR%201%3D1%20--")]
    public async Task Search_TreatsWildcardsAndQuotesAsPlainText(string term)
    {
        var (me, client) = NewPerson();
        Seed(me, "Plain title");

        var response = await client.GetAsync($"/api/chat/sessions?search={term}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await Titles(response));
    }

    [Fact]
    public async Task Search_AVeryLongTermIsCutNotRejected()
    {
        var (me, client) = NewPerson();
        Seed(me, "Plain title");

        var response = await client.GetAsync($"/api/chat/sessions?search={new string('z', 5000)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await Titles(response));
    }

    // ---- paging: sessions ----

    private static string Iso(DateTime t) => Uri.EscapeDataString(t.ToString("O"));

    [Fact]
    public async Task Sessions_LimitGivesTheNewestFirst_AndBeforeGivesTheNextPage()
    {
        var (me, client) = NewPerson();
        var t0 = DateTime.UtcNow.AddHours(-1);
        var created = Enumerable.Range(1, 5).Select(i => t0.AddMinutes(i)).ToArray();
        for (var i = 0; i < 5; i++) Seed(me, $"c{i + 1}", created[i]);

        var page1 = await client.GetAsync("/api/chat/sessions?limit=2");
        var page1Json = await Json(page1);
        var lastCreated = page1Json[1].GetProperty("createdAt").GetDateTime();
        var page2 = await client.GetAsync($"/api/chat/sessions?limit=2&before={Iso(lastCreated)}");
        var lastCreated2 = (await Json(page2))[1].GetProperty("createdAt").GetDateTime();
        var page3 = await client.GetAsync($"/api/chat/sessions?limit=2&before={Iso(lastCreated2)}");

        Assert.Equal(new[] { "c5", "c4" }, page1Json.EnumerateArray().Select(e => e.GetProperty("title").GetString()!));
        Assert.Equal(new[] { "c3", "c2" }, await Titles(page2));
        Assert.Equal(new[] { "c1" }, await Titles(page3));
    }

    [Fact]
    public async Task Sessions_WithNoParameters_ReturnsAtMostTheNewest50()
    {
        var (me, client) = NewPerson();
        var t0 = DateTime.UtcNow.AddDays(-1);
        for (var i = 1; i <= 55; i++) Seed(me, $"c{i}", t0.AddMinutes(i));

        var titles = await Titles(await client.GetAsync("/api/chat/sessions"));

        Assert.Equal(50, titles.Length);
        Assert.Equal("c55", titles[0]);
        Assert.Equal("c6", titles[^1]);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=-5")]
    public async Task Sessions_ANonPositiveLimit_FallsBackToTheDefault(string query)
    {
        var (me, client) = NewPerson();
        var t0 = DateTime.UtcNow.AddDays(-1);
        for (var i = 1; i <= 3; i++) Seed(me, $"c{i}", t0.AddMinutes(i));

        var response = await client.GetAsync($"/api/chat/sessions?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, (await Titles(response)).Length);
    }

    [Fact]
    public async Task Sessions_ALimitAboveTheCap_IsCutTo100()
    {
        var (me, client) = NewPerson();
        var t0 = DateTime.UtcNow.AddDays(-1);
        for (var i = 1; i <= 105; i++) Seed(me, $"c{i}", t0.AddSeconds(i));

        Assert.Equal(100, (await Titles(await client.GetAsync("/api/chat/sessions?limit=100000"))).Length);
    }

    [Fact]
    public async Task Sessions_ANonNumericLimit_Is400_NotA500()
    {
        var (_, client) = NewPerson();
        var response = await client.GetAsync("/api/chat/sessions?limit=lots");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sessions_SearchAndPagingWorkTogether()
    {
        var (me, client) = NewPerson();
        var t0 = DateTime.UtcNow.AddDays(-1);
        for (var i = 1; i <= 6; i++) Seed(me, i % 2 == 0 ? $"match {i}" : $"other {i}", t0.AddMinutes(i));

        var first = await Json(await client.GetAsync("/api/chat/sessions?search=match&limit=2"));
        var cursor = first[1].GetProperty("createdAt").GetDateTime();
        var second = await Titles(await client.GetAsync($"/api/chat/sessions?search=match&limit=2&before={Iso(cursor)}"));

        Assert.Equal(new[] { "match 6", "match 4" }, first.EnumerateArray().Select(e => e.GetProperty("title").GetString()!));
        Assert.Equal(new[] { "match 2" }, second);
    }

    // ---- paging: messages ----

    [Fact]
    public async Task Messages_WithNoParameters_ReturnsTheNewestPageOldestFirst()
    {
        var (me, client) = NewPerson();
        var id = Seed(me, "Long chat");
        SeedMessages(id, 60);

        var contents = await Contents(await client.GetAsync($"/api/chat/sessions/{id}/messages"));

        Assert.Equal(50, contents.Length);
        Assert.Equal("m11", contents[0]);
        Assert.Equal("m60", contents[^1]);
    }

    [Fact]
    public async Task Messages_BeforeScrollsUpOnePageAtATime_WithNoGapsOrRepeats()
    {
        var (me, client) = NewPerson();
        var id = Seed(me, "Scroll");
        SeedMessages(id, 7);

        var newest = await Json(await client.GetAsync($"/api/chat/sessions/{id}/messages?limit=3"));
        var firstId = newest[0].GetProperty("id").GetInt32();
        var older = await Json(await client.GetAsync($"/api/chat/sessions/{id}/messages?limit=3&before={firstId}"));
        var oldestId = older[0].GetProperty("id").GetInt32();
        var oldest = await Contents(await client.GetAsync($"/api/chat/sessions/{id}/messages?limit=3&before={oldestId}"));
        var past = await Contents(await client.GetAsync($"/api/chat/sessions/{id}/messages?limit=3&before=1"));

        Assert.Equal(new[] { "m5", "m6", "m7" }, newest.EnumerateArray().Select(e => e.GetProperty("content").GetString()!));
        Assert.Equal(new[] { "m2", "m3", "m4" }, older.EnumerateArray().Select(e => e.GetProperty("content").GetString()!));
        Assert.Equal(new[] { "m1" }, oldest);
        Assert.Empty(past);
    }

    [Fact]
    public async Task Messages_ALimitAboveTheCap_IsCutTo100()
    {
        var (me, client) = NewPerson();
        var id = Seed(me, "Huge");
        SeedMessages(id, 120);

        Assert.Equal(100, (await Contents(await client.GetAsync($"/api/chat/sessions/{id}/messages?limit=5000"))).Length);
    }

    [Fact]
    public async Task Messages_Paging_StillEnforcesOwnership()
    {
        var (owner, _) = NewPerson();
        var (_, intruder) = NewPerson();
        var id = Seed(owner, "Private");
        SeedMessages(id, 3);

        var response = await intruder.GetAsync($"/api/chat/sessions/{id}/messages?limit=2&before=99");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheAiContext_StillUsesTheNewestMessagesUpToTheLimit_NotThePageSize()
    {
        var (me, client) = NewPerson();
        var id = Seed(me, "Context");
        SeedMessages(id, 60);
        IReadOnlyList<PythonHistoryMessage>? seen = null;
        _factory.Python.Setup(p => p.GenerateAsync(id, It.IsAny<IReadOnlyList<PythonHistoryMessage>>(), "next", It.IsAny<CancellationToken>()))
            .Callback<Guid, IReadOnlyList<PythonHistoryMessage>, string, CancellationToken>((_, h, _, _) => seen = h)
            .ReturnsAsync(new PythonGenerateResult("ok", 1));

        await client.PostAsJsonAsync($"/api/chat/sessions/{id}/messages", new { content = "next" });

        Assert.NotNull(seen);
        Assert.Equal(ChatHistory.Limit, seen!.Count);
        Assert.Equal("m11", seen[0].Content);
    }
}
