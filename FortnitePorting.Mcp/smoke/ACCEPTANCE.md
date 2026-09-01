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
