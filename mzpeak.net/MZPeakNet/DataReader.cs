namespace MZPeak.Reader;

using System.Collections;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Apache.Arrow;
using Apache.Arrow.Ipc;
using ParquetSharp.Arrow;

using MZPeak.Metadata;
using Apache.Arrow.Types;
using MZPeak.Compute;
using Microsoft.Extensions.Logging;
using MZPeak.ControlledVocabulary;
using System.Threading;

/// <summary>
/// Represents a range of values associated with a key.
/// </summary>
/// <typeparam name="T">The comparable type for range bounds.</typeparam>
public record struct GroupTagBounds<T> where T : IComparable<T>
{
    /// <summary>The key identifying this group.</summary>
    public ulong Key;
    /// <summary>The start of the range (inclusive).</summary>
    public T Start;
    /// <summary>The end of the range (inclusive).</summary>
    public T End;

    /// <summary>Creates a new group tag bounds.</summary>
    /// <param name="key">The group key.</param>
    /// <param name="start">The start of the range.</param>
    /// <param name="end">The end of the range.</param>
    public GroupTagBounds(ulong key, T start, T end)
    {
        Key = key;
        Start = start;
        End = end;
    }


    /// <summary>Checks if a value falls within the range.</summary>
    /// <param name="value">The value to check.</param>
    public bool Contains(T value)
    {
        return (Start.CompareTo(value) <= 0) && (value.CompareTo(End) <= 0);
    }
}

class KeyComparator<T> : IComparer<GroupTagBounds<T>> where T : IComparable<T>
{
    public int Compare(GroupTagBounds<T> x, GroupTagBounds<T> y)
    {
        return x.Key.CompareTo(y.Key);
    }
}

/// <summary>
/// Index mapping keys to row ranges within Parquet row groups.
/// </summary>
public class RangeIndex : IEnumerable<GroupTagBounds<ulong>>
{
    /// <summary>The list of range bounds.</summary>
    public List<GroupTagBounds<ulong>> Ranges;

    /// <summary>Gets the number of ranges.</summary>
    public long Length { get => Ranges.Count; }

    /// <summary>Creates a range index from the specified bounds.</summary>
    /// <param name="bounds">The list of group tag bounds.</param>
    public RangeIndex(List<GroupTagBounds<ulong>> bounds)
    {
        Ranges = bounds;
    }

    /// <summary>Gets an enumerator over the ranges.</summary>
    public IEnumerator<GroupTagBounds<ulong>> GetEnumerator()
    {
        return ((IEnumerable<GroupTagBounds<ulong>>)Ranges).GetEnumerator();
    }

    /// <summary>Finds a range by its key using binary search.</summary>
    /// <param name="key">The key to search for.</param>
    public GroupTagBounds<ulong>? FindByKey(ulong key)
    {
        if (Length == 0)
        {
            return null;
        }
        var i = Ranges.BinarySearch(new GroupTagBounds<ulong>(key, 0, 0), new KeyComparator<ulong>());
        if (i < 0)
        {
            return null;
        }
        else
        {
            return Ranges[i];
        }
    }

    /// <summary>Gets all keys whose ranges contain the specified index.</summary>
    /// <param name="index">The index to search for.</param>
    public List<ulong> KeysFor(ulong index)
    {
        List<ulong> groups = new List<ulong>();
        foreach (var group in Ranges)
        {
            if (group.Contains(index))
            {
                groups.Add(group.Key);
            }
        }
        return groups;
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append("RowGroupIndex {\n");
        foreach (var x in Ranges)
        {
            builder.AppendFormat("\t{0}\n", x);
        }
        builder.Append("}");
        return builder.ToString();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return ((IEnumerable)Ranges).GetEnumerator();
    }
}


/// <summary>
/// Metadata for reading data arrays from Parquet files.
/// </summary>
public class DataArraysReaderMeta
{
    /// <summary>The buffer context (spectrum or chromatogram).</summary>
    public BufferContext Context;
    /// <summary>The array index describing available arrays.</summary>
    public ArrayIndex ArrayIndex;
    /// <summary>Index mapping entry keys to row groups.</summary>
    public RangeIndex RowGroupIndex;
    /// <summary>The buffer format used.</summary>
    public BufferFormat Format;

    /// <summary>Optional spacing interpolation models keyed by entry index.</summary>
    public Dictionary<ulong, SpacingInterpolationModel<double>>? SpacingModels;

    /// <summary>Creates metadata with the specified parameters.</summary>
    /// <param name="context">The buffer context.</param>
    /// <param name="arrayIndex">The array index.</param>
    /// <param name="rowGroupIndex">The row group index.</param>
    /// <param name="entrySpanIndex">The entry span index.</param>
    /// <param name="bufferFormat">The buffer format.</param>
    /// <param name="spacingModels">Optional spacing models.</param>
    public DataArraysReaderMeta(BufferContext context, ArrayIndex arrayIndex, RangeIndex rowGroupIndex, BufferFormat bufferFormat, Dictionary<ulong, SpacingInterpolationModel<double>>? spacingModels = null)
    {
        Context = context;
        ArrayIndex = arrayIndex;
        RowGroupIndex = rowGroupIndex;
        Format = bufferFormat;
        SpacingModels = spacingModels;
    }

    /// <summary>Creates metadata by reading from a Parquet file.</summary>
    /// <param name="reader">The Parquet file reader.</param>
    /// <param name="context">The buffer context.</param>
    public DataArraysReaderMeta(FileReader reader, BufferContext context)
    {
        Context = context;
        ArrayIndex = new ArrayIndex
        {
            Entries = new List<ArrayIndexEntry>(),
            Prefix = "?"
        };
        RowGroupIndex = new RangeIndex(new List<GroupTagBounds<ulong>>());
        InferBufferFormat(reader);
        LoadArrayIndex(reader);
        AnnotateSchemaIndices(reader);
        BuildRowGroupIndex(reader);
        SpacingModels = null;
    }

    void InferBufferFormat(FileReader reader)
    {
        var field = reader.Schema.GetFieldByIndex(0);
        switch (field.Name)
        {
            case "point":
                {
                    Format = BufferFormat.Point;
                    break;
                }
            case "chunk":
                {
                    Format = BufferFormat.ChunkValues;
                    break;
                }
            default:
                {
                    throw new NotImplementedException(string.Format("Root schema name {0} isn't recognized", field.Name));
                }
        }
    }

    void LoadArrayIndex(FileReader reader)
    {
        var key = string.Format("{0}_array_index", Context.Name());
        var arrayIndex = JsonSerializer.Deserialize<ArrayIndex>(reader.ParquetReader.FileMetaData.KeyValueMetadata[key]);
        if (arrayIndex == null)
        {
            throw new KeyNotFoundException("Array index is missing");
        }
        ArrayIndex = arrayIndex;
    }

    void AnnotateSchemaIndices(FileReader reader)
    {
        var schema = reader.ParquetReader.FileMetaData.Schema;
        var nCols = schema.NumColumns;
        for (var i = 0; i < nCols; i++)
        {
            var col = schema.Column(i);
            var pathOf = col.Path.ToDotString();
            if (pathOf.EndsWith(".list.item"))
            {
                pathOf = pathOf.Replace(".list.item", "");
            }
            else if (pathOf.EndsWith(".list.element"))
            {
                pathOf = pathOf.Replace(".list.element", "");
            }
            foreach (var arrEnt in ArrayIndex.Entries)
            {
                if (arrEnt.Path == pathOf)
                {
                    arrEnt.SchemaIndex = i;
                }
            }
        }
    }

    void BuildRowGroupIndex(FileReader reader)
    {
        List<GroupTagBounds<ulong>> index = new();
        for (ulong i = 0; i < (ulong)reader.ParquetReader.FileMetaData.NumRowGroups; i++)
        {
            var rg = reader.ParquetReader.RowGroup((int)i);
            var indexMeta = rg.MetaData.GetColumnChunkMetaData(0);
            if (indexMeta.Statistics != null && indexMeta.Statistics.HasMinMax)
            {
                var min = Convert.ToUInt64(indexMeta.Statistics.MinUntyped);
                var max = Convert.ToUInt64(indexMeta.Statistics.MaxUntyped);
                var bounds = new GroupTagBounds<ulong>
                {
                    Key = i,
                    Start = min,
                    End = max,
                };
                index.Add(bounds);
            }
        }
        RowGroupIndex = new RangeIndex(index);
    }
}


/// <summary>
/// Reader for data arrays stored in Parquet format.
/// </summary>
public class DataArraysReader : IAsyncEnumerable<(ulong, StructArray)>
{
    /// <summary>The buffer context (spectrum or chromatogram).</summary>
    public BufferContext BufferContext;
    FileReader FileReader;

    /// <summary>The reader metadata.</summary>
    public DataArraysReaderMeta Metadata;

    /// <summary>Gets the array index from metadata.</summary>
    public ArrayIndex ArrayIndex { get => Metadata.ArrayIndex; }
    /// <summary>Gets the row group index from metadata.</summary>
    public RangeIndex RowGroupIndex { get => Metadata.RowGroupIndex; }

    /// <summary>Gets or sets the spacing interpolation models.</summary>
    public Dictionary<ulong, SpacingInterpolationModel<double>>? SpacingModels
    {
        get
        {
            return Metadata.SpacingModels;
        }
        set
        {
            Metadata.SpacingModels = value;
        }
    }

    /// <summary>Creates a reader with existing metadata.</summary>
    /// <param name="reader">The Parquet file reader.</param>
    /// <param name="meta">The reader metadata.</param>
    public DataArraysReader(FileReader reader, DataArraysReaderMeta meta)
    {
        FileReader = reader;
        Metadata = meta;
    }

    /// <summary>Creates a reader that builds metadata from the file.</summary>
    /// <param name="reader">The Parquet file reader.</param>
    /// <param name="context">The buffer context.</param>
    public DataArraysReader(FileReader reader, BufferContext context)
    {
        BufferContext = context;
        FileReader = reader;
        Metadata = new DataArraysReaderMeta(reader, context);
    }

    /// <summary>Gets the buffer format from metadata.</summary>
    public BufferFormat Format => Metadata.Format;

    /// <summary>Creates an empty struct array matching the schema.</summary>
    public StructArray EmptyArrays()
    {
        List<Field> fields = new();
        List<Array> arrays = new();
        HashSet<Field> arrayTypes = new();
        foreach (var arrType in ArrayIndex.Entries)
        {
            var dtype = arrType.GetArrowType();
            var name = arrType.CreateColumnName();
            var field = new Field(name, dtype, true);
            if (arrayTypes.Contains(field)) continue;
            fields.Add(field);
            arrayTypes.Add(field);
        }
        var structDtype = new StructType(fields);
        return new StructArray(structDtype, 0, [], default);
    }

    /// <summary>Reads data arrays for a specific entry index.</summary>
    /// <param name="key">The entry index to read.</param>
    public async Task<StructArray?> ReadForIndex(ulong key)
    {
        var rowGroups = RowGroupIndex.KeysFor(key);
        if (0 == rowGroups.Count)
        {
            return null;
        }
        ;
        ulong offset = 0;
        for (var i = 0; (ulong)i < rowGroups[0]; i++)
        {
            offset += (ulong)FileReader.ParquetReader.RowGroup(i).MetaData.NumRows;
        }

        int[] rowGroupsArr = new int[rowGroups.Count];
        for (var i = 0; i < rowGroups.Count; i++)
        {
            rowGroupsArr[i] = Convert.ToInt32(rowGroups[i]);
        }

        BaseLayoutReader reader;
        if (Metadata.Format == BufferFormat.Point)
        {
            reader = new PointLayoutReader(FileReader.GetRecordBatchReader(rowGroupsArr), ArrayIndex, SpacingModels);
        }
        else if (Metadata.Format == BufferFormat.ChunkValues)
        {
            reader = new ChunkLayoutReader(FileReader.GetRecordBatchReader(rowGroupsArr), ArrayIndex, SpacingModels);
        }
        else
        {
            throw new InvalidDataException("Data layout not recognized");
        }

        var result = await reader.ReadRowsOf(key);
        return (StructArray?)ArrowArrayConcatenator.Concatenate(Enumerable.Range(0, result.ArrayCount).Select(i => result.Array(i)).ToList());
    }

    /// <summary>Asynchronously enumerates all entries with their index and data.</summary>
    public PeekableDataArraysIter Enumerate()
    {
        BaseLayoutReader reader;
        if (Metadata.Format == BufferFormat.Point)
        {
            reader = new PointLayoutReader(FileReader.GetRecordBatchReader(), ArrayIndex, SpacingModels);
        }
        else if (Metadata.Format == BufferFormat.ChunkValues)
        {
            reader = new ChunkLayoutReader(FileReader.GetRecordBatchReader(), ArrayIndex, SpacingModels);
        }
        else
        {
            throw new InvalidDataException("Data layout not recognized");
        }
        return reader.GetIter();
    }

    public IAsyncEnumerator<(ulong, StructArray)> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return Enumerate();
    }
}


/// <summary>
/// Base class for reading data arrays from different storage layouts.
/// </summary>
public class BaseLayoutReader : IAsyncEnumerable<(ulong, StructArray)>
{
    public static ILogger? Logger = null;

    protected ArrayIndex ArrayIndex;
    protected IArrowArrayStream Reader;
    protected Dictionary<ulong, SpacingInterpolationModel<double>>? SpacingModels;

    /// <summary>Creates a layout reader with the specified configuration.</summary>
    /// <param name="reader">The Arrow array stream.</param>
    /// <param name="arrayIndex">The array index metadata.</param>
    /// <param name="spacingModels">Optional spacing interpolation models.</param>
    public BaseLayoutReader(IArrowArrayStream reader, ArrayIndex arrayIndex, Dictionary<ulong, SpacingInterpolationModel<double>>? spacingModels = null)
    {
        Reader = reader;
        ArrayIndex = arrayIndex;
        SpacingModels = spacingModels;
    }

    /// <summary>
    /// Given a batch of rows, boil it down to the relevant rows for a specific entry
    /// index.
    /// </summary>
    /// <param name="entryIndex">The index to extract</param>
    /// <param name="rootStruct">The collection of rows that have been pulled from the reader which may not be specific</param>
    /// <returns>The selected rows for the entry with the given index after being transformed or expanded to flat arrays</returns>
    public virtual StructArray ProcessSegment(ulong entryIndex, StructArray rootStruct)
    {
        var indexArr = (UInt64Array)rootStruct.Fields[0];
        var mask = Compute.Equal(indexArr, entryIndex);
        rootStruct = (StructArray)Compute.Filter(rootStruct, mask);
        return rootStruct;
    }

    /// <summary>Asynchronously enumerates all entries.</summary>
    public IAsyncEnumerator<(ulong, StructArray)> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return new PeekableDataArraysIter(this, Reader);
    }

    public PeekableDataArraysIter GetIter()
    {
        return new PeekableDataArraysIter(this, Reader);
    }

    /// <summary>Reads rows for a specific entry within a row range.</summary>
    /// <param name="entryIndex">The entry index.</param>
    public async Task<ChunkedArray> ReadRowsOf(ulong entryIndex)
    {
        var chunks = new List<Array>();
        while (true)
        {
            var batch = await Reader.ReadNextRecordBatchAsync();
            if (batch == null)
            {
                break;
            }
            var root = batch.Column(0);

            var rootStruct = (StructArray?)root;
            if (rootStruct == null)
            {
                continue;
            }

            // Assumes that the index column is sorted
            var indexArr = (UInt64Array)rootStruct.Fields[0];
            (ulong, int)? first = Compute.FirstNotNull(indexArr);
            if (first != null)
            {
                if (first.Value.Item1 > entryIndex) break;
            }
            (ulong, int)? last = Compute.LastNotNull(indexArr);
            if (last != null)
            {
                if (last.Value.Item1 < entryIndex) continue;
            }

            var chunk = ProcessSegment(entryIndex, rootStruct);
            if (chunk.Length == 0 && chunks.Count > 0) break;
            chunks.Add(chunk);
        }
        if (chunks.Count == 0) throw new InvalidOperationException($"Cannot handle empty row range");
        return new ChunkedArray(chunks);
    }
}


/// <summary>
/// Reader for point-format data layouts where each row is a single data point.
/// </summary>
public class PointLayoutReader : BaseLayoutReader
{
    /// <summary>Creates a point layout reader.</summary>
    /// <param name="reader">The Arrow array stream.</param>
    /// <param name="arrayIndex">The array index metadata.</param>
    /// <param name="spacingModels">Optional spacing interpolation models.</param>
    public PointLayoutReader(IArrowArrayStream reader, ArrayIndex arrayIndex, Dictionary<ulong, SpacingInterpolationModel<double>>? spacingModels = null) : base(reader, arrayIndex, spacingModels) { }

    public override StructArray ProcessSegment(ulong entryIndex, StructArray rootStruct)
    {
        var rows = base.ProcessSegment(entryIndex, rootStruct);
        var fields = ((StructType)rows.Data.DataType).Fields;
        var columnsAfter = new List<IArrowArray?>(fields.Count);
        Dictionary<int, IArrowArray> converted = new();
        foreach (var entry in ArrayIndex.Entries)
        {
            var name = entry.Path.Split(".").Last();
            IArrowArray? column = null;
            int index = 0;
            for (var i = 0; i < fields.Count; i++)
            {
                if (name == fields[i].Name)
                {
                    index = i;
                    column = rows.Fields[i];
                }
            }

            if (column == null)
            {
                continue;
            }
            if (entry.Transform == NullInterpolation.NullInterpolateCURIE && column.NullCount > 0)
            {
                if (SpacingModels == null || !SpacingModels.ContainsKey(entryIndex))
                {
                    continue;
                }
                SpacingInterpolationModel<double> model = SpacingModels[entryIndex];
                switch (column.Data.DataType.TypeId)
                {
                    case ArrowTypeId.Float:
                        {
                            var builder = new FloatArray.Builder();
                            var modelFloat = new SpacingInterpolationModel<float>(model.Coefficients.Select((v) => (float)v).ToList());
                            NullInterpolation.FillNullsWithModel((FloatArray)column, modelFloat, builder);
                            converted[index] = builder.Build();
                            break;
                        }
                    case ArrowTypeId.Double:
                        {
                            var builder = new DoubleArray.Builder();
                            NullInterpolation.FillNullsWithModel((DoubleArray)column, model, builder);
                            converted[index] = builder.Build();
                            break;
                        }
                    default:
                        {
                            throw new InvalidOperationException("Cannot interpolate non-float");
                        }
                }
            }
            else if (entry.Transform == NullInterpolation.NullZeroCURIE)
            {
                converted[index] = Compute.NullToZero(column);
            }
            else
            {
                converted[index] = column;
            }
        }

        for (var i = 0; i < fields.Count; i++)
        {
            var column = rows.Fields[i];
            if (converted.TryGetValue(i, out column))
                columnsAfter.Add(column);
            else
                columnsAfter.Add(rows.Fields[i]);
        }
        return new StructArray(rows.Data.DataType, rows.Length, columnsAfter, default); ;
    }
}

record TransformKey(ArrayType? ArrayType, string ArrayName, BinaryDataType BinaryDataType, Unit? Unit, string? DataProcessesingId)
{
    public override int GetHashCode()
    {
        return (ArrayType, ArrayName, BinaryDataType, Unit, DataProcessesingId).GetHashCode();
    }

    public static TransformKey FromArrayIndexEntry(ArrayIndexEntry entry)
    {
        BinaryDataType dataType;
        if (!BinaryDataTypeMethods.FromCURIE.TryGetValue(entry.DataTypeCURIE, out dataType)) throw new InvalidDataException();
        return new(entry.GetArrayType(), entry.ArrayName, dataType, entry.GetUnit(), entry.DataProcessesingId);
    }
}

/// <summary>
/// Reader for chunk-format data layouts where each row contains compressed array chunks.
/// </summary>
public class ChunkLayoutReader : BaseLayoutReader
{
    const string NUMPRESS_LINEAR_CURIE = "MS:1002312";
    const string NUMPRESS_SLOF_CURIE = "MS:1002314";

    ArrayIndexEntry? mainAxis;
    int chunkStartIndex = -1;
    int chunkEndIndex = -1;
    int chunkEncodingIndex = -1;
    int chunkValuesIndex = -1;
    HashSet<ArrayIndexEntry> secondaryIndices;
    Dictionary<TransformKey, List<ArrayIndexEntry>> transformMap;


    void ConfigureIndices()
    {
        foreach (var entry in ArrayIndex.Entries)
        {
            if (entry.SchemaIndex == null)
            {
                throw new InvalidOperationException(string.Format("ArrayIndex entries cannot have null indices at this point: {0}", entry));
            }
            var index = (int)entry.SchemaIndex;
            switch (entry.BufferFormat)
            {
                case BufferFormat.ChunkStart:
                    {
                        chunkStartIndex = index;
                        break;
                    }
                case BufferFormat.ChunkEnd:
                    {
                        chunkEndIndex = index;
                        break;
                    }
                case BufferFormat.ChunkEncoding:
                    {
                        chunkEncodingIndex = index;
                        break;
                    }
                case BufferFormat.ChunkValues:
                    {
                        mainAxis = entry;
                        chunkValuesIndex = index;
                        break;
                    }
                case BufferFormat.ChunkSecondary:
                    {
                        secondaryIndices.Add(entry);
                        break;
                    }
                case BufferFormat.ChunkTransform:
                    {
                        var key = TransformKey.FromArrayIndexEntry(entry);
                        if (transformMap.ContainsKey(key))
                            transformMap[key].Add(entry);
                        else
                            transformMap[key] = [entry];
                        secondaryIndices.Add(entry);
                        break;
                    }
                default:
                    throw new NotImplementedException(string.Format("Unsupported buffer format {0} for the chunked layout", entry.BufferFormat));
            }
        }

        if (chunkEncodingIndex == -1)
            throw new InvalidOperationException("Chunk encoding column not found");

        if (mainAxis == null)
            throw new InvalidOperationException("Main axis cannot be null");
    }

    public ChunkLayoutReader(IArrowArrayStream reader, ArrayIndex arrayIndex, Dictionary<ulong, SpacingInterpolationModel<double>>? spacingModels = null) : base(reader, arrayIndex, spacingModels)
    {
        mainAxis = null;
        secondaryIndices = new();
        transformMap = new();
        ConfigureIndices();
    }

    IArrowArray DecodeNoCompression(ulong entryIndex, double startValue, IArrowArray chunkValues, ArrayIndexEntry entryMeta)
    {
        IArrowArray result;
        switch (chunkValues.Data.DataType.TypeId)
        {
            case ArrowTypeId.Int32:
                {
                    var builder = new Int32Array.Builder();
                    NoCompressionCodec.Decode((int)startValue, (Int32Array)chunkValues, builder);
                    result = builder.Build();
                    break;
                }
            case ArrowTypeId.Int64:
                {
                    var builder = new Int64Array.Builder();
                    NoCompressionCodec.Decode((long)startValue, (Int64Array)chunkValues, builder);
                    result = builder.Build();
                    break;
                }
            case ArrowTypeId.Float:
                {
                    var builder = new FloatArray.Builder();
                    NoCompressionCodec.Decode((float)startValue, (FloatArray)chunkValues, builder);
                    result = builder.Build();
                    if (entryMeta.Transform == NullInterpolation.NullInterpolateCURIE && SpacingModels != null && result.NullCount > 0)
                    {
                        var model = SpacingModels[entryIndex];
                        var floatModel = new SpacingInterpolationModel<float>(model.Coefficients.Select(v => (float)v).ToList());
                        builder = new FloatArray.Builder();
                        NullInterpolation.FillNullsWithModel((FloatArray)result, floatModel, builder);
                        result = builder.Build();
                    }
                    break;
                }
            case ArrowTypeId.Double:
                {
                    var builder = new DoubleArray.Builder();
                    NoCompressionCodec.Decode((double)startValue, (DoubleArray)chunkValues, builder);
                    result = builder.Build();
                    if (entryMeta.Transform == NullInterpolation.NullInterpolateCURIE && SpacingModels != null && result.NullCount > 0)
                    {
                        var model = SpacingModels[entryIndex];
                        builder = new DoubleArray.Builder();
                        NullInterpolation.FillNullsWithModel((DoubleArray)result, model, builder);
                        result = builder.Build();
                    }
                    break;
                }
            default:
                {
                    throw new NotImplementedException("Unsupported data type: " + chunkValues.Data.DataType.Name);
                }
        }
        return result;
    }

    IArrowArray DecodeDelta(ulong entryIndex, double startValue, IArrowArray chunkValues, ArrayIndexEntry entryMeta)
    {
        IArrowArray result;
        switch (chunkValues.Data.DataType.TypeId)
        {
            case ArrowTypeId.Int32:
                {
                    var builder = new Int32Array.Builder();
                    DeltaCodec.Decode((int)startValue, (Int32Array)chunkValues, builder);
                    result = builder.Build();
                    break;
                }
            case ArrowTypeId.Int64:
                {
                    var builder = new Int64Array.Builder();
                    DeltaCodec.Decode((long)startValue, (Int64Array)chunkValues, builder);
                    result = builder.Build();
                    break;
                }
            case ArrowTypeId.Float:
                {
                    var builder = new FloatArray.Builder();
                    DeltaCodec.Decode((float)startValue, (FloatArray)chunkValues, builder);
                    result = builder.Build();
                    if (entryMeta.Transform == NullInterpolation.NullInterpolateCURIE && SpacingModels != null && result.NullCount > 0)
                    {
                        var model = SpacingModels[entryIndex];
                        var floatModel = new SpacingInterpolationModel<float>(model.Coefficients.Select(v => (float)v).ToList());
                        builder = new FloatArray.Builder();
                        NullInterpolation.FillNullsWithModel((FloatArray)result, floatModel, builder);
                        result = builder.Build();
                    }
                    break;
                }
            case ArrowTypeId.Double:
                {
                    var builder = new DoubleArray.Builder();
                    DeltaCodec.Decode((double)startValue, (DoubleArray)chunkValues, builder);
                    result = builder.Build();
                    if (entryMeta.Transform == NullInterpolation.NullInterpolateCURIE && SpacingModels != null && result.NullCount > 0)
                    {
                        var model = SpacingModels[entryIndex];
                        builder = new DoubleArray.Builder();
                        NullInterpolation.FillNullsWithModel((DoubleArray)result, model, builder);
                        result = builder.Build();
                    }
                    break;
                }
            default:
                {
                    throw new NotImplementedException("Unsupported data type: " + chunkValues.Data.DataType.Name);
                }
        }
        return result;
    }

    ArrayIndexEntry FindEntryForTransform(ArrayIndexEntry query, string transform)
    {
        foreach (var ent in transformMap[TransformKey.FromArrayIndexEntry(query)])
            if (ent.Transform == transform)
                return ent;
        throw new KeyNotFoundException($"No entry was found for {TransformKey.FromArrayIndexEntry(query)} with transform  = {transform}");
    }

    public override StructArray ProcessSegment(ulong entryIndex, StructArray rootStruct)
    {
        var rows = base.ProcessSegment(entryIndex, rootStruct);
        var encodingMethod = (StringArray)rows.Fields[chunkEncodingIndex];
        var chunkStart = rows.Fields[chunkStartIndex];
        var chunkValues = rows.Fields[chunkValuesIndex];
        var chunkValuesIsLarge = chunkValues.Data.DataType.TypeId == ArrowTypeId.LargeList;
        var isTracing = Logger?.IsEnabled(LogLevel.Trace) ?? false;
        var chunkStartType = chunkStart.Data.DataType;
        if (!chunkStartType.IsFloatingPoint() || chunkStartType.TypeId == ArrowTypeId.HalfFloat)
        {
            throw new InvalidOperationException(string.Format("The chunk start type must be Float or Double, not {0}", chunkStartType));
        }
        var chunkStartDouble = chunkStartType.TypeId == ArrowTypeId.Double;

        if (mainAxis == null) throw new InvalidOperationException("mainAxis cannot be null");
        List<IArrowArray> decodedValues = new();
        Dictionary<ArrayIndexEntry, List<IArrowArray>> secondaryValues = new();
        var nRows = encodingMethod.Length;
        for (var i = 0; i < nRows; i++)
        {
            var startValue = chunkStartDouble ? ((DoubleArray)chunkStart).GetValue(i) : ((FloatArray)chunkStart).GetValue(i);
            if (startValue == null)
            {
                continue;
            }
            var valueList = chunkValuesIsLarge ? ((LargeListArray)chunkValues).GetSlicedValues(i) : ((ListArray)chunkValues).GetSlicedValues(i);
            var method = encodingMethod.GetString(i);
            switch (method)
            {
                case NoCompressionCodec.CURIE:
                    {
                        decodedValues.Add(DecodeNoCompression(entryIndex, (double)startValue, valueList, mainAxis));
                        break;
                    }
                case DeltaCodec.CURIE:
                    {
                        try
                        {
                            decodedValues.Add(DecodeDelta(entryIndex, (double)startValue, valueList, mainAxis));
                        }
                        catch (IndexOutOfRangeException e)
                        {
                            throw new IndexOutOfRangeException(
                                $"Failed to delta decode chunk for entry {entryIndex} starting at {startValue} with {valueList.Length} values",
                                innerException: e
                            );
                        }
                        break;
                    }
                case NUMPRESS_LINEAR_CURIE:
                    {
                        var tfmEntry = FindEntryForTransform(mainAxis, NUMPRESS_LINEAR_CURIE);
                        if (tfmEntry.SchemaIndex == null) throw new InvalidOperationException("Array index entry transform not mapped to column!");
                        var arr = rows.Fields[(int)tfmEntry.SchemaIndex];
                        if (arr.IsNull(i)) throw new InvalidOperationException("Transformed main axis array slot cannot be null");
                        var values = (PrimitiveArray<byte>)((arr.Data.DataType.TypeId == ArrowTypeId.LargeList) ? ((LargeListArray)arr).GetSlicedValues(i) : ((ListArray)arr).GetSlicedValues(i));

                        var valuesNat = Numpress.MSNumpress.decode(NUMPRESS_LINEAR_CURIE, values.ValueBuffer.Span, values.Length * 3);
                        decodedValues.Add(valueList.Data.DataType.TypeId == ArrowTypeId.Float ? Compute.CastFloat(valuesNat) : Compute.CastDouble(valuesNat));
                        break;
                    }
                default: throw new NotImplementedException("Unknown chunk encoding: " + method);
            }
        }

        foreach (var entry in secondaryIndices)
        {
            if (entry.SchemaIndex == null)
                throw new InvalidOperationException($"ArrayIndexEntry schema index somehow made null!?: {entry}");
            List<IArrowArray> chunks = new();
            secondaryValues.Add(entry, chunks);
            var col = rows.Fields[(int)entry.SchemaIndex];
            var colIsLarge = col.Data.DataType.TypeId == ArrowTypeId.LargeList;
            var eltType = colIsLarge ? ((LargeListType)col.Data.DataType).ValueDataType : ((ListType)col.Data.DataType).ValueDataType;
            switch (eltType.TypeId)
            {
                case ArrowTypeId.Float:
                    {
                        for (var i = 0; i < nRows; i++)
                        {
                            if (col.IsNull(i))
                                chunks.Add(new FloatArray.Builder().Build());
                            else
                            {
                                var valsAt = colIsLarge ? ((LargeListArray)col).GetSlicedValues(i) : ((ListArray)col).GetSlicedValues(i);
                                if (entry.Transform == NullInterpolation.NullZeroCURIE)
                                    valsAt = (FloatArray)Compute.NullToZero((FloatArray)valsAt);
                                else if (entry.Transform == NUMPRESS_SLOF_CURIE)
                                {
                                    var decoded = Numpress.MSNumpress.decode(NUMPRESS_SLOF_CURIE, ((UInt8Array)valsAt).ValueBuffer.Span, valsAt.Length * 3);
                                    valsAt = Compute.CastFloat(decoded);
                                }
                                if (isTracing)
                                    Logger?.LogTrace($"{entry.Path} had {valsAt.Length}/{decodedValues[i].Length} values at {i}");
                                chunks.Add((FloatArray)valsAt);
                            }
                        }
                        break;
                    }
                case ArrowTypeId.Double:
                    {
                        for (var i = 0; i < nRows; i++)
                        {
                            if (col.IsNull(i))
                                chunks.Add(new DoubleArray.Builder().Build());
                            else
                            {
                                var valsAt = colIsLarge ? ((LargeListArray)col).GetSlicedValues(i) : ((ListArray)col).GetSlicedValues(i);
                                if (entry.Transform == NullInterpolation.NullZeroCURIE)
                                    valsAt = Compute.NullToZero((DoubleArray)valsAt);
                                else if (entry.Transform == NUMPRESS_SLOF_CURIE)
                                {
                                    var decoded = Numpress.MSNumpress.decode(NUMPRESS_SLOF_CURIE, ((UInt8Array)valsAt).ValueBuffer.Span, valsAt.Length * 3);
                                    valsAt = Compute.CastDouble(decoded);
                                }
                                if (isTracing)
                                    Logger?.LogTrace($"{entry.Path} had {valsAt.Length}/{decodedValues[i].Length} values at {i}");
                                chunks.Add(valsAt);
                            }
                        }
                        break;
                    }
                case ArrowTypeId.Int32:
                    {
                        for (var i = 0; i < nRows; i++)
                        {
                            if (col.IsNull(i))
                                chunks.Add(new Int32Array.Builder().Build());
                            else
                            {
                                var valsAt = (Int32Array)(colIsLarge ? ((LargeListArray)col).GetSlicedValues(i) : ((ListArray)col).GetSlicedValues(i));
                                if (entry.Transform == NullInterpolation.NullZeroCURIE)
                                    valsAt = (Int32Array)Compute.NullToZero(valsAt);
                                chunks.Add(valsAt);
                            }
                        }
                        break;
                    }
                case ArrowTypeId.Int64:
                    {
                        for (var i = 0; i < nRows; i++)
                        {
                            if (col.IsNull(i))
                                chunks.Add(new Int64Array.Builder().Build());
                            else
                            {
                                var valsAt = (Int64Array)(colIsLarge ? ((LargeListArray)col).GetSlicedValues(i) : ((ListArray)col).GetSlicedValues(i));
                                if (entry.Transform == NullInterpolation.NullZeroCURIE)
                                    valsAt = (Int64Array)Compute.NullToZero(valsAt);
                                chunks.Add(valsAt);
                            }
                        }
                        break;
                    }
                default:
                    throw new NotImplementedException(string.Format("Secondary chunk array type {0} not yet implemented", eltType));
            }
        }

        if (mainAxis == null)
            throw new InvalidOperationException("Main axis cannot be null");

        var mainName = mainAxis.Path.Split(".").Last().Replace("_chunk_values", "");
        var fields = new List<Field>
        {
            new Field(mainAxis.Context.IndexName(), new UInt64Type(), true),
            new Field(mainName, mainAxis.GetArrowType(), true)
        };

        foreach (var ent in secondaryValues)
        {
            var name = ent.Key.Path.Split(".").Last();
            if (ent.Value.Count == 0)
                fields.Add(new Field(name, ent.Key.GetArrowType(), true));
            else
                fields.Add(new Field(name, ent.Value[0].Data.DataType, true));
        }

        var dataType = new StructType(fields);

        List<IArrowArray> rowChunks = new();
        for (int i = 0; i < decodedValues.Count; i++)
        {
            var n = decodedValues[i].Length;
            if (isTracing)
                Logger?.LogTrace($"Adding chunk of size {n}");


            var indexBuild = new UInt64Array.Builder();
            indexBuild.AppendRange(Enumerable.Repeat(entryIndex, n));

            List<IArrowArray> cols = new()
            {
                indexBuild.Build(),
                decodedValues[i]
            };

            foreach (var ent in secondaryValues)
            {
                cols.Add(ent.Value[i]);
            }
            var bitmapBuilder = new ArrowBuffer.BitmapBuilder();
            bitmapBuilder.AppendRange(Enumerable.Repeat(true, n));
            var chunk = new StructArray(dataType, n, cols, bitmapBuilder.Build());
            rowChunks.Add(chunk);
        }
        if (rowChunks.Count == 0)
        {
            return CreateEmpty(dataType);
        }
        var combined = (StructArray)ArrowArrayConcatenator.Concatenate(rowChunks);
        if (combined == null) throw new InvalidDataException($"root array cannot be null");
        if (isTracing)
            Logger?.LogTrace($"Chunk layout collected {combined.Length} records");
        return combined;
    }

    protected StructArray CreateEmpty(StructType dataType)
    {
        List<IArrowArray> arrays = new();
        foreach(var f in dataType.Fields)
        {
            switch (f.DataType.TypeId)
            {
                case ArrowTypeId.Float:
                    {
                        arrays.Add(new FloatArray.Builder().Build());
                        break;
                    }
                case ArrowTypeId.Double:
                    {
                        arrays.Add(new DoubleArray.Builder().Build());
                        break;
                    }
                case ArrowTypeId.Int32:
                    {
                        arrays.Add(new Int32Array.Builder().Build());
                        break;
                    }
                case ArrowTypeId.Int64:
                    {
                        arrays.Add(new Int64Array.Builder().Build());
                        break;
                    }
                case ArrowTypeId.UInt32:
                    {
                        arrays.Add(new UInt32Array.Builder().Build());
                        break;
                    }
                case ArrowTypeId.UInt64:
                    {
                        arrays.Add(new UInt64Array.Builder().Build());
                        break;
                    }
                case ArrowTypeId.UInt16:
                    {
                        arrays.Add(new UInt16Array.Builder().Build());
                        break;
                    }
                case ArrowTypeId.UInt8:
                    {
                        arrays.Add(new UInt8Array.Builder().Build());
                        break;
                    }
                case ArrowTypeId.Boolean:
                    {
                        arrays.Add(new BooleanArray.Builder().Build());
                        break;
                    }
                case ArrowTypeId.String:
                    {
                        arrays.Add(new StringArray.Builder().Build());
                        break;
                    }
                case ArrowTypeId.Binary:
                    {
                        arrays.Add(new BinaryArray.Builder().Build());
                        break;
                    }

            }
        }
        return new StructArray(dataType, 0, arrays, ArrowBuffer.Empty);
    }
}


class DataArraysIter : IAsyncEnumerator<(ulong, StructArray)>, IAsyncEnumerable<(ulong, StructArray)>
{
    public CancellationToken CancellationToken;
    BaseLayoutReader LayoutReader;
    IArrowArrayStream StreamReader;
    ulong? CurrentIndex = null;
    bool init = false;
    StructArray? CurrentBatch = null;
    (ulong, StructArray)? NextItem = null;

    public (ulong, StructArray) Current => NextItem == null ? throw new InvalidOperationException() : ((ulong, StructArray))NextItem;

    public DataArraysIter(BaseLayoutReader layoutReader, IArrowArrayStream stream)
    {
        LayoutReader = layoutReader;
        StreamReader = stream;

        CancellationToken = default;
        CurrentIndex = null;
        CurrentBatch = null;
        NextItem = null;
    }

    public async ValueTask<bool> ReadNextBatch(bool updateIndex = false)
    {
        CurrentBatch = null;
        var batch = await StreamReader.ReadNextRecordBatchAsync(CancellationToken);
        if (batch == null)
        {
            return false;
        }

        var root = batch.Column(0);

        var rootStruct = (StructArray?)root;
        if (rootStruct == null)
        {
            return false;
        }

        CurrentBatch = rootStruct;

        var idxCol = (UInt64Array)CurrentBatch.Fields[0];
        var lowestIndex = Compute.Min(idxCol);
        if (updateIndex && ((CurrentIndex != null && lowestIndex > CurrentIndex) || CurrentIndex == null))
        {
            CurrentIndex = lowestIndex;
        }
        return true;
    }

    ulong? FirstIndexInBatch()
    {
        if (CurrentBatch == null) return null;
        var idxCol = (UInt64Array)CurrentBatch.Fields[0];
        if (idxCol.Length == 0) return null;
        return idxCol.GetValue(0);
    }

    async Task<bool> Initialize()
    {
        if (!await ReadNextBatch()) return false;
        if (CurrentBatch == null)
        {
            return false;
        }
        var idxCol = (UInt64Array)CurrentBatch.Fields[0];
        CurrentIndex = Compute.Min(idxCol);
        init = true;
        return init;
    }

    bool BatchHasCurrentIndex()
    {
        if (CurrentBatch == null || CurrentIndex == null) return false;
        return Compute.Equal((UInt64Array)CurrentBatch.Fields[0], (ulong)CurrentIndex).Any((v) => v ?? false);
    }

    async ValueTask<StructArray?> ExtractForCurrentIndex()
    {
        if (CurrentBatch == null || CurrentIndex == null) return null;
        var mask = Compute.Equal((UInt64Array)CurrentBatch.Fields[0], (ulong)CurrentIndex);
        var indices = mask.Select((v, i) => (v, i)).Where((v) => v.v ?? false).Select(v => v.i).ToList();
        var lastPossibleRowIndex = CurrentBatch.Length - 1;
        int n;
        int start;
        StructArray chunk;
        if (indices.Count == 0)
        {
            n = CurrentBatch.Length;
            start = 0;
            chunk = (StructArray)CurrentBatch.Slice(0, 0);
        }
        else
        {
            start = indices[0];
            n = indices.Count;
            chunk = (StructArray)CurrentBatch.Slice(start, n);
        }

        if (n == CurrentBatch.Length || indices.Contains(lastPossibleRowIndex))
        {
            if (await ReadNextBatch(false))
            {
                if (BatchHasCurrentIndex())
                {
                    var rest = await ExtractForCurrentIndex();
                    if (rest != null)
                        chunk = (StructArray)ArrowArrayConcatenator.Concatenate([chunk, rest]);
                }
            }
        }
        else
        {
            CurrentBatch = (StructArray)CurrentBatch.Slice(n, CurrentBatch.Length - n);
        }
        return chunk;
    }

    public async ValueTask<bool> MoveNextAsyncWithProcess(bool doProcess)
    {
        if (CurrentIndex == null)
        {
            if (!await Initialize())
            {
                return false;
            }
        }
        if (CurrentIndex == null)
        {
            return false;
        }
        var nextBatch = await ExtractForCurrentIndex();
        if (nextBatch == null)
        {
            return false;
        }

        if (doProcess)
        {
            nextBatch = LayoutReader.ProcessSegment(
                (ulong)CurrentIndex,
                nextBatch
            );
        }
        NextItem = ((ulong)CurrentIndex, nextBatch);
        var nextIndex = FirstIndexInBatch();
        if (nextIndex < CurrentIndex) throw new InvalidDataException($"Next index {nextIndex} < current index {CurrentIndex}");
        CurrentIndex = nextIndex;
        return true;
    }

    public async ValueTask<bool> MoveNextAsync()
    {
        return await MoveNextAsyncWithProcess(true);
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask();
    }

    public IAsyncEnumerator<(ulong, StructArray)> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        CancellationToken = cancellationToken;
        return this;
    }
}


/// <summary>
/// A seekable, peekable iterator over a batch stream
/// </summary>
public class PeekableDataArraysIter : IAsyncEnumerator<(ulong, StructArray)>, IAsyncEnumerable<(ulong, StructArray)>
{
    DataArraysIter Inner;
    LinkedList<(ulong, StructArray)> Peeked;
    (ulong, StructArray)? Value;

    public (ulong, StructArray) Current => Value != null ? Value.Value : Peeked.First == null ? throw new InvalidOperationException() : Peeked.First.Value;

    public PeekableDataArraysIter(BaseLayoutReader layoutReader, IArrowArrayStream stream)
    {
        Inner = new DataArraysIter(layoutReader, stream);
        Peeked = [];
        Value = null;
    }

    /// <summary>
    /// Peek at the *next* value in the queue, not the *current* value.
    ///
    /// This may trigger I/O and/or consume
    /// </summary>
    /// <returns>The next value or <c>null</c></returns>
    public async ValueTask<(ulong, StructArray)?> Peek()
    {
        if (Peeked.Count == 0)
            await NextFromInner();
        return Peeked.First?.Value;
    }

    /// <summary>
    /// Pull the next value from the inner iterator and add it to the internal queue
    /// </summary>
    /// <returns></returns>
    async ValueTask<bool> NextFromInner()
    {
        if (await Inner.MoveNextAsync())
        {
            Peeked.AddLast(Inner.Current);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Put a value back into the queue. This becomes the *current* value
    /// </summary>
    /// <param name="value"></param>
    public void Prepend((ulong, StructArray) value)
    {
        if (Value != null)
            Peeked.Prepend(Value.Value);
        Value = value;
    }

    public async ValueTask<bool> MoveNextAsync()
    {
        if (Peeked.First != null)
        {
            Value = Peeked.First.Value;
            Peeked.RemoveFirst();
            return true;
        }
        else
        {
            if (await NextFromInner())
            {
                if (Peeked.First == null) throw new InvalidOperationException();
                Value = Peeked.First.Value;
                Peeked.RemoveFirst();
                return true;
            }
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask();
    }

    /// <summary>
    /// Peek at the *next* value's index slot if one exists
    /// </summary>
    /// <returns></returns>
    public async Task<ulong?> PeekIndex()
    {
        var value = await Peek();
        return value?.Item1;
    }

    /// <summary>
    /// Consume the iterator until the *next* value's index is greater than or equal to the requested index.
    /// </summary>
    /// <param name="index"></param>
    /// <returns>Whether the next index matches <c>index</c></returns>
    /// <exception cref="InvalidOperationException">
    /// If the <c>index</c> is < <cref>PeekeIndex</cref>
    /// </exception>
    public async Task<bool> Seek(ulong index)
    {
        var currentIndex = await PeekIndex();
        if (index < currentIndex)
            throw new InvalidOperationException($"Cannot move an iterator to an earlier position in the stream. Current index is {currentIndex}, requested {index}");
        if (index == currentIndex)
            return true;
        if (currentIndex == null)
            return false;
        (ulong, StructArray)? currentValue = null;
        while (await PeekIndex() < index)
        {
            if (await MoveNextAsync())
                currentValue = Value;
            else
                break;
        }
        if (currentValue.HasValue) Prepend(currentValue.Value);
        return await PeekIndex() == index;
    }

    /// <summary>
    /// Consume the next value from the iterator and return it
    /// </summary>
    /// <returns></returns>
    public async Task<(ulong, StructArray)?> Consume()
    {
        return await MoveNextAsync() ? Value : null;
    }

    public IAsyncEnumerator<(ulong, StructArray)> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        Inner.CancellationToken = cancellationToken;
        return this;
    }
}
