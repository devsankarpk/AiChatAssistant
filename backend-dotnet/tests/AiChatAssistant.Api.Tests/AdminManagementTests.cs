using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiChatAssistant.Api.Tests;

/// <summary>Phase 26, admin half, through the real pipeline: the user list, role changes, disabling, the first-admin bootstrap, the usage summary and the CSV export.</summary>
public class AdminManagementTests : IClassFixture<ApiFactory>
{
    private const string Password = "Passw0rd!x";
    private readonly ApiFactory _factory;

    public AdminManagementTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.WithDb(_ => 0);
    }

    // ---- helpers ----

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    private static string Email() => $"adm-{Guid.NewGuid():N}@example.com";

    private sealed record Person(int Id, string Email, string Token, string Refresh)
    {
        public HttpClient Client(ApiFactory f) => f.CreateClient().WithToken(Token);
    }

    /// <summary>A real registration (so the password works for login and a refresh token exists).</summary>
    private async Task<Person> Register(string name = "Some Person", bool admin = false, ApiFactory? factory = null)
    {
        var f = factory ?? _factory;
        var email = Email();
        var response = await f.CreateClient().PostAsJsonAsync("/api/auth/register", new { name, email, password = Password });
        var body = await Json(response);
        var id = f.WithDb(db => db.Users.Single(u => u.Email == email).Id);
        if (admin) MakeAdmin(id, f);
        var token = body.GetProperty("token").GetString()!;
        var refresh = body.GetProperty("refreshToken").GetString()!;
        if (admin)
        {
            // a fresh login so the token carries the Admin role
            var login = await Json(await f.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = Password }));
            token = login.GetProperty("token").GetString()!;
            refresh = login.GetProperty("refreshToken").GetString()!;
        }
        return new Person(id, email, token, refresh);
    }

    private void MakeAdmin(int userId, ApiFactory? f = null) => (f ?? _factory).WithDb(db =>
    {
        var role = db.Roles.Single(r => r.Name == Role.Admin);
        db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id });
        db.SaveChanges();
        return 0;
    });

    private Task<HttpResponseMessage> Login(string email, string password) =>
        _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password });

    private static string Code(JsonElement e) => e.GetProperty("error").GetProperty("code").GetString()!;

    /// <summary>Other tests in this class share one database, so the only reliable way to be "the last admin" is to demote everyone else.</summary>
    private void NoOtherAdmins(int keepUserId) => _factory.WithDb(db =>
    {
        var adminRole = db.Roles.Single(r => r.Name == Role.Admin);
        db.UserRoles.RemoveRange(db.UserRoles.Where(ur => ur.RoleId == adminRole.Id && ur.UserId != keepUserId));
        db.SaveChanges();
        return 0;
    });

    // ---- who may use it ----

    [Fact]
    public async Task EveryAdminEndpoint_NeedsTheAdminRole()
    {
        var user = await Register();
        var client = user.Client(_factory);
        var anon = _factory.CreateClient();
        foreach (var (method, url) in new[]
        {
            (HttpMethod.Get, "/api/admin/users"), (HttpMethod.Put, $"/api/admin/users/{user.Id}/role"),
            (HttpMethod.Post, $"/api/admin/users/{user.Id}/disable"), (HttpMethod.Post, $"/api/admin/users/{user.Id}/enable"),
            (HttpMethod.Get, "/api/admin/usage/summary"), (HttpMethod.Get, "/api/admin/usage/export"),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(new HttpRequestMessage(method, url))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(new HttpRequestMessage(method, url))).StatusCode);
        }
    }

    // ---- the list ----

    [Fact]
    public async Task List_FindsPeopleByNameOrEmail_IgnoringCase_AndShowsRolesAndState()
    {
        var admin = await Register("Boss", admin: true);
        var marker = Guid.NewGuid().ToString("N")[..8];
        var ann = await Register($"Zed{marker} Ann");
        var bob = await Register($"Zed{marker} Bob");
        await admin.Client(_factory).PostAsync($"/api/admin/users/{bob.Id}/disable", null);

        var body = await Json(await admin.Client(_factory).GetAsync($"/api/admin/users?search=ZED{marker.ToUpper()}"));

        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(new[] { ann.Id, bob.Id }, items.Select(i => i.GetProperty("id").GetInt32()).OrderBy(x => x));
        var bobItem = items.Single(i => i.GetProperty("id").GetInt32() == bob.Id);
        Assert.NotEqual(JsonValueKind.Null, bobItem.GetProperty("disabledAt").ValueKind);
        Assert.Equal(new[] { "User" }, bobItem.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.False(bobItem.TryGetProperty("passwordHash", out _));
        var byEmail = await Json(await admin.Client(_factory).GetAsync($"/api/admin/users?search={ann.Email[..14].ToUpper()}"));
        Assert.Contains(byEmail.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetInt32() == ann.Id);
    }

    [Fact]
    public async Task List_IsPaged_AndPageSizeIsCapped()
    {
        var admin = await Register("Boss", admin: true);
        for (var i = 0; i < 3; i++) await Register($"Pager{i}");

        var small = await Json(await admin.Client(_factory).GetAsync("/api/admin/users?pageSize=2&page=2"));
        var huge = await Json(await admin.Client(_factory).GetAsync("/api/admin/users?pageSize=100000"));

        Assert.Equal(2, small.GetProperty("items").GetArrayLength());
        Assert.Equal(2, small.GetProperty("page").GetInt32());
        Assert.Equal(100, huge.GetProperty("pageSize").GetInt32());
    }

    // ---- roles ----

    [Fact]
    public async Task MakingSomeoneAdmin_EndsTheirOldSessions_AndTheNewRoleAppliesAtNextLogin()
    {
        var admin = await Register("Boss", admin: true);
        var person = await Register("Promoted");
        Assert.Equal(HttpStatusCode.Forbidden, (await person.Client(_factory).GetAsync("/api/admin/ping")).StatusCode);
        await Task.Delay(1100); // tokens are compared to the change in whole seconds, so the session must be older than a second

        var response = await admin.Client(_factory).PutAsJsonAsync($"/api/admin/users/{person.Id}/role", new { role = "Admin" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "Admin" }, (await Json(response)).GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await person.Client(_factory).GetAsync("/api/admin/ping")).StatusCode);
        var refresh = await _factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = person.Refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        var login = await Json(await Login(person.Email, Password));
        var again = _factory.CreateClient().WithToken(login.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/admin/ping")).StatusCode);
    }

    [Fact]
    public async Task DemotingAnAdmin_RemovesTheRole_WhenAnotherActiveAdminExists()
    {
        var first = await Register("Boss", admin: true);
        var second = await Register("Second", admin: true);

        var response = await first.Client(_factory).PutAsJsonAsync($"/api/admin/users/{second.Id}/role", new { role = "User" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "User" }, (await Json(response)).GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(1, _factory.WithDb(db => db.UserRoles.Count(ur => ur.UserId == second.Id)));
    }

    [Fact]
    public async Task ChangingYourOwnRole_IsRefused()
    {
        var admin = await Register("Boss", admin: true);

        var response = await admin.Client(_factory).PutAsJsonAsync($"/api/admin/users/{admin.Id}/role", new { role = "User" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("cannot_change_own_role", Code(await Json(response)));
    }

    // The acting administrator is always an active administrator, so "the last active admin" can only come up when the caller's token still
    // says Admin after the role was taken away behind its back (for example by hand in the database). The rule is the safety net for that.

    private async Task<(Person Stale, Person Target)> StaleAdminAndOnlyRealAdmin()
    {
        var stale = await Register("Stale", admin: true);
        var target = await Register("Target", admin: true);
        NoOtherAdmins(target.Id);
        return (stale, target); // stale's token still carries Admin, but the database no longer says so
    }

    [Fact]
    public async Task TheLastRealAdmin_CannotBeDisabledOrDemoted_ByATokenWhoseRoleWasRemovedBehindIt()
    {
        var (stale, target) = await StaleAdminAndOnlyRealAdmin();

        var disable = await stale.Client(_factory).PostAsync($"/api/admin/users/{target.Id}/disable", null);
        var demote = await stale.Client(_factory).PutAsJsonAsync($"/api/admin/users/{target.Id}/role", new { role = "User" });

        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);
        Assert.Equal("last_admin", Code(await Json(disable)));
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal("last_admin", Code(await Json(demote)));
        Assert.True(_factory.WithDb(db => db.Users.Single(u => u.Id == target.Id).DisabledAt is null && db.UserRoles.Any(ur => ur.UserId == target.Id)));
    }

    [Fact]
    public async Task ADisabledAdmin_GivesNoCover_ForTheLastAdminRule()
    {
        var (stale, target) = await StaleAdminAndOnlyRealAdmin();
        var disabledColleague = await Register("Disabled", admin: true);
        NoOtherAdmins(target.Id);
        MakeAdmin(disabledColleague.Id);
        _factory.WithDb(db => { db.Users.Single(u => u.Id == disabledColleague.Id).DisabledAt = DateTime.UtcNow; db.SaveChanges(); return 0; });

        var response = await stale.Client(_factory).PostAsync($"/api/admin/users/{target.Id}/disable", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AnAdmin_CanDisableAnotherAdmin_WhileAnActiveAdminRemains()
    {
        var actor = await Register("Actor", admin: true);
        var target = await Register("Target", admin: true);

        var response = await actor.Client(_factory).PostAsync($"/api/admin/users/{target.Id}/disable", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ChangeRole_RejectsAnUnknownRole_AndAnUnknownUser()
    {
        var admin = await Register("Boss", admin: true);
        var person = await Register();

        var bad = await admin.Client(_factory).PutAsJsonAsync($"/api/admin/users/{person.Id}/role", new { role = "Root" });
        var none = await admin.Client(_factory).PutAsJsonAsync("/api/admin/users/999999/role", new { role = "Admin" });

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Equal("user_not_found", Code(await Json(none)));
    }

    [Fact]
    public async Task ChangeRole_ToTheRoleTheyAlreadyHave_ChangesNothing_AndKeepsTheirSessions()
    {
        var admin = await Register("Boss", admin: true);
        var person = await Register();

        var response = await admin.Client(_factory).PutAsJsonAsync($"/api/admin/users/{person.Id}/role", new { role = "User" });
        await Task.Delay(1100);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await person.Client(_factory).GetAsync("/api/auth/me")).StatusCode);
    }

    // ---- disabling ----

    [Fact]
    public async Task ADisabledAccount_LosesItsTokenAtOnce_CannotRefresh_AndCannotLogIn()
    {
        var admin = await Register("Boss", admin: true);
        var person = await Register();
        Assert.Equal(HttpStatusCode.OK, (await person.Client(_factory).GetAsync("/api/auth/me")).StatusCode);

        var response = await admin.Client(_factory).PostAsync($"/api/admin/users/{person.Id}/disable", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await Json(response)).GetProperty("disabledAt").ValueKind);
        Assert.Equal(HttpStatusCode.Unauthorized, (await person.Client(_factory).GetAsync("/api/auth/me")).StatusCode); // no waiting: immediate
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = person.Refresh })).StatusCode);
        var login = await Login(person.Email, Password);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("account_disabled", Code(await Json(login)));
    }

    [Fact]
    public async Task TheDisabledAnswer_IsOnlyGivenToSomeoneWhoKnowsThePassword()
    {
        var admin = await Register("Boss", admin: true);
        var person = await Register();
        await admin.Client(_factory).PostAsync($"/api/admin/users/{person.Id}/disable", null);

        var wrong = await Login(person.Email, "Wrong-pass1!");
        var unknown = await Login(Email(), "Wrong-pass1!");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync()); // identical: no hint that the account exists or is disabled
    }

    [Fact]
    public async Task EnablingAgain_RestoresLogin_AndKeepsEverythingTheyOwned()
    {
        var admin = await Register("Boss", admin: true);
        var person = await Register();
        var chat = _factory.WithDb(db => { var s = new ChatSession { Id = Guid.NewGuid(), UserId = person.Id, Title = "Mine" }; db.ChatSessions.Add(s); db.SaveChanges(); return s.Id; });
        await admin.Client(_factory).PostAsync($"/api/admin/users/{person.Id}/disable", null);

        var enable = await admin.Client(_factory).PostAsync($"/api/admin/users/{person.Id}/enable", null);

        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(enable)).GetProperty("disabledAt").ValueKind);
        var login = await Login(person.Email, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var client = _factory.CreateClient().WithToken((await Json(login)).GetProperty("token").GetString()!);
        Assert.Contains("Mine", await (await client.GetAsync("/api/chat/sessions")).Content.ReadAsStringAsync());
        Assert.True(_factory.WithDb(db => db.ChatSessions.Any(s => s.Id == chat && s.DeletedAt == null)));
    }

    [Fact]
    public async Task Disable_IsIdempotent_RefusesYourself_AndUnknownUsers()
    {
        var admin = await Register("Boss", admin: true);
        var person = await Register();

        var first = await admin.Client(_factory).PostAsync($"/api/admin/users/{person.Id}/disable", null);
        var stamp = (await Json(first)).GetProperty("disabledAt").GetString();
        await Task.Delay(50);
        var second = await admin.Client(_factory).PostAsync($"/api/admin/users/{person.Id}/disable", null);
        var self = await admin.Client(_factory).PostAsync($"/api/admin/users/{admin.Id}/disable", null);
        var none = await admin.Client(_factory).PostAsync("/api/admin/users/999999/disable", null);
        var noneEnable = await admin.Client(_factory).PostAsync("/api/admin/users/999999/enable", null);

        Assert.Equal(stamp, (await Json(second)).GetProperty("disabledAt").GetString()); // not re-stamped
        Assert.Equal("cannot_disable_self", Code(await Json(self)));
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noneEnable.StatusCode);
    }

    [Fact]
    public async Task Disabling_StopsTheRealtimeHubFromAcceptingTheOldToken()
    {
        var admin = await Register("Boss", admin: true);
        var person = await Register();
        await admin.Client(_factory).PostAsync($"/api/admin/users/{person.Id}/disable", null);

        var negotiate = await _factory.CreateClient().PostAsync($"/hubs/direct/negotiate?negotiateVersion=1&access_token={person.Token}", null);

        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    // ---- first-admin bootstrap ----

    private (AppDbContext Db, int UserId) LoneUserDb(string email)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        var user = new User { Name = "First", Email = email, PasswordHash = "x" };
        db.Users.Add(user);
        db.SaveChanges();
        return (db, user.Id);
    }

    [Fact]
    public async Task Bootstrap_PromotesTheConfiguredAccount_WhenNobodyIsAdmin()
    {
        var (db, id) = LoneUserDb("first@example.com");
        using var _ = db;

        var promoted = await AdminBootstrap.TryPromoteAsync(db, "First@Example.com ", null, CancellationToken.None);

        Assert.True(promoted);
        Assert.True(db.UserRoles.Any(ur => ur.UserId == id && ur.Role.Name == Role.Admin));
    }

    [Fact]
    public async Task Bootstrap_DoesNothing_OnceAnyoneIsAdmin_SoADemotedAdminIsNeverQuietlyRestored()
    {
        var (db, id) = LoneUserDb("first@example.com");
        using var _ = db;
        Assert.True(await AdminBootstrap.TryPromoteAsync(db, "first@example.com", null, CancellationToken.None));
        db.UserRoles.RemoveRange(db.UserRoles.Where(ur => ur.UserId == id));
        var other = new User { Name = "Other", Email = "o@example.com", PasswordHash = "x" };
        db.Users.Add(other);
        db.SaveChanges();
        db.UserRoles.Add(new UserRole { UserId = other.Id, RoleId = db.Roles.Single(r => r.Name == Role.Admin).Id });
        db.SaveChanges();

        Assert.False(await AdminBootstrap.TryPromoteAsync(db, "first@example.com", null, CancellationToken.None));
        Assert.False(db.UserRoles.Any(ur => ur.UserId == id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("someone.else@example.com")]
    public async Task Bootstrap_DoesNothing_ForABlankSetting_OrAnAddressWithNoAccount(string? configured)
    {
        var (db, _) = LoneUserDb("first@example.com");
        using var _2 = db;

        Assert.False(await AdminBootstrap.TryPromoteAsync(db, configured, null, CancellationToken.None));
        Assert.False(db.UserRoles.Any());
    }

    [Fact]
    public async Task Bootstrap_OnlyActsForTheAddressItIsAskedAbout()
    {
        var (db, _) = LoneUserDb("first@example.com");
        using var _2 = db;

        Assert.False(await AdminBootstrap.TryPromoteAsync(db, "first@example.com", "someone.else@example.com", CancellationToken.None));
    }

    [Fact]
    public async Task Bootstrap_RegisteringWithTheConfiguredAddress_GivesAnAdminToken_ButOnlyTheFirstTime()
    {
        using var factory = new ApiFactory { Settings = { [AdminBootstrap.SettingName] = "boot@example.com" } };
        factory.WithDb(_ => 0);

        var first = await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new { name = "Boot", email = "boot@example.com", password = Password });
        var roles = (await Json(first)).GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Contains("Admin", roles);
        var token = (await Json(first)).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().WithToken(token).GetAsync("/api/admin/ping")).StatusCode);
    }

    [Fact]
    public async Task Bootstrap_AnExistingAccount_BecomesAdminAtTheNextLogin()
    {
        using var factory = new ApiFactory { Settings = { [AdminBootstrap.SettingName] = "late@example.com" } };
        factory.WithDb(db =>
        {
            db.Users.Add(new User { Name = "Late", Email = "late@example.com", PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password), EmailVerifiedAt = DateTime.UtcNow });
            db.SaveChanges();
            return 0;
        });

        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "late@example.com", password = Password });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("Admin", (await Json(login)).GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Bootstrap_AtStartUp_PromotesAnExistingAccount_AndALogsNothingSecret()
    {
        var (db, id) = LoneUserDb("first@example.com");
        using var _ = db;
        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [AdminBootstrap.SettingName] = "first@example.com" }).Build();
        var service = new AdminBootstrapService(services.GetRequiredService<IServiceScopeFactory>(), config, NullLogger<AdminBootstrapService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.True(db.UserRoles.Any(ur => ur.UserId == id && ur.Role.Name == Role.Admin));
    }

    // ---- usage summary and export ----

    private void SeedUsage(User user, Guid chat, DateTime at, int total, int prompt, int completion, string model, decimal cost) => _factory.WithDb(db =>
    {
        db.UsageLogs.Add(new UsageLog { UserId = user.Id, SessionId = chat, TokensUsed = total, PromptTokens = prompt, CompletionTokens = completion, Model = model, CostEstimate = cost, CreatedAt = at });
        db.SaveChanges();
        return 0;
    });

    private (User User, Guid Chat) Payer(string name, string chatTitle = "Chat")
    {
        var (user, _) = _factory.CreateUser(name, Email(), "User");
        var chat = _factory.WithDb(db => { var s = new ChatSession { Id = Guid.NewGuid(), UserId = user.Id, Title = chatTitle }; db.ChatSessions.Add(s); db.SaveChanges(); return s.Id; });
        return (user, chat);
    }

    /// <summary>A date range no other test in this shared database writes into.</summary>
    private static (DateTime From, DateTime To) Window(int year) => (new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(year, 12, 31, 23, 59, 59, DateTimeKind.Utc));
    private static string Q(DateTime from, DateTime to) => $"from={from:yyyy-MM-ddTHH:mm:ssZ}&to={to:yyyy-MM-ddTHH:mm:ssZ}";

    [Fact]
    public async Task Summary_AddsUpTheWholeRange_NotOnePageOfRows()
    {
        var admin = await Register("Boss", admin: true);
        var (ann, annChat) = Payer("Ann");
        var (bob, bobChat) = Payer("Bob");
        var (from, to) = Window(2031);
        for (var i = 0; i < 25; i++) SeedUsage(ann, annChat, from.AddDays(1).AddMinutes(i), 100, 60, 40, "m", 0.001m);
        SeedUsage(bob, bobChat, from.AddDays(2), 1000, 600, 400, "m", 0.5m);

        var body = await Json(await admin.Client(_factory).GetAsync($"/api/admin/usage/summary?{Q(from, to)}"));

        var totals = body.GetProperty("totals");
        Assert.Equal(26, totals.GetProperty("requests").GetInt32());
        Assert.Equal(3500, totals.GetProperty("tokens").GetInt64());
        Assert.Equal(2100, totals.GetProperty("promptTokens").GetInt64());
        Assert.Equal(1400, totals.GetProperty("completionTokens").GetInt64());
        Assert.Equal(0.525m, totals.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task Summary_ListsTheBiggestSpendersFirst_WithTheirNames()
    {
        var admin = await Register("Boss", admin: true);
        var (cheap, cheapChat) = Payer("Cheap");
        var (pricey, priceyChat) = Payer("Pricey");
        var (from, to) = Window(2032);
        SeedUsage(cheap, cheapChat, from.AddDays(1), 10, 5, 5, "m", 0.01m);
        SeedUsage(pricey, priceyChat, from.AddDays(1), 10, 5, 5, "m", 2m);
        SeedUsage(pricey, priceyChat, from.AddDays(2), 10, 5, 5, "m", 1m);

        var byUser = (await Json(await admin.Client(_factory).GetAsync($"/api/admin/usage/summary?{Q(from, to)}"))).GetProperty("byUser").EnumerateArray().ToList();

        Assert.Equal(new[] { "Pricey", "Cheap" }, byUser.Select(u => u.GetProperty("userName").GetString()));
        Assert.Equal(2, byUser[0].GetProperty("requests").GetInt32());
        Assert.Equal(3m, byUser[0].GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task Summary_GroupsByUtcDay_OldestFirst()
    {
        var admin = await Register("Boss", admin: true);
        var (u, chat) = Payer("Daily");
        var (from, to) = Window(2033);
        SeedUsage(u, chat, from.AddDays(10).AddHours(23).AddMinutes(59), 10, 0, 0, "m", 1m);
        SeedUsage(u, chat, from.AddDays(11).AddMinutes(1), 20, 0, 0, "m", 2m);
        SeedUsage(u, chat, from.AddDays(11).AddHours(5), 30, 0, 0, "m", 4m);

        var days = (await Json(await admin.Client(_factory).GetAsync($"/api/admin/usage/summary?{Q(from, to)}"))).GetProperty("byDay").EnumerateArray().ToList();

        Assert.Equal(new[] { "2033-01-11", "2033-01-12" }, days.Select(d => d.GetProperty("date").GetString()));
        Assert.Equal(new[] { 1, 2 }, days.Select(d => d.GetProperty("requests").GetInt32()));
        Assert.Equal(6m, days[1].GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task Summary_OfAnEmptyRange_IsAllZeros_AndABackwardsRangeIs400()
    {
        var admin = await Register("Boss", admin: true);
        var (from, to) = Window(2034);

        var empty = await Json(await admin.Client(_factory).GetAsync($"/api/admin/usage/summary?{Q(from, to)}"));
        var backwards = await admin.Client(_factory).GetAsync($"/api/admin/usage/summary?{Q(to, from)}");

        Assert.Equal(0, empty.GetProperty("totals").GetProperty("requests").GetInt32());
        Assert.Equal(0m, empty.GetProperty("totals").GetProperty("cost").GetDecimal());
        Assert.Equal(0, empty.GetProperty("byUser").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
    }

    [Fact]
    public async Task Summary_StillCountsUsageOfAChatThatWasDeleted()
    {
        var admin = await Register("Boss", admin: true);
        var (u, chat) = Payer("Gone");
        var (from, to) = Window(2035);
        SeedUsage(u, chat, from.AddDays(3), 50, 0, 0, "m", 0.25m);
        _factory.WithDb(db => { db.ChatSessions.Single(s => s.Id == chat).DeletedAt = DateTime.UtcNow; db.SaveChanges(); return 0; });

        var totals = (await Json(await admin.Client(_factory).GetAsync($"/api/admin/usage/summary?{Q(from, to)}"))).GetProperty("totals");

        Assert.Equal(0.25m, totals.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task UsageList_NowCarriesTheSplit_TheModel_AndTheCost()
    {
        var admin = await Register("Boss", admin: true);
        var (u, chat) = Payer("Lister");
        var (from, to) = Window(2036);
        SeedUsage(u, chat, from.AddDays(1), 150, 100, 50, "vendor/model-x", 0.0123m);

        var item = (await Json(await admin.Client(_factory).GetAsync($"/api/admin/usage?{Q(from, to)}"))).GetProperty("items")[0];

        Assert.Equal((100, 50, "vendor/model-x", 0.0123m), (item.GetProperty("promptTokens").GetInt32(), item.GetProperty("completionTokens").GetInt32(), item.GetProperty("model").GetString(), item.GetProperty("costEstimate").GetDecimal()));
    }

    [Fact]
    public async Task Export_IsACsvFile_WithAHeader_AndOneLinePerRow_NewestFirst()
    {
        var admin = await Register("Boss", admin: true);
        var (u, chat) = Payer("Exporter", "My chat");
        var (from, to) = Window(2037);
        SeedUsage(u, chat, from.AddDays(1), 150, 100, 50, "vendor/model-x", 0.0123m);
        SeedUsage(u, chat, from.AddDays(2), 30, 20, 10, "vendor/model-x", 0.5m);

        var response = await admin.Client(_factory).GetAsync($"/api/admin/usage/export?{Q(from, to)}");
        var lines = (await response.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Matches(@"^attachment; filename=usage-\d{8}-\d{6}\.csv", response.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("Time (UTC),User,Email,Chat,Model,Prompt tokens,Completion tokens,Total tokens,Cost (USD)", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("\"2037-01-03 00:00:00\",\"Exporter\"", lines[1]); // newest first
        Assert.EndsWith(",\"My chat\",\"vendor/model-x\",20,10,30,0.5", lines[1]);
    }

    [Fact]
    public async Task Export_DefusesCellsASpreadsheetWouldRunAsAFormula_AndEscapesQuotes()
    {
        var admin = await Register("Boss", admin: true);
        var (u, chat) = Payer("=HYPERLINK(\"http://evil\")", "+cmd|' /C calc'!A0, \"quoted\"");
        var (from, to) = Window(2038);
        SeedUsage(u, chat, from.AddDays(1), 1, 0, 0, "@SUM(1)", 0m);

        var csv = await (await admin.Client(_factory).GetAsync($"/api/admin/usage/export?{Q(from, to)}")).Content.ReadAsStringAsync();
        var row = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];

        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", row);
        Assert.Contains("\"'+cmd|' /C calc'!A0, \"\"quoted\"\"\"", row);
        Assert.Contains("\"'@SUM(1)\"", row);
        Assert.DoesNotContain(",\"=", row);
        Assert.DoesNotContain(",\"+", row);
    }

    [Fact]
    public async Task Export_OfAnEmptyRange_IsJustTheHeader_AndABackwardsRangeIs400()
    {
        var admin = await Register("Boss", admin: true);
        var (from, to) = Window(2039);

        var lines = (await (await admin.Client(_factory).GetAsync($"/api/admin/usage/export?{Q(from, to)}")).Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var backwards = await admin.Client(_factory).GetAsync($"/api/admin/usage/export?{Q(to, from)}");

        Assert.Single(lines);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
    }

    // ---- privacy is untouched ----

    [Fact]
    public async Task AnAdmin_StillCannotReadSomeoneElsesDirectConversation()
    {
        var admin = await Register("Boss", admin: true);
        var a = await Register("Alice");
        var b = await Register("Bob");
        var conv = await Json(await a.Client(_factory).PostAsJsonAsync("/api/direct/conversations", new { userId = b.Id }));
        var id = conv.GetProperty("id").GetString();

        var response = await admin.Client(_factory).GetAsync($"/api/direct/conversations/{id}/messages");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
