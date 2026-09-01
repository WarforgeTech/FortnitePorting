using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FortnitePorting.Mcp.Core.IndexDump;

/// <summary>
/// One placeable prop, in the shortest form that still answers the customer agent's three
/// questions: what do I search for, what exact string do I place, and how big is it.
/// <para>
/// Keys are terse because the file is designed to be grepped whole - a full dump is hundreds of
/// thousands of lines and every byte of key name is paid for on each one.
/// </para>
/// </summary>
public sealed record PropRow
{
    /// <summary>Stable row id, also the join key used by <see cref="GalleryRow"/>.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary>Human display name, as shown in the creative inventory.</summary>
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>Prop item definition, full UEFN object path. PLACE THIS - it auto-resolves to the prop BP.</summary>
    [JsonPropertyName("ppid")] public required string Ppid { get; init; }

    /// <summary>Blueprint CLASS path with the "_C" suffix. The bare package path will not load.</summary>
    [JsonPropertyName("bp")] public string? Bp { get; init; }

    /// <summary>Static mesh package path. The only one of the three CaptureAssetImage renders.</summary>
    [JsonPropertyName("sm")] public string? Sm { get; init; }

    /// <summary>Static mesh render bounds, X/Y/Z in whole centimetres. Null when no mesh resolved.</summary>
    [JsonPropertyName("sz")] public int[]? Sz { get; init; }

    /// <summary>Scope id this row lives in; joins to scopes.tsv.</summary>
    [JsonPropertyName("sc")] public string? Sc { get; init; }

    /// <summary>Creative category, from the prop's own creative tags.</summary>
    [JsonPropertyName("cat")] public string? Cat { get; init; }

    /// <summary>Gallery ids this prop is a member of.</summary>
    [JsonPropertyName("gal")] public List<string> Gal { get; init; } = [];

    /// <summary>Lowercase search tokens: name, asset name, gallery names, theme, creative tags.</summary>
    [JsonPropertyName("kw")] public List<string> Kw { get; init; } = [];
}

/// <summary>One creative gallery. Members are found by grepping props-full.jsonl for the gallery id.</summary>
public sealed record GalleryRow
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>Full UEFN object path of the FortPlaysetItemDefinition.</summary>
    [JsonPropertyName("asset")] public required string Asset { get; init; }

    [JsonPropertyName("sc")] public string? Sc { get; init; }

    /// <summary>How many canonical prop rows carry this gallery id.</summary>
    [JsonPropertyName("n")] public int N { get; init; }

    /// <summary>Where the member list came from: "associated" or "saveRecords".</summary>
    [JsonPropertyName("src")] public string? Src { get; init; }

    [JsonPropertyName("kw")] public List<string> Kw { get; init; } = [];
}

/// <summary>A UEFN mount prefix worth handing to find_assets, plus whether anyone has proven it works.</summary>
public sealed record ScopeRow
{
    public required string ScopeId { get; init; }
    public required string UefnPath { get; init; }
    public required string RegistryPrefix { get; init; }
    public required string Theme { get; init; }
    public required int RowCount { get; init; }
    public required string SampleAssetName { get; init; }
    public required IReadOnlyList<string> Verified { get; init; }

    /// <summary>What the live editor said about this mount, if anyone asked it.</summary>
    public required MountStatus Status { get; init; }

    /// <summary>The operator's note from the verification file, verbatim.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The single word the <c>verified</c> column carries. It is a status, not a boolean: a mount
    /// the editor answered for reports which verbs answered, one that answered "nothing here"
    /// reports <c>missing</c>, and one nobody asked reports <c>unverified</c>.
    /// </summary>
    public string VerifiedLabel => Status switch
    {
        MountStatus.Missing => "missing",
        MountStatus.Verified when Verified.Count > 0 => string.Join('+', Verified),
        MountStatus.Verified => "verified",
        _ => "unverified"
    };

    /// <summary>Highest-frequency display-name tokens under this scope; the atlas's vocabulary column.</summary>
    public IReadOnlyList<string> Vocabulary { get; init; } = [];
}

/// <summary>Serialisers for the index dataset. Every file is written deterministically.</summary>
public static class IndexWriters
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions RowOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Paths contain no HTML-hostile characters and this file is read by greps, not browsers.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions MetaOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>One JSON object per line, UTF-8, LF endings, in the order given.</summary>
    public static async Task<int> WriteJsonLinesAsync<T>(string path, IEnumerable<T> rows)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };

        var count = 0;
        foreach (var row in rows)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, RowOptions));
            count++;
        }

        return count;
    }

    public static async Task WriteScopesAsync(string path, IReadOnlyList<ScopeRow> scopes)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };

        // The note column is what tells a reader WHY a scope reads the way it does - "grey render,
        // may be an unloaded material" and "find_assets returned 0 rows" are very different reasons
        // to be cautious, and neither survives being compressed into the status word.
        await writer.WriteLineAsync("scopeId\tuefnPath\tregistryPrefix\ttheme\trowCount\tsampleAssetName\tverified\tnote");
        foreach (var scope in scopes)
        {
            await writer.WriteLineAsync(string.Join('\t',
                scope.ScopeId, scope.UefnPath, scope.RegistryPrefix, scope.Theme,
                scope.RowCount.ToString(), scope.SampleAssetName, scope.VerifiedLabel,
                Flatten(scope.Note)));
        }
    }

    /// <summary>A note is free text from an operator; a stray tab or newline would break the row.</summary>
    private static string Flatten(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.ReplaceLineEndings(" ").Replace('\t', ' ').Trim();

    public static async Task WriteMetaAsync(string path, object meta)
        => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(meta, MetaOptions) + "\n", new UTF8Encoding(false));

    /// <summary>
    /// The generated orientation page. It carries the placement rules that were established by
    /// driving the live editor, because every one of them is a thing an agent would otherwise have
    /// to rediscover by failing: a PPID that will not render, a blueprint path that will not load,
    /// an unscoped search that times out.
    /// </summary>
    public static async Task WriteAtlasAsync(string path, IReadOnlyList<ScopeRow> scopes, IndexCounts counts, string gameVersion)
    {
        var builder = new StringBuilder();

        var verifiedCount = scopes.Count(scope => scope.Status is MountStatus.Verified);
        var missingCount = scopes.Count(scope => scope.Status is MountStatus.Missing);
        var unverifiedCount = scopes.Count - verifiedCount - missingCount;

        // Built here rather than inline so the interpolated numbers do not wreck the paragraph's
        // line wrapping inside the raw string literal below.
        var missingPercent = counts.RowsUnderMissingMount == 0
            ? 0
            : counts.PreviewableUnderMissingMount * 100.0 / counts.RowsUnderMissingMount;

        var missingPreviewNote = counts.RowsUnderMissingMount == 0
            ? "No row on this dump has a PPID under a missing mount."
            : $"Measured on this dump: of the {counts.RowsUnderMissingMount:N0} rows whose PPID sits in a "
              + $"missing mount, **{counts.PreviewableUnderMissingMount:N0} ({missingPercent:N0}%) have an "
              + "`sm` in a mount that is not itself missing**, so a preview is reachable for those and "
              + "for those only. The remainder have no mesh at all, or a mesh in another missing mount; "
              + "for them there is no preview route and you place blind.";

        builder.Append($"""
            # Fortnite creative asset atlas

            > **Generated file.** Written by `FortnitePorting.Mcp --dump-index` against Fortnite
            > {gameVersion}. Hand-tuning the prose is welcome and expected; the tables below are
            > regenerated on every dump, so put durable notes in their own section at the bottom.

            This dataset exists so an agent holding **only the stock UEFN editor MCP** can get from a
            human sentence ("put a low hedge along the path") to a placed actor, without a catalogue
            tool and without guessing asset names.

            ## The flow

            1. **Human words -> rows.** `grep -i hedge index/props-full.jsonl`. Every row carries
               `kw`, lowercase tokens from its display name, its gallery names, its theme and its
               creative tags, so plain English hits even when the asset is called
               `BP_Helios_JuniperHedge_Straight`.
            2. **Row -> scoped search.** Often unnecessary - the row already holds exact paths. When
               you want to browse siblings, take the folder half of the row's `bp` or `sm` (or its
               scope's UEFN path from `scopes.tsv`, joined on `sc`) and call
               `find_assets(folder_path=<that folder>, name=<leaf name>)`. **Always scope the
               search.** An unscoped `find_assets` over the whole project is the documented way to
               hang the editor.
            3. **Row -> picture.** `CaptureAssetImage` on the row's `sm` (static mesh) or `bp`
               (blueprint) path. It renders live, in the editor, from loaded content.
            4. **Row -> placement.** `SceneTools.add_to_scene_from_asset` with the row's `ppid`.

            A row's three paths usually live in three different mounts: the `ppid` in a composition
            plugin (`/Burd_Comp`), the `bp` and `sm` in shared environment content
            (`/Game/Environments`). That is expected, and it is why `scopes.tsv` counts a row against
            every mount it reaches rather than only its PPID's.

            ## Hard rules

            These were established by driving the live editor, not inferred from the archive. Each
            one is a failure mode you would otherwise hit blind.

            | Rule | Why it matters |
            | --- | --- |
            | **Place the `ppid`.** The prop item definition's full object path (`/Plugin/.../PPID_X.PPID_X`) places and auto-resolves to the creative prop BP actor. | This is the preferred placement identity - one string, correct actor, no class-suffix bookkeeping. |
            | **A blueprint needs the `_C` class path.** `/path/BP_Name.BP_Name_C` places; the bare package path **fails to load**. | The `bp` column is already written in class form. Do not strip the suffix. |
            | **A static mesh path places as a `FortStaticMeshActor`.** | Fine for dressing, but it is a bare mesh - no prop behaviour, no creative interactions. Prefer the `ppid`. |
            | **`CaptureAssetImage` NEVER works on a PPID.** It works on `sm` and `bp` paths. | Use `sm` for the picture and `ppid` for the placement. They are different strings on purpose. |
            | **`GetAssetThumbnails` returns empty for unloaded plugin content.** Rendering is live-only. | Do not read an empty thumbnail result as "this asset does not exist". |
            | **Scope every `find_assets` call** with `folder_path`. | Verified working scoped, e.g. `folder_path='/Game/Environments', name='JuniperHedge'`. |

            ## Sizes

            `sz` is `[x, y, z]` in whole centimetres, read from the static mesh's render bounds - the
            true visual size of the thing you are about to place. A row with no `sz` is one whose
            blueprint exposed no static mesh (particle-only props, splines, volumes); it is still
            placeable, its size is just not knowable from the archive.

            **A row can have `sz` but no `sm`.** When every mesh a blueprint exposes is a shadow
            proxy, `sm` is deliberately null: a proxy measures correctly but `CaptureAssetImage`
            renders it as a featureless blob, which is worse than showing nothing. The size is still
            reported from the proxy, and `dump-report.log` names the mesh that was suppressed. Place
            these by `ppid` and skip the preview.

            ## Scopes

            The `verified` column is a **status, not a boolean**, recording what the live editor
            actually did when somebody drove it against that mount ({verifiedCount} verified,
            {missingCount} missing, {unverifiedCount} unverified here):

            | value | meaning |
            | --- | --- |
            | `find+capture` | `find_assets` returned rows there **and** `CaptureAssetImage` rendered a textured preview. Fully usable. |
            | `find` | `find_assets` returned rows. Capture was either not attempted or came back untextured - search works, preview quality is unconfirmed. |
            | `find+capture+place`, `resolve+place` | As above plus a confirmed placement. |
            | `missing` | **`find_assets` returned nothing.** The mount is not exposed in the UEFN content browser at all - see below. |
            | `unverified` | Nobody has asked the editor. **Untested, not broken** - this dump can prove a path exists in the archive, never that UEFN will accept it. |

            `scopes.tsv` carries a `note` column with the operator's verbatim reason for each
            non-default status; the table below omits it for width.

            ### When a scope is `missing`

            The mount is not in the content browser, so for a row whose `sc` names it:

            * **Do not search or capture at that path.** `find_assets` will return nothing there and
              `CaptureAssetImage` has nothing to render. An empty result is the mount being absent,
              not the asset being absent.
            * **Place it by its `ppid`.** Placement resolves an object path directly rather than
              going through the browser - which is how a PPID under `/Burd_Comp` places. This has
              not been probed on the missing mounts themselves, so treat it as the route to try
              rather than a guarantee.
            * **Look for the preview under the `sm`, not under `sc`.** A row's mesh and blueprint
              usually live in a different mount from its PPID (often `/Game/Environments`), and that
              mount has its own status. {missingPreviewNote}

            | scope | UEFN path | theme | rows | verified | sample vocabulary |
            | --- | --- | --- | ---: | --- | --- |

            """);

        foreach (var scope in scopes)
        {
            var verified = scope.VerifiedLabel.Replace("+", ", ");
            var vocabulary = scope.Vocabulary.Count == 0 ? "-" : string.Join(", ", scope.Vocabulary);
            builder.AppendLine($"| `{scope.ScopeId}` | `{scope.UefnPath}` | {Dash(scope.Theme)} | {scope.RowCount:N0} | {verified} | {vocabulary} |");
        }

        builder.Append($"""

            ## Files

            | file | rows | what it is |
            | --- | ---: | --- |
            | `props-full.jsonl` | {counts.FullRows:N0} | Every canonical display-named prop. This is the one you grep. |
            | `galleries.jsonl` | {counts.Galleries:N0} | Creative galleries. To list a gallery's contents, grep its `id` in `props-full.jsonl`. |
            | `scopes.tsv` | {counts.Scopes:N0} | Mount table. A row is counted against every mount it reaches (PPID, blueprint and mesh), so the totals exceed {counts.FullRows:N0}. |
            | `dump-report.log` | {counts.Failures:N0} | Per-row failures from this dump. A row with a null `bp` or `sm` has a line here saying why. |

            ## Notes

            _Hand-written notes go here; this section is not regenerated._

            """);

        await File.WriteAllTextAsync(path, builder.ToString().ReplaceLineEndings("\n"), new UTF8Encoding(false));
    }

    private static string Dash(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
}

/// <summary>Row counts shared between META.json and the atlas.</summary>
public sealed record IndexCounts
{
    public required int FullRows { get; init; }
    public required int Galleries { get; init; }
    public required int Scopes { get; init; }
    public required int Failures { get; init; }

    /// <summary>Rows whose PPID lives in a mount UEFN does not expose in its content browser.</summary>
    public int RowsUnderMissingMount { get; init; }

    /// <summary>How many of those still have a mesh in a mount that is not itself missing.</summary>
    public int PreviewableUnderMissingMount { get; init; }
}
