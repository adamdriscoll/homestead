namespace Homestead.Data.Entities;

public enum PostType { Microblog, Article, Review, Guide, Gallery, Checkin, Fitness }
public enum PostStatus { Draft, Published, Unpublished, Deleted }
public enum MediaRole { Inline, Cover, Gallery, GuideStep }
public enum GenerationStatus { Building, Succeeded, Failed }
public enum GuideSupplyKind { Tool, Material }

public sealed class Post
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public PostType Type { get; set; }
    public PostStatus Status { get; set; }
    public string? Slug { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FirstPublishedAtUtc { get; set; }
    public DateTime? LastPublishedAtUtc { get; set; }
    public PostDraft? Draft { get; set; }
    public PostPublishedSnapshot? PublishedSnapshot { get; set; }
    public PostSchedule? Schedule { get; set; }
    public ReviewDetails? Review { get; set; }
    public FitnessActivity? Activity { get; set; }
    public List<GuideStep> Steps { get; set; } = [];
    public List<GuideSupply> Supplies { get; set; } = [];
    public List<PostMedia> Media { get; set; } = [];
    public List<PostTag> Tags { get; set; } = [];
}

public sealed class PostSchedule
{
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public DateTime PublishAtUtc { get; set; }
    public string SnapshotJson { get; set; } = "{}";
    public bool Committed { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public string? Error { get; set; }
}

public sealed class PostDraft
{
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public string? Title { get; set; }
    public string BodyMarkdown { get; set; } = "";
    public string? Summary { get; set; }
    public long Version { get; set; }
    public Guid? CoverMediaId { get; set; }
    public string? LocationName { get; set; }
    public string? LocationAddress { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

public sealed class PostPublishedSnapshot
{
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public int SchemaVersion { get; set; } = 1;
    public string SnapshotJson { get; set; } = "{}";
    public DateTime PublishedAtUtc { get; set; }
    public bool IsActive { get; set; }
}

public sealed class ReviewDetails
{
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public string Subject { get; set; } = "";
    public int? Rating { get; set; }
    public string? ExternalUrl { get; set; }
}

public sealed class FitnessActivity
{
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public byte[] OriginalGpx { get; set; } = [];
    public string TrackJson { get; set; } = "{}";
}

public sealed class GuideStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public int Order { get; set; }
    public string? Title { get; set; }
    public string BodyMarkdown { get; set; } = "";
    public int? DurationSeconds { get; set; }
}

public sealed class GuideSupply
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public GuideSupplyKind Kind { get; set; }
    public int Order { get; set; }
    public string Name { get; set; } = "";
    public string? Url { get; set; }
    public string? Quantity { get; set; }
}

public sealed class MediaAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StoredFilename { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long Size { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PostMedia
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public Guid MediaAssetId { get; set; }
    public MediaAsset MediaAsset { get; set; } = null!;
    public MediaRole Role { get; set; }
    public bool InlineOnly { get; set; }
    public int SortOrder { get; set; }
    public string? Caption { get; set; }
    public string? AltText { get; set; }
    public Guid? GuideStepId { get; set; }
    public GuideStep? GuideStep { get; set; }
}

public sealed class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string Slug { get; set; } = "";
}

public sealed class PostTag
{
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public Guid TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}

public sealed class SiteGeneration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime BuiltAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    public string OutputRoot { get; set; } = "";
    public GenerationStatus Status { get; set; }
    public string? Error { get; set; }
}
