namespace Homestead.Features.Newsletter;

// Request-only form data. Never saved into a generation; shared rendering supplies the form markup.
public sealed record NewsletterFormModel(string Mode, SubscriptionInput Input, string? AntiforgeryToken = null,
    string? Token = null, string? Message = null, string? Error = null);
