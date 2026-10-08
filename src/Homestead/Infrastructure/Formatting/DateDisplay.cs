using System.Globalization;
using Humanizer;

namespace Homestead.Infrastructure.Formatting;

public static class DateDisplay
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public static string Relative(DateTime utc) => utc.Humanize(utcDate: true, culture: Culture);

    public static string Timestamp(DateTime utc, string timeZone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        return $"{local.ToString("MMMM d, yyyy 'at' h:mm:ss tt", Culture)} ({timeZone})";
    }
}
