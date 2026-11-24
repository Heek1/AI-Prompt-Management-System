using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PromptManager.Infrastructure;
using PromptManager.Models;

namespace PromptManager.Data;

public class AppDbContext : IdentityDbContext<AppUser>, IDataProtectionKeyContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Prompt> Prompts => Set<Prompt>();
    public DbSet<PromptVersion> PromptVersions => Set<PromptVersion>();
    public DbSet<PromptRating> PromptRatings => Set<PromptRating>();
    public DbSet<PromptRun> PromptRuns => Set<PromptRun>();
    public DbSet<PromptInputSet> PromptInputSets => Set<PromptInputSet>();
    public DbSet<EvalCase> EvalCases => Set<EvalCase>();
    public DbSet<EvalRun> EvalRuns => Set<EvalRun>();
    public DbSet<EvalResult> EvalResults => Set<EvalResult>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<LookupValue> LookupValues => Set<LookupValue>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PromptTag> PromptTags => Set<PromptTag>();

    /// <summary>Data-protection keys (cookies, antiforgery tokens), see Program.cs.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Prompt>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Title).IsRequired().HasMaxLength(FieldLimits.Title);
            e.Property(p => p.Description).HasMaxLength(FieldLimits.Description);
            e.Property(p => p.CurrentText).IsRequired().HasMaxLength(FieldLimits.PromptText);
            e.Property(p => p.Category).IsRequired().HasMaxLength(FieldLimits.Category);
            e.Property(p => p.AiTool).IsRequired().HasMaxLength(FieldLimits.AiTool);
            e.Property(p => p.Author).IsRequired().HasMaxLength(FieldLimits.PersonName);
            e.Property(p => p.ConcurrencyStamp).IsRequired().HasMaxLength(36).IsConcurrencyToken();
            e.Property(p => p.OwnerId).HasMaxLength(450);
            e.Property(p => p.Slug).IsRequired().HasMaxLength(Slugs.MaxLength);
            e.HasIndex(p => p.Slug).IsUnique();
            e.HasIndex(p => p.OwnerId);
        });

        modelBuilder.Entity<LookupValue>(e =>
        {
            e.HasKey(v => v.Id);
            e.Property(v => v.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.Name).IsRequired().HasMaxLength(Math.Max(FieldLimits.Category, FieldLimits.AiTool));
            e.HasIndex(v => new { v.Kind, v.Name }).IsUnique();
            // The lists that used to be hard-coded, plus two tools the owner had
            // planned; admins change them on the Lists page from here on.
            var id = 0;
            e.HasData(
                new[] { "Code Generation", "Bug Fixing", "Testing", "Documentation", "UI Design", "Business Analysis", "Learning" }
                    .Select(n => new LookupValue { Id = ++id, Kind = LookupKind.Category, Name = n })
                    .Concat(new[] { "ChatGPT", "Copilot", "Claude", "Gemini", "Grok", "Mistral" }
                        .Select(n => new LookupValue { Id = ++id, Kind = LookupKind.Tool, Name = n }))
                    .ToArray());
        });

        modelBuilder.Entity<Tag>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).IsRequired().HasMaxLength(TagNames.MaxLength);
            e.Property(t => t.Slug).IsRequired().HasMaxLength(TagNames.MaxLength);
            e.HasIndex(t => t.Slug).IsUnique();
        });

        modelBuilder.Entity<PromptTag>(e =>
        {
            e.HasKey(pt => new { pt.PromptId, pt.TagId });
            e.HasOne(pt => pt.Prompt).WithMany(p => p.PromptTags).HasForeignKey(pt => pt.PromptId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(pt => pt.Tag).WithMany(t => t.PromptTags).HasForeignKey(pt => pt.TagId).OnDelete(DeleteBehavior.Cascade);
            // The library filters by tag.
            e.HasIndex(pt => pt.TagId);
        });

        modelBuilder.Entity<PromptVersion>(e =>
        {
            e.HasKey(v => v.Id);
            e.Property(v => v.PromptText).IsRequired().HasMaxLength(FieldLimits.PromptText);
            e.Property(v => v.ChangeNote).HasMaxLength(FieldLimits.ChangeNote);
            e.Property(v => v.CreatedBy).IsRequired().HasMaxLength(FieldLimits.PersonName);
            e.Property(v => v.CreatedById).HasMaxLength(450);
            e.Property(v => v.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.PromotedBy).HasMaxLength(FieldLimits.PersonName);
            e.HasOne(v => v.Prompt)
             .WithMany(p => p.Versions)
             .HasForeignKey(v => v.PromptId)
             .OnDelete(DeleteBehavior.Cascade);

            // A version number is assigned once per prompt; the database is the
            // last line of defence if two writers pick the same number.
            e.HasIndex(v => new { v.PromptId, v.VersionNumber }).IsUnique();
        });

        modelBuilder.Entity<PromptRating>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.UserName).IsRequired().HasMaxLength(FieldLimits.PersonName);
            e.Property(r => r.UserId).IsRequired().HasMaxLength(450);
            e.Property(r => r.Comment).HasMaxLength(FieldLimits.Comment);
            e.HasOne(r => r.Prompt)
             .WithMany(p => p.Ratings)
             .HasForeignKey(r => r.PromptId)
             .OnDelete(DeleteBehavior.Cascade);

            // One rating per account per prompt. RateAsync relies on this index
            // instead of a check-then-insert, which two requests could both pass.
            e.HasIndex(r => new { r.PromptId, r.UserId }).IsUnique();
        });

        modelBuilder.Entity<PromptRun>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Model).IsRequired().HasMaxLength(100);
            e.Property(r => r.ServedByModel).HasMaxLength(100);
            e.Property(r => r.Provider).IsRequired().HasMaxLength(50);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.StopReason).HasMaxLength(50);
            e.Property(r => r.RunBy).HasMaxLength(FieldLimits.PersonName);
            e.Property(r => r.RunByUserId).HasMaxLength(450);
            e.Property(r => r.CostUsd).HasPrecision(18, 6);
            e.HasOne(r => r.Prompt)
             .WithMany()
             .HasForeignKey(r => r.PromptId)
             .OnDelete(DeleteBehavior.Cascade);

            // Runs are listed newest first, per prompt and overall.
            e.HasIndex(r => new { r.PromptId, r.CreatedAt });
            e.HasIndex(r => r.CreatedAt);
        });

        modelBuilder.Entity<PromptInputSet>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Name).IsRequired().HasMaxLength(FieldLimits.InputSetName);
            e.Property(s => s.ValuesJson).IsRequired();
            e.HasOne(s => s.Prompt)
             .WithMany()
             .HasForeignKey(s => s.PromptId)
             .OnDelete(DeleteBehavior.Cascade);

            // Saving under an existing name replaces that set.
            e.HasIndex(s => new { s.PromptId, s.Name }).IsUnique();
        });

        modelBuilder.Entity<ApiKey>(e =>
        {
            e.HasKey(k => k.Id);
            e.Property(k => k.Name).IsRequired().HasMaxLength(100);
            e.Property(k => k.Prefix).IsRequired().HasMaxLength(16);
            e.Property(k => k.Hash).IsRequired().HasMaxLength(64);
            e.Property(k => k.OwnerId).IsRequired().HasMaxLength(450);
            e.Ignore(k => k.IsActive);
            // Every API request looks its key up by hash.
            e.HasIndex(k => k.Hash).IsUnique();
            e.HasIndex(k => k.OwnerId);
        });

        modelBuilder.Entity<EvalCase>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).IsRequired().HasMaxLength(100);
            e.Property(c => c.Regex).HasMaxLength(1000);
            e.HasOne(c => c.Prompt).WithMany().HasForeignKey(c => c.PromptId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EvalRun>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Model).IsRequired().HasMaxLength(100);
            e.Property(r => r.JudgeModel).HasMaxLength(100);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.CostUsd).HasPrecision(18, 6);
            e.Property(r => r.JudgeCostUsd).HasPrecision(18, 6);
            e.Property(r => r.RunBy).HasMaxLength(FieldLimits.PersonName);
            e.Property(r => r.RunByUserId).HasMaxLength(450);
            e.Ignore(r => r.PassRate);
            e.HasOne(r => r.Prompt).WithMany().HasForeignKey(r => r.PromptId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.PromptId, r.Model, r.VersionNumber });
            // The worker looks for queued and interrupted runs on start-up.
            e.HasIndex(r => r.Status);
        });

        modelBuilder.Entity<EvalResult>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.CaseName).IsRequired().HasMaxLength(100);
            e.HasOne(r => r.EvalRun).WithMany(r => r.Results).HasForeignKey(r => r.EvalRunId).OnDelete(DeleteBehavior.Cascade);
            // Deleting a case keeps its past results under the stored case name.
            e.HasOne<EvalCase>().WithMany().HasForeignKey(r => r.EvalCaseId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<PromptRun>().WithMany().HasForeignKey(r => r.PromptRunId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
