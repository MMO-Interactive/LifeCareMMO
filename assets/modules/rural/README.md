# Rural Town Module Pack

205 modular pieces for the rural starter district. They snap to the construction grid in
`LifeCare.Construction`: 4 m tiles, 3.2 m stories, 0.2 m floor slabs and walls.

Every module is generated from code by
[`tools/blender/rural_modules/build_rural_pack.py`](../../../tools/blender/rural_modules/build_rural_pack.py). To change a
module, edit the script and regenerate; don't hand-edit the GLBs.

```powershell
./tools/Build-RuralModulePack.ps1            # everything: GLBs, manifest, .blend, previews (about 20 s)
./tools/Build-RuralModulePack.ps1 -NoRender  # GLBs, manifest and .blend only
```

## Contents

| Path | What |
|---|---|
| `glb/<category>/<id>.glb` | One mesh per module, Y-up, metres, with transforms applied |
| `manifest.json` | Each module's id, name, category, attach type, footprint, bounds, triangle count, materials and gameplay tags |
| `rural_modules.blend` | Source scene: every module, grouped by category, plus the sample town |
| `previews/sheet_*.png` | Catalog sheets, one per category (walls have one per siding style) |
| `previews/town_*.png` | The sample town built only from these modules |

## Placement conventions

glTF is Y-up. Each module's front, the exterior side, faces **+Z**. Rotate modules about their origin in 90° steps.

| Attach | Origin | Notes |
|---|---|---|
| `tile` | Tile centre; y = 0 at the level base (floors) or ground (terrain, roads) | Floor slabs run 0–0.2 m, matching `LotGeometry.Floor` |
| `edge` | Edge midpoint at the level base | Runs along X; walls fill 0.2–3.2 m, centred on the edge, with the exterior at +Z |
| `vertex` | Grid vertex | The outer corner fills the −X/+Z quadrant |
| `roof` | Footprint centre at the wall top (`LevelElevation(level + 1)`) | 6:12 pitch, 0.3 m eaves; gable and hip roofs span 2 tiles (8 m) |
| `free` | Base centre | Wall-mounted items put the wall face at z = 0 and project toward +Z |

How the families fit together:

- **Walls**: 6 siding styles × 11 types (full wall, 5 doors, 5 windows), plus a half wall, inner and outer corners, gable infill and shed infill in each style.
- **Gable roofs**: an `end` at each end (rotate the far one 180°) and a `mid` between them. Add `gable_infill_<style>` under each end at roof level.
- **Hip roofs**: `hip_end` at each end with `gable_mid` between, or `hip_2x2` on its own.
- **Roads**: two `road_lane` tiles, the second rotated 180°, make a 16 m street together with `sidewalk` tiles (curb at −Z after rotation). Use `road_plain` for intersections and `sidewalk_corner` at corners.
- **Raised buildings**: put `floor_raised_wood` on a lot base lifted 0.4 m, run `skirt_lattice` around the perimeter, and use `ramp_2x1` (rises 0.6 m) or `steps_3`.

## Engine notes

- The current `GltfMeshFileImporter` reads positions, normals, UVs and tangents and merges every primitive into one mesh. **It ignores materials.** Each GLB still carries its `LC_*` PBR materials, and the palette is listed in `manifest.json`. Showing those colours in-engine needs material support in the importer, or vertex-colour baking.
- The largest module is about 5k vertices, well under the importer's 16-bit index limit.
- UVs are box-projected at 2 m per texture repeat, ready for tiling textures. Signs are blank: no text or decals yet.
