using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Homestead.Data.Entities;

namespace Homestead.Features.Export;

public interface IBackupUploader
{
    Task UploadAsync(BackupSettings settings, string accessKey, string secretKey, string objectKey, Stream archive, CancellationToken ct);
}

public sealed class S3BackupUploader : IBackupUploader
{
    public async Task UploadAsync(BackupSettings settings, string accessKey, string secretKey, string objectKey, Stream archive, CancellationToken ct)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = settings.ForcePathStyle,
            AuthenticationRegion = settings.Region,
            MaxErrorRetry = 2,
            // Compatible servers may not implement AWS's optional checksum extensions.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        if (settings.Endpoint.Length > 0) config.ServiceURL = settings.Endpoint;
        else config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        using var client = new BackupS3Client(new BasicAWSCredentials(accessKey, secretKey), config);
        using var transfer = new TransferUtility(client);
        await transfer.UploadAsync(new TransferUtilityUploadRequest
        {
            BucketName = settings.Bucket,
            Key = objectKey,
            InputStream = archive,
            ContentType = "application/zip",
            AutoCloseStream = false
        }, ct);
    }

    // Send ordinary signed payloads: some compatible servers do not support AWS chunk framing.
    private sealed class BackupS3Client(AWSCredentials credentials, AmazonS3Config config) : AmazonS3Client(credentials, config)
    {
        public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            request.UseChunkEncoding = false;
            return base.PutObjectAsync(request, cancellationToken);
        }

        public override Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken = default)
        {
            request.UseChunkEncoding = false;
            return base.UploadPartAsync(request, cancellationToken);
        }
    }
}
