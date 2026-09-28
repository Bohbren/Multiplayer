using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;
using Multiplayer.Common;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Util;
using RimWorld;
using UnityEngine;
using Verse;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace Multiplayer.Client.Desyncs;

public class SaveableDesyncInfo(
    SyncCoordinator coordinator,
    ClientSyncOpinion local,
    ClientSyncOpinion remote,
    int diffAt,
    bool diffAtFound,
    string desyncMessage)
{
    public readonly ClientSyncOpinion local = local;
    public readonly ClientSyncOpinion remote = remote;
    public readonly int diffAt = diffAt;
    private readonly Task<string> metadata = Task.Run(MetadataGenerator.Generate);
    private readonly Task<FileInfo> replay = Task.Run(SaveReplayIfApplicable);
    // Built right away on the main thread, while game state still reflects the desync
    private readonly DesyncReport report = BuildReport(coordinator, local, remote, diffAt, diffAtFound, desyncMessage);

    public bool ReadyToSave => metadata.IsCompleted && replay.IsCompleted;

    public void Save([CanBeNull] HostInfo hostInfo)
    {
        var watch = Stopwatch.StartNew();

        var zipPath = Path.Combine(Multiplayer.DesyncsDir, FindFileNameForNextDesyncFile() + ".zip");

        try
        {
            using var zip = MpZipFile.Open(zipPath, ZipArchiveMode.Create);

            zip.AddEntry(DesyncArchive.SummaryEntry, report.ToMarkdown());
            zip.AddEntry("desync_info", GetDesyncDetails());

            zip.AddEntry("local_traces.txt", GetLocalTraces());
            zip.AddEntry("host_traces.txt", hostInfo?.Traces ?? "No host traces");

            zip.AddEntry("local_jitted_methods.txt", JittedMethods.GetJittedMethodsString());
            zip.AddEntry("host_jitted_methods.txt", hostInfo?.JittedMethods ?? "No host jitted methods");

            var extraLogs = LogGenerator.PrepareLogData();
            if (extraLogs != null) zip.AddEntry("local_logs.txt", extraLogs);

            zip.AddEntry("local_metadata.txt", metadata.Result);

            try
            {
                replay.Wait();
            }
            catch (AggregateException e)
            {
                if (e.InnerExceptions.SingleOrDefault(inner => inner is TaskCanceledException) == null) throw;
            }
            if (replay.IsCompletedSuccessfully) {
                var replayFile = replay.Result;
                zip.CreateEntryFromFile(replayFile.FullName, "replay.rwmts", CompressionLevel.NoCompression);
                DeleteFileSilent(replayFile);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Exception writing desync info: {e}");
        }

        try
        {
            if (File.Exists(zipPath))
                DesyncArchive.Add(zipPath, DesyncArchiveDir, DesyncArchive.ArchiveFileName(report.CreatedUtc, report.Signature));
        }
        catch (Exception e)
        {
            Log.Warning($"Couldn't copy desync report to the archive: {e}");
        }

        Log.Message($"Desync info writing took {watch.ElapsedMilliseconds}");
    }

    public static string DesyncArchiveDir => Path.Combine(Multiplayer.DesyncsDir, "Archive");

    private static DesyncReport BuildReport(SyncCoordinator coordinator, ClientSyncOpinion local,
        ClientSyncOpinion remote, int diffAt, bool diffAtFound, string desyncMessage)
    {
        var report = new DesyncReport
        {
            CreatedUtc = DateTime.UtcNow,
            DesyncMessage = desyncMessage ?? "",
            DiffAt = diffAt,
            DiffAtFound = diffAtFound,
            LocalTraceCount = local.desyncStackTraceHashes.Count,
            RemoteTraceCount = remote.desyncStackTraceHashes.Count,
            OpinionStartTick = local.startTick,
            LastValidTick = coordinator.lastValidTick,
            RecentCommands = coordinator.recentCommands.Snapshot(),
        };

        // Each piece is best-effort: a failure here must never stop the desync zip from being written
        try
        {
            report.MpVersion = MpVersion.Version;
            report.GameVersion = VersionControl.CurrentVersionStringWithRev;
            report.ActiveExpansions = ModLister.AllExpansions
                .Where(e => !e.isCore && e.Status == ExpansionStatus.Active)
                .Select(e => e.defName)
                .ToList();
            report.ModCount = LoadedModManager.RunningModsListForReading.Count;
            report.IsHost = Multiplayer.LocalServer != null;
            report.ArbiterPlaying = Multiplayer.session.ArbiterPlaying;
            report.AsyncTime = Multiplayer.GameComp.asyncTime;
            report.Multifaction = Multiplayer.GameComp.multifaction;
            report.PlayerCount = Multiplayer.session.players.Count;
            report.MapCount = Find.Maps?.Count ?? 0;
        }
        catch (Exception e)
        {
            Log.Warning($"Partial desync summary: {e}");
        }

        try
        {
            // diffAt is -1 when every trace matched; point at the last shared trace instead
            var traceIndex = diffAt >= 0 ? diffAt : local.desyncStackTraces.Count - 1;
            var trace = local.desyncStackTraces.ElementAtOrDefault(traceIndex);
            if (trace != null)
            {
                report.FirstDivergingTraceInfo = $"#{traceIndex} Tick:{trace.tick} Hash:{trace.hash} '{trace.AdditionalInfo}'";
                report.FirstDivergingStack = trace.StackTraceString
                    .Split('\n')
                    .Select(l => l.TrimEnd('\r'))
                    .Where(l => l.Length > 0)
                    .ToList();
            }
        }
        catch (Exception e)
        {
            Log.Warning($"Couldn't read the diverging trace for the desync summary: {e}");
        }

        return report;
    }

    private string GetLocalTraces()
    {
        var traceMessage = "";
        var localCount = local.desyncStackTraceHashes.Count;
        var remoteCount = remote.desyncStackTraceHashes.Count;
        int count = Math.Min(localCount, remoteCount);

        if (diffAt == -1)
        {
            if (count == 0)
            {
                return $"No traces (remote: {remoteCount}, local: {localCount})";
            }

            traceMessage = "Note: trace hashes are equal between local and remote\n\n";
        }
        else if (!diffAtFound)
        {
            traceMessage = "Note: traces differ in amount, but the existing ones are equal. This means that a tick" +
                           " has ended sooner on one of the connection sides\n\n";
        }

        traceMessage += local.GetFormattedStackTracesForRange(diffAt);
        return traceMessage;
    }

    private string GetDesyncDetails()
    {
        var desyncInfo = new StringBuilder();

        desyncInfo
            .AppendLine("###Tick Data###")
            .AppendLine($"Arbiter Connected And Playing|||{Multiplayer.session.ArbiterPlaying}")
            .AppendLine($"Last Valid Tick - Local|||{coordinator.lastValidTick}")
            .AppendLine($"Last Valid Tick - Arbiter|||{coordinator.arbiterWasPlayingOnLastValidTick}")
            .AppendLine("\n###Version Data###")
            .AppendLine($"Multiplayer Mod Version|||{MpVersion.Version}")
            .AppendLine($"Rimworld Version and Rev|||{VersionControl.CurrentVersionStringWithRev}")
            .AppendLine("\n###Debug Options###")
            .AppendLine($"Multiplayer Debug Build - Client|||{MpVersion.IsDebug}")
            .AppendLine($"Multiplayer Debug Mode - Host|||{Multiplayer.GameComp.debugMode}")
            .AppendLine($"Rimworld Developer Mode - Client|||{Prefs.DevMode}")
            .AppendLine("\n###Server Info###")
            .AppendLine($"Player Count|||{Multiplayer.session.players.Count}")
            .AppendLine($"Async time active|||{Multiplayer.GameComp.asyncTime}")
            .AppendLine($"Multifaction active|||{Multiplayer.GameComp.multifaction}")
            .AppendLine($"Map Count|||{Find.Maps?.Count.ToStringSafe()}")
            .AppendLine("\n###CPU Info###")
            .AppendLine($"Processor Name|||{SystemInfo.processorType}")
            .AppendLine($"Processor Speed (MHz)|||{SystemInfo.processorFrequency}")
            .AppendLine($"Thread Count|||{SystemInfo.processorCount}")
            .AppendLine("\n###GPU Info###")
            .AppendLine($"GPU Family|||{SystemInfo.graphicsDeviceVendor}")
            .AppendLine($"GPU Type|||{SystemInfo.graphicsDeviceType}")
            .AppendLine($"GPU Name|||{SystemInfo.graphicsDeviceName}")
            .AppendLine($"GPU VRAM|||{SystemInfo.graphicsMemorySize}")
            .AppendLine("\n###RAM Info###")
            .AppendLine($"Physical Memory Present|||{SystemInfo.systemMemorySize}")
            .AppendLine("\n###OS Info###")
            .AppendLine($"OS Type|||{SystemInfo.operatingSystemFamily}")
            .AppendLine($"OS Name and Version|||{SystemInfo.operatingSystem}");

        return desyncInfo.ToString();
    }

    private static string FindFileNameForNextDesyncFile()
    {
        const string FilePrefix = "Desync-";
        const string FileExtension = ".zip";

        //Find all current existing desync zips
        var files = new DirectoryInfo(Multiplayer.DesyncsDir).GetFiles($"{FilePrefix}*{FileExtension}");

        const int MaxFiles = 10;

        //Delete any pushing us over the limit, and reserve room for one more
        if (files.Length > MaxFiles - 1)
            files.OrderByDescending(f => f.LastWriteTime).Skip(MaxFiles - 1).Do(DeleteFileSilent);

        //Find the current max desync number
        int max = 0;
        foreach (var f in files)
            if (int.TryParse(
                    f.Name.Substring(FilePrefix.Length, f.Name.Length - FilePrefix.Length - FileExtension.Length),
                    out int result) && result > max)
                max = result;

        return $"{FilePrefix}{max + 1:00}";
    }

    private static Task<FileInfo> SaveReplayIfApplicable()
    {
        if (!Multiplayer.settings.includeReplayInDesync) return Task.FromCanceled<FileInfo>(new CancellationToken(true));

        try
        {
            var tmp = new FileInfo(Path.Combine(Multiplayer.DesyncsDir, "desync-replay.tmp.zip"));
            if (tmp.Exists) tmp.Delete();
            Replay.ForSaving(tmp).WriteData(Multiplayer.session.dataSnapshot);
            return Task.FromResult(tmp);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save replay for desync: {e}");
            return Task.FromException<FileInfo>(e);
        }
    }

    private static void DeleteFileSilent(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (IOException)
        {
        }
    }

    public record HostInfo([CanBeNull] string Traces, [CanBeNull] string JittedMethods);
}
