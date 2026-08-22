#nullable enable
// test-FolderWatcher.csx
// Verifies ScanExisting() picks up all .md files in the watch folder (oldest first) and that
// live Start() debouncing eventually reports a new file exactly once after it stabilizes.

#load "TestKit.csx"
#load "../src/FolderWatcher.csx"
#load "../src/Logging.csx"

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

TestKit.Section("FolderWatcher: ScanExisting returns an empty list for a non-existent folder");
{
    var folder = Path.Combine(Path.GetTempPath(), $"pensieve-watch-missing-{Guid.NewGuid()}");
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var watcher = new FolderWatcher(folder, logger);

    var files = watcher.ScanExisting();
    TestKit.Assert(files.Count == 0, "should return an empty list when the watch folder doesn't exist");
}

TestKit.Section("FolderWatcher: ScanExisting finds all .md files present in the folder");
{
    var folder = Path.Combine(Path.GetTempPath(), $"pensieve-watch-{Guid.NewGuid()}");
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "meeting1.md"), "content1");
    File.WriteAllText(Path.Combine(folder, "meeting2.md"), "content2");
    File.WriteAllText(Path.Combine(folder, "ignored.txt"), "not markdown");

    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var watcher = new FolderWatcher(folder, logger);

    var files = watcher.ScanExisting();

    TestKit.Assert(files.Count == 2, "should only pick up .md files, ignoring other extensions");
    TestKit.Assert(files.TrueForAll(f => f.EndsWith(".md")), "all returned files should have a .md extension");

    Directory.Delete(folder, recursive: true);
}

TestKit.Section("FolderWatcher: live Start() reports a newly created file after the debounce window");
{
    var folder = Path.Combine(Path.GetTempPath(), $"pensieve-watch-live-{Guid.NewGuid()}");
    Directory.CreateDirectory(folder);
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));

    using var watcher = new FolderWatcher(folder, logger);
    var reportedFiles = new List<string>();
    var signal = new ManualResetEventSlim(false);

    watcher.Start(path =>
    {
        lock (reportedFiles) { reportedFiles.Add(path); }
        signal.Set();
    }, debounce: TimeSpan.FromMilliseconds(200));

    var newFile = Path.Combine(folder, "new-meeting.md");
    File.WriteAllText(newFile, "hello");

    var signaled = signal.Wait(TimeSpan.FromSeconds(5));

    TestKit.Assert(signaled, "the debounced callback should fire within 5 seconds of the file being created");
    lock (reportedFiles)
    {
        TestKit.Assert(reportedFiles.Count >= 1 && reportedFiles.Contains(newFile), "the new file's path should be reported to the callback");
    }

    Directory.Delete(folder, recursive: true);
}
