using Homestead.Data.Entities;

namespace Homestead.Features.Articles;

public static class PostValidation
{
    public static string? LocationError(ArticleInput input)
    {
        if (input.LocationName?.Length > 300 || input.LocationAddress?.Length > 1000)
            return "Use a place name of at most 300 characters and an address of at most 1000 characters.";
        if (input.Latitude.HasValue != input.Longitude.HasValue ||
            input.Latitude is { } latitude && (!double.IsFinite(latitude) || latitude is < -90 or > 90) ||
            input.Longitude is { } longitude && (!double.IsFinite(longitude) || longitude is < -180 or > 180))
            return "Enter both latitude (-90 to 90) and longitude (-180 to 180), or leave both empty.";
        return null;
    }

    public static bool IsExternalUrl(string? url) => string.IsNullOrWhiteSpace(url) ||
        (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
         string.IsNullOrEmpty(uri.UserInfo) && !url.Contains('\\') && !url.Any(char.IsControl));

    public static string? PublicationError(Post post) => post.Type switch
    {
        PostType.Article when string.IsNullOrWhiteSpace(post.Draft!.Title) || string.IsNullOrWhiteSpace(post.Draft.BodyMarkdown)
            => "An article needs a title and body before publication.",
        PostType.Microblog when string.IsNullOrWhiteSpace(post.Draft!.BodyMarkdown) && !ArticleService.DraftMedia(post).Any()
            => "A note needs text or at least one photo before publication.",
        PostType.Review when string.IsNullOrWhiteSpace(post.Review?.Subject) || post.Review.Rating is not (>= 1 and <= 5) || string.IsNullOrWhiteSpace(post.Draft!.BodyMarkdown)
            => "A review needs a subject, an integer rating from 1 to 5, and review text before publication.",
        PostType.Review when !IsExternalUrl(post.Review?.ExternalUrl) => "Use an absolute HTTP or HTTPS URL without credentials for the reviewed subject.",
        PostType.Guide when string.IsNullOrWhiteSpace(post.Draft!.Title) || post.Steps.Count == 0 || post.Steps.Any(x => string.IsNullOrWhiteSpace(x.BodyMarkdown))
            => "A guide needs a title and at least one step, with instructions in every step, before publication.",
        PostType.Gallery when !ArticleService.DraftMedia(post).Any() => "A gallery needs at least one photo before publication.",
        PostType.Checkin when string.IsNullOrWhiteSpace(post.Draft!.LocationName) => "A check-in needs a place name before publication.",
        _ => null
    };
}
