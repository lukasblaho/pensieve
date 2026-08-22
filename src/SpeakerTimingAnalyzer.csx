#nullable enable
// SpeakerTimingAnalyzer.csx
// Deterministic (non-LLM) computation of per-speaker speaking time and turn count. Never
// invents a speaker: only speakers actually found in the transcript's turns are reported.
//
// Rule: for each turn, duration = (start time of the NEXT turn) - (start time of THIS turn),
// summed per speaker. The transcript's final turn contributes 0 duration, since there is no
// subsequent timestamp to bound it (a deliberate, simple undercount rather than an estimate).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public static class SpeakerTimingAnalyzer
{
    // Matches Fireflies' native turn format: "**Speaker 1** *[00:02]*: text" or with hours
    // "**Speaker 1** *[1:02:03]*: text".
    private static readonly Regex TurnRegex = new Regex(
        @"^\*\*(?<speaker>[^*]+)\*\*\s*\*\[(?<time>\d{1,2}(?::\d{2}){1,2})\]\*\s*:",
        RegexOptions.Multiline);

    private sealed record Turn(string Speaker, double StartSeconds);

    /// <summary>Parses per-speaker turns out of a folder-sourced transcript's raw text
    /// (Fireflies' native "**Speaker N** *[MM:SS]*: ..." format) and computes speaking time/turn
    /// counts. Returns an empty list if no recognizable turns are found (never guessed).</summary>
    public static List<SpeakerTiming> AnalyzeRawText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new List<SpeakerTiming>();
        }

        var turns = new List<Turn>();
        foreach (Match match in TurnRegex.Matches(rawText))
        {
            var speaker = match.Groups["speaker"].Value.Trim();
            if (TryParseTimeToSeconds(match.Groups["time"].Value, out var seconds))
            {
                turns.Add(new Turn(speaker, seconds));
            }
        }

        return ComputeFromTurns(turns);
    }

    /// <summary>Computes speaking time/turn counts from an API-sourced transcript's ordered
    /// <see cref="Sentence"/> list, grouping consecutive sentences into speaker turns by
    /// <see cref="Sentence.SpeakerName"/> and using each sentence's <see cref="Sentence.StartTime"/>
    /// as its turn start.</summary>
    public static List<SpeakerTiming> AnalyzeSentences(List<Sentence> sentences)
    {
        if (sentences == null || sentences.Count == 0)
        {
            return new List<SpeakerTiming>();
        }

        var ordered = sentences
            .Where(s => !string.IsNullOrWhiteSpace(s.SpeakerName) && s.StartTime.HasValue)
            .OrderBy(s => s.StartTime!.Value)
            .ToList();

        var turns = ordered.Select(s => new Turn(s.SpeakerName!.Trim(), s.StartTime!.Value)).ToList();
        return ComputeFromTurns(turns);
    }

    private static List<SpeakerTiming> ComputeFromTurns(List<Turn> turns)
    {
        var totals = new Dictionary<string, SpeakerTiming>();

        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            var duration = i < turns.Count - 1 ? Math.Max(0, turns[i + 1].StartSeconds - turn.StartSeconds) : 0;

            if (!totals.TryGetValue(turn.Speaker, out var timing))
            {
                timing = new SpeakerTiming { Speaker = turn.Speaker };
                totals[turn.Speaker] = timing;
            }

            timing.SpeakingTimeSeconds += duration;
            timing.TurnCount += 1;
        }

        // Preserve first-appearance order for stable, predictable output.
        var order = new List<string>();
        var seen = new HashSet<string>();
        foreach (var turn in turns)
        {
            if (seen.Add(turn.Speaker)) order.Add(turn.Speaker);
        }

        return order.Select(s => totals[s]).ToList();
    }

    private static bool TryParseTimeToSeconds(string text, out double seconds)
    {
        var parts = text.Split(':');
        seconds = 0;
        try
        {
            if (parts.Length == 2)
            {
                var m = int.Parse(parts[0]);
                var s = int.Parse(parts[1]);
                seconds = m * 60 + s;
                return true;
            }
            if (parts.Length == 3)
            {
                var h = int.Parse(parts[0]);
                var m = int.Parse(parts[1]);
                var s = int.Parse(parts[2]);
                seconds = h * 3600 + m * 60 + s;
                return true;
            }
        }
        catch (FormatException)
        {
            // fall through to return false below
        }
        return false;
    }
}
