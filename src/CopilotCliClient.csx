#nullable enable
// CopilotCliClient.csx
// Generates structured transcript analysis by shelling out to the local GitHub Copilot CLI
// (`copilot -p "<prompt>" --silent ...`) instead of calling an LLM HTTP API directly.
// Source transcripts may be in English, Slovak, or Czech, but generated analysis is always
// produced in English. Strict "never invent, stay scoped to this transcript only" instructions
// are enforced in the prompt; missing fields fall back to "not specified"/empty in code, not
// left to the model's discretion.

#load "Logging.csx"
#load "Models.csx"

using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public sealed class CopilotCliClient
{
    private static readonly int[] BackoffSecondsSchedule = { 1, 3, 9 };

    private readonly Logger _logger;
    private readonly string _model;
    private readonly string _executable;

    /// <param name="model">Copilot CLI model name (e.g. "claude-sonnet-4.5", "gpt-5-mini") or
    /// empty/"auto" to let the CLI pick its configured default.</param>
    /// <param name="executable">Name/path of the copilot CLI binary (default: "copilot", found via PATH).</param>
    public CopilotCliClient(Logger logger, string model = "", string executable = "copilot")
    {
        _logger = logger;
        _model = model;
        _executable = executable;
    }

    private const string SystemPrompt = @"You are an assistant that analyzes a single meeting transcript.
The transcript may be in English, Slovak, or Czech, but your entire response MUST be written in English regardless of the transcript's language.
Rules you MUST follow:
1. Base your analysis EXCLUSIVELY on the transcript text given below. Do not use any knowledge about other meetings, projects, or people beyond what is explicitly written here.
2. NEVER invent or guess anything. Do not infer agreements, decisions, owners, due dates, open questions, tags, keywords, or diagrams that are not explicitly supported by the transcript text.
3. If a decision/agreement, open question, owner, or due date is not EXPLICITLY stated in the transcript, use exactly the string ""not specified"" for that field (owner/due), or leave the relevant list empty. Do not guess.
4. tags: propose 3 to 8 short topical tags (single words or short phrases) that describe what this specific transcript is about.
5. keywords: list key terms/phrases that are actually used in this transcript (for a per-meeting vocabulary index). Do not invent terms not present in the text.
6. diagrams: ONLY when the transcript explicitly discusses a process flow, system architecture, or component/data relationship, produce one or more Mermaid diagrams describing exactly what was discussed (each with a short title and valid Mermaid syntax in the ""mermaid"" field, without a surrounding ``` fence). If no such flow/architecture is discussed, return an empty array — do not invent a diagram.
7. Do not open any files, run any commands, or use any tools — base your answer solely on the transcript text below.
8. speaker_quality: for EVERY speaker who actually speaks in the transcript (use their exact label/name as it appears in the transcript, e.g. ""Speaker 1"" or a real name), rate clarity, informativeness, and engagement as integers from 1 (very poor) to 5 (excellent), based only on how they communicated in THIS transcript. Never invent or rate a speaker who does not appear in the transcript. These ratings are a judgment call, not a factual fallback field: always give your best-effort integer 1-5 for each, even if evidence is limited — but the ""rationale"" must honestly reflect that limited evidence and must reference only things actually said in this transcript (no outside assumptions about the person).
9. Respond EXCLUSIVELY as a JSON object with exactly this structure, with no markdown code fence and no extra text whatsoever:
{
  ""summary"": string,
  ""agreements"": string[],
  ""open_questions"": string[],
  ""next_actions"": [ { ""task"": string, ""owner"": string, ""due"": string } ],
  ""tags"": string[],
  ""keywords"": string[],
  ""diagrams"": [ { ""title"": string, ""mermaid"": string } ],
  ""speaker_quality"": [ { ""speaker"": string, ""clarity"": number, ""informativeness"": number, ""engagement"": number, ""rationale"": string } ]
}";

    // How many additional full CLI invocations to attempt when the model's response fails to
    // parse as valid structured JSON. Re-parsing the exact same (invalid) text can never
    // succeed, so each attempt here re-generates a fresh response instead.
    private const int MaxJsonRegenerationAttempts = 2;

    public async Task<TranscriptAnalysis> AnalyzeTranscriptAsync(Transcript transcript)
    {
        var prompt = $"{SystemPrompt}\n\n---\nMeeting title: {transcript.Title ?? "not specified"}\n\nMeeting transcript:\n{transcript.RawText}";

        Exception? lastParseException = null;
        for (var attempt = 0; attempt <= MaxJsonRegenerationAttempts; attempt++)
        {
            var stdout = await RunCopilotWithRetryAsync(prompt).ConfigureAwait(false);
            var jsonText = ExtractJson(stdout);
            try
            {
                return ParseTranscriptAnalysis(jsonText);
            }
            catch (Exception ex)
            {
                lastParseException = ex;
                if (attempt < MaxJsonRegenerationAttempts)
                {
                    _logger.Warn($"Copilot CLI response was not valid structured JSON (attempt {attempt + 1}); regenerating.");
                }
            }
        }

        _logger.Error("Copilot CLI response was not valid structured JSON after all regeneration attempts.", lastParseException ?? new Exception("Unknown error"));
        throw new InvalidOperationException("Copilot CLI never returned valid structured JSON after retries.", lastParseException);
    }

    private async Task<string> RunCopilotWithRetryAsync(string prompt)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= BackoffSecondsSchedule.Length; attempt++)
        {
            try
            {
                var (exitCode, stdout, stderr) = await RunProcessAsync(prompt).ConfigureAwait(false);

                if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    return stdout;
                }

                lastException = new InvalidOperationException(
                    $"copilot CLI exited with code {exitCode}. stderr: {stderr}");

                if (attempt < BackoffSecondsSchedule.Length)
                {
                    _logger.Warn($"CopilotCli.analyze: attempt {attempt + 1} failed (exit {exitCode}), retrying in {BackoffSecondsSchedule[attempt]}s.");
                    await Task.Delay(TimeSpan.FromSeconds(BackoffSecondsSchedule[attempt])).ConfigureAwait(false);
                    continue;
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < BackoffSecondsSchedule.Length)
                {
                    _logger.Warn($"CopilotCli.analyze: attempt {attempt + 1} threw ({ex.Message}), retrying in {BackoffSecondsSchedule[attempt]}s.");
                    await Task.Delay(TimeSpan.FromSeconds(BackoffSecondsSchedule[attempt])).ConfigureAwait(false);
                    continue;
                }
            }
        }

        _logger.Error("CopilotCli.analyze: failed after retries.", lastException ?? new Exception("Unknown error"));
        throw new InvalidOperationException("copilot CLI invocation failed after retries.", lastException);
    }

    /// <summary>
    /// Reads the names of MCP servers configured in the user's global
    /// ~/.copilot/mcp-config.json (if present) so they can all be explicitly disabled for this
    /// invocation. Never throws — any read/parse failure just yields an empty list, since this is
    /// a best-effort optimization, not something the analysis should ever fail over.
    /// </summary>
    private static System.Collections.Generic.List<string> GetConfiguredMcpServerNames()
    {
        var names = new System.Collections.Generic.List<string>();
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configPath = System.IO.Path.Combine(home, ".copilot", "mcp-config.json");
            if (!System.IO.File.Exists(configPath)) return names;

            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object)
            {
                foreach (var server in servers.EnumerateObject())
                {
                    names.Add(server.Name);
                }
            }
        }
        catch
        {
            // Best-effort only; if the config can't be read/parsed, just proceed without
            // explicitly disabling anything beyond the built-ins.
        }
        return names;
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(string prompt)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--silent");
        psi.ArgumentList.Add("--no-color");
        psi.ArgumentList.Add("--no-ask-user");
        psi.ArgumentList.Add("--log-level");
        psi.ArgumentList.Add("error");
        // Disable all tools: analysis must be based only on the transcript text in the prompt,
        // never on file/bash/web access, and must never reach across to other meetings.
        psi.ArgumentList.Add("--available-tools=");
        // Also disable every MCP server (built-in + whatever the user has configured globally in
        // ~/.copilot/mcp-config.json, e.g. Jira/Confluence/Azure/Honeycomb/etc.). Even with
        // --available-tools= restricting exposure, the CLI still loads each server's static tool
        // definitions into the system prompt, which can exceed the model's context budget and
        // make every invocation fail with exit code 1 and no stderr. Pensieve never needs any
        // tool, so proactively disabling all configured servers avoids that failure mode
        // regardless of how many/which MCP servers the user has installed.
        psi.ArgumentList.Add("--disable-builtin-mcps");
        foreach (var serverName in GetConfiguredMcpServerNames())
        {
            psi.ArgumentList.Add("--disable-mcp-server");
            psi.ArgumentList.Add(serverName);
        }

        if (!string.IsNullOrWhiteSpace(_model) && !string.Equals(_model, "auto", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(_model);
        }

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to start '{_executable}'. Ensure the GitHub Copilot CLI is installed and on PATH.", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Use the synchronous parameterless WaitForExit() (via Task.Run to stay async), which is
        // documented to also wait for the redirected output/error stream readers to finish —
        // avoiding a race where the process has exited but BeginOutputReadLine callbacks haven't
        // finished flushing all buffered lines yet.
        await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);

        return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    /// <summary>
    /// Extracts a JSON object from the CLI's text output, tolerating markdown code fences or
    /// stray surrounding text in case the model doesn't follow the "JSON only" instruction exactly.
    /// </summary>
    public static string ExtractJson(string text)
    {
        text = text.Trim();

        var fenceMatch = Regex.Match(text, "```(?:json)?\\s*([\\s\\S]*?)```", RegexOptions.IgnoreCase);
        if (fenceMatch.Success)
        {
            return fenceMatch.Groups[1].Value.Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return text.Substring(start, end - start + 1);
        }

        return text;
    }

    /// <summary>Deterministically renders a speaker-labelled transcript from Fireflies API
    /// sentence data (never LLM-generated), used both as the verbatim record and as the analysis
    /// input for API-sourced transcripts.</summary>
    public static string BuildTranscriptText(Transcript transcript)
    {
        var sb = new StringBuilder();
        foreach (var sentence in transcript.Sentences.OrderBy(s => s.Index))
        {
            var speaker = string.IsNullOrWhiteSpace(sentence.SpeakerName) ? "not specified" : sentence.SpeakerName;
            sb.AppendLine($"{speaker}: {sentence.Text}");
        }
        return sb.ToString();
    }

    public static TranscriptAnalysis ParseTranscriptAnalysis(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var analysis = new TranscriptAnalysis
        {
            Summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "",
        };

        analysis.Agreements = ReadStringArray(root, "agreements");
        analysis.OpenQuestions = ReadStringArray(root, "open_questions");
        analysis.Tags = ReadStringArray(root, "tags");
        analysis.Keywords = ReadStringArray(root, "keywords");

        if (root.TryGetProperty("next_actions", out var actionsEl) && actionsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in actionsEl.EnumerateArray())
            {
                var action = new ActionItem
                {
                    Task = a.TryGetProperty("task", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "",
                    Owner = a.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(o.GetString()) ? o.GetString()! : "not specified",
                    Due = a.TryGetProperty("due", out var due) && due.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(due.GetString()) ? due.GetString()! : "not specified",
                };
                analysis.NextActions.Add(action);
            }
        }

        if (root.TryGetProperty("diagrams", out var diagramsEl) && diagramsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in diagramsEl.EnumerateArray())
            {
                var mermaid = d.TryGetProperty("mermaid", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(mermaid)) continue; // never fabricate an empty diagram

                analysis.Diagrams.Add(new DiagramItem
                {
                    Title = d.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()) ? t.GetString()! : "Diagram",
                    Mermaid = mermaid.Trim(),
                });
            }
        }

        if (root.TryGetProperty("speaker_quality", out var qualityEl) && qualityEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qualityEl.EnumerateArray())
            {
                var speaker = q.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(speaker)) continue; // never fabricate an unnamed speaker

                analysis.SpeakerQuality.Add(new SpeakerQualityRating
                {
                    Speaker = speaker.Trim(),
                    Clarity = ClampRating(q, "clarity"),
                    Informativeness = ClampRating(q, "informativeness"),
                    Engagement = ClampRating(q, "engagement"),
                    Rationale = q.TryGetProperty("rationale", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "",
                });
            }
        }

        return analysis;
    }

    private static int ClampRating(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var val)) return 1;

        double raw;
        if (val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out var d))
        {
            raw = d;
        }
        else if (val.ValueKind == JsonValueKind.String && double.TryParse(val.GetString(), out var parsed))
        {
            raw = parsed;
        }
        else
        {
            return 1;
        }

        var rounded = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, 1, 5);
    }

    private static System.Collections.Generic.List<string> ReadStringArray(JsonElement root, string property)
    {
        var result = new System.Collections.Generic.List<string>();
        if (root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    result.Add(item.GetString()!);
                }
            }
        }
        return result;
    }
}
