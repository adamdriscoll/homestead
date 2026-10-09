using System.Net;
using System.Net.Sockets;
using Homestead.Data.Entities;
using Homestead.Features.Export;

namespace Homestead.Tests;

public sealed class S3BackupUploaderTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(17 * 1024 * 1024)]
    public async Task Compatible_upload_uses_signing_region_path_style_and_private_zip_object(int size)
    {
        // Loopback HTTP is only a wire-test fixture. The settings form requires HTTPS.
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start(); var number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        using var server = new HttpListener(); server.Prefixes.Add($"http://127.0.0.1:{number}/"); server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var bytes = new byte[size]; "PK-test-archive"u8.CopyTo(bytes);
        using var archive = new MemoryStream(bytes);
        var upload = new S3BackupUploader().UploadAsync(new BackupSettings
        {
            Endpoint = $"http://127.0.0.1:{number}",
            Bucket = "private-backups",
            Region = "us-east-1",
            ForcePathStyle = true
        }, "test-access", "test-secret", "homestead/backup.zip", archive, timeout.Token);
        var parts = new SortedDictionary<int, byte[]>();
        var complete = false;
        while (!complete)
        {
            var context = await server.GetContextAsync().WaitAsync(timeout.Token);
            context.Response.StatusCode = 200; context.Response.Headers["ETag"] = "\"test-etag\"";
            try
            {
                Assert.Equal("/private-backups/homestead/backup.zip", context.Request.Url!.AbsolutePath);
                Assert.Contains("AWS4-HMAC-SHA256", context.Request.Headers["Authorization"]!);
                Assert.Contains("/us-east-1/s3/aws4_request", context.Request.Headers["Authorization"]!);
                Assert.Null(context.Request.Headers["x-amz-acl"]);
                Assert.Null(context.Request.Headers["Content-Encoding"]);
                using var body = new MemoryStream(); await context.Request.InputStream.CopyToAsync(body, timeout.Token);
                if (context.Request.HttpMethod == "PUT")
                {
                    var part = context.Request.QueryString["partNumber"];
                    parts[part is null ? 1 : int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)] = body.ToArray();
                    complete = part is null;
                    if (part is null) Assert.Equal("application/zip", context.Request.ContentType);
                }
                else
                {
                    Assert.Equal("POST", context.Request.HttpMethod);
                    var initiate = context.Request.Url.Query.StartsWith("?uploads", StringComparison.Ordinal);
                    var xml = initiate
                        ? "<InitiateMultipartUploadResult><Bucket>private-backups</Bucket><Key>homestead/backup.zip</Key><UploadId>test-upload</UploadId></InitiateMultipartUploadResult>"
                        : "<CompleteMultipartUploadResult><Bucket>private-backups</Bucket><Key>homestead/backup.zip</Key><ETag>test-etag</ETag></CompleteMultipartUploadResult>";
                    context.Response.ContentType = "application/xml";
                    await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(xml), timeout.Token);
                    complete = !initiate;
                }
            }
            finally { context.Response.Close(); }
        }
        await upload;
        Assert.Equal(bytes, parts.Values.SelectMany(x => x).ToArray());
        Assert.True(archive.CanRead);
    }
}
