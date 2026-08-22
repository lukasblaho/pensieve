#nullable enable
// Version.csx
// Simple, manually-bumped application version, recorded into each generated meeting's
// metadata.json so output can be traced back to the app version that produced it. Not derived
// from git (this is not a version-controlled repo).

public static class AppVersion
{
    public const string Current = "1.2.0";
}
