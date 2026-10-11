namespace Homestead.Data.Entities;

public enum NewsletterFrequency { Instant, Weekly }
public enum NewsletterDeliveryStatus { Pending, Sent, Cancelled }

// Private configuration and reader data, included only in full backups.
public sealed class NewsletterSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public string ProtectedApiKey { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string SenderName { get; set; } = "";
    public DateTime? EnabledAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class NewsletterSubscriber
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public bool Subscribed { get; set; }
    public NewsletterFrequency Frequency { get; set; }
    public int PostTypes { get; set; } = 127;
    public DateTime? ConfirmedAtUtc { get; set; }
    public DateTime? NextDigestAtUtc { get; set; }
    public Guid TokenVersion { get; set; } = Guid.NewGuid();
    public Guid PendingToken { get; set; }
    public NewsletterFrequency PendingFrequency { get; set; }
    public int PendingPostTypes { get; set; }
    public DateTime? ConfirmationExpiresAtUtc { get; set; }
    public DateTime? LastConfirmationAtUtc { get; set; }
}

// A post's first successful activation is remembered even after removal or updates.
public sealed class NewsletterPost
{
    public Guid PostId { get; set; }
    public DateTime ActivatedAtUtc { get; set; }
    public string SnapshotJson { get; set; } = "";
}

public sealed class NewsletterDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriberId { get; set; }
    public string Key { get; set; } = "";
    public string PostIdsJson { get; set; } = "[]";
    public string? Subject { get; set; }
    public string? Html { get; set; }
    public string? Text { get; set; }
    public NewsletterDeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
}
