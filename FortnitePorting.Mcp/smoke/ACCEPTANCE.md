# FortnitePorting MCP — Acceptance Test Log (2026-08-31)

Server: publish\mcp\FortnitePorting.Mcp.exe (Release, commit 0ea40dea + fixes), archive FN 42.00 local.
Method: tools driven exactly as an AI agent would (`--call` harness = same code path as MCP dispatch), images visually inspected by the testing agent; plus raw JSON-RPC stdio session and Claude Code registration handshake.

| # | Test (user-style prompt) | Result | Evidence |
|---|---|---|---|
| T1 | "find some hedge props" | PASS | 138 hits; 24-cell contact sheet visually confirmed (planters, junipers, topiary cones/spheres/cubes, snowy). One false positive ("Hedgehog") — expected substring behavior, trivially skipped by agent. |
| T2 | "post-apocalyptic rusted floor and wall building kit" | PASS | Agent workflow (broad terms + visual sheets): "rust" → RustBucket rusted catwalk floors/railings/stairs; "corrugated" (89) → Fortilla patchwork shanty wall panels. Complete kit identified visually. "wasteland"=0, "scrap" matched "Skyscraper" — literal substring limits, mitigated by synonym search + sheets (and by display-name index, see fix round). |
| T3 | "show me gates and fences" | PASS | 163 hits; sheet shows corrugated gates/fences, spiked barriers, dojo gates, welcome gates. |
| T7 | "stone pillars / columns" | PASS | 1,996 hits; sheet shows warehouse pillars, marble manor pillars, fluted classical columns, broken columns. |
| T8 | "what galleries exist / browse galleries" | PASS | search "gallery" in Prefab → 588 PID_* galleries. |
| T9 | "find the Peely outfit, show icon, list styles" | PASS w/ finding | "peely" → 3 CIDs (Mech/Tech/Toon); icon = real render (iconSource=handler). Original Peely is CID_349_..._Banana — registry-name search misses display names → FIX ROUND: display-name index. Styles proven on Renegade Raider (3 material variants). |
| T10 | "get me the texture files for a prop" | PASS | Prop exports include full PBR texture sets (e.g. WP4 test: 1 .uemodel + 4 PNGs, 6.7MB; gallery props each carry basecolor/normal/etc.). |
| T11 | "find a llama sound" + export | PASS | search_files "llama" fileType=sound → 89; exported InvulnLlama_Pickup → valid RIFF/WAVE 412,672 B in ExportRoot. |
| T12 | misspelling "hegde" | PASS (graceful) | total=0, no error. Improvement queued: helpful note on 0 results. |
| T14 | mockup match (dig-site conservatory) | PASS | All mockup elements discoverable + visually verifiable: hedges (T1), pillars (T7), gates (T3), glass greenhouse panels ("greenhouse" 6, LabRat glass walls, sheet verified), domes 145, lanterns 316. |
| T15 | "Export the Battlewood Boulevard gallery as individual assets" | see below | Runner-level: Nature Gallery 52 props/662 files/939MB/0 failures, byte-exact reconciliation; Prop Gallery 7,190 files reconciled. Tool-level run logged below. |
| — | MCP protocol / stdio purity | PASS | 38/38 raw JSON-RPC checks; handshake 475ms mid-load; stdout byte-pure; McpException surfaced; process survives tool throws. |
| — | Claude Code registration | PASS | `claude mcp add fortnite-assets --scope user` → `claude mcp list` = ✔ Connected (real handshake by Claude Code). Nested headless `claude -p` not testable from this session (separate login context) — noted honestly. |

## Known limitations / queued fixes
1. Registry search is literal substring over asset/package names → display-name index (background-built, disk-cached) to make "Peely"/"Battlewood Boulevard" first-class. IN PROGRESS.
2. Gallery textures duplicated per prop folder (independent importability by design; ~18MB duplicates possible). `shareTextures` option is a candidate follow-up.
3. Zero-result searches return bare total=0 → add suggestion note.
4. Style-variant export (beyond base) not yet wired into export_assets (list_asset_styles exists for discovery).

---

# Fix round — display-name index (2026-08-31, later same day)

Addresses queued fixes **1** and **3** above, plus the slow gallery name resolution found in T15.
Server rebuilt and re-published to `publish\mcp\FortnitePorting.Mcp.exe`; all evidence below comes from that exe.

## What changed
* **New `Core/DisplayNameIndex.cs`** — per-category `objectPath → displayName` map for all 28 catalog categories.
  Built by filtering the registry with the existing `AssetQuery` category filters, opening each package 12-way parallel
  and running that category's `DisplayNameHandler`. Cached per category at `<DataDirectory>\NameIndex\{category}.json`,
  stamped with `{gameVersion}|{categoryRowCount}` so a game update rebuilds only the categories that actually moved.
  Background build starts from `ArchiveHostedService` once the archive is Ready, smallest category first, fire-and-forget,
  and is wrapped so no failure can reach the host.
* **`search_assets`** now matches display names with the same contains/regex semantics as asset/package names, merges and
  dedupes by `objectPath`, reports `displayName` and `matchedOn: name|displayName|both` per item, carries a `nameIndex`
  block, notes partial coverage while a category is still building, and returns a "try this instead" note on zero results.
  The registry path is untouched — display-name matching is an in-memory dictionary hit per row.
* **`get_status`** gained a `nameIndex` section with per-category `notBuilt`/`building(percent)`/`ready(count)`.
* **`ExportRunner.FindGalleries`** uses the Prefab index when ready (in-memory scan); falls back to the old
  load-every-playset behaviour when it is not.
* **`--nameindex [--category X]`** CLI mode added so the build is measurable in the foreground.
* `FortnitePorting.Mcp/README.md` written.

## Measurements (FN 42.00 archive, 12-way parallel)

Cold full build, `--nameindex`, no cache present:

| Category | Rows | Time | Names |
|---|---|---|---|
| Prop | 105,512 | **6.0 s** (17,567 rows/s) | 105,512 |
| Item | 7,020 | 0.4 s | 2,900 |
| Outfit | 3,459 | 0.2 s | 3,206 |
| Prefab | 1,673 | 1.4 s (first category pays cold IO) | 1,673 |
| Vehicle | 109 | 0.8 s (handler walks the vehicle blueprint) | 94 |
| 23 others | 15,744 | < 0.1 s each | 14,578 |
| **All 28** | **133,517** | **8.8 s** | **127,963** |

Prop is far under the 10-minute threshold, so **nothing is excluded from auto-build**.

Memory: managed heap 1,618 → 1,735 MB across the Prop build (≈120 MB for the index itself on top of the
569,456-row asset registry); whole-process peak working set 2,686 MB for archive + full index.
Disk cache 17.4 MB for Prop, ≈19 MB total.

## Verification

| # | Check | Result | Evidence |
|---|---|---|---|
| a | Cold run: background index build completes | PASS | Cache deleted, real stdio server started: `[NAMEINDEX] All categories done in 8.8s - 127,963 display names, 28/28 categories ready`. Per-category timings above. Server stayed responsive throughout (tool calls served mid-build). |
| b | `search_assets {"query":"peely","category":"Outfit"}` finds the original Peely | PASS | `total=13` in **63 ms**, includes `CID_349_Athena_Commando_M_Banana` / displayName `"Peely"` / `matchedOn:"displayName"` — the exact miss from T9. Also Agent Peely, Polar Peely, Toon Peely (`matchedOn:"both"`), Unpeely, KAWSPEELY, P33LY (`matchedOn:"name"`). Every item carries a displayName. |
| c | `search_assets {"query":"battlewood","category":"Prefab"}` returns 8 galleries, < 2 s warm | PASS | `total=8` in **76 ms**: Nature, Floor, Wall, Roof, Foundation, Prop, Stores, Golden Reel — all `matchedOn:"displayName"`. |
| d | `export_gallery {"galleryName":"Battlewood Boulevard Nature Gallery"}` resolves fast via index | PASS | Name resolution isolated (deliberate no-match query, which is the worst case that used to load all 2,170 playsets): **67 ms**, was seconds. Full run resolved to `PID_FNEC_Ch7_Nature_Gallery_b` and exported 52/52 props, 662 files, 984,506,712 bytes, **0 failures**. `_fixtest` deleted afterwards. |
| e | Zero-result suggestion note | PASS | `{"query":"hegde","category":"Prop"}` → `total=0`, `isError=false`, note suggesting shorter/generic terms, synonyms, spelling, dropping the category filter, regex alternation, and browse_category + make_contact_sheet. |
| f | `--selftest`, `--tools`, stdio sanity | PASS | selftest PASSED in 4.3 s (12 tools discovered, icon decoded); `--tools` lists all 12; raw JSON-RPC stdio harness **44/44 checks PASSED** cold, including handshake 332 ms mid-load, stdout byte-purity (27 protocol lines, 0 bad, no BOM), display-name search and zero-result note over the wire. |

### No regressions
* T1 "hedge" in Prop still returns **138** over stdio; icon, contact-sheet, error-path and stdout-purity checks all still pass.
* Un-scoped searches (no `category`) improved as a side effect: "peely" with no category went from 59 to **90** hits.

### Left imperfect
* `FindGalleries`' fast path only knows display names for playsets that survive the Prefab category filters
  (1,673 of 2,170 rows). Galleries hidden by those filters ("Device", "PID_Playset", …) are still matchable by
  asset name, but no longer by display name. Deliberate trade for the ~70× speedup.
* A search issued during the first ~9 s of a cold run can still report partial display-name coverage; the reply
  says so and names the category. Warm runs load every category off disk inside the 3 s grace.
* Fix 2 (`shareTextures`) and fix 4 (style-variant export) remain queued.

---

# Outfits deep pass (2026-08-31, third round)

Closes queued fix **4** (style-variant export) and takes Outfits to production grade.
Server rebuilt and re-published to `publish\mcp\FortnitePorting.Mcp.exe`; all evidence below comes from that build,
driven through `--call` (the same dispatch path the MCP server uses).

## What changed

* **New `Core/StyleResolver.cs`** — headless port of the GUI's whole style pipeline
  (`AssetInfo` ctor + `AssetStyleInfo` + `ExportService.ConvertStyles`). Reads `ItemVariants` into channels of named
  options and maps each option onto the `ExportStyleBase` the exporter wants: `ExportStructStyle` for part/material/
  mesh/particle/tag/morph variants, `ExportColorStyle` for `FortCosmeticRichColorVariant` (StyleData = `RichColorVar`,
  ColorData = the `ColorPairs` entry) and for `FortCosmeticMaterialParameterSetVariant` (StyleData = `InlineVariant`,
  ColorData = the choice, `IsParamSet = true`), plus the GUI's synthetic **"Universal"** empty-struct option for
  `FortCosmeticLoadoutTagDrivenVariant`. Selecting every option of every channel is exactly `AssetInfo.GetAllStyles()`,
  i.e. what a GUI folder export does.
* **`export_assets` gained `styles`** — omitted = base look (unchanged behaviour); `"all"` = every option of every
  channel; or an object of channel to option, e.g. `{"Style":"Black & Gold"}`. Names are matched case- and
  punctuation-insensitively (`{"style":"black and gold"}` resolves). The resolved array goes straight into
  `ExportSession.CreateExport(displayName, asset, exportType, styles)`. Applies to every cosmetic that carries
  `ItemVariants` — outfits, backpacks, pickaxes — because they share the one `MeshExport.ExportStyles` mechanism.
  A bad channel/option fails that asset with a message listing the valid names, before any work is done.
* **`export_assets` gained `importLobbyPoses`** (default false, matching the GUI toggle).
* **`export_assets` now reports** `appliedStyles`, a `parts[]` block (name / partType / gender / fromStyle /
  poseAsset / material counts) and `notes[]`.
* **`list_asset_styles` and `get_asset_info.styleVariants` are now backed by the same resolver as the exporter**, so
  every option they list is selectable verbatim. `list_asset_styles` also returns a `usage` line showing the exact
  `styles` argument to pass. The two code paths were previously separate ports and had drifted — the old param-set
  reader listed choices the GUI skips.
* **`ExportRunner.ResolveModelPaths` widened** to cover style override materials/parameters, head `.uepose` pose
  assets, body master-skeleton meshes and animation sections. These were previously only caught by the filesystem
  diff, so a re-export into a folder that already held the output under-reported them.
* **New `Core/CharacterPartInspector.cs`** — resolves an outfit's parts in `MeshExport`'s exact order
  (`BaseCharacterParts`, else `HeroDefinition.Specializations[0].CharacterParts`, else the flat `CharacterParts` used
  by backpacks/pets). Feeds a new `characterParts` block in `get_asset_info`: source, part types, **bodyType**,
  per-part mesh and `AdditionalData`, and a `partsWithoutMesh` count.
* **`get_asset_info` rarity fixed and enriched** — `EFortRarity`'s tokens are internal aliases
  (`Quality == Epic`, `Fine == Legendary`, `Sturdy == Rare`) and `.ToString()` was returning the alias. `rarity` is
  now the player-facing name with `rarityRaw` beside it. Added `introducedSeason` from the
  `Cosmetics.Filter.Season.N` gameplay tag.
* **New `--outfitaudit N [--category X] [--seed S]` CLI mode**, and `--iconcoverage` now names its misses.

## Verification

| # | Check | Result | Evidence |
|---|---|---|---|
| o1 | Renegade Raider style export produces materially different output | PASS | `CID_028_Athena_Commando_F`, flat output dirs. base **30 files / 1,355,389 B**; `{"Style":"Black & Gold"}` **42 files / 1,410,080 B**; `"all"` **46 files / 1,432,228 B**. The 12 files unique to Black & Gold are the override texture set `T_RenegadeRaider_Onyx_{Body,FaceAcc}_{D,E,FX,M,N,S}` plus `T_Fuzz_MASK.png`; `"all"` adds 4 more (`F_MED_Commando_TV21_{d,m,n,s}` = the Checkered variant). The 30 base files are byte-identical across all three runs. |
| o2 | Loose name matching | PASS | `{"style":"black and gold"}` gives `appliedStyles:["Style: Black & Gold"]` and the same 42 files / 1,410,080 B. |
| o3 | Style export works for other cosmetic categories | PASS | Backpack `BID_479_CatBurglar` ("Gold Dagger Pack", 4 options): base **6** files; `{"Style":"Shadow"}` **7** (adds `T_CatBurglar_GoodDark_Backpack_D.png`); `"all"` **9** (adds `T_M_MED_CatBurglar_Good_Backpack_{D,S}.png`). Pickaxes run the same code path; the ones sampled (`HalloweenScythe`) carry no `ItemVariants` and say so. |
| o4 | `list_asset_styles` and `export_assets` agree | PASS | `list_asset_styles` on CID_028 returns channel `"Style"` with options `Default / Checkered / Black & Gold` and a `usage` string; all three names are accepted verbatim by `export_assets`. |
| o5 | Bad style selections fail loudly, not silently | PASS | Unknown option: `Channel "Style" of 'CID_028_Athena_Commando_F' has no option named "Neon Purple". Available options: "Default", "Checkered", "Black & Gold".` Unknown channel lists `"Style"`. Styleless asset with `styles:"all"` tells the caller to omit `styles`. `styles:"everything"` is rejected with the two valid forms. All are per-asset `failures[]` with `isError=false` for the batch. |
| o6 | Part completeness, 3 diverse outfits | PASS | **CID_028 Renegade Raider** (classic CID, female): 3 parts Body+Head+Hat, `.uepose` facial pose, master skeleton `SK_M_Female_Base_Skeleton.uemodel`, 30 files. **CID_694 Midas** (Head/Body/Face/Hat): 4 parts, 29 files = 23 png + 5 uemodel + 1 uepose. **Character_FearlessFlightHero, Spider-Man (Miles Morales)** (modern `Character_*`): 3 parts Head/Body/Face, 15 files = 11 png + 4 uemodel. Nothing `get_asset_info` listed was missing from any of the three exports. |
| o7 | Part completeness at scale | PASS | `--outfitaudit 1200 --seed 77` = **1,017 unique outfits**. 942 resolve via `BaseCharacterParts`; **every one of those has both a Head and a Body part** (0 missing-body). Face 69.3%, Hat 10.4%, MiscOrTail 2.9%, Backpack 0.2%. Only **8 parts out of roughly 2,800** carry no `SkeletalMesh`, and their names show them to be deliberate no-ops or dev stubs (`NoBackpack`, `NoFaceAccessory`, `CID_BentBaton_Temp`, two `_FaceAcc` entries). 1-2 outfits resolve zero parts (`Character_NPCHireReward`, an NPC-hire reward stub). |
| o8 | Outfit icon coverage: what is the missing 9%? | EXPLAINED, no fix possible | `--iconcoverage 150 --category Outfit` with the cache cleared: **139/150 = 92.7% handler, 11 placeholder**. All 11 misses are Save-the-World hero cosmetics under `/SaveTheWorld/Heroes/{Commando,Constructor,Ninja,Outlander}/CosmeticCharacterItemDefinitions/`. `--outfitaudit` on the same seed shows those same 11 rows are **unloadable**, and `search_files {"query":"CID_Constructor_022"}` returns **0 files**: the packages are listed in the asset registry but are not shipped in this BR-only install. No IconResolver fallback can reach them. At the larger sample, 73 of 1,017 sampled Outfit rows (7.2%) are these STW ghosts. **Real-icon coverage over loadable BR outfits is 139/139 = 100%.** |
| o9 | `get_asset_info` metadata, 5 well-known skins plus a Series one | PASS | Drift `Legendary` (raw `Fine`) / set Drift / S5 / 2 parts / Style(6). Midas `Legendary` (raw `Fine`) / Golden Ghost / S12 / 4 parts / Style(4). Peely `Epic` (raw `Quality`) / Banana Bunch / S8 / 3 parts. Raven `Legendary` / Nevermore / S3 / 2 parts. Renegade Raider `Rare` (raw `Sturdy`) / Storm Scavenger / S1 / 3 parts / Style(3). Spider-Man (Miles Morales) `Epic` / **series MarvelSeries "MARVEL SERIES"** / set "Across the Spider-Verse" / S24 / 3 parts / Style(2). All match known Fortnite data. |
| o10 | Rebuild / self-test / tool schema | PASS | `dotnet build -c Release`: 0 errors, 0 CS warnings. `--selftest` PASSED in 4.6 s (569,456 registry entries, 12 tools, icon decoded 21,295 B). `--tools` shows `styles` on `export_assets` as an untyped union property carrying the usage text. stdout purity untouched: no new Console writes, Serilog still stderr-only. |

## Findings that are environment, not code

* **Animation export is dead in this build.** `importLobbyPoses:true` on Midas resolves the montage
  `CatBurglar_Male_Idle` and exports its skeleton and lobby props, but the `.ueanim` fails with
  `DllNotFoundException: CUE4Parse-Natives` — Fortnite animations are ACL-compressed and need the native library,
  which this machine cannot build (`cmake` is not on PATH; the build logs `CUE4Parse-Natives build failed. Continuing
  without it`). The exporter swallows that as a warning, so without help an export would report success with no
  animation file. `export_assets` now returns an explicit `notes[]` entry saying exactly this. Meshes, textures and
  `.uepose` pose assets are unaffected. Emote and animation exports share the limitation.

## Left imperfect

* **No outfit in this archive uses the `HeroDefinition.Specializations` fallback** — 0 of 1,017 sampled. The code
  path is implemented and mirrors `MeshExport` exactly, but it could not be exercised against real data here: the
  assets that would use it are the Save-the-World hero definitions, whose packages are not shipped in this install
  (see o8). Recorded as untested rather than verified.
* `styles:"all"` deliberately does **not** include a Prefab's "Individual Props" object styles — `export_gallery`
  owns that path and does it better (one folder per prop). `list_asset_styles` still lists them for discovery.
* Style overrides land in the same flat output folder as the base look. Which texture belongs to which style is
  readable from the file names and from `appliedStyles`, but there is no per-style subfolder.
* The stdio JSON-RPC harness referenced in the earlier rounds is not checked into this repo, so this round was
  verified through `--call` (identical dispatch) and `--tools` rather than over the wire.
* Fix 2 (`shareTextures`) remains queued.
