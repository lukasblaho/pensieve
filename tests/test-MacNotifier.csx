#nullable enable
// test-MacNotifier.csx
// Verifies the AppleScript command-building/escaping logic used to show macOS Notification
// Center alerts (without invoking the real `osascript` binary).

#load "TestKit.csx"
#load "../src/MacNotifier.csx"

using System;

TestKit.Section("MacNotifier: escapes double quotes and backslashes for AppleScript string literals");
{
    var escaped = MacNotifier.EscapeForAppleScript("She said \"hi\" and used a \\backslash\\");
    TestKit.Assert(escaped == "She said \\\"hi\\\" and used a \\\\backslash\\\\", "quotes and backslashes should both be escaped, backslashes first");
}

TestKit.Section("MacNotifier: EscapeForAppleScript handles empty/null-like input");
{
    TestKit.Assert(MacNotifier.EscapeForAppleScript("") == "", "empty string should remain empty");
    TestKit.Assert(MacNotifier.EscapeForAppleScript(null!) == "", "null should be treated as empty");
}

TestKit.Section("MacNotifier: BuildAppleScript includes title, subtitle, message, and sound when provided");
{
    var script = MacNotifier.BuildAppleScript("Weekly Sync", "Pensieve", "Meeting processed successfully.", "Glass");

    TestKit.Assert(script.StartsWith("display notification \"Meeting processed successfully.\""), "message should come first, as the primary display notification argument");
    TestKit.Assert(script.Contains("with title \"Weekly Sync\""), "should include the title clause");
    TestKit.Assert(script.Contains("subtitle \"Pensieve\""), "should include the subtitle clause");
    TestKit.Assert(script.Contains("sound name \"Glass\""), "should include the sound clause when a sound is provided");
}

TestKit.Section("MacNotifier: BuildAppleScript omits subtitle/sound clauses when not provided");
{
    var script = MacNotifier.BuildAppleScript("Weekly Sync", "", "Meeting processed successfully.", "");

    TestKit.Assert(script.Contains("with title \"Weekly Sync\""), "should still include the title clause");
    TestKit.Assert(!script.Contains("subtitle"), "should omit the subtitle clause when empty");
    TestKit.Assert(!script.Contains("sound name"), "should omit the sound clause when empty");
}

TestKit.Section("MacNotifier: BuildAppleScript escapes special characters embedded in title/message");
{
    var script = MacNotifier.BuildAppleScript("Q&A \"Roadmap\"", "", "Discussed \\ next steps", "");

    TestKit.Assert(script.Contains("with title \"Q&A \\\"Roadmap\\\"\""), "title quotes should be escaped inside the generated script");
    TestKit.Assert(script.Contains("display notification \"Discussed \\\\ next steps\""), "message backslash should be escaped inside the generated script");
}
