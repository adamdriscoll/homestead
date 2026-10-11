using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Homestead.Features.Articles;

public static class GpxParser
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    public const int MaximumPoints = 50_000;
    private const string V10 = "http://www.topografix.com/GPX/1/0";
    private const string V11 = "http://www.topografix.com/GPX/1/1";
    private const string GarminV1 = "http://www.garmin.com/xmlschemas/TrackPointExtension/v1";
    private const string GarminV2 = "http://www.garmin.com/xmlschemas/TrackPointExtension/v2";

    public static FitnessSnapshot Parse(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new InvalidOperationException("Choose a GPX file up to 10 MB.");
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes };
            // Bound nesting before constructing a tree. Never resolve entities or remote schemas.
            using (var validation = XmlReader.Create(new MemoryStream(bytes, writable: false), settings))
                while (validation.Read())
                    if (validation.Depth > 32) throw new InvalidOperationException("The GPX file is too deeply nested.");
            using var reader = XmlReader.Create(new MemoryStream(bytes, writable: false), settings);
            var root = XDocument.Load(reader).Root;
            if (root is null || root.Name.LocalName != "gpx" || root.Name.NamespaceName is not (V10 or V11) ||
                (string?)root.Attribute("version") != (root.Name.NamespaceName == V10 ? "1.0" : "1.1"))
                throw new InvalidOperationException("Choose a GPX 1.0 or 1.1 file containing recorded tracks.");
            var ns = root.Name.Namespace;
            var segments = new List<FitnessSegment>();
            var count = 0;
            double distance = 0, duration = 0, gain = 0;
            var completeTime = true; var hasElevation = false;
            foreach (var segment in root.Elements(ns + "trk").SelectMany(track => track.Elements(ns + "trkseg")))
            {
                if (!segment.Elements(ns + "trkpt").Any()) continue;
                if (segments.Count >= 100) throw new InvalidOperationException("Use a GPX track with at most 100 segments.");
                var points = new List<FitnessPoint>();
                foreach (var element in segment.Elements(ns + "trkpt"))
                {
                    if (++count > MaximumPoints) throw new InvalidOperationException("Use a GPX track with at most 50,000 points.");
                    var lat = Number((string?)element.Attribute("lat")) ?? throw new InvalidOperationException("Every track point needs valid coordinates.");
                    var lon = Number((string?)element.Attribute("lon")) ?? throw new InvalidOperationException("Every track point needs valid coordinates.");
                    if (lat is < -90 or > 90 || lon is < -180 or > 180) throw new InvalidOperationException("GPX coordinates are outside their valid range.");
                    var elevation = Number((string?)element.Element(ns + "ele"));
                    if (elevation is < -12000 or > 100000) throw new InvalidOperationException("GPX elevation must be between -12,000 and 100,000 meters.");
                    DateTime? time = null;
                    if (element.Element(ns + "time") is { } timestamp)
                    {
                        // GPX dateTimes must carry an offset; never interpret a device time in the server's zone.
                        var text = timestamp.Value;
                        if (!(text.EndsWith('Z') || text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':') ||
                            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                            throw new InvalidOperationException("GPX timestamps need a valid date, time, and UTC offset.");
                        time = parsed.UtcDateTime;
                    }
                    var extensions = element.Element(ns + "extensions");
                    XElement? Sensor(string name) => extensions?.Descendants().FirstOrDefault(x =>
                        x.Name.LocalName == name && x.Name.NamespaceName is GarminV1 or GarminV2);
                    var hrValue = Number(Sensor("hr")?.Value);
                    if (hrValue is { } hr && (hr < 1 || hr > 300 || hr != Math.Truncate(hr)))
                        throw new InvalidOperationException("GPX heart rate must be an integer between 1 and 300 bpm.");
                    var speed = Number(Sensor("speed")?.Value ?? (ns.NamespaceName == V10 ? (string?)element.Element(ns + "speed") : null));
                    if (speed is < 0 or > 1000) throw new InvalidOperationException("GPX speed must be between 0 and 1,000 meters per second.");
                    if (time is null) completeTime = false;
                    if (points.LastOrDefault() is { } previous)
                    {
                        var meters = Distance(previous.Latitude, previous.Longitude, lat, lon);
                        distance += meters;
                        if (previous.TimeUtc is { } before && time is { } after)
                        {
                            var seconds = (after - before).TotalSeconds;
                            if (seconds <= 0) throw new InvalidOperationException("Track timestamps must increase within each segment.");
                            duration += seconds;
                            speed ??= meters / seconds;
                        }
                        if (previous.ElevationMeters is { } beforeElevation && elevation is { } afterElevation)
                        { hasElevation = true; gain += Math.Max(0, afterElevation - beforeElevation); }
                    }
                    points.Add(new(lat, lon, elevation, time, (int?)hrValue, speed));
                }
                if (points.Count > 0) segments.Add(new(points.ToImmutableArray()));
            }
            if (segments.Count == 0 || !segments.Any(x => x.Points.Length >= 2))
                throw new InvalidOperationException("The GPX file needs a recorded track with at least two points. Routes and waypoints alone are not activities.");
            var allPoints = segments.SelectMany(x => x.Points).ToArray();
            var heartRates = allPoints.Where(x => x.HeartRate is not null).Select(x => x.HeartRate!.Value).ToArray();
            return new(segments.ToImmutableArray(), distance, completeTime ? duration : null, hasElevation ? gain : null,
                allPoints.Where(x => x.TimeUtc is not null).Select(x => x.TimeUtc).Min(),
                heartRates.Length == 0 ? null : heartRates.Average(), heartRates.Length == 0 ? null : heartRates.Max());
        }
        catch (XmlException ex) { throw new InvalidOperationException("The GPX file is invalid XML. Export a fresh GPX file and try again.", ex); }
    }

    private static double? Number(string? text)
    {
        if (text is null) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new InvalidOperationException("GPX measurements must be finite numbers.");
        return value;
    }

    private static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        const double radians = Math.PI / 180;
        var a = Math.Pow(Math.Sin((lat2 - lat1) * radians / 2), 2) + Math.Cos(lat1 * radians) * Math.Cos(lat2 * radians) *
            Math.Pow(Math.Sin((lon2 - lon1) * radians / 2), 2);
        return 6_371_008.8 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}
