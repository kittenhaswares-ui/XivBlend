using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Meddle.Utils.Files;

/// <summary>
/// A deliberately small, read-only TMB timeline reader. XivBlend only decodes
/// the records needed to keep skeletal, facial and visible emote events on the
/// same 30 Hz clock; unknown records remain safely ignored.
/// </summary>
/// <remarks>
/// The record layout is independently adapted from the MIT-licensed Dalamud
/// VFXEditor TMB reader. See NOTICE-XIVBLEND.md for the pinned source revision.
/// </remarks>
public sealed class TmbTimelineFile
{
    public const uint TmlbMagic = 0x424C4D54; // TMLB
    private const int MaximumTimelineBytes = 64 * 1024 * 1024;
    private const int MaximumItems = 65_536;
    private const int MaximumTrackMembershipIds = 1_000_000;
    private const int MaximumStringBytes = 4_096;

    public int ByteLength { get; }
    public int TimelineLengthFrames { get; }
    public string? FaceLibrary { get; }
    public IReadOnlyList<TmbAnimationEvent> Animations { get; }
    public IReadOnlyList<TmbVfxEvent> VisualEffects { get; }
    public IReadOnlyList<TmbPropEvent> Props { get; }
    public IReadOnlyList<TmbVisibilityEvent> Visibility { get; }

    public TmbTimelineFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            throw new InvalidDataException("TMB timeline is truncated.");
        }

        if (ReadUInt32(data, 0) != TmlbMagic)
        {
            throw new InvalidDataException("TMB timeline does not begin with TMLB.");
        }

        var declaredSize = ReadInt32(data, 4);
        if (declaredSize is < 12 or > MaximumTimelineBytes || declaredSize > data.Length)
        {
            throw new InvalidDataException($"TMB timeline declares invalid size {declaredSize}.");
        }

        var itemCount = ReadInt32(data, 8);
        if (itemCount is < 0 or > MaximumItems)
        {
            throw new InvalidDataException($"TMB timeline declares invalid item count {itemCount}.");
        }

        ByteLength = declaredSize;
        var timeline = data[..declaredSize];
        var animationEvents = new List<MutableAnimationEvent>();
        var visualEffects = new List<MutableVfxEvent>();
        var props = new List<MutablePropEvent>();
        var visibility = new List<MutableVisibilityEvent>();
        var trackMembership = new List<(short TrackId, int TrackOrder, short[] EntryIds)>();
        string? faceLibrary = null;
        var timelineLengthFrames = 0;
        var totalTrackMembershipIds = 0;

        var position = 12;
        for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
        {
            EnsureRange(timeline, position, 8, "TMB item header");
            var magic = ReadMagic(timeline, position);
            var size = ReadInt32(timeline, position + 4);
            if (size < 8 || position > declaredSize - size)
            {
                throw new InvalidDataException(
                    $"TMB item {itemIndex} ({magic}) declares invalid size {size}.");
            }

            var item = new TmbItemHeader(magic, position, size, itemIndex);
            switch (magic)
            {
                case "TMDH":
                    RequireItemSize(item, 0x10);
                    timelineLengthFrames = Math.Max(0, (int)ReadInt16(timeline, position + 12));
                    break;
                case "TMPP":
                    RequireItemSize(item, 0x0C);
                    faceLibrary = ReadOffsetString(timeline, item, 8);
                    break;
                case "TMTR":
                    RequireItemSize(item, 0x18);
                    var entryIds = ReadOffsetIdList(timeline, item, 12, 16);
                    totalTrackMembershipIds = checked(totalTrackMembershipIds + entryIds.Length);
                    if (totalTrackMembershipIds > MaximumTrackMembershipIds)
                    {
                        throw new InvalidDataException(
                            "TMB timeline contains an unreasonable number of track memberships.");
                    }

                    trackMembership.Add((
                        ReadInt16(timeline, position + 8),
                        itemIndex,
                        entryIds));
                    break;
                case "C009":
                    RequireItemSize(item, 0x18);
                    animationEvents.Add(new MutableAnimationEvent(
                        magic,
                        ReadInt16(timeline, position + 8),
                        ReadInt16(timeline, position + 10),
                        ReadInt32(timeline, position + 12),
                        0,
                        0.0f,
                        0.0f,
                        ReadOffsetString(timeline, item, 20) ?? string.Empty,
                        itemIndex));
                    break;
                case "C010":
                    RequireItemSize(item, 0x28);
                    animationEvents.Add(new MutableAnimationEvent(
                        magic,
                        ReadInt16(timeline, position + 8),
                        ReadInt16(timeline, position + 10),
                        ReadInt32(timeline, position + 12),
                        ReadInt32(timeline, position + 20),
                        ReadSingle(timeline, position + 24),
                        ReadSingle(timeline, position + 28),
                        ReadOffsetString(timeline, item, 32) ?? string.Empty,
                        itemIndex));
                    break;
                case "C012":
                    RequireItemSize(item, 0x48);
                    visualEffects.Add(new MutableVfxEvent(
                        magic,
                        ReadInt16(timeline, position + 8),
                        ReadInt16(timeline, position + 10),
                        ReadInt32(timeline, position + 12),
                        ReadOffsetString(timeline, item, 20) ?? string.Empty,
                        ReadInt16(timeline, position + 24),
                        ReadInt16(timeline, position + 26),
                        ReadInt16(timeline, position + 28),
                        ReadInt16(timeline, position + 30),
                        ReadOffsetVector3(timeline, item, 32),
                        ReadOffsetVector3(timeline, item, 40),
                        ReadOffsetVector3(timeline, item, 48),
                        ReadOffsetVector4(timeline, item, 56),
                        ReadInt32(timeline, position + 64),
                        itemIndex));
                    break;
                case "C173":
                    RequireItemSize(item, 0x44);
                    visualEffects.Add(new MutableVfxEvent(
                        magic,
                        ReadInt16(timeline, position + 8),
                        ReadInt16(timeline, position + 10),
                        0,
                        ReadOffsetString(timeline, item, 20) ?? string.Empty,
                        ReadInt16(timeline, position + 24),
                        ReadInt16(timeline, position + 26),
                        0,
                        0,
                        Vector3.One,
                        Vector3.Zero,
                        Vector3.Zero,
                        Vector4.One,
                        ReadInt32(timeline, position + 28),
                        itemIndex));
                    break;
                case "C198":
                    RequireItemSize(item, 0x28);
                    props.Add(new MutablePropEvent(
                        magic,
                        ReadInt16(timeline, position + 8),
                        ReadInt16(timeline, position + 10),
                        ReadInt32(timeline, position + 12),
                        ReadUInt16(timeline, position + 32),
                        ReadUInt16(timeline, position + 34),
                        ReadInt32(timeline, position + 36),
                        itemIndex));
                    break;
                case "C094":
                    RequireItemSize(item, 0x20);
                    var extraOffset = ReadInt32(timeline, position + 28);
                    var enableFilter = false;
                    var filter = 0;
                    if (extraOffset != 0)
                    {
                        var extra = ResolveItemOffset(timeline, item, extraOffset, 8);
                        enableFilter = ReadInt32(timeline, extra) == 1;
                        filter = ReadInt32(timeline, extra + 4);
                    }

                    visibility.Add(new MutableVisibilityEvent(
                        magic,
                        ReadInt16(timeline, position + 8),
                        ReadInt16(timeline, position + 10),
                        ReadInt32(timeline, position + 12),
                        ReadSingle(timeline, position + 20),
                        ReadSingle(timeline, position + 24),
                        enableFilter,
                        filter,
                        itemIndex));
                    break;
            }

            position += size;
        }

        AssignTracks(animationEvents, trackMembership);
        AssignTracks(visualEffects, trackMembership);
        AssignTracks(props, trackMembership);
        AssignTracks(visibility, trackMembership);

        TimelineLengthFrames = timelineLengthFrames;
        FaceLibrary = NormalizeGameKey(faceLibrary);
        Animations = animationEvents
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => new TmbAnimationEvent(
                item.Magic,
                item.Id,
                item.Time,
                item.Duration,
                item.Flags,
                item.AnimationStart,
                item.AnimationEnd,
                NormalizeGameKey(item.Path) ?? string.Empty,
                item.TrackId,
                item.TrackOrder,
                item.ItemOrder))
            .ToArray();
        VisualEffects = visualEffects
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => new TmbVfxEvent(
                item.Magic,
                item.Id,
                item.Time,
                item.Duration,
                NormalizeGameKey(item.Path) ?? string.Empty,
                item.BindPoint1,
                item.BindPoint2,
                item.BindPoint3,
                item.BindPoint4,
                item.Scale,
                item.Rotation,
                item.Position,
                item.Color,
                item.Visibility,
                item.TrackId,
                item.TrackOrder,
                item.ItemOrder))
            .ToArray();
        Props = props
            .Select(item => new TmbPropEvent(
                item.Magic,
                item.Id,
                item.Time,
                item.Duration,
                item.ModelId,
                item.BodyId,
                item.Variant,
                item.TrackId,
                item.TrackOrder,
                item.ItemOrder))
            .ToArray();
        Visibility = visibility
            .Select(item => new TmbVisibilityEvent(
                item.Magic,
                item.Id,
                item.Time,
                item.FadeFrames,
                item.StartVisibility,
                item.EndVisibility,
                item.EnableFilter,
                item.Filter,
                item.TrackId,
                item.TrackOrder,
                item.ItemOrder))
            .ToArray();
    }

    private static void AssignTracks<T>(
        IReadOnlyList<T> events,
        IReadOnlyList<(short TrackId, int TrackOrder, short[] EntryIds)> memberships)
        where T : MutableTimelineEvent
    {
        var membership = new Dictionary<short, (short TrackId, int TrackOrder)>();
        foreach (var (trackId, trackOrder, entryIds) in memberships)
        {
            foreach (var entryId in entryIds)
            {
                membership.TryAdd(entryId, (trackId, trackOrder));
            }
        }

        foreach (var item in events)
        {
            if (membership.TryGetValue(item.Id, out var track))
            {
                item.TrackId = track.TrackId;
                item.TrackOrder = track.TrackOrder;
            }
        }
    }

    private static short[] ReadOffsetIdList(
        ReadOnlySpan<byte> timeline,
        TmbItemHeader item,
        int offsetField,
        int countField)
    {
        var offset = ReadInt32(timeline, item.Offset + offsetField);
        var count = ReadInt32(timeline, item.Offset + countField);
        if (offset == 0 || count == 0)
        {
            return [];
        }

        if (count is < 0 or > MaximumItems)
        {
            throw new InvalidDataException($"{item.Magic} contains invalid timeline ID count {count}.");
        }

        var absolute = ResolveItemOffset(timeline, item, offset, checked(count * sizeof(short)));
        var result = new short[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = ReadInt16(timeline, absolute + index * sizeof(short));
        }

        return result;
    }

    private static string? ReadOffsetString(ReadOnlySpan<byte> timeline, TmbItemHeader item, int fieldOffset)
    {
        var relative = ReadInt32(timeline, item.Offset + fieldOffset);
        if (relative == 0)
        {
            return null;
        }

        var absolute = ResolveItemOffset(timeline, item, relative, 1);
        var remaining = Math.Min(timeline.Length - absolute, MaximumStringBytes);
        var terminator = timeline.Slice(absolute, remaining).IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException($"{item.Magic} contains an unterminated offset string.");
        }

        return Encoding.UTF8.GetString(timeline.Slice(absolute, terminator));
    }

    private static Vector3 ReadOffsetVector3(ReadOnlySpan<byte> timeline, TmbItemHeader item, int fieldOffset)
    {
        var offset = ReadInt32(timeline, item.Offset + fieldOffset);
        var count = ReadInt32(timeline, item.Offset + fieldOffset + 4);
        if (offset == 0 || count != 3)
        {
            return fieldOffset == 32 ? Vector3.One : Vector3.Zero;
        }

        var absolute = ResolveItemOffset(timeline, item, offset, 3 * sizeof(float));
        return new Vector3(
            ReadSingle(timeline, absolute),
            ReadSingle(timeline, absolute + 4),
            ReadSingle(timeline, absolute + 8));
    }

    private static Vector4 ReadOffsetVector4(ReadOnlySpan<byte> timeline, TmbItemHeader item, int fieldOffset)
    {
        var offset = ReadInt32(timeline, item.Offset + fieldOffset);
        var count = ReadInt32(timeline, item.Offset + fieldOffset + 4);
        if (offset == 0 || count != 4)
        {
            return Vector4.One;
        }

        var absolute = ResolveItemOffset(timeline, item, offset, 4 * sizeof(float));
        return new Vector4(
            ReadSingle(timeline, absolute),
            ReadSingle(timeline, absolute + 4),
            ReadSingle(timeline, absolute + 8),
            ReadSingle(timeline, absolute + 12));
    }

    private static int ResolveItemOffset(
        ReadOnlySpan<byte> timeline,
        TmbItemHeader item,
        int relative,
        int byteCount)
    {
        if (relative < 0)
        {
            throw new InvalidDataException($"{item.Magic} contains a negative relative offset.");
        }

        var absolute = checked(item.Offset + 8 + relative);
        EnsureRange(timeline, absolute, byteCount, $"{item.Magic} offset payload");
        return absolute;
    }

    private static string? NormalizeGameKey(string? value)
    {
        var normalized = value?.Trim().Replace('\\', '/');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static void RequireItemSize(TmbItemHeader item, int expected)
    {
        if (item.Size != expected)
        {
            throw new InvalidDataException(
                $"TMB item {item.Magic} has size 0x{item.Size:X}, expected 0x{expected:X}.");
        }
    }

    private static string ReadMagic(ReadOnlySpan<byte> data, int offset)
    {
        EnsureRange(data, offset, 4, "TMB magic");
        return Encoding.ASCII.GetString(data.Slice(offset, 4));
    }

    private static short ReadInt16(ReadOnlySpan<byte> data, int offset)
    {
        EnsureRange(data, offset, sizeof(short), "TMB Int16");
        return BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
    {
        EnsureRange(data, offset, sizeof(ushort), "TMB UInt16");
        return BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset)
    {
        EnsureRange(data, offset, sizeof(int), "TMB Int32");
        return BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        EnsureRange(data, offset, sizeof(uint), "TMB UInt32");
        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    private static float ReadSingle(ReadOnlySpan<byte> data, int offset)
    {
        var value = BitConverter.Int32BitsToSingle(ReadInt32(data, offset));
        if (!float.IsFinite(value))
        {
            throw new InvalidDataException("TMB contains a non-finite floating-point value.");
        }

        return value;
    }

    private static void EnsureRange(ReadOnlySpan<byte> data, int offset, int count, string label)
    {
        if (offset < 0 || count < 0 || offset > data.Length - count)
        {
            throw new InvalidDataException($"{label} leaves the declared timeline bounds.");
        }
    }

    private readonly record struct TmbItemHeader(string Magic, int Offset, int Size, int Order);

    private abstract class MutableTimelineEvent(short id, int itemOrder)
    {
        public short Id { get; } = id;
        public int ItemOrder { get; } = itemOrder;
        public short? TrackId { get; set; }
        public int? TrackOrder { get; set; }
    }

    private sealed class MutableAnimationEvent(
        string magic,
        short id,
        short time,
        int duration,
        int flags,
        float animationStart,
        float animationEnd,
        string path,
        int itemOrder) : MutableTimelineEvent(id, itemOrder)
    {
        public string Magic { get; } = magic;
        public short Time { get; } = time;
        public int Duration { get; } = duration;
        public int Flags { get; } = flags;
        public float AnimationStart { get; } = animationStart;
        public float AnimationEnd { get; } = animationEnd;
        public string Path { get; } = path;
    }

    private sealed class MutableVfxEvent(
        string magic,
        short id,
        short time,
        int duration,
        string path,
        short bindPoint1,
        short bindPoint2,
        short bindPoint3,
        short bindPoint4,
        Vector3 scale,
        Vector3 rotation,
        Vector3 position,
        Vector4 color,
        int visibility,
        int itemOrder) : MutableTimelineEvent(id, itemOrder)
    {
        public string Magic { get; } = magic;
        public short Time { get; } = time;
        public int Duration { get; } = duration;
        public string Path { get; } = path;
        public short BindPoint1 { get; } = bindPoint1;
        public short BindPoint2 { get; } = bindPoint2;
        public short BindPoint3 { get; } = bindPoint3;
        public short BindPoint4 { get; } = bindPoint4;
        public Vector3 Scale { get; } = scale;
        public Vector3 Rotation { get; } = rotation;
        public Vector3 Position { get; } = position;
        public Vector4 Color { get; } = color;
        public int Visibility { get; } = visibility;
    }

    private sealed class MutablePropEvent(
        string magic,
        short id,
        short time,
        int duration,
        ushort modelId,
        ushort bodyId,
        int variant,
        int itemOrder) : MutableTimelineEvent(id, itemOrder)
    {
        public string Magic { get; } = magic;
        public short Time { get; } = time;
        public int Duration { get; } = duration;
        public ushort ModelId { get; } = modelId;
        public ushort BodyId { get; } = bodyId;
        public int Variant { get; } = variant;
    }

    private sealed class MutableVisibilityEvent(
        string magic,
        short id,
        short time,
        int fadeFrames,
        float startVisibility,
        float endVisibility,
        bool enableFilter,
        int filter,
        int itemOrder) : MutableTimelineEvent(id, itemOrder)
    {
        public string Magic { get; } = magic;
        public short Time { get; } = time;
        public int FadeFrames { get; } = fadeFrames;
        public float StartVisibility { get; } = startVisibility;
        public float EndVisibility { get; } = endVisibility;
        public bool EnableFilter { get; } = enableFilter;
        public int Filter { get; } = filter;
    }
}

public sealed record TmbAnimationEvent(
    string Magic,
    short Id,
    int Time,
    int Duration,
    int Flags,
    float AnimationStart,
    float AnimationEnd,
    string Path,
    short? TrackId,
    int? TrackOrder,
    int ItemOrder);

public sealed record TmbVfxEvent(
    string Magic,
    short Id,
    int Time,
    int Duration,
    string Path,
    short BindPoint1,
    short BindPoint2,
    short BindPoint3,
    short BindPoint4,
    Vector3 Scale,
    Vector3 Rotation,
    Vector3 Position,
    Vector4 Color,
    int Visibility,
    short? TrackId,
    int? TrackOrder,
    int ItemOrder);

public sealed record TmbPropEvent(
    string Magic,
    short Id,
    int Time,
    int Duration,
    ushort ModelId,
    ushort BodyId,
    int Variant,
    short? TrackId,
    int? TrackOrder,
    int ItemOrder);

public sealed record TmbVisibilityEvent(
    string Magic,
    short Id,
    int Time,
    int FadeFrames,
    float StartVisibility,
    float EndVisibility,
    bool EnableFilter,
    int Filter,
    short? TrackId,
    int? TrackOrder,
    int ItemOrder);
