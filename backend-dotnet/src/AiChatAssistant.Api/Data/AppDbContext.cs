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
    }
}
