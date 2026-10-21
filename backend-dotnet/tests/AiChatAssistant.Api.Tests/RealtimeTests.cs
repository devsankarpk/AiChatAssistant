using System.Security.Claims;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Direct;
using AiChatAssistant.Api.Hubs;
using AiChatAssistant.Api.Models;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AiChatAssistant.Api.Tests;

public class PresenceTrackerTests
{
    private readonly InMemoryPresenceTracker _presence = new();

    [Fact]
    public void TheFirstConnectionBringsAUserOnline_AndLaterOnesDoNot()
    {
        Assert.False(_presence.IsOnline(7));

        Assert.True(_presence.UserConnected(7, "tab-1"));
        Assert.False(_presence.UserConnected(7, "tab-2"));

        Assert.True(_presence.IsOnline(7));
    }

    [Fact]
    public void OnlyTheLastDisconnectTakesAUserOffline()
    {
        _presence.UserConnected(7, "tab-1");
        _presence.UserConnected(7, "tab-2");

        Assert.False(_presence.UserDisconnected(7, "tab-1")); // another tab remains
        Assert.True(_presence.IsOnline(7));
        Assert.True(_presence.UserDisconnected(7, "tab-2"));
        Assert.False(_presence.IsOnline(7));
    }

    [Fact]
    public void DisconnectingAnUnknownConnection_ChangesNothing()
    {
        _presence.UserConnected(7, "tab-1");

        Assert.False(_presence.UserDisconnected(7, "never-seen"));
        Assert.False(_presence.UserDisconnected(99, "tab-1"));
        Assert.True(_presence.IsOnline(7));
    }

    [Fact]
    public void UsersAreTrackedIndependently_AndAUserCanComeBackOnline()
    {
        _presence.UserConnected(1, "a");
        _presence.UserConnected(2, "b");
        _presence.UserDisconnected(1, "a");

        Assert.False(_presence.IsOnline(1));
        Assert.True(_presence.IsOnline(2));
        Assert.True(_presence.UserConnected(1, "a2")); // online again counts as "just came online"
    }
}

public class DirectDeliveryServiceTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly Mock<IDirectNotifier> _notifier = new();
    private readonly User _alice = new() { Name = "Alice", Email = "a@example.com", PasswordHash = "x" };
    private readonly User _bob = new() { Name = "Bob", Email = "b@example.com", PasswordHash = "x" };
    private readonly User _carol = new() { Name = "Carol", Email = "c@example.com", PasswordHash = "x" };

    public DirectDeliveryServiceTests()
    {
        _db.Users.AddRange(_alice, _bob, _carol);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private DirectDeliveryService Create() => new(_db, _notifier.Object);

    private DirectConversation Conversation(User a, User b)
    {
        var c = new DirectConversation { Id = Guid.NewGuid(), UserLowId = Math.Min(a.Id, b.Id), UserHighId = Math.Max(a.Id, b.Id) };
        _db.DirectConversations.Add(c);
        _db.SaveChanges();
        return c;
    }

    private DirectMessage Message(DirectConversation c, User sender, string text, DateTime? delivered = null, DateTime? deleted = null)
    {
        var m = new DirectMessage { ConversationId = c.Id, SenderId = sender.Id, Content = text, DeliveredAt = delivered, DeletedAt = deleted };
        _db.DirectMessages.Add(m);
        _db.SaveChanges();
        return m;
    }

    [Fact]
    public async Task ConnectingMarksEverythingWaitingForTheUserAsDelivered_AndTellsEachSender()
    {
        var withAlice = Conversation(_alice, _bob);
        var withCarol = Conversation(_bob, _carol);
        var a1 = Message(withAlice, _alice, "a1");
        var a2 = Message(withAlice, _alice, "a2");
        var c1 = Message(withCarol, _carol, "c1");

        await Create().MarkPendingDeliveredAsync(_bob.Id);

        Assert.All(new[] { a1, a2, c1 }, m => Assert.NotNull(_db.DirectMessages.Single(x => x.Id == m.Id).DeliveredAt));
        _notifier.Verify(n => n.MessagesStatusChangedAsync(_alice.Id, withAlice.Id, It.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(new[] { a1.Id, a2.Id })), "delivered", It.IsAny<CancellationToken>()), Times.Once);
        _notifier.Verify(n => n.MessagesStatusChangedAsync(_carol.Id, withCarol.Id, It.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(new[] { c1.Id })), "delivered", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ItLeavesAlone_TheUsersOwnMessages_AlreadyDeliveredOnes_DeletedOnes_AndOtherPeoplesConversations()
    {
        var mine = Conversation(_alice, _bob);
        var theirs = Conversation(_alice, _carol);
        var own = Message(mine, _bob, "sent by Bob");
        var already = Message(mine, _alice, "already delivered", delivered: DateTime.UtcNow.AddMinutes(-5));
        var deleted = Message(mine, _alice, "", deleted: DateTime.UtcNow);
        var notBobs = Message(theirs, _alice, "Alice to Carol");

        await Create().MarkPendingDeliveredAsync(_bob.Id);

        Assert.Null(_db.DirectMessages.Single(m => m.Id == own.Id).DeliveredAt);
        Assert.Null(_db.DirectMessages.Single(m => m.Id == deleted.Id).DeliveredAt);
        Assert.Null(_db.DirectMessages.Single(m => m.Id == notBobs.Id).DeliveredAt);
        Assert.True(_db.DirectMessages.Single(m => m.Id == already.Id).DeliveredAt < DateTime.UtcNow.AddMinutes(-4)); // untouched
        _notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ASecondConnection_FindsNothingLeftToDo_AndNotifiesNobody()
    {
        var c = Conversation(_alice, _bob);
        Message(c, _alice, "hi");
        var service = Create();

        await service.MarkPendingDeliveredAsync(_bob.Id);
        _notifier.Invocations.Clear();
        await service.MarkPendingDeliveredAsync(_bob.Id);

        _notifier.VerifyNoOtherCalls();
    }
}

public class DirectHubTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly InMemoryPresenceTracker _presence = new();
    private readonly Mock<IDirectDeliveryService> _delivery = new();
    private readonly Mock<IGroupManager> _groups = new();
    private readonly Mock<IDirectNotifier> _notifier = new();
    private readonly User _alice = new() { Name = "Alice", Email = "a@example.com", PasswordHash = "x" };
    private readonly User _bob = new() { Name = "Bob", Email = "b@example.com", PasswordHash = "x" };
    private readonly User _carol = new() { Name = "Carol", Email = "c@example.com", PasswordHash = "x" };

    public DirectHubTests()
    {
        _db.Users.AddRange(_alice, _bob, _carol);
        _db.SaveChanges();
    }

    private DirectConversation Conversation(User a, User b)
    {
        var c = new DirectConversation { Id = Guid.NewGuid(), UserLowId = Math.Min(a.Id, b.Id), UserHighId = Math.Max(a.Id, b.Id) };
        _db.DirectConversations.Add(c);
        _db.SaveChanges();
        return c;
    }

    public void Dispose() => _db.Dispose();

    private DirectHub CreateHub(string connectionId, ClaimsPrincipal? user)
    {
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns(user);
        return new DirectHub(_presence, _delivery.Object, _notifier.Object, _db) { Context = context.Object, Groups = _groups.Object };
    }

    [Fact]
    public async Task Connecting_JoinsThePersonalGroup_GoesOnline_AndDeliversWhatWasWaiting()
    {
        var hub = CreateHub("conn-1", ClaimsPrincipalFactory.Create(_alice.Id, _alice.Email));

        await hub.OnConnectedAsync();

        _groups.Verify(g => g.AddToGroupAsync("conn-1", DirectHub.GroupFor(_alice.Id), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(_presence.IsOnline(_alice.Id));
        _delivery.Verify(d => d.MarkPendingDeliveredAsync(_alice.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LastSeenIsWrittenOnlyWhenTheLastTabCloses()
    {
        var tab1 = CreateHub("tab-1", ClaimsPrincipalFactory.Create(_alice.Id));
        var tab2 = CreateHub("tab-2", ClaimsPrincipalFactory.Create(_alice.Id));
        await tab1.OnConnectedAsync();
        await tab2.OnConnectedAsync();

        await tab1.OnDisconnectedAsync(null);
        Assert.Null(_db.Users.Single(u => u.Id == _alice.Id).LastSeenAt); // another tab is still open
        Assert.True(_presence.IsOnline(_alice.Id));

        var before = DateTime.UtcNow.AddSeconds(-1);
        await tab2.OnDisconnectedAsync(null);

        var lastSeen = _db.Users.Single(u => u.Id == _alice.Id).LastSeenAt;
        Assert.NotNull(lastSeen);
        Assert.True(lastSeen >= before);
        Assert.False(_presence.IsOnline(_alice.Id));
    }

    [Fact]
    public async Task AConnectionWithoutAUserId_IsAbortedAndNeverCountsAsOnline()
    {
        var abort = false;
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns("anon");
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity()));
        context.Setup(c => c.Abort()).Callback(() => abort = true);
        var hub = new DirectHub(_presence, _delivery.Object, _notifier.Object, _db) { Context = context.Object, Groups = _groups.Object };

        await hub.OnConnectedAsync();

        Assert.True(abort);
        _groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _delivery.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DisconnectingSomeoneWhoWasNeverConnected_DoesNotTouchLastSeen()
    {
        var hub = CreateHub("ghost", ClaimsPrincipalFactory.Create(_alice.Id));

        await hub.OnDisconnectedAsync(null);

        Assert.Null(_db.Users.Single(u => u.Id == _alice.Id).LastSeenAt);
    }

    // ---- typing -----------------------------------------------------------------------------

    [Fact]
    public async Task Typing_IsForwardedToTheOtherParticipantOnly()
    {
        var conversation = Conversation(_alice, _bob);

        await CreateHub("a1", ClaimsPrincipalFactory.Create(_alice.Id)).Typing(conversation.Id);

        _notifier.Verify(n => n.PeerTypingAsync(_bob.Id, conversation.Id, _alice.Id, It.IsAny<CancellationToken>()), Times.Once);
        _notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Typing_WorksBothWays()
    {
        var conversation = Conversation(_alice, _bob);

        await CreateHub("b1", ClaimsPrincipalFactory.Create(_bob.Id)).Typing(conversation.Id);

        _notifier.Verify(n => n.PeerTypingAsync(_alice.Id, conversation.Id, _bob.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Typing_FromSomeoneOutsideTheConversation_IsIgnored_NotForwardedAndNoError()
    {
        var conversation = Conversation(_alice, _bob);

        var ex = await Record.ExceptionAsync(() => CreateHub("c1", ClaimsPrincipalFactory.Create(_carol.Id)).Typing(conversation.Id));

        Assert.Null(ex); // silent, so a probe learns nothing about which conversation ids exist
        _notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Typing_ForAnUnknownConversation_IsIgnored()
    {
        await CreateHub("a1", ClaimsPrincipalFactory.Create(_alice.Id)).Typing(Guid.NewGuid());

        _notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Typing_WithoutAUserId_IsIgnored()
    {
        var conversation = Conversation(_alice, _bob);

        await CreateHub("anon", new ClaimsPrincipal(new ClaimsIdentity())).Typing(conversation.Id);

        _notifier.VerifyNoOtherCalls();
    }

    // ---- presence broadcasts ----------------------------------------------------------------

    [Fact]
    public async Task ComingOnline_IsBroadcastToEveryoneWhoSharesAConversation_AndNoOneElse()
    {
        Conversation(_alice, _bob);
        Conversation(_alice, _carol);
        var bobAndCarol = new User { Name = "Dana", Email = "d@example.com", PasswordHash = "x" };
        _db.Users.Add(bobAndCarol);
        _db.SaveChanges();
        Conversation(_bob, bobAndCarol); // not Alice's

        await CreateHub("a1", ClaimsPrincipalFactory.Create(_alice.Id)).OnConnectedAsync();

        _notifier.Verify(n => n.PeerPresenceChangedAsync(
            It.Is<IReadOnlyCollection<int>>(ids => ids.OrderBy(i => i).SequenceEqual(new[] { _bob.Id, _carol.Id }.OrderBy(i => i))),
            _alice.Id, true, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ASecondTab_DoesNotBroadcastAgain()
    {
        Conversation(_alice, _bob);
        await CreateHub("a1", ClaimsPrincipalFactory.Create(_alice.Id)).OnConnectedAsync();
        _notifier.Invocations.Clear();

        await CreateHub("a2", ClaimsPrincipalFactory.Create(_alice.Id)).OnConnectedAsync();

        _notifier.Verify(n => n.PeerPresenceChangedAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GoingOffline_IsBroadcastOnlyWhenTheLastTabCloses_WithTheSameLastSeenAsStored()
    {
        Conversation(_alice, _bob);
        var tab1 = CreateHub("a1", ClaimsPrincipalFactory.Create(_alice.Id));
        var tab2 = CreateHub("a2", ClaimsPrincipalFactory.Create(_alice.Id));
        await tab1.OnConnectedAsync();
        await tab2.OnConnectedAsync();
        _notifier.Invocations.Clear();

        await tab1.OnDisconnectedAsync(null);
        _notifier.Verify(n => n.PeerPresenceChangedAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<int>(), false, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);

        await tab2.OnDisconnectedAsync(null);

        var stored = _db.Users.Single(u => u.Id == _alice.Id).LastSeenAt;
        Assert.NotNull(stored);
        _notifier.Verify(n => n.PeerPresenceChangedAsync(
            It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { _bob.Id })), _alice.Id, false, stored, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task APersonWithNoConversations_BroadcastsToNobody()
    {
        await CreateHub("c1", ClaimsPrincipalFactory.Create(_carol.Id)).OnConnectedAsync();

        _notifier.Verify(n => n.PeerPresenceChangedAsync(It.Is<IReadOnlyCollection<int>>(ids => ids.Count == 0), _carol.Id, true, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ThePersonalGroupNameIsStable()
    {
        Assert.Equal("user-42", DirectHub.GroupFor(42));
    }
}

public class SignalRDirectNotifierTests
{
    private readonly Mock<IClientProxy> _proxy = new();
    private readonly Mock<IHubClients> _clients = new();
    private readonly Mock<IHubContext<DirectHub>> _hub = new();

    private SignalRDirectNotifier Create()
    {
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_proxy.Object);
        _hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        return new SignalRDirectNotifier(_hub.Object, NullLogger<SignalRDirectNotifier>.Instance);
    }

    [Fact]
    public async Task MessageReceived_GoesToTheRecipientsGroup_UnderTheAgreedEventName()
    {
        var message = new DirectMessageResponse { Id = 5, Content = "hi" };

        await Create().MessageReceivedAsync(9, message);

        _clients.Verify(c => c.Group("user-9"), Times.Once);
        _proxy.Verify(p => p.SendCoreAsync("MessageReceived", It.Is<object?[]>(a => a.Length == 1 && ReferenceEquals(a[0], message)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StatusChanged_GoesToTheSendersGroup()
    {
        await Create().MessagesStatusChangedAsync(3, Guid.NewGuid(), new long[] { 1, 2 }, "read");

        _clients.Verify(c => c.Group("user-3"), Times.Once);
        _proxy.Verify(p => p.SendCoreAsync("MessageStatusChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MessagesRead_GoesToTheReadersOwnGroup()
    {
        await Create().MessagesReadAsync(4, Guid.NewGuid(), 10);

        _clients.Verify(c => c.Group("user-4"), Times.Once);
        _proxy.Verify(p => p.SendCoreAsync("MessagesRead", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MessageDeleted_GoesToTheGivenUsersGroup()
    {
        await Create().MessageDeletedAsync(8, Guid.NewGuid(), 42);

        _clients.Verify(c => c.Group("user-8"), Times.Once);
        _proxy.Verify(p => p.SendCoreAsync("MessageDeleted", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PeerTyping_GoesToTheRecipientOnly()
    {
        await Create().PeerTypingAsync(6, Guid.NewGuid(), 5);

        _clients.Verify(c => c.Group("user-6"), Times.Once);
        _clients.Verify(c => c.Group("user-5"), Times.Never);
        _proxy.Verify(p => p.SendCoreAsync("PeerTyping", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PeerPresenceChanged_GoesToEachPeersGroup_InOneCall()
    {
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Groups(It.IsAny<IReadOnlyList<string>>())).Returns(_proxy.Object);
        _hub.SetupGet(h => h.Clients).Returns(clients.Object);
        var notifier = new SignalRDirectNotifier(_hub.Object, NullLogger<SignalRDirectNotifier>.Instance);

        await notifier.PeerPresenceChangedAsync(new[] { 2, 3 }, 1, true, null);

        clients.Verify(c => c.Groups(It.Is<IReadOnlyList<string>>(g => g.SequenceEqual(new[] { "user-2", "user-3" }))), Times.Once);
        _proxy.Verify(p => p.SendCoreAsync("PeerPresenceChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PeerPresenceChanged_WithNoPeers_SendsNothing()
    {
        var clients = new Mock<IHubClients>();
        _hub.SetupGet(h => h.Clients).Returns(clients.Object);
        var notifier = new SignalRDirectNotifier(_hub.Object, NullLogger<SignalRDirectNotifier>.Instance);

        await notifier.PeerPresenceChangedAsync(Array.Empty<int>(), 1, true, null);

        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task APushThatFails_NeverThrowsIntoTheRequest()
    {
        _proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub is down"));

        var ex = await Record.ExceptionAsync(() => Create().MessageReceivedAsync(1, new DirectMessageResponse()));

        Assert.Null(ex);
    }
}
