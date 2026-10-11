using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Homestead.Data.Entities;

namespace Homestead.Features.Articles;

public sealed record FitnessPoint(double Latitude, double Longitude, double? ElevationMeters,
    DateTime? TimeUtc, int? HeartRate, double? SpeedMetersPerSecond);
public sealed record FitnessSegment(ImmutableArray<FitnessPoint> Points);
public sealed record FitnessSnapshot(ImmutableArray<FitnessSegment> Segments, double DistanceMeters, double? DurationSeconds,
    double? ElevationGainMeters, DateTime? StartedAtUtc, double? AverageHeartRate, int? MaximumHeartRate)
{
    [JsonIgnore] public double? AverageSpeedMetersPerSecond => DurationSeconds is > 0 ? DistanceMeters / DurationSeconds : null;
    [JsonIgnore] public string DistanceText => FormatDistance(FitnessUnitSystem.Metric);
    [JsonIgnore]
    public string? DurationText => DurationSeconds is { } seconds ?
        $"{(int)(seconds / 3600)}:{(int)(seconds / 60 % 60):00}:{(int)(seconds % 60):00}" : null;
    [JsonIgnore]
    public string? PaceText => FormatPace(FitnessUnitSystem.Metric);

    public string FormatDistance(FitnessUnitSystem units) =>
        (DistanceMeters / DistanceUnitMeters(units)).ToString("0.00", CultureInfo.InvariantCulture) + " " + DistanceUnit(units);

    public string? FormatPace(FitnessUnitSystem units) => AverageSpeedMetersPerSecond is > 0 and var speed && DistanceUnitMeters(units) / speed <= 31536000 ?
        $"{(int)(DistanceUnitMeters(units) / speed / 60)}:{(int)(DistanceUnitMeters(units) / speed) % 60:00} /{DistanceUnit(units)}" : null;

    public static double DistanceUnitMeters(FitnessUnitSystem units) => units == FitnessUnitSystem.Imperial ? 1609.344 : 1000;
    public static string DistanceUnit(FitnessUnitSystem units) => units == FitnessUnitSystem.Imperial ? "mi" : "km";
}
