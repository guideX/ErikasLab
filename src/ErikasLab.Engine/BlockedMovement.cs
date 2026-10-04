using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// Phase 2P centralized blocked-movement policy constants. One place for the
/// diagnostic probe distance, the progress-ratio enter/release thresholds, and
/// the enter/release confirmation delays, so no blocked-response tuning values
/// are scattered through the code.
///
/// The blocked response is a *feedback* into the locomotion target-speed
/// decision only: when sustained movement intent produces almost no usable
/// translation, the target speed is suppressed to zero and the existing
/// Phase 2I deceleration envelope slows Erika to idle. It never generates
/// translation, never modifies accepted displacement, and never adds a second
/// movement authority. Root motion remains the only source of requested
/// locomotion; collision remains the only environment constraint.
/// </summary>
public static class BlockedMovementPolicy
{
    /// <summary>
    /// Diagnostic probe distance (meters). A short forward cast from Erika's
    /// current position along her smoothed facing, run through the existing
    /// player-collision resolver without applying the result. 0.10 m detects
    /// immediate obstruction (a head-on wall push accepts almost none of it)
    /// without reacting to walls far ahead, and is far shorter than every
    /// intended interior passage (doorway 0.60 m, hearth bypass 0.85 m, narrow
    /// diagonal weave ~0.33 m), so valid routes never false-block.
    /// </summary>
    public const float BlockedMovementProbeDistanceMeters = 0.10f;

    /// <summary>
    /// Progress ratio at or below which movement counts as blocked. The ratio
    /// is accepted probe travel / probe distance, so 0.15 means only 15% of the
    /// intended probe travel survives — motion within ~8.6 degrees of head-on.
    /// Meaningful wall slides (shallow/moderate diagonal) keep far more than
    /// this and stay locomotion.
    /// </summary>
    public const float BlockedEnterProgressRatio = 0.15f;

    /// <summary>
    /// Progress ratio at or above which a blocked route counts as viable again.
    /// Strictly above <see cref="BlockedEnterProgressRatio"/> (hysteresis), so a
    /// direction hovering near the boundary cannot flicker the latch. 0.30
    /// means at least 30% of the probe travel survives before the block releases.
    /// </summary>
    public const float BlockedReleaseProgressRatio = 0.30f;

    /// <summary>
    /// Elapsed low-progress time (seconds) required before the blocked latch
    /// engages. A few frames at any rate: long enough to ignore one-frame
    /// collision noise and doorway/corner brushes, short enough to feel
    /// responsive. Frame-rate independent (accumulates elapsed time).
    /// </summary>
    public const float BlockedEnterDelaySeconds = 0.05f;

    /// <summary>
    /// Elapsed high-progress time (seconds) required before a blocked latch
    /// releases. Smaller than the enter delay so recovery stays responsive while
    /// still confirming the new direction is clearly viable.
    /// </summary>
    public const float BlockedReleaseDelaySeconds = 0.02f;
}

/// <summary>
/// Phase 2P bounded blocked-movement state. Tracks whether sustained movement
/// intent is producing usable translation, using a short diagnostic collision
/// probe through the existing <see cref="PlayerCollisionResolver"/>. The probe
/// is diagnostic only: it never moves Erika and never feeds the resolver that
/// produces accepted displacement.
///
/// State is bounded to: the blocked/unblocked latch, the current low-progress
/// candidate duration, the release candidate duration, the time since the
/// latch engaged, and the latest progress ratio. No history is accumulated and
/// no generalized locomotion-state graph is built.
/// </summary>
public sealed class BlockedMovementTracker
{
    private readonly PlayerCollisionSet _collisions;
    private readonly float _radius;
    private bool _blocked;
    private float _candidateSeconds;
    private float _releaseCandidateSeconds;
    private float _blockedSeconds;
    private float _progressRatio = 1f;

    public BlockedMovementTracker(PlayerCollisionSet collisions, float radius)
    {
        _collisions = collisions;
        _radius = radius;
    }

    /// <summary>True while the sustained-blocked latch is active.</summary>
    public bool IsMovementBlocked => _blocked;

    /// <summary>Elapsed seconds since the blocked latch engaged (0 when not blocked).</summary>
    public float BlockedMovementSeconds => _blockedSeconds;

    /// <summary>Latest probe progress ratio in [0, 1] (1 = completely free).</summary>
    public float BlockedMovementProgressRatio => _progressRatio;

    /// <summary>Current low-progress candidate duration (seconds) toward the enter delay.</summary>
    public float BlockedMovementCandidateSeconds => _candidateSeconds;

    /// <summary>Reset all state to the unblocked, free defaults.</summary>
    public void Clear()
    {
        _blocked = false;
        _candidateSeconds = 0f;
        _releaseCandidateSeconds = 0f;
        _blockedSeconds = 0f;
        _progressRatio = 1f;
    }

    /// <summary>
    /// Advance the blocked-movement state one frame.
    ///
    /// <paramref name="probeEnabled"/> is true only while directional movement
    /// intent exists, translation is not gated by a stationary turn-in-place,
    /// and player collision is active. When it is false the state is cleared:
    /// losing movement intent means ordinary Phase 2I deceleration (never a
    /// blocked response), and a turn-in-place must not read its own lack of
    /// translation as obstruction.
    ///
    /// When enabled, a short probe is cast from <paramref name="position"/>
    /// along <paramref name="probeDirection"/> (need not be normalized) through
    /// the existing resolver. The accepted/probe length ratio drives separate
    /// enter/release hysteresis with elapsed-time confirmation.
    /// </summary>
    public void Update(Vector2 position, Vector2 probeDirection, bool probeEnabled, double deltaSeconds)
    {
        if (!probeEnabled)
        {
            Clear();
            return;
        }

        var dt = (float)deltaSeconds;
        if (!float.IsFinite(dt) || dt <= 0f)
        {
            return;
        }

        if (!IsFinite(position) || !IsFinite(probeDirection))
        {
            Clear();
            return;
        }

        var probeLength = probeDirection.Length();
        if (probeLength < 1e-6f)
        {
            Clear();
            return;
        }

        var distance = BlockedMovementPolicy.BlockedMovementProbeDistanceMeters;
        var probeDelta = probeDirection / probeLength * distance;
        var resolved = PlayerCollisionResolver.Resolve(_collisions, position, probeDelta, _radius);

        var ratio = distance > 1e-6f ? resolved.Delta.Length() / distance : 1f;
        if (!float.IsFinite(ratio) || ratio < 0f)
        {
            ratio = 0f;
        }
        if (ratio > 1f)
        {
            ratio = 1f;
        }

        _progressRatio = ratio;

        if (ratio <= BlockedMovementPolicy.BlockedEnterProgressRatio)
        {
            _releaseCandidateSeconds = 0f;
            _candidateSeconds += dt;
            if (_candidateSeconds >= BlockedMovementPolicy.BlockedEnterDelaySeconds)
            {
                _blocked = true;
            }
        }
        else if (ratio >= BlockedMovementPolicy.BlockedReleaseProgressRatio)
        {
            _candidateSeconds = 0f;
            _releaseCandidateSeconds += dt;
            if (_releaseCandidateSeconds >= BlockedMovementPolicy.BlockedReleaseDelaySeconds)
            {
                _blocked = false;
            }
        }

        if (_blocked)
        {
            _blockedSeconds += dt;
        }
        else
        {
            _blockedSeconds = 0f;
        }
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}
