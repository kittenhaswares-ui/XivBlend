using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace Meddle.Utils.Files.SqPack;

public class SqPackFile
{
    public readonly SqPackFileInfo FileHeader;
    
    private readonly byte[] rawData;
    public ReadOnlySpan<byte> RawData => resolvedData ?? rawData;
    private byte[]? resolvedData;
    
    public void ResolveData(byte[] key)
    {
        if (FileHeader.Type != FileType.Empty) throw new InvalidOperationException("Called resolve for non Empty type file");
        var mangledFile = RawData[64..].ToArray();
        var compressedFile = new byte[key.Length + mangledFile.Length];
        key.CopyTo(compressedFile, 0);
        mangledFile.CopyTo(compressedFile, key.Length);
        using var inputStream = new MemoryStream(compressedFile);
        using var zlibStream = new ZLibStream(inputStream, CompressionMode.Decompress);
        using var outputStream = new MemoryStream();
        zlibStream.CopyTo(outputStream);
        var output = outputStream.ToArray();
        resolvedData = output;
    }

    public SqPackFile(SqPackFileInfo fileHeader, byte[] fileData) : this(fileHeader, (ReadOnlySpan<byte>)fileData) { }
    
    public SqPackFile(SqPackFileInfo fileHeader, ReadOnlySpan<byte> fileData)
    {
        FileHeader = fileHeader;
        rawData = fileData.ToArray();
    }
}

public class SqPackIndex2File
{
    public SqPackHeader FileHeader { get; }
    public SqPackIndexHeader IndexHeader { get; }
    private readonly Index2HashTableEntry[] entries;
    public ReadOnlySpan<Index2HashTableEntry> Entries => entries;

    public SqPackIndex2File(byte[] data)
    {
        var reader = new BinaryReader(new MemoryStream(data));
        FileHeader = reader.Read<SqPackHeader>();
        
        reader.BaseStream.Seek(FileHeader.size, SeekOrigin.Begin);
        IndexHeader = reader.Read<SqPackIndexHeader>();
        
        reader.BaseStream.Seek(IndexHeader.IndexDataOffset, SeekOrigin.Begin);
        var entryCount = IndexHeader.IndexDataSize / Unsafe.SizeOf<Index2HashTableEntry>();
        entries = new Index2HashTableEntry[entryCount];
        for (var i = 0; i < entryCount; i++)
        {
            entries[i] = reader.Read<Index2HashTableEntry>();
        }
    }
}

public class SqPackIndexFile
{
    public SqPackHeader FileHeader { get; }
    public SqPackIndexHeader IndexHeader { get; }
    private readonly IndexHashTableEntry[] entries;
    public ReadOnlySpan<IndexHashTableEntry> Entries => entries;

    public SqPackIndexFile(byte[] data)
    {
        var reader = new BinaryReader(new MemoryStream(data));
        FileHeader = reader.Read<SqPackHeader>();
        
        reader.BaseStream.Seek(FileHeader.size, SeekOrigin.Begin);
        IndexHeader = reader.Read<SqPackIndexHeader>();
        
        reader.BaseStream.Seek(IndexHeader.IndexDataOffset, SeekOrigin.Begin);
        var entryCount = IndexHeader.IndexDataSize / Unsafe.SizeOf<IndexHashTableEntry>();
        entries = new IndexHashTableEntry[entryCount];
        for (var i = 0; i < entryCount; i++)
        {
            entries[i] = reader.Read<IndexHashTableEntry>();
        }
    }
}
