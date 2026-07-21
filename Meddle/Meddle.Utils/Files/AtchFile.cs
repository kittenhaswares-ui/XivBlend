using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Meddle.Utils.Files;

/// <summary>
/// Minimal, defensive reader for FFXIV attachment-offset files. XivBlend only
/// needs the authored transform for a named attachment category, so this does
/// not expose the mutable metadata surface used by mod editors.
/// </summary>
public sealed class AtchFile
{
    private const int AccessoryBitfieldBytes = 32;
    private const int StateBytes = 32;
    private const int MaximumPoints = AccessoryBitfieldBytes * 8;
    private const int MaximumStates = 64;
    private const int MaximumBoneNameBytes = 256;

    public ushort StateCount { get; }
    public IReadOnlyList<AtchPoint> Points { get; }

    public AtchFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 + AccessoryBitfieldBytes)
        {
            throw new InvalidDataException("ATCH file is truncated.");
        }

        var pointCount = BinaryPrimitives.ReadUInt16LittleEndian(data);
        StateCount = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if (pointCount > MaximumPoints || StateCount > MaximumStates)
        {
            throw new InvalidDataException(
                $"ATCH file declares an unreasonable {pointCount} points or {StateCount} states.");
        }

        var namesBytes = checked((int)pointCount * 4);
        var statesBytes = checked((int)pointCount * StateCount * StateBytes);
        var stateStart = checked(4 + namesBytes + AccessoryBitfieldBytes);
        if (stateStart > data.Length || statesBytes > data.Length - stateStart)
        {
            throw new InvalidDataException("ATCH point or state table leaves the file bounds.");
        }

        var pointNames = new string[pointCount];
        Span<byte> reversed = stackalloc byte[4];
        for (var index = 0; index < pointNames.Length; index++)
        {
            var encoded = data.Slice(4 + index * 4, 4);
            var encodedLength = encoded.IndexOf((byte)0);
            if (encodedLength < 0)
            {
                encodedLength = encoded.Length;
            }

            for (var characterIndex = 0; characterIndex < encodedLength; characterIndex++)
            {
                reversed[characterIndex] = encoded[encodedLength - characterIndex - 1];
            }

            pointNames[index] = Encoding.ASCII.GetString(reversed[..encodedLength]);
            if (pointNames[index].Length is 0 or > 3
                || pointNames[index].Any(character => !char.IsAsciiLetterOrDigit(character)))
            {
                throw new InvalidDataException($"ATCH point {index} has an invalid category name.");
            }
        }

        var accessoryBits = data.Slice(4 + namesBytes, AccessoryBitfieldBytes);
        var points = new AtchPoint[pointCount];
        var statePosition = stateStart;
        for (var pointIndex = 0; pointIndex < pointCount; pointIndex++)
        {
            var states = new AtchEntry[StateCount];
            for (var stateIndex = 0; stateIndex < StateCount; stateIndex++)
            {
                var state = data.Slice(statePosition, StateBytes);
                var boneOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(state));
                var bone = ReadNullTerminatedAscii(data, boneOffset, MaximumBoneNameBytes);
                var scale = ReadSingle(state, 4);
                var offset = new Vector3(
                    ReadSingle(state, 8),
                    ReadSingle(state, 12),
                    ReadSingle(state, 16));
                var rotation = new Vector3(
                    ReadSingle(state, 20),
                    ReadSingle(state, 24),
                    ReadSingle(state, 28));
                if (string.IsNullOrWhiteSpace(bone)
                    || !float.IsFinite(scale)
                    || !IsFinite(offset)
                    || !IsFinite(rotation))
                {
                    throw new InvalidDataException(
                        $"ATCH point {pointIndex} state {stateIndex} has invalid transform data.");
                }

                states[stateIndex] = new AtchEntry(bone, scale, offset, rotation);
                statePosition += StateBytes;
            }

            var accessory = (accessoryBits[pointIndex >> 3] & (1 << (pointIndex & 7))) != 0;
            points[pointIndex] = new AtchPoint(pointNames[pointIndex], accessory, states);
        }

        Points = points;
    }

    public AtchPoint? GetPoint(string type)
    {
        return Points.FirstOrDefault(point =>
            string.Equals(point.Type, type, StringComparison.OrdinalIgnoreCase));
    }

    private static float ReadSingle(ReadOnlySpan<byte> data, int offset)
    {
        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[offset..]));
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> data, int offset, int maximumBytes)
    {
        if (offset < 0 || offset >= data.Length)
        {
            throw new InvalidDataException("ATCH bone-name offset leaves the file bounds.");
        }

        var available = Math.Min(maximumBytes, data.Length - offset);
        var bytes = data.Slice(offset, available);
        var terminator = bytes.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException("ATCH bone name is not safely terminated.");
        }

        var nameBytes = bytes[..terminator];
        if (nameBytes.IsEmpty)
        {
            throw new InvalidDataException("ATCH bone name contains invalid bytes.");
        }

        foreach (var value in nameBytes)
        {
            if (value is < 0x20 or > 0x7e)
            {
                throw new InvalidDataException("ATCH bone name contains invalid bytes.");
            }
        }

        return Encoding.ASCII.GetString(nameBytes);
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}

public sealed record AtchPoint(
    string Type,
    bool Accessory,
    IReadOnlyList<AtchEntry> Entries);

public sealed record AtchEntry(
    string Bone,
    float Scale,
    Vector3 Offset,
    Vector3 Rotation);
