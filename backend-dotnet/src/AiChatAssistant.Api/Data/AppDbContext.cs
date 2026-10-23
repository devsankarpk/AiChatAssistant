using System.Text.Json;
using AiChatAssistant.Api.Messaging;
using AiChatAssistant.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AiChatAssistant.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<UsageLog> UsageLogs => Set<UsageLog>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<DirectConversation> DirectConversations => Set<DirectConversation>();
    public DbSet<DirectMessage> DirectMessages => Set<DirectMessage>();
    public DbSet<AiReplyJob> AiReplyJobs => Set<AiReplyJob>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    private readonly List<(string Type, Func<object> Payload)> _pendingEvents = new();

    /// <summary>
    /// Records an event to publish after the next save. See <see cref="IOutbox"/>. Nothing is written until
    /// <see cref="SaveChangesAsync(bool, CancellationToken)"/> runs.
    /// </summary>
    public void QueueEvent(string type, Func<object> payload) => _pendingEvents.Add((type, payload));

    /// <summary>
    /// Saves the changes and, in the same transaction, one outbox row per queued event. The changes go first so
    /// database-generated ids (a new message's id) exist when the event payload is built. If anything fails the
    /// transaction rolls back and neither the change nor its events exist; the queued events are kept for a retry.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (_pendingEvents.Count == 0)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        var ownsTransaction = Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await Database.BeginTransactionAsync(cancellationToken) : null;

        var written = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        foreach (var (type, payload) in _pendingEvents)
        {
            OutboxMessages.Add(new OutboxMessage
            {
                Type = type,
                Payload = JsonSerializer.Serialize(payload(), DirectEvents.Json),
                ClaimToken = Guid.NewGuid(),
            });
        }
        await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        _pendingEvents.Clear();
        return written;
    }

    /// <summary>All dates are UTC: see <see cref="UtcDateTimeConverter"/> for why this matters.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.Property(u => u.Name).HasMaxLength(120).IsRequired();
            entity.Property(u => u.Email).HasMaxLength(256).IsRequired();
            entity.HasIndex(u => u.Email).IsUnique();
            entity.Property(u => u.PasswordHash).HasMaxLength(255).IsRequired();
            entity.Property(u => u.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("Roles");
            entity.Property(r => r.Name).HasMaxLength(50).IsRequired();
            entity.HasIndex(r => r.Name).IsUnique();
            entity.HasData(
                new Role { Id = 1, Name = Role.Admin },
                new Role { Id = 2, Name = Role.User });
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.ToTable("UserRoles");
            entity.HasKey(ur => new { ur.UserId, ur.RoleId });
            entity.HasOne(ur => ur.User)
                .WithMany(u => u.UserRoles)
                .HasForeignKey(ur => ur.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(ur => ur.Role)
                .WithMany(r => r.UserRoles)
                .HasForeignKey(ur => ur.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatSession>(entity =>
        {
            entity.ToTable("ChatSessions");
            entity.Property(s => s.Title).HasMaxLength(200).IsRequired();
            entity.Property(s => s.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            entity.HasOne(s => s.User)
                .WithMany(u => u.ChatSessions)
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(s => s.UserId);
        });

        modelBuilder.Entity<Message>(entity =>
        {
            entity.ToTable("Messages");
            entity.Property(m => m.Role)
                .HasConversion(
                    v => v.ToString().ToLowerInvariant(),
                    v => Enum.Parse<MessageRole>(v, true))
                .HasColumnType("enum('user','assistant')");
            entity.Property(m => m.Content).HasColumnType("text").IsRequired();
            entity.Property(m => m.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            entity.HasOne(m => m.Session)
                .WithMany(s => s.Messages)
                .HasForeignKey(m => m.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(m => m.SessionId);
        });

        modelBuilder.Entity<UsageLog>(entity =>
        {
            entity.ToTable("UsageLogs");
            entity.Property(l => l.CostEstimate).HasColumnType("decimal(10,4)").HasDefaultValue(0m);
            entity.Property(l => l.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            entity.HasOne(l => l.User)
                .WithMany(u => u.UsageLogs)
                .HasForeignKey(l => l.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.Session)
                .WithMany(s => s.UsageLogs)
                .HasForeignKey(l => l.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.UserId);
            entity.HasIndex(l => l.CreatedAt);
        });

        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            entity.ToTable("PasswordResetTokens");
            entity.Property(t => t.TokenHash).HasMaxLength(64).IsRequired(); // SHA-256 hex = 64 chars
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.Property(t => t.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            entity.HasOne(t => t.User)
                .WithMany(u => u.PasswordResetTokens)
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(t => t.UserId);
        });

        modelBuilder.Entity<DirectConversation>(entity =>
        {
            entity.ToTable("DirectConversations");
            entity.Property(c => c.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            // Deleting a user removes their direct conversations (and, below, the messages in them).
            entity.HasOne(c => c.UserLow)
                .WithMany()
                .HasForeignKey(c => c.UserLowId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(c => c.UserHigh)
                .WithMany()
                .HasForeignKey(c => c.UserHighId)
                .OnDelete(DeleteBehavior.Cascade);
            // One conversation per pair, stored lower id first.
            entity.HasIndex(c => new { c.UserLowId, c.UserHighId }).IsUnique();
            entity.HasIndex(c => c.UserHighId);
        });

        modelBuilder.Entity<DirectMessage>(entity =>
        {
            entity.ToTable("DirectMessages");
            entity.Property(m => m.Content).HasColumnType("text").IsRequired();
            entity.Property(m => m.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            entity.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(m => m.Sender)
                .WithMany()
                .HasForeignKey(m => m.SenderId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(m => new { m.ConversationId, m.Id });
            entity.HasIndex(m => m.SenderId);
        });

        modelBuilder.Entity<AiReplyJob>(entity =>
        {
            entity.ToTable("AiReplyJobs");
            entity.Property(j => j.Status)
                .HasConversion(
                    v => v.ToString().ToLowerInvariant(),
                    v => Enum.Parse<AiReplyJobStatus>(v, true))
                .HasColumnType("enum('pending','processing','done','failed')");
            entity.Property(j => j.Error).HasMaxLength(500);
            entity.Property(j => j.ClaimToken).IsConcurrencyToken();
            entity.Property(j => j.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            entity.Property(j => j.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            entity.HasOne(j => j.Session)
                .WithMany()
                .HasForeignKey(j => j.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(j => j.User)
                .WithMany()
                .HasForeignKey(j => j.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // Deleting a message removes the job that answers it, and clears the pointer to a deleted reply.
            entity.HasOne(j => j.UserMessage)
                .WithMany()
                .HasForeignKey(j => j.UserMessageId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(j => j.AssistantMessage)
                .WithMany()
                .HasForeignKey(j => j.AssistantMessageId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(j => new { j.SessionId, j.Status });
            entity.HasIndex(j => j.UserMessageId).IsUnique(); // one job per question
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.Property(o => o.Type).HasMaxLength(100).IsRequired();
            entity.Property(o => o.Payload).HasColumnType("text").IsRequired();
            entity.Property(o => o.ClaimToken).IsConcurrencyToken();
            entity.Property(o => o.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
            // The dispatcher's query: oldest undispatched first.
            entity.HasIndex(o => new { o.DispatchedAt, o.Id });
        });
    }
}
