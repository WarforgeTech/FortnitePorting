# FortnitePorting → UEFN Import Pipeline: Validator Test Series

Purpose: a validator agent runs these tests to verify that assets exported by the
`fortnite-assets` MCP server and imported into a UEFN project came over correctly —
geometry, scale, materials, textures, placement — and reports PASS/WARN/FAIL per asset.

## The pipeline under test (proven 2026-08-31 on BP_Helios_JuniperHedge_Straight)

1. `fortnite-assets` MCP: `export_assets {objectPaths, outputDir, meshFormat:"Gltf2"}` → `.glb` (+LOD glbs) + PNG texture set per asset.
2. Blender headless: `blender-launcher.exe -b --python glb2fbx.py -- <in.glb> <out.fbx>`
   (script at `FortniteAssetExports\_uefn_staging\glb2fbx.py`; MS Store Blender launcher at
   `%LOCALAPPDATA%\Microsoft\WindowsApps\BlenderFoundation.Blender_ppwjx1n5r4v9t\blender-launcher.exe`).
   MUST use `apply_scale_options="FBX_SCALE_NONE"` — `FBX_SCALE_ALL` produces 100× too-small meshes (verified failure + fix).
   The script writes `<out.fbx>.done` JSON with per-mesh dimensions in meters = the scale ground truth.
3. UEFN MCP (`http://127.0.0.1:8000/mcp`, toolsets via unreal-mcp):
   - `StaticMeshTools.import_file` (FBX/OBJ ONLY — .glb rejected by FbxFactory, verified) → StaticMesh under `/AssetImports/FortPorting/<AssetName>/`.
   - `TextureTools.import_file` per PNG → Texture2D.
   - Material: instance `M_FortPort` (`/AssetImports/FortPorting/Core/M_FortPort`) — master with
     TextureSampleParameter2D params `Diffuse` (Color, RGB→BaseColor, A→OpacityMask), `Normal` (Normal→Normal),
     `OSSR` (LinearColor, R→AO, G→Specular, A→Roughness); BLEND_Masked, TwoSided.
     `MaterialInstanceTools.create` + `set_texture_parameter` per texture, `StaticMeshTools.set_material` per slot.
   - `SceneTools.add_to_scene_from_asset` (snap_to_ground) + `set_actor_folder "FortPorting/Imported"`.
4. `AssetTools.save_assets []` after every asset batch (session-death insurance + thumbnail refresh).

## Known traps the validator must respect

- **Stale thumbnails**: `CaptureAssetImage`/`GetAssetThumbnails` can show the pre-material (gray) state.
  Visual verification MUST use `CaptureViewport` after `FocusOnActors` on a placed instance (verified live: thumbnail gray, viewport correct).
- `ObjectTools.set_properties` takes `instance` + `values` where values is a **JSON string**, not an object.
- Unknown MCP arguments are silently ignored; on odd results re-read the tool schema.
- Never run an unscoped asset search; scope `find_assets` to `/AssetImports/...`.
- Save with `save_assets []` before any Verse build; session id dies only on editor restart.
- One mutating agent in the editor at a time.
- bash → `--call` JSON args: use forward slashes inside JSON paths.

## Test series (per asset A, with source objectPath P)

Reference data comes from the fortnite-assets server: `get_asset_info P`, `get_asset_icon P` (reference image),
`get_properties_json P` (source truth), plus the exporter's returned file list and glb2fbx `.done` dimensions.

| ID | Test | Method | Pass criteria |
|----|------|--------|---------------|
| V1 | Asset exists & class | `AssetTools.exists` + `get_asset_class` on `/AssetImports/FortPorting/<A>/SM_<A>` | exists, class `StaticMesh` |
| V2 | Scale fidelity | `StaticMeshTools.get_bounds` extents (cm) vs `.done` mesh_dimensions_m ×100 | each axis within 2% |
| V3 | Geometry integrity | `get_triangle_count`/`get_vertex_count` LOD0 vs Blender-reported counts (extend .done) | within 5% (import may split verts) |
| V4 | Material slots | `get_material_slots` count vs source material count (glb materials / source properties) | equal; every slot assigned a `MI_*` under /AssetImports/FortPorting (not WorldGridMaterial/default) |
| V5 | Texture set complete | `find_assets` in asset folder, type Texture2D, vs exporter PNG list | every exported diffuse/normal/mask PNG imported; `TextureTools.get_size` matches source PNG dims |
| V6 | Material wiring | `MaterialInstanceTools.get_texture_parameter` Diffuse/Normal(/OSSR) on each MI | Diffuse+Normal non-null and pointing at this asset's textures |
| V7 | Placement & outliner | `SceneTools.find_actors name:"FP_<A>"` + folderPath + bounds.min.z | exactly 1 actor, folder `FortPorting/Imported`, on/near ground (min.z > -50, < 100) |
| V8 | VISUAL: in-level render | `FocusOnActors` → `CaptureViewport` → vision inspection vs `get_asset_icon P` reference | Same silhouette & proportions; dominant color family matches reference; textures visibly present (no uniform gray, no checkerboard, no magenta); alpha-masked parts show gaps not solid quads; report a 1-line verdict + saved screenshot path |
| V9 | VISUAL: multi-angle | 2nd `CaptureViewport` with `captureTransform` from behind/above | no missing backfaces (TwoSided working), no inside-out normals (mesh not "hollow") |
| V10 | No collateral damage | `find_assets /AssetImports/FortPorting` count delta; editor log tail via LogsToolset for new errors | only expected new assets; no repeating error spam tied to the import |

Suite-level checks (once per run):
- S1: `save_assets []` returns true at end; `is_dirty` false on spot-checked assets.
- S2: Actor count in `FortPorting/Imported` == assets imported this run (get_actors_in_folder).
- S3: Editor still responsive (`GetContentBrowserPath` answers) — session survived the batch.
- S4: Evidence bundle: every V8/V9 screenshot saved under `FortniteAssetExports\_uefn_staging\_validation\<A>\` with a RESULTS.md matrix.

## Grading
- FAIL: V1, V2, V4, V6, V7 misses, or V8 shows gray/magenta/checkerboard or wrong silhouette.
- WARN: V3 out of tolerance, V5 missing non-critical masks, V9 backface artifacts, dark/washed lighting judgment calls.
- PASS: everything else. Report per-asset row + suite summary; iterate fixes (material wiring, scale flags) and re-run failed rows before sign-off.

## Current test-set status

| Asset | Source objectPath (short) | Status |
|---|---|---|
| BP_Helios_JuniperHedge_Straight | PPID_Burd_Comp_...JuniperHedge_Straight_d4d748db | V1,V2,V4,V6,V7,V8 PASS (pilot, this doc's proof run); V3,V5,V9,V10 pending formal run |
| (batch set: topiary hedge, AresArena pillar, Fortilla corrugated wall/gate, RustBucket floor, Battlewood BigBush, stone pillar, + 1 misc) | — | queued |
