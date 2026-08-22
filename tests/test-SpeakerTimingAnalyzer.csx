#nullable enable
// test-SpeakerTimingAnalyzer.csx
// Verifies deterministic per-speaker speaking-time and turn-count computation for both
// folder-sourced (raw text, "**Speaker N** *[MM:SS]*:") and API-sourced (Sentence list)
// transcripts, including the "last turn contributes 0 duration" rule.

#load "TestKit.csx"
#load "../src/Models.csx"
#load "../src/SpeakerTimingAnalyzer.csx"

using System;
using System.Collections.Generic;
using System.Linq;

TestKit.Section("SpeakerTimingAnalyzer: computes per-speaker speaking time from folder-sourced raw text");
{
    var rawText =
        "**Speaker 1** *[00:00]*: Hello everyone, let's get started.\n" +
        "**Speaker 2** *[00:10]*: Sounds good.\n" +
        "**Speaker 1** *[00:15]*: Let's review the agenda.\n" +
        "**Speaker 2** *[00:40]*: Agreed.\n"; // last turn -> 0 duration

    var stats = SpeakerTimingAnalyzer.AnalyzeRawText(rawText);

    TestKit.Assert(stats.Count == 2, "should find exactly 2 speakers");

    var s1 = stats.First(s => s.Speaker == "Speaker 1");
    var s2 = stats.First(s => s.Speaker == "Speaker 2");

    // Speaker 1: turn@0 -> next@10 = 10s; turn@15 -> next@40 = 25s. Total = 35s.
    TestKit.Assert(s1.SpeakingTimeSeconds == 35, $"Speaker 1 speaking time should be 35s, was {s1.SpeakingTimeSeconds}");
    TestKit.Assert(s1.TurnCount == 2, "Speaker 1 should have 2 turns");

    // Speaker 2: turn@10 -> next@15 = 5s; turn@40 is the LAST turn -> 0s. Total = 5s.
    TestKit.Assert(s2.SpeakingTimeSeconds == 5, $"Speaker 2 speaking time should be 5s, was {s2.SpeakingTimeSeconds}");
    TestKit.Assert(s2.TurnCount == 2, "Speaker 2 should have 2 turns");
}

TestKit.Section("SpeakerTimingAnalyzer: parses H:MM:SS timestamps for longer meetings");
{
    var rawText =
        "**Alice** *[59:50]*: Almost at the hour mark.\n" +
        "**Bob** *[1:00:10]*: Yep, right on time.\n";

    var stats = SpeakerTimingAnalyzer.AnalyzeRawText(rawText);
    var alice = stats.First(s => s.Speaker == "Alice");

    // 1:00:10 (3610s) - 59:50 (3590s) = 20s
    TestKit.Assert(alice.SpeakingTimeSeconds == 20, $"Alice's speaking time should be 20s across the hour boundary, was {alice.SpeakingTimeSeconds}");
}

TestKit.Section("SpeakerTimingAnalyzer: returns empty list for text with no recognizable turns (never guesses)");
{
    var stats = SpeakerTimingAnalyzer.AnalyzeRawText("Just some plain prose with no speaker markers at all.");
    TestKit.Assert(stats.Count == 0, "should return an empty list when no speaker turns can be parsed");

    var statsEmpty = SpeakerTimingAnalyzer.AnalyzeRawText("");
    TestKit.Assert(statsEmpty.Count == 0, "should return an empty list for empty input");
}

TestKit.Section("SpeakerTimingAnalyzer: computes per-speaker speaking time from API-sourced Sentence list");
{
    var sentences = new List<Sentence>
    {
        new Sentence { Index = 0, SpeakerName = "Alice", Text = "Hi", StartTime = 0 },
        new Sentence { Index = 1, SpeakerName = "Bob", Text = "Hey", StartTime = 8 },
        new Sentence { Index = 2, SpeakerName = "Alice", Text = "Let's begin", StartTime = 12 },
        new Sentence { Index = 3, SpeakerName = "Bob", Text = "Sure", StartTime = 30 }, // last -> 0 duration
    };

    var stats = SpeakerTimingAnalyzer.AnalyzeSentences(sentences);
    var alice = stats.First(s => s.Speaker == "Alice");
    var bob = stats.First(s => s.Speaker == "Bob");

    // Alice: 0->8 = 8s; 12->30 = 18s. Total = 26s.
    TestKit.Assert(alice.SpeakingTimeSeconds == 26, $"Alice's speaking time should be 26s, was {alice.SpeakingTimeSeconds}");
    // Bob: 8->12 = 4s; 30 is the last sentence -> 0s. Total = 4s.
    TestKit.Assert(bob.SpeakingTimeSeconds == 4, $"Bob's speaking time should be 4s, was {bob.SpeakingTimeSeconds}");
}

TestKit.Section("SpeakerTimingAnalyzer: sentences without a SpeakerName or StartTime are skipped, never guessed");
{
    var sentences = new List<Sentence>
    {
        new Sentence { Index = 0, SpeakerName = "Alice", Text = "Hi", StartTime = 0 },
        new Sentence { Index = 1, SpeakerName = null, Text = "unattributed", StartTime = 5 },
        new Sentence { Index = 2, SpeakerName = "Bob", Text = "no timestamp", StartTime = null },
        new Sentence { Index = 3, SpeakerName = "Alice", Text = "bye", StartTime = 20 },
    };

    var stats = SpeakerTimingAnalyzer.AnalyzeSentences(sentences);

    TestKit.Assert(stats.Count == 1, "only Alice's fully-attributed, timestamped turns should be counted");
    TestKit.Assert(stats[0].Speaker == "Alice", "Bob should be excluded since none of his sentences had a start time");
}
