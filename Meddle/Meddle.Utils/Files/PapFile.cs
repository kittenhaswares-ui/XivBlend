using System.Runtime.InteropServices;
using System.Text;

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
    
    public PapFile(byte[] data) : this((ReadOnlySpan<byte>)data) { }

    public PapFile(ReadOnlySpan<byte> data)
    {
        this.data = data.ToArray();
        var reader = new SpanBinaryReader(data);
        FileHeader = reader.Read<PapFileHeader>();
        Animations = reader.Read<PapAnimation>(FileHeader.AnimationCount).ToArray();
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
