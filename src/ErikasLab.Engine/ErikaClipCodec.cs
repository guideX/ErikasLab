using System.Numerics;
using System.Text;

namespace ErikasLab.Engine;

/// <summary>
/// Deterministic binary format for one skeleton plus one animation clip.
/// Both the content-pipeline writer and the runtime reader delegate to this
/// codec, so the format is unit-testable without MonoGame. Layout (v1):
/// magic "ERIKACLIP1", bone count, per bone (name, parent, bind T/Q), clip
/// (name, duration, fps), channel count, per channel (bone, key count,
/// has-T, has-R, times, translations, rotations). All floats little-endian.
/// </summary>
public static class ErikaClipCodec
{
    public const string Magic = "ERIKACLIP1";

    private const int MaxNameLength = 256;
    private const int MaxBoneCount = 1024;
    private const int MaxChannelCount = 4096;
    private const int MaxKeyCount = 1 << 20;

    public static void Write(Stream destination, Skeleton skeleton, AnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        Write(writer, skeleton, clip);
    }

    /// <summary>
    /// Core writer used by both streams and content-pipeline writers
    /// (<c>ContentWriter</c> derives from <c>BinaryWriter</c>).
    /// </summary>
    public static void Write(BinaryWriter writer, Skeleton skeleton, AnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(clip);

        writer.Write(Encoding.ASCII.GetBytes(Magic));
        writer.Write(skeleton.BoneCount);
        foreach (var bone in skeleton.Bones)
        {
            WriteName(writer, bone.Name);
            writer.Write(bone.ParentIndex);
            WriteVector3(writer, bone.BindTranslation);
            WriteQuaternion(writer, bone.BindRotation);
        }

        WriteName(writer, clip.Name);
        writer.Write(clip.DurationSeconds);
        writer.Write(clip.FramesPerSecond);
        writer.Write(clip.Channels.Count);
        foreach (var channel in clip.Channels)
        {
            writer.Write(channel.BoneIndex);
            writer.Write(channel.KeyCount);
            writer.Write(channel.HasTranslation);
            writer.Write(channel.HasRotation);
            foreach (var time in channel.Times)
            {
                writer.Write(time);
            }

            if (channel.Translations is not null)
            {
                foreach (var value in channel.Translations)
                {
                    WriteVector3(writer, value);
                }
            }

            if (channel.Rotations is not null)
            {
                foreach (var value in channel.Rotations)
                {
                    WriteQuaternion(writer, value);
                }
            }
        }
    }

    public static (Skeleton Skeleton, AnimationClip Clip) Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        return Read(reader);
    }

    /// <summary>
    /// Core reader used by both streams and content-pipeline readers
    /// (<c>ContentReader</c> derives from <c>BinaryReader</c>).
    /// </summary>
    public static (Skeleton Skeleton, AnimationClip Clip) Read(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var magic = reader.ReadBytes(Magic.Length);
        if (Encoding.ASCII.GetString(magic) != Magic)
        {
            throw new InvalidDataException("Not an Erika clip artifact (bad magic).");
        }

        var boneCount = reader.ReadInt32();
        if (boneCount <= 0 || boneCount > MaxBoneCount)
        {
            throw new InvalidDataException($"Implausible bone count {boneCount}.");
        }

        var bones = new SkeletonBone[boneCount];
        for (var i = 0; i < boneCount; i++)
        {
            bones[i] = new SkeletonBone(
                ReadName(reader),
                reader.ReadInt32(),
                ReadVector3(reader),
                ReadQuaternion(reader));
        }

        var skeleton = new Skeleton(bones);

        var clipName = ReadName(reader);
        var duration = reader.ReadSingle();
        var fps = reader.ReadSingle();
        var channelCount = reader.ReadInt32();
        if (channelCount <= 0 || channelCount > MaxChannelCount)
        {
            throw new InvalidDataException($"Implausible channel count {channelCount}.");
        }

        var channels = new AnimationChannel[channelCount];
        for (var i = 0; i < channelCount; i++)
        {
            var boneIndex = reader.ReadInt32();
            var keyCount = reader.ReadInt32();
            var hasTranslation = reader.ReadBoolean();
            var hasRotation = reader.ReadBoolean();
            if (boneIndex < 0 || boneIndex >= boneCount)
            {
                throw new InvalidDataException($"Channel {i} targets bone {boneIndex} outside the skeleton.");
            }

            if (keyCount <= 0 || keyCount > MaxKeyCount)
            {
                throw new InvalidDataException($"Channel {i} has implausible key count {keyCount}.");
            }

            if (!hasTranslation && !hasRotation)
            {
                throw new InvalidDataException($"Channel {i} has neither track.");
            }

            var times = new float[keyCount];
            for (var k = 0; k < keyCount; k++)
            {
                times[k] = reader.ReadSingle();
            }

            Vector3[]? translations = null;
            if (hasTranslation)
            {
                translations = new Vector3[keyCount];
                for (var k = 0; k < keyCount; k++)
                {
                    translations[k] = ReadVector3(reader);
                }
            }

            Quaternion[]? rotations = null;
            if (hasRotation)
            {
                rotations = new Quaternion[keyCount];
                for (var k = 0; k < keyCount; k++)
                {
                    rotations[k] = ReadQuaternion(reader);
                }
            }

            channels[i] = new AnimationChannel(boneIndex, times, translations, rotations);
        }

        var clip = new AnimationClip(clipName, duration, fps, channels);
        return (skeleton, clip);
    }

    private static void WriteName(BinaryWriter writer, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length == 0 || bytes.Length > MaxNameLength)
        {
            throw new InvalidDataException($"Bone/clip name '{name}' has invalid length.");
        }

        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadName(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length <= 0 || length > MaxNameLength)
        {
            throw new InvalidDataException($"Invalid name length {length}.");
        }

        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector3(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteQuaternion(BinaryWriter writer, Quaternion value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }

    private static Quaternion ReadQuaternion(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
