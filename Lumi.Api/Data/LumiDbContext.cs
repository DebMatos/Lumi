
using Lumi.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Lumi.Api.Data;

public class LumiDbContext : DbContext
{
    public LumiDbContext(DbContextOptions<LumiDbContext> options)
        : base(options)
    {
    }

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();

    public DbSet<MicroAction> MicroActions => Set<MicroAction>();

    public DbSet<MicroActionFollowUp> MicroActionFollowUps
        => Set<MicroActionFollowUp>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Conversation>()
            .HasMany(c => c.Messages)
            .WithOne(m => m.Conversation)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Message>()
            .HasIndex(m => new { m.ConversationId, m.CreatedAt });
      
       modelBuilder.Entity<MicroAction>()
            .HasMany(m => m.FollowUps)
            .WithOne(f => f.MicroAction)
            .HasForeignKey(f => f.MicroActionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MicroActionFollowUp>()
            .HasIndex(f => new { f.Status, f.DueAt });
    }
}
