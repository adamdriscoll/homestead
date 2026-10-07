using Homestead.Data.Entities;

namespace Homestead.Features.Articles;

public static class PostValidation
{
    public static bool IsExternalUrl(string? url) => string.IsNullOrWhiteSpace(url) ||
        (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
         string.IsNullOrEmpty(uri.UserInfo) && !url.Contains('\\') && !url.Any(char.IsControl));

    public static string? PublicationError(Post post) => post.Type switch
    {
        PostType.Article when string.IsNullOrWhiteSpace(post.Draft!.Title) || string.IsNullOrWhiteSpace(post.Draft.BodyMarkdown)
            => "An article needs a title and body before publication.",
        PostType.Microblog when string.IsNullOrWhiteSpace(post.Draft!.BodyMarkdown) && post.Media.Count == 0
            => "A note needs text or at least one photo before publication.",
        PostType.Review when string.IsNullOrWhiteSpace(post.Review?.Subject) || post.Review.Rating is not (>= 1 and <= 5) || string.IsNullOrWhiteSpace(post.Draft!.BodyMarkdown)
            => "A review needs a subject, an integer rating from 1 to 5, and review text before publication.",
        PostType.Review when !IsExternalUrl(post.Review?.ExternalUrl) => "Use an absolute HTTP or HTTPS URL without credentials for the reviewed subject.",
        PostType.Guide when string.IsNullOrWhiteSpace(post.Draft!.Title) || post.Steps.Count == 0 || post.Steps.Any(x => string.IsNullOrWhiteSpace(x.BodyMarkdown))
            => "A guide needs a title and at least one step, with instructions in every step, before publication.",
        PostType.Gallery when post.Media.Count == 0 => "A gallery needs at least one photo before publication.",
        _ => null
    };
}
