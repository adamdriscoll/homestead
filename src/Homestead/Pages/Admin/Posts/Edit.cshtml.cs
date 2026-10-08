using Homestead.Data;
using Homestead.Features.Articles;
using Homestead.Features.Media;
using Homestead.Features.Publishing;

namespace Homestead.Pages.Admin.Posts;

public sealed class EditModel(ArticleService articles, MediaService media, PublishingService publishing, ActiveSite active, HomesteadDbContext db)
    : PostEditorModel(articles, media, publishing, active, db);
