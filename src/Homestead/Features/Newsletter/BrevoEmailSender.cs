using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Homestead.Features.Newsletter;

public sealed record NewsletterEmail(Guid Id, string To, string Subject, string Html, string Text);

public interface INewsletterEmailSender
{
    Task SendAsync(string apiKey, string senderEmail, string senderName, NewsletterEmail email, CancellationToken ct);
}

public sealed class BrevoEmailSender(HttpClient client) : INewsletterEmailSender
{
    public async Task SendAsync(string apiKey, string senderEmail, string senderName, NewsletterEmail email, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
        request.Headers.Add("api-key", apiKey);
        request.Content = JsonContent.Create(new
        {
            sender = new { email = senderEmail, name = senderName },
            to = new[] { new { email = email.To } },
            subject = email.Subject,
            htmlContent = email.Html,
            textContent = email.Text,
            headers = new Dictionary<string, string> { ["idempotencyKey"] = email.Id.ToString("D") }
        });
        using var response = await client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;
        // Brevo acknowledges repeated accepted requests with this code. Never log provider bodies.
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            try
            {
                var error = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (error.TryGetProperty("code", out var code) && code.GetString() == "duplicate_parameter") return;
            }
            catch (JsonException) { }
        }
        throw new HttpRequestException("Brevo did not accept the email.", null, response.StatusCode);
    }
}
