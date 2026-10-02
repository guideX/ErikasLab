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

- `W` / `S`: move forward / backward (camera-relative; Erika faces the heading and walks forward)
- `A` / `D`: move left / right (camera-relative, normalized diagonals)
- `Shift`: sprint modifier (movement + Shift plays run, movement alone plays walk)
- Mouse: look around; the pointer is recentered while the game is focused
- Arrow keys: keyboard look fallback
- `1` / `2` / `3`: diagnostic idle / walk / run selector (hard switch, latches; movement takes precedence)
- `Escape`: exit

No movement input plays idle (stationary). `Shift` alone stays idle. WASD now drives
Erika; the camera keeps look only and no longer translates with WASD.
Movement is driven by portable `FrameTime.DeltaSeconds` plus authored root-motion
displacement, so it is not frame-rate dependent.

## Phase 1 capabilities

- Three-project solution with a portable engine/game split and a WindowsDX host
- Perspective camera with position, yaw, pitch, field of view, aspect ratio, and near/far clip planes
- Ground plane plus five colored boxes at varied positions, depths, sizes, and elevation
- Depth buffering, back-face culling, basic directional lighting, and GPU vertex/index buffer caching
- Delta-time animation/root-motion advancement, mouse/keyboard look, resizable backbuffer projection updates, and startup diagnostics
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

Phase 2E adds: root-motion player locomotion on the Phase 2D clips — WASD
movement intent (camera-relative, normalized diagonals) plus Shift sprint selects
idle/walk/run, Erika faces the heading (snapped) and travels via the authored
Hips displacement consumed into her world transform (`RootMotionEvaluator`,
loop-wrap-aware, world-scaled by `ErikaFigure.Scale`), with rendered Hips X/Z
pinned to clip start (Y/rotation/hierarchy preserved) and idle stationary.
`1`/`2`/`3` remain as a non-interfering diagnostic latch. No blending, IK,
physics, combat, jumping, or new assets.

Phase 2F adds: short skeletal crossfades and smooth turning on top of the Phase
2E locomotion, without changing root-motion authority. A single bounded
`AnimationTransition` (source/destination clip + loop origins + clock) drives a
`PoseBlender` that interpolates local TRS bone-by-bone (linear translation,
normalized shortest-path quaternion rotation), so `AnimatedModelRenderer`
evaluates both poses and blends them; alpha 0 reproduces the source pose and
alpha 1 the destination exactly. Crossfades are 0.20 s, elapsed-time based
(frame-rate independent). Yaw now turns toward movement intent at a centralized
4π rad/s (~720°/s) along the shortest wrapped path with no overshoot, and world
root displacement follows the smoothed facing (curved travel while turning).
Interruptions replace the single transition (releasing mid-blend reverses it by
swapping ends and remapping progress to `1 - alpha`), so no pop, no teleport, no
double root motion, and no unbounded state. Idle stays root-suppressed; `1`/`2`/`3`
diagnostics are unchanged.

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
- Root policy: consumed root motion (Phase 2E). Walk/run Hips horizontal (X/Z)
  travel drives Erika's world transform via the portable `RootMotionEvaluator`:
  absolute(t) = loops(t) * net + (sample(norm(t)) - start), delta = absolute(curr)
  - absolute(prev), so N loops produce exactly N * net with no wrap discontinuity
  (never naive currNorm - prevNorm). The delta (native FBX units) is scaled by
  the centralized `ErikaFigure.Scale` (1 unit = 1 m via x0.00944) and rotated by
  Erika's heading yaw, so displacement follows her facing. Rendered Hips X/Z is
  pinned to the clip start reference (Y, rotation, and limb hierarchy preserved);
  idle stays verbatim and stationary (its ~2 mm net drift never reaches the world).
  Measured with the shipped sidecars: walk 184.50 units = 1.741 m per 1.0333 s
  loop (~1.685 m/s); run 374.79 units = 3.538 m per 0.6333 s loop (~5.586 m/s).
  Idle net is ~0. No loop snap-back; transitions (idle/walk/run, direction changes)
  apply no stale delta and do not teleport. During a Phase 2F crossfade only the
  destination clip is authoritative; the outgoing pose is visual-only, and each
  participating locomotion pose has its consumed Hips X/Z pinned to its own clip
  start before blending, so neither end adds a second root delta.
- Playback: idle auto-starts; movement selects walk/run (Shift sprints) and
  `1`/`2`/`3` remain as a diagnostic latch (movement takes precedence), each
  switch resetting deterministically to the loop start (`GameSession.ActiveClipName`/
  `ClipStartSeconds`, elapsed = absolute clock − start, root bookkeeping reset so
  no stale delta leaks); an interruption reversal instead resumes the returning
  clip's phase for continuity. Phase 2F starts a 0.20 s `AnimationTransition` on
  every switch; holding a state never restarts the loop and a finished transition
  retires so single-clip rendering resumes. Heading turns smoothly at 4π rad/s
  (shortest wrapped path, clamped at the target) and root travel follows the
  smoothed facing. Every clip loops exactly, advances on the absolute game clock
  (no drift), is frame-rate independent; no per-frame FBX parsing/loading, no GPU
  recreation, caller-provided arrays only (no per-frame allocations), no per-frame
  console output (one line per switch only).
- Renderer: `AnimatedModelRenderer` replaces `SkinnedEffect` onto the 4
  imported parts (weights per vertex from the declaration), preserving all 4
  diffuse textures, depth, lighting, and world rendering; static path retained
  for non-Erika models. Engine/Game stay free of MonoGame/Windows types.
- Limitations: three clips with short crossfades (no blend trees/state machine);
  no acceleration/deceleration, turn-in-place/lean animations (headings turn at a
  constant rate), strafing/backwards
  clips (Erika faces the heading and uses the forward clip), foot IK, layers,
  retargeting, physics, collision/gravity/jumping, or movement. WASD drives Erika
  camera-relatively (default camera: W=-Z, S=+Z, A=-X, D=+X); the camera itself
  keeps look only. Skeleton compatibility is strict: walk/run sidecar skeletons must
  match the canonical 67 joints (names, parents, bind pose) or content load
  fails loudly.

## Deferred work

Combat, inventory, AI, quests, complicated physics, networking, guideXOS support, animation state machines/blend trees, production content, save/load, and a larger renderer/content system are intentionally deferred. Canonical Erika now walks/runs via consumed root motion from the ignored local `erika/` source directory (see above); the remaining 34 FBX files await future phases. Phase 2F covered short crossfades and turn-rate smoothing; the smallest logical next step is a small movement-feel pass (acceleration/deceleration and turn-in-place) that still leaves root-motion authority unchanged, or dedicated strafe/backward clips if the animation set is expanded.

## Repository hygiene

Build output (`bin/`, `obj/`), IDE state, temporary files, and the local `erika/` asset directory are ignored. The first Phase 1 commit is intentionally source-only.
