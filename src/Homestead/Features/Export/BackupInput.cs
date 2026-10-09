using System.ComponentModel.DataAnnotations;
using Homestead.Data.Entities;

namespace Homestead.Features.Export;

public sealed class BackupInput : IValidatableObject
{
    public bool Enabled { get; set; }
    [EnumDataType(typeof(BackupFrequency))] public BackupFrequency Frequency { get; set; }
    [StringLength(2048), Display(Name = "S3 endpoint URL")] public string? Endpoint { get; set; }
    [StringLength(100)] public string Region { get; set; } = "us-east-1";
    [StringLength(63)] public string? Bucket { get; set; }
    [StringLength(500), Display(Name = "Object key prefix")] public string? Prefix { get; set; } = "homestead";
    [Display(Name = "Use path-style bucket URLs")] public bool ForcePathStyle { get; set; } = true;
    [StringLength(1024), Display(Name = "Access key ID")] public string? AccessKey { get; set; }
    [StringLength(1024), Display(Name = "Secret access key")] public string? SecretKey { get; set; }
    [Display(Name = "Clear saved credentials")] public bool ClearCredentials { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrEmpty(Endpoint) && (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var url) ||
            url.Scheme != "https" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || url.AbsolutePath != "/"))
            yield return new("Use an HTTPS endpoint without credentials, path, query, or fragment.", [nameof(Endpoint)]);
        if (!string.IsNullOrEmpty(Bucket) && !System.Text.RegularExpressions.Regex.IsMatch(Bucket, @"\A[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]\z"))
            yield return new("Use a bucket name of 3–63 lowercase letters, numbers, dots, or hyphens.", [nameof(Bucket)]);
        if (string.IsNullOrWhiteSpace(Region) || !System.Text.RegularExpressions.Regex.IsMatch(Region, @"\A[a-z0-9-]+\z"))
            yield return new("Enter the signing region, such as us-east-1.", [nameof(Region)]);
        if (!string.IsNullOrEmpty(Prefix) && (Prefix.Contains('\\') || Prefix.Any(char.IsControl) || Prefix.Trim('/').Split('/').Any(x => x is "" or "." or "..")))
            yield return new("Use a prefix with ordinary slash-separated names.", [nameof(Prefix)]);
        if (Enabled && string.IsNullOrWhiteSpace(Bucket)) yield return new("Enter a bucket before enabling backups.", [nameof(Bucket)]);
    }

    public static BackupInput From(BackupSettings settings) => new()
    {
        Enabled = settings.Enabled,
        Frequency = settings.Frequency,
        Endpoint = settings.Endpoint,
        Region = settings.Region,
        Bucket = settings.Bucket,
        Prefix = settings.Prefix,
        ForcePathStyle = settings.ForcePathStyle
    };
}
