using AiChatAssistant.Api.Controllers;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Direct;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Tests.TestSupport;
using AiChatAssistant.Api.Validators;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Tests;

/// <summary>
/// Direct (user-to-user) chat: REST only, Phase 10. Real validators and an in-memory AppDbContext;
/// nothing here is faked because there is nothing external to fake yet (no hub, no broker).
/// </summary>
public class DirectChatControllerTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly User _alice;
    private readonly User _bob;
    private readonly User _carol;
    private readonly User _admin;

    public DirectChatControllerTests()
    {
        _alice = new User { Name = "Alice Rao", Email = "alice@example.com", PasswordHash = "x" };
        _bob = new User { Name = "Bob Menon", Email = "bob@example.com", PasswordHash = "x" };
        _carol = new User { Name = "Carol Iyer", Email = "carol@example.com", PasswordHash = "x" };
        _admin = new User { Name = "Root Admin", Email = "admin@example.com", PasswordHash = "x" };
        _db.Users.AddRange(_alice, _bob, _carol, _admin);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private DirectChatController CreateController(User caller, params string[] roles)
    {
        var controller = new DirectChatController(
            _db,
            new StartConversationRequestValidator(),
            new SendDirectMessageRequestValidator());
        controller.SetUser(ClaimsPrincipalFactory.Create(caller.Id, caller.Email, roles));
        return controller;
    }

    private DirectConversation SeedConversation(User a, User b)
    {
        var conversation = new DirectConversation
        {
            Id = Guid.NewGuid(),
            UserLowId = Math.Min(a.Id, b.Id),
            UserHighId = Math.Max(a.Id, b.Id),
        };
        _db.DirectConversations.Add(conversation);
        _db.SaveChanges();
        return conversation;
    }

    private DirectMessage SeedMessage(DirectConversation conversation, User sender, string content, DateTime? createdAt = null)
    {
        var message = new DirectMessage
        {
            ConversationId = conversation.Id,
            SenderId = sender.Id,
            Content = content,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        _db.DirectMessages.Add(message);
        _db.SaveChanges();
        return message;
    }

    private static T Value<T>(ActionResult<T> result) => (T)((ObjectResult)result.Result!).Value!;

    // ---- user search -------------------------------------------------------------------------

    [Fact]
    public async Task SearchUsers_MatchesByName_AndReturnsOnlyIdAndName()
    {
        var result = await CreateController(_alice).SearchUsers("bob", default);

        var people = Value(result);
        var found = Assert.Single(people);
        Assert.Equal(_bob.Id, found.Id);
        Assert.Equal("Bob Menon", found.Name);
        // The DTO has no room for anything private: exactly Id and Name, never an email.
        Assert.Equal(new[] { "Id", "Name" }, typeof(UserSearchResult).GetProperties().Select(p => p.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task SearchUsers_DoesNotMatchOnEmail_SoAnEmailCannotBeProbed()
    {
        var result = await CreateController(_alice).SearchUsers("bob@example.com", default);

        Assert.Empty(Value(result));
    }

    [Fact]
    public async Task SearchUsers_NeverReturnsTheCaller()
    {
        var result = await CreateController(_alice).SearchUsers("alice", default);

        Assert.Empty(Value(result));
    }

    [Fact]
    public async Task SearchUsers_IsCaseInsensitive_AndMatchesAnywhereInTheName()
    {
        var result = await CreateController(_alice).SearchUsers("  MENON ", default);

        Assert.Equal(_bob.Id, Assert.Single(Value(result)).Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    [InlineData(" b ")]
    public async Task SearchUsers_Returns400_ForATermShorterThanTwoCharacters(string? term)
    {
        var result = await CreateController(_alice).SearchUsers(term, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SearchUsers_ReturnsAtMostTwentyResults_InNameOrder()
    {
        for (var i = 0; i < 25; i++)
        {
            _db.Users.Add(new User { Name = $"Sam {i:D2}", Email = $"sam{i}@example.com", PasswordHash = "x" });
        }
        _db.SaveChanges();

        var people = Value(await CreateController(_alice).SearchUsers("sam", default));

        Assert.Equal(20, people.Count);
        Assert.Equal("Sam 00", people[0].Name);
        Assert.Equal("Sam 19", people[19].Name);
    }

    // ---- start a conversation ----------------------------------------------------------------

    [Fact]
    public async Task StartConversation_CreatesIt_StoresThePairLowerIdFirst_AndReturns201()
    {
        // Bob (higher id) starts the chat with Alice (lower id).
        var result = await CreateController(_bob).StartConversation(new StartConversationRequest { UserId = _alice.Id }, default);

        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var response = Assert.IsType<ConversationResponse>(created.Value);
        Assert.Equal(_alice.Id, response.Peer.Id);
        Assert.Equal("Alice Rao", response.Peer.Name);

        var row = Assert.Single(_db.DirectConversations);
        Assert.Equal(Math.Min(_alice.Id, _bob.Id), row.UserLowId);
        Assert.Equal(Math.Max(_alice.Id, _bob.Id), row.UserHighId);
        Assert.True(row.UserLowId < row.UserHighId);
    }

    [Fact]
    public async Task StartConversation_IsIdempotent_WhoeverStartsIt()
    {
        var first = await CreateController(_alice).StartConversation(new StartConversationRequest { UserId = _bob.Id }, default);
        var again = await CreateController(_alice).StartConversation(new StartConversationRequest { UserId = _bob.Id }, default);
        var fromTheOtherSide = await CreateController(_bob).StartConversation(new StartConversationRequest { UserId = _alice.Id }, default);

        var firstId = Assert.IsType<ConversationResponse>(Assert.IsType<ObjectResult>(first.Result).Value).Id;
        var againResult = Assert.IsType<OkObjectResult>(again.Result);
        var otherResult = Assert.IsType<OkObjectResult>(fromTheOtherSide.Result);
        Assert.Equal(firstId, Assert.IsType<ConversationResponse>(againResult.Value).Id);
        Assert.Equal(firstId, Assert.IsType<ConversationResponse>(otherResult.Value).Id);
        Assert.Single(_db.DirectConversations);
    }

    [Fact]
    public async Task StartConversation_Returns400_ForYourself()
    {
        var result = await CreateController(_alice).StartConversation(new StartConversationRequest { UserId = _alice.Id }, default);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("cannot_message_self", System.Text.Json.JsonSerializer.Serialize(bad.Value, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        Assert.Empty(_db.DirectConversations);
    }

    [Fact]
    public async Task StartConversation_Returns404_ForAnUnknownUser()
    {
        var result = await CreateController(_alice).StartConversation(new StartConversationRequest { UserId = 99999 }, default);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Empty(_db.DirectConversations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task StartConversation_Returns400_ForAnInvalidUserId(int userId)
    {
        var result = await CreateController(_alice).StartConversation(new StartConversationRequest { UserId = userId }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ---- list conversations ------------------------------------------------------------------

    [Fact]
    public async Task ListConversations_ReturnsOnlyTheCallersConversations_NewestActivityFirst_WithUnreadCounts()
    {
        var withBob = SeedConversation(_alice, _bob);
        var withCarol = SeedConversation(_alice, _carol);
        var bobAndCarol = SeedConversation(_bob, _carol); // not Alice's
        var t = DateTime.UtcNow;
        SeedMessage(withBob, _bob, "older, from Bob", t.AddMinutes(-30));
        SeedMessage(withCarol, _carol, "newest, from Carol", t.AddMinutes(-1));
        SeedMessage(withCarol, _carol, "newest, from Carol (2)", t);
        SeedMessage(bobAndCarol, _bob, "private to Bob and Carol", t);

        var list = Value(await CreateController(_alice).ListConversations(default));

        Assert.Equal(2, list.Count);
        Assert.Equal(withCarol.Id, list[0].Id); // most recent message first
        Assert.Equal("Carol Iyer", list[0].Peer.Name);
        Assert.Equal("newest, from Carol (2)", list[0].LastMessage!.Content);
        Assert.Equal(2, list[0].UnreadCount);
        Assert.Equal(withBob.Id, list[1].Id);
        Assert.Equal(1, list[1].UnreadCount);
        Assert.DoesNotContain(list, c => c.Id == bobAndCarol.Id);
    }

    [Fact]
    public async Task ListConversations_DoesNotCountYourOwnMessagesAsUnread_AndShowsAnEmptyConversation()
    {
        var withBob = SeedConversation(_alice, _bob);
        SeedMessage(withBob, _alice, "I wrote this");
        var withCarol = SeedConversation(_alice, _carol); // no messages yet

        var list = Value(await CreateController(_alice).ListConversations(default));

        Assert.Equal(0, list.Single(c => c.Id == withBob.Id).UnreadCount);
        var empty = list.Single(c => c.Id == withCarol.Id);
        Assert.Null(empty.LastMessage);
        Assert.Equal(0, empty.UnreadCount);
    }

    [Fact]
    public async Task ListConversations_ReportsThePeerAsOffline_UntilPresenceExists()
    {
        var withBob = SeedConversation(_alice, _bob);
        _bob.LastSeenAt = new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
        _db.SaveChanges();

        var item = Assert.Single(Value(await CreateController(_alice).ListConversations(default)));

        Assert.Equal(withBob.Id, item.Id);
        Assert.False(item.PeerOnline);
        Assert.Equal(_bob.LastSeenAt, item.PeerLastSeenAt);
    }

    // ---- send a message ----------------------------------------------------------------------

    [Fact]
    public async Task SendMessage_StoresTheMessageAsSent_Trimmed_AndReturns201()
    {
        var conversation = SeedConversation(_alice, _bob);

        var result = await CreateController(_alice).SendMessage(conversation.Id, new SendDirectMessageRequest { Content = "  hello Bob  " }, default);

        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var response = Assert.IsType<DirectMessageResponse>(created.Value);
        Assert.Equal("hello Bob", response.Content);
        Assert.Equal("sent", response.Status);
        Assert.False(response.Deleted);
        Assert.Equal(_alice.Id, response.SenderId);
        var stored = Assert.Single(_db.DirectMessages);
        Assert.Equal(_alice.Id, stored.SenderId);
        Assert.Null(stored.DeliveredAt);
        Assert.Null(stored.ReadAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendMessage_Returns400_ForEmptyOrWhitespaceContent(string content)
    {
        var conversation = SeedConversation(_alice, _bob);

        var result = await CreateController(_alice).SendMessage(conversation.Id, new SendDirectMessageRequest { Content = content }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(_db.DirectMessages);
    }

    [Fact]
    public async Task SendMessage_Returns400_ForOver8000Characters_AndAcceptsExactly8000()
    {
        var conversation = SeedConversation(_alice, _bob);

        var tooLong = await CreateController(_alice).SendMessage(conversation.Id, new SendDirectMessageRequest { Content = new string('x', 8001) }, default);
        var justRight = await CreateController(_alice).SendMessage(conversation.Id, new SendDirectMessageRequest { Content = new string('x', 8000) }, default);

        Assert.IsType<BadRequestObjectResult>(tooLong.Result);
        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(justRight.Result).StatusCode);
    }

    [Fact]
    public async Task SendMessage_Returns404_ForSomeoneWhoIsNotInTheConversation()
    {
        var conversation = SeedConversation(_alice, _bob);

        var result = await CreateController(_carol).SendMessage(conversation.Id, new SendDirectMessageRequest { Content = "let me in" }, default);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Empty(_db.DirectMessages);
    }

    [Fact]
    public async Task SendMessage_Returns404_ForAnAdminWhoIsNotInTheConversation_BecauseDirectMessagesArePrivate()
    {
        var conversation = SeedConversation(_alice, _bob);

        var result = await CreateController(_admin, Role.Admin).SendMessage(conversation.Id, new SendDirectMessageRequest { Content = "admin peeking" }, default);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Empty(_db.DirectMessages);
    }

    [Fact]
    public async Task SendMessage_Returns404_ForAnUnknownConversation()
    {
        var result = await CreateController(_alice).SendMessage(Guid.NewGuid(), new SendDirectMessageRequest { Content = "hi" }, default);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // ---- read messages -----------------------------------------------------------------------

    [Fact]
    public async Task GetMessages_ReturnsTheConversationOldestFirst_ForEitherParticipant()
    {
        var conversation = SeedConversation(_alice, _bob);
        SeedMessage(conversation, _alice, "one");
        SeedMessage(conversation, _bob, "two");
        SeedMessage(conversation, _alice, "three");

        var asAlice = Value(await CreateController(_alice).GetMessages(conversation.Id, null, null, default));
        var asBob = Value(await CreateController(_bob).GetMessages(conversation.Id, null, null, default));

        Assert.Equal(new[] { "one", "two", "three" }, asAlice.Select(m => m.Content));
        Assert.Equal(new[] { "one", "two", "three" }, asBob.Select(m => m.Content));
    }

    [Fact]
    public async Task GetMessages_Returns404_ForAnOutsider_AndForAnAdmin()
    {
        var conversation = SeedConversation(_alice, _bob);
        SeedMessage(conversation, _alice, "secret");

        var outsider = await CreateController(_carol).GetMessages(conversation.Id, null, null, default);
        var admin = await CreateController(_admin, Role.Admin).GetMessages(conversation.Id, null, null, default);

        Assert.IsType<NotFoundObjectResult>(outsider.Result);
        Assert.IsType<NotFoundObjectResult>(admin.Result);
    }

    [Fact]
    public async Task GetMessages_PagesBackwardsWithTheBeforeCursor_WithoutGapsOrDuplicates()
    {
        var conversation = SeedConversation(_alice, _bob);
        for (var i = 1; i <= 25; i++)
        {
            SeedMessage(conversation, i % 2 == 0 ? _alice : _bob, $"m{i:D2}");
        }
        var alice = CreateController(_alice);

        var newest = Value(await alice.GetMessages(conversation.Id, null, 10, default));
        var older = Value(await alice.GetMessages(conversation.Id, newest[0].Id, 10, default));
        var oldest = Value(await alice.GetMessages(conversation.Id, older[0].Id, 10, default));

        Assert.Equal(Enumerable.Range(16, 10).Select(i => $"m{i:D2}"), newest.Select(m => m.Content));
        Assert.Equal(Enumerable.Range(6, 10).Select(i => $"m{i:D2}"), older.Select(m => m.Content));
        Assert.Equal(Enumerable.Range(1, 5).Select(i => $"m{i:D2}"), oldest.Select(m => m.Content));
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData(0, 50)]
    [InlineData(-5, 50)]
    [InlineData(1000, 100)]
    public async Task GetMessages_UsesADefaultPageOf50_AndCapsALimitAt100(int? limit, int expectedCount)
    {
        var conversation = SeedConversation(_alice, _bob);
        for (var i = 0; i < 130; i++)
        {
            _db.DirectMessages.Add(new DirectMessage { ConversationId = conversation.Id, SenderId = _alice.Id, Content = $"m{i}" });
        }
        _db.SaveChanges();

        var page = Value(await CreateController(_alice).GetMessages(conversation.Id, null, limit, default));

        Assert.Equal(expectedCount, page.Count);
    }

    [Fact]
    public async Task GetMessages_DerivesTheStatusFromTimestamps_AndClearsDeletedContent()
    {
        var conversation = SeedConversation(_alice, _bob);
        var now = DateTime.UtcNow;
        _db.DirectMessages.AddRange(
            new DirectMessage { ConversationId = conversation.Id, SenderId = _alice.Id, Content = "sent only" },
            new DirectMessage { ConversationId = conversation.Id, SenderId = _alice.Id, Content = "delivered", DeliveredAt = now },
            new DirectMessage { ConversationId = conversation.Id, SenderId = _alice.Id, Content = "read", DeliveredAt = now, ReadAt = now },
            new DirectMessage { ConversationId = conversation.Id, SenderId = _alice.Id, Content = "should never be shown", DeletedAt = now });
        _db.SaveChanges();

        var messages = Value(await CreateController(_bob).GetMessages(conversation.Id, null, null, default));

        Assert.Equal(new[] { "sent", "delivered", "read", "sent" }, messages.Select(m => m.Status));
        Assert.Equal(string.Empty, messages[3].Content);
        Assert.True(messages[3].Deleted);
        Assert.False(messages[2].Deleted);
    }

    // ---- schema rules the controller relies on (the in-memory provider does not enforce unique
    // ---- indexes, so enforcement itself is verified against real MySQL in the Docker stack) ----

    [Fact]
    public void TheModelDeclaresAUniqueIndexOnTheUserPair_SoTheDatabaseCanRejectDuplicates()
    {
        var index = _db.Model.FindEntityType(typeof(DirectConversation))!
            .GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(DirectConversation.UserLowId), nameof(DirectConversation.UserHighId) }));

        Assert.True(index.IsUnique);
    }
}
