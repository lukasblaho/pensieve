#nullable enable
// Config.csx
// Loads configuration entirely from a .env file (hand-rolled parser, no NuGet dependency)
// and validates required values. All new opt-in features (Fireflies API source, auto-delete,
// Obsidian export, Notion export) default to OFF/disabled.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public sealed class AgentConfig
{
    // --- Sources ---
    public bool EnableFolderWatch { get; init; } = true;
    public string WatchFolder { get; init; } = "";

    // Optional: path to Fireflies' own native "Summaries" export folder, sibling to
    // WATCH_FOLDER's "Transcripts". When set, used purely to reliably resolve each transcript's
    // Fireflies ID/title/date via filename-matched pairing — never as extra analysis input.
    public string SummaryFolder { get; init; } = "";

    public bool EnableFirefliesApiSource { get; init; } = false;
    public string FirefliesApiKey { get; init; } = "";
    public int PollIntervalMinutes { get; init; } = 15;

    // --- Output ---
    public string OutputDir { get; init; } = "";

    // --- Copilot CLI ---
    public string CopilotModel { get; init; } = "auto";
    public string CopilotExecutable { get; init; } = "copilot";

    // --- Fireflies deletion (destructive, opt-in) ---
    public bool FirefliesAutoDeleteAfterProcessing { get; init; } = false;

    // --- Exports (opt-in) ---
    public bool EnableObsidianExport { get; init; } = false;
    public string ObsidianVaultPath { get; init; } = "";
    public string ObsidianSubfolder { get; init; } = "Meetings";

    public bool EnableNotionExport { get; init; } = false;
    public string NotionApiToken { get; init; } = "";
    public string NotionDatabaseId { get; init; } = "";

    // --- macOS Notification Center alert (opt-in) ---
    public bool EnableMacOsNotifications { get; init; } = false;
    public string MacOsNotificationSound { get; init; } = "";

    public string DataDir { get; init; } = "data";
    public string StateFilePath => Path.Combine(DataDir, "state.json");
    public string VocabularyFilePath => Path.Combine(DataDir, "vocabulary.json");
    public string LogsDir => Path.Combine(DataDir, "logs");
}

public static class ConfigLoader
{
    /// <summary>
    /// Parses a .env file (KEY=VALUE per line, '#' comments, blank lines ignored,
    /// optional surrounding quotes stripped) without external dependencies.
    /// </summary>
    public static Dictionary<string, string> ParseEnvFile(string path)
    {
        var result = new Dictionary<string, string>();
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
            {
                continue;
            }

            var idx = line.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var key = line.Substring(0, idx).Trim();
            var value = line.Substring(idx + 1).Trim();

            if (value.Length >= 2 &&
                ((value.StartsWith("\"") && value.EndsWith("\"")) ||
                 (value.StartsWith("'") && value.EndsWith("'"))))
            {
                value = value.Substring(1, value.Length - 2);
            }

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Loads config, preferring OS environment variables over .env file values,
    /// so real environment variables can override .env in CI/production if needed.
    /// </summary>
    public static AgentConfig Load(string envFilePath = ".env", string dataDir = "data")
    {
        var fileValues = ParseEnvFile(envFilePath);

        string Get(string key, string fallback = "")
        {
            var fromEnv = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(fromEnv))
            {
                return fromEnv;
            }
            return fileValues.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;
        }

        bool GetBool(string key, bool fallback)
        {
            var raw = Get(key, fallback ? "true" : "false");
            return raw.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
        }

        var enableFolderWatch = GetBool("ENABLE_FOLDER_WATCH", true);
        var watchFolder = Get("WATCH_FOLDER");
        var summaryFolder = Get("SUMMARY_FOLDER");
        var enableFirefliesApiSource = GetBool("ENABLE_FIREFLIES_API_SOURCE", false);
        var firefliesKey = Get("FIREFLIES_API_KEY");
        var outputDir = Get("OUTPUT_DIR", "./notes");
        var pollIntervalRaw = Get("POLL_INTERVAL_MINUTES", "15");
        var copilotModel = Get("COPILOT_MODEL", "auto");
        var copilotExecutable = Get("COPILOT_EXECUTABLE", "copilot");
        var firefliesAutoDelete = GetBool("FIREFLIES_AUTO_DELETE_AFTER_PROCESSING", false);
        var enableObsidianExport = GetBool("ENABLE_OBSIDIAN_EXPORT", false);
        var obsidianVaultPath = Get("OBSIDIAN_VAULT_PATH");
        var obsidianSubfolder = Get("OBSIDIAN_SUBFOLDER", "Meetings");
        var enableNotionExport = GetBool("ENABLE_NOTION_EXPORT", false);
        var notionApiToken = Get("NOTION_API_TOKEN");
        var notionDatabaseId = Get("NOTION_DATABASE_ID");
        var enableMacOsNotifications = GetBool("ENABLE_MACOS_NOTIFICATIONS", false);
        var macOsNotificationSound = Get("MACOS_NOTIFICATION_SOUND");

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(outputDir)) missing.Add("OUTPUT_DIR");
        if (!enableFolderWatch && !enableFirefliesApiSource)
        {
            missing.Add("ENABLE_FOLDER_WATCH or ENABLE_FIREFLIES_API_SOURCE (at least one source must be enabled)");
        }
        if (enableFolderWatch && string.IsNullOrWhiteSpace(watchFolder)) missing.Add("WATCH_FOLDER");
        // Fireflies API key is required if the API is used either as a transcript source or for
        // ID resolution/deletion.
        var firefliesApiNeeded = enableFirefliesApiSource || firefliesAutoDelete;
        if (firefliesApiNeeded && string.IsNullOrWhiteSpace(firefliesKey)) missing.Add("FIREFLIES_API_KEY");
        if (enableObsidianExport && string.IsNullOrWhiteSpace(obsidianVaultPath)) missing.Add("OBSIDIAN_VAULT_PATH");
        if (enableNotionExport && string.IsNullOrWhiteSpace(notionApiToken)) missing.Add("NOTION_API_TOKEN");
        if (enableNotionExport && string.IsNullOrWhiteSpace(notionDatabaseId)) missing.Add("NOTION_DATABASE_ID");

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Missing required configuration value(s): {string.Join(", ", missing)}. " +
                $"Set them in '{envFilePath}' (see .env.example) or as environment variables.");
        }

        if (!int.TryParse(pollIntervalRaw, out var pollInterval) || pollInterval <= 0)
        {
            throw new InvalidOperationException(
                $"POLL_INTERVAL_MINUTES must be a positive integer, got: '{pollIntervalRaw}'.");
        }

        return new AgentConfig
        {
            EnableFolderWatch = enableFolderWatch,
            WatchFolder = watchFolder,
            SummaryFolder = summaryFolder,
            EnableFirefliesApiSource = enableFirefliesApiSource,
            FirefliesApiKey = firefliesKey,
            OutputDir = outputDir,
            PollIntervalMinutes = pollInterval,
            CopilotModel = copilotModel,
            CopilotExecutable = copilotExecutable,
            FirefliesAutoDeleteAfterProcessing = firefliesAutoDelete,
            EnableObsidianExport = enableObsidianExport,
            ObsidianVaultPath = obsidianVaultPath,
            ObsidianSubfolder = string.IsNullOrWhiteSpace(obsidianSubfolder) ? "Meetings" : obsidianSubfolder,
            EnableNotionExport = enableNotionExport,
            NotionApiToken = notionApiToken,
            NotionDatabaseId = notionDatabaseId,
            EnableMacOsNotifications = enableMacOsNotifications,
            MacOsNotificationSound = macOsNotificationSound,
            DataDir = dataDir,
        };
    }
}
