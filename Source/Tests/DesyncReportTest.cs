using System.IO.Compression;
using System.Text;
using Multiplayer.Common;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Util;

namespace Tests;

public class DesyncReportTest
{
    private string dir = null!;

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), "mp-desync-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(dir, recursive: true);

    private static DesyncReport MakeReport(string message = "Wrong random state on map 3", params string[] stack) => new()
    {
        CreatedUtc = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc),
        DesyncMessage = message,
        MpVersion = "0.11.5",
        GameVersion = "1.6.4871 rev1",
        ActiveExpansions = ["Royalty", "Anomaly"],
        PlayerCount = 3,
        DiffAt = 12,
        DiffAtFound = true,
        FirstDivergingTraceInfo = "#12 Tick:100 Hash:5 'info'",
        FirstDivergingStack = stack.Length > 0 ? stack.ToList() :
        [
            "at RimWorld.GameComponent_Anomaly.UpdateCorpseTrackers () [0x00042] in <c847e073cda54790b59d58357cc8cf98>:0",
            "at RimWorld.GameComponent_Anomaly.GameComponentTick () [0x00010] in <c847e073cda54790b59d58357cc8cf98>:0",
        ],
        RecentCommands =
        [
            new RecentCommand(90, "Sync", 0, 1, true, "SyncMethod Pawn_DraftController.set_Drafted"),
            new RecentCommand(95, "Designator", 0, 2, false, null),
        ],
    };

    [Test]
    public void FactsRoundTripThroughMarkdown()
    {
        var report = MakeReport();
        var facts = DesyncReport.ParseFacts(report.ToMarkdown());

        Assert.That(facts["signature"], Is.EqualTo(report.Signature));
        Assert.That(facts["dlcs"], Is.EqualTo("Royalty, Anomaly"));
        Assert.That(facts["players"], Is.EqualTo("3"));
        Assert.That(facts["created_utc"], Is.EqualTo("2026-09-28T12:00:00Z"));
        Assert.That(facts["top_frame"], Is.EqualTo("RimWorld.GameComponent_Anomaly.UpdateCorpseTrackers ()"));
    }

    [Test]
    public void MarkdownListsRecentCommands()
    {
        var md = MakeReport().ToMarkdown();
        Assert.That(md, Does.Contain("| 90 | Sync | 0 | 1 | yes | SyncMethod Pawn_DraftController.set_Drafted |"));
        Assert.That(md, Does.Contain("| 95 | Designator | 0 | 2 |  |  |"));
    }

    [Test]
    public void SignatureIgnoresNumbersAndIlOffsets()
    {
        var a = MakeReport("Wrong random state on map 3");
        var b = MakeReport("Wrong random state on map 7",
            "at RimWorld.GameComponent_Anomaly.UpdateCorpseTrackers () [0x00099] in <ffffffffffffffffffffffffffffffff>:0",
            "at RimWorld.GameComponent_Anomaly.GameComponentTick () [0x00001] in <ffffffffffffffffffffffffffffffff>:0");

        Assert.That(b.Signature, Is.EqualTo(a.Signature));
    }

    [Test]
    public void SignatureDiffersForDifferentCode()
    {
        var a = MakeReport();
        var b = MakeReport(stack: "at Verse.AI.JobDriver.ReadyForNextToil () [0x00000] in <x>:0");
        var c = MakeReport("Wrong random state for the world");

        Assert.That(b.Signature, Is.Not.EqualTo(a.Signature));
        Assert.That(c.Signature, Is.Not.EqualTo(a.Signature));
    }

    [Test]
    public void ParseFactsOfUnrelatedTextIsEmpty()
    {
        Assert.That(DesyncReport.ParseFacts("hello\nworld"), Is.Empty);
    }

    [Test]
    public void RecentCommandLogKeepsNewest()
    {
        var log = new RecentCommandLog(3);
        for (int i = 0; i < 5; i++)
            log.Add(new RecentCommand(i, "Sync", 0, 0, false, null));

        Assert.That(log.Snapshot().Select(c => c.Tick), Is.EqualTo(new[] { 2, 3, 4 }));
    }

    private string WriteReportZip(string name, DesyncReport? report, bool withReplay = true, int padding = 0)
    {
        var path = Path.Combine(dir, name);
        using var zip = MpZipFile.Open(path, ZipArchiveMode.Create);
        if (report != null) zip.AddEntry(DesyncArchive.SummaryEntry, report.ToMarkdown());
        zip.AddEntry("local_traces.txt", "traces");
        if (withReplay) zip.AddEntry(DesyncArchive.ReplayEntry, new byte[] { 1, 2, 3 });
        if (padding > 0)
        {
            // Stored uncompressed so the file size is predictable
            using var s = zip.CreateEntry("padding", CompressionLevel.NoCompression).Open();
            s.Write(new byte[padding], 0, padding);
        }
        return path;
    }

    [Test]
    public void ArchivePrunesOldestByCount()
    {
        var archive = Path.Combine(dir, "Archive");
        var src = WriteReportZip("src.zip", MakeReport());

        for (int i = 1; i <= 5; i++)
            DesyncArchive.Add(src, archive, $"Desync-2026010{i}-000000-aaaa.zip", maxFiles: 3);

        var names = Directory.GetFiles(archive).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.That(names, Is.EqualTo(new[]
        {
            "Desync-20260103-000000-aaaa.zip",
            "Desync-20260104-000000-aaaa.zip",
            "Desync-20260105-000000-aaaa.zip",
        }));
    }

    [Test]
    public void ArchivePrunesBySizeButKeepsNewest()
    {
        var archive = Path.Combine(dir, "Archive");
        var src = WriteReportZip("src.zip", MakeReport(), padding: 10_000);
        long size = new FileInfo(src).Length;

        DesyncArchive.Add(src, archive, "Desync-20260101-000000-aaaa.zip", maxBytes: size * 2);
        DesyncArchive.Add(src, archive, "Desync-20260102-000000-aaaa.zip", maxBytes: size * 2);
        DesyncArchive.Add(src, archive, "Desync-20260103-000000-aaaa.zip", maxBytes: size * 2);
        Assert.That(Directory.GetFiles(archive), Has.Length.EqualTo(2));

        // A single report bigger than the limit is still kept
        DesyncArchive.Add(src, archive, "Desync-20260104-000000-aaaa.zip", maxBytes: 1);
        Assert.That(Directory.GetFiles(archive).Select(Path.GetFileName),
            Is.EqualTo(new[] { "Desync-20260104-000000-aaaa.zip" }));
    }

    [Test]
    public void BundleGroupsReportsAndSkipsReplays()
    {
        var archive = Path.Combine(dir, "Archive");
        Directory.CreateDirectory(archive);
        var anomaly = MakeReport();
        var other = MakeReport("Wrong random state for the world");

        File.Move(WriteReportZip("a.zip", anomaly), Path.Combine(archive, "Desync-20260101-000000-a.zip"));
        File.Move(WriteReportZip("b.zip", anomaly), Path.Combine(archive, "Desync-20260102-000000-b.zip"));
        File.Move(WriteReportZip("c.zip", other), Path.Combine(archive, "Desync-20260103-000000-c.zip"));
        File.Move(WriteReportZip("d.zip", null), Path.Combine(archive, "Desync-20260104-000000-legacy.zip"));

        var output = Path.Combine(dir, "bundle.zip");
        var result = DesyncArchive.CreateBundle(archive, output, includeReplays: false, DateTime.UtcNow);

        Assert.That(result.ReportCount, Is.EqualTo(4));
        Assert.That(result.GroupCount, Is.EqualTo(3)); // anomaly x2, world, unknown (legacy)

        using var bundle = MpZipFile.Open(output, ZipArchiveMode.Read);
        var names = bundle.Entries.Select(e => e.FullName).ToList();
        Assert.That(names, Does.Contain("reports/Desync-20260101-000000-a/summary.md"));
        Assert.That(names, Does.Contain("reports/Desync-20260104-000000-legacy/local_traces.txt"));
        Assert.That(names.Where(n => n.EndsWith(DesyncArchive.ReplayEntry)), Is.Empty);

        var index = bundle.GetString(DesyncArchive.IndexEntry);
        Assert.That(index, Does.Contain($"| {anomaly.Signature} | 2 |"));
        Assert.That(index, Does.Contain($"| {other.Signature} | 1 |"));
        Assert.That(index, Does.Contain("| unknown | 1 |"));
    }

    [Test]
    public void BundleCanIncludeReplays()
    {
        var archive = Path.Combine(dir, "Archive");
        Directory.CreateDirectory(archive);
        File.Move(WriteReportZip("a.zip", MakeReport()), Path.Combine(archive, "Desync-20260101-000000-a.zip"));

        var output = Path.Combine(dir, "bundle.zip");
        DesyncArchive.CreateBundle(archive, output, includeReplays: true, DateTime.UtcNow);

        using var bundle = MpZipFile.Open(output, ZipArchiveMode.Read);
        Assert.That(bundle.GetEntry("reports/Desync-20260101-000000-a/replay.rwmts"), Is.Not.Null);
    }

    [Test]
    public void BundleOfMissingArchiveIsEmpty()
    {
        var output = Path.Combine(dir, "bundle.zip");
        var result = DesyncArchive.CreateBundle(Path.Combine(dir, "nope"), output, false, DateTime.UtcNow);

        Assert.That(result.ReportCount, Is.EqualTo(0));
        using var bundle = MpZipFile.Open(output, ZipArchiveMode.Read);
        Assert.That(bundle.GetString(DesyncArchive.IndexEntry), Does.Contain("0 report(s)"));
    }
}
