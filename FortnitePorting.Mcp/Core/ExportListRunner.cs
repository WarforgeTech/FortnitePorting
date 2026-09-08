using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FortnitePorting.Exporting;
using FortnitePorting.Mcp.Config;
using Serilog;
using EMeshFormat = CUE4Parse_Conversion.Options.EMeshFormat;
using IoPath = System.IO.Path;

namespace FortnitePorting.Mcp.Core;

/// <summary>One line of the input list. Only <see cref="ObjectPath"/> is required.</summary>
public class ExportListItem
{
    /// <summary>Full "package.object" path, exactly as search_assets returns it.</summary>
    public string? ObjectPath { get; set; }

    /// <summary>Optional grouping folder: the asset lands in &lt;outputDir&gt;/&lt;category&gt;/&lt;name&gt;/.</summary>
    public string? Category { get; set; }

    /// <summary>Optional folder name, used instead of loading the asset for its in-game display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Optional explicit folder name. Wins over <see cref="DisplayName"/> and the asset's own name.</summary>
    public string? Folder { get; set; }
}

public record ExportListOptions
{
    public required string ListPath { get; init; }
    public required string OutputDir { get; init; }
    public EMeshFormat MeshFormat { get; init; } = EMeshFormat.UEFormat;
    public EImageFormat ImageFormat { get; init; } = EImageFormat.PNG;
    public ESoundFormat SoundFormat { get; init; } = ESoundFormat.WAV;
    public bool ExportMaterials { get; init; } = true;
    public StyleSelection? Styles { get; init; }

    /// <summary>Skip any entry whose folder already holds a manifest.json.</summary>
    public bool Resume { get; init; }
}

/// <summary>Per-entry row of export_report.json.</summary>
public class ExportListEntryReport
{
    /// <summary>1-based position in the input list, so a row can be matched back to it.</summary>
    public int Index { get; set; }

    public string ObjectPath { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>Absolute folder this asset was exported into. Null only if naming itself failed.</summary>
    public string? Folder { get; set; }

    /// <summary>pending | ok | partial | failed | skipped.</summary>
    public string Status { get; set; } = "pending";

    public double Seconds { get; set; }
    public int FileCount { get; set; }
    public long Bytes { get; set; }
    public string? ManifestPath { get; set; }
    public string? PrimaryMesh { get; set; }
    public string? PrimaryMeshPath { get; set; }
    public List<string> Files { get; set; } = [];
    public List<string> MissingArtifacts { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>
/// export_report.json: the machine-readable state of a bulk run, rewritten after every asset so it
/// is both a progress file while the run is going and the resume ledger afterwards.
/// </summary>
public class ExportListReport
{
    public string Mode { get; set; } = "export-list";
    public string ListPath { get; set; } = string.Empty;
    public string OutputRoot { get; set; } = string.Empty;
    public string MeshFormat { get; set; } = string.Empty;
    public string ImageFormat { get; set; } = string.Empty;
    public string? Styles { get; set; }
    public bool Resume { get; set; }

    public string StartedUtc { get; set; } = string.Empty;
    public string UpdatedUtc { get; set; } = string.Empty;

    /// <summary>True while the run is in flight - a report left true means the process died mid-run.</summary>
    public bool Running { get; set; } = true;

    public double ElapsedSeconds { get; set; }

    /// <summary>Projected seconds remaining, from the mean time of the entries actually exported so far.</summary>
    public double? EtaSeconds { get; set; }

    public int Total { get; set; }
    public int Processed { get; set; }
    public int Ok { get; set; }
    public int Partial { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }

    public List<ExportListEntryReport> Entries { get; set; } = [];
}

/// <summary>
/// Bulk export for kitbash lists: hundreds of object paths exported sequentially inside ONE process
/// so the archive is mounted once, each into its own folder, with a report written after every
/// asset so an interrupted run can be resumed instead of restarted.
/// <para>
/// Deliberately sequential: <see cref="ExportRunner"/> serializes exports anyway (the CUE4Parse
/// conversion path keeps static caches that are not thread-safe), so parallelism here would buy
/// nothing but a harder-to-read progress log.
/// </para>
/// </summary>
public class ExportListRunner(HeadlessLoader loader, ExportRunner runner)
{
    public const string ReportFileName = "export_report.json";
    private const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions ListOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Reads the list file. Accepts a bare array (the documented shape) or an object wrapping one
    /// under "assets"/"entries"/"items", because that is what a hand-written list tends to become.
    /// </summary>
    public static List<ExportListItem> ReadList(string listPath)
    {
        var json = File.ReadAllText(listPath);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var root = document.RootElement;
        if (root.ValueKind is JsonValueKind.Object)
        {
            foreach (var name in new[] { "assets", "entries", "items", "list" })
            {
                if (root.TryGetProperty(name, out var array) && array.ValueKind is JsonValueKind.Array)
                {
                    root = array;
                    break;
                }
            }
        }

        if (root.ValueKind is not JsonValueKind.Array)
            throw new InvalidDataException(
                "The list file must be a JSON array of objects with at least an \"objectPath\" property "
                + "(or an object carrying that array under \"assets\").");

        var items = root.Deserialize<List<ExportListItem>>(ListOptions) ?? [];
        return items;
    }

    public async Task<ExportListReport> RunAsync(ExportListOptions options, CancellationToken token = default)
    {
        var items = ReadList(options.ListPath);
        var outputRoot = IoPath.GetFullPath(options.OutputDir);
        Directory.CreateDirectory(outputRoot);

        var reportPath = IoPath.Combine(outputRoot, ReportFileName);
        var previous = options.Resume ? TryReadPreviousReport(reportPath) : null;

        var started = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();

        var report = new ExportListReport
        {
            ListPath = IoPath.GetFullPath(options.ListPath),
            OutputRoot = outputRoot,
            MeshFormat = options.MeshFormat.ToString(),
            ImageFormat = options.ImageFormat.ToString(),
            Styles = options.Styles is null ? null : options.Styles.All ? "all" : string.Join(", ", options.Styles.ByChannel.Select(pair => $"{pair.Key}={pair.Value}")),
            Resume = options.Resume,
            StartedUtc = started.ToString("O"),
            UpdatedUtc = started.ToString("O"),
            Total = items.Count
        };

        var baseOptions = new ExportOptions
        {
            MeshFormat = options.MeshFormat,
            ImageFormat = options.ImageFormat,
            SoundFormat = options.SoundFormat,
            ExportMaterials = options.ExportMaterials,
            Styles = options.Styles,
            ManifestFileName = ManifestFileName
        };

        // One namer per parent directory keeps "Wall" and "Wall_2" allocation deterministic, which is
        // what lets a resumed run recognise the folders the first run created.
        var folders = new FolderNamer();
        var exportedSeconds = 0.0;
        var exportedCount = 0;

        WriteReport(reportPath, report);

        for (var i = 0; i < items.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var item = items[i];
            var entry = new ExportListEntryReport
            {
                Index = i + 1,
                ObjectPath = item.ObjectPath ?? string.Empty,
                Category = string.IsNullOrWhiteSpace(item.Category) ? null : item.Category,
                DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? null : item.DisplayName
            };

            report.Entries.Add(entry);

            var entryClock = Stopwatch.StartNew();
            try
            {
                await ProcessEntryAsync(item, entry, outputRoot, folders, baseOptions, options, previous, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // A single bad entry must never take the run down: hundreds of assets are in flight.
                entry.Status = "failed";
                entry.Error = $"{e.GetType().Name}: {e.Message}";
                Log.Debug(e, "export-list entry {Index} threw", entry.Index);
            }

            entry.Seconds = entry.Status is "skipped" ? 0 : Math.Round(entryClock.Elapsed.TotalSeconds, 2);

            if (entry.Status is not "skipped")
            {
                exportedSeconds += entryClock.Elapsed.TotalSeconds;
                exportedCount++;
            }

            Tally(report, clock.Elapsed);

            // ETA from exported entries only - skipped ones cost nothing and would flatter it.
            var remaining = items.Count - (i + 1);
            report.EtaSeconds = exportedCount > 0 && remaining > 0
                ? Math.Round(exportedSeconds / exportedCount * remaining, 1)
                : remaining == 0 ? 0 : null;

            WriteReport(reportPath, report);

            Log.Information("[{Index}/{Total}] {Status,-7} {Name} - {Files} file(s), {Seconds:N1}s (elapsed {Elapsed:N0}s, ETA {Eta})",
                entry.Index, items.Count, entry.Status, entry.DisplayName ?? entry.ObjectPath,
                entry.FileCount, entry.Seconds, clock.Elapsed.TotalSeconds,
                report.EtaSeconds is { } eta ? $"{eta:N0}s" : "n/a");

            if (entry.Error is not null)
                Log.Warning("[{Index}/{Total}] {Path}: {Error}", entry.Index, items.Count, entry.ObjectPath, entry.Error);
        }

        report.Running = false;
        report.EtaSeconds = 0;
        Tally(report, clock.Elapsed);
        WriteReport(reportPath, report);

        return report;
    }

    // ---------------------------------------------------------------- internals

    private async Task ProcessEntryAsync(
        ExportListItem item, ExportListEntryReport entry, string outputRoot, FolderNamer folders,
        ExportOptions baseOptions, ExportListOptions options, ExportListReport? previous, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(item.ObjectPath))
        {
            entry.Status = "failed";
            entry.Error = "Entry has no \"objectPath\".";
            return;
        }

        var parent = string.IsNullOrWhiteSpace(item.Category)
            ? outputRoot
            : IoPath.Combine(outputRoot, ExportRunner.SanitizeFileName(item.Category));

        // An explicit folder/displayName names the folder without opening the package, so a resume
        // pass over an annotated list never touches the archive for an entry it is going to skip.
        var explicitName = !string.IsNullOrWhiteSpace(item.Folder) ? item.Folder
            : !string.IsNullOrWhiteSpace(item.DisplayName) ? item.DisplayName
            : null;

        // A failed load still has to consume its folder-name slot, or every later entry that shares
        // a display name would shift by one between runs and stop being resumable.
        var loadError = (string?) null;
        if (explicitName is null)
        {
            var (name, error) = await ResolveNameAsync(item.ObjectPath);
            explicitName = name ?? item.ObjectPath.SubstringAfterLastDot();
            loadError = error;
        }

        var folder = IoPath.Combine(parent, folders.Next(parent, explicitName));
        entry.Folder = folder;
        entry.DisplayName ??= IoPath.GetFileName(folder);

        if (options.Resume && File.Exists(IoPath.Combine(folder, ManifestFileName)))
        {
            entry.Status = "skipped";
            CarryForward(previous, entry);
            return;
        }

        if (loadError is not null)
        {
            entry.Status = "failed";
            entry.Error = loadError;
            return;
        }

        Directory.CreateDirectory(folder);

        var result = await runner.ExportAssets([item.ObjectPath], baseOptions with { OutputDir = folder }, token);
        var asset = result.Assets.FirstOrDefault();

        if (asset is not null)
        {
            entry.DisplayName = asset.DisplayName;
            entry.FileCount = asset.Files.Count;
            entry.Bytes = asset.Bytes;
            entry.ManifestPath = asset.ManifestPath;
            entry.PrimaryMesh = asset.PrimaryMeshFile;
            entry.PrimaryMeshPath = asset.PrimaryMeshPath;
            entry.Files = asset.Files.Select(file => file.Path).ToList();
            entry.MissingArtifacts = [.. asset.MissingArtifacts];
        }

        if (result.Failures.Count > 0)
            entry.Error = string.Join(" | ", result.Failures.Select(failure => failure.Error));

        entry.Status = asset is null ? "failed"
            : result.Failures.Count > 0 || asset.MissingArtifacts.Count > 0 ? "partial"
            : "ok";

        // A folder with no manifest is not resumable, so never leave an empty one to be skipped later.
        ExportRunner.DeleteIfEmpty(folder);
    }

    /// <summary>
    /// Opens the package purely for its in-game display name, which is what names the folder. The
    /// export itself re-resolves the object; the provider caches packages, so this costs a lookup.
    /// </summary>
    private async Task<(string? Name, string? Error)> ResolveNameAsync(string objectPath)
    {
        try
        {
            var asset = await loader.Provider.LoadPackageObjectAsync(ExportSession.FixPath(objectPath));
            return (ExportRunner.DisplayNameOf(asset) ?? asset.Name, null);
        }
        catch (Exception e)
        {
            return (null, $"Failed to load object: {e.Message}");
        }
    }

    /// <summary>
    /// A skipped entry should still describe what is on disk, so a resumed run's report is as
    /// complete as an uninterrupted one's. Falls back to reading the manifest's own file list when
    /// there is no previous report to copy from.
    /// </summary>
    private static void CarryForward(ExportListReport? previous, ExportListEntryReport entry)
    {
        var before = previous?.Entries.FirstOrDefault(row =>
            row.Folder is not null && entry.Folder is not null
            && row.Folder.Equals(entry.Folder, StringComparison.OrdinalIgnoreCase));

        if (before is not null)
        {
            entry.DisplayName = before.DisplayName ?? entry.DisplayName;
            entry.FileCount = before.FileCount;
            entry.Bytes = before.Bytes;
            entry.ManifestPath = before.ManifestPath;
            entry.PrimaryMesh = before.PrimaryMesh;
            entry.PrimaryMeshPath = before.PrimaryMeshPath;
            entry.Files = before.Files;
            entry.MissingArtifacts = before.MissingArtifacts;
            return;
        }

        try
        {
            var files = Directory.EnumerateFiles(entry.Folder!, "*", SearchOption.AllDirectories).ToList();
            entry.Files = files;
            entry.FileCount = files.Count;
            entry.Bytes = files.Sum(path => new FileInfo(path).Length);
            entry.ManifestPath = IoPath.Combine(entry.Folder!, ManifestFileName);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Tally(ExportListReport report, TimeSpan elapsed)
    {
        report.Processed = report.Entries.Count(row => row.Status is not "pending");
        report.Ok = report.Entries.Count(row => row.Status is "ok");
        report.Partial = report.Entries.Count(row => row.Status is "partial");
        report.Failed = report.Entries.Count(row => row.Status is "failed");
        report.Skipped = report.Entries.Count(row => row.Status is "skipped");
        report.TotalFiles = report.Entries.Sum(row => row.FileCount);
        report.TotalBytes = report.Entries.Sum(row => row.Bytes);
        report.ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1);
        report.UpdatedUtc = DateTime.UtcNow.ToString("O");
    }

    private static ExportListReport? TryReadPreviousReport(string reportPath)
    {
        try
        {
            if (!File.Exists(reportPath)) return null;
            return JsonSerializer.Deserialize<ExportListReport>(File.ReadAllText(reportPath), ReportOptions);
        }
        catch (Exception e)
        {
            Log.Warning("Existing {File} could not be read ({Message}); resuming from the folders on disk instead.",
                ReportFileName, e.Message);
            return null;
        }
    }

    /// <summary>
    /// Written through a temp file: the report is rewritten after every asset, so a run killed
    /// mid-write must not leave a truncated file behind as the resume ledger.
    /// </summary>
    private static void WriteReport(string reportPath, ExportListReport report)
    {
        try
        {
            var temporaryPath = reportPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(report, ReportOptions));
            File.Move(temporaryPath, reportPath, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Warning("Could not write {Path}: {Message}", reportPath, e.Message);
        }
    }
}

internal static class ExportListExtensions
{
    public static string SubstringAfterLastDot(this string value)
    {
        var index = value.LastIndexOf('.');
        return index >= 0 && index + 1 < value.Length ? value[(index + 1)..] : value;
    }
}
