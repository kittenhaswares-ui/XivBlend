using System.Runtime.InteropServices;
using System.Text;

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Meddle.Utils.Files;

public class PapFile
{
    // PAP files begin with the ASCII bytes "pap ". SpanBinaryReader reads
    // integers in the native little-endian layout used by FFXIV, so those
    // bytes are represented by 0x20706170 as a uint.
    public const uint PapMagic = 0x20706170;
    public PapFileHeader FileHeader;
    public PapAnimation[] Animations;
    
    private readonly byte[] data;
    public ReadOnlySpan<byte> RawData => data;
    public ReadOnlySpan<byte> HavokData => RawData.Slice((int)FileHeader.HavokOffset, (int)FileHeader.FooterOffset - (int)FileHeader.HavokOffset);
    public ReadOnlySpan<byte> FooterData => RawData.Slice((int)FileHeader.FooterOffset);
    
    /// <summary>
    /// Returns the embedded TMLB belonging to an animation table entry.
    /// Vanilla PAPs use a few alignment bytes between timelines; accepting a
    /// small bounded gap also tolerates harmless padding written by mod tools.
    /// </summary>
    public ReadOnlySpan<byte> GetTimelineData(int animationIndex)
    {
        if ((uint)animationIndex >= (uint)Animations.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(animationIndex));
        }

        var position = checked((int)FileHeader.FooterOffset);
        if (position < 0 || position > data.Length - 12)
        {
            throw new InvalidDataException("PAP footer offset is outside the file.");
        }

        for (var index = 0; index <= animationIndex; index++)
        {
            position = FindNextTimeline(position);
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position + 4));
            if (size < 12 || position > data.Length - size)
            {
                throw new InvalidDataException($"PAP embedded timeline {index} has invalid size {size}.");
            }

            if (index == animationIndex)
            {
                return data.AsSpan(position, size);
            }

            position += size;
        }

        throw new InvalidDataException("PAP embedded timeline could not be resolved.");
    }

    private int FindNextTimeline(int start)
    {
        // Normal padding is 0-3 bytes. Some mod tools preserve a different
        // file alignment, so allow a tiny gap without searching arbitrary data.
        var maximum = Math.Min(data.Length - 12, checked(start + 15));
        for (var candidate = start; candidate <= maximum; candidate++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(candidate)) == TmbTimelineFile.TmlbMagic)
            {
                return candidate;
            }
        }

        throw new InvalidDataException("PAP embedded timeline is missing after its alignment padding.");
    }

    public PapFile(byte[] data) : this((ReadOnlySpan<byte>)data) { }

    public PapFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < Unsafe.SizeOf<PapFileHeader>())
        {
            throw new InvalidDataException("PAP header is truncated.");
        }

        this.data = data.ToArray();
        var reader = new SpanBinaryReader(data);
        FileHeader = reader.Read<PapFileHeader>();
        if (FileHeader.Magic != PapMagic)
        {
            throw new InvalidDataException("File does not begin with the PAP magic.");
        }

        if (FileHeader.AnimationCount is 0 or > 4096)
        {
            throw new InvalidDataException(
                $"PAP declares invalid animation count {FileHeader.AnimationCount}.");
        }

        var animationTableBytes = checked(FileHeader.AnimationCount * Unsafe.SizeOf<PapAnimation>());
        if (reader.Remaining < animationTableBytes)
        {
            throw new InvalidDataException("PAP animation table is truncated.");
        }

        Animations = reader.Read<PapAnimation>(FileHeader.AnimationCount).ToArray();
        var havokOffset = checked((int)FileHeader.HavokOffset);
        var footerOffset = checked((int)FileHeader.FooterOffset);
        if (havokOffset < reader.Position
            || footerOffset <= havokOffset
            || footerOffset > data.Length)
        {
            throw new InvalidDataException("PAP Havok/footer offsets are invalid.");
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 40)]
    public unsafe struct PapAnimation
    {
        public fixed byte Name[32];
        public ushort Type;
        public short HavokIndex;
        public int IsFaceRaw;
        
        public string GetName => GetNameString();
        public bool IsFace => IsFaceRaw != 0;
        
        private string GetNameString()
        {
            fixed (byte* ptr = Name)
            {
                return Encoding.UTF8.GetString(ptr, 32).TrimEnd('\0');
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PapFileHeader
    {
        public uint Magic;
        public uint Version;
        public ushort AnimationCount;
        public ushort ModelId;
        public SkeletonType ModelType;
        public byte Variant;
        public uint InfoOffset;
        public uint HavokOffset;
        public uint FooterOffset;
    }

    public enum SkeletonType : byte
    {
        Human = 0,
        Monster = 1,
        DemiHuman = 2,
        Weapon = 3,
    }
}
