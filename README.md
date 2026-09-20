# Erika's Lab

Erika's Lab is the Windows-first technical and gameplay foundation for the future Fimbul Winter game. Phase 1 establishes a small code-first C# architecture and proves that the project can render and move through a real 3D world.

The current platform implementation is MonoGame using its Windows DirectX backend. That choice is intentionally contained in the host project; reusable engine and game code does not depend on Windows, MonoGame input types, `GraphicsDevice`, or platform windowing. The longer-term goal is to keep enough of the core portable for future guideXOS Server work without designing that target prematurely.

## Architecture

```text
ErikasLab.Platform.MonoGame  Windows executable, MonoGame WindowsDX host
        ├── ErikasLab.Game   Phase 1 world/session and game-specific behavior
        └── ErikasLab.Engine Portable timing, input, camera, scene, mesh, renderer contracts
ErikasLab.Engine.Tests       Portable xUnit tests (no GPU, no MonoGame)
```

`ErikasLab.Engine` and `ErikasLab.Game` target `net8.0` and use `System.Numerics` plus project-owned data types. `ErikasLab.Platform.MonoGame` targets `net8.0-windows7.0`, references `MonoGame.Framework.WindowsDX` 3.8.4.1 (plus the same-version `MonoGame.Content.Builder.Task` for content builds), and adapts the portable scene data to MonoGame vertex/index buffers, `BasicEffect`, and loaded `Model` assets.

## Prerequisites

- Windows 10 22H2 or newer
- .NET 8 SDK or a newer SDK with the .NET 8 targeting/runtime packs
- .NET 8 Windows Desktop Runtime
- A DirectX-capable graphics adapter

The repository currently builds with .NET SDK 10.0.400 while targeting .NET 8.

## Restore, build, and run

From the repository root:

```powershell
dotnet restore ErikasLab.sln
dotnet build ErikasLab.sln --configuration Release
dotnet run --project src/ErikasLab.Platform.MonoGame/ErikasLab.Platform.MonoGame.csproj --configuration Release
```

The host is framework-dependent and uses the installed .NET 8 runtime. The project removes two legacy host files transitively supplied by the current WindowsDX package because they otherwise shadow the installed .NET host when launching the generated Windows executable directly.

## Controls

- `W` / `S`: move forward / backward
- `A` / `D`: strafe left / right
- Mouse: look around; the pointer is recentered while the game is focused
- Arrow keys: keyboard look fallback
- `1` / `2` / `3`: play idle / walk / run (Phase 2D validation selector, hard switch)
- `Escape`: exit

Movement is driven by portable `FrameTime.DeltaSeconds`, so it is not frame-rate dependent.

## Phase 1 capabilities

- Three-project solution with a portable engine/game split and a WindowsDX host
- Perspective camera with position, yaw, pitch, field of view, aspect ratio, and near/far clip planes
- Ground plane plus five colored boxes at varied positions, depths, sizes, and elevation
- Depth buffering, back-face culling, basic directional lighting, and GPU vertex/index buffer caching
- Delta-time camera movement, mouse/keyboard look, resizable backbuffer projection updates, and startup diagnostics
- Clean restore/build with warnings treated as errors

Phase 2B adds: stock-pipeline import of canonical Erika as a static textured
model in the test world (portable `ModelAssetId`/`ModelInstance` boundary,
measured scale/orientation/grounding, startup model diagnostics), plus portable
engine tests. It also fixes the ground-plane triangle winding, which had the
Phase 1 ground silently back-face-culled.

Phase 2C adds: single-clip skeletal playback of `Take 001`
(`idle_looking_around`, 4.0 s @ 30 Hz) on the canonical 67-bone skeleton via a
small custom processor + compact `erika_idle.bin` sidecar, portable Engine
evaluator (`Skeleton`/`AnimationClip`/`AnimationEvaluator`, Slerp + linear
interp, hierarchical resolve, `inverseBind * absolute` skinning), and
`SkinnedEffect` rendering. Only single-clip playback exists (no
blending/state machines/root-motion systems).

Phase 2D adds: multi-clip locomotion on the same pipeline — `walk`
(`erika/walking.fbx`, 1.033 s @ 30 Hz) and `run` (`erika/running.fbx`,
0.633 s @ 30 Hz) alongside idle, each as its own `ERIKACLIP1` sidecar sharing
the canonical 67-bone skeleton, selectable with `1`/`2`/`3` (hard switch, loop
reset, no blending). No gameplay movement, state machine, or root-motion
system yet.

Meshes are generated in code for the proof scene, and canonical Erika arrives through the content pipeline described below, so no manual asset authoring is required yet.

## Erika content (Phase 2B)

Canonical static model: `erika/idle_looking_around.fbx` — a base Erika export
(near-neutral standing, 67-bone skeleton, geometry-identical to the other base
files per `docs/ERIKA_ASSET_AUDIT.md`). The local `erika/` directory stays
git-ignored and its FBX files are never modified; Phase 2D references three
sources in `src/ErikasLab.Platform.MonoGame/Content/Content.mgcb`
(`idle_looking_around`, `walking`, `running`).

One-time machine setup (each FBX embeds textures but records dead workstation
paths, which stock MonoGame follows instead of the embedded blobs):

```powershell
python tools/extract_erika_textures.py
```

This extracts the 5 canonical PNGs per source, read-only, to the absolute
locations the importer demands (`<repo-drive>:\home\app\mixamo-mini\tmp\skins_<guid>.fbm\`,
one GUID directory per FBX; bytes are identical across the three).
After that, `dotnet build` compiles the model with the stock `FbxImporter` +
`ModelProcessor` (4 meshes, 4 parts, 72 runtime bones, `BasicEffect` with
diffuse textures, skinning channels preserved) and stages the `.xnb` files into
the app output. No `dotnet-mgcb` tool install is needed: the build drives the
`dotnet-mgcb` DLL from the NuGet cache via the `MGCBCommand` property.
(`OpenAssetImporter` was tried and rejected: its output references
pipeline-only material types the runtime cannot deserialize.)

Conventions (single explicit conversion, centralized in `ErikaFigure` /
`ModelPlacement`; the FBX-to-world correction lives only in the platform
model renderer):

- 1 game world unit = 1 meter. True bind-pose height 180.1 FBX units maps to
  1.70 m (x0.00944); runtime mesh bounding spheres inflate the height (~238
  units), so they are diagnostics-only.
- Import faces +Z (verified from bind-pose eyes-vs-head); yaw correction is 0.
- Feet rest on the ground plane via the measured lower bound (-0.6 units).

If content is missing at startup, the app fails fast naming the expected asset
and the restore steps above instead of surfacing a bare `ContentLoadException`.

## Erika animation (Phase 2C/2D)

Three supported clips on the canonical 67-bone skeleton (all 30 Hz, T/R only,
no scale; Hips is the sole translation track):

- `idle_looking_around` from `erika/idle_looking_around.fbx` (4.000 s,
  120 frames, 42 merged channels, 5082 keys; source stack `mixamo.com`).
- `walk` from `erika/walking.fbx` (1.033 s, 31 frames, 40 merged channels,
  1280 keys; source stack `mixamo.com`). `running.fbx` vs `running2.fbx` carry
  byte-identical animation (same SHA over all keys), so `running.fbx` is used.
- `run` from `erika/running.fbx` (0.633 s, 19 frames, 52 merged channels,
  1040 keys; source stack `mixamo.com`).

- Build: `ErikaModelProcessor` (in `src/ErikasLab.Content.Pipeline`, referencing
  `AssimpNetter` from the MGCB distribution) emits the stock `Model` plus one
  deterministic sidecar per source (`erika_idle.bin` ~107 kB, `erika_walk.bin`
  ~30 kB, `erika_run.bin` ~25 kB; `ErikaClipCodec` v1 unchanged: magic
  `ERIKACLIP1`, skeleton + clip, little-endian) staged to `Content/erika/`.
  Per-asset `/processorParam:ClipName=` / `/processorParam:SidecarFilename=`
  select the stable id (`idle_looking_around`/`walk`/`run`). Runtime never
  opens the FBX. The walking/running `Model` outputs are build exhaust only;
  rendering always uses the canonical idle `Model`.
- Why Assimp-direct: stock MonoGame `AnimationContent` strips FBX joint
  orientation (pre-rotation pivots) from keys (UpLeg bind 180 deg becomes a
  25 deg key), producing exploded/collapsed poses; raw Assimp
  `NodeAnimationChannel` keys preserve correct full parent-relative locals
  (UpLeg ~168 deg ~= bind 180 deg) and are used instead. Skeleton still comes
  from the import DOM (`BoneContent`, OffsetMatrix-derived).
- Canonical skeleton: 67 joints (`mixamorig:Hips` root; raw FBX `LimbNode`
  names carry a `Model` suffix stripped by the importer), parent indices,
  bind T/Q. Runtime `Model` has 72 bones (67 + RootNode + 4 mesh nodes);
  all 67 map by name (fail loudly otherwise).
- Bind/inverse-bind: local `R*T` (no scale), absolute via parents
  (order-independent resolve, cycle-checked), inverse via `Matrix4x4.Invert`.
  Cross-validated against the runtime `Model` (max inverse-bind deviation
  0.0002 units).
- Interpolation: translation linear (`Vector3.Lerp`), rotation
  `Quaternion.Slerp` + normalize; exact `t=0`, exact-duration loops to 0,
  negatives wrap; 30 Hz cadence verified.
- Skinning: `skin = inverseBind * absolute` (System.Numerics, row-major;
  direct element copy to MonoGame matrices, no transpose). At bind this is
  identity (mesh untouched; verified by the identity-palette bind screenshot).
  **Palette is skeleton-ordered (67)**: imported `BlendIndices` are
  mesh-relative (0..66, e.g. Eyes to slots 7/8 = eyes, Body torso to slot 0 =
  Hips), which stock `ModelProcessor` does not remap to `Model` order for
  this pivot-rich source — a `Model`-ordered palette (72) stretches limbs.
  The 5 `Model` extras are never indexed.
- Root policy: Hips translation played verbatim for all clips; no generalized
  root-motion system. Idle drift < 4 cm per axis
  (X [-3.67,-0.29] Y [94.50,97.70] Z [0.66,2.95]); Erika stays at her world
  spot. Walk/run are traveling clips, not in-place: walk Hips
  X [-0.29,3.98] Y [94.68,102.65] Z [1.49,185.98] (184.5 units ≈ 1.74 m per
  1.033 s loop); run Hips X [-0.68,1.33] Y [92.43,100.56] Z [1.70,376.49]
  (374.8 units ≈ 3.54 m per 0.633 s loop). Looping snap-back keeps her near
  the camera; the stored data is never zeroed.
- Playback: idle auto-starts; `1`/`2`/`3` hard-switch idle/walk/run with a
  deterministic loop reset (`GameSession.ActiveClipName`/`ClipStartSeconds`,
  elapsed = absolute clock − start). Every clip loops exactly, advances on
  the absolute game clock (no drift), is frame-rate independent; no per-frame
  FBX parsing/loading, no GPU recreation, caller-provided arrays only
  (no per-frame allocations), no per-frame console output (one line per
  switch only).
- Renderer: `AnimatedModelRenderer` replaces `SkinnedEffect` onto the 4
  imported parts (weights per vertex from the declaration), preserving all 4
  diffuse textures, depth, lighting, and world rendering; static path retained
  for non-Erika models. Engine/Game stay free of MonoGame/Windows types.
- Limitations: three validation clips with hard switches only; no blending,
  locomotion state machine, root-motion gameplay (walk/run visibly snap back
  each loop), bow/weapons/combat, IK, layers, retargeting, physics, or
  movement. Skeleton compatibility is strict: walk/run sidecar skeletons must
  match the canonical 67 joints (names, parents, bind pose) or content load
  fails loudly.

## Deferred work

Combat, inventory, AI, quests, complicated physics, networking, guideXOS support, animation blending/state machines/locomotion, production content, save/load, and a larger renderer/content system are intentionally deferred. Canonical Erika now plays idle/walk/run via the ignored local `erika/` source directory (see above); the remaining 34 FBX files await future phases. The smallest logical next step is a deliberate locomotion policy: WASD-driven clip selection plus a root-motion decision (consume Hips travel into world motion vs. in-place masking) informed by the measured 1.74 m walk and 3.54 m run per-loop travel.

## Repository hygiene

Build output (`bin/`, `obj/`), IDE state, temporary files, and the local `erika/` asset directory are ignored. The first Phase 1 commit is intentionally source-only.
