#nullable enable
// MacNotifier.csx
// Optional (config-gated) side effect: shows a macOS Notification Center alert when a meeting
// has finished processing. Uses the built-in `osascript` (AppleScript "display notification")
// so no external dependency (e.g. terminal-notifier/Homebrew) is required, consistent with the
// rest of the project having no CLI dependencies beyond the `copilot` executable itself.
// Never throws on failure — a notification failing must never affect the (already persisted)
// processing/state of a meeting.

#load "Logging.csx"

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

public sealed class MacNotifier
{
    private readonly Logger _logger;
    private readonly string _sound;

    /// <param name="sound">Optional macOS notification sound name (e.g. "Glass", "Ping").
    /// Empty/whitespace means no sound.</param>
    public MacNotifier(Logger logger, string sound = "")
    {
        _logger = logger;
        _sound = sound;
    }

    /// <summary>
    /// Escapes a string for safe embedding inside a double-quoted AppleScript string literal:
    /// backslashes must be escaped first, then double quotes.
    /// </summary>
    public static string EscapeForAppleScript(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
    }

    /// <summary>
    /// Builds the full `display notification` AppleScript source that would be passed to
    /// `osascript -e`. Kept as a pure/static function so it can be unit tested without actually
    /// invoking osascript.
    /// </summary>
    public static string BuildAppleScript(string title, string subtitle, string message, string sound = "")
    {
        var sb = new StringBuilder();
        sb.Append("display notification \"").Append(EscapeForAppleScript(message)).Append('"');

        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append(" with title \"").Append(EscapeForAppleScript(title)).Append('"');
        }

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            sb.Append(" subtitle \"").Append(EscapeForAppleScript(subtitle)).Append('"');
        }

        if (!string.IsNullOrWhiteSpace(sound))
        {
            sb.Append(" sound name \"").Append(EscapeForAppleScript(sound)).Append('"');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Path to the compiled Pensieve.app's executable (built by install.sh via `osacompile`
    /// from resources/PensieveNotifier.applescript), if present alongside the working
    /// directory. When available, alerts are routed through it so macOS attributes the
    /// notification's sender to "Pensieve" instead of osascript's generic "Script Editor"
    /// identity.
    /// </summary>
    private static string? FindPensieveAppExecutable()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "Pensieve.app", "Contents", "MacOS", "applet");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Shows a macOS Notification Center alert. No-ops (with a warning log) when not
    /// running on macOS. Never throws — failures are logged and swallowed.</summary>
    public void Notify(string title, string subtitle, string message)
    {
        if (!OperatingSystem.IsMacOS())
        {
            _logger.Warn("MacNotifier: skipped (not running on macOS).");
            return;
        }

        try
        {
            var pensieveAppPath = FindPensieveAppExecutable();

            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (pensieveAppPath != null)
            {
                // Pensieve.app is a compiled AppleScript applet: run it with
                // <title> <subtitle> <message> <sound> as its `argv`.
                psi.FileName = pensieveAppPath;
                psi.ArgumentList.Add(title ?? "");
                psi.ArgumentList.Add(subtitle ?? "");
                psi.ArgumentList.Add(message ?? "");
                psi.ArgumentList.Add(_sound ?? "");
            }
            else
            {
                var script = BuildAppleScript(title, subtitle, message, _sound);
                psi.FileName = "osascript";
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);
            }

            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger.Warn("MacNotifier: failed to start 'osascript' process.");
                return;
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var stderr = process.StandardError.ReadToEnd();
                _logger.Warn($"MacNotifier: 'osascript' exited with code {process.ExitCode}. stderr: {stderr}");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"MacNotifier: failed to show notification: {ex.Message}");
        }
    }
}
