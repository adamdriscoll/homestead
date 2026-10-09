using System.ComponentModel.DataAnnotations;
using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Features.Export;

public sealed record AutomatedBackupResult(bool Succeeded, string Message);

public sealed class AutomatedBackupService(HomesteadDbContext db, BackupService backups, PublicationLock writer,
    IDataProtectionProvider protection, IBackupUploader uploader, TimeProvider clock, ILogger<AutomatedBackupService> logger)
{
    private readonly IDataProtector protector = protection.CreateProtector("Homestead.S3BackupCredentials.v1");

    public async Task SaveAsync(BackupInput input, CancellationToken ct = default)
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(input, new ValidationContext(input), errors, validateAllProperties: true))
            throw new InvalidOperationException(errors[0].ErrorMessage);
        await writer.Semaphore.WaitAsync(ct);
        try
        {
            var settings = await db.BackupSettings.SingleOrDefaultAsync(ct) ?? new BackupSettings();
            var access = input.ClearCredentials ? "" : settings.ProtectedAccessKey;
            var secret = input.ClearCredentials ? "" : settings.ProtectedSecretKey;
            if (!string.IsNullOrWhiteSpace(input.AccessKey)) access = protector.Protect(input.AccessKey.Trim());
            if (!string.IsNullOrWhiteSpace(input.SecretKey)) secret = protector.Protect(input.SecretKey);
            if (input.Enabled && (access.Length == 0 || secret.Length == 0))
                throw new InvalidOperationException("Enter both credentials before enabling backups.");
            if (settings.Region != input.Region || settings.Endpoint != (input.Endpoint ?? "") || settings.Bucket != (input.Bucket ?? "") ||
                settings.Prefix != (input.Prefix ?? "").Trim('/') || settings.Frequency != input.Frequency || settings.Enabled != input.Enabled)
            {
                settings.LastAttemptAtUtc = null; settings.LastSuccessAtUtc = null; settings.LastObjectKey = null; settings.LastError = null;
            }
            settings.Enabled = input.Enabled; settings.Frequency = input.Frequency;
            settings.Endpoint = input.Endpoint ?? ""; settings.Region = input.Region;
            settings.Bucket = input.Bucket ?? ""; settings.Prefix = (input.Prefix ?? "").Trim('/');
            settings.ForcePathStyle = input.ForcePathStyle;
            settings.ProtectedAccessKey = access; settings.ProtectedSecretKey = secret;
            if (db.Entry(settings).State == EntityState.Detached) db.BackupSettings.Add(settings);
            await db.SaveChangesAsync(ct);
        }
        finally { writer.Semaphore.Release(); }
    }

    public async Task<AutomatedBackupResult> RunAsync(bool scheduled = false, CancellationToken ct = default)
    {
        await writer.Semaphore.WaitAsync(ct);
        try { return await RunLockedAsync(false, scheduled, ct); }
        finally { writer.Semaphore.Release(); }
    }

    // Publication already holds the writer lock. Never reacquire it while capturing a backup.
    public async Task<AutomatedBackupResult> RunLockedAsync(bool beforePublication, bool scheduled = false, CancellationToken ct = default)
    {
        var settings = await db.BackupSettings.SingleOrDefaultAsync(ct);
        if (settings is null || !settings.Enabled) return new(true, "Automated backups are disabled.");
        if (beforePublication && settings.Frequency != BackupFrequency.BeforePublication) return new(true, "No publication backup required.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (scheduled)
        {
            if (settings.Frequency == BackupFrequency.BeforePublication) return new(true, "No scheduled backup required.");
            var interval = settings.Frequency == BackupFrequency.Daily ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
            if (settings.LastSuccessAtUtc is { } success && now < success + interval ||
                settings.LastAttemptAtUtc is { } attempt && now < attempt + TimeSpan.FromMinutes(15))
                return new(true, "Next backup is not due yet.");
        }
        settings.LastAttemptAtUtc = now;
        await db.SaveChangesAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            await using var archive = await backups.CreateLockedAsync(timeout.Token);
            var key = $"{(settings.Prefix.Length > 0 ? settings.Prefix + "/" : "")}homestead-{now:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.zip";
            await uploader.UploadAsync(settings, protector.Unprotect(settings.ProtectedAccessKey),
                protector.Unprotect(settings.ProtectedSecretKey), key, archive, timeout.Token);
            settings.LastSuccessAtUtc = clock.GetUtcNow().UtcDateTime; settings.LastObjectKey = key; settings.LastError = null;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Automated backup uploaded successfully.");
            return new(true, "Full backup uploaded successfully.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Provider exception messages can contain signed URLs or credentials. Store only a safe diagnostic.
            settings.LastError = "Backup failed. Check S3 settings, bucket permissions, connectivity, and local storage, then retry.";
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Automated backup failed ({FailureType}).", ex.GetType().Name);
            return new(false, settings.LastError);
        }
    }
}
