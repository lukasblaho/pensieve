#nullable enable
// test-CopilotCliClient.csx
// Verifies JSON extraction (incl. markdown-fenced responses), structured-JSON parsing for the
// analysis schema (summary/agreements/open_questions/next_actions/meeting_type/category/topics
// [flattened into Tags, capped at 5]/keywords/diagrams), and full process-invocation plumbing
// using a small fake "copilot" executable (a shell script) instead of the real GitHub Copilot
// CLI, so tests never depend on network access, authentication, or the CLI being installed.

#load "TestKit.csx"
#load "../src/CopilotCliClient.csx"
#load "../src/Logging.csx"
#load "../src/Models.csx"

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

TestKit.Section("CopilotCliClient: builds transcript text with speaker labels, ordered by index");
{
    var transcript = new Transcript
    {
        Id = "t1",
        Title = "Demo",
        Sentences = new System.Collections.Generic.List<Sentence>
        {
            new Sentence { Index = 1, SpeakerName = "Bob", Text = "Second line." },
            new Sentence { Index = 0, SpeakerName = "Alice", Text = "First line." },
        }
    };

    var text = CopilotCliClient.BuildTranscriptText(transcript);

    TestKit.Assert(text.IndexOf("Alice: First line.") < text.IndexOf("Bob: Second line."), "transcript text should be ordered by sentence index, not insertion order");
}

TestKit.Section("CopilotCliClient: ExtractJson tolerates plain JSON, fenced JSON, and stray surrounding text");
{
    var plain = "{\"summary\": \"ok\"}";
    TestKit.Assert(CopilotCliClient.ExtractJson(plain) == plain, "plain JSON should pass through unchanged");

    var fenced = "```json\n{\"summary\": \"ok\"}\n```";
    TestKit.Assert(CopilotCliClient.ExtractJson(fenced).Trim() == "{\"summary\": \"ok\"}", "should strip ```json fences");

    var fencedNoLang = "```\n{\"summary\": \"ok\"}\n```";
    TestKit.Assert(CopilotCliClient.ExtractJson(fencedNoLang).Trim() == "{\"summary\": \"ok\"}", "should strip plain ``` fences without a language tag");

    var withPreamble = "Here is the result:\n{\"summary\": \"ok\"}\nThanks!";
    TestKit.Assert(CopilotCliClient.ExtractJson(withPreamble) == "{\"summary\": \"ok\"}", "should extract JSON object from surrounding stray text");
}

TestKit.Section("CopilotCliClient: parses well-formed structured JSON (new analysis schema)");
{
    var fixturePath = Path.Combine(Directory.GetCurrentDirectory(), "tests", "test-fixtures", "sample-copilot-response.json");
    var json = File.ReadAllText(fixturePath);

    var analysis = CopilotCliClient.ParseTranscriptAnalysis(json);

    TestKit.Assert(analysis.Summary.Contains("weekly status"), "summary should be the English text from the fixture");
    TestKit.Assert(analysis.Agreements.Count == 1, "should parse 1 agreement");
    TestKit.Assert(analysis.OpenQuestions.Count == 0, "should parse 0 open questions (empty array in fixture)");
    TestKit.Assert(analysis.NextActions.Count == 1, "should parse 1 next action");
    TestKit.Assert(analysis.NextActions[0].Owner == "Bob", "action owner should be parsed correctly");
    TestKit.Assert(analysis.NextActions[0].Due == "Friday", "action due date should be parsed correctly");
    TestKit.Assert(analysis.MeetingType == "sync", "meeting_type should be parsed correctly");
    TestKit.Assert(analysis.Category == "team", "category should be parsed correctly");
    TestKit.Assert(analysis.Topics.Count == 1 && analysis.Topics.Contains("release planning"), "topics should be parsed correctly");
    TestKit.Assert(analysis.Tags.Count == 3 && analysis.Tags.SequenceEqual(new[] { "sync", "team", "release planning" }), "tags should be flattened from meeting_type + category + topics, in that order");
    TestKit.Assert(analysis.Keywords.Count == 2 && analysis.Keywords.Contains("weekly status"), "keywords should be parsed correctly");
    TestKit.Assert(analysis.Diagrams.Count == 0, "should parse 0 diagrams (empty array in fixture)");
}

TestKit.Section("CopilotCliClient: flattened Tags is hard-capped at 5 even if the model returns more topics");
{
    var jsonWithTooManyTopics = "{\"summary\": \"Test\", \"agreements\": [], \"open_questions\": [], \"next_actions\": [], \"meeting_type\": \"planning\", \"category\": \"technology\", \"topics\": [\"a\", \"b\", \"c\", \"d\", \"e\"], \"keywords\": [], \"diagrams\": []}";
    var analysis = CopilotCliClient.ParseTranscriptAnalysis(jsonWithTooManyTopics);

    TestKit.Assert(analysis.Tags.Count == 5, "Tags should be hard-capped at 5 regardless of how many topics the model returns");
    TestKit.Assert(analysis.Tags.SequenceEqual(new[] { "planning", "technology", "a", "b", "c" }), "Tags should keep meeting_type + category first, then truncate topics to fill the remaining slots");
}

TestKit.Section("CopilotCliClient: missing owner/due in a returned action falls back to 'not specified'");
{
    var jsonWithMissingFields = "{\"summary\": \"Test\", \"agreements\": [], \"open_questions\": [], \"next_actions\": [{\"task\": \"Do something\"}], \"keywords\": [], \"diagrams\": []}";
    var analysis = CopilotCliClient.ParseTranscriptAnalysis(jsonWithMissingFields);

    TestKit.Assert(analysis.NextActions[0].Owner == "not specified", "owner should default to 'not specified' when absent from the response");
    TestKit.Assert(analysis.NextActions[0].Due == "not specified", "due should default to 'not specified' when absent from the response");
}

TestKit.Section("CopilotCliClient: parses diagrams only when explicitly present, never fabricates empty ones");
{
    var jsonWithDiagram = "{\"summary\": \"Test\", \"agreements\": [], \"open_questions\": [], \"next_actions\": [], \"keywords\": [], \"diagrams\": [{\"title\": \"Data Flow\", \"mermaid\": \"graph TD; A-->B;\"}, {\"title\": \"Empty\", \"mermaid\": \"\"}]}";
    var analysis = CopilotCliClient.ParseTranscriptAnalysis(jsonWithDiagram);

    TestKit.Assert(analysis.Diagrams.Count == 1, "should only include diagrams with non-empty mermaid content, skipping the empty one");
    TestKit.Assert(analysis.Diagrams[0].Title == "Data Flow", "diagram title should be parsed correctly");
    TestKit.Assert(analysis.Diagrams[0].Mermaid == "graph TD; A-->B;", "diagram mermaid content should be parsed correctly");
}

TestKit.Section("CopilotCliClient: parses speaker_quality ratings and clamps out-of-range values");
{
    var jsonWithQuality = "{\"summary\": \"Test\", \"agreements\": [], \"open_questions\": [], \"next_actions\": [], \"keywords\": [], \"diagrams\": [], \"speaker_quality\": [{\"speaker\": \"Alice\", \"clarity\": 4, \"informativeness\": 5, \"engagement\": 3, \"rationale\": \"Explained the plan clearly.\"}, {\"speaker\": \"Bob\", \"clarity\": 9, \"informativeness\": -2, \"engagement\": 3.6, \"rationale\": \"Short answers only.\"}]}";
    var analysis = CopilotCliClient.ParseTranscriptAnalysis(jsonWithQuality);

    TestKit.Assert(analysis.SpeakerQuality.Count == 2, "should parse 2 speaker quality ratings");
    var alice = analysis.SpeakerQuality.First(q => q.Speaker == "Alice");
    TestKit.Assert(alice.Clarity == 4 && alice.Informativeness == 5 && alice.Engagement == 3, "in-range ratings should be parsed as-is");
    TestKit.Assert(alice.Rationale == "Explained the plan clearly.", "rationale should be parsed correctly");

    var bob = analysis.SpeakerQuality.First(q => q.Speaker == "Bob");
    TestKit.Assert(bob.Clarity == 5, "clarity above range should be clamped to 5");
    TestKit.Assert(bob.Informativeness == 1, "informativeness below range should be clamped to 1");
    TestKit.Assert(bob.Engagement == 4, "engagement should be rounded (3.6 -> 4) then within range");
}

TestKit.Section("CopilotCliClient: speaker_quality missing/empty is handled gracefully (no fabricated ratings)");
{
    var jsonNoQuality = "{\"summary\": \"Test\", \"agreements\": [], \"open_questions\": [], \"next_actions\": [], \"keywords\": [], \"diagrams\": []}";
    var analysis = CopilotCliClient.ParseTranscriptAnalysis(jsonNoQuality);

    TestKit.Assert(analysis.SpeakerQuality.Count == 0, "should default to an empty list when speaker_quality is absent");
}

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Console.WriteLine("Skipping process-invocation tests on Windows (fake executable is a POSIX shell script).");
}
else
{
    TestKit.Section("CopilotCliClient: invokes the configured executable and parses its stdout as the analysis");
    {
        var fixturePath = Path.Combine(Directory.GetCurrentDirectory(), "tests", "test-fixtures", "sample-copilot-response-text.txt");
        var responseText = File.ReadAllText(fixturePath);

        var scriptPath = WriteFakeCopilotScript(alwaysSucceedResponse: responseText);
        var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
        var client = new CopilotCliClient(logger, model: "auto", executable: scriptPath);

        var transcript = new Transcript { Id = "t1", Title = "Weekly Sync", RawText = "Alice: Let's start." };
        var analysis = await client.AnalyzeTranscriptAsync(transcript);

        TestKit.Assert(analysis.Summary.Contains("weekly status"), "analysis generated via the fake copilot executable should be parsed correctly");
        TestKit.Assert(analysis.NextActions.Count == 1, "should parse the next action from the fake executable's output");
        TestKit.Assert(analysis.Tags.Contains("release planning"), "should parse tags from the fake executable's output");

        File.Delete(scriptPath);
    }

    TestKit.Section("CopilotCliClient: retries once on transient (non-zero exit) failure, then succeeds");
    {
        var fixturePath = Path.Combine(Directory.GetCurrentDirectory(), "tests", "test-fixtures", "sample-copilot-response-text.txt");
        var responseText = File.ReadAllText(fixturePath);
        var counterFile = Path.Combine(Path.GetTempPath(), $"pensieve-fake-copilot-counter-{Guid.NewGuid()}");

        var scriptPath = WriteFakeCopilotScript(alwaysSucceedResponse: null, failOnceThenSucceedWith: responseText, counterFile: counterFile);
        var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
        var client = new CopilotCliClient(logger, model: "auto", executable: scriptPath);

        var transcript = new Transcript { Id = "t2", Title = "Retry Test", RawText = "Alice: Let's start." };
        var analysis = await client.AnalyzeTranscriptAsync(transcript);

        TestKit.Assert(analysis.Summary.Contains("weekly status"), "should recover after one transient failure and parse the eventual successful response");

        File.Delete(scriptPath);
        if (File.Exists(counterFile)) File.Delete(counterFile);
    }

    TestKit.Section("CopilotCliClient: a malformed-JSON response triggers a fresh CLI invocation (regeneration), not a re-parse of the same broken text");
    {
        var fixturePath = Path.Combine(Directory.GetCurrentDirectory(), "tests", "test-fixtures", "sample-copilot-response-text.txt");
        var responseText = File.ReadAllText(fixturePath);
        var counterFile = Path.Combine(Path.GetTempPath(), $"pensieve-fake-copilot-json-counter-{Guid.NewGuid()}");

        // Exit code 0 both times (so the process-level retry loop is not what saves this), but
        // the FIRST invocation's stdout is invalid JSON; only the SECOND invocation returns a
        // valid response. Re-parsing the same (first) broken string could never succeed — only
        // an actual second invocation of the executable can produce this valid text.
        var scriptPath = WriteFakeCopilotScriptWithBadJsonThenValid(responseText, counterFile);
        var logger = new Logger(Path.Combine(Path.GetTempPath(), "pensieve-tests-logs"));
        var client = new CopilotCliClient(logger, model: "auto", executable: scriptPath);

        var transcript = new Transcript { Id = "t3", Title = "Bad JSON Test", RawText = "Alice: Let's start." };
        var analysis = await client.AnalyzeTranscriptAsync(transcript);

        TestKit.Assert(analysis.Summary.Contains("weekly status"), "should regenerate via a fresh CLI invocation and successfully parse the second (valid) response");
        TestKit.Assert(File.Exists(counterFile) && File.ReadAllText(counterFile).Trim() == "2", "the fake executable should have been invoked exactly twice (initial + one regeneration)");

        File.Delete(scriptPath);
        if (File.Exists(counterFile)) File.Delete(counterFile);
    }
}

// Writes a small POSIX shell script whose FIRST invocation exits 0 with invalid JSON stdout,
// and every subsequent invocation exits 0 with the given valid response — used to verify that a
// JSON parse failure triggers a genuine second CLI invocation rather than re-parsing the same
// broken text. Also increments a counter file so tests can assert the exact invocation count.
static string WriteFakeCopilotScriptWithBadJsonThenValid(string validResponse, string counterFile)
{
    var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-copilot-badjson-{Guid.NewGuid()}.sh");
    var body =
        $"#!/bin/bash\n" +
        $"count=0\n" +
        $"if [ -f \"{counterFile}\" ]; then count=$(cat \"{counterFile}\"); fi\n" +
        $"count=$((count + 1))\n" +
        $"echo \"$count\" > \"{counterFile}\"\n" +
        $"if [ \"$count\" -eq 1 ]; then\n" +
        $"  echo '{{\"summary\": \"broken' \n" +
        $"  exit 0\n" +
        $"else\n" +
        $"  cat <<'FAKE_COPILOT_EOF'\n{validResponse}\nFAKE_COPILOT_EOF\n" +
        $"  exit 0\n" +
        $"fi\n";

    File.WriteAllText(scriptPath, body);
    var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("chmod", $"+x \"{scriptPath}\"") { UseShellExecute = false });
    chmod!.WaitForExit();
    return scriptPath;
}

// Writes a small POSIX shell script that stands in for the real `copilot` CLI, so process
// spawning, argument passing, and stdout parsing can be tested without any real dependency.
static string WriteFakeCopilotScript(string? alwaysSucceedResponse, string? failOnceThenSucceedWith = null, string? counterFile = null)
{
    var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-copilot-{Guid.NewGuid()}.sh");
    string body;

    if (alwaysSucceedResponse != null)
    {
        body = $"#!/bin/bash\ncat <<'FAKE_COPILOT_EOF'\n{alwaysSucceedResponse}\nFAKE_COPILOT_EOF\nexit 0\n";
    }
    else
    {
        body = $"#!/bin/bash\n" +
               $"if [ ! -f \"{counterFile}\" ]; then\n" +
               $"  echo attempt1 > \"{counterFile}\"\n" +
               $"  echo 'simulated transient failure' 1>&2\n" +
               $"  exit 1\n" +
               $"else\n" +
               $"  cat <<'FAKE_COPILOT_EOF'\n{failOnceThenSucceedWith}\nFAKE_COPILOT_EOF\n" +
               $"  exit 0\n" +
               $"fi\n";
    }

    File.WriteAllText(scriptPath, body);
    var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("chmod", $"+x \"{scriptPath}\"") { UseShellExecute = false });
    chmod!.WaitForExit();
    return scriptPath;
}
