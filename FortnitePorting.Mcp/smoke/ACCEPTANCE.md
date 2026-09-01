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

---

# Defect fix round — D1-D12 from the category audit

Source: `category-audit/AUDIT.md` (audited at `98f57f6f`, exe build 2026-08-31 19:58, Fortnite 42.00,
569,456 registry rows). Fixed on top of `c3228b8c`.
All "after" figures come from `publish\mcp\FortnitePorting.Mcp.exe` with
`FPMCP_CONFIG=C:/Users/texas/.fortniteporting-mcp.json`.

**Gate:** `dotnet build -c Release` 0 errors · `--selftest` PASSED (6.6 s, 12 tools) ·
`dotnet publish -c Release -o publish\mcp` OK, `CUE4Parse-Natives.dll` present in the publish output.

## Per-defect before / after

| # | Sev | Before | After | Evidence |
|---|---|---|---|---|
| **D1a** | blocker | Emote export wrote only `.wav` + base skeleton `.uemodel`; `DllNotFoundException: CUE4Parse-Natives` in the log | `.ueanim` produced | `EID_TakeTheL` → **`Emote_Dance_Loser_CMM.ueanim`, 28,458 bytes** alongside the 2,425,308 B wav and 9,348 B skeleton. `get_status` now reports `nativeAnimationSupport: true`. |
| **D1b** | blocker | `assetsExported:1, failures:[]` while the primary artifact was silently dropped | Partial exports surface as failures | With the DLL moved aside, the same call returns `status:"partial"`, `assetsIncomplete:1`, `complete:false`, `missingArtifacts:["animation: 1 section(s) were resolved but no .ueanim/.psa was written."]`, a matching `failures[]` entry and a note naming the missing native. With the DLL present: `status:"ok"`, `assetsIncomplete:0`. |
| **D2** | blocker | WeaponMod 0 assets, Wildlife 0 assets; `ManuallyDefinedAssets` unreferenced dead code | WeaponMod **52**, Wildlife **13** | `list_categories` → `WeaponMod assetCount 52 (manualAssets 52)`, `Wildlife 13 (manualAssets 13)`. `search_assets {"query":"wolf","category":"Wildlife"}` → 1 hit `Wolf` (was 0); `"boar"` → 1 hit `Boar`. Wildlife contact sheet renders 13 cells, 11 real creature icons with correct labels (visually inspected). WeaponMod sheet p0: 24/24 real. Export through the category's own path: `OpticCherrySmoke.uemodel` + 4 textures, `status:"ok"`. |
| **D3** | major | Item 38.3 %, Resource 20.0 %, Trap 16.7 % real icons; whole pages 24/24 magenta | **Item 91.7 %, Resource 100 %, Trap 100 %** (target ≥80 %) | `--iconcoverage 60 --category X`. Deep pages: Item p40 19/24 real (was 0/24), Trap last page 18/18, Resource last page 12/13. Every hidden family was verified artless first by sampling it directly (`make_contact_sheet` with a path query): `/SaveTheWorld/Items/Weapons/` 0 real of 96 sampled (3,784 rows), `/SaveTheWorld/Items/Traps/` 0 of 96 (347 rows), Juno ingredients 0 of 96 (220 rows), `/SaveTheWorld/Items/Ingredients/` 0 of 46, `DIsguiseDevice_SW` 0 of 48 (61 rows), `/Sprout` 0 of 96 (234 rows). All stay findable via `search_files` and exportable by direct objectPath. |
| **D3 bonus** | — | Emote 96.7 % | **Emote 100 %** | Same command. |
| **D4** | major | `browse_category Trap pageSize 6` → `returned 3`, `total 444`; sheet cell *n* ≠ browse row *n* | Both page ONE canonical list | Trap p0 pageSize 6 → `returned 6`, `total 66`, `totalPages 11`; the sheet at the same page/pageSize returns 6 cells and all six `objectPath`s match row-for-row (Armored Wall, Team Settings and Inventory, Item Spawner Plate, Barrier, Elimination Zone, Weapon-free zone). `Item pageSize 8` → `returned 8` (was 5). All 28 categories return exactly pageSize rows at p0. |
| **D5** | major | All 1,005 Banners displayed `Banner Icon`; display-name search dead there | Real names | `browse_category Banner` → Akita, Alpaca Lean, Anvil Alarm, Aqua Peony, Ashen Magus, Baller Leag. `search_assets {"query":"Ashen","category":"Banner"}` → 1 hit, `matchedOn:"both"`. ("Peely" still returns 0: there is no Peely banner in 42.00, so the original repro was a bad example rather than a bug.) |
| **D6** | major | `nameIndex: coverage "none", 0/28 ready`, every category `notBuilt`, while search worked | `coverage "complete", 28/28 usable` | Fresh process `get_status` → `readyCategories 0, cachedCategories 26, usableCategories 28/28, availableDisplayNames 127,887`; Wildlife/WeaponMod report `catalog` (not registry-backed, nothing to index) rather than `notBuilt`. Costs 67 ms and still never blocks: the probe reads only the `Count` header of each cache file. |
| **D7** | minor | Emote p0 was 16/24 identical white `EID_CT_CapturePose_*` silhouettes | Gone | Moved to `DisallowedNames`, which is applied even under `LoadHiddenAssets` (the existing `HideNames = ["_CT", ...]` never fired for exactly that reason). Emote sheet p0 is now 24 distinct real emotes: Crash's Victory, Lush Life, Egg Mobile, Super Shadow Transformation, … |
| **D8** | minor | Dev rows leaked raw asset names as display names | Prettified fallback, source labelled | `SID_Guitar_Figure` → **"Guitar Figure"** with `displayNameSource:"assetName"`; localised rows report `displayNameSource:"displayName"`. Also on `get_asset_info`. The search index still stores only genuine localised names. |
| **D9** | minor | Item searches flooded by rarity clones, undisclosed | Annotated | `search_assets {"query":"shotgun","category":"Item"}` → `WID_ArcadeShotgun_C canonical:true`, `_R/_SR/_UC/_VR canonical:false`, plus a note explaining that the `canonical:true` row is the one browse and the sheets show. Full coverage kept, so an exact clone name still resolves. |
| **D10** | minor | `list_asset_styles` on `ESD_AirSprite` → `channelCount 0` although variants exist | `channelCount 1` | Channel `Variant` / `SiblingExtractableDefinitions` with A, Candy, Galaxy, Gold, Holofoil and their objectPaths, plus usage text saying to export them as separate paths rather than through `styles`. Candidates come from the registry by name prefix, then each is opened to confirm its `ParentExtractableDefinition`. |
| **D11** | minor | `styleCount` meant "rows folded by identical display name", read as "style variants" | Renamed | `browse_category` emits `collapsedDuplicates` (0 = unique), documented in the tool description and in the reply note as explicitly not a style count. `list_categories` reports per-category `deduped` and `collapsedDuplicates` totals. |
| **D12** | minor | All 109 vehicles `"No Description."`; duplicate display names indistinguishable | Both addressed | `FortVehicleItemDefinition` genuinely ships no Description (verified by dumping one), so the handler surfaces what it does hold: `"Spawn names: battlebus, armoredbus. Actor class: ArmoredBattleBus_Vehicle."`. Duplicates get a discriminator: `ArmoredTruck (VID_Valet_ArmoredTruck)` vs `ArmoredTruck (VID_Valet_ArmoredTruck_VaultDestroyed)`. |

## Category counts, before → after

Filtering and dedupe changed what "browsable" means, so the headline counts moved:

| Category | audit n | now | why |
|---|---:|---:|---|
| Item | 7,020 | 1,230 | 4,079 artless rows hidden, 1,711 rarity clones folded |
| Trap | 444 | 66 | 347 STW tier rows hidden, 31 folded |
| Resource | 384 | 109 | 275 Juno / STW / Sprout rows hidden |
| Prop | 105,512 | 26,620 | 78,892 duplicate display names folded (this dedupe used to run per-page, so `total` overstated what you could reach) |
| Emote | 2,171 | 2,156 | 15 CapturePose dev rigs hidden |
| WeaponMod | 0 | 52 | manual assets wired in |
| Wildlife | 0 | 13 | manual assets wired in |

Every other category is unchanged.

## Design notes

* **One canonical list.** `AssetQuery.CanonicalAsync` builds, per category, the deduped and ordered
  `CategoryItem` list that `browse_category`, `make_contact_sheet`, `list_categories` and `search_assets`
  all use. `Filtered` (raw registry rows) stays as the layer `DisplayNameIndex` builds from — the index
  cannot depend on a list deduped by the names it produces. Dedupe is therefore display-name driven and
  waits briefly for that category's index; if the index is not ready the list is returned undeduped, is
  **not** cached, and the reply says so.
* **The `HidePredicate` / `AddStyleHandler` delegates and `AssetEnumerationState` are gone.** They only
  ever ran over a single page, which is what made browse and the sheets disagree. They are replaced by
  the declarative `DedupeDisplayNames` / `DisambiguateDuplicateNames` flags the canonical builder applies
  to the whole category.
* **Name-index cache schema bumped to v2** (`{version}|{rows}|v2`) so Banner caches written by older
  builds, holding 1,005 copies of "Banner Icon", are discarded. Full rebuild of all 28 categories: 9.1 s.
* **`runtimes/CUE4Parse-Natives.dll`** is committed with a README recording its provenance (extracted from
  the official FortnitePorting v4.3.2 self-contained build, same CUE4Parse fork) and how to rebuild it via
  CMake. The csproj copies it to the root of both build and publish output, next to the exe.

## Left open

* **Prop page 0 is still placeholder-heavy** (14/24 real). Alphabetical ordering lands it on the Akita
  pipe props; random-sample coverage for Prop is 100 %, so this is an ordering artefact, not missing art.
  Out of scope for this round — the audit flagged it as a Prop-owner concern.
* **Item is 91.7 %, not 100 %.** The remaining misses are scattered singletons — `AGID_HeroTransformation_*`,
  one Mars enemy fist, an STW tower grenade — with no family to hide. Suppressing them individually would
  risk hiding real content for a few percent.
* **Zombie Chicken and Klombo** are catalog Wildlife entries whose mesh AND icon are absent from Fortnite
  42.00 (`search_files` finds neither `Chicken_Zombie_Bird` nor `Butter_Cake_Mammal`). They are kept in the
  catalog — the content may return — but `browse_category` now reports `available:false` with an
  explanatory note and the sheet legend marks the cell, instead of silently showing magenta.
* **`ExportContext.cs:117` is untouched**, as briefed. The silent swallow is compensated on the MCP side by
  checking the export model's resolved artifacts against what actually landed on disk. It is not a fix to
  the shared exporter, so a GUI export still swallows the same failures.
* Vehicle `tags[]` is still empty: vehicle DataLists carry `Traits`, not the `Tags` container the shared
  gameplay-tag handler reads. Left alone rather than guessing at the struct shape.

---

# Export manifest round (2026-08-31, later same day)

Driven by the UEFN import validation run (`FortniteAssetExports/_uefn_staging/_validation/RESULTS.md`), whose
9/9 structural PASS sat next to 4 WARN + 1 FAIL on colour fidelity. Every one of those traced to information
CUE4Parse **has in memory** and the export **throws away**:

* Gltf2 writes material *names* with every `baseColorTexture` / `normalTexture` **null** — no slot to texture link.
* Exported PNGs are alpha=255 everywhere; foliage opacity lives in a separate `_M` / `_Mask` texture.
* Apollo foliage colour lives in a LUT texture and in base-material colour defaults, not in the diffuse (which is ~white).
* `CP_Apollo_BigBush` exports a shadow proxy that sorts **alphabetically before** the render mesh.

`export_assets` / `export_gallery` now write a per-asset `*.manifest.json` (`manifest.json` inside each
gallery prop folder) built from the live `BaseExport` -> `ExportMesh` -> `ExportMaterial` model, and return its
path. Nothing existing was renamed; all fields are additions.

## Results

| # | Check | Result | Evidence |
|---|---|---|---|
| M1 | `CP_Apollo_BigBush` -> manifest exists, render mesh correct | PASS | `primaryMesh = CP_BigBush.glb` (**not** `BigBushShadowProxy.glb`, which is classified `role: "shadow_proxy"`); asset note `shadow_proxy_present`. |
| M2 | BigBush materials list the LUT and the tints | PASS | `CP_M_BigBush.roles.lut = "ColorPalette"` -> `Apollo_Foliage_LUT_MountainPines.png`; `baseMaterialDefaults` carries `Color1_Base #7C813A`, `Color2_Lit #545B21`, `Color3_Shadows #35461C` — the olive green missing from the white-bush FAIL. Material notes: `masked_blend`, `opacity_in_mask_texture`, `color_via_lut`. |
| M3 | Every referenced texture file exists on disk | PASS | 9/9 referenced textures present for BigBush; `textureFiles.referencedButMissing = []` on all four test assets. |
| M4 | JuniperHedge prop shows Diffuse / Normals / mask params | PASS | `MI_JuniperHedges_leaf_fallback` (BLEND_Masked, twoSided) -> `Diffuse` = `JuniperTree_needle_diffuse.png`, `Normals` = `JuniperTree_needle_normal.png`, `MaskTexture` = `JuniperTree_needle_OSSR.png`. Second slot `MI_JuniperHedges_leaf_fallback_core` (BLEND_Opaque) — the two "orphan" MIs the validation run could not place. |
| M5 | Weapon export | PASS | `WID_Assault_AutoHigh_Athena_BB_R` -> `primaryMesh = SK_SCAR.glb`, slot 0 `MI_Sniper_Scar_Inst` with `Diffuse` / `Normals` / `SpecularMasks` / `CustomizationMask (_CM)`. Manifest 27 KB, valid JSON. |
| M6 | Outfit export | PASS | `CID_028_Athena_Commando_F` -> 3 render meshes (Body / Head / Hat) + 1 `role: "skeleton"`; `primaryMesh = null` with notes `multiple_render_meshes`, `import_all_render_meshes`; every part's slots carry `Diffuse` / `M` / `Normals` / `SpecularMasks`. Export status `partial` is the pre-existing `.uepose`-in-Gltf2 gap, unrelated. |
| M7 | LOD / proxy sidecars accounted for | PASS | `CP_BigBush_LOD2/LOD3.glb` attach to the render mesh, `BigBushShadowProxy_LOD1.glb` to the proxy; `unaccountedMeshFiles = []` on all four assets. |
| M8 | `export_gallery` per-prop manifests | PASS | "DustHaven Tent" -> 7/7 prop folders each hold `manifest.json` with its own `primaryMesh`; "Crime City Wall Gallery" 475/475 props, 10,529 files, manifests throughout. |
| M9 | `--selftest`, build, publish | PASS | self-test PASSED in 5.5 s, 12 tools; `dotnet build -c Release` 0 errors; `dotnet publish -c Release -o publish\mcp` clean. |

## Sample manifest (trimmed — `PPID_CR_Legacy_CP_Apollo_BigBush.manifest.json`)

```json
{
  "schemaVersion": 1,
  "asset": {
    "objectPath": "/CR_Legacy/Playsets/PlaysetProps/PPID_CR_Legacy_CP_Apollo_BigBush.PPID_CR_Legacy_CP_Apollo_BigBush",
    "displayName": "CP_Apollo_BigBush",
    "exportType": "Prop",
    "sourceBoundsCm": { "sizeX": 1102.85, "sizeY": 1124.72, "sizeZ": 785.12, "units": "cm" }
  },
  "primaryMesh": "CP_BigBush.glb",
  "meshes": [
    { "name": "BigBushShadowProxy", "file": "BigBushShadowProxy.glb", "role": "shadow_proxy", "isPrimary": false,
      "sidecarFiles": [ { "file": "BigBushShadowProxy_LOD1.glb", "kind": "lod", "lodIndex": 1 } ] },
    { "name": "Default__CP_Apollo_BigBush_C", "file": "CP_BigBush.glb", "role": "render", "isPrimary": true, "numLods": 3,
      "sidecarFiles": [ { "file": "CP_BigBush_LOD2.glb", "kind": "lod", "lodIndex": 2 },
                        { "file": "CP_BigBush_LOD3.glb", "kind": "lod", "lodIndex": 3 } ],
      "materials": [
        { "slot": 0, "name": "CP_M_BigBush", "blendMode": "BLEND_Masked", "twoSided": true,
          "roles": { "diffuse": "Diffuse", "normal": "Normals", "mask": "MaskTexture", "lut": "ColorPalette" },
          "textures": [
            { "parameter": "ColorPalette", "file": "Apollo_Foliage_LUT_MountainPines.png", "sRGB": true,  "compressionSettings": "TC_Default" },
            { "parameter": "Diffuse",      "file": "T_Apollo_Medium_Leaf_D_Clr.png",       "sRGB": true,  "compressionSettings": "TC_Default" },
            { "parameter": "MaskTexture",  "file": "T_Apollo_Medium_Leaf_MASK.png",        "sRGB": false, "compressionSettings": "TC_Masks" },
            { "parameter": "Normals",      "file": "T_Apollo_Medium_Leaf_N.png",           "sRGB": false, "compressionSettings": "TC_Normalmap" }
          ],
          "baseMaterialDefaults": { "vectorCount": 37, "truncated": true, "vectors": [
            { "name": "Color1_Base",    "hex": "7C813A" },
            { "name": "Color2_Lit",     "hex": "545B21" },
            { "name": "Color3_Shadows", "hex": "35461C" } ] },
          "notes": [ "masked_blend", "opacity_in_mask_texture", "color_via_lut" ] } ] }
  ],
  "textureFiles": { "unreferencedOnDisk": [] },
  "notes": [ "shadow_proxy_present" ]
}
```

Tool output gained `manifestPath`, `primaryMesh`, `primaryMeshPath` and `manifestNotes` per asset; the manifest
file is also folded into `files[]` so `fileCount` stays truthful.

## Design notes

* **Nothing is faked into the pixels.** Diffuse PNGs are still written exactly as the exporter produces them
  (alpha 255). The manifest names which texture *parameter* drives opacity instead — `roles.mask` plus the
  `opacity_in_mask_texture` note — because writing invented alpha would corrupt the source data.
* **`primaryMesh` is asserted only when it is true.** One render-role mesh -> it is named. Several (an outfit's
  head/body/hat) -> `primaryMesh` is `null`, `primaryMeshCandidates` gives the ranking, and
  `import_all_render_meshes` says what to do. Shadow proxies, collision hulls, `*Skeleton` and LOD meshes are
  classified out of the render pool by `ExportManifest.ClassifyMesh`.
* **Base-material defaults are capped at 48 vectors / 48 scalars** (`DefaultsCap`), colour- and surface-looking
  names ranked first, with `vectorCount` / `scalarCount` / `truncated` reported. Uncapped, a Fortnite weapon
  uber-shader emitted 4,936 colours and 9,528 scalars — a 2.2 MB manifest. Capped it is 27 KB, and the BigBush
  tints still survive the cull. `get_properties_json` remains the route to the full set.
* **`FortnitePorting.Exporting` is untouched.** Two `ExportRunner` helpers (`ToDiskPath`, `EnumerateMeshes`)
  went from `private` to `internal`; that is the whole footprint outside the new file.
* **Manifest failure never fails an export.** `ExportManifest.WriteAsync` catches, logs a warning and returns
  null; the export still reports its files.

## Left open

* **`sourceBoundsCm` is UE's authored `FBoxSphereBounds`**, not the tight bounds of the exported geometry.
  JuniperHedge reports 439.3 / 226.1 / 210.9 cm where Blender measures the exported mesh at 436.2 / 209.7 /
  201.6 cm. Use it as a sanity check, not as an import scale reference.
* **Vector parameters on a material instance are overrides only.** `CP_M_BigBush` overrides no colours, so its
  `vectors[]` is empty; the tints come from `baseMaterialDefaults`. Consumers must read both.
* **Manifests are large for multi-material assets** (BigBush 91 KB, outfit 164 KB). They are meant to be parsed,
  not pasted into a prompt — read `primaryMesh` and `meshes[].materials[].textures[]` and ignore the rest.
* **1x1 placeholder textures are flagged, not skipped.** Each texture entry carries `bytes` and
  `placeholder: true` under 512 bytes; the exporter still writes them.

---

# Manifest schema v2 — channel semantics and build stamp (2026-08-31, later same day)

Four asks from the UEFN round-2 validation (`FortniteAssetExports/_uefn_staging/_validation/RESULTS.md`
§19). That round proved empirically that a consumer cannot safely infer Fortnite's texture conventions
from the manifest as it stood: mis-reading an `_S` map as R=ambient-occlusion darkened three assets by
0.31–0.72x, and the opacity channel of a foliage mask had to be found by dumping channels by hand.
`schemaVersion` is now **2**.

## What changed

| Ask | Field | Rule |
|---|---|---|
| Channel semantics | `meshes[].materials[].textures[].channelSemantics` | `SpecularMasks`/`_S` family → `{R:Specular, G:Metallic, B:Roughness}`; foliage `MaskTexture`/`_M` family → `{R:OpacityMask, G:Shading}`. Matched on **parameter name** (trailing `_2`/`_3` layer index stripped), omitted entirely for anything else. |
| Opacity channel | `meshes[].materials[].opacityChannel` | `"R"`, emitted only when the material carries `opacity_in_mask_texture` **and** its mask-role parameter is the foliage family. A `SpecularMasks` map carries no opacity, so it gets the note and no channel rather than a wrong one. |
| hex is sRGB | `hexEncoding: "sRGB"` beside every `hex`, plus root `schemaNotes.hex` | Chose annotation over emitting a second `hexLinear`: the linear values are already there as `r`/`g`/`b`, so a second encoding would be a third thing to get wrong. |
| Layer disambiguation | `distinctTextureFiles` + `layers_share_textures` note | Emitted for a material the name calls layered (`layered_material_N`) **or** one whose parameters carry a numbered layer set (`Diffuse_Texture_2`…). The note fires when every diffuse layer names one file. |
| Build stamp | `generator` (now an object) + `get_status.server` | `StampBuild` MSBuild target writes `AssemblyMetadata("BuildTimestampUtc")` and `("GitCommit")`; `McpServerInfo.BuildStamp` renders them. |

Root `schemaNotes` documents each of the above; `guidance` was corrected — it previously stated the
round-1 packing (`B=ambient occlusion`), which is exactly the error round 2 disproved.

## Verification (all against the freshly published exe, commit ffe69f1843)

* `--selftest` **PASSED in 5.7s**, 12 tools registered.
* `get_status` → `server: { version 1.0.0, buildTimestampUtc "2026-09-01T03:44:02Z", gitCommit
  "ffe69f1843", buildStamp "fortnite-porting 1.0.0, commit ffe69f1843, built 2026-09-01T03:44:02Z",
  manifestSchemaVersion 2 }`. A stale publish now shows itself before an export, not after.
  `publish/mcp` was then re-published from the commit itself and re-checked (`--selftest` PASSED,
  stamp `commit f19fcfeec2, built 2026-09-01T03:47:11Z`), so the shipped exe reports the commit it
  was actually built from rather than that commit's parent. The manifests below were written by the
  pre-commit build of identical source and still read `ffe69f1843` — which is the feature working.
* Re-exported ApolloBigBush, ApolloHedgeCube, DojoGateWall, GreenhouseWall — all four manifests
  `schemaVersion: 2` with the same generator stamp.
* **channelSemantics**: `SpecularMasks`, `_2`, `_3`, `_4` on DojoGateWall and `_1`–`_3` on
  GreenhouseWall all carry `{R:Specular, G:Metallic, B:Roughness}`; `MaskTexture` on all three
  BigBush materials and on `CP_MI_Apollo_Bush` carries `{R:OpacityMask, G:Shading}`. Nothing else
  is annotated — `Diffuse`, `Normals`, `ColorPalette`, `Position`, `X-Axis` and, deliberately,
  `MaskTexture_OpaqueCanopy` (a canopy layer whose packing was never measured) are all left bare.
* **opacityChannel R** on `M_BigBush`, `CP_M_BigBush` and `CP_MI_Apollo_Bush` — the three materials
  flagged `opacity_in_mask_texture`. Absent on every opaque material.
* **Layer disambiguation**: GreenhouseWall `MI_LabRat_Wall_3layer_Inst` →
  `distinctTextureFiles: 3` across 9 parameters + `layers_share_textures`, which is the manifest
  finally saying in one field what §17 needed a channel investigation to establish. DojoGateWall
  `MI_Asteria_Dojo_Wall_B` → `distinctTextureFiles: 9` across 12 parameters and **no** share note:
  its layers are genuinely distinct.
* **hexEncoding**: 284 `hex` fields across the four v2 manifests, 0 without `hexEncoding`.
  `Color1_Base` reads `7C813A` / linear `0.20156, 0.21953, 0.04231` — the exact 2.4x trap from §14,
  now labelled at the point of use.

## Left open

* **The mapping is a convention, not a read of the source graph.** It is keyed on parameter names
  that were measured in one validation set. An unfamiliar packing gets no `channelSemantics` rather
  than a guess, so absence means "unknown", never "no semantics".
* **`MaskTexture_OpaqueCanopy` and other suffixed variants are not annotated.** Only a trailing
  numeric layer index (`_2`, `_3`) is stripped before matching; a named variant is treated as a
  different, unmeasured family.
* **`distinctTextureFiles` counts files, not blend behaviour.** Round-2 ask #3 also wanted the layer
  *mask* source (vertex colour channel / height map); the export does not carry it, so a consumer
  still cannot tell how the layers combine — only whether combining them could matter.
* **Asks #2 (`role: primary|layer2|canopy`) and #4 (`reads_vertex_color`) are not implemented.**
* **`generator` changed from string to object.** That is a breaking read for a v1 consumer, which is
  what the `schemaVersion` bump is for.
* **Every build recompiles this project**, because the stamp target rewrites the generated
  AssemblyInfo each time. Intentional: an exe that reports a build time it did not have would defeat
  the point.

---

# `--dump-index` — the grep-first asset index (CE-1)

`FortnitePorting.Mcp.exe --dump-index <outDir> [--tier a|b] [--bounds core|all|none]` writes an
`index/` dataset that a **customer agent holding only the stock UEFN editor MCP** greps to get from
a human sentence to a placed prop. It answers the three things that MCP cannot: what string to
search for, which of a prop's three identities each call wants, and how big the thing is.

## Why three identities

A creative prop is three different objects and the editor MCP treats them differently. These were
established by driving the live editor, not inferred from the archive, and they are written into the
generated `atlas.md` so the consumer never has to rediscover them by failing:

* **`ppid`** — the `FortPlaysetPropItemDefinition` full object path. `add_to_scene_from_asset` on it
  places and **auto-resolves to the creative prop BP actor**. Preferred placement identity.
* **`bp`** — the blueprint **class** path, `_C`-suffixed. The bare package path *fails to load*, so
  the column is written in class form and must not be trimmed.
* **`sm`** — the static mesh package path. Places as a bare `FortStaticMeshActor`, and is the only
  one of the three `CaptureAssetImage` will render. **Capture never works on a PPID**;
  `GetAssetThumbnails` returns empty for unloaded plugin content (rendering is live-only).
* Every `find_assets` call must be scoped with `folder_path` — unscoped is the documented way to
  hang the editor.

## Row schema (v1)

```json
{"id":"PPID_Burd_Comp_BP_Helios_JuniperHedge_Straight_d4d748db",
 "name":"BP_Helios_JuniperHedge_Straight",
 "ppid":"/Burd_Comp/SetupAssets/Maps/PPIDs/PPID_Burd_Comp_BP_Helios_JuniperHedge_Straight_d4d748db.PPID_Burd_Comp_BP_Helios_JuniperHedge_Straight_d4d748db",
 "bp":"/Game/Environments/Asteria/Foliage/Hedges/JuniperHedges/Blueprints/BP_Helios_JuniperHedge_Straight.BP_Helios_JuniperHedge_Straight_C",
 "sm":"/Game/Environments/Asteria/Foliage/Hedges/JuniperHedges/Meshes/SM_JuniperHedge_straight_fallback",
 "sz":[439,226,211],"sc":"burd_comp",
 "gal":["PID_FNEC_Burd_Gallery_c","PID_FNEC_Burd_Prefab_b","PID_FNEC_Desert_M_Prefab_Hotel",
        "PID_FNEC_Desert_M_Prefab_Park","PID_FNEC_Desert_PropGallery_e",
        "PID_FNEC_Harbor_PropGallery_a","PID_FNEC_Utopia_Gallery_c"],
 "kw":["burd","city","deca","desert","gas","harbor","heatwave","hedge","helios","hotel","juniper",
       "outdoor","park","prefab","sandy","square","station","store","straight","strip","town","utopia"]}
```

Keys are terse on purpose — the file is designed to be grepped whole, so every byte of key name is
paid for 26,620 times. `sz` is the static mesh's render bounds in whole centimetres. `sc` joins to
`scopes.tsv`. `kw` is name + gallery-name + theme + creative-tag tokens, camel-case split and
lowercased, which is what makes plain English hit an asset called `BP_Helios_JuniperHedge_Straight`.

`galleries.jsonl` rows are `{id, name, asset, sc, n, src, kw}`; a gallery's contents are found by
grepping its `id` in `props-*.jsonl`, so the member list is never stored twice.

## Pipeline and what each phase costs

Measured on Fortnite **42.00**, 569,456 registry rows, 12-way parallel, warm name-index cache.
Archive mount (4.6 s) is excluded; it is the same mount every CLI mode pays.

| phase | what it does | tier b / bounds core |
| --- | --- | ---: |
| P1 canonical | `AssetQuery.CanonicalAsync(Prop)` — 105,512 registry rows → 26,620 deduped (78,892 folded) | 0.5 s |
| P2 galleries | all 2,169 `FortPlaysetItemDefinition`, one package load each; members read as `FSoftObjectPath` **strings** and joined by path (never `TryLoad`ed) | 2.7 s |
| P3 placement | per prop: one package load, `ActorSaveRecord` → `TemplateRecords[*].ActorClass` soft path → `_C` class path | 56.2 s |
| P4 mesh + bounds | blueprint load → SCS / `InheritableComponentHandler` / CDO / parent-class walk → first `StaticMesh`; mesh load for render bounds | (same pass) |
| P5 scopes | distinct mount prefixes, merged with `Config/mount-verification.json` | 0.2 s |
| P6 write | seven files | 5.2 s |
| **total** | | **64.9 s** |

`--tier a --bounds none` (no mesh loads, no bounds) writes the same row set in **43.7 s**. Tier
controls the blueprint/mesh hop; `--bounds` controls the separate static-mesh load that measures it.

Loading gallery members as strings rather than `TryLoad`ing them is what keeps P2 at 2.7 s: a
gallery's members are props P3 opens anyway, so loading them here would double the archive work for
nothing. The save-record-collection walk is kept only as a fallback for galleries whose
`AssociatedPlaysetProps` is empty — merging both sources unconditionally double-counts every prop.

## Coverage

| | count | of 26,620 |
| --- | ---: | ---: |
| rows shipped | 26,620 | 100% |
| with `bp` | 26,620 | 100% |
| with `sm` | 24,834 | 93.3% |
| with `sz` | 24,622 | 92.5% |
| galleries | 2,169 | — |
| scopes | 273 | — |
| failure lines | 1,787 | — |

Failures are almost entirely one cause: **1,784 blueprints expose no `StaticMesh`** through the SCS,
the inheritable-component handler, the CDO or the parent class — particle-only props, splines and
volumes. Plus 2 blueprint classes that would not load and 1 gallery package
(`JunoPlotPlaysetItemDefintion`) that would not load. Every one of those rows still ships with its
`ppid`, `name`, `kw` and galleries; only `sm`/`sz` are null, and `dump-report.log` carries a line
per row saying why.

## Verification

* **Spot row — JuniperHedge straight.** `ppid` is the known `/Burd_Comp` PPID, `bp` ends
  `.BP_Helios_JuniperHedge_Straight_C`, `sm` is
  `/Game/Environments/Asteria/Foliage/Hedges/JuniperHedges/Meshes/SM_JuniperHedge_straight_fallback`,
  `sz` **`[439, 226, 211]`** — the expected value exactly. Full row above.
* **Spot row — Apollo_BigBush.** `bp`
  `/Game/Athena/Apollo/Environments/BuildingActors/Foliage/Apollo_BigBush.Apollo_BigBush_C`, `sm`
  `/Game/Environments/Apollo/Foliage/BigBush/Meshes/BigBushShadowProxy`, `sz` `[1142, 1125, 785]`,
  6 galleries.
* **Spot row — Battlewood Boulevard Nature Gallery.** `n: 52`, `src: "associated"` — the expected
  member count.
* **Grep tests.** `grep -i hedge props-full.jsonl` → **109** rows (target >20).
  `grep -i battlewood galleries.jsonl` → **8** (target 8).
* **Determinism.** Two consecutive dumps into different directories: `atlas.md`, `scopes.tsv`,
  `galleries.jsonl`, `props-core.jsonl`, `props-full.jsonl` and `dump-report.log` **byte-identical**;
  `META.json` differs only in `generatedUtc`. Rows are sorted `(name, ppid)`, ids are handed out
  after the sort, scope ids are stable and sorted by UEFN path, and keyword lists are sorted.
* **`--selftest` PASSED in 4.9 s**, 12 tools registered.

## Sizes

| file | raw bytes | gzip -9 |
| --- | ---: | ---: |
| `META.json` | 420 | 272 |
| `atlas.md` | 35,449 | 10,941 |
| `scopes.tsv` | 28,212 | 8,465 |
| `galleries.jsonl` | 550,684 | 68,902 |
| `props-core.jsonl` | 18,374,034 | 1,812,201 |
| `props-full.jsonl` | 18,486,006 | 1,823,279 |
| `dump-report.log` | 206,805 | 20,385 |
| **total** | **37,681,610** | **3,743,517** (whole dir, deflate) |

## Mount verification

`scopes.tsv` merges the generated mount table with hand-maintained `Config/mount-verification.json`,
which ships next to the exe so an operator can record a newly verified mount after a live probe
without rebuilding. `unverified` is the default and means **untested, not broken** — the dump can
prove a path exists in the archive, never that UEFN will accept it. Two mounts are currently
verified, both from live editor probes:

| scope | UEFN path | rows | verified |
| --- | --- | ---: | --- |
| `game.environments` | `/Game/Environments` | 13,624 | find + capture + place |
| `burd_comp` | `/Burd_Comp` | 146 | resolve + place |

A row is counted against **every** mount it reaches — its PPID's, its blueprint's and its mesh's —
not just its PPID's. Without that, neither verified mount would appear in the table at all: a prop's
PPID lives in a composition plugin (`/Burd_Comp`) while its blueprint and mesh live in shared
environment content (`/Game/Environments`), and it is the second one an agent scopes a search to.

## Left open

* **`props-core.jsonl` is 99.2% of `props-full.jsonl` (26,402 of 26,620) and earns nothing.** The
  specified definition is "gallery members ∪ curated families", and it was measured: 26,237 of
  26,620 props belong to at least one gallery (86,665 membership links), so the union degenerates to
  "almost everything". Narrower definitions were measured too and do not help — restricting to
  galleries whose *name* contains "Gallery" still gives 25,013; deduplicating by blueprint gives
  26,454 distinct blueprints out of 26,620 rows, because `CanonicalAsync` already folded the 78,892
  clones away. The generated atlas now says this in its file table and tells the reader to grep
  `props-full.jsonl` instead. **CE-3 should either drop `props-core.jsonl` or redefine it** —
  curated-families-only is 12,991 rows and is the only cut measured that halves the file.
* **`name` is the game's own display name, which for creative props is usually an engineering name**
  (`BP_Helios_JuniperHedge_Straight`, `Apollo_BigBush`). It is left verbatim rather than prettified,
  because it is the string the archive actually holds; `kw` is what carries the plain English.
* **`ULevelSaveRecord.HalfBoundsExtent` is zero on all 26,620 props.** It looked like a free
  per-prop placement footprint that would cover rows whose mesh never resolves. It was implemented,
  measured, found dead, and removed — a `PropMeshResolver` comment records the measurement so nobody
  tries it again. Rows without a mesh therefore ship with no size at all.
* **`sm` is the *first* static mesh a blueprint exposes**, not all of them. A multi-mesh prop (a shed
  plus its door) gets one representative mesh and that mesh's bounds, which can understate the
  prop's true footprint.
* **Bounds are the mesh's, not the actor's.** Component transforms in the blueprint (scale, offset)
  are not applied, so a prop whose SCS scales its mesh reports the unscaled size.
* **Only `Prop` is indexed.** Prefabs appear as galleries (name + membership + scope) but have no
  rows of their own, and no other category is covered.
* **Only two mounts are verified**, and both verifications are single-asset probes. 271 of the 273
  scopes are `unverified`, which is honest but means the scope table cannot yet tell a consumer
  which mounts are safe to search.
