#nullable enable
// ObsidianExporter.csx
// Optional (config-gated) export: copies a fully-written meeting folder into an Obsidian
// vault subfolder, preserving the note's YAML frontmatter/tags so Obsidian picks them up
// natively. Never touches the original transcript source file.

#load "Logging.csx"

using System;
using System.IO;

public sealed class ObsidianExporter
{
    private readonly string _vaultPath;
    private readonly string _subfolder;
    private readonly Logger _logger;

    public ObsidianExporter(string vaultPath, string subfolder, Logger logger)
    {
        _vaultPath = vaultPath;
        _subfolder = subfolder;
        _logger = logger;
    }

    /// <summary>Copies the meeting folder's contents into
    /// VAULT_PATH/SUBFOLDER/&lt;meeting-folder-name&gt;/.</summary>
    public string Export(string meetingFolderPath)
    {
        var meetingFolderName = Path.GetFileName(meetingFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destination = Path.Combine(_vaultPath, _subfolder, meetingFolderName);

        Directory.CreateDirectory(destination);
        CopyDirectoryRecursive(meetingFolderPath, destination);

        _logger.Info($"ObsidianExporter: exported '{meetingFolderName}' to '{destination}'.");
        return destination;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectoryRecursive(dir, destSubDir);
        }
    }
}
