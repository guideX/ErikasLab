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

Phase 2L adds: the first actual Fimbul Winter environment — a Viking-style
longhouse standing in a dark forest clearing, built as a **blockout** (simple
geometry, placeholder vertex-color materials, no final art). A portable
`LonghouseLayout` (Engine) centralizes every dimension, derived measurement,
deterministic post/tree placement rule, and the spawn; `EnvironmentFactory`
(Game) turns it into static `SceneObject` geometry that reuses the existing
scene/mesh path (`Scene` + `MeshFactory` + one shared unit primitive per palette
entry). The longhouse is 16 m long x 6 m wide, walls 2.6 m to a 5.0 m ridge,
with a 1.2 m x 2.0 m **open** doorway in the +Z gable end (two flanking wall
segments plus a lintel, no fake door texture), two steep overhanging roof slabs
with a ridge beam and filled triangular gable ends, corner/repeated side posts,
tie beams, rafters, a central stone hearth, long benches, and two tables. It
sits on a 36 m clearing over a 140 m dark forest floor, ringed by 24
deterministically jittered blockout conifers (trunk + three canopy tiers). Erika
spawns outside the front entrance at `(0, 0, 12)` facing -Z (straight at the
door, consistent with the Phase 2H derived spawn facing), so both the exterior
and the interior are immediately visible. There is intentionally **no player
collision and no camera collision** yet, and the ground stays flat; the camera
may clip through walls/roof inside, which is expected and motivates a later
camera-collision phase. All Phase 2E–2K locomotion, camera, crossfade,
turn-in-place, and spawn-facing behavior is unchanged. New `MeshFactory`
helpers are limited to `CreateGroundRectangle` and `CreateTriangularPrism`, and
new `ColorRgba` entries are the blockout palette; no new gameplay or animation
behavior was required.

Phase 2M adds: a small static third-person camera-obstruction system on top of the Phase 2H follow camera, so the camera no longer renders through the longhouse walls/roof or tree trunks. A portable `CameraObstructionSet` (Engine) holds 32 oriented boxes (`CameraObstructionBox`: center, half extents, orientation) built once by `EnvironmentFactory` from the same authoritative `LonghouseLayout` as the rendered geometry: the two long walls, rear wall, the two front doorway flanking segments, the doorway lintel, the two rotated pitched roof slabs, and the 24 tree trunks. `ThirdPersonCamera.Follow` spherecasts the target-to-candidate segment against the radius-expanded boxes (Minkowski-expanded slab test in each box's local space) and, on the nearest hit, clamps the camera immediately to just in front of the obstruction (hit distance minus a centralized 0.05 m surface padding); when the line of sight is clear the existing exponential follow smoothing moves the camera back outward naturally. The camera is treated as a 0.25 m sphere (centralized `CameraCollisionPolicy.CollisionRadiusMeters`), and a single flat camera floor at 0.15 m keeps it above the ground plane at high positive pitch. The orbit/control basis (`ControlForward`/`ControlRight`) is derived from orbit yaw only and is never touched by collision, so WASD behavior is bit-identical with obstruction enabled or disabled (covered by A/B gameplay tests at 30/60/144 Hz). The genuine doorway stays open: the flanking segments + lintel mirror the rendered wall arrangement, so camera lines through the door center remain unobstructed while off-center casts are blocked. Boxes containing the look target are ignored for the query (robust starting-inside policy until player collision exists). Gables (triangular prisms), posts (subsumed by the wall boxes), and canopies are intentionally not colliders. There is still no player collision and no physics engine; Erika may walk through walls, and the camera stays finite in that case. All Phase 2E–2L locomotion, animation, camera, and environment tests remain passing (415 total).

Phase 2N adds: a small static player-collision system on top of the Phase 2E-2K root-motion locomotion, so Erika can no longer walk or run through the longhouse shell or the tree trunks. A portable `PlayerCollisionSet` (Engine) holds 29 flat-XZ oriented boxes (`PlayerCollisionBox`: center, half extents, yaw) built once by `EnvironmentFactory` from the same authoritative `LonghouseLayout` as the rendered geometry: the two long walls, the rear wall, the two front doorway flanking segments, and the 24 tree trunks. This is deliberately a different set from the Phase 2M camera obstructions — the pitched roof, doorway lintel, gables, rafters, and interior masses are excluded because the player is a horizontal circle at foot level and there is no vertical collision. `GameSession` constrains only the displacement the authored root-motion stack already requested: the requested world XZ delta is passed to the portable `PlayerCollisionResolver`, which returns the accepted delta, and Erika's Y and yaw are untouched, so collision is a constraint and never a second movement authority. The resolver first depenetrates an already-overlapping center (bounded iterations and a 1.0 m correction cap, deterministic tie-breaking), then runs a bounded sweep-and-slide: each static box is Minkowski-expanded by the 0.30 m player radius and the player-center segment is tested against it in the box's local space (slab method), the earliest hit wins, the center moves to the safe contact (minus a 0.01 m skin), and the inward normal component is removed while the tangential remainder continues. A head-on run stops at the wall with no invented sideways motion, a shallow hit slides along the wall, and a corner resolves in a bounded 4 iterations with no bounce, restitution, or friction. The genuine 1.2 m doorway stays open: the 0.30 m radius leaves a 0.60 m usable center corridor, and the flanking segments alone define the jambs (no invisible collider spans the opening). High run speed cannot tunnel because the sweep is continuous, and the resolver is frame-rate independent (30/60/144 Hz all stop at the same position). Blocked movement does not touch the Phase 2I speed envelope or the Phase 2K playback clock — movement intent and animation continue while physical translation is constrained, so pushing against a wall produces foot sliding, which is an acknowledged Phase 2N limitation. Turn-in-place (Phase 2J) still gates translation to exactly zero, and Phase 2M camera obstruction is completely independent (separate set, separate position). Unobstructed locomotion is bit-identical with player collision enabled or disabled (A/B tested). Furnishings/hearth are intentionally still non-solid.

Phase 2O adds: interior-furnishing collision on top of the Phase 2N system, making the Phase 2L hearth, both long benches, and both tables solid without changing the collision architecture. `EnvironmentFactory.CreatePlayerCollisionSet` now appends five flat-XZ `PlayerCollisionBox` blockers derived from the same `LonghouseLayout` values the rendered geometry uses (the hearth as one box over its full 1.4 m x 4.0 m stone footprint centered at z = -1.5, each bench over its 0.5 m x 14 m seat footprint at x = +/-2.4, and each table over its 0.8 m x 3.5 m tabletop projection at x = +/-1.65, z = 3.0), for 34 player blockers total (5 shell + 5 furnishings + 24 trunks). Table center X/Z and bench center X are now shared layout values rather than renderer-only literals, so collision and rendering cannot drift. The hearth is a single solid box (Erika walks around the fire bed, never over it); tables use the tabletop projection with no individual legs, so walking underneath is not allowed. The 0.30 m radius and 0.01 m skin are unchanged, and no new collider type, broadphase, or resolver behavior was added. The interior stays navigable: the genuine doorway, the central aisle between the tables, and both lateral hearth bypasses remain clear (effective widths 0.60 m, 1.90 m, and 0.85 m after player-radius expansion), with the narrowest intended route being the ~0.33 m diagonal weave between the hearth's front corner and the table's rear-inner corner. The 0.10 m bench/table gap and the 0.55 m table/hearth vertical gap are intentionally impassable. Camera obstruction is untouched (still 32 boxes; furnishings are not camera colliders). Clear-space locomotion is bit-identical with collision enabled or disabled, and high-speed sweeps cannot tunnel through any furnishing.

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

## Camera obstruction (Phase 2M)

The follow camera now avoids static environment geometry. The renderer never
raycasts; the collision math is portable Engine code with no MonoGame
dependency.

- Representation: 32 static oriented boxes (`CameraObstructionBox`) built once
  by `EnvironmentFactory` from `LonghouseLayout` — 6 wall boxes (long walls,
  rear, front flanking segments, doorway lintel), 2 rotated roof slabs, 24 tree
  trunks. Each box mirrors the matching scene object's name, center, half
  extents, and orientation.
- Query: per frame, `Follow` spherecasts the target-to-candidate segment
  against every box expanded by the camera radius (0.25 m) using a slab test in
  the box's local space; the nearest hit wins regardless of iteration order.
  O(boxes), allocation-free, no mesh/triangle tests.
- Response: on a hit the camera clamps immediately to
  `hit distance - 0.05 m padding` along the target-to-candidate line (fast
  pull-in, no wall penetration); when unobstructed the existing exponential
  smoothing (rate 10/s) moves the camera back outward smoothly. A single flat
  camera floor at 0.15 m prevents below-ground views at high positive pitch.
- Doorway: the flanking segments + lintel keep the real 1.2 m x 2.0 m doorway
  open; camera lines through the door center are unobstructed (the camera sphere
  must stay below y = 1.75 to fit), off-center casts are blocked.
- Starting-inside policy: a box containing the look target is ignored for the
  query, so Erika walking through a wall (no player collision yet) cannot
  collapse the camera or emit NaNs. Player penetration is not solved.
- Control basis: collision changes only the camera *position*;
  `ControlForward`/`ControlRight` still derive from orbit yaw alone, so WASD is
  identical with obstruction on or off (A/B tested, including 30/60/144 Hz).
- Diagnostics: `IsCameraObstructed`, `NominalDesiredDistance`,
  `ActualTargetDistance`, `NearestObstructionHitFraction`, `ObstructionCount`;
  the startup `Camera policy:` line reports the active box count/radius/padding.
- Limitations: no player collision, no physics, no wall sliding/corner
  resolution (hard clamping can pop bounded by the nominal distance when the
  lagging camera's line of sight suddenly crosses a wall), no character fading
  or first-person fallback, gables/posts/canopies are not colliders.

## Player collision (Phase 2N)

Erika is now blocked by the longhouse shell and tree trunks. The collision math
is portable Engine code with no MonoGame dependency; the renderer has no
collision responsibility and the camera rig never consumes it.

- Shape: a horizontal circle of radius 0.30 m centered on Erika's XZ position
  (centralized `PlayerCollisionPolicy.PlayerCollisionRadiusMeters`). No capsule,
  no bone/mesh collider, no vertical physics; Erika's Y is never changed.
- Representation: 29 static flat-XZ oriented boxes (`PlayerCollisionBox`) built
  once by `EnvironmentFactory` from `LonghouseLayout` — left/right long walls,
  rear wall, the two front doorway flanking segments, and 24 tree trunks. The
  roof, lintel, gables, rafters, and interior masses are intentionally excluded
  (irrelevant to a foot-level circle). A different set from the 32 Phase 2M
  camera obstructions.
- Query: `PlayerCollisionSet.Sweep` Minkowski-expands each box by the player
  radius and runs a slab test against the player-center segment in the box's
  local space; the nearest hit wins regardless of iteration order, and a
  high-speed segment that crosses a thin wall is still caught.
- Response: `PlayerCollisionResolver` depenetrates a starting overlap (bounded
  iterations, 1.0 m correction cap, deterministic tie-breaking), then sweeps and
  slides: move to the safe contact minus a 0.01 m skin
  (`PlayerCollisionPolicy.PlayerCollisionSkinMeters`), remove the inward normal
  component, continue with the tangential remainder. At most 4 slide
  iterations. No bounce, restitution, or friction.
- Doorway: the 1.2 m opening is genuine. The 0.30 m radius leaves a 0.60 m
  usable center corridor; the flanking segments are the only blockers at the
  door plane, so no invisible collider spans the opening and straight,
  off-center, and diagonal entries all work.
- Root motion: collision constrains only the requested displacement from the
  authored root-motion stack (`GameSession.ApplyPlayerCollision`). It never
  generates velocity, never rotates Erika, and never touches the Phase 2I speed
  envelope or the Phase 2K playback clock. Unobstructed locomotion is identical
  with collision enabled or disabled (`PlayerCollisionEnabled`, A/B tested at
  30/60/144 Hz).
- Diagnostics: `RequestedPlayerDisplacement`, `AcceptedPlayerDisplacement`,
  `WasPlayerCollisionConstrained`, `PlayerCollisionHitCount`,
  `LastPlayerCollisionName`; the startup `Player collision policy:` line
  reports the blocker count/radius/skin/iterations.
- Limitations: flat XZ only (no gravity, jumping, slopes, or step-up);
  furnishings, benches, tables, and the hearth were not solid in 2N (they are
  made solid in Phase 2O below); pushing into a wall while input continues
  produces foot sliding (movement intent and animation continue while
  translation is constrained); no blocked-movement animation response.

## Player collision — interior furnishings (Phase 2O)

The Phase 2N collision set is extended to the longhouse interior. The collision
architecture (circle, skin, Minkowski sweep, bounded depenetration, sweep-and-
slide resolver, 4-iteration cap) is reused unchanged; only the blocker set grew.

- Representation: 34 static flat-XZ oriented boxes (`PlayerCollisionBox`) —
  the Phase 2N 29 (5 shell walls + 24 tree trunks) plus the central hearth, both
  long benches, and both tables. Built once by `EnvironmentFactory` from the
  same authoritative `LonghouseLayout` dimensions as the rendered geometry; the
  table center X/Z and bench center X are shared layout values, not
  renderer-only literals.
- Hearth: one solid box over the full 1.4 m x 4.0 m stone footprint centered at
  `(0, -1.5)`. Erika walks around the fire bed, never across it. No fire/damage/
  heat/interaction gameplay.
- Benches: the full 0.5 m (deep) x 14 m (long) seat footprint at `x = +/-2.4`.
  Tables: the 0.8 m x 3.5 m tabletop projection at `x = +/-1.65`, `z = 3.0`
  (no individual legs; walking underneath is not allowed). All yaw 0.
- Navigability (after 0.30 m radius expansion): doorway 0.60 m, central table
  aisle 1.90 m, each hearth bypass 0.85 m. The narrowest intended route is the
  ~0.33 m diagonal between the hearth front corner and the table rear-inner
  corner. The 0.10 m bench/table gap and the 0.55 m table/hearth vertical gap
  are intentionally impassable; collision was not shrunk to open every visual
  gap, and the 0.30 m radius was not changed.
- Behavior: head-on stops at contact with no sideways motion; diagonal contact
  slides tangentially; corners stay bounded and deterministic; a 40 m/s sweep
  cannot tunnel; a starting overlap is depenetrated (bounded, finite).
- Independence: camera obstruction is untouched (still 32 boxes; the low
  furnishings are not camera colliders). Root motion, speed envelope, pose
  playback, turn-in-place, and clear-space locomotion are unchanged; blocked
  movement still produces foot sliding.
- Limitations: flat XZ only (no gravity, jumping, slopes, step-up, or vertical
  collision); furnishings are static obstacles with no interaction behavior;
  no blocked-movement animation response.

## Blocked-movement locomotion response (Phase 2P)

Erika no longer walks or runs indefinitely when a solid obstacle prevents
meaningful translation. A short diagnostic probe detects sustained lack of
usable progress and suppresses the locomotion target speed so the existing
Phase 2I deceleration envelope slows her to idle through the normal crossfade
path. No second animation or movement system is added.

- Probe: a 0.10 m forward cast from Erika's current position along her smoothed
  facing, run through the existing `PlayerCollisionResolver` without applying
  the result. It is diagnostic only — it never moves Erika and never feeds the
  accepted displacement. The probe runs only while directional movement intent
  exists and a stationary turn-in-place is not gating translation.
- Progress metric: `progressRatio = length(acceptedProbeDisplacement) /
  probeDistance`, clamped to `[0, 1]`. 1.0 is completely free; near 0.0 is
  fully blocked. Always finite; zero/non-finite probes are safe.
- Hysteresis: enter blocked at or below 0.15 (only 15% of probe travel
  survives — motion within ~8.6 degrees of head-on); release at or above 0.30.
  The gap prevents flicker when hovering near the boundary.
- Time confirmation: 0.05 s of sustained low progress before the latch
  engages (ignores one-frame collision noise and corner brushes); 0.02 s of
  high progress before release. Frame-rate independent (elapsed time).
- Speed-envelope integration: while the latch is active, the locomotion target
  speed is suppressed to zero. The existing 24 m/s^2 deceleration slows Erika
  to the 0.01 m/s stop threshold, then the existing crossfade path enters
  idle. The held input remains intent; the probe prevents reacceleration while
  the obstruction remains. No second deceleration curve.
- Playback: as the speed envelope decelerates, the Phase 2K visual playback
  rate slows naturally with it. No special blocked animation.
- Recovery: when the probe reports viable movement (e.g. after redirecting along
  a wall or rotating the camera), the latch releases and the normal acceleration
  envelope resumes from the current speed. No speed jump, no root replay.
- Wall sliding: meaningful tangential movement (shallow/moderate diagonal)
  keeps a high progress ratio and stays locomotion. Only near-head-on pushes
  latch blocked.
- Clear-space no-op: when the probe reports free movement, the blocked response
  is a no-op — position, yaw, clip, speed, root gain, visual rate, pose clocks,
  and transitions are identical with the response enabled or disabled
  (`BlockedMovementResponseEnabled` A/B seam, like `PlayerCollisionEnabled`).
- Diagnostics: `IsMovementBlocked`, `BlockedMovementSeconds`,
  `BlockedMovementProgressRatio`, `BlockedMovementCandidateSeconds`,
  `BlockedMovementProbeDistanceMeters`; the startup `Blocked-movement policy:`
  line reports the probe distance, thresholds, and delays.
- Limitations: the probe is a short forward cast, so a very small obstacle
  approached at a shallow angle can still be slid around (correct behavior —
  Erika navigates around small trees); the blocked response targets sustained
  obstruction, not transient corner contact.

## Deferred work

Combat, inventory, AI, quests, complicated physics, networking, guideXOS support, animation state machines/blend trees, production content, save/load, and a larger renderer/content system are intentionally deferred. Canonical Erika now walks/runs via consumed root motion from the ignored local `erika/` source directory (see above); the remaining 34 FBX files await future phases. Phase 2F covered short crossfades and turn-rate smoothing; Phase 2G added the third-person follow/orbit camera; Phase 2H made the camera continuously look at Erika and aligned her spawn facing with initial forward movement; Phase 2I added acceleration/deceleration movement response (speed envelope scaling the single authored root-motion authority); Phase 2J added stationary turn-in-place (a gated 45°-enter/15°-release procedural pivot that holds idle and zero translational target until the heading error is resolved, then releases through the existing speed envelope); Phase 2K synchronized the visible locomotion playback rate with the Phase 2I speed envelope through a separate pose clock, bounding the run→walk raw rate at 2×. Phase 2L added the first environment blockout (Viking longhouse in a forest clearing) as static vertex-colored geometry owned by a centralized portable layout. Phase 2M added the static camera-obstruction pass (32 oriented boxes, spherecast pull-in, smooth outward recovery) on the real environment geometry. Phase 2N added static flat-XZ player collision and wall sliding against the longhouse shell and tree trunks (29 blockers, bounded sweep-and-slide), constraining the authored root-motion displacement without a second movement authority. Phase 2O extended that set to the longhouse interior (solid hearth, benches, and tables; 34 blockers total) using the same architecture, and verified the interior stays navigable. With the environment collision set now covering the shell, trees, and furnishings, the most obvious remaining locomotion/collision defect is foot sliding while blocked, so the smallest logical next step is a blocked-movement locomotion response (with camera corner-snapping/character fading as the next environment-polish candidates after that), camera corner-snapping/character fading, stride warping/foot IK to remove the remaining capped run→walk slide, and authored turn-in-place clips. Final textures/models are intentionally deferred.

## Repository hygiene

Build output (`bin/`, `obj/`), IDE state, temporary files, and the local `erika/` asset directory are ignored. The first Phase 1 commit is intentionally source-only.
