#!/usr/bin/env dotnet-script
#nullable enable
// run-tests.csx
// Custom lightweight test runner (no xUnit/dotnet test dependency, since dotnet-script
// doesn't integrate cleanly with standard .NET test runners).
//
// Usage: dotnet script tests/run-tests.csx
// Exits with code 0 if all assertions passed, 1 if any failed.

#load "TestKit.csx"
#load "test-FirefliesClient.csx"
#load "test-CopilotCliClient.csx"
#load "test-StateStore.csx"
#load "test-MeetingFolderWriter.csx"
#load "test-TranscriptFileParser.csx"
#load "test-GlobalVocabularyStore.csx"
#load "test-FolderWatcher.csx"
#load "test-ObsidianExporter.csx"
#load "test-NotionExporter.csx"
#load "test-MacNotifier.csx"
#load "test-DateTimeHelper.csx"
#load "test-KeywordFormatter.csx"
#load "test-SpeakerTimingAnalyzer.csx"

using System;

Console.WriteLine();
Console.WriteLine("========================================");
Console.WriteLine($"Test summary: {TestKit.Passed} passed, {TestKit.Failed} failed");
Console.WriteLine("========================================");

Environment.Exit(TestKit.Failed == 0 ? 0 : 1);
