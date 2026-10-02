# FortnitePorting.Mcp

A headless [Model Context Protocol](https://modelcontextprotocol.io) server that puts the
FortnitePorting asset pipeline behind a set of tools an AI agent can drive: search the locally
installed Fortnite archive, *look* at candidate assets as images, and export meshes, textures and
sounds to disk.

It reuses FortnitePorting's real loading and exporting code (CUE4Parse provider, AES keys and
mappings from the FortnitePorting API, the full asset category table, the exporters) with every UI
concern removed. It is read-only against the game install; the only thing it writes is exports,
logs and caches under its own data directory.

---

## Requirements

* .NET 10 SDK (to build) — the published output is a normal framework-dependent exe.
* A local Fortnite install (the `...\FortniteGame\Content\Paks` folder).
* Internet access on first run, to fetch AES keys, mappings and the oodle/zlib/detex natives.
  Both can be pinned locally instead (`AesKeyOverride`, `MappingsFileOverride`).

## Build

```powershell
dotnet publish FortnitePorting.Mcp\FortnitePorting.Mcp.csproj -c Release -o publish\mcp
```

## Configuration

Config is a single JSON file, located via `--config <path>` or the `FPMCP_CONFIG` environment
variable. Every field is optional.

```jsonc
{
  // Where the game's .pak/.utoc files live.
  "ArchiveDirectory": "C:\\Program Files\\Epic Games\\Fortnite\\FortniteGame\\Content\\Paks",

  // Server-owned scratch: logs, icon cache, name index, native dependencies, default exports.
  "DataDirectory": "C:\\Users\\<you>\\AppData\\Local\\FortnitePortingMcp",

  // Default export root. Defaults to <DataDirectory>\Exports.
  "ExportRoot": "C:\\Users\\<you>\\Documents\\FortniteAssetExports",

  // Optional: pin the AES main key instead of fetching it from the FortnitePorting API.
  "AesKeyOverride": null,

  // Optional: use a local .usmap instead of the API-provided mappings.
  "MappingsFileOverride": null,

  // Localisation for display names. Default English.
  "Language": "English",

  // Mount Fortnite's IoStore on-demand containers. Default true, and you want it true:
  // cosmetic and weapon textures ship in the local paks with ONLY their lowest mip, so with
  // this off a SCAR or a pickaxe exports 64x64 diffuse maps. Costs an Epic OAuth call and
  // roughly +1s on the archive mount (+6s the first time, while container tocs are cached).
  // get_status reports the outcome under "textureStreaming".
  "UseTextureStreaming": true,

  // On-demand chunk request timeout, and how long a cached chunk survives in <DataDirectory>\Cache.
  "ChunkRequestTimeoutSeconds": 30,
  "ChunkCacheLifetimeDays": 7
}
```

### Register with Claude Code

```powershell
claude mcp add fortnite-assets --scope user `
  --env FPMCP_CONFIG=C:\Users\<you>\.fortniteporting-mcp.json `
  -- C:\path\to\publish\mcp\FortnitePorting.Mcp.exe
```

`claude mcp list` should then show `fortnite-assets ✔ Connected`.

---

## Tools

| Tool | What it does |
|---|---|
| `get_status` | Archive load state, counts, and per-category display-name index status. Never blocks. |
| `list_categories` | Every browsable category and its asset-registry row count. Gives the `category` values the other tools accept. |
| `search_assets` | Fast registry search over asset name, package path **and** in-game display name. Never opens a `.uasset`. |
| `browse_category` | Pages through one category, opening each asset for its real display name, description and gameplay tags. |
| `get_asset_info` | Full detail for one asset: display name, description, rarity/series/set, tags, icon textures, style channels, export type. For a sidekick also `animationSources` and `animationDiscovery` (how many animations an `includeAnimations` export will attempt - counted, not converted). |
| `search_files` | Searches the mounted virtual file system by path — meshes, sounds, maps, textures, animations that have no item definition. |
| `get_asset_icon` | One asset's icon as a PNG, at the requested size. |
| `make_contact_sheet` | Composites up to 60 candidates into one numbered grid image plus a legend mapping cells back to object paths. |
| `list_asset_styles` | The style/variant channels and options an asset exposes. |
| `get_properties_json` | The raw deserialised properties of an asset, for when the structured tools are not enough. |
| `export_assets` | Exports one or more object paths to disk (mesh + materials + textures + sounds). `includeAnimations: true` adds a sidekick's whole animation set - see [Sidekick animations](#sidekick-animations-includeanimations). |
| `export_gallery` | Exports a Creative gallery/prefab, either as per-prop folders (default) or as one composed prefab. Also takes `includeAnimations`. |

## Intended agent workflow

1. **`search_assets`** — cast a wide net. "peely", "hedge", "battlewood". Returns object paths.
2. **`make_contact_sheet`** — the key step. Registry names are not descriptive enough to choose
   from; one grid image of 24–60 candidates is worth dozens of guesses. The legend maps each cell
   number back to its object path.
3. **`get_asset_icon`** / **`get_asset_info`** — zoom in on the shortlist.
4. **`export_assets`** / **`export_gallery`** — export the chosen paths.

`browse_category` is the fallback when you do not know the vocabulary at all, and `search_files`
covers raw meshes/sounds/maps that have no item definition and therefore never appear in
`search_assets`.

---

## Startup behaviour

Two things warm up in the background. Neither blocks the MCP handshake.

**1. Archive mount (~7 s).** The provider mounts the paks, submits keys, loads mappings and reads
the asset registries. Until it finishes, `get_status` answers instantly and every other tool waits
about two seconds and then returns `status:"loading"` with a stage and percent — that is not an
error, just retry.

**2. Display-name index (~9 s cold, <1 s warm).** The asset registry only knows internal names:
the original Peely is `CID_349_Athena_Commando_M_Banana`, and the Battlewood Boulevard galleries
are all `PID_FNEC_Ch7_*`. So once the archive is up, the server opens every registry-filtered
package per category, reads that category's display name, and keeps an `objectPath → displayName`
map in memory. `search_assets` then matches display names with the same contains/regex semantics
as asset names, and reports `matchedOn: name | displayName | both` per hit.

The index is built smallest-category-first and cached per category at
`<DataDirectory>\NameIndex\{category}.json`, stamped with the game version plus that category's
registry row count — change either (a game update) and just that category rebuilds. Warm runs load
the maps straight off disk.

Measured on a 42.00 archive (12-way parallel loads):

| Category | Rows | Cold build |
|---|---|---|
| Prop | 105,512 | 6.0 s (17.5k rows/s), 105,512 names |
| Item | 7,020 | 0.4 s |
| Outfit | 3,459 | 0.2 s |
| Prefab | 1,673 | 1.4 s (first category pays the cold-IO cost) |
| Vehicle | 109 | 0.8 s (its handler walks the vehicle blueprint) |
| **all 28 categories** | **133,517** | **8.8 s, 127,963 display names** |

Peak working set for the whole process (archive + full index) was ~2.7 GB, and the index itself
added roughly 120 MB of managed heap on top of the asset registry. Nothing is excluded from the
automatic build.

While a category is still building, searches against it match asset and package names only and the
reply carries a `note` saying so; `get_status → nameIndex` shows the per-category state
(`notBuilt` / `building` with a percent / `ready` with a count).

---

## Export layout

`export_assets` has three modes:

* **`outputDir` omitted** — exports mirror the game path underneath the configured `ExportRoot`,
  e.g. `ExportRoot\Game\Athena\Items\...`. Good for building up a library that stays organised the
  way the game is.
* **`outputDir` set** — everything lands *flat* in that one folder (mesh next to its textures).
  Good for a one-off you are about to import somewhere.
* **`outputDir` set + `perAssetFolders: true`** — each asset gets its own folder
  `<outputDir>\<Display Name>\`, flat inside, with a plain `manifest.json`. Colliding display names
  (Fortnite has dozens of props called "Wall") take a `_2` suffix. Same layout `export_gallery`
  produces per prop, and the one to use for a kitbash list, where a shared flat folder makes one
  asset's textures indistinguishable from another's. Each asset's folder comes back as its
  `outputRoot`.

`export_gallery` resolves a gallery either by `galleryObjectPath` or by `galleryName` (matched
against asset names and, once the Prefab index is ready, display names — an in-memory scan rather
than opening every playset package).

* **`perAssetFolders: true` (default)** — every member prop is exported on its own into
  `<outputDir>\<Gallery Display Name>\<PropName>\`, so each folder holds one mesh plus its own
  textures and can be imported independently. Textures shared between props are therefore written
  once per prop folder; that duplication is deliberate.
* **`perAssetFolders: false`** — the gallery is exported once as a composed prefab.

Meshes are `.uemodel` by default (`ActorX`/`.psk` and `Gltf2`/`.glb` also available), textures PNG
or TGA, sounds WAV (other formats need `ffmpeg` on PATH).

---

## Bulk export (`--export-list`)

A kitbash list is hundreds of assets, and driving that through `export_assets` a call at a time
either mounts the archive per call or blocks the agent for minutes with no visible progress.
`--export-list` is the batch path: one process, one archive mount, assets exported sequentially into
per-asset folders, with a machine-readable report rewritten after **every** asset.

```powershell
FortnitePorting.Mcp.exe --export-list kitbash.json D:\Kitbash --meshFormat Gltf2 --resume
```

| Flag | Meaning |
|---|---|
| `<list.json>` | Input list (positional, required). |
| `<outputDir>` | Export root for this run (positional, required). |
| `--meshFormat` | `UEFormat` (default), `ActorX`, `Gltf2`. |
| `--imageFormat` | `PNG` (default) or `TGA`. |
| `--styles` | `all`, or a channel→option object like `{"Style":"Black & Gold"}` — same union `export_assets` takes. |
| `--resume` | Skip any entry whose folder already holds a `manifest.json`. |
| `--includeAnimations` | Sidekick entries also export their animation set (same as the `export_assets` option). |

The list is a JSON array; only `objectPath` is required.

```jsonc
[
  { "objectPath": "/CRP_CondoCanyon/.../PPID_..._Sandbag_A.PPID_..._Sandbag_A", "category": "Props" },
  { "objectPath": "/Game/Athena/Items/Weapons/WID_...T03.WID_...T03",
    "category": "Weapons", "folder": "SCAR_Ore_T03" }
]
```

* `category` groups the asset into `<outputDir>\<category>\<name>\`.
* `folder` names the folder outright; `displayName` is used when `folder` is absent. Either one
  spares the run from opening the package just to learn the name — which is what makes a `--resume`
  pass over an annotated list nearly free.
* With neither, the folder is the asset's in-game display name, deduped with `_2` exactly as above.

**Failures never abort the run.** A bad path fails its own entry and the next asset starts.
Progress goes to stderr as `[n/total] status name — files, seconds (elapsed, ETA)`.

`<outputDir>\export_report.json` is the run's ledger, rewritten (through a temp file, so a kill
mid-write cannot truncate it) after every asset:

```jsonc
{
  "mode": "export-list", "listPath": "...", "outputRoot": "...",
  "meshFormat": "Gltf2", "imageFormat": "PNG", "styles": null, "resume": false,
  "startedUtc": "...", "updatedUtc": "...",
  "running": false,          // still true => the process died mid-run
  "elapsedSeconds": 3.0, "etaSeconds": 0,
  "total": 6, "processed": 6,
  "ok": 5, "partial": 0, "failed": 1, "skipped": 0,
  "totalFiles": 53, "totalBytes": 17716754,
  "entries": [{
    "index": 1, "objectPath": "...", "category": "Props", "displayName": "...",
    "folder": "<abs>", "status": "ok",   // ok | partial | failed | skipped | pending
    "seconds": 1.02, "fileCount": 13, "bytes": 3663309,
    "manifestPath": "<abs>", "primaryMesh": "SM_Airborne_Sandbag_A.glb", "primaryMeshPath": "<abs>",
    "files": ["<abs>", "..."], "missingArtifacts": [], "error": null
  }]
}
```

`partial` means the asset wrote files but dropped a primary artifact (see `missingArtifacts`) — the
same distinction `export_assets` makes. Exit code is `0` when nothing failed, `2` when something
did, `1` for a bad invocation.

Resuming re-reads the previous report so skipped entries keep their file lists rather than going
blank, and folder naming is deterministic for a given list order, so a resumed run lands on exactly
the folders the first one created. Entries that *failed* are retried, not skipped.

---

## Sidekick animations (`includeAnimations`)

A sidekick (`CosmeticCompanionItemDefinition`, category `Sidekick`) exports its visible mesh like any
cosmetic. Its animations are not named by the item at all - they hang off a small object graph - so
`export_assets {objectPaths:[...], outputDir, perAssetFolders:true, includeAnimations:true}` adds a
discovery + export phase after the mesh (`Core/CompanionAnimations.cs`).

**Discovery** walks the real object graph through CUE4Parse property reflection - `FPackageIndex`,
`FSoftObjectPath`, `FStructFallback`, `FInstancedStruct`, arrays, sets and maps, read generically with no
per-class schema, a depth cap of 8 and a visited set:

| Root | What it is | Yields |
|---|---|---|
| `AnimTableAsset` | `ProxyTable` (`PRT_<Companion>_AnimTable`) | `RuntimeValues[].Value{Asset}` sequences / blend spaces, `{Chooser}` -> `ChooserTable`s (walked) |
| `ReactionChooserProxy` | `ProxyTable` -> `ChooserTable` | the reaction montages (`*_RCT_*_M`) |
| `InteractionSceneAsset` | `ContextualAnimSceneAsset` | petting/hi-five montages for BOTH roles |
| `ItemVariants[]` | `ContextualAnimSceneEmote` (CAS) / `AdditivePoseAnim` | the sidekick emote montage + the player's montage; quirk poses |
| `MainAnimClass` | `AnimBlueprintGeneratedClass` (class + CDO properties) | tables/anims the anim blueprint references - P.A.N.D.A. Team Jr. has no `AnimTableAsset`, its 21 table-driven anims come from here |
| folder sweep | `/<Companion>/Animation/**` and `/<RigFamily>/Animation/**` anywhere in the VFS | the rest of the shared rig library, incl. CreativeCompanion `/<Family>/Animation/UEFN/` creative reactions |

The rig family is the prefix of `MainSkeletalMesh` (`FullSnore_Quadruped_INVIS` -> `FullSnore`). The
family's own `Animation/Emotes/` is skipped (another companion's emotes). Everything is deduped by
mount-relative package path; class is read from the package header, so non-animation assets in those
folders (choosers, proxy assets, sounds) are never opened. CAS tracks carry their `Role`; a track whose
role is not the CAS `PrimaryRole` (or whose path is under `/Player/`) is `playerSide`.

**Layout** (beside the manifest; `<AssetName>_Animations` instead of `Animations` when several assets
share one flat folder):

```
<Display Name>/
  BandSoda_Quadruped.uemodel, textures..., manifest.json
  Animations/
    Skeletons/   one .uemodel per DISTINCT skeleton (companion mesh, rig family, player, emote props)
    Core/ Actions/ AimOffsets/ Reacts/ Quirks/ Emotes/ Creative/ Player/ Other/   <Name>.ueanim
```

Group comes from the path segment after `/Animation/` (`UEFN`/CreativeCompanion -> `Creative`; player-side
-> `Player`), falling back to name tokens (`_COR_`, `_ACT_`, `_AO_`, `_RCT_`, `_QRK_`). Montages go through
`AnimExport`'s montage path (sections + notifies - notify sounds and spawned prop meshes land beside the
montage as `extraFiles`), sequences through its sequence path. A blend space has no file of its own: its
`samples[]` (sample value + sequence) are listed and each sample sequence is exported as its own entry.

**Formats.** Animations follow `meshFormat`: UEFormat -> `.ueanim` + `.uemodel` skeletons; ActorX ->
`.psa` + `.pskx` skeletons. glTF cannot hold a skeleton-only file and the glb writer carries no animation,
so with `Gltf2` the companion mesh is still `.glb` but the animation phase writes UEFormat and says so in
`animations.notes`.

**Manifest** (additive - `schemaVersion` stays 2, `schemaRevision: "2.1"`): `companionRig`
(`mainSkeletalMesh`, `mainAnimClass`, `baseRigSkeleton`, `companionMeshSkeleton`, `rigFamily`),
`animationSources[]`, `animationsRoot`, `animations[]` (`name, objectPath, class, file, files?, group,
kind: sequence|montage|additive|blendspace, skeleton, skeletonFile, skeletonRole, lengthSeconds,
frameCount, source: animTable|reactionChooser|cas|variant|animBlueprint|folderSweep|blendSpaceSample, via,
playerSide, casRole?, additive?, sections?, samples?, extraFiles?, status, error?`), `skeletons[]`
(`role, boneCount, animationsUsing, bonesSharedWithCompanionMesh`), `retargetAssets[]` (RTG_/IK_ paths,
informational) and `animationSummary`. The tool result carries the same summary per asset plus
`animationsExported` / `animationsFailed` (with reasons) at the top level.

**Honesty.** A resolved animation that writes no file is a `missingArtifacts` entry on the asset (so the
export is `partial` and listed in `failures`), with the reason recovered from the converter's warning
(ExportContext only logs it). A skeleton that did not write is `skeletonError` on the entry and counted in
`skeletonFilesMissing`. `includeAnimations:false` (the default) leaves the export exactly as before.

**Which skeleton are the animations on?** Mostly NOT the visible mesh. Each animation stays on the
skeleton it was authored for, reported as `skeletonRole`:

* `companionMesh` - plays directly on the exported mesh (the sidekick's own emote, quirk poses, and - for
  sidekicks that are their own rig family, e.g. Flopsticks - everything).
* `rigFamily` - the shared library (locomotion, actions, aim offsets, reacts, creative) authored on the
  family skeleton, e.g. `FullSnore_Quadruped_Skeleton` for every FullSnore-rig dog. Bone NAMES largely
  match the visible mesh (`bonesSharedWithCompanionMesh`, e.g. 100 of BandSoda's 106) but proportions do
  not (58 of those 100 bones have a different rest translation). The anim blueprint runs on the invisible
  base rig (`companionRig.mainSkeletalMesh`), and the companion folder ships `RTG_<Companion>` / `IK_<Companion>`
  retarget assets (`retargetAssets`) - Fortnite's own bridge to the visible mesh; those assets are listed,
  not converted. To play the family animations on the visible mesh: retarget
  (UE IK Retargeter from the family skeleton to the companion skeleton), or in Blender import the
  `.ueanim` onto the family skeleton `.uemodel` from `Animations/Skeletons/` - or apply by bone name
  using rotations only.
* `player` - `Fortnite_M_Avg_Player_Skeleton`: the player half of a CAS emote/interaction (`playerSide`).
* `prop` - an emote prop's own skeleton (e.g. `SK_BandSoda_Gadget`).

Additive sequences (`kind: additive`, all `_ADD` / `_AO_` / quirk poses) are exported as stored and
flagged: cooked Fortnite strips `RefPoseSeq`, so CUE4Parse cannot bake them onto a base pose
(`additive.bakedOntoBasePose: false`) and the `.ueanim` carries additive deltas with the additive type in
its metadata - layer them over a base pose (e.g. `*_COR_Base_Pose`) rather than playing them alone.

---

## Command-line modes

Besides the default stdio server, the same exe carries its development harness:

| Flag | Purpose |
|---|---|
| `--tools` | Lists every registered tool with its input schema. |
| `--call <tool> '<jsonArgs>'` | Invokes one tool in-process, prints the JSON, writes any images to `<DataDirectory>\call_output`. |
| `--selftest` | End-to-end smoke: mount the archive, check the registry, extract the native dependencies, decode one icon, discover the tools. |
| `--export-list <list.json> <outputDir> [--meshFormat …] [--imageFormat …] [--styles …] [--resume] [--includeAnimations]` | Bulk export for kitbash lists: one archive mount, per-asset folders, resumable, `export_report.json`. See above. |
| `--nameindex [--category <Name>]` | Builds the display-name index in the foreground with per-category timings and memory. |
| `--iconcoverage <n> [--category <Name>]` | Samples a category through the icon resolver and reports how much real artwork (vs placeholder) it found. |

## Notes

* **stdout is protocol-only.** The first statement in `Program.cs` redirects `Console.Out` to
  stderr, and all logging goes through Serilog to stderr and a rolling log file under
  `<DataDirectory>\Logs`. Nothing may ever `Console.Write` to real stdout.
* **`objectPath` is always the full `package.object` string** returned by `search_assets`. Bare
  names will not resolve.
* **Icon coverage is imperfect.** Every image result reports an `iconSource`: `handler` and
  `rawTexture` mean real artwork was found, `placeholder` and `generated` mean none was.
