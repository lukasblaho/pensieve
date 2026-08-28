#!/usr/bin/env dotnet-script
#nullable enable
// main.csx
// pensieve CLI entry point.
//
// Usage:
//   dotnet script main.csx -- sync    # single fetch/process pass over all enabled sources, then exit
//   dotnet script main.csx -- watch   # live-watch WATCH_FOLDER for new .md transcripts (blocking)
//   dotnet script main.csx -- run     # loop: sync, sleep POLL_INTERVAL_MINUTES, repeat
//
// All configuration comes from .env (see .env.example).

#load "src/Config.csx"
#load "src/Logging.csx"
#load "src/FolderWatcher.csx"
#load "src/FirefliesClient.csx"
#load "src/CopilotCliClient.csx"
#load "src/StateStore.csx"
#load "src/GlobalVocabularyStore.csx"
#load "src/MeetingIndexStore.csx"
#load "src/MeetingLinker.csx"
#load "src/SeriesKeyGenerator.csx"
#load "src/ObsidianExporter.csx"
#load "src/NotionExporter.csx"
#load "src/MacNotifier.csx"
#load "src/Orchestrator.csx"

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

var command = Args.Count > 0 ? Args[0].ToLowerInvariant() : "";

if (command != "sync" && command != "run" && command != "watch")
{
    Console.WriteLine("Usage: dotnet script main.csx -- <sync|watch|run>");
    Console.WriteLine("  sync   Run a single pass over all enabled sources and exit.");
    Console.WriteLine("  watch  Live-watch WATCH_FOLDER for new .md transcripts (blocking, Ctrl+C to stop).");
    Console.WriteLine("  run    Run continuously: sync, sleep POLL_INTERVAL_MINUTES, repeat.");
    Environment.Exit(1);
    return;
}

AgentConfig config;
try
{
    config = ConfigLoader.Load();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    Environment.Exit(1);
    return;
}

var logger = new Logger(config.LogsDir);
var httpClient = new HttpClient();

FolderWatcher? folderWatcher = config.EnableFolderWatch ? new FolderWatcher(config.WatchFolder, logger) : null;
FirefliesClient? firefliesClient = (config.EnableFirefliesApiSource || config.FirefliesAutoDeleteAfterProcessing)
    ? new FirefliesClient(httpClient, config.FirefliesApiKey, logger)
    : null;
var copilotClient = new CopilotCliClient(logger, config.CopilotModel, config.CopilotExecutable);
var stateStore = new StateStore(config.StateFilePath);
var vocabularyStore = new GlobalVocabularyStore(config.VocabularyFilePath);
var meetingIndexStore = config.EnableMeetingLinking ? new MeetingIndexStore(config.MeetingsIndexFilePath) : null;
ObsidianExporter? obsidianExporter = config.EnableObsidianExport
    ? new ObsidianExporter(config.ObsidianVaultPath, config.ObsidianSubfolder, logger)
    : null;
NotionExporter? notionExporter = config.EnableNotionExport
    ? new NotionExporter(httpClient, config.NotionApiToken, config.NotionDatabaseId, logger)
    : null;
MacNotifier? macNotifier = config.EnableMacOsNotifications
    ? new MacNotifier(logger, config.MacOsNotificationSound)
    : null;

var orchestrator = new Orchestrator(
    config, folderWatcher, firefliesClient, copilotClient, stateStore, vocabularyStore,
    meetingIndexStore, obsidianExporter, notionExporter, macNotifier, logger);

if (command == "sync")
{
    logger.Info("Starting single sync pass.");
    try
    {
        await orchestrator.RunSyncAsync();
    }
    catch (Exception ex)
    {
        logger.Error("Sync pass failed with an unhandled error.", ex);
        Environment.Exit(1);
    }
    return;
}

if (command == "watch")
{
    if (!config.EnableFolderWatch)
    {
        Console.Error.WriteLine("ENABLE_FOLDER_WATCH is false; nothing to watch. Enable it in .env to use the 'watch' command.");
        Environment.Exit(1);
        return;
    }

    // Process anything already sitting in the folder first, then start live watching.
    logger.Info("Processing any pre-existing files in the watch folder before starting live watch.");
    await orchestrator.RunSyncAsync();

    var watchCts = new CancellationTokenSource();
    Console.CancelKeyPress += (sender, eventArgs) =>
    {
        logger.Info("Shutdown requested (Ctrl+C).");
        eventArgs.Cancel = true;
        watchCts.Cancel();
    };

    orchestrator.StartLiveFolderWatch();
    logger.Info($"Live-watching '{config.WatchFolder}' for new transcripts. Press Ctrl+C to stop.");

    try
    {
        await Task.Delay(Timeout.Infinite, watchCts.Token);
    }
    catch (TaskCanceledException)
    {
        // expected on Ctrl+C
    }

    logger.Info("Agent stopped.");
    return;
}

// command == "run": poll loop with graceful Ctrl+C shutdown.
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, eventArgs) =>
{
    logger.Info("Shutdown requested (Ctrl+C). Finishing current pass then exiting.");
    eventArgs.Cancel = true;
    cts.Cancel();
};

logger.Info($"Starting poll loop (every {config.PollIntervalMinutes} minute(s)). Press Ctrl+C to stop.");

while (!cts.IsCancellationRequested)
{
    try
    {
        await orchestrator.RunSyncAsync();
    }
    catch (Exception ex)
    {
        logger.Error("Sync pass failed with an unhandled error; will retry next interval.", ex);
    }

    try
    {
        await Task.Delay(TimeSpan.FromMinutes(config.PollIntervalMinutes), cts.Token);
    }
    catch (TaskCanceledException)
    {
        break;
    }
}

logger.Info("Agent stopped.");
