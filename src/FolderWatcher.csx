#nullable enable
// FolderWatcher.csx
// Watches a local folder for new .md transcript files that Fireflies auto-saves there.
// Two complementary modes are supported:
//   - ScanExisting(): a one-shot directory scan, used by `sync`/`run` to pick up any .md file
//     present at the time of the pass (works even if the watcher process wasn't running when
//     the file was dropped).
//   - Start(onNewFile): a live FileSystemWatcher for the `watch` command, debounced so a file
//     is only reported once it has stopped growing (Fireflies may write it incrementally).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

public sealed class FolderWatcher : IDisposable
{
    private readonly string _folder;
    private readonly Logger _logger;
    private FileSystemWatcher? _watcher;
    private readonly Dictionary<string, Timer> _debounceTimers = new();
    private readonly object _lock = new object();

    public FolderWatcher(string folder, Logger logger)
    {
        _folder = folder;
        _logger = logger;
    }

    /// <summary>Returns all .md files currently present in the watched folder, oldest first.</summary>
    public List<string> ScanExisting()
    {
        if (!Directory.Exists(_folder))
        {
            _logger.Warn($"FolderWatcher: watch folder '{_folder}' does not exist.");
            return new List<string>();
        }

        return Directory.GetFiles(_folder, "*.md")
            .OrderByDescending(f => SafeGetCreationTimeUtc(f))
            .ToList();
    }

    /// <summary>
    /// Starts live watching. <paramref name="onNewFile"/> is invoked (on a background thread)
    /// once a new/changed .md file's size has been stable for the debounce window, meaning
    /// Fireflies has finished writing it.
    /// </summary>
    public void Start(Action<string> onNewFile, TimeSpan? debounce = null)
    {
        if (!Directory.Exists(_folder))
        {
            Directory.CreateDirectory(_folder);
        }

        var debounceWindow = debounce ?? TimeSpan.FromSeconds(3);

        _watcher = new FileSystemWatcher(_folder, "*.md")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        void ScheduleDebounced(string path)
        {
            lock (_lock)
            {
                if (_debounceTimers.TryGetValue(path, out var existingTimer))
                {
                    existingTimer.Change(debounceWindow, Timeout.InfiniteTimeSpan);
                    return;
                }

                var timer = new Timer(_ =>
                {
                    lock (_lock) { _debounceTimers.Remove(path); }
                    if (File.Exists(path))
                    {
                        onNewFile(path);
                    }
                }, null, debounceWindow, Timeout.InfiniteTimeSpan);

                _debounceTimers[path] = timer;
            }
        }

        _watcher.Created += (_, e) => ScheduleDebounced(e.FullPath);
        _watcher.Changed += (_, e) => ScheduleDebounced(e.FullPath);
        _watcher.Renamed += (_, e) => ScheduleDebounced(e.FullPath);
        _watcher.Error += (_, e) => _logger.Error("FolderWatcher: watcher error.", e.GetException());

        _logger.Info($"FolderWatcher: watching '{_folder}' for new .md transcripts.");
    }

    private static DateTime SafeGetCreationTimeUtc(string path)
    {
        try { return File.GetCreationTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var timer in _debounceTimers.Values) timer.Dispose();
            _debounceTimers.Clear();
        }
        _watcher?.Dispose();
    }
}
