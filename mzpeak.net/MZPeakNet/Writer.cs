namespace MZPeak.Writer;

using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apache.Arrow;
using Microsoft.Extensions.Logging;
using MZPeak.Compute;
using MZPeak.ControlledVocabulary;
using MZPeak.Metadata;
using MZPeak.Storage;
using MZPeak.Writer.Data;
using MZPeak.Writer.Visitors;
using ParquetSharp;
using ParquetSharp.Arrow;

using EncryptionConfigurations = Dictionary<string, ParquetSharp.FileEncryptionProperties>;

/// <summary>
/// Represents the current state of the writer during file creation.
/// </summary>
public enum WriterState
{
    /// <summary>Initial state.</summary>
    Start = 0,

    /// <summary>Writing spectrum data arrays.</summary>
    SpectrumData = 1,
    /// <summary>Writing spectrum peak data.</summary>
    SpectrumPeakData = 2,
    /// <summary>Writing spectrum metadata.</summary>
    SpectrumMetadata = 3,

    /// <summary>Writing chromatogram data arrays.</summary>
    ChromatogramData = 4,
    /// <summary>Writing chromatogram metadata.</summary>
    ChromatogramMetadata = 5,

    /// <summary>Writing wavelength spectrum data arrays.</summary>
    WavelengthData = 6,
    /// <summary>Writing wavelength spectrum metadata.</summary>
    WavelengthMetadata = 7,

    /// <summary>Writing other data.</summary>
    OtherData = 900,
    /// <summary>Writing other metadata.</summary>
    OtherMetadata = 901,

    /// <summary>Writing complete.</summary>
    Done = 999,
}


/// <summary>
/// A configuration struct that controls the size of certain important units of a Parquet file.
/// </summary>
/// <param name="PageSize">
///     The number of bytes per Parquet data page. The larger this is, the greater window the compression
///     algorithm has to reduce data size, but random access granularity when the page index is used
/// </param>
/// <param name="RowGroupSize">
///     The maximum number of bytes per Parquet row group. This is a collection of data pages that must be
///     written out together. The larger this value, the more data pages can be included in the same group
///     and thus will share the same dictionary, indirectly affecting overall file size.
/// </param>
/// <param name="DictionarySize">
///     The maximum number of bytes allowed for the dictionary data page for a column in a given row group.
///     The larger this is, the greater the number of repeated elements that can fit in the dictionary
///     before overflowing and forcing the writer to fall back to storing everything directly in plain encoding.
/// </param>
/// <param name="EntryBufferSize">
///     The number of spectra to buffer between writes.
/// </param>
/// <param name="CompressionLevel">
///     The Zstandard compression level to use when compressing data.
/// </param>
public record ParquetDataWriterConfig(
    long PageSize = 1048576,
    long RowGroupSize = 1048576,
    long DictionarySize = 1048576,
    ulong EntryBufferSize = 5000,
    int CompressionLevel = 3
);


/// <summary>
/// Writer for creating mzPeak archive files containing mass spectrometry data.
/// </summary>
public class MZPeakWriter : IDisposable
{
    /// <summary>Optional logger for diagnostic output.</summary>
    public static ILogger? Logger = null;

    /// <summary>The current writer state.</summary>
    public WriterState State = WriterState.Start;
    MzPeakMetadata MzPeakMetadata;
    IMZPeakArchiveWriter Storage;

    protected string? PeakPath = null;
    protected Stream? PeakStream = null;
    protected FileWriter? PeakWriter = null;

    SpectrumMetadataBuilder SpectrumMetadata;
    ChromatogramMetadataBuilder ChromatogramMetadata;
    WavelengthSpectrumMetadataBuilder? WavelengthSpectrumMetadata;

    BaseDataLayoutWriter SpectrumData;
    BaseDataLayoutWriter ChromatogramData;
    BaseDataLayoutWriter? SpectrumPeakData = null;
    BaseDataLayoutWriter? WavelengthSpectrumData = null;

    public ParquetDataWriterConfig DataWriterConfig {get; set;}

    bool standardContentFlushed = false;

    public bool SpectrumHasArrayType(ArrayType arrayType) => SpectrumData.HasArrayType(arrayType);
    public bool SpectrumPeaksHasArrayType(ArrayType arrayType) => SpectrumPeakData?.HasArrayType(arrayType) ?? false;
    public bool ChromatogramHasArrayType(ArrayType arrayType) => ChromatogramData.HasArrayType(arrayType);

    public EncryptionConfigurations EncryptionConfigurations { get; set; }

    public int CompressionLevel = 3;

    FileIndexEntry? CurrentEntry;
    FileWriter? CurrentWriter;

    /// <summary>Gets or sets the file description metadata.</summary>
    public FileDescription FileDescription { get => MzPeakMetadata.FileDescription; set => MzPeakMetadata.FileDescription = value; }
    /// <summary>Gets or sets the list of instrument configurations.</summary>
    public List<InstrumentConfiguration> InstrumentConfigurations { get => MzPeakMetadata.InstrumentConfigurations; set => MzPeakMetadata.InstrumentConfigurations = value; }
    /// <summary>Gets or sets the list of software used.</summary>
    public List<Software> Softwares { get => MzPeakMetadata.Softwares; set => MzPeakMetadata.Softwares = value; }
    /// <summary>Gets or sets the list of samples.</summary>
    public List<Sample> Samples { get => MzPeakMetadata.Samples; set => MzPeakMetadata.Samples = value; }
    /// <summary>Gets or sets the list of data processing methods.</summary>
    public List<DataProcessingMethod> DataProcessingMethods { get => MzPeakMetadata.DataProcessingMethods; set => MzPeakMetadata.DataProcessingMethods = value; }
    /// <summary>Gets or sets the run-level metadata.</summary>
    public MSRun Run { get => MzPeakMetadata.Run; set => MzPeakMetadata.Run = value; }
    public List<ScanSettings> ScanSettings { get => MzPeakMetadata.ScanSettings; set => MzPeakMetadata.ScanSettings = value; }

    protected static ArrayIndex DefaultSpectrumArrayIndex(bool useChunked = false)
    {
        var builder = useChunked ? ArrayIndexBuilder.ChunkBuilder(BufferContext.Spectrum) : ArrayIndexBuilder.PointBuilder(BufferContext.Spectrum);
        builder.Add(ArrayType.MZArray, BinaryDataType.Float64, Unit.MZ, 0);
        builder.Add(ArrayType.IntensityArray, BinaryDataType.Float32, Unit.NumberOfDetectorCounts);
        return builder.Build();
    }

    protected static ArrayIndex DefaultChromatogramArrayIndex(bool useChunked = false)
    {
        var builder = useChunked ? ArrayIndexBuilder.ChunkBuilder(BufferContext.Chromatogram) : ArrayIndexBuilder.PointBuilder(BufferContext.Chromatogram);
        builder.Add(ArrayType.TimeArray, BinaryDataType.Float64, Unit.Minute, 0);
        builder.Add(ArrayType.IntensityArray, BinaryDataType.Float32, Unit.NumberOfDetectorCounts);
        return builder.Build();
    }

    protected static ArrayIndex DefaultWavelengthSpectrumArrayIndex(bool useChunked = false)
    {
        var builder = useChunked ? ArrayIndexBuilder.ChunkBuilder(BufferContext.WavelengthSpectrum) : ArrayIndexBuilder.PointBuilder(BufferContext.WavelengthSpectrum);
        builder.Add(ArrayType.WavelengthArray, BinaryDataType.Float32, Unit.Nanometer, 0);
        builder.Add(ArrayType.IntensityArray, BinaryDataType.Float32, Unit.NumberOfDetectorCounts);
        return builder.Build();
    }

    protected SchemaDescriptor TranslateSchema(Schema schema)
    {
        var stream = new MemoryStream();
        var tmp = new FileWriter(new ParquetSharp.IO.ManagedOutputStream(stream), schema);
        tmp.Close();
        stream.Seek(0, SeekOrigin.Begin);
        var reader = new ParquetFileReader(stream);
        return reader.FileMetaData.Schema;
    }


    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(WavelengthSpectrumMetadata))]
    void initializeWavelengthMetadata()
    {
        WavelengthSpectrumMetadata = new WavelengthSpectrumMetadataBuilder();
    }

    public ParquetSharp.WriterPropertiesBuilder ConfigureByteShuffleColumnsFrom(ParquetSharp.WriterPropertiesBuilder writerProps, ArrayIndex arrayIndex, ArrayType targetArrayType, Schema schema)
    {
        /* Three-fold API workaround
            Step 1: Translate Arrow schema to Parquet schema using temporary in-memory Parquet file because
                ParquetSharp doesn't have a one-shot conversion method.
            Step 2: Traverse the the index to find entries with metadata matches and then traverse the ParquetSchema
                    which are exact or prefix matches. This makes matching list element columns easier.
            Step 3: Disable the dictionary encoding, because the underlying C++ library **always** falls back to PLAIN encoding
                    when the dictionary page is too large, and THEN set the desired encoding.
        */
        var parquetSchema = TranslateSchema(schema);
        foreach (var arrayType in arrayIndex.Entries)
        {
            if (arrayType.ArrayTypeCURIE == targetArrayType.CURIE() && arrayType.BufferFormat != BufferFormat.ChunkEncoding)
            {
                for (var i = 0; i < parquetSchema.NumColumns; i++)
                {
                    var descr = parquetSchema.Column(i);
                    var path = descr.Path.ToDotString();
                    if (arrayType.Path == path || path.StartsWith(arrayType.Path + "."))
                    {
                        Logger?.LogDebug($"Setting {descr.Path} encoding to ByteStreamSplit");
                        writerProps = writerProps.DisableDictionary(descr.Path).Encoding(descr.Path, ParquetSharp.Encoding.ByteStreamSplit);
                    }
                }
            }
        }
        return writerProps;
    }

    protected virtual ParquetSharp.WriterPropertiesBuilder SpectrumDataWriterPropertiesBuilder()
    {
        var writerProps = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(ParquetSharp.Compression.Zstd)
            .CompressionLevel(DataWriterConfig.CompressionLevel)
            .EnableDictionary()
            .EnableStatistics()
            .EnableWritePageIndex()
            .DisableDictionary($"{SpectrumData.LayoutName()}.{SpectrumData.BufferContext.IndexName()}")
            .Encoding(
                $"{SpectrumData.LayoutName()}.{SpectrumData.BufferContext.IndexName()}",
                ParquetSharp.Encoding.DeltaBinaryPacked
            ).DataPagesize(
                DataWriterConfig.PageSize
            ).MaxRowGroupLength(
                DataWriterConfig.RowGroupSize
            ).DictionaryPagesizeLimit(
                DataWriterConfig.DictionarySize
            );

        var schema = SpectrumData.ArrowSchema();
        writerProps = ConfigureByteShuffleColumnsFrom(writerProps, SpectrumData.ArrayIndex, ArrayType.MZArray, schema);
        return writerProps;
    }

    /// <summary>Starts writing spectrum data arrays.</summary>
    public virtual void StartSpectrumData()
    {
        CloseCurrentWriter();
        var entry = FileIndexEntry.FromEntityAndData(EntityType.Spectrum, DataKind.DataArrays);
        var stream = Storage.OpenStream(entry);
        var managedStream = new ParquetSharp.IO.ManagedOutputStream(stream);

        var writerProps = SpectrumDataWriterPropertiesBuilder();

        var schema = SpectrumData.ArrowSchema();
        var arrowProps = new ArrowWriterPropertiesBuilder().StoreSchema();

        var writer = new FileWriter(managedStream, schema, writerProps.Build(), arrowProps.Build());

        State = WriterState.SpectrumData;
        CurrentWriter = writer;
        CurrentEntry = entry;
    }

    protected virtual ParquetSharp.WriterPropertiesBuilder ChromatogramDataWriterPropertiesBuilder()
    {
        var writerProps = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(ParquetSharp.Compression.Zstd)
            .CompressionLevel(DataWriterConfig.CompressionLevel)
            .EnableDictionary()
            .EnableStatistics()
            .EnableWritePageIndex()
            .DisableDictionary($"{ChromatogramData.LayoutName()}.{ChromatogramData.BufferContext.IndexName()}")
            .Encoding(
                $"{ChromatogramData.LayoutName()}.{ChromatogramData.BufferContext.IndexName()}",
                ParquetSharp.Encoding.DeltaBinaryPacked
            ).DataPagesize(
                DataWriterConfig.PageSize
            ).MaxRowGroupLength(
                DataWriterConfig.RowGroupSize
            ).DictionaryPagesizeLimit(
                DataWriterConfig.DictionarySize
            );

        /* Three-fold API workaround
            Step 1: Translate Arrow schema to Parquet schema using temporary in-memory Parquet file because
                   ParquetSharp doesn't have a one-shot conversion method.
            Step 2: Traverse the the index to find entries with metadata matches and then traverse the ParquetSchema
                    which are exact or prefix matches. This makes matching list element columns easier.
            Step 3: Disable the dictionary encoding, because the underlying C++ library **always** falls back to PLAIN encoding
                    when the dictionary page is too large, and THEN set the desired encoding.
        */
        var schema = ChromatogramData.ArrowSchema();
        writerProps = ConfigureByteShuffleColumnsFrom(writerProps, ChromatogramData.ArrayIndex, ArrayType.TimeArray, schema);
        return writerProps;
    }

    /// <summary>Starts writing chromatogram data arrays.</summary>
    public virtual void StartChromatogramData()
    {
        var entry = FileIndexEntry.FromEntityAndData(EntityType.Chromatogram, DataKind.DataArrays);
        var stream = Storage.OpenStream(entry);
        var managedStream = new ParquetSharp.IO.ManagedOutputStream(stream);
        var schema = ChromatogramData.ArrowSchema();
        var writerProps = ChromatogramDataWriterPropertiesBuilder();

        var arrowProps = new ArrowWriterPropertiesBuilder().StoreSchema();
        CurrentEntry = entry;
        CurrentWriter = new FileWriter(managedStream, schema, writerProps.Build(), arrowProps.Build());
    }

    protected virtual ParquetSharp.WriterPropertiesBuilder WavelengthSpectrumDataWriterPropertiesBuilder()
    {
        if (WavelengthSpectrumData == null) throw new InvalidOperationException("Cannot configure, WavelengthSpectrumData is null");
        var writerProps = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(ParquetSharp.Compression.Zstd)
            .CompressionLevel(DataWriterConfig.CompressionLevel)
            .EnableDictionary()
            .EnableStatistics()
            .EnableWritePageIndex()
            .DisableDictionary($"{SpectrumData.LayoutName()}.{WavelengthSpectrumData.BufferContext.IndexName()}")
            .Encoding(
                $"{WavelengthSpectrumData.LayoutName()}.{WavelengthSpectrumData.BufferContext.IndexName()}",
                ParquetSharp.Encoding.DeltaBinaryPacked
            ).DataPagesize(
                DataWriterConfig.PageSize
            ).MaxRowGroupLength(
                DataWriterConfig.RowGroupSize
            ).DictionaryPagesizeLimit(
                DataWriterConfig.DictionarySize
            );

        var schema = WavelengthSpectrumData.ArrowSchema();
        writerProps = ConfigureByteShuffleColumnsFrom(writerProps, WavelengthSpectrumData.ArrayIndex, ArrayType.WavelengthArray, schema);
        return writerProps;
    }

    /// <summary>Starts writing spectrum data arrays.</summary>
    public virtual void StartWavelengthSpectrumData()
    {
        CloseCurrentWriter();
        if (WavelengthSpectrumData == null)
            return;
        var entry = FileIndexEntry.FromEntityAndData(EntityType.WavelengthSpectrum, DataKind.DataArrays);
        var stream = Storage.OpenStream(entry);
        var managedStream = new ParquetSharp.IO.ManagedOutputStream(stream);

        var writerProps = WavelengthSpectrumDataWriterPropertiesBuilder();
        var schema = WavelengthSpectrumData.ArrowSchema();

        var arrowProps = new ArrowWriterPropertiesBuilder().StoreSchema();

        var writer = new FileWriter(managedStream, schema, writerProps.Build(), arrowProps.Build());

        State = WriterState.WavelengthData;
        CurrentWriter = writer;
        CurrentEntry = entry;
    }

    /// <summary>Closes the current file writer.</summary>
    public virtual void CloseCurrentWriter()
    {
        if (CurrentEntry != null)
        {
            CurrentWriter?.Close();
            CurrentEntry = null;
            CurrentWriter = null;
        }
    }

    public ArrayIndex SpectrumArrayIndex => SpectrumData.ArrayIndex;
    public ArrayIndex ChromatogramArrayIndex => ChromatogramData.ArrayIndex;
    public ArrayIndex? SpectrumPeakArrayIndex => SpectrumPeakData?.ArrayIndex;

    protected virtual ParquetSharp.WriterPropertiesBuilder SpectrumPeakDataWriterPropertiesBuilder()
    {
        if (SpectrumPeakData == null) throw new InvalidOperationException();
        var writerProps = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(ParquetSharp.Compression.Zstd)
            .CompressionLevel(DataWriterConfig.CompressionLevel)
            .EnableDictionary()
            .EnableStatistics()
            .EnableWritePageIndex()
            .DisableDictionary($"{SpectrumPeakData.LayoutName()}.{SpectrumPeakData.BufferContext.IndexName()}")
            .Encoding(
                $"{SpectrumPeakData.LayoutName()}.{SpectrumPeakData.BufferContext.IndexName()}",
                ParquetSharp.Encoding.DeltaBinaryPacked
            ).DataPagesize(
                DataWriterConfig.PageSize
            ).MaxRowGroupLength(
                DataWriterConfig.RowGroupSize
            ).DictionaryPagesizeLimit(
                DataWriterConfig.DictionarySize
            );

        var schema = SpectrumPeakData.ArrowSchema();
        writerProps = ConfigureByteShuffleColumnsFrom(writerProps, SpectrumPeakData.ArrayIndex, ArrayType.MZArray, schema);

        return writerProps;
    }

    /// <summary>Starts writing spectrum peak data.</summary>
    public virtual void StartSpectrumPeakData(bool useTmp=false)
    {
        if (SpectrumPeakData == null) throw new InvalidOperationException();
        if (useTmp)
        {
            PeakPath = Path.GetTempFileName();
            PeakStream = File.Open(PeakPath, FileMode.Create, FileAccess.ReadWrite);
            var managedStream = new ParquetSharp.IO.ManagedOutputStream(PeakStream);
            var writerProps = SpectrumPeakDataWriterPropertiesBuilder();

            var schema = SpectrumPeakData.ArrowSchema();
            var arrowProps = new ArrowWriterPropertiesBuilder().StoreSchema();

            PeakWriter = new FileWriter(managedStream, schema, writerProps.Build(), arrowProps.Build());
        } else
        {
            CloseCurrentWriter();
            var entry = FileIndexEntry.FromEntityAndData(EntityType.Spectrum, DataKind.Peaks);
            var stream = Storage.OpenStream(entry);
            var managedStream = new ParquetSharp.IO.ManagedOutputStream(stream);

            var writerProps = SpectrumPeakDataWriterPropertiesBuilder();

            var schema = SpectrumPeakData.ArrowSchema();
            var arrowProps = new ArrowWriterPropertiesBuilder().StoreSchema();

            var writer = new FileWriter(managedStream, schema, writerProps.Build(), arrowProps.Build());

            State = WriterState.SpectrumPeakData;
            CurrentWriter = writer;
            CurrentEntry = entry;
        }
    }

    protected void StorePeakFileFromTemporaryFile()
    {
        if (PeakWriter == null || PeakStream == null) throw new InvalidOperationException("Peaks are not written to a temporary file");
        FlushSpectrumPeakData();
        CloseCurrentWriter();
        var entry = FileIndexEntry.FromEntityAndData(EntityType.Spectrum, DataKind.Peaks);
        var outStream = Storage.OpenStream(entry);
        PeakWriter.Close();
        PeakStream.Seek(0, SeekOrigin.Begin);
        PeakStream.CopyTo(outStream);
        PeakStream.Close();
    }

    /// <summary>Creates an mzPeak writer.</summary>
    /// <param name="storage">The archive storage backend.</param>
    /// <param name="spectrumArrayIndex">Optional custom spectrum array index.</param>
    /// <param name="chromatogramArrayIndex">Optional custom chromatogram array index.</param>
    /// <param name="includeSpectrumPeakData">Whether to include spectrum peak data.</param>
    /// <param name="spectrumPeakArrayIndex">Optional custom spectrum peak array index.</param>
    /// <param name="useChunked">Optionally set any default array indices to use chunked encoding. Has no effect on explicitly provided array indices.</param>
    public MZPeakWriter(IMZPeakArchiveWriter storage,
                        ArrayIndex? spectrumArrayIndex = null,
                        ArrayIndex? chromatogramArrayIndex = null,
                        bool includeSpectrumPeakData = false,
                        ArrayIndex? spectrumPeakArrayIndex = null,
                        bool useChunked = false,
                        EncryptionConfigurations? encryptionConfigurations = null,
                        ParquetDataWriterConfig? dataWriterConfig = null)
    {
        EncryptionConfigurations = encryptionConfigurations ?? new();
        if (spectrumArrayIndex == null)
            spectrumArrayIndex = DefaultSpectrumArrayIndex(useChunked);
        if (chromatogramArrayIndex == null)
            chromatogramArrayIndex = DefaultChromatogramArrayIndex(useChunked);
        Storage = storage;
        MzPeakMetadata = new();
        SpectrumMetadata = new();
        SpectrumData = spectrumArrayIndex.InferBufferFormat() switch
        {
            BufferFormat.Point => new PointLayoutBuilder(spectrumArrayIndex),
            BufferFormat.ChunkValues => new ChunkLayoutBuilder(spectrumArrayIndex),
            _ => throw new NotImplementedException($"Buffer format {spectrumArrayIndex.InferBufferFormat()} not recognized")
        };
        ChromatogramMetadata = new();
        ChromatogramData = chromatogramArrayIndex.InferBufferFormat() switch
        {
            BufferFormat.Point => new PointLayoutBuilder(chromatogramArrayIndex),
            BufferFormat.ChunkValues => new ChunkLayoutBuilder(chromatogramArrayIndex),
            _ => throw new NotImplementedException($"Buffer format {chromatogramArrayIndex.InferBufferFormat()} not recognized")
        };
        ChromatogramData.ShouldRemoveZeroRuns = false;
        if (includeSpectrumPeakData)
            SpectrumPeakData = new PointLayoutBuilder(spectrumPeakArrayIndex ?? DefaultSpectrumArrayIndex());
        WavelengthSpectrumMetadata = null;
        DataWriterConfig = dataWriterConfig ?? new();
    }

    public void SpectraUseNullMarking()
    {
        if (State != WriterState.Start) throw new InvalidOperationException($"Cannot enable null marking after writing has already begun");
        int k = 0;
        foreach (var e in SpectrumArrayIndex.EntriesFor(ArrayType.MZArray).Where(e => e.BufferFormat == BufferFormat.Point || e.BufferFormat == BufferFormat.ChunkValues))
        {
            k += 1;
            e.Transform = NullInterpolation.NullInterpolateCURIE;
        }
        if (k == 0) throw new InvalidOperationException($"Failed to update transform for any m/z array entries from {SpectrumArrayIndex.Entries}");
        k = 0;
        foreach (var e in SpectrumArrayIndex.EntriesFor(ArrayType.IntensityArray).Where(e => e.BufferFormat == BufferFormat.Point || e.BufferFormat == BufferFormat.ChunkSecondary))
        {
            k += 1;
            e.Transform = NullInterpolation.NullZeroCURIE;
        }
        if (k == 0) throw new InvalidOperationException($"Failed to update transform for any intensity array entries from {SpectrumArrayIndex.Entries}");
    }

    /// <summary>Gets the current spectrum index.</summary>
    public ulong CurrentSpectrum => SpectrumMetadata.SpectrumCounter;
    /// <summary>Gets the current chromatogram index.</summary>
    public ulong CurrentChromatogram => ChromatogramMetadata.ChromatogramCounter;
    /// <summary>Gets the current wavelength spectrum index.</summary>
    public ulong CurrentWavelengthSpectrum => WavelengthSpectrumMetadata?.SpectrumCounter ?? 0;

    /// <summary>Adds spectrum data arrays from a dictionary.</summary>
    /// <param name="entryIndex">The spectrum index.</param>
    /// <param name="arrays">Dictionary mapping array index entries to arrays.</param>
    /// <param name="isProfile">Whether the spectrum is profile mode.</param>
    public EntryDerivedMetadata AddSpectrumData(ulong entryIndex, Dictionary<ArrayIndexEntry, Array> arrays, bool? isProfile = null)
    {
        var r = SpectrumData.Add(entryIndex, arrays, isProfile);
        if (SpectrumData.BufferedSize > DataWriterConfig.RowGroupSize || (SpectrumMetadata.SpectrumCounter % DataWriterConfig.EntryBufferSize == 0 && SpectrumMetadata.SpectrumCounter > 0))
        {
            FlushSpectrumData();
        }
        return r;
    }

    /// <summary>Adds spectrum data arrays.</summary>
    /// <param name="entryIndex">The spectrum index.</param>
    /// <param name="arrays">The data arrays to add.</param>
    /// <param name="isProfile">Whether the spectrum is profile mode.</param>
    public EntryDerivedMetadata AddSpectrumData(ulong entryIndex, IEnumerable<Array> arrays, bool? isProfile = null)
    {
        var r = SpectrumData.Add(entryIndex, arrays, isProfile);
        if (SpectrumData.BufferedSize > DataWriterConfig.RowGroupSize || (SpectrumMetadata.SpectrumCounter % DataWriterConfig.EntryBufferSize == 0 && SpectrumMetadata.SpectrumCounter > 0))
        {
            FlushSpectrumData();
        }
        return r;
    }

    /// <summary>Adds spectrum data from Arrow arrays.</summary>
    /// <param name="entryIndex">The spectrum index.</param>
    /// <param name="arrays">The Arrow arrays to add.</param>
    /// <param name="isProfile">Whether the spectrum is profile mode.</param>
    public EntryDerivedMetadata AddSpectrumData(ulong entryIndex, IEnumerable<IArrowArray> arrays, bool? isProfile = null)
    {
        var r = SpectrumData.Add(entryIndex, arrays, isProfile);
        if (SpectrumData.BufferedSize > DataWriterConfig.RowGroupSize || (SpectrumMetadata.SpectrumCounter % DataWriterConfig.EntryBufferSize == 0 && SpectrumMetadata.SpectrumCounter > 0))
        {
            FlushSpectrumData();
        }
        return r;
    }

    /// <summary>Adds spectrum peak data from a dictionary.</summary>
    /// <param name="entryIndex">The spectrum index.</param>
    /// <param name="arrays">Dictionary mapping array index entries to arrays.</param>
    public EntryDerivedMetadata AddSpectrumPeakData(ulong entryIndex, Dictionary<ArrayIndexEntry, Array> arrays)
    {
        if (SpectrumPeakData == null) throw new InvalidOperationException("Spectrum peak writing is not enabled");
        var r = SpectrumPeakData.Add(entryIndex, arrays, false);
        if (PeakWriter != null && (SpectrumPeakData.BufferedSize > DataWriterConfig.RowGroupSize || SpectrumMetadata.SpectrumCounter % DataWriterConfig.EntryBufferSize == 0))
        {
            FlushSpectrumPeakData();
        }
        return r;
    }

    /// <summary>Adds spectrum peak data arrays.</summary>
    /// <param name="entryIndex">The spectrum index.</param>
    /// <param name="arrays">The data arrays to add.</param>
    public EntryDerivedMetadata AddSpectrumPeakData(ulong entryIndex, IEnumerable<Array> arrays)
    {
        if (SpectrumPeakData == null) throw new InvalidOperationException("Spectrum peak writing is not enabled");
        var r = SpectrumPeakData.Add(entryIndex, arrays, false);
        if (PeakWriter != null && (SpectrumPeakData.BufferedSize > DataWriterConfig.RowGroupSize || SpectrumMetadata.SpectrumCounter % DataWriterConfig.EntryBufferSize == 0))
        {
            FlushSpectrumPeakData();
        }
        return r;
    }

    /// <summary>Adds spectrum peak data from Arrow arrays.</summary>
    /// <param name="entryIndex">The spectrum index.</param>
    /// <param name="arrays">The Arrow arrays to add.</param>
    public EntryDerivedMetadata AddSpectrumPeakData(ulong entryIndex, IEnumerable<IArrowArray> arrays)
    {
        if (SpectrumPeakData == null) throw new InvalidOperationException("Spectrum peak writing is not enabled");
        var r = SpectrumPeakData.Add(entryIndex, arrays, false);
        return r;
    }

    /// <summary>Adds chromatogram data from a dictionary.</summary>
    /// <param name="entryIndex">The chromatogram index.</param>
    /// <param name="arrays">Dictionary mapping array index entries to arrays.</param>
    public EntryDerivedMetadata AddChromatogramData(ulong entryIndex, Dictionary<ArrayIndexEntry, Array> arrays)
    {
        return ChromatogramData.Add(entryIndex, arrays, isProfile: true);
    }

    /// <summary>Adds chromatogram data from Arrow arrays.</summary>
    /// <param name="entryIndex">The chromatogram index.</param>
    /// <param name="arrays">The Arrow arrays to add.</param>
    public EntryDerivedMetadata AddChromatogramData(ulong entryIndex, IEnumerable<IArrowArray> arrays)
    {
        return ChromatogramData.Add(entryIndex, arrays, isProfile: true);
    }

    /// <summary>Adds chromatogram data arrays.</summary>
    /// <param name="entryIndex">The chromatogram index.</param>
    /// <param name="arrays">The data arrays to add.</param>
    public EntryDerivedMetadata AddChromatogramData(ulong entryIndex, IEnumerable<Array> arrays)
    {
        return ChromatogramData.Add(entryIndex, arrays, isProfile: true);
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(WavelengthSpectrumData))]
    void initializeWavelengthData()
    {
        WavelengthSpectrumData = new PointLayoutBuilder(DefaultWavelengthSpectrumArrayIndex(false));
    }

    /// <summary>Adds wavelength spectrum data from a dictionary.</summary>
    /// <param name="entryIndex">The wavelength spectrum index.</param>
    /// <param name="arrays">Dictionary mapping array index entries to arrays.</param>
    public EntryDerivedMetadata AddWavelengthSpectrumData(ulong entryIndex, Dictionary<ArrayIndexEntry, Array> arrays)
    {
        if (WavelengthSpectrumData == null)
            initializeWavelengthData();
        return WavelengthSpectrumData.Add(entryIndex, arrays, isProfile: true);
    }

    /// <summary>Adds wavelength spectrum data from Arrow arrays.</summary>
    /// <param name="entryIndex">The wavelength spectrum index.</param>
    /// <param name="arrays">The Arrow arrays to add.</param>
    public EntryDerivedMetadata AddWavelengthSpectrumData(ulong entryIndex, IEnumerable<IArrowArray> arrays)
    {
        if (WavelengthSpectrumData == null)
            initializeWavelengthData();
        return WavelengthSpectrumData.Add(entryIndex, arrays, isProfile: true);
    }

    /// <summary>Adds wavelength spectrum data arrays.</summary>
    /// <param name="entryIndex">The wavelength spectrum index.</param>
    /// <param name="arrays">The data arrays to add.</param>
    public EntryDerivedMetadata AddWavelengthSpectrumData(ulong entryIndex, IEnumerable<Array> arrays)
    {
        if (WavelengthSpectrumData == null)
            initializeWavelengthData();
        return WavelengthSpectrumData.Add(entryIndex, arrays, isProfile: true);
    }

    /// <summary>Flushes buffered spectrum data to the output.</summary>
    public virtual void FlushSpectrumData()
    {
        if (State == WriterState.Start)
        {
            StartSpectrumData();
        }
        if (State == WriterState.SpectrumData && CurrentWriter != null)
        {
            if (SpectrumData.LayoutName() == "chunk")
            {
                CurrentWriter.NewBufferedRowGroup();
            }
            var batch = SpectrumData.GetRecordBatch();
            Logger?.LogDebug($"Flushing {batch.Length} rows to spectra_data");
            CurrentWriter.WriteBufferedRecordBatch(batch);

        }
        else if (SpectrumData.BufferedRows > 0)
        {
            throw new InvalidOperationException($"Attempting to flush the spectrum data buffer while the current entry is {CurrentEntry}");
        }
    }

    /// <summary>Flushes buffered spectrum peak data to the output.</summary>
    public virtual void FlushSpectrumPeakData()
    {
        if (State == WriterState.SpectrumPeakData && SpectrumPeakData != null && CurrentWriter != null)
        {
            var batch = SpectrumPeakData.GetRecordBatch();
            Logger?.LogDebug($"Flushing {batch.Length} rows to spectra_peaks");
            CurrentWriter.WriteBufferedRecordBatch(batch);
        }
        else if (PeakWriter != null && SpectrumPeakData != null)
        {
            var batch = SpectrumPeakData.GetRecordBatch();
            Logger?.LogDebug($"Flushing {batch.Length} rows to spectra_peaks");
            PeakWriter.WriteBufferedRecordBatch(batch);
        }
        else if (SpectrumData.BufferedRows > 0)
        {
            throw new InvalidOperationException($"Attempting to flush the spectrum peak data buffer while the current entry is {CurrentEntry}");
        }
    }

    /// <summary>Adds a spectrum entry with metadata.</summary>
    /// <param name="id">The spectrum native ID.</param>
    /// <param name="time">The retention time.</param>
    /// <param name="dataProcessingRef">Optional data processing reference.</param>
    /// <param name="mzDeltaModel">Optional m/z delta interpolation model coefficients.</param>
    /// <param name="spectrumParams">Optional spectrum parameters.</param>
    /// <param name="auxiliaryArrays">Optional auxiliary arrays.</param>
    public ulong AddSpectrum(
        string id,
        double time,
        string? dataProcessingRef,
        List<Param>? spectrumParams = null,
        EntryDerivedMetadata? entryMeta = null
    )
    {
        return SpectrumMetadata.AppendSpectrum(
            id,
            time,
            dataProcessingRef,
            spectrumParams ?? new(),
            entryMeta ?? EntryDerivedMetadata.Empty
        );
    }

    /// <summary>Adds a scan entry to a spectrum.</summary>
    /// <param name="sourceIndex">The parent spectrum index.</param>
    /// <param name="instrumentConfigurationRef">Optional instrument configuration reference.</param>
    /// <param name="scanParams">Scan parameters.</param>
    /// <param name="ionMobility">Optional ion mobility value.</param>
    /// <param name="ionMobilityType">Optional ion mobility type CURIE.</param>
    /// <param name="scanWindows">Optional scan windows parameters.</param>
    public void AddScan(
        ulong sourceIndex,
        uint? instrumentConfigurationRef,
        List<Param>? scanParams=null,
        double? ionMobility = null,
        string? ionMobilityType = null,
        List<List<Param>>? scanWindows = null,
        ulong? scanIndex = null,
        string? spectrumReference = null
    )
    {
        SpectrumMetadata.AppendScan(
            sourceIndex,
            instrumentConfigurationRef,
            ionMobility,
            ionMobilityType,
            scanIndex,
            spectrumReference,
            scanParams,
            scanWindows
        );
    }

    /// <summary>Adds a precursor entry to a spectrum.</summary>
    /// <param name="sourceIndex">The parent spectrum index.</param>
    /// <param name="precursorIndex">The precursor spectrum index.</param>
    /// <param name="precursorId">Optional precursor spectrum ID.</param>
    /// <param name="isolationWindowParams">Isolation window parameters.</param>
    /// <param name="activationParams">Activation parameters.</param>
    public void AddPrecursor(
        ulong sourceIndex,
        ulong? precursorIndex,
        string? precursorId,
        List<Param> isolationWindowParams,
        List<Param> activationParams
    )
    {
        SpectrumMetadata.AppendPrecursor(
            sourceIndex,
            precursorIndex,
            precursorId,
            isolationWindowParams,
            activationParams
        );
    }

    /// <summary>Adds a selected ion entry to a precursor.</summary>
    /// <param name="sourceIndex">The parent spectrum index.</param>
    /// <param name="precursorIndex">The precursor index.</param>
    /// <param name="selectedIonParams">Selected ion parameters.</param>
    /// <param name="ionMobility">Optional ion mobility value.</param>
    /// <param name="ionMobilityType">Optional ion mobility type CURIE.</param>
    public void AddSelectedIon(
        ulong sourceIndex,
        ulong? precursorIndex,
        List<Param> selectedIonParams,
        double? ionMobility = null,
        string? ionMobilityType = null
    )
    {
        SpectrumMetadata.AppendSelectedIon(
            sourceIndex,
            precursorIndex,
            ionMobility,
            ionMobilityType,
            selectedIonParams
        );
    }

    /// <summary>Adds a chromatogram entry with metadata.</summary>
    /// <param name="id">The chromatogram native ID.</param>
    /// <param name="dataProcessingRef">Optional data processing reference.</param>
    /// <param name="chromatogramParams">Optional chromatogram parameters.</param>
    /// <param name="entryDerivedMetadata">Optional auxiliary arrays.</param>
    public ulong AddChromatogram(
        string id,
        string? dataProcessingRef,
        List<Param>? chromatogramParams = null,
        EntryDerivedMetadata? entryDerivedMetadata = null
    )
    {
        return ChromatogramMetadata.AppendChromatogram(id, dataProcessingRef, chromatogramParams ?? new(), entryDerivedMetadata);
    }

    /// <summary>Adds a precursor entry to a chromatogram.</summary>
    /// <param name="sourceIndex">The parent chromatogram index.</param>
    /// <param name="precursorIndex">The precursor chromatogram index.</param>
    /// <param name="precursorId">Optional precursor chromatogram ID.</param>
    /// <param name="isolationWindowParams">Isolation window parameters.</param>
    /// <param name="activationParams">Activation parameters.</param>
    public void AddChromatogramPrecursor(
        ulong sourceIndex,
        ulong? precursorIndex,
        string? precursorId,
        List<Param> isolationWindowParams,
        List<Param> activationParams
    )
    {
        ChromatogramMetadata.AppendPrecursor(
            sourceIndex,
            precursorIndex,
            precursorId,
            isolationWindowParams,
            activationParams
        );
    }

    /// <summary>Adds a selected ion entry to a precursor.</summary>
    /// <param name="sourceIndex">The parent chromatogram index.</param>
    /// <param name="precursorIndex">The precursor index.</param>
    /// <param name="selectedIonParams">Selected ion parameters.</param>
    /// <param name="ionMobility">Optional ion mobility value.</param>
    /// <param name="ionMobilityType">Optional ion mobility type CURIE.</param>
    public void AddChromatogramSelectedIon(
        ulong sourceIndex,
        ulong? precursorIndex,
        List<Param> selectedIonParams,
        double? ionMobility = null,
        string? ionMobilityType = null
    )
    {
        ChromatogramMetadata.AppendSelectedIon(
            sourceIndex,
            precursorIndex,
            ionMobility,
            ionMobilityType,
            selectedIonParams
        );
    }

    /// <summary>Adds a wavelength spectrum entry with metadata.</summary>
    /// <param name="id">The spectrum native ID.</param>
    /// <param name="time">The retention time.</param>
    /// <param name="dataProcessingRef">Optional data processing reference.</param>
    /// <param name="spectrumParams">Optional spectrum parameters.</param>
    /// <param name="auxiliaryArrays">Optional auxiliary arrays.</param>
    public ulong AddWavelengthSpectrum(
        string id,
        double time,
        string? dataProcessingRef,
        List<Param>? spectrumParams = null,
        EntryDerivedMetadata? entryDerivedMetadata = null
    )
    {
        if (WavelengthSpectrumMetadata == null)
            initializeWavelengthMetadata();
        return WavelengthSpectrumMetadata.AppendSpectrum(
            id,
            time,
            dataProcessingRef,
            spectrumParams ?? new(),
            entryDerivedMetadata
        );
    }

    /// <summary>Adds a scan entry to a wavelength spectrum.</summary>
    /// <param name="sourceIndex">The parent spectrum index.</param>
    /// <param name="instrumentConfigurationRef">Optional instrument configuration reference.</param>
    /// <param name="scanParams">Scan parameters.</param>
    /// <param name="ionMobility">Optional ion mobility value.</param>
    /// <param name="ionMobilityType">Optional ion mobility type CURIE.</param>
    /// <param name="scanWindows">Optional scan windows parameters.</param>
    public void AddWavelengthScan(
        ulong sourceIndex,
        uint? instrumentConfigurationRef,
        List<Param>? scanParams = null,
        double? ionMobility = null,
        string? ionMobilityType = null,
        List<List<Param>>? scanWindows = null,
        ulong? scanIndex = null,
        string? spectrumReference = null
    )
    {
        if (WavelengthSpectrumMetadata == null)
            initializeWavelengthMetadata();
        WavelengthSpectrumMetadata.AppendScan(
            sourceIndex,
            instrumentConfigurationRef,
            ionMobility,
            ionMobilityType,
            scanIndex,
            spectrumReference,
            scanParams,
            scanWindows
        );
    }

    /// <summary>Writes spectrum metadata to the archive.</summary>
    public void WriteSpectrumMetadata()
    {
        if (State >= WriterState.SpectrumMetadata)
            return;
        CloseCurrentWriter();
        State = WriterState.SpectrumMetadata;
        var entry = FileIndexEntry.FromEntityAndData(EntityType.Spectrum, DataKind.Metadata);
        var stream = Storage.OpenStream(entry);
        var managedStream = new ParquetSharp.IO.ManagedOutputStream(stream);
        var writerProps = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(ParquetSharp.Compression.Zstd)
            .CompressionLevel(DataWriterConfig.CompressionLevel)
            .EnableDictionary()
            .DisableDictionary("spectrum.index")
            .DisableDictionary("scan.source_index")
            .DisableDictionary("precursor.source_index")
            .DisableDictionary("precursor.precursor_index")
            .DisableDictionary("selected_ion.source_index")
            .DisableDictionary("selected_ion.precursor_index")
            .Encoding("spectrum.index", ParquetSharp.Encoding.DeltaBinaryPacked)
            .Encoding("scan.source_index", ParquetSharp.Encoding.DeltaBinaryPacked)
            .Encoding("scan.scan_index", ParquetSharp.Encoding.DeltaBinaryPacked)
            .Encoding("precursor.source_index", ParquetSharp.Encoding.DeltaBinaryPacked)
            .Encoding("precursor.precursor_index", ParquetSharp.Encoding.DeltaBinaryPacked)
            .Encoding("selected_ion.source_index", ParquetSharp.Encoding.DeltaBinaryPacked)
            .Encoding("selected_ion.precursor_index", ParquetSharp.Encoding.DeltaBinaryPacked)
            .EnableStatistics()
            .EnableWritePageIndex();
        var arrowProps = new ArrowWriterPropertiesBuilder().StoreSchema();
        CurrentEntry = entry;

        var meta = PrepareRunLevelMetadataDictionary();
        meta["spectrum_count"] = SpectrumMetadata.Length.ToString();
        meta["spectrum_data_point_count"] = SpectrumData.NumberOfPoints.ToString();

        CurrentWriter = new FileWriter(
            managedStream,
            SpectrumMetadata.ArrowSchema(meta),
            writerProps.Build(),
            arrowProps.Build()
        );
        CurrentWriter.NewBufferedRowGroup();
        var batch = SpectrumMetadata.Build();
        CurrentWriter.WriteBufferedRecordBatch(batch);
        CloseCurrentWriter();
    }


    /// <summary>Writes chromatogram data to the archive.</summary>
    public void WriteChromatogramData()
    {
        if (State >= WriterState.ChromatogramData)
            return;
        State = WriterState.ChromatogramData;
        if (ChromatogramData.BufferedRows == 0) return;
        StartChromatogramData();
        var batch = ChromatogramData.GetRecordBatch();
        Logger?.LogDebug($"Flushing {batch.Length} rows to chromatogram_data");
        CurrentWriter?.WriteBufferedRecordBatch(batch);
        CloseCurrentWriter();
    }

    public void WriteWavelengthData()
    {
        if (State >= WriterState.WavelengthData || WavelengthSpectrumData == null)
            return;
        State = WriterState.WavelengthData;
        if (WavelengthSpectrumData.BufferedRows == 0) return;
        StartWavelengthSpectrumData();
        var batch = WavelengthSpectrumData.GetRecordBatch();
        Logger?.LogDebug($"Flushing {batch.Length} rows to wavelength_spectra_data");
        CurrentWriter?.WriteBufferedRecordBatch(batch);
        CloseCurrentWriter();
    }

    public void WriteWavelengthMetadata()
    {
        if (State >= WriterState.WavelengthMetadata || WavelengthSpectrumMetadata == null)
            return;
        State = WriterState.WavelengthMetadata;
        var entry = FileIndexEntry.FromEntityAndData(EntityType.WavelengthSpectrum, DataKind.Metadata);

        Logger?.LogInformation($"Writing wavelength spectrum metadata, {WavelengthSpectrumMetadata.Length} rows to write");
        var stream = Storage.OpenStream(entry);
        var managedStream = new ParquetSharp.IO.ManagedOutputStream(stream);

        var writerProps = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(ParquetSharp.Compression.Zstd)
            .CompressionLevel(DataWriterConfig.CompressionLevel)
            .EnableDictionary()
            .EnableStatistics()
            .EnableWritePageIndex()
            .Encoding("spectrum.index", ParquetSharp.Encoding.DeltaBinaryPacked)
            .Encoding("scan.source_index", ParquetSharp.Encoding.DeltaBinaryPacked)
            .Encoding("scan.scan_index", ParquetSharp.Encoding.DeltaBinaryPacked);
        var arrowProps = new ArrowWriterPropertiesBuilder().StoreSchema();

        var meta = PrepareRunLevelMetadataDictionary();
        meta["wavelength_spectrum_count"] = WavelengthSpectrumMetadata.Length.ToString();
        meta["wavelength_spectrum_data_point_count"] = (WavelengthSpectrumData?.NumberOfPoints ?? 0).ToString();

        CurrentEntry = entry;
        CurrentWriter = new FileWriter(managedStream, WavelengthSpectrumMetadata.ArrowSchema(meta), writerProps.Build(), arrowProps.Build());
        CurrentWriter.NewBufferedRowGroup();
        var batch = WavelengthSpectrumMetadata.Build();
        CurrentWriter.WriteBufferedRecordBatch(batch);
        CloseCurrentWriter();
    }

    protected virtual Dictionary<string, string> PrepareRunLevelMetadataDictionary()
    {
        var meta = new Dictionary<string, string>();
        meta["file_description"] = JsonSerializer.Serialize(FileDescription);
        meta["instrument_configuration_list"] = JsonSerializer.Serialize(InstrumentConfigurations);
        meta["data_processing_method_list"] = JsonSerializer.Serialize(DataProcessingMethods);
        meta["software_list"] = JsonSerializer.Serialize(Softwares);
        meta["sample_list"] = JsonSerializer.Serialize(Samples);
        meta["run"] = JsonSerializer.Serialize(Run);
        return meta;
    }

    /// <summary>Writes chromatogram metadata to the archive.</summary>
    public void WriteChromatogramMetadata()
    {
        if (State >= WriterState.ChromatogramMetadata)
            return;
        State = WriterState.ChromatogramMetadata;
        var entry = FileIndexEntry.FromEntityAndData(EntityType.Chromatogram, DataKind.Metadata);
        var stream = Storage.OpenStream(entry);

        var managedStream = new ParquetSharp.IO.ManagedOutputStream(stream);

        var writerProps = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(ParquetSharp.Compression.Zstd)
            .CompressionLevel(DataWriterConfig.CompressionLevel)
            .EnableDictionary()
            .EnableStatistics()
            .EnableWritePageIndex();
        var arrowProps = new ArrowWriterPropertiesBuilder().StoreSchema();

        var meta = PrepareRunLevelMetadataDictionary();
        meta["chromatogram_count"] = ChromatogramMetadata.Length.ToString();
        meta["chromatogram_data_point_count"] = ChromatogramData.NumberOfPoints.ToString();

        CurrentEntry = entry;
        CurrentWriter = new FileWriter(managedStream, ChromatogramMetadata.ArrowSchema(meta), writerProps.Build(), arrowProps.Build());
        CurrentWriter.NewBufferedRowGroup();
        var batch = ChromatogramMetadata.Build();
        CurrentWriter.WriteBufferedRecordBatch(batch);
        CloseCurrentWriter();
    }

    public void FlushStandardContent()
    {
        if (standardContentFlushed) return;
        FlushSpectrumData();
        if (State == WriterState.SpectrumData)
            CloseCurrentWriter();
        if (SpectrumPeakData != null)
        {
            if (PeakWriter != null)
            {
                StorePeakFileFromTemporaryFile();
                if (PeakPath != null)
                {
                    File.Delete(PeakPath);
                }
            }
            else
            {
                StartSpectrumPeakData();
                FlushSpectrumPeakData();
                CloseCurrentWriter();
            }
        }
        WriteSpectrumMetadata();
        WriteChromatogramData();
        WriteChromatogramMetadata();
        WriteWavelengthData();
        WriteWavelengthMetadata();
        standardContentFlushed = true;
    }

    public Stream StartEntry(FileIndexEntry entry)
    {
        CloseCurrentWriter();
        var stream = Storage.OpenStream(entry);
        CurrentEntry = entry;
        return stream;
    }

    public ParquetSharp.IO.ManagedOutputStream StartParquetEntry(FileIndexEntry entry)
    {
        return new ParquetSharp.IO.ManagedOutputStream(StartEntry(entry));
    }

    public void WriteFileMetadataToIndex()
    {
        Storage.FileIndex().Metadata.Add(
            MZPeakConstants.VERSION_KEY,
            MZPeakConstants.MZPEAK_VERSION
        );
        List<ControlledVocabularyEntry> cvList = [ControlledVocabularyEntry.PSIMS, ControlledVocabularyEntry.Unit];
        Storage.FileIndex().Metadata.Add(
            MZPeakConstants.CV_LIST_KEY,
            JsonSerializer.SerializeToNode(cvList)
        );
        Storage.FileIndex().Metadata.Add(
            MZPeakConstants.FILE_DESCRIPTION_KEY,
            JsonSerializer.SerializeToNode(FileDescription)
        );
        Storage.FileIndex().Metadata.Add(
            MZPeakConstants.INSTRUMENT_CONFIGURATION_LIST_KEY,
            JsonSerializer.SerializeToNode(InstrumentConfigurations)
        );
        Storage.FileIndex().Metadata.Add(
            MZPeakConstants.DATA_PROCESSING_METHOD_LIST_KEY,
            JsonSerializer.SerializeToNode(DataProcessingMethods)
        );
        Storage.FileIndex().Metadata.Add(
            MZPeakConstants.SOFTWARE_LIST_KEY,
            JsonSerializer.SerializeToNode(Softwares)
        );

        Storage.FileIndex().Metadata.Add(
            MZPeakConstants.SAMPLE_LIST_KEY,
            JsonSerializer.SerializeToNode(Samples)
        );
        Storage.FileIndex().Metadata.Add(
            MZPeakConstants.SCAN_SETTINGS_LIST_KEY,
            JsonSerializer.SerializeToNode(ScanSettings)
        );
        Storage.FileIndex().Metadata.Add(
            MZPeakConstants.MS_RUN_KEY,
            JsonSerializer.SerializeToNode(Run)
        );
    }

    /// <summary>Closes the writer and finalizes the archive.</summary>
    public void Close()
    {
        WriteFileMetadataToIndex();
        FlushStandardContent();
        Storage.Dispose();
    }

    /// <summary>Disposes resources and closes the writer.</summary>
    public void Dispose()
    {
        Close();
    }
}