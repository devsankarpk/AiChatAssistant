using AiChatAssistant.Api.Controllers;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Admin;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc;

namespace AiChatAssistant.Api.Tests;

public class AdminControllerTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly User _ada = new() { Name = "Ada", Email = "ada@example.com", PasswordHash = "x" };
    private readonly ChatSession _session;

    public AdminControllerTests()
    {
        _db.Users.Add(_ada);
        _db.SaveChanges();
        _session = new ChatSession { Id = Guid.NewGuid(), UserId = _ada.Id, Title = "Quarterly plan" };
        _db.ChatSessions.Add(_session);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private AdminController Controller()
    {
        var controller = new AdminController(_db);
        controller.SetUser(ClaimsPrincipalFactory.Create(_ada.Id, "ada@example.com", "Admin"));
        return controller;
    }

    private void Log(DateTime at, int tokens = 10)
    {
        _db.UsageLogs.Add(new UsageLog { UserId = _ada.Id, SessionId = _session.Id, TokensUsed = tokens, CreatedAt = at });
        _db.SaveChanges();
    }

    private static PagedUsageLogResponse Page(ActionResult<PagedUsageLogResponse> result) =>
        Assert.IsType<PagedUsageLogResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public void Ping_ReturnsPong()
    {
        var ok = Assert.IsType<OkObjectResult>(Controller().Ping().Result);
        Assert.Contains("pong", ok.Value!.ToString());
    }

    [Fact]
    public async Task Usage_PageSizeIsCappedAt100_AndFloorsAtOne()
    {
        for (var i = 0; i < 105; i++) Log(new DateTime(2026, 1, 1).AddMinutes(i));

        var big = Page(await Controller().GetUsage(pageSize: 5000));
        var zero = Page(await Controller().GetUsage(pageSize: 0));
        var negative = Page(await Controller().GetUsage(pageSize: -3));

        Assert.Equal(100, big.PageSize);
        Assert.Equal(100, big.Items.Count);
        Assert.Equal(105, big.TotalCount);
        Assert.Equal(2, big.TotalPages);
        Assert.Equal(1, zero.PageSize);
        Assert.Equal(1, negative.PageSize);
    }

    [Fact]
    public async Task Usage_PagesAreNewestFirst_AndPageBelowOneBecomesOne()
    {
        for (var i = 0; i < 5; i++) Log(new DateTime(2026, 1, 1).AddDays(i), tokens: i);

        var first = Page(await Controller().GetUsage(page: 1, pageSize: 2));
        var second = Page(await Controller().GetUsage(page: 2, pageSize: 2));
        var third = Page(await Controller().GetUsage(page: 3, pageSize: 2));
        var clamped = Page(await Controller().GetUsage(page: -4, pageSize: 2));

        Assert.Equal(new[] { 4, 3 }, first.Items.Select(i => i.TokensUsed));
        Assert.Equal(new[] { 2, 1 }, second.Items.Select(i => i.TokensUsed));
        Assert.Equal(new[] { 0 }, third.Items.Select(i => i.TokensUsed));
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(1, clamped.Page);
    }

    [Fact]
    public async Task Usage_EmptyTable_ReturnsAnEmptyPage_NotAnError()
    {
        var page = Page(await Controller().GetUsage());

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, page.TotalPages);
    }

    [Fact]
    public async Task Usage_DateFilter_IncludesBothBounds()
    {
        Log(new DateTime(2026, 3, 1), 1);
        Log(new DateTime(2026, 3, 5), 2);
        Log(new DateTime(2026, 3, 10), 3);
        Log(new DateTime(2026, 3, 20), 4);

        var page = Page(await Controller().GetUsage(from: new DateTime(2026, 3, 5), to: new DateTime(2026, 3, 10)));

        Assert.Equal(new[] { 3, 2 }, page.Items.Select(i => i.TokensUsed));
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task Usage_OnlyFrom_And_OnlyTo_WorkAlone()
    {
        Log(new DateTime(2026, 3, 1), 1);
        Log(new DateTime(2026, 3, 20), 2);

        var fromOnly = Page(await Controller().GetUsage(from: new DateTime(2026, 3, 10)));
        var toOnly = Page(await Controller().GetUsage(to: new DateTime(2026, 3, 10)));

        Assert.Equal(new[] { 2 }, fromOnly.Items.Select(i => i.TokensUsed));
        Assert.Equal(new[] { 1 }, toOnly.Items.Select(i => i.TokensUsed));
    }

    [Fact]
    public async Task Usage_FromLaterThanTo_Is400_InTheStandardShape()
    {
        var result = await Controller().GetUsage(from: new DateTime(2026, 4, 2), to: new DateTime(2026, 4, 1));

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var error = Assert.IsType<Common.ErrorResponse>(bad.Value);
        Assert.Equal("validation_error", error.Error.Code);
    }

    [Fact]
    public async Task Usage_FromEqualToTo_IsAllowed()
    {
        var at = new DateTime(2026, 4, 1, 12, 0, 0);
        Log(at);

        var page = Page(await Controller().GetUsage(from: at, to: at));

        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Usage_RowsCarryTheJoinedUserAndSessionFields()
    {
        Log(new DateTime(2026, 5, 1), 42);

        var row = Page(await Controller().GetUsage()).Items.Single();

        Assert.Equal("Ada", row.UserName);
        Assert.Equal("ada@example.com", row.UserEmail);
        Assert.Equal("Quarterly plan", row.SessionTitle);
        Assert.Equal(_session.Id, row.SessionId);
        Assert.Equal(42, row.TokensUsed);
        Assert.Equal(0m, row.CostEstimate);
    }
}
