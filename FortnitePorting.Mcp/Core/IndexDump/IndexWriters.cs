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

    /// <summary>
    /// Present ONLY when at least one of this row's identities sits in a known-missing mount, and
    /// then it names the ones that do not ("bp+sm"). <see cref="Unreachable"/> means none do - that
    /// asset cannot be reached by any route.
    /// <para>
    /// Absent is the normal case and means nothing is blocked, which is exactly why it is absent:
    /// the field would otherwise cost bytes on tens of thousands of rows just to say "fine".
    /// </para>
    /// </summary>
    [JsonPropertyName("reach")] public string? Reach { get; init; }

    /// <summary>The value of <see cref="Reach"/> when no identity is usable at all.</summary>
    public const string Unreachable = "none";

    /// <summary>Creative category, from the prop's own creative tags.</summary>
    [JsonPropertyName("cat")] public string? Cat { get; init; }

    /// <summary>
    /// Present and true when the name marks this as one unit of a larger assembly (a quarter, a
    /// half, a corner, a segment, a piece). Absent otherwise - it is never written false.
    /// </summary>
    [JsonPropertyName("frag")] public bool? Frag { get; init; }

    /// <summary>Gallery ids this prop is a member of.</summary>
    [JsonPropertyName("gal")] public List<string> Gal { get; init; } = [];

    /// <summary>
    /// HIGH-PRECISION tokens: the asset's own display name and asset name, its theme, and its
    /// creative tags. Search this first - every token here describes what the asset actually is.
    /// </summary>
    [JsonPropertyName("kw")] public List<string> Kw { get; init; } = [];

    /// <summary>
    /// RECALL tokens: the names of the galleries this prop appears in, minus anything already in
    /// <see cref="Kw"/>. Widens a search that found nothing, at the cost of precision - a gallery
    /// name lands on every one of its members, so these describe the company an asset keeps rather
    /// than the asset itself. Absent when it would add nothing.
    /// </summary>
    [JsonPropertyName("gkw")] public List<string>? Gkw { get; init; }
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

    /// <summary>
    /// Plain-English gloss for one <c>verified</c> column value, composed from its verbs.
    /// <para>
    /// Generated rather than hand-listed because the hand-written version drifted: a consumer's
    /// documentation claimed three statuses while the file shipped six, leaving an agent to guess
    /// what <c>resolve+place</c> meant and whether <c>find</c> was safe to place from.
    /// </para>
    /// </summary>
    private static string ExplainStatus(string label)
    {
        if (label == "missing")
            return "**`find_assets` returned nothing.** The mount is not exposed in the UEFN content browser at all - see below. This is the only status that is a stop sign.";

        if (label == "unverified")
            return "Nobody has asked the editor about this mount. **Untested, not broken** - this dump can prove a path exists in the archive, never that UEFN will accept it. Placing from these works in practice.";

        var verbs = label.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(verb => verb switch
        {
            "find" => "`find_assets` returned rows there",
            "capture" => "`CaptureAssetImage` rendered a **textured** preview (a grey render is recorded as `find` instead)",
            "place" => "`add_to_scene_from_asset` placed an actor",
            "resolve" => "a PPID placement auto-resolved to its creative prop blueprint",
            _ => $"`{verb}` was observed"
        });

        return "Confirmed: " + string.Join("; ", verbs) + ".";
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
        var unreachablePercent = counts.FullRows == 0
            ? 0
            : counts.UnreachableRows * 100.0 / counts.FullRows;

        var constrained = counts.PartiallyReachableRows + counts.UnreachableRows;

        var missingPreviewNote = constrained == 0
            ? "No row on this dump has an identity in a missing mount."
            : $"Measured on this dump: **{constrained:N0} of {counts.FullRows:N0} rows have at least one "
              + $"identity in a missing mount**. {counts.PartiallyReachableRows:N0} of those keep a usable "
              + $"route and carry a `reach` field naming it; **{counts.UnreachableRows:N0} "
              + $"({unreachablePercent:N1}% of all rows) keep none** and carry `\"reach\":\"none\"`. Every "
              + "other row is unconstrained and has no `reach` field at all.";

        var unreachableNote = counts.UnreachableRows == 0
            ? "No row on this dump is fully unreachable."
            : $"There are **{counts.UnreachableRows:N0}** such rows here - "
              + $"`grep '\"reach\":\"none\"' props-full.jsonl` lists them.";

        builder.Append($"""
            # Fortnite creative asset atlas

            > **Generated file.** Written by `FortnitePorting.Mcp --dump-index` against Fortnite
            > {gameVersion}. Hand-tuning the prose is welcome and expected; the tables below are
            > regenerated on every dump, so put durable notes in their own section at the bottom.

            This dataset exists so an agent holding **only the stock UEFN editor MCP** can get from a
            human sentence ("put a low hedge along the path") to a placed actor, without a catalogue
            tool and without guessing asset names.

            ## The flow

            1. **Human words -> rows. Search `kw` first, `gkw` only if that fails.** `kw` holds
               lowercase tokens from the asset's OWN name, its theme and its creative tags, so a
               `kw` hit means the asset really is the thing you asked for even when it is called
               `BP_Helios_JuniperHedge_Straight`. `gkw` holds the names of the galleries it appears
               in - pure recall, and imprecise by construction, because a gallery called
               `..._Prefab_Greenhouse` puts `greenhouse` on all 111 of its members including a
               boombox and a plate of tacos. Compound names are indexed both ways: `PrincessCastle`
               is searchable as `princess`, as `castle` and as `princesscastle`.
               **If the row has a `reach` field, read it first** - it tells you which of the steps
               below are actually available for that asset.
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
            | **Reachability is per-identity.** An identity works only if its own scope is not `missing` - and a missing mount blocks **placement too**, not just search. | Probed 2026-09-01: placing a PPID under `/Suburban_Composition` returned "Could not load asset at path". Check the row's `reach` field before you try anything. |

            ## When a search returns nothing

            Do not fall back to grepping `name`. Asset names are engineering names and a `name`
            scan is how you end up scrolling a set you found by luck. Widen in this order, and stop
            at the first step that returns something:

            1. **Drop to one token.** Intersecting two plausible words is the single most common way
               to get zero - `glass dome`, `greenhouse glass` and `farm plot` all return nothing
               while `dome`, `gazebo` and `plot` return plenty. Search one word, then narrow.
            2. **Try `gkw`.** The gallery vocabulary knows words the asset's own name does not. This
               is the step that turns "Fortnite has no greenhouse" into a list of greenhouse-adjacent
               props.
            3. **Try a broader physical noun.** The index names what a thing IS, not what it is for:
               a glass dome is a `gazebo`, a plaza tile is a `floor`, a planting bed is a
               `flowerbed`. Colour is not indexed at all - there is no `orange` or `terracotta`
               token - so search the material or set name instead (`claytile`, `stone`, `marble`).
            4. **Scan this atlas's scope table.** The `sample vocabulary` column is the highest-
               frequency words under each mount; reading it sideways tells you which theme owns the
               look you want, and `theme` gives you the search term.
            5. **List a gallery, then a sibling folder.** Pick a promising row, grep its `gal` ids to
               see what ships alongside it, and only then use `find_assets(folder_path=...)` on the
               folder half of its `sm` to enumerate the rest of that set.

            ## Fragments

            A row with `"frag":true` is one unit of a larger assembly - a quarter shell, a half
            arch, a corner piece, a wall segment. **Its `sz` is the fragment's own bounds, not the
            size of the assembled shape**, and the index carries no pivot or origin data, so you
            cannot compute the assembly offsets from it.

            Place ONE unit, look at it with `CaptureViewport`, and work out the transform from what
            you see before you repeat it. Radial assemblies especially do not fall out of `sz`
            arithmetic: four quarter-domes co-located at four yaws pinwheel, and the same four
            offset by `sz/2` explode. This flag is a name heuristic (`Quarter`, `Half`, `Corner`,
            `Seg`, `Piece`), so it finds the obvious cases and will miss a fragment named something
            else - absence of `frag` is not proof of wholeness.

            ## Previews mislead, in both directions

            `CaptureAssetImage` is reliable for **silhouette and geometry** and unreliable for
            **colour and material**. This is not only the grey-render case that `scopes.tsv` notes;
            measured examples run both ways:

            * A greenhouse wall previewed as blue tinted glass and is an opaque grey-tan wall in
              the level. Chosen on the preview, it was the weakest element in the scene.
            * A clay tile floor previewed as pale planks and renders strong orange-red in the level.
              It was nearly rejected on the preview and was the best match in the index.
            * An archway previewed flat grey and is white marble with a gold emblem.
            * A hedge previewed plain green and carries pink flowers.

            So: use the preview to choose a **shape**, then **place one candidate and
            `CaptureViewport` it** before duplicating it across a scene. The in-level render is the
            only thing that tells you what an asset actually looks like.

            ## Sizes

            `sz` is `[x, y, z]` in whole centimetres, read from the static mesh's render bounds - the
            true visual size of the thing you are about to place. A row with no `sz` is one whose
            blueprint exposed no static mesh (particle-only props, splines, volumes); it is still
            placeable, its size is just not knowable from the archive.

            **`sz` is render bounds, not a tiling pitch.** For upright pieces - walls, hedges, fences
            - they coincide, and a run spaced at `sz.x` lands flush (a 25-piece hedge perimeter
            spaced at 428 read back at exactly 428). For floor and ground pieces they often do not:
            the mesh's footprint and its pivot disagree, and a grid laid on `sz` comes out
            staggered. Place two, measure the gap, then commit to the grid.

            **A row can have `sz` but no `sm`.** When every mesh a blueprint exposes is a shadow
            proxy, `sm` is deliberately null: a proxy measures correctly but `CaptureAssetImage`
            renders it as a featureless blob, which is worse than showing nothing. The size is still
            reported from the proxy, and `dump-report.log` names the mesh that was suppressed. Place
            these by `ppid` and skip the preview.

            ## Scopes

            The `verified` column is a **status, not a boolean**, recording what the live editor
            actually did when somebody drove it against that mount ({verifiedCount} verified,
            {missingCount} missing, {unverifiedCount} unverified here).

            This table is generated from the file, so it lists every value `scopes.tsv` actually
            contains and cannot drift out of date:

            | value | scopes | what it means |
            | --- | ---: | --- |

            """);

        foreach (var (label, count) in scopes
                     .GroupBy(scope => scope.VerifiedLabel, StringComparer.Ordinal)
                     .Select(group => (Label: group.Key, Count: group.Count()))
                     .OrderByDescending(entry => entry.Count)
                     .ThenBy(entry => entry.Label, StringComparer.Ordinal))
        {
            builder.AppendLine($"| `{label}` | {count} | {ExplainStatus(label)} |");
        }

        builder.Append($"""

            **What a status licenses.** Only `missing` is a stop sign. Everything else - including
            `unverified` - is safe to place from: a rehearsal placed 67 props across seven mounts,
            most of them `unverified`, with zero failures. Treat the verbs as telling you what has
            been *confirmed*, not what is *permitted*.

            `scopes.tsv` carries a `note` column with the operator's verbatim reason for each
            non-default status; the table below omits it for width.

            ### When a scope is `missing`

            **Reachability is per-identity. An identity works only if ITS OWN scope is not
            `missing`.** A missing mount does not merely hide its assets from the content browser -
            it makes every identity under it unusable, placement included.

            This was probed on 2026-09-01, and it is why the rule is stated this strongly rather
            than hedged: `add_to_scene_from_asset` on a PPID under `/Suburban_Composition` returned
            **"Could not load asset at path"**. It did not place. The earlier assumption - that a
            PPID resolves an object path directly and so sidesteps the browser - is wrong for a
            mount UEFN has not loaded at all.

            So, for any row:

            * **Check each identity against its own scope**, not against the row's `sc`. A row's
              `ppid`, `bp` and `sm` routinely live in three different mounts.
            * **The `reach` field does this for you.** It appears only on rows where something is
              blocked, and names the identities that still work (`"bp+sm"`, `"sm"`). No `reach`
              field means nothing is blocked.
            * **`"reach":"none"` means the asset is unavailable to creators this season.** Every
              route into it is in a mount UEFN does not expose. **Do not attempt placement** - it
              will fail the way the probe did. {unreachableNote}
            * **Do not search or capture at a missing path.** `find_assets` returns nothing there
              and `CaptureAssetImage` has nothing to render. An empty result is the mount being
              absent, not the asset being absent.

            {missingPreviewNote}

            One caveat on reading `reach`: it rules routes **out**, it does not promise the rest
            work. An identity in an `unverified` mount is untested, not proven good.

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

    /// <summary>Rows with at least one identity in a missing mount, but at least one still usable.</summary>
    public int PartiallyReachableRows { get; init; }

    /// <summary>Rows where EVERY identity sits in a missing mount - unavailable by any route.</summary>
    public int UnreachableRows { get; init; }
}
