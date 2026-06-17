using Apache.Arrow;
using Apache.Arrow.Types;
using MZPeak.ControlledVocabulary;
using MZPeak.Compute;
using MZPeak.Reader.Visitors;
using Microsoft.Extensions.Logging;
using System.Numerics;


namespace MZPeak.Metadata;


using SpacingModels = Dictionary<ulong, SpacingInterpolationModel<double>>;
using NativeIdIndex = Dictionary<ulong, string?>;


/// <summary>
/// A base class for generic metadata table reading
/// </summary>
public abstract class MetadataReaderBase<T>
{
    internal static ILogger? Logger = null;

    protected MzPeakMetadata mzPeakMetadata;

    protected Dictionary<ulong, ulong?> DataPointCounts { get; set; }
    protected Dictionary<ulong, ulong?> PeakCounts { get; set; }

    /// <summary>Gets the file description metadata.</summary>
    public FileDescription FileDescription => mzPeakMetadata.FileDescription;
    /// <summary>Gets the list of instrument configurations.</summary>
    public List<InstrumentConfiguration> InstrumentConfigurations => mzPeakMetadata.InstrumentConfigurations;
    /// <summary>Gets the list of software used.</summary>
    public List<Software> Softwares => mzPeakMetadata.Softwares;
    /// <summary>Gets the list of samples.</summary>
    public List<Sample> Samples => mzPeakMetadata.Samples;
    /// <summary>Gets the list of data processing methods.</summary>
    public List<DataProcessingMethod> DataProcessingMethods => mzPeakMetadata.DataProcessingMethods;
    /// <summary>Gets the run-level metadata.</summary>
    public MSRun Run => mzPeakMetadata.Run;

    protected MetadataReaderBase(MzPeakMetadata mzPeakMetadata)
    {
        this.mzPeakMetadata = mzPeakMetadata;
        DataPointCounts = new();
        PeakCounts = new();
    }

    protected void GetNativeIdsFrom(StructArray? table, ref NativeIdIndex nativeIds)
    {
        if (table == null)
        {
            return;
        }

        var dtype = (StructType)table.Data.DataType;
        var fieldIdx = dtype.GetFieldIndex("id");
        if (fieldIdx < 0)
        {
            return;
        }

        var indexArr = (UInt64Array)table.Fields[0];
        var modelArr = (LargeStringArray)table.Fields[fieldIdx];
        nativeIds.EnsureCapacity(indexArr.Length);
        for (var i = 0; i < indexArr.Length; i++)
        {
            var index = indexArr.GetValue(i);
            if (index == null)
            {
                continue;
            }
            var nativeId = modelArr.GetString(i);
            nativeIds.Add((ulong)index, nativeId);
        }
    }

    /// <summary>Gets the number of entries in the metadata table.</summary>
    public abstract long Length { get; }

    /// <summary>Loads all metadata entries into a list.</summary>
    public abstract List<T> BulkLoad();

    /// <summary>Gets a single metadata entry by index.</summary>
    /// <param name="index">The entry index.</param>
    public abstract T Get(ulong index);

    protected void loadEntryCounts<U>(PrimitiveArray<U> countArray, UInt64Array indexArr, Dictionary<ulong, ulong?> accumulator) where U : struct, INumber<U>
    {
        foreach (var (i, c) in indexArr.AsEnumerable().Zip(countArray.AsEnumerable()))
        {
            if (i == null) continue;
            if (c == null)
            {
                accumulator[(ulong)i] = null;
            }
            else
            {
                var count = (U)c;
                accumulator[(ulong)i] = ulong.CreateSaturating(count);
            }
        }
    }

    protected bool loadCountFrom(ChunkedArray mainTable, SpectrumProperties column, Dictionary<ulong, ulong?> accumulator)
    {
        var query = ColumnParam.Inflect(column.CURIE(), column.Name());
        int? countCol = null;
        for (var i = 0; i < mainTable.ArrayCount; i++)
        {
            var chunk = (StructArray)mainTable.Array(i);
            var idxField = (UInt64Array)chunk.Fields[0];
            if (countCol == null)
            {
                countCol = ((StructType)chunk.Data.DataType).GetFieldIndex(query);
                if (countCol < 0)
                {
                    countCol = null;
                }
            }
            if (countCol == null) {
                return false;
            };
            var countField = chunk.Fields[(int)countCol];
            switch (countField.Data.DataType.TypeId)
            {
                case ArrowTypeId.UInt32:
                    {
                        loadEntryCounts((UInt32Array)countField, idxField, accumulator);
                        break;
                    }
                case ArrowTypeId.UInt64:
                    {
                        loadEntryCounts((UInt64Array)countField, idxField, accumulator);
                        break;
                    }
                case ArrowTypeId.Int32:
                    {
                        loadEntryCounts((Int32Array)countField, idxField, accumulator);
                        break;
                    }
                case ArrowTypeId.Int64:
                    {
                        loadEntryCounts((Int64Array)countField, idxField, accumulator);
                        break;
                    }
                default:
                    {
                        throw new InvalidOperationException($"Unsupported {query} type {countField.Data.DataType}");
                    }
            }
        }
        return true;
    }

    virtual protected ChunkedArray? MainTable => null;

    /// <summary>
    /// Get the number of profile data points recorded being stored for the requested index
    /// </summary>
    /// <param name="index">The entry index to look up data point counts for</param>
    /// <returns>The number of profile data points, or <c>null</c> if no points were stored</returns>
    public ulong? NumberOfDataPointsFor(ulong index)
    {
        if (DataPointCounts.Count == 0)
        {
            if (MainTable == null) return null;
            loadCountFrom(MainTable, SpectrumProperties.NumberOfDataPoints, DataPointCounts);
        }
        ulong? outVal;
        DataPointCounts.TryGetValue(index, out outVal);
        return outVal;
    }

    /// <summary>
    /// Get the number of discrete peaks recorded being stored for the requested index
    /// </summary>
    /// <param name="index">The entry index to look up peak counts for</param>
    /// <returns>The number of discrete peaks, or <c>null</c> if no peaks were stored</returns>
    public ulong? NumberOfPeaks(ulong index)
    {
        if (PeakCounts.Count == 0)
        {
            if (MainTable == null) return null;
            loadCountFrom(MainTable, SpectrumProperties.NumberOfPeaks, PeakCounts);
        }
        ulong? outVal;
        PeakCounts.TryGetValue(index, out outVal);
        return outVal;
    }

    /// <summary>Gets native IDs keyed by entry index.</summary>
    public NativeIdIndex GetNativeIds()
    {
        var tab = new NativeIdIndex();
        if (MainTable == null)
        {
            return tab;
        }
        for (var i = 0; i < MainTable.ArrayCount; i++)
        {
            var chunk = MainTable.Array(i);
            GetNativeIdsFrom((StructArray)chunk, ref tab);
        }
        return tab;
    }
}


/// <summary>
/// Reader for spectrum metadata from Parquet files.
/// </summary>
public class SpectrumMetadataReader : MetadataReaderBase<SpectrumDescription>
{
    /// <summary>The underlying Parquet file reader.</summary>
    public ParquetSharp.Arrow.FileReader FileReader;

    ChunkedArray? spectrumMetadata = null;
    List<ColumnParam> spectrumMetadataColumns;
    ChunkedArray? scanMetadata = null;
    List<ColumnParam> scanMetadataColumns;
    ChunkedArray? precursorMetadata = null;
    List<ColumnParam> precursorMetadataColumns;
    ChunkedArray? selectedIonMetadata = null;
    List<ColumnParam> selectedIonMetadataColumns;

    /// <summary>Gets the number of spectra.</summary>
    public override long Length
    {
        get
        {
            if (SpectrumMetadata == null)
            {
                InitializeTables().Wait();
            }
            return SpectrumMetadata == null ? 0 : SpectrumMetadata.Length;
        }
    }

    /// <summary>Creates a spectrum metadata reader.</summary>
    /// <param name="fileReader">The Parquet file reader.</param>
    /// <param name="initializeFacets">Whether to initialize tables immediately.</param>
    public SpectrumMetadataReader(ParquetSharp.Arrow.FileReader fileReader, bool initializeFacets = true) : base(MzPeakMetadata.FromParquet(fileReader.ParquetReader))
    {
        FileReader = fileReader;

        spectrumMetadataColumns = new();
        scanMetadataColumns = new();
        precursorMetadataColumns = new();
        selectedIonMetadataColumns = new();

        if (initializeFacets)
        {
            InitializeTables().Wait();
        }
    }

    void loadSpectrumInterpolationModels(ListArray modelArr, UInt64Array indexArr, ref SpacingModels accumulator)
    {
        for (var i = 0; i < indexArr.Length; i++)
        {
            var index = indexArr.GetValue(i);
            if (index == null)
            {
                continue;
            }
            if (modelArr.IsNull(i))
            {
                continue;
            }
            var modelAt = modelArr.GetSlicedValues(i);
            var coefs = SpacingInterpolationModel<double>.FromArray(modelAt);
            if (coefs != null)
            {
                accumulator[(ulong)index] = coefs;
            }
        }
    }

    void loadSpectrumInterpolationModels(LargeListArray modelArr, UInt64Array indexArr, ref SpacingModels accumulator)
    {
        for (var i = 0; i < indexArr.Length; i++)
        {
            var index = indexArr.GetValue(i);
            if (index == null)
            {
                continue;
            }
            if (modelArr.IsNull(i) || modelArr.GetValueLength(i) == 0)
            {
                continue;
            }
            var modelAt = modelArr.GetSlicedValues(i);
            var coefs = SpacingInterpolationModel<double>.FromArray(modelAt);
            if (coefs != null)
            {
                accumulator[(ulong)index] = coefs;
            }
        }
    }

    /// <summary>Gets spacing interpolation models keyed by spectrum index.</summary>
    public SpacingModels GetSpacingModelIndex()
    {
        SpacingModels acc = new();
        if (SpectrumMetadata == null)
        {
            return acc;
        }

        if (SpectrumMetadata.ArrayCount == 0)
        {
            return acc;
        }

        var dtype = (StructType)SpectrumMetadata.Array(0).Data.DataType;
        var fieldIdx = dtype.GetFieldIndex("mz_delta_model");

        if (fieldIdx < 0)
        {
            return new();
        }

        for (var i = 0; i < SpectrumMetadata.ArrayCount; i++)
        {
            var chunk = (StructArray)SpectrumMetadata.Array(i);
            var indexArr = (UInt64Array)chunk.Fields[0];
            var modelArr = chunk.Fields[fieldIdx];
            if (modelArr.Data.DataType.TypeId == ArrowTypeId.List)
            {
                loadSpectrumInterpolationModels((ListArray)modelArr, indexArr, ref acc);
            }
            else if (modelArr.Data.DataType.TypeId == ArrowTypeId.LargeList)
            {
                loadSpectrumInterpolationModels((LargeListArray)modelArr, indexArr, ref acc);
            }
            else
            {
                throw new NotImplementedException($"{modelArr.Data.DataType.Name} not supported");
            }
        }
        return acc;
    }

    /// <summary>Gets or sets the spectrum metadata table.</summary>
    public ChunkedArray? SpectrumMetadata
    {
        get
        {
            if (spectrumMetadata == null)
            {
                InitializeTables().Wait();
            }
            return spectrumMetadata;
        }
        set => spectrumMetadata = value;
    }

    /// <summary>Gets or sets the scan metadata table.</summary>
    public ChunkedArray? ScanMetadata
    {
        get
        {
            if (scanMetadata == null)
            {
                InitializeTables().Wait();
            }
            return scanMetadata;
        }
        set => scanMetadata = value;
    }

    /// <summary>Gets or sets the precursor metadata table.</summary>
    public ChunkedArray? PrecursorMetadata
    {
        get
        {
            if (precursorMetadata == null)
            {
                InitializeTables().Wait();
            }
            return precursorMetadata;
        }
        set => precursorMetadata = value;
    }

    /// <summary>Gets or sets the selected ion metadata table.</summary>
    public ChunkedArray? SelectedIonMetadata
    {
        get
        {
            if (selectedIonMetadata == null)
            {
                InitializeTables().Wait();
            }
            return selectedIonMetadata;
        }
        set => selectedIonMetadata = value;
    }

    /// <summary>Loads all spectrum descriptions.</summary>
    public override List<SpectrumDescription> BulkLoad()
    {
        if (SpectrumMetadata == null) return new();
        var spectra = new List<SpectrumInfo>();
        for (var i = 0; i < SpectrumMetadata.ArrayCount; i++)
        {
            var vis = new SpectrumVisitor();
            vis.Visit(SpectrumMetadata.Array(i));
            spectra.AddRange(vis.Values);
        }
        var descrs = spectra.Select(s => new SpectrumDescription(s, new(), new(), new())).ToList();
        if (ScanMetadata != null)
        {
            for (var i = 0; i < ScanMetadata.ArrayCount; i++)
            {
                var vis = new ScanVisitor();
                vis.Visit(ScanMetadata.Array(i));
                foreach (var rec in vis.Values)
                {
                    descrs[(int)rec.SourceIndex].Scans.Add(rec);
                }
            }
        }
        if (PrecursorMetadata != null)
        {
            for (var i = 0; i < PrecursorMetadata.ArrayCount; i++)
            {
                var vis = new PrecursorVisitor();
                vis.Visit(PrecursorMetadata.Array(i));
                foreach (var rec in vis.Values)
                {
                    descrs[(int)rec.SourceIndex].Precursors.Add(rec);
                }
            }
        }
        if (SelectedIonMetadata != null)
        {
            for (var i = 0; i < SelectedIonMetadata.ArrayCount; i++)
            {
                var vis = new SelectedIonVisitor();
                vis.Visit(SelectedIonMetadata.Array(i));
                foreach (var rec in vis.Values)
                {
                    descrs[(int)rec.SourceIndex].SelectedIons.Add(rec);
                }
            }
        }
        return descrs;
    }

    protected override ChunkedArray? MainTable => SpectrumMetadata;

    SpectrumDescription GetSpectrum(ulong index)
    {
        if (SpectrumMetadata == null) throw new IndexOutOfRangeException($"{index} out of spectrum index range");
        UInt64Array idxArr;
        SpectrumInfo? rec = null;
        for (var i = 0; i < SpectrumMetadata.ArrayCount; i++)
        {
            var chunk = (StructArray)SpectrumMetadata.Array(i);
            idxArr = (UInt64Array)chunk.Fields[0];
            var first = Compute.Compute.FirstNotNull(idxArr);
            var last = Compute.Compute.LastNotNull(idxArr);
            if (last == null || first == null || first.Value.Item1 > index || last.Value.Item1 < index) continue;
            var mask = Compute.Compute.Equal(idxArr, index);
            var recs = Compute.Compute.Filter(chunk, mask);
            var visitor = new SpectrumVisitor();
            visitor.Visit(recs);
            rec = visitor.Values[0];
            break;
        }
        if (rec == null) throw new IndexOutOfRangeException($"{index} out of spectrum index range");

        var pn = rec.Parameters.Find(p => p.AccessionCURIE == "MS:1000127");
        List<ScanInfo> scanRecs = new();
        if (ScanMetadata != null)
        {
            for (var i = 0; i < ScanMetadata.ArrayCount; i++)
            {
                var chunk = (StructArray)ScanMetadata.Array(i);
                idxArr = (UInt64Array)chunk.Fields[0];
                var first = Compute.Compute.FirstNotNull(idxArr);
                var last = Compute.Compute.LastNotNull(idxArr);
                if (last == null || first == null || first.Value.Item1 > index || last.Value.Item1 < index) continue;
                var mask = Compute.Compute.Equal(idxArr, index);
                var recs = Compute.Compute.Filter(chunk, mask);
                var visitor = new ScanVisitor();
                visitor.Visit(recs);
                scanRecs.AddRange(visitor.Values);
                break;
            }
        }
        List<PrecursorInfo> precursorInfos = new();
        if (PrecursorMetadata != null)
        {
            for (var i = 0; i < PrecursorMetadata.ArrayCount; i++)
            {
                var chunk = (StructArray)PrecursorMetadata.Array(i);
                idxArr = (UInt64Array)chunk.Fields[0];
                var first = Compute.Compute.FirstNotNull(idxArr);
                var last = Compute.Compute.LastNotNull(idxArr);
                if (last == null || first == null || first.Value.Item1 > index || last.Value.Item1 < index) continue;
                var mask = Compute.Compute.Equal(idxArr, index);
                var recs = Compute.Compute.Filter(chunk, mask);
                var visitor = new PrecursorVisitor();
                visitor.Visit(recs);
                precursorInfos.AddRange(visitor.Values);
                break;
            }
        }
        List<SelectedIonInfo> selectedIons = new();
        if (SelectedIonMetadata != null)
        {
            for (var i = 0; i < SelectedIonMetadata.ArrayCount; i++)
            {
                var chunk = (StructArray)SelectedIonMetadata.Array(i);
                idxArr = (UInt64Array)chunk.Fields[0];
                var first = Compute.Compute.FirstNotNull(idxArr);
                var last = Compute.Compute.LastNotNull(idxArr);
                if (last == null || first == null || first.Value.Item1 > index || last.Value.Item1 < index) continue;
                var mask = Compute.Compute.Equal(idxArr, index);
                var recs = Compute.Compute.Filter(chunk, mask);
                var visitor = new SelectedIonVisitor();
                visitor.Visit(recs);
                selectedIons.AddRange(visitor.Values);
                break;
            }
        }

        return new SpectrumDescription(rec, scanRecs, precursorInfos, selectedIons);
    }

    public async Task InitializeTables()
    {
        var reader = FileReader.GetRecordBatchReader();
        int ctr = 0;
        List<IArrowArray> spectra = [];
        List<IArrowArray> scans = [];
        List<IArrowArray> precursors = [];
        List<IArrowArray> selectedIons = [];
        while (true)
        {
            RecordBatch batch = await reader.ReadNextRecordBatchAsync();
            if (batch == null)
            {
                Logger?.LogDebug($"Read {ctr} batches from {this}");
                break;
            }
            Logger?.LogDebug("batch {ctr}, {batch.Length} items", batch, ctr);
            ctr++;
            var arr = batch.Column("spectrum");
            if (arr != null) spectra.Add(arr);
            arr = batch.Column("scan");
            if (arr != null) scans.Add(arr);
            try
            {
                arr = batch.Column("precursor");
                if (arr != null)
                {
                    if (arr != null) precursors.Add(arr);
                }
            }
            catch (ArgumentOutOfRangeException) { }


            try
            {
                arr = batch.Column("selected_ion");
                if (arr != null)
                {
                    if (arr != null) selectedIons.Add(arr);
                }
            }
            catch (ArgumentOutOfRangeException) { }
        }

        if (spectra.Count > 0)
        {
            SpectrumMetadata = new ChunkedArray(spectra);
        }

        if (scans.Count > 0)
        {
            ScanMetadata = new ChunkedArray(scans);
        }
        if (precursors.Count > 0)
        {
            PrecursorMetadata = new ChunkedArray(precursors);
        }
        if (selectedIons.Count > 0)
        {
            SelectedIonMetadata = new ChunkedArray(selectedIons);
        }
        if (spectrumMetadata != null)
        {
            NumberOfDataPointsFor(0);
            NumberOfPeaks(0);
        }
    }

    /// <summary>Gets the spectrum description for the specified index.</summary>
    /// <param name="index">The spectrum index.</param>
    public override SpectrumDescription Get(ulong index)
    {
        return GetSpectrum(index);
    }
}

/// <summary>
/// Reader for chromatogram metadata from Parquet files.
/// </summary>
public class ChromatogramMetadataReader : MetadataReaderBase<ChromatogramDescription>
{
    /// <summary>The underlying Parquet file reader.</summary>
    public ParquetSharp.Arrow.FileReader FileReader;

    ChunkedArray? chromatogramMetadata = null;
    List<ColumnParam> chromatogramMetadataColumns;
    ChunkedArray? precursorMetadata = null;
    List<ColumnParam> precursorMetadataColumns;
    ChunkedArray? selectedIonMetadata = null;
    List<ColumnParam> selectedIonMetadataColumns;

    /// <summary>Gets the number of chromatograms.</summary>
    public override long Length
    {
        get
        {
            if (ChromatogramMetadata == null)
            {
                InitializeTables().Wait();
            }
            return ChromatogramMetadata == null ? 0 : ChromatogramMetadata.Length;
        }
    }

    protected override ChunkedArray? MainTable => ChromatogramMetadata;

    /// <summary>Creates a chromatogram metadata reader.</summary>
    /// <param name="fileReader">The Parquet file reader.</param>
    /// <param name="initializeFacets">Whether to initialize tables immediately.</param>
    public ChromatogramMetadataReader(ParquetSharp.Arrow.FileReader fileReader, bool initializeFacets = true) : base(MzPeakMetadata.FromParquet(fileReader.ParquetReader))
    {
        chromatogramMetadataColumns = new();
        precursorMetadataColumns = new();
        selectedIonMetadataColumns = new();
        FileReader = fileReader;
        if (initializeFacets)
        {
            InitializeTables().Wait();
        }
    }

    /// <summary>Gets or sets the chromatogram metadata table.</summary>
    public ChunkedArray? ChromatogramMetadata
    {
        get
        {
            if (chromatogramMetadata == null)
            {
                InitializeTables().Wait();
            }
            return chromatogramMetadata;
        }
        set => chromatogramMetadata = value;
    }

    /// <summary>Gets or sets the precursor metadata table.</summary>
    public ChunkedArray? PrecursorMetadata
    {
        get
        {
            if (precursorMetadata == null)
            {
                InitializeTables().Wait();
            }
            return precursorMetadata;
        }
        set => precursorMetadata = value;
    }

    /// <summary>Gets or sets the selected ion metadata table.</summary>
    public ChunkedArray? SelectedIonMetadata
    {
        get
        {
            if (selectedIonMetadata == null)
            {
                InitializeTables().Wait();
            }
            return selectedIonMetadata;
        }
        set => selectedIonMetadata = value;
    }

    /// <summary>Loads all chromatogram descriptions.</summary>
    public override List<ChromatogramDescription> BulkLoad()
    {
        if (ChromatogramMetadata == null) return new();
        var recs = new List<ChromatogramInfo>();
        for (var i = 0; i < ChromatogramMetadata.ArrayCount; i++)
        {
            var vis = new ChromatogramVisitor();
            vis.Visit(ChromatogramMetadata.Array(i));
            recs.AddRange(vis.Values);
        }
        var descrs = recs.Select(s => new ChromatogramDescription(s, new(), new())).ToList();
        if (PrecursorMetadata != null)
        {
            for (var i = 0; i < PrecursorMetadata.ArrayCount; i++)
            {
                var vis = new PrecursorVisitor();
                vis.Visit(PrecursorMetadata.Array(i));
                foreach (var rec in vis.Values)
                {
                    descrs[(int)rec.SourceIndex].Precursors.Add(rec);
                }
            }
        }
        if (SelectedIonMetadata != null)
        {
            for (var i = 0; i < SelectedIonMetadata.ArrayCount; i++)
            {
                var vis = new SelectedIonVisitor();
                vis.Visit(SelectedIonMetadata.Array(i));
                foreach (var rec in vis.Values)
                {
                    descrs[(int)rec.SourceIndex].SelectedIons.Add(rec);
                }
            }
        }
        return descrs;
    }

    ChromatogramDescription GetChromatogram(ulong index)
    {
        if (ChromatogramMetadata == null) throw new IndexOutOfRangeException($"{index} out of chromatogram index range");
        UInt64Array idxArr;
        ChromatogramInfo? rec = null;
        for (var i = 0; i < ChromatogramMetadata.ArrayCount; i++)
        {
            var chunk = (StructArray)ChromatogramMetadata.Array(i);
            idxArr = (UInt64Array)chunk.Fields[0];
            var first = Compute.Compute.FirstNotNull(idxArr);
            var last = Compute.Compute.LastNotNull(idxArr);
            if (last == null || first == null || first.Value.Item1 > index || last.Value.Item1 < index) continue;
            var mask = Compute.Compute.Equal(idxArr, index);
            var recs = Compute.Compute.Filter(chunk, mask);
            var visitor = new ChromatogramVisitor();
            visitor.Visit(recs);
            rec = visitor.Values[0];
            break;
        }
        if (rec == null) throw new IndexOutOfRangeException($"{index} out of chromatogram index range");

        List<PrecursorInfo> precursorInfos = new();
        if (PrecursorMetadata != null)
        {
            for (var i = 0; i < PrecursorMetadata.ArrayCount; i++)
            {
                var chunk = (StructArray)PrecursorMetadata.Array(i);
                idxArr = (UInt64Array)chunk.Fields[0];
                var first = Compute.Compute.FirstNotNull(idxArr);
                var last = Compute.Compute.LastNotNull(idxArr);
                if (last == null || first == null || first.Value.Item1 > index || last.Value.Item1 < index) continue;
                var mask = Compute.Compute.Equal(idxArr, index);
                var recs = Compute.Compute.Filter(chunk, mask);
                var visitor = new PrecursorVisitor();
                visitor.Visit(recs);
                precursorInfos.AddRange(visitor.Values);
                break;
            }
        }
        List<SelectedIonInfo> selectedIons = new();
        if (SelectedIonMetadata != null)
        {
            for (var i = 0; i < SelectedIonMetadata.ArrayCount; i++)
            {
                var chunk = (StructArray)SelectedIonMetadata.Array(i);
                idxArr = (UInt64Array)chunk.Fields[0];
                var first = Compute.Compute.FirstNotNull(idxArr);
                var last = Compute.Compute.LastNotNull(idxArr);
                if (last == null || first == null || first.Value.Item1 > index || last.Value.Item1 < index) continue;
                var mask = Compute.Compute.Equal(idxArr, index);
                var recs = Compute.Compute.Filter(chunk, mask);
                var visitor = new SelectedIonVisitor();
                visitor.Visit(recs);
                selectedIons.AddRange(visitor.Values);
                break;
            }
        }
        return new ChromatogramDescription(rec, precursorInfos, selectedIons);
    }

    /// <summary>Initializes metadata tables by reading from the Parquet file.</summary>
    public async Task InitializeTables()
    {
        var reader = FileReader.GetRecordBatchReader();
        var ctr = 0;
        List<IArrowArray> chromatograms = [];
        List<IArrowArray> precursors = [];
        List<IArrowArray> selectedIons = [];
        while (true)
        {
            RecordBatch batch = await reader.ReadNextRecordBatchAsync();
            if (batch == null)
            {
                Logger?.LogDebug($"Read {ctr} batches from {this}");
                break;
            }
            Logger?.LogDebug("batch {ctr}, {batch.Length} items", batch, ctr);
            ctr++;
            var arr = batch.Column("chromatogram");
            if (arr != null) chromatograms.Add(arr);
            arr = batch.Column("precursor");
            if (arr != null) precursors.Add(arr);
            arr = batch.Column("selected_ion");
            if (arr != null) selectedIons.Add(arr);
        }

        if (chromatograms.Count > 0)
        {
            ChromatogramMetadata = new ChunkedArray(chromatograms);
        }
        if (precursors.Count > 0)
        {
            PrecursorMetadata = new ChunkedArray(precursors);
        }
        if (selectedIons.Count > 0)
        {
            SelectedIonMetadata = new ChunkedArray(selectedIons);
        }

        if (chromatogramMetadata != null)
        {
            NumberOfDataPointsFor(0);
            NumberOfPeaks(0);
        }
    }

    /// <summary>Gets the chromatogram description for the specified index.</summary>
    /// <param name="index">The chromatogram index.</param>
    public override ChromatogramDescription Get(ulong index)
    {
        return GetChromatogram(index);
    }
}
