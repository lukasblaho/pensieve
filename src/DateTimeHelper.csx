#nullable enable
// DateTimeHelper.csx
// Converts UTC instants to the Europe/Bratislava local timezone consistently across the app
// (folder naming, note.md date display), correctly handling CET/CEST daylight-saving
// transitions.

using System;

public static class DateTimeHelper
{
    private static readonly Lazy<TimeZoneInfo> BratislavaTimeZoneLazy = new Lazy<TimeZoneInfo>(ResolveBratislavaTimeZone);

    public static TimeZoneInfo BratislavaTimeZone => BratislavaTimeZoneLazy.Value;

    private static TimeZoneInfo ResolveBratislavaTimeZone()
    {
        // IANA id works on Linux/macOS; Windows uses "Central Europe Standard Time".
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava"); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        try { return TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time"); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        // Last resort: fixed CET offset without DST (should not normally be reached).
        return TimeZoneInfo.CreateCustomTimeZone("CET-fallback", TimeSpan.FromHours(1), "CET (fallback)", "CET (fallback)");
    }

    /// <summary>Converts any DateTimeOffset (in any offset) to Bratislava local time.</summary>
    public static DateTimeOffset ToBratislava(DateTimeOffset value)
    {
        return TimeZoneInfo.ConvertTime(value, BratislavaTimeZone);
    }

    /// <summary>Converts a Unix-millisecond timestamp (assumed UTC) to Bratislava local time.</summary>
    public static DateTimeOffset ToBratislava(long unixMilliseconds)
    {
        return ToBratislava(DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds));
    }
}
