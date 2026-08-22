#nullable enable
// test-ObsidianExporter.csx
// Verifies the Obsidian export copies a meeting folder's full contents (including
// subdirectories like diagrams/) into the configured vault subfolder, without touching or
// removing the source meeting folder.

#load "TestKit.csx"
#load "../src/ObsidianExporter.csx"
#load "../src/Logging.csx"

using System;
using System.IO;

TestKit.Section("ObsidianExporter: copies a meeting folder's files and subfolders into the vault");
{
    var sourceFolder = Path.Combine(Path.GetTempPath(), $"pensieve-meeting-{Guid.NewGuid()}");
    Directory.CreateDirectory(sourceFolder);
    Directory.CreateDirectory(Path.Combine(sourceFolder, "diagrams"));
    File.WriteAllText(Path.Combine(sourceFolder, "note.md"), "# Note");
    File.WriteAllText(Path.Combine(sourceFolder, "transcript.md"), "raw transcript");
    File.WriteAllText(Path.Combine(sourceFolder, "diagrams", "diagram-1.md"), "```mermaid\ngraph TD;\n```");

    var vaultPath = Path.Combine(Path.GetTempPath(), $"pensieve-vault-{Guid.NewGuid()}");
    var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
    var exporter = new ObsidianExporter(vaultPath, "Meetings", logger);

    var destination = exporter.Export(sourceFolder);

    TestKit.Assert(Directory.Exists(destination), "destination folder should be created in the vault");
    TestKit.Assert(destination.Contains(Path.Combine(vaultPath, "Meetings")), "destination should be under the configured vault subfolder");
    TestKit.Assert(File.Exists(Path.Combine(destination, "note.md")), "note.md should be copied");
    TestKit.Assert(File.Exists(Path.Combine(destination, "transcript.md")), "transcript.md should be copied");
    TestKit.Assert(File.Exists(Path.Combine(destination, "diagrams", "diagram-1.md")), "nested diagrams/ folder should be copied recursively");
    TestKit.Assert(File.ReadAllText(Path.Combine(destination, "note.md")) == "# Note", "copied file content should match the source exactly");

    TestKit.Assert(Directory.Exists(sourceFolder), "source meeting folder should still exist (export copies, never moves)");
    TestKit.Assert(File.Exists(Path.Combine(sourceFolder, "note.md")), "source files should remain untouched after export");

    Directory.Delete(sourceFolder, recursive: true);
    Directory.Delete(vaultPath, recursive: true);
}
