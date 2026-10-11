using Homestead.Data.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Homestead.Data;

public sealed class HomesteadDbContext(DbContextOptions<HomesteadDbContext> options)
    : IdentityUserContext<ApplicationUser>(options)
{
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<PostDraft> PostDrafts => Set<PostDraft>();
    public DbSet<PostPublishedSnapshot> PublishedSnapshots => Set<PostPublishedSnapshot>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<PostMedia> PostMedia => Set<PostMedia>();
    public DbSet<ReviewDetails> Reviews => Set<ReviewDetails>();
    public DbSet<FitnessActivity> FitnessActivities => Set<FitnessActivity>();
    public DbSet<GuideStep> GuideSteps => Set<GuideStep>();
    public DbSet<GuideSupply> GuideSupplies => Set<GuideSupply>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PostTag> PostTags => Set<PostTag>();
    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();
    public DbSet<BackupSettings> BackupSettings => Set<BackupSettings>();
    public DbSet<NewsletterSettings> NewsletterSettings => Set<NewsletterSettings>();
    public DbSet<NewsletterSubscriber> NewsletterSubscribers => Set<NewsletterSubscriber>();
    public DbSet<NewsletterPost> NewsletterPosts => Set<NewsletterPost>();
    public DbSet<NewsletterDelivery> NewsletterDeliveries => Set<NewsletterDelivery>();
    public DbSet<SiteGeneration> SiteGenerations => Set<SiteGeneration>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
        => builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<NewsletterSettings>().ToTable("NewsletterSettings", t => t.HasCheckConstraint("CK_NewsletterSettings_Id", "Id = 1"));
        builder.Entity<NewsletterSubscriber>(e =>
        {
            e.Property(x => x.Email).UseCollation("NOCASE").HasMaxLength(254);
            e.HasIndex(x => x.Email).IsUnique();
            e.ToTable("NewsletterSubscribers", t =>
            {
                t.HasCheckConstraint("CK_NewsletterSubscriber_Frequency", "Frequency BETWEEN 0 AND 1 AND PendingFrequency BETWEEN 0 AND 1");
                t.HasCheckConstraint("CK_NewsletterSubscriber_Types", "PostTypes BETWEEN 1 AND 127 AND PendingPostTypes BETWEEN 0 AND 127");
            });
        });
        builder.Entity<NewsletterPost>().HasKey(x => x.PostId);
        builder.Entity<NewsletterDelivery>(e =>
        {
            e.HasIndex(x => new { x.SubscriberId, x.Key }).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
            e.HasOne<NewsletterSubscriber>().WithMany().HasForeignKey(x => x.SubscriberId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable("NewsletterDeliveries", t => t.HasCheckConstraint("CK_NewsletterDelivery_Status", "Status BETWEEN 0 AND 2"));
        });
        builder.Entity<BackupSettings>(e => e.ToTable("BackupSettings", t =>
        {
            t.HasCheckConstraint("CK_BackupSettings_Id", "Id = 1");
            t.HasCheckConstraint("CK_BackupSettings_Frequency", "Frequency BETWEEN 0 AND 2");
        }));
        builder.Entity<ApplicationUser>(e =>
        {
            e.HasIndex(x => x.AdminSlot).IsUnique();
            e.ToTable("AspNetUsers", t => t.HasCheckConstraint("CK_User_AdminSlot", "AdminSlot = 1"));
        });
        builder.Entity<Post>(e =>
        {
            e.Property(x => x.Slug).UseCollation("NOCASE").HasMaxLength(200);
            e.HasIndex(x => x.Slug).IsUnique();
            e.ToTable("Posts", t =>
            {
                t.HasCheckConstraint("CK_Post_Type", "Type BETWEEN 0 AND 6");
                t.HasCheckConstraint("CK_Post_Status", "Status BETWEEN 0 AND 3");
                t.HasCheckConstraint("CK_Post_PublishedSlug", "FirstPublishedAtUtc IS NULL OR (Slug IS NOT NULL AND length(trim(Slug)) > 0)");
                t.HasTrigger("TR_Post_LockedPublication");
            });
        });
        builder.Entity<PostDraft>(e =>
        {
            e.HasKey(x => x.PostId);
            e.HasOne(x => x.Post).WithOne(x => x.Draft).HasForeignKey<PostDraft>(x => x.PostId);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.LocationName).HasMaxLength(300);
            e.Property(x => x.LocationAddress).HasMaxLength(1000);
            e.ToTable("PostDrafts", t => t.HasCheckConstraint("CK_Draft_Coordinates",
                "(Latitude IS NULL AND Longitude IS NULL) OR (Latitude IS NOT NULL AND Longitude IS NOT NULL AND Latitude BETWEEN -90 AND 90 AND Longitude BETWEEN -180 AND 180)"));
        });
        builder.Entity<PostPublishedSnapshot>(e =>
        {
            e.HasKey(x => x.PostId);
            e.HasOne(x => x.Post).WithOne(x => x.PublishedSnapshot).HasForeignKey<PostPublishedSnapshot>(x => x.PostId);
        });
        builder.Entity<ReviewDetails>(e =>
        {
            e.HasKey(x => x.PostId);
            e.HasOne(x => x.Post).WithOne(x => x.Review).HasForeignKey<ReviewDetails>(x => x.PostId);
            e.ToTable("Reviews", t => t.HasCheckConstraint("CK_Review_Rating", "Rating IS NULL OR Rating BETWEEN 1 AND 5"));
        });
        builder.Entity<FitnessActivity>(e =>
        {
            e.HasKey(x => x.PostId);
            e.HasOne(x => x.Post).WithOne(x => x.Activity).HasForeignKey<FitnessActivity>(x => x.PostId);
        });
        builder.Entity<GuideStep>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(300);
            e.HasAlternateKey(x => new { x.Id, x.PostId });
            e.HasOne(x => x.Post).WithMany(x => x.Steps).HasForeignKey(x => x.PostId);
            e.HasIndex(x => new { x.PostId, x.Order }).IsUnique();
            e.ToTable("GuideSteps", t =>
            {
                t.HasCheckConstraint("CK_Step_Order", "\"Order\" >= 0");
                t.HasCheckConstraint("CK_Step_Duration", "DurationSeconds IS NULL OR DurationSeconds BETWEEN 1 AND 31536000");
            });
        });
        builder.Entity<GuideSupply>(e =>
        {
            e.HasOne(x => x.Post).WithMany(x => x.Supplies).HasForeignKey(x => x.PostId);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.Quantity).HasMaxLength(300);
            e.HasIndex(x => new { x.PostId, x.Kind, x.Order }).IsUnique();
            e.ToTable("GuideSupplies", t =>
            {
                t.HasCheckConstraint("CK_Supply_Kind", "Kind BETWEEN 0 AND 1");
                t.HasCheckConstraint("CK_Supply_Order", "\"Order\" >= 0");
            });
        });
        builder.Entity<MediaAsset>().HasIndex(x => x.StoredFilename).IsUnique();
        builder.Entity<PostMedia>(e =>
        {
            e.HasOne(x => x.Post).WithMany(x => x.Media).HasForeignKey(x => x.PostId);
            e.HasOne(x => x.MediaAsset).WithMany().HasForeignKey(x => x.MediaAssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.GuideStep).WithMany().HasForeignKey(x => new { x.GuideStepId, x.PostId })
                .HasPrincipalKey(x => new { x.Id, x.PostId }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PostId, x.Role, x.SortOrder }).IsUnique();
            e.ToTable("PostMedia", t =>
            {
                t.HasCheckConstraint("CK_Media_Role", "Role BETWEEN 0 AND 3");
                t.HasCheckConstraint("CK_Media_Order", "SortOrder >= 0");
            });
        });
        builder.Entity<Tag>(e =>
        {
            e.Property(x => x.Slug).UseCollation("NOCASE");
            e.Property(x => x.NormalizedName).UseCollation("NOCASE");
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.NormalizedName).IsUnique();
        });
        builder.Entity<PostTag>(e =>
        {
            e.HasKey(x => new { x.PostId, x.TagId });
            e.HasOne(x => x.Post).WithMany(x => x.Tags).HasForeignKey(x => x.PostId);
            e.HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId);
        });
        builder.Entity<SiteSettings>(e =>
        {
            e.Property(x => x.ThemeId).HasDefaultValue("porch");
            e.Property(x => x.EnableNotes).HasDefaultValue(true);
            e.Property(x => x.EnableArticles).HasDefaultValue(true);
            e.Property(x => x.EnableReviews).HasDefaultValue(true);
            e.Property(x => x.EnableGuides).HasDefaultValue(true);
            e.Property(x => x.EnableGalleries).HasDefaultValue(true);
            e.Property(x => x.EnableCheckins).HasDefaultValue(true);
            e.Property(x => x.EnableFitness).HasDefaultValue(true);
            e.Property(x => x.FitnessUnits).HasDefaultValue(FitnessUnitSystem.Metric);
            e.ToTable("SiteSettings", t =>
            {
                t.HasCheckConstraint("CK_Settings_Singleton", "Id = 1");
                t.HasCheckConstraint("CK_Settings_FitnessUnits", "FitnessUnits BETWEEN 0 AND 1");
            });
            e.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.AvatarMediaId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.LogoMediaId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.HeaderMediaId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private void NormalizeTags()
    {
        foreach (var entry in ChangeTracker.Entries<Tag>().Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Name = entry.Entity.Name.Trim().Normalize();
            entry.Entity.NormalizedName = entry.Entity.Name.ToUpperInvariant();
            entry.Entity.Slug = entry.Entity.Slug.Trim().Normalize().ToLowerInvariant();
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        NormalizeTags();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        NormalizeTags();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}

public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    value => value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime(),
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
