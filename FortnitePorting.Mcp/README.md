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
  "Language": "English"
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
| `get_asset_info` | Full detail for one asset: display name, description, rarity/series/set, tags, icon textures, style channels, export type. |
| `search_files` | Searches the mounted virtual file system by path — meshes, sounds, maps, textures, animations that have no item definition. |
| `get_asset_icon` | One asset's icon as a PNG, at the requested size. |
| `make_contact_sheet` | Composites up to 60 candidates into one numbered grid image plus a legend mapping cells back to object paths. |
| `list_asset_styles` | The style/variant channels and options an asset exposes. |
| `get_properties_json` | The raw deserialised properties of an asset, for when the structured tools are not enough. |
| `export_assets` | Exports one or more object paths to disk (mesh + materials + textures + sounds). |
| `export_gallery` | Exports a Creative gallery/prefab, either as per-prop folders (default) or as one composed prefab. |

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

`export_assets` has two modes:

* **`outputDir` omitted** — exports mirror the game path underneath the configured `ExportRoot`,
  e.g. `ExportRoot\Game\Athena\Items\...`. Good for building up a library that stays organised the
  way the game is.
* **`outputDir` set** — everything lands *flat* in that one folder (mesh next to its textures).
  Good for a one-off you are about to import somewhere.

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

## Command-line modes

Besides the default stdio server, the same exe carries its development harness:

| Flag | Purpose |
|---|---|
| `--tools` | Lists every registered tool with its input schema. |
| `--call <tool> '<jsonArgs>'` | Invokes one tool in-process, prints the JSON, writes any images to `<DataDirectory>\call_output`. |
| `--selftest` | End-to-end smoke: mount the archive, check the registry, extract the native dependencies, decode one icon, discover the tools. |
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
