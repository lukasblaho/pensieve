#nullable enable
// test-DateTimeHelper.csx
// Verifies UTC -> Europe/Bratislava conversion, including correct handling of the CET/CEST
// daylight-saving boundary.

#load "TestKit.csx"
#load "../src/DateTimeHelper.csx"

using System;

TestKit.Section("DateTimeHelper: converts a UTC unix-ms timestamp to Bratislava local time (summer/CEST, +2)");
{
    // 2026-08-12T15:11:53Z -> Bratislava is CEST (UTC+2) in August.
    var unixMs = new DateTimeOffset(2026, 8, 12, 15, 11, 53, TimeSpan.Zero).ToUnixTimeMilliseconds();
    var local = DateTimeHelper.ToBratislava(unixMs);

    TestKit.Assert(local.Hour == 17 && local.Minute == 11, "August (CEST, UTC+2) should shift 15:11 UTC to 17:11 local");
    TestKit.Assert(local.Offset == TimeSpan.FromHours(2), "offset should be +2 during CEST");
}

TestKit.Section("DateTimeHelper: converts a UTC unix-ms timestamp to Bratislava local time (winter/CET, +1)");
{
    // 2026-01-12T15:11:53Z -> Bratislava is CET (UTC+1) in January.
    var unixMs = new DateTimeOffset(2026, 1, 12, 15, 11, 53, TimeSpan.Zero).ToUnixTimeMilliseconds();
    var local = DateTimeHelper.ToBratislava(unixMs);

    TestKit.Assert(local.Hour == 16 && local.Minute == 11, "January (CET, UTC+1) should shift 15:11 UTC to 16:11 local");
    TestKit.Assert(local.Offset == TimeSpan.FromHours(1), "offset should be +1 during CET");
}

TestKit.Section("DateTimeHelper: ToBratislava(DateTimeOffset) is idempotent/stable when re-applied");
{
    var utc = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
    var localOnce = DateTimeHelper.ToBratislava(utc);
    var localTwice = DateTimeHelper.ToBratislava(localOnce);

    TestKit.Assert(localOnce.ToUnixTimeMilliseconds() == localTwice.ToUnixTimeMilliseconds(), "converting an already-local DateTimeOffset again should represent the same instant");
}
