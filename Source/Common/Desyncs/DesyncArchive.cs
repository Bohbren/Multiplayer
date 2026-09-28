using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Multiplayer.Common.Util;

namespace Multiplayer.Common.Desyncs;

/// <summary>
/// Long-term storage for desync reports, and bundling them into one zip for analysis.
/// Unlike the main desync folder (which keeps only the newest few), the archive keeps reports until it hits its limits.
/// </summary>
public static class DesyncArchive
{
    public const int DefaultMaxFiles = 100;
    public const long DefaultMaxBytes = 500L * 1024 * 1024;

    public const string SummaryEntry = "summary.md";
    public const string ReplayEntry = "replay.rwmts";
    public const string IndexEntry = "INDEX.md";

    public static string ArchiveFileName(DateTime createdUtc, string signature) =>
        $"Desync-{createdUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{signature}.zip";

    /// <summary>Copies a finished desync zip into the archive, then prunes the archive to its limits.</summary>
    public static string Add(string reportZip, string archiveDir, string archiveFileName,
        int maxFiles = DefaultMaxFiles, long maxBytes = DefaultMaxBytes)
    {
        Directory.CreateDirectory(archiveDir);
        var target = Path.Combine(archiveDir, archiveFileName);
        File.Copy(reportZip, target, overwrite: true);
        Prune(archiveDir, maxFiles, maxBytes, keep: target);
        return target;
    }

    /// <summary>Deletes the oldest archived reports until both limits hold. Never deletes <paramref name="keep"/>.</summary>
    public static void Prune(string archiveDir, int maxFiles, long maxBytes, string? keep = null)
    {
        var files = new DirectoryInfo(archiveDir).GetFiles("Desync-*.zip")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal) // names start with a sortable timestamp
            .ToList();

        long total = 0;
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            bool isKept = keep != null && Path.GetFullPath(f.FullName) == Path.GetFullPath(keep);
            if (!isKept && (i >= maxFiles || total + f.Length > maxBytes))
            {
                try { f.Delete(); } catch (IOException) { }
                continue;
            }
            total += f.Length;
        }
    }

    public record BundleResult(string Path, int ReportCount, int GroupCount);

    /// <summary>
    /// Packs every archived report into one zip: reports/&lt;name&gt;/... plus an INDEX.md that groups repeat desyncs
    /// by signature. Replays are large, so they're left out unless asked for.
    /// </summary>
    public static BundleResult CreateBundle(string archiveDir, string outputZip, bool includeReplays, DateTime nowUtc)
    {
        var reports = new List<(string name, Dictionary<string, string> facts)>();

        using (var bundle = MpZipFile.Open(outputZip, ZipArchiveMode.Create))
        {
            var files = Directory.Exists(archiveDir)
                ? new DirectoryInfo(archiveDir).GetFiles("Desync-*.zip").OrderBy(f => f.Name, StringComparer.Ordinal).ToList()
                : [];

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file.Name);
                Dictionary<string, string> facts = new();

                try
                {
                    using var report = MpZipFile.Open(file.FullName, ZipArchiveMode.Read);
                    foreach (var entry in report.Entries)
                    {
                        if (!includeReplays && entry.FullName == ReplayEntry) continue;
                        var bytes = entry.GetBytes();
                        bundle.AddEntry($"reports/{name}/{entry.FullName}", bytes);
                        if (entry.FullName == SummaryEntry)
                            facts = DesyncReport.ParseFacts(Encoding.UTF8.GetString(bytes));
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException)
                {
                    facts["error"] = $"Could not read report: {e.Message}";
                }

                reports.Add((name, facts));
            }

            bundle.AddEntry(IndexEntry, Encoding.UTF8.GetBytes(BuildIndex(reports, includeReplays, nowUtc)));
        }

        int groups = reports.Select(r => r.facts.GetValueOrDefault("signature", "unknown")).Distinct().Count();
        return new BundleResult(outputZip, reports.Count, groups);
    }

    public static string BuildIndex(List<(string name, Dictionary<string, string> facts)> reports, bool includeReplays, DateTime nowUtc)
    {
        static string F(Dictionary<string, string> facts, string key) =>
            facts.TryGetValue(key, out var v) ? v.Replace("|", "\\|") : "";

        var sb = new StringBuilder();
        sb.AppendLine("# Multiplayer desync bundle");
        sb.AppendLine();
        sb.AppendLine($"Created {nowUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC. " +
                      $"{reports.Count} report(s). Replays {(includeReplays ? "included" : "not included")}.");
        sb.AppendLine();
        sb.AppendLine("Each report is under reports/<name>/. Start with its summary.md, then local_traces.txt and host_traces.txt.");
        sb.AppendLine("Reports without a summary.md were made by an older version of the mod.");
        sb.AppendLine();

        var groups = reports
            .GroupBy(r => r.facts.GetValueOrDefault("signature", "unknown"))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        sb.AppendLine("## Grouped by signature");
        sb.AppendLine("Same signature = same kind of mismatch with the same top frames, so probably the same bug.");
        sb.AppendLine();
        sb.AppendLine("| signature | count | desync | top frame | dlcs | reports |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var g in groups)
        {
            var first = g.First().facts;
            sb.AppendLine($"| {g.Key} | {g.Count()} | {F(first, "desync")} | {F(first, "top_frame")} | {F(first, "dlcs")} | " +
                          $"{string.Join(", ", g.Select(r => r.name))} |");
        }
        sb.AppendLine();

        sb.AppendLine("## All reports");
        sb.AppendLine();
        sb.AppendLine("| report | created (UTC) | role | mp version | game version | players | async | multifaction | signature |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var (name, facts) in reports)
        {
            sb.AppendLine($"| {name} | {F(facts, "created_utc")} | {F(facts, "role")} | {F(facts, "mp_version")} | " +
                          $"{F(facts, "game_version")} | {F(facts, "players")} | {F(facts, "async_time")} | " +
                          $"{F(facts, "multifaction")} | {F(facts, "signature")}{(facts.ContainsKey("error") ? " (" + F(facts, "error") + ")" : "")} |");
        }

        return sb.ToString();
    }
}
