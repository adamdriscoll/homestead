namespace Homestead.Features.Publishing;

public static class LocationMapAssets
{
    private static readonly string[] AssetFiles = ["location-map.js", "fitness-map.js", "leaflet/leaflet.js", "leaflet/leaflet.css", "leaflet/LICENSE.txt",
        "leaflet/images/marker-icon.png", "leaflet/images/marker-icon-2x.png", "leaflet/images/marker-shadow.png",
        "leaflet/images/layers.png", "leaflet/images/layers-2x.png"];

    public static IEnumerable<(string RelativePath, string SourcePath)> Files(string contentRoot)
    {
        var root = Path.Combine(contentRoot, "Maps");
        return AssetFiles.Select(file => ("assets/map/" + file, Path.Combine(root, file)));
    }
}
