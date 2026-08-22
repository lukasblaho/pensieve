#nullable enable
// SafeFileName.csx
// Slugifies meeting titles for use in filenames.

using System;
using System.Linq;
using System.Text;

public static class SafeFileName
{
    public static string Slugify(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "untitled-meeting";
        }

        var normalized = title.Trim().ToLowerInvariant();
        var sb = new StringBuilder();
        var lastWasDash = false;

        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        var result = sb.ToString().Trim('-');
        if (result.Length == 0)
        {
            return "untitled-meeting";
        }

        // Keep filenames reasonably short.
        return result.Length > 80 ? result.Substring(0, 80).Trim('-') : result;
    }
}
