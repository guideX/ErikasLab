namespace ErikasLab.Engine;

/// <summary>
/// One skeleton plus one animation clip: the unit the content pipeline
/// produces and the runtime player consumes. Portable.
/// </summary>
public sealed class SkeletalAnimation
{
    public SkeletalAnimation(Skeleton skeleton, AnimationClip clip)
    {
        Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        Clip = clip ?? throw new ArgumentNullException(nameof(clip));
    }

    public Skeleton Skeleton { get; }

    public AnimationClip Clip { get; }
}
