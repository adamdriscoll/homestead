using System.ComponentModel.DataAnnotations;
using Homestead.Data.Entities;

namespace Homestead.Features.Newsletter;

public sealed class NewsletterInput
{
    public bool Enabled { get; set; }
    [StringLength(1024), DataType(DataType.Password)] public string? ApiKey { get; set; }
    public bool ClearApiKey { get; set; }
    [StringLength(254), EmailAddress, Display(Name = "Sender email")] public string? SenderEmail { get; set; }
    [StringLength(120), Display(Name = "Sender name")] public string? SenderName { get; set; }
    public static NewsletterInput From(NewsletterSettings settings) => new()
    { Enabled = settings.Enabled, SenderEmail = settings.SenderEmail, SenderName = settings.SenderName };
}

public sealed class SubscriptionInput : IValidatableObject
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [EnumDataType(typeof(NewsletterFrequency))] public NewsletterFrequency Frequency { get; set; }
    public List<PostType> PostTypes { get; set; } = Enum.GetValues<PostType>().ToList();
    public int TypeMask => PostTypes.Aggregate(0, (mask, type) => mask | (1 << (int)type));
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (PostTypes.Count == 0 || PostTypes.Any(type => !Enum.IsDefined(type)))
            yield return new("Choose at least one valid post type.", [nameof(PostTypes)]);
    }
    public static SubscriptionInput From(NewsletterSubscriber subscriber) => new()
    {
        Email = subscriber.Email,
        Frequency = subscriber.Frequency,
        PostTypes = Enum.GetValues<PostType>().Where(type => (subscriber.PostTypes & (1 << (int)type)) != 0).ToList()
    };
}
