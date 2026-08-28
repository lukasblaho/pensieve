#nullable enable
// Orchestrator.csx
// Wires: folder watch (+ optional Fireflies API source) -> analyze -> write meeting folder ->
// update global vocabulary -> mark processed -> optional delete from Fireflies -> optional
// Obsidian/Notion exports. Every step is independently idempotent via StateStore so a crash or
// transient failure at any point is safely retried on the next pass without reprocessing
// completed work or touching the original transcript source.

#load "Config.csx"
#load "Logging.csx"
#load "Models.csx"
#load "FolderWatcher.csx"
#load "TranscriptFileParser.csx"
#load "FirefliesClient.csx"
#load "CopilotCliClient.csx"
#load "StateStore.csx"
#load "MeetingFolderWriter.csx"
#load "GlobalVocabularyStore.csx"
#load "MeetingIndexStore.csx"
#load "MeetingLinker.csx"
#load "SeriesKeyGenerator.csx"
#load "ObsidianExporter.csx"
#load "NotionExporter.csx"
#load "MacNotifier.csx"

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

public sealed class Orchestrator
{
    private readonly AgentConfig _config;
    private readonly FolderWatcher? _folderWatcher;
    private readonly FirefliesClient? _firefliesClient;
    private readonly CopilotCliClient _copilotClient;
    private readonly StateStore _stateStore;
    private readonly GlobalVocabularyStore _vocabularyStore;
    private readonly MeetingIndexStore? _meetingIndexStore;
    private readonly ObsidianExporter? _obsidianExporter;
    private readonly NotionExporter? _notionExporter;
    private readonly MacNotifier? _macNotifier;
    private readonly Logger _logger;

    public Orchestrator(
        AgentConfig config,
        FolderWatcher? folderWatcher,
        FirefliesClient? firefliesClient,
        CopilotCliClient copilotClient,
        StateStore stateStore,
        GlobalVocabularyStore vocabularyStore,
        MeetingIndexStore? meetingIndexStore,
        ObsidianExporter? obsidianExporter,
        NotionExporter? notionExporter,
        MacNotifier? macNotifier,
        Logger logger)
    {
        _config = config;
        _folderWatcher = folderWatcher;
        _firefliesClient = firefliesClient;
        _copilotClient = copilotClient;
        _stateStore = stateStore;
        _vocabularyStore = vocabularyStore;
        _meetingIndexStore = meetingIndexStore;
        _obsidianExporter = obsidianExporter;
        _notionExporter = notionExporter;
        _macNotifier = macNotifier;
        _logger = logger;
    }

    /// <summary>
    /// Runs a single sync pass over both enabled sources and returns the number of meetings
    /// successfully processed.
    /// </summary>
    public async Task<int> RunSyncAsync()
    {
        var processedCount = 0;

        if (_config.EnableFolderWatch && _folderWatcher != null)
        {
            var files = _folderWatcher.ScanExisting();
            _logger.Info($"Found {files.Count} .md file(s) in watch folder.");

            foreach (var file in files)
            {
                if (await TryProcessFolderFileAsync(file).ConfigureAwait(false))
                {
                    processedCount++;
                }
            }
        }

        if (_config.EnableFirefliesApiSource && _firefliesClient != null)
        {
            processedCount += await RunFirefliesApiSyncAsync().ConfigureAwait(false);
        }

        _logger.Info($"Sync pass complete. Successfully processed {processedCount} meeting(s).");
        return processedCount;
    }

    /// <summary>Processes a single folder-dropped .md file immediately (used by the live watcher).</summary>
    public async Task<bool> TryProcessFolderFileAsync(string filePath)
    {
        Transcript transcript;
        try
        {
            transcript = TranscriptFileParser.Parse(filePath, string.IsNullOrWhiteSpace(_config.SummaryFolder) ? null : _config.SummaryFolder);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to parse transcript file '{filePath}'; skipping.", ex);
            return false;
        }

        var sourceKey = filePath;

        if (_stateStore.IsUpToDate(sourceKey, transcript.ContentHash))
        {
            _logger.Info($"Skipping '{filePath}': already processed and unchanged.");
            return false;
        }

        try
        {
            await ProcessTranscriptAsync(transcript, sourceKey).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to process folder transcript '{filePath}'; will retry next sync.", ex);
            return false;
        }
    }

    private async Task<int> RunFirefliesApiSyncAsync()
    {
        var lastDate = _stateStore.LastProcessedDate;
        DateTimeOffset? fromDate = lastDate.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)lastDate.Value)
            : null;

        _logger.Info(fromDate.HasValue
            ? $"Fetching Fireflies API transcripts since {fromDate:O}."
            : "No prior Fireflies API state; fetching all available transcripts (oldest first).");

        var transcripts = await _firefliesClient!.FetchTranscriptsSinceAsync(fromDate).ConfigureAwait(false);
        var ordered = transcripts.OrderBy(t => t.Date ?? 0).ToList();

        var processedCount = 0;
        foreach (var transcript in ordered)
        {
            var sourceKey = $"fireflies:{transcript.Id}";
            if (_stateStore.IsUpToDate(sourceKey, transcript.Id))
            {
                continue; // API transcripts are immutable once fetched; ID itself is the hash.
            }

            transcript.RawText = CopilotCliClient.BuildTranscriptText(transcript);

            try
            {
                await ProcessTranscriptAsync(transcript, sourceKey).ConfigureAwait(false);
                processedCount++;

                if (transcript.Date.HasValue && (!_stateStore.LastProcessedDate.HasValue || transcript.Date.Value > _stateStore.LastProcessedDate.Value))
                {
                    _stateStore.LastProcessedDate = transcript.Date.Value;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to process Fireflies API transcript '{transcript.Id}'; will retry next sync.", ex);
            }
        }

        return processedCount;
    }

    private async Task ProcessTranscriptAsync(Transcript transcript, string sourceKey)
    {
        _logger.Info($"Processing transcript '{transcript.Id}' ({transcript.Title ?? "untitled"})...");

        // 1. Analyze via Copilot CLI (strictly scoped to this transcript's content).
        var analysis = await _copilotClient.AnalyzeTranscriptAsync(transcript).ConfigureAwait(false);

        // 2. Resolve the Fireflies ID if not already known (needed for optional deletion).
        if (transcript.FirefliesId == null && _config.EnableFirefliesApiSource && _firefliesClient != null)
        {
            transcript.FirefliesId = await _firefliesClient.TryResolveTranscriptIdAsync(transcript.Title, transcript.GetDateTimeOffset()).ConfigureAwait(false);
            if (transcript.FirefliesId == null)
            {
                _logger.Warn($"Could not resolve a Fireflies ID for '{transcript.Title}'; deletion (if enabled) will be skipped for this meeting.");
            }
        }

        // 3. Compute related-meeting links (purely mechanical — no LLM cross-meeting knowledge),
        // only when ENABLE_MEETING_LINKING is on. Must happen before writing the folder so the
        // Related Meetings section can be rendered into note.md.
        var relatedEntries = new List<MeetingIndexEntry>();
        var seriesKey = "";
        if (_config.EnableMeetingLinking && _meetingIndexStore != null)
        {
            seriesKey = SeriesKeyGenerator.Generate(transcript.Title);
            var current = new MeetingIndexEntry
            {
                MeetingId = transcript.Id,
                Title = transcript.Title ?? "",
                DateEpochMs = transcript.Date,
                Tags = analysis.Tags,
                Keywords = analysis.Keywords,
                SeriesKey = seriesKey,
            };
            relatedEntries = MeetingLinker.FindRelated(
                current, _meetingIndexStore.All(), _config.MeetingLinkMinSharedTags, _config.MeetingLinkMaxRelated);
        }

        // 4. Write the meeting folder (transcript copy + note + diagrams + keywords + related
        // meetings). Never touches the original source file.
        var meetingFolder = MeetingFolderWriter.WriteMeetingFolder(_config.OutputDir, transcript, analysis, relatedEntries, seriesKey);

        // 5. Append this meeting's tags/keywords into the global, append-only vocabulary, and
        // (when linking is enabled) index this meeting for future related-meeting lookups.
        _vocabularyStore.AddMeeting(transcript.Id, analysis.Tags, analysis.Keywords);
        if (_config.EnableMeetingLinking && _meetingIndexStore != null)
        {
            _meetingIndexStore.AddOrUpdate(new MeetingIndexEntry
            {
                MeetingId = transcript.Id,
                Title = transcript.Title ?? "",
                DateEpochMs = transcript.Date,
                FolderPath = meetingFolder,
                Tags = analysis.Tags,
                Keywords = analysis.Keywords,
                SeriesKey = seriesKey,
            });
        }

        // 6. Persist state now that analysis + writing succeeded.
        var record = new MeetingRecord
        {
            SourceKey = sourceKey,
            ContentHash = transcript.SourceType == TranscriptSourceType.Folder ? transcript.ContentHash : transcript.Id,
            MeetingFolder = meetingFolder,
            FirefliesId = transcript.FirefliesId,
            MeetingId = transcript.Id,
            Analyzed = true,
        };
        _stateStore.UpsertRecord(record);

        // 7. Optional: delete the transcript from Fireflies (opt-in, irreversible). Only
        // attempted when an ID was actually resolved — never guessed.
        if (_config.FirefliesAutoDeleteAfterProcessing && transcript.FirefliesId != null && _firefliesClient != null)
        {
            try
            {
                await _firefliesClient.DeleteTranscriptAsync(transcript.FirefliesId).ConfigureAwait(false);
                _stateStore.MarkDeleted(sourceKey);
                _logger.Info($"Deleted transcript '{transcript.FirefliesId}' from Fireflies after successful processing.");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to delete transcript '{transcript.FirefliesId}' from Fireflies; will retry next sync (processing itself was not affected).", ex);
            }
        }

        // 8. Optional exports.
        if (_config.EnableObsidianExport && _obsidianExporter != null)
        {
            try
            {
                _obsidianExporter.Export(meetingFolder);
                _stateStore.MarkObsidianExported(sourceKey);
            }
            catch (Exception ex)
            {
                _logger.Error("Obsidian export failed; will retry next sync.", ex);
            }
        }

        if (_config.EnableNotionExport && _notionExporter != null)
        {
            try
            {
                // Resolve each related meeting's already-created Notion page id (if any) so the
                // native relation property (when enabled) can reference it — never guessed;
                // related meetings not yet exported to Notion simply have a null page id and are
                // skipped for the relation property (but still listed in the plain-text block).
                var notionRelatedMeetings = relatedEntries.Select(e => new NotionRelatedMeetingRef
                {
                    Title = e.Title,
                    DateEpochMs = e.DateEpochMs,
                    NotionPageId = _stateStore.GetRecordByMeetingId(e.MeetingId)?.NotionPageId,
                }).ToList();

                var relationPropertyName = _config.EnableNotionRelationLinks ? _config.NotionRelationPropertyName : null;
                var pageId = await _notionExporter.ExportAsync(transcript, analysis, notionRelatedMeetings, relationPropertyName).ConfigureAwait(false);
                _stateStore.MarkNotionExported(sourceKey, pageId);
            }
            catch (Exception ex)
            {
                _logger.Error("Notion export failed; will retry next sync.", ex);
            }
        }

        // 9. Optional: show a macOS Notification Center alert that the meeting was processed.
        if (_config.EnableMacOsNotifications && _macNotifier != null)
        {
            try
            {
                var title = string.IsNullOrWhiteSpace(transcript.Title) ? "Meeting processed" : transcript.Title!;
                var subtitle = "Pensieve";
                var message = !string.IsNullOrWhiteSpace(analysis.Summary)
                    ? (analysis.Summary.Length > 120 ? analysis.Summary.Substring(0, 117) + "..." : analysis.Summary)
                    : "Meeting transcript has been processed.";
                _macNotifier.Notify(title, subtitle, message);
            }
            catch (Exception ex)
            {
                _logger.Error("macOS notification failed; this does not affect processing state.", ex);
            }
        }

        _logger.Info($"Finished transcript '{transcript.Id}'.");
    }

    /// <summary>Starts live folder watching for the `watch` command; new files are processed as
    /// soon as they finish being written by Fireflies.</summary>
    public void StartLiveFolderWatch()
    {
        if (!_config.EnableFolderWatch || _folderWatcher == null)
        {
            _logger.Warn("StartLiveFolderWatch called but folder watching is disabled (ENABLE_FOLDER_WATCH=false).");
            return;
        }

        _folderWatcher.Start(filePath =>
        {
            TryProcessFolderFileAsync(filePath).GetAwaiter().GetResult();
        });
    }
}
