namespace Homestead.Data.Entities;

public enum BackupFrequency { BeforePublication, Daily, Weekly }

// Private configuration: never include this entity in public rendering models.
public sealed class BackupSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public BackupFrequency Frequency { get; set; }
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "";
    public string Prefix { get; set; } = "homestead";
    public bool ForcePathStyle { get; set; } = true;
    public string ProtectedAccessKey { get; set; } = "";
    public string ProtectedSecretKey { get; set; } = "";
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }
    public string? LastObjectKey { get; set; }
    public string? LastError { get; set; }
}
