using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Multiplayer.Common.Desyncs;

public record RecentCommand(int Tick, string Type, int MapId, int PlayerId, bool IssuedBySelf, string? Description);

/// <summary>
/// Keeps the last <see cref="Capacity"/> executed commands so a desync report can show what players did before it.
/// </summary>
public class RecentCommandLog(int capacity = RecentCommandLog.DefaultCapacity)
{
    public const int DefaultCapacity = 100;

    public int Capacity { get; } = capacity;
    private readonly Queue<RecentCommand> commands = new();

    public void Add(RecentCommand cmd)
    {
        commands.Enqueue(cmd);
        while (commands.Count > Capacity)
            commands.Dequeue();
    }

    public List<RecentCommand> Snapshot() => commands.ToList();
}

/// <summary>
/// A structured, self-describing summary of one desync, written as summary.md into each desync zip.
/// The "Key facts" block is machine-readable (key: value lines) so reports can be indexed and grouped in bulk.
/// </summary>
public class DesyncReport
{
    public const int FormatVersion = 1;
    private const string FactsHeader = "## Key facts";
    private const int SignatureFrames = 8;

    public DateTime CreatedUtc;
    public string DesyncMessage = "";
    public string MpVersion = "";
    public string GameVersion = "";
    public List<string> ActiveExpansions = [];
    public int ModCount;
    public bool IsHost;
    public bool ArbiterPlaying;
    public bool AsyncTime;
    public bool Multifaction;
    public int PlayerCount;
    public int MapCount;
    public int LastValidTick;
    public int OpinionStartTick;
    public int DiffAt;
    public bool DiffAtFound;
    public int LocalTraceCount;
    public int RemoteTraceCount;
    public string? FirstDivergingTraceInfo;
    public List<string> FirstDivergingStack = [];
    public List<RecentCommand> RecentCommands = [];

    /// <summary>
    /// Stable id for "the same desync": the kind of mismatch plus the top frames of the first diverging trace.
    /// Numbers, IL offsets and assembly ids are stripped so the same bug hashes the same across sessions.
    /// </summary>
    public string Signature
    {
        get
        {
            var parts = new List<string> { NormalizeMessage(DesyncMessage) };
            parts.AddRange(FirstDivergingStack.Take(SignatureFrames).Select(NormalizeFrame));
            return Fnv1a(string.Join("\n", parts)).ToString("x8");
        }
    }

    public string TopFrame => FirstDivergingStack.Select(NormalizeFrame).FirstOrDefault(f => f.Length > 0) ?? "";

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Multiplayer desync report");
        sb.AppendLine();
        sb.AppendLine(FactsHeader);
        sb.AppendLine("```");
        foreach (var (key, value) in Facts())
            sb.AppendLine($"{key}: {value}");
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## Where the clients diverged");
        if (DiffAt == -1)
            sb.AppendLine("Trace hashes matched, so the divergence happened in code that isn't traced (see local_traces.txt for context).");
        else if (!DiffAtFound)
            sb.AppendLine("Trace counts differ but the shared prefix matches: one side ended the tick sooner.");
        else
            sb.AppendLine($"First differing trace index: {DiffAt}");
        sb.AppendLine();
        if (FirstDivergingTraceInfo != null)
        {
            sb.AppendLine(DiffAt == -1
                ? "Last traced call before the divergence (the bug is in untraced code after it):"
                : "First diverging trace (local side):");
            sb.AppendLine("```");
            sb.AppendLine(FirstDivergingTraceInfo);
            foreach (var frame in FirstDivergingStack)
                sb.AppendLine(frame);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine($"## Last {RecentCommands.Count} commands before the desync");
        if (RecentCommands.Count == 0)
        {
            sb.AppendLine("None recorded.");
        }
        else
        {
            sb.AppendLine("| tick | type | map | player | self | description |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var c in RecentCommands)
                sb.AppendLine($"| {c.Tick} | {c.Type} | {c.MapId} | {c.PlayerId} | {(c.IssuedBySelf ? "yes" : "")} | {EscapeCell(c.Description)} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Other files in this report");
        sb.AppendLine("- desync_info: hardware and session details");
        sb.AppendLine("- local_traces.txt / host_traces.txt: traces around the divergence point on each side");
        sb.AppendLine("- local_jitted_methods.txt / host_jitted_methods.txt: methods compiled at the time, useful for spotting mod differences");
        sb.AppendLine("- local_logs.txt: the game log (paths and connection details redacted)");
        sb.AppendLine("- local_metadata.txt: loaded mods and Harmony patches");
        sb.AppendLine("- replay.rwmts: replay of the session, if enabled in settings");

        return sb.ToString();
    }

    private IEnumerable<(string, string)> Facts()
    {
        yield return ("format", FormatVersion.ToString());
        yield return ("signature", Signature);
        yield return ("created_utc", CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        yield return ("desync", OneLine(DesyncMessage));
        yield return ("top_frame", TopFrame);
        yield return ("mp_version", MpVersion);
        yield return ("game_version", GameVersion);
        yield return ("dlcs", ActiveExpansions.Count == 0 ? "none" : string.Join(", ", ActiveExpansions));
        yield return ("mod_count", ModCount.ToString());
        yield return ("role", IsHost ? "host" : "client");
        yield return ("players", PlayerCount.ToString());
        yield return ("maps", MapCount.ToString());
        yield return ("async_time", AsyncTime.ToString());
        yield return ("multifaction", Multifaction.ToString());
        yield return ("arbiter", ArbiterPlaying.ToString());
        yield return ("last_valid_tick", LastValidTick.ToString());
        yield return ("opinion_start_tick", OpinionStartTick.ToString());
        yield return ("diff_at", DiffAt.ToString());
        yield return ("traces_local", LocalTraceCount.ToString());
        yield return ("traces_remote", RemoteTraceCount.ToString());
    }

    /// <summary>Reads the Key facts block back out of a summary.md.</summary>
    public static Dictionary<string, string> ParseFacts(string markdown)
    {
        var facts = new Dictionary<string, string>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int i = Array.IndexOf(lines, FactsHeader);
        if (i < 0 || i + 1 >= lines.Length || lines[i + 1] != "```") return facts;

        for (i += 2; i < lines.Length && lines[i] != "```"; i++)
        {
            int sep = lines[i].IndexOf(": ", StringComparison.Ordinal);
            if (sep > 0) facts[lines[i].Substring(0, sep)] = lines[i].Substring(sep + 2);
        }

        return facts;
    }

    public static string NormalizeMessage(string message) => Regex.Replace(OneLine(message), @"\d+", "#");

    // "at Verse.AI.JobDriver.ReadyForNextToil () [0x00012] in <c847e073...>:0" => "Verse.AI.JobDriver.ReadyForNextToil ()"
    public static string NormalizeFrame(string frame)
    {
        var f = frame.Trim();
        if (f.StartsWith("at ")) f = f.Substring(3);
        int il = f.IndexOf(" [0x", StringComparison.Ordinal);
        if (il >= 0) f = f.Substring(0, il);
        int loc = f.IndexOf(" in <", StringComparison.Ordinal);
        if (loc >= 0) f = f.Substring(0, loc);
        return f.Trim();
    }

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string EscapeCell(string? s) => s == null ? "" : OneLine(s).Replace("|", "\\|");

    private static uint Fnv1a(string s)
    {
        uint hash = 2166136261;
        foreach (char c in s)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }
}
