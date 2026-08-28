#nullable enable
// test-SeriesKeyGenerator.csx
// Verifies purely mechanical (regex-based) title normalization for recurring-series detection:
// dates/times/weekdays/month names/standalone numbers/punctuation are stripped so that only the
// date/time embedded in a recurring meeting's title differs between occurrences.

#load "TestKit.csx"
#load "../src/SeriesKeyGenerator.csx"

TestKit.Section("SeriesKeyGenerator: strips embedded dates so recurring titles normalize identically");
{
    var a = SeriesKeyGenerator.Generate("Daily Standup - Aug 28");
    var b = SeriesKeyGenerator.Generate("Daily Standup 8/29");
    var c = SeriesKeyGenerator.Generate("Daily Standup 2026-08-30");

    TestKit.Assert(a == "daily standup", $"month+day title should normalize to 'daily standup', was '{a}'");
    TestKit.Assert(b == "daily standup", $"slash-date title should normalize to 'daily standup', was '{b}'");
    TestKit.Assert(c == "daily standup", $"ISO-date title should normalize to 'daily standup', was '{c}'");
    TestKit.Assert(a == b && b == c, "all three date variants of the same recurring title should produce the same series key");
}

TestKit.Section("SeriesKeyGenerator: strips weekday names and parenthetical punctuation");
{
    var key = SeriesKeyGenerator.Generate("Weekly Sync (Monday)");
    TestKit.Assert(key == "weekly sync", $"weekday + parens should be stripped, was '{key}'");
}

TestKit.Section("SeriesKeyGenerator: strips time-of-day and standalone numbers");
{
    var key = SeriesKeyGenerator.Generate("Team Sync 2026-08-28 10:00");
    TestKit.Assert(key == "team sync", $"date + time should be stripped, was '{key}'");
}

TestKit.Section("SeriesKeyGenerator: is case-insensitive");
{
    var upper = SeriesKeyGenerator.Generate("DAILY STANDUP - AUG 28");
    TestKit.Assert(upper == "daily standup", $"should lowercase regardless of input case, was '{upper}'");
}

TestKit.Section("SeriesKeyGenerator: different meeting names still produce different keys");
{
    var standup = SeriesKeyGenerator.Generate("Daily Standup - Aug 28");
    var retro = SeriesKeyGenerator.Generate("Sprint Retro - Aug 28");
    TestKit.Assert(standup != retro, "unrelated meeting titles should not collapse to the same series key");
}

TestKit.Section("SeriesKeyGenerator: handles null/blank titles gracefully");
{
    TestKit.Assert(SeriesKeyGenerator.Generate(null) == "", "null title should return empty string, never guessed");
    TestKit.Assert(SeriesKeyGenerator.Generate("   ") == "", "whitespace-only title should return empty string");
}
