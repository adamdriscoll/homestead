using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Homestead.Data.Entities;
using Homestead.Features.Articles;

namespace Homestead.Features.Publishing;

public static class FitnessRendering
{
    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Summary(FitnessSnapshot activity, FitnessUnitSystem units = FitnessUnitSystem.Metric)
    {
        var items = new List<(string Label, string Value)> { ("Distance", activity.FormatDistance(units)) };
        if (activity.DurationText is { } duration) items.Add(("Recorded duration", duration));
        if (activity.FormatPace(units) is { } pace) items.Add(("Average pace", pace));
        if (activity.AverageSpeedMetersPerSecond is { } speed) items.Add(("Average speed", N(speed * 3600 / FitnessSnapshot.DistanceUnitMeters(units)) + " " + FitnessSnapshot.DistanceUnit(units) + "/h"));
        if (activity.ElevationGainMeters is { } gain) items.Add(("Elevation gain", units == FitnessUnitSystem.Imperial ? N(gain / .3048) + " ft" : N(gain) + " m"));
        if (activity.AverageHeartRate is { } heart) items.Add(("Average heart rate", N(heart) + " bpm"));
        if (activity.MaximumHeartRate is { } max) items.Add(("Maximum heart rate", max + " bpm"));
        return "<dl class=\"grid gap-3 sm:grid-cols-2\" data-fitness-summary>" + string.Concat(items.Select(x =>
            $"<div><dt>{x.Label}</dt><dd><strong>{WebUtility.HtmlEncode(x.Value)}</strong></dd></div>")) + "</dl>";
    }

    public static string Render(FitnessSnapshot activity, FitnessUnitSystem units = FitnessUnitSystem.Metric, bool privateMap = false)
    {
        var html = new StringBuilder("<section aria-label=\"Fitness activity\" class=\"space-y-6\" data-fitness-activity>");
        html.Append(Summary(activity, units));
        var timed = activity.Segments.SelectMany(s => s.Points).Any(p => p.TimeUtc is not null);
        // Plot each segment independently, including breaks where a sensor reading is absent.
        string Chart(string title, string unit, Func<FitnessPoint, double?> value)
        {
            var series = new List<List<(double X, double Y)>>();
            var sample = 0;
            foreach (var segment in activity.Segments)
            {
                List<(double X, double Y)> run = [];
                foreach (var point in segment.Points)
                {
                    sample++;
                    if (value(point) is not { } y || timed && point.TimeUtc is null)
                    { if (run.Count > 0) series.Add(run); run = []; continue; }
                    var x = timed ? (point.TimeUtc!.Value - activity.StartedAtUtc!.Value).TotalSeconds / 60 : sample;
                    run.Add((x, y));
                }
                if (run.Count > 0) series.Add(run);
            }
            if (series.Count == 0) return "";
            var all = series.SelectMany(x => x).ToArray();
            var minX = all.Min(x => x.X); var maxX = all.Max(x => x.X);
            var minY = all.Min(x => x.Y); var maxY = all.Max(x => x.Y);
            var displayed = Sample(series, 200).ToArray();
            var paths = displayed.Select(run => string.Join(" ", Sample(run, Math.Max(2, 1000 / displayed.Length)).Select(p =>
                $"{N(48 + (p.X - minX) / Math.Max(1, maxX - minX) * 576)},{N(192 - (p.Y - minY) / Math.Max(1, maxY - minY) * 160)}")));
            var axes = "<path d=\"M48 32V192H624\" fill=\"none\" stroke=\"currentColor\" stroke-opacity=\"0.5\" />" +
                $"<text x=\"44\" y=\"36\" text-anchor=\"end\" fill=\"currentColor\" font-size=\"12\">{N(maxY)}</text>" +
                $"<text x=\"44\" y=\"192\" text-anchor=\"end\" fill=\"currentColor\" font-size=\"12\">{N(minY)}</text>" +
                $"<text x=\"48\" y=\"212\" fill=\"currentColor\" font-size=\"12\">{N(minX)}</text>" +
                $"<text x=\"624\" y=\"212\" text-anchor=\"end\" fill=\"currentColor\" font-size=\"12\">{N(maxX)}</text>";
            return $"<figure class=\"block\"><figcaption>{title} ({unit})</figcaption>" + Svg($"{title} in {unit}, by " + (timed ? "minutes since start" : "recorded point order"), paths, axes) +
                $"<p>Range: {N(minY)}–{N(maxY)} {unit}. " + (timed ? $"Time: {N(minX)}–{N(maxX)} minutes since start." : "Horizontal axis: recorded point order (timestamps unavailable).") + "</p></figure>";
        }
        var speed = Chart("Speed", FitnessSnapshot.DistanceUnit(units) + "/h", p => p.SpeedMetersPerSecond * 3600 / FitnessSnapshot.DistanceUnitMeters(units));
        var heartRate = Chart("Heart rate", "bpm", p => p.HeartRate);
        html.Append("<div data-fitness-tabs class=\"tabs tabs-border\" aria-label=\"Activity views\" hidden>");
        foreach (var (name, label) in new[] { ("map", "Map"), ("speed", "Speed"), ("heart-rate", "Heart rate") })
        {
            if (name == "heart-rate" && heartRate.Length == 0) continue;
            var iconPath = name switch
            {
                "map" => "M9 18l-6 3V6l6-3 6 3 6-3v15l-6 3-6-3ZM9 3v15M15 6v15",
                "speed" => "M4.93 19.07a10 10 0 1 1 14.14 0M12 3v2M4.93 5.93l1.42 1.42M3 13h2M19 13h2M12 13l5-5",
                _ => "M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78L12 21l8.84-8.61a5.5 5.5 0 0 0 0-7.78ZM3 12h4l2-4 4 8 2-4h6"
            };
            html.Append($"<button type=\"button\" class=\"tab min-h-11 gap-2 px-2 sm:px-4\" data-fitness-tab=\"{name}\">");
            html.Append($"<svg viewBox=\"0 0 24 24\" class=\"size-4 shrink-0 fill-none stroke-current\" aria-hidden=\"true\" focusable=\"false\"><path d=\"{iconPath}\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" /></svg>{label}</button>");
        }
        html.Append("</div>");
        html.Append("<div data-fitness-panel=\"map\">").Append(Route(activity, privateMap)).Append("</div>");
        html.Append("<div data-fitness-panel=\"speed\">").Append(speed.Length > 0 ? speed : "<p>No recorded speed is available.</p>").Append("</div>");
        if (heartRate.Length > 0) html.Append("<div data-fitness-panel=\"heart-rate\">").Append(heartRate).Append("</div>");
        return html.Append("</section>").ToString();
    }

    public static string Route(FitnessSnapshot activity, bool privateMap = false)
    {
        // Only coordinates belong in the map payload. Retain every point for bounds and segment continuity.
        var coordinates = JsonSerializer.Serialize(activity.Segments.Select(s => s.Points.Select(p => new[] { p.Latitude, p.Longitude })));
        return "<figure class=\"block\" aria-label=\"Activity route\" data-fitness-route>" +
            "<div data-fitness-map-toolbar role=\"toolbar\" aria-label=\"Route map controls\" class=\"flex items-center justify-end border border-base-300 border-b-0 rounded-t-lg bg-base-200 p-1\" hidden>" +
            "<button type=\"button\" data-fitness-fit-route class=\"btn btn-ghost btn-square min-h-11 min-w-11\" aria-label=\"Fit route\" title=\"Fit route\">" +
            "<svg viewBox=\"0 0 24 24\" class=\"size-5 fill-none stroke-current\" aria-hidden=\"true\" focusable=\"false\"><path d=\"M8 3H3v5M16 3h5v5M3 16v5h5M21 16v5h-5\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" /></svg></button></div>" +
            $"<div data-fitness-map data-private-map=\"{privateMap.ToString().ToLowerInvariant()}\" data-route=\"{WebUtility.HtmlEncode(coordinates)}\" class=\"h-[28rem] w-full isolate rounded-b-lg border border-base-300\" role=\"region\" aria-label=\"Activity route map\" hidden></div>" +
            (privateMap ? "<p data-fitness-map-consent hidden><button type=\"button\" class=\"btn btn-outline min-h-11\" data-fitness-load-map>Show route map</button> Map tiles load from OpenStreetMap and disclose the viewed area.</p>" : "") +
            "<p data-fitness-map-status role=\"status\" hidden>Map tiles could not be loaded. The recorded route and start/finish markers are still available.</p>" +
            "<noscript><p>Enable JavaScript to view the route map.</p></noscript></figure>";
    }

    private static string Svg(string label, IEnumerable<string> paths, string extra = "") =>
        $"<svg viewBox=\"0 0 640 224\" class=\"w-full h-auto\" role=\"img\" aria-label=\"{WebUtility.HtmlEncode(label)}\">" +
        string.Concat(paths.Select(path => path.Contains(' ') ?
            $"<polyline points=\"{path}\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linejoin=\"round\" />" :
            $"<circle cx=\"{path.Split(',')[0]}\" cy=\"{path.Split(',')[1]}\" r=\"2\" fill=\"currentColor\" />")) + extra + "</svg>";

    private static IEnumerable<T> Sample<T>(IReadOnlyList<T> values, int maximum)
    {
        if (values.Count <= maximum) return values;
        return Enumerable.Range(0, maximum).Select(i => values[(int)((long)i * (values.Count - 1) / (maximum - 1))]);
    }
}
