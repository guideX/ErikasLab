# Erika Asset Audit (Phase 2A)

Investigation-only phase. No character rendering, animation playback, package changes,
or architectural changes were made. Source FBXs under `erika/` were opened read-only
and never modified or converted.

## 1. Preflight (recorded before any change)

- Repository path: `D:\dev\ErikasLab`
- Branch: `main` (tracks `origin/main`, up to date)
- HEAD SHA: `8d2de24053d86717dd81521579665e9eb790b177`
- HEAD subject: `...` (single-commit history at time of audit)
- Note: the baseline `6be4c70e1382487a1849a009c74637c44ec182ea` from the phase brief
  does not exist in the local object store; work proceeded from the actual HEAD above.
- Worktree status at start: clean (`git status`: "nothing to commit, working tree clean").
- Remote: `origin  git@github.com:guideX/ErikasLab.git` (fetch + push).
- `erika/` ignore confirmed: `.gitignore` contains `/erika/`; `git check-ignore -v erika`
  resolves to that rule; `git ls-files` lists no `erika/` entries.
- Tracked files at start: solution, props, README, and 22 `.cs`/`.csproj` files only.
  No tracked production changes existed, so nothing was overwritten.

## 2. Existing architecture (Phase 1, unchanged)

- `ErikasLab.Engine` (`net8.0`): portable timing, input, camera, scene, mesh,
  renderer contracts (`IRenderer`, `Scene`, `MeshData`, `Transform`, ...).
- `ErikasLab.Game` (`net8.0`, refs Engine only): `GameSession`, code-first test world.
- `ErikasLab.Platform.MonoGame` (`net8.0-windows7.0`, `win-x64`): the only project
  referencing `MonoGame.Framework.WindowsDX 3.8.4.1`; adapts portable scene data to
  vertex/index buffers with `BasicEffect`. No `.mgcb`, no Content project, no content
  pipeline wiring — meshes are generated in code.
- Dependency check: grep over `src/ErikasLab.Engine` and `src/ErikasLab.Game` for
  `MonoGame|Xna|Windows|GraphicsDevice|DllImport` returns zero hits. Engine/Game
  remain free of MonoGame and Windows dependencies. Not refactored in this phase.

## 3. Asset inventory — every file inspected, not just named

Method: read-only binary FBX parsing (header/version, node census, Model/Geometry/
Deformer/Material/Texture/Video/Pose/Take records, OO/OP connection maps, GlobalSettings,
KeyTime array decode for durations, SHA-256 over geometry data for identity checks).
Nothing was written to or converted from `erika/`.

Common facts (all 37 files):

- Format: binary FBX, version 7700, writer `FBX SDK/FBX Plugins version 2020.2`.
- Source DCC paths embedded in the files point at a Mixamo authoring machine
  (`.../Mixamo/Characters/Erika Archer...`), and every file carries takes
  `Take 001` + `mixamo.com` with animation stacks of the same names. Mixamo origin confirmed.
- Every file is self-contained: full skinned mesh set + full skeleton + exactly one
  animation clip + embedded textures. There is no separate "model-only" file.
- All 37 files are byte-distinct (SHA-256 whole-file hashes all differ — including
  `running.fbx` vs `running2.fbx` and left vs right strafe, which share sizes but not content).
- Total: 37 files, 752,810,192 bytes (~752.8 MB decimal).

Two variants exist (proven by geometry hashes + Maya scene paths + bone census):

- **Base variant (33 files):** 4 meshes, 67 `LimbNode` bones, 4 `Skin` + 268 `Cluster`
  deformers (67 bones x 4 meshes), 4 `BindPose` nodes, 4 Phong materials,
  6 embedded texture slots, 315 animation curves / 54 curve-nodes per file.
- **Bow variant (4 files):** 6 meshes (adds Bow + Arrow), 70 `LimbNode` bones
  (+3 prop bones), 6 `Skin` + 420 `Cluster` deformers, 6 `BindPose` nodes,
  6 Phong materials, 10 embedded texture slots, 321 curves / 55 curve-nodes per file.
  The bow mesh set is a distinct re-export (see section 4) — not base meshes + props.

### 3.1 Base variant (33 files)

Geometry (identical bytes in every base file — hash-verified on a 4-file sample,
array sizes identical in all 33): control points = Vertices floats / 3.

| Mesh | Control points | Polygon indices | Role |
| --- | --- | --- | --- |
| `Erika_Archer_Body_Mesh` | 3,465 | 12,808 | body |
| `Erika_Archer_Eyelashes_Mesh` | 6,427 | 25,726 | eyelashes |
| `Erika_Archer_Eyes_Mesh` | 764 | 3,120 | eyes |
| `Erika_Archer_Clothes_Mesh` | 98 | 214 | clothes/archer garb |

Clip table (`dur` decoded from KeyTime, FBX ticks = 46,186,158,000/s; `fr` = duration at
30 fps; keys = total KeyTime entries across all curves):

| File | Bytes | Likely role | Dur (s) | Fr | Keys |
| --- | --- | --- | --- | --- | --- |
| `crawl.fbx` | 19,832,960 | crawl | 2.333 | 70 | 22,227 |
| `death.fbx` | 19,870,720 | death | 4.400 | 132 | 31,722 |
| `death2.fbx` | 20,004,544 | death alt | 4.933 | 148 | 45,603 |
| `death3.fbx` | 19,791,136 | death alt | 2.267 | 68 | 18,741 |
| `death4.fbx` | 19,813,712 | death alt | 2.333 | 70 | 20,739 |
| `diagnol_wall_run.fbx` | 19,686,304 | wall-run (diagonal; sic filename) | 1.133 | 34 | 10,005 |
| `fall_flat.fbx` | 19,859,424 | fall / land flat | 2.533 | 76 | 24,432 |
| `fall_loop.fbx` | 19,670,240 | falling loop | 1.067 | 32 | 8,667 |
| `flip_jump.fbx` | 19,808,160 | acrobatic jump | 2.100 | 63 | 20,160 |
| `forward_wall_run.fbx` | 19,803,632 | wall-run | 2.100 | 63 | 19,782 |
| `freehang_climb.fbx` | 19,966,736 | climb | 3.867 | 116 | 33,375 |
| `idle_looking_around.fbx` | 19,980,416 | idle (**recommended canonical**, see 4) | 4.000 | 120 | 34,515 |
| `inverted_double_kick_to_kip_up.fbx` | 19,936,352 | acrobatic kick | 3.200 | 96 | 31,020 |
| `jogging.fbx` | 19,640,336 | locomotion | 0.700 | 21 | 6,174 |
| `jump.fbx` | 19,642,848 | jump | 0.867 | 26 | 6,384 |
| `jump_backwards.fbx` | 19,668,304 | jump | 0.867 | 26 | 8,505 |
| `jump_standing.fbx` | 19,712,576 | jump | 1.467 | 44 | 12,195 |
| `jump_up.fbx` | 19,655,520 | jump | 0.833 | 25 | 7,440 |
| `land_and_run.fbx` | 19,675,856 | land/run | 0.933 | 28 | 9,135 |
| `land_standing.fbx` | 19,694,768 | land | 1.100 | 33 | 10,710 |
| `left_strafe_walking.fbx` | 19,684,976 | strafe | 1.033 | 31 | 9,894 |
| `melee_kick.fbx` | 19,709,344 | melee | 1.433 | 43 | 11,925 |
| `right_strafe_walking.fbx` | 19,684,976 | strafe (same size as left, different bytes) | 1.033 | 31 | 9,894 |
| `run_and_flip.fbx` | 19,799,168 | acrobatic run | 2.233 | 67 | 19,410 |
| `running.fbx` | 19,641,840 | locomotion | 0.633 | 19 | 6,300 |
| `running2.fbx` | 19,641,840 | locomotion alt (same size/length as running, different bytes) | 0.633 | 19 | 6,300 |
| `running_backwards.fbx` | 19,640,480 | locomotion | 0.633 | 19 | 6,186 |
| `running_up_stairs.fbx` | 19,633,520 | locomotion | 0.600 | 18 | 5,607 |
| `somersault.fbx` | 19,832,240 | acrobatic | 2.267 | 68 | 22,407 |
| `standing_up.fbx` | 20,061,680 | get up (longest clip) | 6.067 | 182 | 52,185 |
| `walking.fbx` | 19,673,808 | locomotion | 1.033 | 31 | 8,964 |
| `walking_backwards.fbx` | 19,648,320 | locomotion | 0.967 | 29 | 6,840 |
| `walking_backwards_cautious.fbx` | 19,707,824 | locomotion | 1.467 | 44 | 11,799 |

### 3.2 Bow variant (4 files)

Meshes (internally identical between the 4 files; all differ from base):

| Mesh | Control points | Polygon indices |
| --- | --- | --- |
| `Erika_Archer_Body_Mesh` | 212 | 804 |
| `Erika_Archer_Eyes_Mesh` | 62 | 170 |
| `Erika_Archer_Bow_Mesh` | 764 | 3,120 |
| `Erika_Archer_Arrow_Mesh` | 3,465 | 12,808 |
| `Erika_Archer_Clothes_Mesh` | 98 | 214 |
| `Erika_Archer_Eyelashes_Mesh` | 6,427 | 25,726 |

| File | Bytes | Likely role | Dur (s) | Fr | Keys |
| --- | --- | --- | --- | --- | --- |
| `draw_arrow.fbx` | 25,177,008 | bow draw (**recommended bow canonical**, see 4) | 1.033 | 31 | 8,598 |
| `walk_backward_aiming.fbx` | 25,198,064 | aim-walk | 1.467 | 44 | 10,353 |
| `walk_sideways1_aiming.fbx` | 25,176,176 | aim-walk | 1.200 | 36 | 8,529 |
| `walk_sideways2_aiming.fbx` | 25,184,384 | aim-walk | 1.300 | 39 | 9,213 |

Notable differences: bow files carry 3 extra bones, 2 extra meshes, 2 extra skins/
bind-poses, 4 extra texture slots, and 6 extra curves (321 vs 315). Their Body/Eyes
geometries are lower-density than base while Arrow/Bow reuse the base Body/Eyes
*array sizes* with different content (hashes differ) — i.e. a separate character
export, not a base export with props attached.

## 4. Canonical character recommendation

**Recommended canonical base model: `idle_looking_around.fbx`.**
**Recommended bow model: `draw_arrow.fbx`.**

Evidence:

- All 33 base files contain byte-identical mesh data (SHA-256 over Vertices/Normals/
  UV/index arrays matches across the sampled files; array sizes match in all 33), so
  any base file supplies the same bind-pose geometry. `idle_looking_around.fbx` is
  preferred because it is a near-neutral standing motion (safest visual reference),
  the longest neutral clip (4.0 s / 120 frames), and a base-variant file.
- The canonical skeleton is the shared 67-bone `mixamorig:*Model` hierarchy
  (section 5). No file is a neutral T-pose export — every file stores a mid-motion
  pose — but each file also stores per-skin `BindPose` nodes (`skinCluster1..4Pose`),
  so the rest pose is recoverable from any file without retargeting.
- Base/bow variants: two Maya sources are embedded in the files —
  `.../Erika Archer/erika_archer.ma` (base, 4 meshes) and
  `.../Erika Archer With Bow Arrow/erika_archer_bow_arrow.ma` (bow, 6 meshes + 3
  prop bones). The bow set is internally consistent (identical geometry hashes
  between `draw_arrow.fbx` and `walk_backward_aiming.fbx`) but distinct from base,
  so bow clips must play on the bow skeleton/mesh, not the base one.
- Material/texture references are complete in-file (section 6 of findings below);
  no external texture files are needed.

## 5. Skeleton findings + representative compatibility sample

Skeleton (base): 67 `LimbNode` bones named `mixamorig:<Joint>Model` — Hips, Spine,
Spine1, Spine2, Neck, Head, HeadTop_End, eyes, shoulders/arms/hands with full finger
chains (4 segments per thumb/finger), legs/feet/toes. Root bone: **`mixamorig:HipsModel`**
(the only LimbNode with no parent; mesh models are separate scene roots). Max hierarchy
depth 11 (Hips to fingertip). 67 matching `*NodeAttribute` records.

Bow variant adds exactly 3 bones, all else identical:
`mixamorig:Left_arch1Model` parented under `LeftHandModel`,
`mixamorig:Left_arch2Model` under `Left_arch1Model`,
`mixamorig:arrowModel` under `HipsModel`.

Representative sample inspected end-to-end (hierarchy map, animated channels, key data):
`running.fbx` (locomotion), `crawl.fbx` (crawl), `death.fbx` (fall/death),
`draw_arrow.fbx` + `walk_backward_aiming.fbx` (bow), `diagnol_wall_run.fbx` (wall-run),
plus `jogging`, `fall_flat`, `freehang_climb`, `idle_looking_around` for durations/geometry.

Compatibility conclusion:

- Bone names: identical across all base files (sorted name-sets equal); bow files are
  a strict superset (+3 prop bones).
- Bone count: 67 everywhere in base; 70 in bow files. No missing/renamed shared bones.
- Parent map: identical for all shared bones across the whole sample (verified via
  OO connection maps, not filenames).
- Animation drives a subset, uniformly: `HipsModel` translation + rotation, plus
  rotation on 51 body/limb/finger-segment joints (53 channels over 52 models; leaf
  joints such as `*4` fingertip segments beyond segment 3 and toe-end joints carry no
  curves and ride the bind pose). Bow clips add `Left_arch1Model` rotation only.
  Channels are T/R only — no scale curves anywhere.
- Feasibility: **one canonical 67-bone skeleton plus many clips is strongly supported
  by the evidence** for all base motions; bow motions need the 70-bone superset
  skeleton (or the 3 prop bones grafted under identical parents). No retargeting
  design is needed — only same-hierarchy clip reuse plus a bind-pose fallback for
  unanimated joints. (Retargeting itself was explicitly out of scope and not attempted.)

## 6. Material / texture findings

- Shading: `FbxSurfacePhong` (Phong) materials — 4 in base, 6 in bow files.
- Base texture set (embedded `Video/Content` blobs, 6 slots / 5 unique files):
  `FemaleFitA_Body_diffuse.png`, `FemaleFitA_StdNM.png`,
  `Erika_Archer_Clothes_diffuse.png`, `Erika_Archer_Clothes_normal.png`,
  `FemaleFitA_eyelash_diffuse.png`. No texture files exist alongside the FBXs; the
  `.fbm` paths are per-export GUID temp dirs (`mixamo-mini/tmp/skins_<guid>.fbm/`)
  and resolve to nothing locally — pixels must come from the embedded blobs.
- Bow files add: `Arrow_DIFF.png`, `Arrow_NM.jpg`, `Bow_DIFF.jpg`, `Bow_NM.jpg`
  (Photoshop CS5.1 XMP metadata present in the embedded JPEGs).
- Implication for 2B: textures must be extracted from the FBX embedded content
  (offline step or custom importer), not loaded from disk paths.

## 7. Units / orientation findings (evidence only, no constants chosen)

Raw `GlobalSettings` (identical in all sampled files): `UpAxis=1/Sign=1`,
`FrontAxis=2/Sign=1`, `CoordAxis=0/Sign=1`, `OriginalUpAxis=1`,
`UnitScaleFactor=1.0`, `OriginalUnitScaleFactor=1.0`, `TimeMode=6`, `TimeProtocol=1`.
Standard reading (to be verified in the 2B import spike, not baked in yet):
Y-up, centimeter units (`UnitScaleFactor 1.0` = Mixamo cm convention), 30 fps
(`TimeMode 6` = 30 fps, corroborated: every decoded clip duration is an exact
multiple of 1/30 s). No scale/orientation conversion constant is adopted in this phase.

## 8. MonoGame pipeline findings (verified against local 3.8.4.1 artifacts, nothing installed)

Repo state: only the `MonoGame.Framework.WindowsDX 3.8.4.1` *runtime* package is
referenced; no MGCB wiring, no `.mgcb`, no Content project. (MGCB tooling 3.8.4/3.8.4.1
happens to exist in the local NuGet cache with `monogame.content.builder.task`, but it
is not referenced by the repo.)

- Runtime (`MonoGame.Framework.dll` 3.8.4.1 API docs): `Model`/`ModelBone` hierarchy
  with `CopyAbsoluteBoneTransformsTo/From` exists; `SkinnedEffect` (with bone-transform
  slots) exists for GPU skinning. There is **no** `AnimationClip`/`SkinningData`/
  bone-weight container type in the runtime — stock runtime models carry bones and
  mesh parts but no clip object model.
- Pipeline (`MonoGame.Framework.Content.Pipeline` 3.8.4.1 API docs): `FbxImporter`
  and Assimp-based `OpenAssetImporter` (with `FindSkeleton`/`ImportSkeleton`/
  `FindDeformationBones`/`FindRootBone`) exist, and the import DOM (`NodeContent`)
  carries `AnimationContentDictionary` (`AnimationContent` with `Channels`/`Duration`,
  `AnimationKeyframe` with `Time`/`Transform`). So the importer stage *can* represent
  meshes, materials/textures, skinning-relevant bones, bind pose, and animation
  keyframes. But the stock `ModelProcessor` output (`ModelContent`: only
  `Bones`/`Meshes`/`Root`/`Tag`, with processor options limited to color-key, effects,
  mipmaps, tangents, texture resize, rotation/scale) exposes **no animation or
  skinning export** — stock processing drops the clips.
- Static rendering: stock MGCB (`FbxImporter`/`OpenAssetImporter` + `ModelProcessor`)
  appears sufficient for mesh/material/bone-transform import (to be proven by a spike).
- Animation extraction/playback: stock MonoGame appears **insufficient** — there is no
  clip pipeline output and no runtime clip player. Smallest reasonable future solution
  (no engine switch): a **small custom `ModelProcessor` subclass** (narrow
  content-pipeline extension assembly) that also serializes the imported
  `AnimationContent` (per-bone T/R keyframes) plus skin weights/bind pose into
  engine-owned assets; alternative of equal size is a small offline FBX→custom-format
  conversion step run outside the game build. Either way, Engine stays portable by
  defining its own skinned-mesh/clip types and letting the MonoGame host adapt them
  (as it already does for `MeshData`).

## 9. Risks

- No neutral-pose export: rest pose must come from `BindPose` nodes; static preview
  must use bind pose, not frame 0.
- Two non-interchangeable mesh sets: base clips must not be applied to bow meshes
  expecting prop bones (and vice versa) without explicit handling of the 3 extra bones.
- Textures are embedded with dead absolute/GUID paths; extractor must read blobs.
- ~20–25 MB per FBX (752.8 MB total) — content-build time and artifact size need care;
  convert once to compact engine assets rather than shipping FBXs.
- Stock pipeline drops animation; custom processor/converter is required (small, scoped).
- Scale/orientation constants must be settled by measurement in the 2B spike, not assumed.
- `running` vs `running2`, four `death*` variants, mirrored strafes: content-selection
  (which clips ship) is a game-design decision for later, not this audit.

## 10. Recommended Phase 2B implementation plan

1. Spike: add MGCB with one file (`idle_looking_around.fbx`) through stock
   `ModelProcessor`; verify mesh/material/bone import and measure scale/orientation.
2. Add a minimal content-pipeline extension: custom `ModelProcessor` subclass that
   additionally writes skin weights, bind pose, and the `Take 001` keyframes into a
   compact engine-owned format (Engine types stay portable; MonoGame host adapts).
3. Static milestone: render the canonical Erika bind pose (all 4 base meshes, Phong→
   engine lighting, embedded diffuse textures) in the existing 3D world. No animation yet.
4. Animation milestone (separate): Hips-root-motion + T/R keyframe sampling at 30 fps
   against the 67-bone hierarchy, one clip at a time; bow clips against the 70-bone set.
5. Keep `erika/` ignored and read-only; only converted/compact artifacts enter the repo
   deliberately.

## 11. Validation for this phase

- `git status` before commit: only untracked `docs/ERIKA_ASSET_AUDIT.md`; no FBX
  tracked/modified (whole-file SHA-256 spot-check unchanged; `git ls-files` shows no
  `erika/` entries); no `.csproj`/package changes (`git diff --stat` empty for tracked files).
- Solution build: `dotnet build ErikasLab.sln --configuration Release` — 0 errors,
  0 warnings (verified 2026-09-17).
