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
- Mouse: orbit the third-person camera around Erika; the pointer is recentered while the game is focused
- Arrow keys: keyboard orbit fallback
- `1` / `2` / `3`: diagnostic idle / walk / run selector (hard switch, latches; movement takes precedence)
- `Escape`: exit

No movement input plays idle (stationary). `Shift` alone stays idle. WASD drives
Erika; the third-person camera orbits on look input and never translates with WASD.
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

Phase 2G adds: a portable third-person follow/orbit camera (`ThirdPersonCamera`)
that observes Erika's authoritative world position without owning it. Mouse or
arrow look orbits yaw/pitch around her; position trails via elapsed-time
exponential smoothing (`alpha = 1 - exp(-rate * dt)`, frame-rate independent,
bounded, no overshoot), and spawn/session-reset or a >25 m target jump snaps
instead of flying. WASD stays camera-relative (basis = horizontal orbit
forward/right), Erika keeps her own smoothed facing, and camera pitch never adds
vertical travel. Camera collision is intentionally deferred.

Phase 2H adds: camera-targeting and spawn-facing consistency. The rendered view
is now a true look-at (`camera.Forward = normalize(target - camera.Position)`),
so Erika stays centred while only the camera *position* trails; the
orbit/control basis (`ControlForward`/`ControlRight`, yaw-only) is separate and
is what WASD uses, so follow lag can never bend movement intent. Erika spawns
facing the exact direction initial `W` requests (derived from the camera
convention, not a hard-coded turn), so the game no longer opens in front of a
character who immediately turns 180°. Degenerate camera-at-target cases fall
back to the orbit basis (finite, roll-free). Root motion, crossfades, loop
seams, and orbit/facing independence are unchanged.

Phase 2I adds: acceleration/deceleration movement response on top of the Phase
2E/2F root-motion locomotion, without adding a second motion authority. A
portable `MovementSpeedEnvelope` (Engine) owns one finite, non-negative
meters-per-second scalar that ramps toward a target with elapsed-time
`MoveTowards(current, target, rate * dt)` — centralized acceleration
(16 m/s²) and deceleration (24 m/s²) rates, clamped to the target (no
overshoot, no NaN, stable under large `dt`). The target is derived from movement
intent (no movement → 0, walk/run → authored clip speed), where the authored
speed comes from the clip's net Hips horizontal displacement, duration, and
`ErikaFigure.Scale` (`RootMotionEvaluator.ComputeHorizontalSpeedMetersPerSecond`;
walk ≈ 1.685 m/s, run ≈ 5.586 m/s with the shipped sidecars) — never a literal
speed constant. The active clip stays the sole root-motion authority: the
evaluated authored world delta is scaled by
`gain = currentSpeed / activeClipAuthoredSpeed`, so starts ramp in from 0, stops
ramp out to 0, and walk↔run keep world speed continuous; path and direction still
come entirely from the animation and the smoothed facing. Releasing movement keeps
the current walk/run clip as authority and coasts through its authored root motion
until the speed reaches a 0.01 m/s threshold, at which point speed is clamped to
exactly zero and the normal 0.20 s crossfade to idle begins; camera orbit after
release never steers Erika (no new movement intent means the facing is retained).
Run-to-walk may temporarily scale the walk clip's root delta above 1 (observed peak
gain ≈ 3.08 at 60 Hz) during the brief deceleration, which is bounded and converges
to 1. Animation playback rate is intentionally not speed-scaled yet, so transient
foot sliding during acceleration/deceleration is an acknowledged Phase 2I
limitation. Camera targeting, crossfades, yaw smoothing, spawn facing, and loop
accumulation are unchanged; a latent float-rounding seam bug in
`RootMotionEvaluator` (a remainder just below the loop duration folding back to 0
without advancing the loop, which could emit a near-full-loop reverse snap) was
fixed and covered by a regression test.

Phase 2J adds: stationary turn-in-place on top of the Phase 2F yaw smoother and Phase 2I speed envelope, without a second motion authority. When Erika is effectively stationary (current Phase 2I speed at or below a centralized 0.1 m/s threshold) and the requested camera-relative heading differs from her current yaw by at least a centralized 45° enter angle, she enters a turn-in-place state: the authoritative yaw rotates through the existing 4π rad/s shortest-path smoother while idle stays the visual clip and the translational target stays zero, so a large heading change reads as a deliberate stationary pivot rather than a sliding idle. Once the wrapped heading error falls to a centralized 15° release angle (hysteresis against the 45° enter angle, so boundary input cannot flicker the state), the gate lifts and the normal Phase 2I acceleration envelope begins toward the authored walk/run speed. Small corrections (< 45°) keep the ordinary Phase 2F behavior (smooth turn while travel begins); residual/coasting motion above the stationary threshold never enters a turn (the existing coast-facing policy is preserved), and only once the speed reaches the threshold may a new stationary turn begin. Losing directional intent cancels the turn immediately and retains the facing; a mid-turn direction change retargets the newest heading via shortest-path math with no queued turns. Shift during a gated turn only selects the post-turn run target — it never creates run displacement during the turn. Camera orbit alone never rotates Erika; orbiting updates the control basis so a new direction requests the fresh heading. No authored turn clips exist in the asset library (only idle/walk/run are imported), so the procedural fallback is used: idle remains the visual clip while the authoritative yaw rotates, and proper authored turn clips can replace the visual later without changing the gameplay state contract. Root-motion authority is unchanged: the active locomotion clip remains the sole translational authority, the turn state itself applies no world translation, and the Phase 2I speed envelope, loop seams, crossfades, camera targeting, and spawn facing are untouched. The known foot-slide limitation (animation playback rate is intentionally not speed-scaled) remains unresolved.

Phase 2K adds: locomotion visual playback-rate synchronization on top of the
Phase 2I speed envelope, without adding a second motion authority. A portable
`AnimationPlaybackClock` (Engine) owns the visual *pose* phase separately from
the authoritative root-motion clock: it advances by
`poseElapsed += ((previousRate + currentRate) * 0.5) * dt` (trapezoidal,
matching the linear `MoveTowards` speed ramp), wraps into `[0, duration)` with
exact-boundary semantics (exact multiple maps to 0, just-below-duration is
preserved, just-above-duration wraps to the remainder), and is allocation-free.
`GameSession` owns the active-clip pose clock plus an independent outgoing-clip
clock during a crossfade, and derives each clip's visual rate as
`currentSpeed / authoredSpeed` through the centralized `LocomotionPlaybackRates`
helper, clamped to `[0, 2]`. Steady walk/run render at exactly 1×; idle is always
1× and never tied to translational speed; starts ramp up from 0; coasting ramps
down toward 0; walk→run begins the run clip below 1× (≈0.35 at the shipped
speeds) and converges to 1× as Erika accelerates; run→walk's raw rate (up to
≈3.08, the run/walk authored ratio ≈3.31) is capped at 2× for ≈0.08 s, leaving
a short residual stride mismatch rather than a ≈3× walk cycle. The renderer now
samples explicit pose times passed from the session (`PoseElapsedSeconds` /
`SourcePoseElapsedSeconds` / `DestinationPoseElapsedSeconds`) instead of
deriving them from the absolute clock, so it never integrates gameplay animation
timing; crossfade blend alpha remains transition-elapsed / 0.20 s and is
unchanged, and each side of a blend advances its own clock and rate. The
root-motion clock (`ClipStartSeconds` + absolute time), the single root-motion
authority, the Phase 2I gain, loop/seam handling, yaw smoothing, camera, spawn
facing, and Phase 2J turn-in-place (idle visual, zero translation) are all
unchanged: world position, yaw, and speed are bit-identical with synchronization
on or off (verified at 30/60/144 Hz). Residual foot sliding remains only in the
capped run→walk window; stride warping and foot IK are still deferred.

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
  no authored turn-in-place/lean animations (Phase 2J turns stationarily via the
  procedural idle + yaw-rotation fallback at a constant rate); visual locomotion
  playback is now speed-synchronized (Phase 2K) but run→walk caps the walk
  cadence at 2×, so a short residual foot slide remains there, and no stride
  warping/foot IK exists; strafing/backwards
  clips (Erika faces the heading and uses the forward clip), foot IK, layers,
  retargeting, physics, collision/gravity/jumping, or movement. WASD drives Erika
  camera-relatively (default camera: W=-Z, S=+Z, A=-X, D=+X); the camera itself
  keeps look only. Skeleton compatibility is strict: walk/run sidecar skeletons must
  match the canonical 67 joints (names, parents, bind pose) or content load
  fails loudly.

## Third-person camera (Phase 2G, refined in 2H)

Erika is followed by a compact portable rig (`ThirdPersonCamera`, Engine) that
owns only orbit angles, follow distance, look-at height, and the smoothed camera
position; `GameSession` keeps Erika's position/facing and `CameraState` is the
final view the renderer consumes. The renderer never computes follow behavior.

- Two bases (Phase 2H). The **orbit/control basis**
  (`ControlForward`/`ControlRight`) is horizontal, derived from orbit yaw only,
  and is what WASD means. The **rendered view basis** (`camera.Forward`) is the
  physical look direction, recomputed each frame as
  `normalize(target - camera.Position)`. Position may lag; orientation tracks
  the target, so Erika stays centred during walk/run/turn/orbit.
- Model: `target = ErikaPosition + (0, 1.25, 0)`; `desired = target -
  orbitForward * 4.5`; `camera.Position` smooths toward `desired`; then the view
  is pointed at `target` from that smoothed position.
- Degenerate handling: if the camera position and target coincide (or the vector
  is non-finite), the view falls back to the orbit forward basis instead of
  normalizing a zero vector, so all camera vectors stay finite and roll-free.
- Initial framing: distance 4.50 m, look-at height 1.25 m, orbit yaw 0.00 rad
  (along the Phase 1 forward, -Z, behind the initial movement heading), pitch
  -0.28 rad (~-16 deg, slightly above and looking down). Spawn snaps to this
  frame (no fly-in).
- Spawn facing (Phase 2H): Erika's initial yaw is derived from the initial
  control forward (`yaw = atan2(direction.X, direction.Z)`), so she starts
  facing exactly the direction initial `W` requests and the game does not open
  on a 180° correction.
- Orbit: mouse (0.0025 rad/px) and arrow keys (1.7 rad/s) drive yaw/pitch; yaw
  wraps to (-pi, pi] with no boundary jump and pitch clamps to [-1.20, +0.50]
  rad (~-68.8 deg to +28.6 deg) so the camera cannot flip through the poles.
  All elapsed-time based.
- Follow smoothing: exponential toward the desired position at rate 10/s
  (time constant ~0.10 s): frame-rate independent, deterministic, bounded,
  overshoot-free, allocation-free. Snaps on first use/reset or when the target
  discontinuity exceeds 25 m, so initialization never flies across the world.
- Camera-relative movement: WASD derives from the orbit/control basis (not the
  lagged rendered view); pitch is dropped (Y=0) so looking up/down never adds
  vertical player movement; diagonals normalize; opposite keys cancel.
- Facing independence: orbiting while stationary leaves Erika's yaw untouched;
  after orbiting, WASD uses the new control direction and Erika turns toward it
  through the existing Phase 2F yaw smoother (no MMO mouse-lock, no auto-recenter).
- Deferred: no camera collision/occlusion/raycasts; the camera may pass through
  walls, terrain, and props until real environment geometry exists.

## Deferred work

Combat, inventory, AI, quests, complicated physics, networking, guideXOS support, animation state machines/blend trees, production content, save/load, and a larger renderer/content system are intentionally deferred. Canonical Erika now walks/runs via consumed root motion from the ignored local `erika/` source directory (see above); the remaining 34 FBX files await future phases. Phase 2F covered short crossfades and turn-rate smoothing; Phase 2G added the third-person follow/orbit camera; Phase 2H made the camera continuously look at Erika and aligned her spawn facing with initial forward movement; Phase 2I added acceleration/deceleration movement response (speed envelope scaling the single authored root-motion authority); Phase 2J added stationary turn-in-place (a gated 45°-enter/15°-release procedural pivot that holds idle and zero translational target until the heading error is resolved, then releases through the existing speed envelope); Phase 2K synchronized the visible locomotion playback rate with the Phase 2I speed envelope through a separate pose clock, bounding the run→walk raw rate at 2×. The smallest logical next step is stride warping or foot IK to remove the remaining capped run→walk slide, authored turn-in-place clips to replace the procedural visual fallback, or a camera collision/obstruction pass once real environment geometry exists.

## Repository hygiene

Build output (`bin/`, `obj/`), IDE state, temporary files, and the local `erika/` asset directory are ignored. The first Phase 1 commit is intentionally source-only.
