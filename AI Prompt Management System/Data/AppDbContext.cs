using Microsoft.EntityFrameworkCore;
using AI_Prompt_Management_System.Models;

namespace AI_Prompt_Management_System.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Prompt> Prompts => Set<Prompt>();
    public DbSet<PromptVersion> PromptVersions => Set<PromptVersion>();
    public DbSet<PromptRating> PromptRatings => Set<PromptRating>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Prompt>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Title).IsRequired().HasMaxLength(200);
            e.Property(p => p.CurrentText).IsRequired();
            e.Property(p => p.Category).IsRequired().HasMaxLength(100);
            e.Property(p => p.AiTool).IsRequired().HasMaxLength(50);
            e.Property(p => p.Author).IsRequired().HasMaxLength(100);
        });

        modelBuilder.Entity<PromptVersion>(e =>
        {
            e.HasKey(v => v.Id);
            e.HasOne(v => v.Prompt)
             .WithMany(p => p.Versions)
             .HasForeignKey(v => v.PromptId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PromptRating>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasOne(r => r.Prompt)
             .WithMany(p => p.Ratings)
             .HasForeignKey(r => r.PromptId)
             .OnDelete(DeleteBehavior.Cascade);

            // Unique constraint: one rating per user per prompt
            e.HasIndex(r => new { r.PromptId, r.UserName }).IsUnique();
        });
    }
}
