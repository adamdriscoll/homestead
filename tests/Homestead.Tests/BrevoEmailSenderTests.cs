using System.Net;
using System.Text.Json;
using Homestead.Features.Newsletter;

namespace Homestead.Tests;

public sealed class BrevoEmailSenderTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;
        public string Body { get; set; } = "{\"messageId\":\"accepted\"}";
        public string RequestBody { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.brevo.com/v3/smtp/email", request.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("private-key", request.Headers.GetValues("api-key").Single());
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(Status) { Content = new StringContent(Body) };
        }
    }

    [Fact]
    public async Task Sends_one_recipient_with_sender_bodies_and_durable_idempotency_key()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler);
        var sender = new BrevoEmailSender(client); var id = Guid.NewGuid();
        var email = new NewsletterEmail(id, "reader@example.test", "Weekly summary", "<html><body>Safe text</body></html>", "Safe text");
        await sender.SendAsync("private-key", "owner@example.test", "Owner", email, default);
        using var request = JsonDocument.Parse(handler.RequestBody); var root = request.RootElement;
        Assert.Equal("owner@example.test", root.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("Owner", root.GetProperty("sender").GetProperty("name").GetString());
        Assert.Equal("reader@example.test", Assert.Single(root.GetProperty("to").EnumerateArray()).GetProperty("email").GetString());
        Assert.Equal(email.Html, root.GetProperty("htmlContent").GetString()); Assert.Equal(email.Text, root.GetProperty("textContent").GetString());
        Assert.Equal(id.ToString("D"), root.GetProperty("headers").GetProperty("idempotencyKey").GetString());
        Assert.DoesNotContain("private-key", handler.RequestBody);
        handler.Status = HttpStatusCode.BadRequest; handler.Body = "{\"code\":\"duplicate_parameter\",\"message\":\"duplicate idempotency key\"}";
        await sender.SendAsync("private-key", "owner@example.test", "Owner", email, default);
        handler.Status = HttpStatusCode.TooManyRequests; handler.Body = "provider private response";
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync("private-key", "owner@example.test", "Owner", email, default));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode); Assert.DoesNotContain("private", error.Message);
    }
}
