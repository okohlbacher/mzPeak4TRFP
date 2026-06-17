namespace MZPeak.Storage;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.IO.Compression;

using ParquetSharp.IO;
using ParquetSharp.Encryption;
using System.Text;
using Microsoft.Extensions.Logging;

using ParquetSharp;
using ParquetSharp.Arrow;
using DecryptionConfigurations = Dictionary<string, ParquetSharp.FileDecryptionProperties>;



public enum EntityTypeTag
{
    Spectrum,
    Chromatogram,
    WavelengthSpectrum,
    Other
}

[JsonConverter(typeof(EntityTypeJsonConverter))]
public record struct EntityType(EntityTypeTag Tag, string? Value) : IComparable<EntityTypeTag>
{
    public int CompareTo(EntityTypeTag other)
    {
        return Tag.CompareTo(other);
    }

    public static EntityType Spectrum => new(EntityTypeTag.Spectrum, null);
    public static EntityType Chromatogram => new(EntityTypeTag.Chromatogram, null);
    public static EntityType WavelengthSpectrum => new(EntityTypeTag.WavelengthSpectrum, null);
}


class EntityTypeJsonConverter : JsonConverter<EntityType>
{
    public override EntityType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException();
        }

        var val = reader.GetString()?.ToLower();
        if (val == null) throw new JsonException("Entity type JSON cannot be null");
        return val switch
        {
            "spectrum" => new EntityType(EntityTypeTag.Spectrum, null),
            "chromatogram" => new EntityType(EntityTypeTag.Chromatogram, null),
            "wavelength spectrum" => new EntityType(EntityTypeTag.WavelengthSpectrum, null),
            _ => new EntityType(EntityTypeTag.Other, val)
        };
    }

    public override void Write(Utf8JsonWriter writer, EntityType value, JsonSerializerOptions options)
    {
        if (value.Tag == EntityTypeTag.Other) {
            writer.WriteStringValue(value.Value);
        } else
        {
            var text = value.Tag switch
            {
                EntityTypeTag.Spectrum => "spectrum",
                EntityTypeTag.Chromatogram => "chromatogram",
                EntityTypeTag.WavelengthSpectrum => "wavelength spectrum",
                _ => throw new NotImplementedException()
            };
            writer.WriteStringValue(text);
        }

    }
}


public enum DataKindTag
{
    DataArrays,
    Metadata,
    Peaks,
    Other,
    Proprietary
}


[JsonConverter(typeof(DataKindTJsonConverter))]
public record struct DataKind(DataKindTag Tag, string? Value) : IComparable<DataKindTag>
{
    public int CompareTo(DataKindTag other)
    {
        return Tag.CompareTo(other);
    }

    public static DataKind DataArrays => new(DataKindTag.DataArrays, null);
    public static DataKind Metadata => new(DataKindTag.Metadata, null);
    public static DataKind Peaks => new(DataKindTag.Peaks, null);
    public static DataKind Proprietary => new(DataKindTag.Proprietary, null);
}


class DataKindTJsonConverter : JsonConverter<DataKind>
{
    public override DataKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException();
        }

        var val = reader.GetString()?.ToLower();
        if (val == null) throw new JsonException("Data kind JSON cannot be null");
        return val switch
        {
            "data arrays" => new(DataKindTag.DataArrays, null),
            "data_array" => new(DataKindTag.DataArrays, null),
            "metadata" => new(DataKindTag.Metadata, null),
            "peaks" => new(DataKindTag.Peaks, null),
            "proprietary" => new(DataKindTag.Proprietary, null),
            _ => new(DataKindTag.Other, val)
        };
    }

    public override void Write(Utf8JsonWriter writer, DataKind value, JsonSerializerOptions options)
    {
        if (value.Tag == DataKindTag.Other)
        {
            writer.WriteStringValue(value.Value);
        }
        else
        {
            var text = value.Tag switch
            {
                DataKindTag.DataArrays => "data arrays",
                DataKindTag.Metadata => "metadata",
                DataKindTag.Peaks => "peaks",
                DataKindTag.Proprietary => "proprietary",
                _ => throw new NotImplementedException()
            };
            writer.WriteStringValue(text);
        }

    }
}


[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record FileIndexEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("entity_type")]
    public EntityType EntityType { get; set; }

    [JsonPropertyName("data_kind")]
    public DataKind DataKind { get; set; }

    public static FileIndexEntry FromEntityAndData(EntityType entityType, DataKind dataKind)
    {
        string entityTypeTag = "";
        switch (entityType.Tag)
        {
            case EntityTypeTag.Chromatogram:
                {
                    entityTypeTag = "chromatograms";
                    break;
                }
            case EntityTypeTag.Spectrum:
                {
                    entityTypeTag = "spectra";
                    break;
                }
            case EntityTypeTag.WavelengthSpectrum:
                {
                    entityTypeTag = "wavelength_spectra";
                    break;
                }
            case EntityTypeTag.Other:
                {
                    throw new NotImplementedException(entityType.ToString());
                }
        }
        string dataKindTag = "";
        switch (dataKind.Tag)
        {
            case DataKindTag.DataArrays:
                {
                    dataKindTag = "data";
                    break;
                }
            case DataKindTag.Metadata:
                {
                    dataKindTag = "metadata";
                    break;
                }
            case DataKindTag.Peaks:
                {
                    dataKindTag = "peaks";
                    break;
                }
            case DataKindTag.Proprietary:
                {
                    throw new NotImplementedException(dataKind.ToString());
                }
            case DataKindTag.Other:
                {
                    throw new NotImplementedException(dataKind.ToString());
                }
        }
        return new FileIndexEntry(
            string.Format("{0}_{1}.parquet", entityTypeTag, dataKindTag),
            entityType,
            dataKind
        );
    }

    public FileIndexEntry(string name, EntityType entityType, DataKind dataKind)
    {
        Name = name;
        EntityType = entityType;
        DataKind = dataKind;
    }
}


[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class FileIndex
{
    public const string FILE_NAME = "mzpeak_index.json";

    [JsonPropertyName("files")]
    public List<FileIndexEntry> Files { get; set; }

    [JsonPropertyName("metadata")]
    public JsonObject Metadata { get; set; }

    public FileIndexEntry? FindEntry(EntityType entityType, DataKind dataKind)
    {
        foreach(var entry in Files)
        {
            if (entry.DataKind == dataKind && entry.EntityType == entityType)
                return entry;
        }
        return null;
    }

    public static DecryptionConfigurations UniformDecryption(FileDecryptionProperties decryptionProperties)
    {
        DecryptionConfigurations decryptionConfigs = new();
        decryptionConfigs[FileIndexEntry.FromEntityAndData(EntityType.Spectrum, DataKind.DataArrays).Name] = decryptionProperties;
        decryptionConfigs[FileIndexEntry.FromEntityAndData(EntityType.Spectrum, DataKind.Peaks).Name] = decryptionProperties;
        decryptionConfigs[FileIndexEntry.FromEntityAndData(EntityType.Spectrum, DataKind.Metadata).Name] = decryptionProperties;
        decryptionConfigs[FileIndexEntry.FromEntityAndData(EntityType.Chromatogram, DataKind.DataArrays).Name] = decryptionProperties;
        decryptionConfigs[FileIndexEntry.FromEntityAndData(EntityType.Chromatogram, DataKind.Metadata).Name] = decryptionProperties;
        decryptionConfigs[FileIndexEntry.FromEntityAndData(EntityType.WavelengthSpectrum, DataKind.DataArrays).Name] = decryptionProperties;
        decryptionConfigs[FileIndexEntry.FromEntityAndData(EntityType.WavelengthSpectrum, DataKind.Metadata).Name] = decryptionProperties;
        return decryptionConfigs;
    }

    public FileIndex()
    {
        Files = new List<FileIndexEntry>();
        Metadata = new JsonObject();
    }
}


public interface IMZPeakArchiveStorage
{
    internal static ILogger? Logger = null;

    public DecryptionConfigurations DecryptionConfigurations { get; set; }

    /// <summary>
    /// Get the list of file names in the archive. This may include files not in the index.
    /// </summary>
    /// <returns></returns>
    public List<string> FileNames();

    /// <summary>
    /// Open the archive member corresponding to `entityType` and `dataKind`, if one exists.
    ///
    /// If multiple matches exist, only the first is returned.
    /// </summary>
    /// <param name="entityType"></param>
    /// <param name="dataKind"></param>
    /// <returns></returns>
    public Stream? OpenEntry(EntityType entityType, DataKind dataKind)
    {
        var entry = FileIndex().FindEntry(entityType, dataKind);
        if (entry == null)
        {
            return null;
        }
        else
        {
            return OpenStream(entry.Name);
        }
    }

    public FileReader? OpenFromFileIndexEntry(FileIndexEntry entry, ReaderProperties? props=null, ArrowReaderProperties? arrowProps=null)
    {
        if (props == null)
            props = ReaderProperties.GetDefaultReaderProperties();
        if (arrowProps == null)
            arrowProps = ArrowReaderProperties.GetDefault();
        if (entry == null) return null;
        var stream = OpenStream(entry.Name);
        Logger?.LogTrace("Opening {entry}", entry);
        if (DecryptionConfigurations.ContainsKey(entry.Name))
        {
            Logger?.LogTrace("{entry} has decryption config", entry);
            props.FileDecryptionProperties = DecryptionConfigurations[entry.Name];
        }
        return stream == null ? null : new FileReader(
            new ManagedRandomAccessFile(stream),
            props,
            arrowProps
        );
    }

    /// <summary>
    /// Open the spectrum data arrays volume, if it exists, null otherwise.
    /// </summary>
    /// <returns></returns>
    public FileReader? SpectrumData(long bufferSize = 4096 * 4, bool prebuffer = false)
    {
        var entry = FileIndex().FindEntry(EntityType.Spectrum, DataKind.DataArrays);
        var arrowProps = ArrowReaderProperties.GetDefault();
        arrowProps.BatchSize = bufferSize;
        arrowProps.PreBuffer = prebuffer;
        if (entry == null) return null;
        return OpenFromFileIndexEntry(entry, null, arrowProps);
    }

    /// <summary>
    /// Open the spectrum data arrays volume containing explicitly centroided peaks, if it exists, null otherwise.
    /// </summary>
    /// <returns></returns>
    public FileReader? SpectrumPeaks(long bufferSize = 4096 * 4, bool prebuffer = false)
    {
        var entry = FileIndex().FindEntry(EntityType.Spectrum, DataKind.Peaks);
        var arrowProps = ArrowReaderProperties.GetDefault();
        arrowProps.BatchSize = bufferSize;
        arrowProps.PreBuffer = prebuffer;
        if (entry == null) return null;
        return OpenFromFileIndexEntry(entry, null, arrowProps);
    }

    /// <summary>
    /// Open the chromatogram data arrays volume, if it exists, null otherwise.
    /// </summary>
    /// <returns></returns>
    public FileReader? ChromatogramData(long bufferSize = 4096)
    {
        var entry = FileIndex().FindEntry(EntityType.Chromatogram, DataKind.DataArrays);
        var arrowProps = ArrowReaderProperties.GetDefault();
        arrowProps.BatchSize = bufferSize;
        if (entry == null) return null;
        return OpenFromFileIndexEntry(entry, null, arrowProps);
    }

    /// <summary>
    /// Open the spectrum metadata volume, if it exists, null otherwise.
    /// </summary>
    /// <returns></returns>
    public FileReader? SpectrumMetadata()
    {
        var entry = FileIndex().FindEntry(EntityType.Spectrum, DataKind.Metadata);
        if (entry == null) return null;
        return OpenFromFileIndexEntry(entry, null, null);
    }

    /// <summary>
    /// Open the chromatogram metadata volume, if it exists, null otherwise.
    /// </summary>
    /// <returns></returns>
    public ParquetSharp.Arrow.FileReader? ChromatogramMetadata()
    {
        var entry = FileIndex().FindEntry(EntityType.Chromatogram, DataKind.Metadata);
        if (entry == null) return null;
        return OpenFromFileIndexEntry(entry);
    }

    /// <summary>
    /// Open the wavelength spectrum metadata volume, if it exists, null otherwise.
    /// </summary>
    /// <returns></returns>
    public FileReader? WavelengthSpectrumMetadata()
    {
        var entry = FileIndex().FindEntry(EntityType.WavelengthSpectrum, DataKind.Metadata);
        if (entry == null) return null;
        return OpenFromFileIndexEntry(entry);
    }

    /// <summary>
    /// Open the wavelength spectrum data arrays volume, if it exists, null otherwise.
    /// </summary>
    /// <returns></returns>
    public ParquetSharp.Arrow.FileReader? WavelengthSpectrumData()
    {
        var entry = FileIndex().FindEntry(EntityType.WavelengthSpectrum, DataKind.DataArrays);
        if (entry == null) return null;
        return OpenFromFileIndexEntry(entry);
    }

    /// <summary>
    /// Open the requested file name in the archive
    /// </summary>
    /// <param name="name">The file name to open</param>
    /// <returns>Readable, seekable stream</returns>
    public Stream OpenStream(string name);

    /// <summary>
    /// Access the file index from the archive
    /// </summary>
    /// <returns></returns>
    public FileIndex FileIndex();
}


/// <summary>
/// A facade around a single Stream that spans only a byte range
/// </summary>
public class StreamSegment : Stream
{
    Stream Stream;

    long Offset;

    long _length;

    bool LeaveOpen;

    public StreamSegment(Stream stream, long offset, long length, bool leaveOpen = false)
    {
        Stream = stream;
        Offset = offset;
        _length = length;
        LeaveOpen = leaveOpen;
    }

    public new void Dispose()
    {
        if (!LeaveOpen)
        {
            Stream.Dispose();
        }
        ;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => Stream.Position - Offset;
        set => Stream.Position = Offset + value;
    }

    public override void Flush()
    {
        Stream.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long bytesToRead = count - offset;
        if (Position + bytesToRead > _length)
        {
            bytesToRead = _length - Position;
        }
        return Stream.Read(buffer, offset, (int)bytesToRead);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        switch (origin)
        {
            case SeekOrigin.Begin:
                {
                    Position = offset < _length ? offset : _length;
                    break;
                }
            case SeekOrigin.Current:
                {
                    Position = Position + offset < _length ? Position + offset : _length;
                    break;
                }
            case SeekOrigin.End:
                {
                    throw new NotImplementedException();
                }
        }
        return Position;
    }

    public override void SetLength(long value)
    {
        throw new NotImplementedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }

    public void Configure()
    {
        Stream.Seek(Offset, SeekOrigin.Begin);
    }
}


public abstract class BaseZipArchive : IMZPeakArchiveStorage
{
    static int ZIP_LEADER = 0x04034b50;

    protected List<string> fileNames;
    protected FileIndex fileIndex;

    public DecryptionConfigurations DecryptionConfigurations { get; set; }

    public static bool IsZipArchiveHeader(byte[] data)
    {
        if (data == null || data.Length < 4) return false;
        return BitConverter.ToInt32(data, 0) == ZIP_LEADER;
    }

    public static bool IsStreamZip(Stream stream)
    {
        long? pos = null;
        if (stream.CanSeek)
        {
            pos = stream.Position;
        }

        byte[] buf = [0, 0, 0, 0];
        stream.ReadExactly(buf);
        if (stream.CanSeek && pos != null)
        {
            stream.Position = pos.Value;
        }
        return IsZipArchiveHeader(buf);
    }

    public BaseZipArchive(DecryptionConfigurations? decryptionConfigurations = null)
    {
        fileNames = new List<string>();
        fileIndex = new FileIndex();
        DecryptionConfigurations = decryptionConfigurations ?? new();
    }

    public List<string> FileNames()
    {
        return fileNames;
    }

    public FileIndex FileIndex()
    {
        return fileIndex;
    }

    public abstract Stream OpenArchiveStream();

    public abstract ZipArchive OpenArchive();

    public abstract Stream OpenStream(string name);

    protected void extractInitialMetadata()
    {
        List<string> fileNames = [];
        var archive = OpenArchive();
        FileIndex? fileIndex = null;
        foreach (var entry in archive.Entries)
        {
            fileNames.Add(entry.Name);
            if (entry.Name == Storage.FileIndex.FILE_NAME)
            {
                using (var stream = new StreamReader(entry.Open()))
                {
                    var indexJson = stream.ReadToEnd();
                    fileIndex = JsonSerializer.Deserialize<FileIndex>(indexJson);

                    if (fileIndex == null)
                    {
                        throw new InvalidDataException("Index JSON file did not deserialize successfully");
                    }
                }
            }
        }
        archive.Dispose();
        this.fileNames = fileNames;
        if (fileIndex == null)
        {
            throw new FileNotFoundException("Index JSON file not found");
        }
        this.fileIndex = fileIndex;
    }
}

public class LocalZipArchive : BaseZipArchive
{
    public string Path;

    public LocalZipArchive(string path, DecryptionConfigurations? decryptionConfigurations = null) : base(decryptionConfigurations)
    {
        Path = path;
        extractInitialMetadata();
    }

    public override Stream OpenArchiveStream()
    {
        var stream = File.OpenRead(Path);
        return stream;
    }

    public override ZipArchive OpenArchive()
    {
        var stream = OpenArchiveStream();
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    public override Stream OpenStream(string name)
    {
        {
            var stream = OpenArchiveStream();
            var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = archive.GetEntry(name);
            if (entry == null)
            {
                throw new FileNotFoundException(name);
            }

            // Hacky means of checking that the file isn't compressed
            if (entry.Length != entry.CompressedLength)
            {
                throw new IOException("File in MZPeak ZIP Archive cannot be stored with compression");
            }

            var length = entry.Length;

            // Hacky means of getting the offset of the file contents
            var substreamNotSeekable = entry.Open();
            var offset = stream.Position;
            substreamNotSeekable.Close();

            stream.Close();
            stream = OpenArchiveStream();
            var segStream = new StreamSegment(stream, offset, length);
            segStream.Configure();
            return segStream;
        }
    }
}

public class ZipArchiveStream<T> : BaseZipArchive where T : Stream
{
    T Stream;

    public ZipArchiveStream(T stream, DecryptionConfigurations? decryptionConfigurations = null) : base(decryptionConfigurations)
    {
        Stream = stream;
        if (!Stream.CanRead) throw new InvalidOperationException("Stream must be readable");
        if (!Stream.CanSeek) throw new InvalidOperationException("Stream must be seekable");
        extractInitialMetadata();
    }

    public override ZipArchive OpenArchive()
    {
        var stream = OpenArchiveStream();
        return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
    }

    public override Stream OpenArchiveStream()
    {
        return Stream;
    }

    public override Stream OpenStream(string name)
    {
        var stream = OpenArchiveStream();
        var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry(name);
        if (entry == null)
        {
            throw new FileNotFoundException(name);
        }

        // Hacky means of checking that the file isn't compressed
        if (entry.Length != entry.CompressedLength)
        {
            throw new IOException("File in MZPeak ZIP Archive cannot be stored with compression");
        }

        var length = entry.Length;

        // Hacky means of getting the offset of the file contents
        var substreamNotSeekable = entry.Open();
        var offset = stream.Position;
        substreamNotSeekable.Close();

        stream = OpenArchiveStream();
        var segStream = new StreamSegment(stream, offset, length, true);
        return segStream;
    }
}


public class DirectoryArchive : IMZPeakArchiveStorage
{
    public string Path;
    List<string> fileNames;
    FileIndex fileIndex;
    public DecryptionConfigurations DecryptionConfigurations { get; set; }

    public DirectoryArchive(string path, DecryptionConfigurations? decryptionConfigurations = null)
    {
        Path = path;
        fileNames = new List<string>();
        fileIndex = new FileIndex();
        DecryptionConfigurations = decryptionConfigurations ?? new();
        extractInitialMetadata();
    }

    public FileIndex FileIndex()
    {
        return fileIndex;
    }

    public List<string> FileNames()
    {
        return fileNames;
    }

    public Stream OpenStream(string name)
    {
        var pathOf = System.IO.Path.Join(Path, name);
        if (!File.Exists(pathOf))
        {
            throw new FileNotFoundException(name);
        }
        return new FileStream(pathOf, FileMode.Open);
    }

    void extractInitialMetadata()
    {
        List<string> fileNames = [];
        FileIndex? fileIndex = null;

        foreach (var entry in Directory.EnumerateFileSystemEntries(Path))
        {
            if (!File.Exists(entry)) continue;

            fileNames.Add(entry);
            var fName = System.IO.Path.GetFileName(entry);
            if (fName == Storage.FileIndex.FILE_NAME)
            {

                using (var stream = new StreamReader(File.Open(entry, FileMode.Open)))
                {
                    var indexJson = stream.ReadToEnd();
                    fileIndex = JsonSerializer.Deserialize<FileIndex>(indexJson);

                    if (fileIndex == null)
                    {
                        throw new InvalidDataException("Index JSON file did not deserialize successfully");
                    }
                }
            }
        }

        this.fileNames = fileNames;
        if (fileIndex == null)
        {
            throw new FileNotFoundException("Index JSON file not found");
        }
        this.fileIndex = fileIndex;
    }
}


public interface IMZPeakArchiveWriter : IDisposable
{
    internal static ILogger? Logger = null;

    public Stream OpenStream(FileIndexEntry indexEntry);

    public FileIndex FileIndex();
}


public class DirectoryArchiveWriter : IMZPeakArchiveWriter
{
    public static ILogger? Logger = null;

    public string Path;
    public FileIndex FileIndex;

    public DirectoryArchiveWriter(string path)
    {
        Path = path;
        FileIndex = new();
    }

    public void Dispose()
    {
        var path = System.IO.Path.Join(Path, FileIndex.FILE_NAME);
        using (var stream = File.Create(path))
        {
            var payload = JsonSerializer.Serialize(FileIndex, options: new JsonSerializerOptions() { WriteIndented = true });
            var bytesOf = new UTF8Encoding().GetBytes(payload);
            stream.Write(bytesOf);
        }
    }

    public Stream OpenStream(FileIndexEntry indexEntry)
    {
        var path = System.IO.Path.Join(Path, indexEntry.Name);
        FileIndex.Files.Add(indexEntry);
        return File.Create(path);
    }

    FileIndex IMZPeakArchiveWriter.FileIndex()
    {
        return FileIndex;
    }
}


public class ZipStreamArchiveWriter<T> : IMZPeakArchiveWriter where T : Stream
{
    // public static ILogger? Logger = null;

    ZipArchive Archive;
    T OuterStream;
    Stream? CurrentStream;
    ZipArchiveEntry? CurrentEntry;
    long LastStart;
    public FileIndex FileIndex;

    public ZipStreamArchiveWriter(T stream)
    {
        OuterStream = stream;
        Archive = new(OuterStream, ZipArchiveMode.Create, true, System.Text.Encoding.UTF8);
        CurrentStream = null;
        CurrentEntry = null;
        LastStart = 0;
        FileIndex = new();
    }

    void CloseCurrent()
    {
        if (CurrentStream != null)
        {
            IMZPeakArchiveWriter.Logger?.LogDebug($"Closing current stream for {CurrentEntry}");
            CurrentStream.Close();
            IMZPeakArchiveWriter.Logger?.LogDebug($"{(OuterStream.Position - LastStart) / 1000000.0} MB written");
            CurrentStream = null;
            CurrentEntry = null;
        }
    }

    public void Dispose()
    {
        CloseCurrent();
        var entry = Archive.CreateEntry(FileIndex.FILE_NAME, CompressionLevel.NoCompression);
        using (var stream = entry.Open())
        {
            IMZPeakArchiveWriter.Logger?.LogDebug("Writing file index");
            var payload = JsonSerializer.Serialize(FileIndex, options: new JsonSerializerOptions() { WriteIndented = true });
            var bytesOf = new UTF8Encoding().GetBytes(payload);
            stream.Write(bytesOf);
        }
        IMZPeakArchiveWriter.Logger?.LogDebug("Closing ZIP archive");
        Archive.Dispose();
    }

    public Stream OpenStream(FileIndexEntry indexEntry)
    {
        CloseCurrent();
        IMZPeakArchiveWriter.Logger?.LogDebug($"Opening {indexEntry}");
        var entry = Archive.CreateEntry(indexEntry.Name, CompressionLevel.NoCompression);
        LastStart = OuterStream.Position;
        CurrentStream = entry.Open();
        CurrentEntry = entry;
        FileIndex.Files.Add(indexEntry);
        return CurrentStream;
    }

    FileIndex IMZPeakArchiveWriter.FileIndex()
    {
        return FileIndex;
    }
}