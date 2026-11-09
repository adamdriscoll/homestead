namespace Homestead.Data.Entities;

public sealed class PageViews
{
    public string Path { get; set; } = "";
    public long Hits { get; set; }
}
