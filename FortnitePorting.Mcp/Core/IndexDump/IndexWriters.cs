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

        await writer.WriteLineAsync("scopeId\tuefnPath\tregistryPrefix\ttheme\trowCount\tsampleAssetName\tverified");
        foreach (var scope in scopes)
        {
            var verified = scope.Verified.Count == 0 ? "unverified" : string.Join('+', scope.Verified);
            await writer.WriteLineAsync(string.Join('\t',
                scope.ScopeId, scope.UefnPath, scope.RegistryPrefix, scope.Theme,
                scope.RowCount.ToString(), scope.SampleAssetName, verified));
        }
    }

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

            `verified` says whether somebody actually drove the editor against that mount, and with
            which verbs (`find`, `capture`, `place`, `resolve`). **`unverified` is the default and
            means untested, not broken** - this dump can prove a path exists in the archive, never
            that UEFN will accept it.

            | scope | UEFN path | theme | rows | verified | sample vocabulary |
            | --- | --- | --- | ---: | --- | --- |

            """);

        foreach (var scope in scopes)
        {
            var verified = scope.Verified.Count == 0 ? "unverified" : string.Join(", ", scope.Verified);
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
}
