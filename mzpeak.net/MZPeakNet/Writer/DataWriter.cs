using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.Extensions.Logging;
using MZPeak.Compute;
using MZPeak.ControlledVocabulary;
using MZPeak.Metadata;
using Array = Apache.Arrow.Array;

namespace MZPeak.Writer.Data;

using ComputeFn = Compute.Compute;

public record EntryDerivedMetadata(SpacingInterpolationModel<double>? SpacingInterpolationModel, List<AuxiliaryArray> AuxiliaryArrays, int? DataPointCount=null, int? PeakCount=null)
{
    public static EntryDerivedMetadata Empty => new(null, []);
};

public abstract class BaseDataLayoutWriter
{
    public static ILogger? Logger = null;

    /// <summary>
    /// The kind of entity this data writer handles
    /// </summary>
    public BufferContext BufferContext { get; protected set; }

    /// <summary>
    /// Whether or not to remove runs of zero intensity values. This is reasonable for high resolution
    /// mass spectra, but maybe not for chromatographic traces.
    /// </summary>
    public bool ShouldRemoveZeroRuns { get; set; } = true;

    /// <summary>
    /// A counter on the total number of data points written
    /// </summary>
    public ulong NumberOfPoints = 0;

    /// <summary>
    /// The array builder for the <c>{layout}.{namespace}_index</c> column. This array is simply populated with runs of index values.
    /// </summary>
    protected UInt64Array.Builder Index;

    /// <summary>
    /// The array builders for all of the other columns in the table. They run parallel to <see cref="DataTypes"> and <see cref="ArrayIndex"/>
    /// </summary>
    protected List<IArrowArrayBuilder> Arrays;
    /// <summary>
    /// The Arrow types for all of the other columns in the table. They run parallel to <see cref="Arrays"> and <see cref="ArrayIndex"/>
    /// </summary>
    protected List<IArrowType> DataTypes;

    /// <summary>
    /// The formalized <c>ArrayIndex</c> associated with this data writer. All columns beyond the index column are defined here, and this dictates
    /// the order of columns as they are written. This in turn influences how the list-based overloads of <see cref="Add"/>  methods work.
    /// </summary>
    public ArrayIndex ArrayIndex { get; protected set; }

    /// <summary>
    /// The number of rows currently stored in the writer. This may not be the total number of rows written by this writer as they are removed and
    /// flushed to disk by <c>GetRecordBatch</c>.
    /// </summary>
    public int BufferedRows => Index.Length;
    /// <summary>
    /// Get an estimate of the total number of values currently stored in the writer. This is not precise as it does not take into account any values
    /// not stored explicitly.
    /// </summary>
    public int BufferedSize => Index.Length + DataTypes.Zip(Arrays).Sum(dtBuilder => dtBuilder.Second.Length);

    /// <summary>
    /// The name of the layout as it will appear in the Parquet schema, e.g. <c>point</c> or <c>chunk</c>
    /// </summary>
    /// <returns>The layout name as a string</returns>
    public abstract string LayoutName();

    public BaseDataLayoutWriter(ArrayIndex arrayIndex)
    {
        ArrayIndex = arrayIndex;
        Index = new();
        Arrays = new();
        DataTypes = new();
        InitializeBuilders();
    }

    /// <summary>
    /// Test whether there are any columns in this writer which correspond to the requested array type. See <see cref="ArrayIndex.HasArrayType(ArrayType)"/>
    /// </summary>
    /// <param name="arrayType"></param>
    /// <returns></returns>
    public bool HasArrayType(ArrayType arrayType) => ArrayIndex.HasArrayType(arrayType);

    protected virtual void InitializeBuilders()
    {
        int i = 1;
        foreach (var entry in ArrayIndex.Entries)
        {
            entry.SchemaIndex = i;
            i++;
            BufferContext = entry.Context;
            switch (entry.DataTypeCURIE)
            {
                case "MS:1000523":
                    {
                        DataTypes.Add(new DoubleType());
                        Arrays.Add(new DoubleArray.Builder());
                        break;
                    }
                case "MS:1000521":
                    {
                        DataTypes.Add(new FloatType());
                        Arrays.Add(new FloatArray.Builder());
                        break;
                    }
                case "MS:1000519":
                    {
                        DataTypes.Add(new Int32Type());
                        Arrays.Add(new Int32Array.Builder());
                        break;
                    }
                case "MS:1000522":
                    {
                        DataTypes.Add(new Int64Type());
                        Arrays.Add(new Int64Array.Builder());
                        break;
                    }
                default:
                    {
                        throw new NotImplementedException(entry.DataTypeCURIE);
                    }
            }
        }
    }

    protected Dictionary<ArrayIndexEntry, Array> RemoveZeroIntensityRuns(Dictionary<ArrayIndexEntry, Array> arrays, IArrowArray intensityArrayVal)
    {
        switch (intensityArrayVal.Data.DataType.TypeId)
        {
            case ArrowTypeId.Float:
                {
                    var indices = ZeroRunRemoval.WhereNotZeroRun((FloatArray)intensityArrayVal);
                    arrays = ComputeFn.Take(arrays, indices);
                    break;
                }
            case ArrowTypeId.Double:
                {
                    var indices = ZeroRunRemoval.WhereNotZeroRun((DoubleArray)intensityArrayVal);
                    arrays = ComputeFn.Take(arrays, indices);
                    break;
                }
            case ArrowTypeId.Int8:
                {
                    var indices = ZeroRunRemoval.WhereNotZeroRun((Int8Array)intensityArrayVal);
                    arrays = ComputeFn.Take(arrays, indices);
                    break;
                }
            case ArrowTypeId.Int16:
                {
                    var indices = ZeroRunRemoval.WhereNotZeroRun((Int16Array)intensityArrayVal);
                    arrays = ComputeFn.Take(arrays, indices);
                    break;
                }
            case ArrowTypeId.Int32:
                {
                    var indices = ZeroRunRemoval.WhereNotZeroRun((Int32Array)intensityArrayVal);
                    arrays = ComputeFn.Take(arrays, indices);
                    break;
                }
            case ArrowTypeId.Int64:
                {
                    var indices = ZeroRunRemoval.WhereNotZeroRun((Int64Array)intensityArrayVal);
                    arrays = ComputeFn.Take(arrays, indices);
                    break;
                }
            default:
                throw new NotImplementedException();
        }
        return arrays;
    }

    protected Dictionary<ArrayIndexEntry, Array> MarkNulls(Dictionary<ArrayIndexEntry, Array> arrays, ArrayIndexEntry intensityEntry, ArrayIndexEntry coordinateEntry)
    {
        BooleanArray mask;
        var weights = arrays[intensityEntry];
        switch (weights.Data.DataType.TypeId)
        {
            case ArrowTypeId.Float:
                mask = ComputeFn.Invert(ZeroRunRemoval.IsZeroPairMask((FloatArray)weights));
                arrays[intensityEntry] = ComputeFn.NullifyAt((FloatArray)weights, mask);
                break;
            case ArrowTypeId.Double:
                mask = ComputeFn.Invert(ZeroRunRemoval.IsZeroPairMask((DoubleArray)weights));
                arrays[intensityEntry] = ComputeFn.NullifyAt((DoubleArray)weights, mask);
                break;
            case ArrowTypeId.Int32:
                mask = ComputeFn.Invert(ZeroRunRemoval.IsZeroPairMask((Int32Array)weights));
                arrays[intensityEntry] = ComputeFn.NullifyAt((Int32Array)weights, mask);
                break;
            case ArrowTypeId.Int64:
                mask = ComputeFn.Invert(ZeroRunRemoval.IsZeroPairMask((Int64Array)weights));
                arrays[intensityEntry] = ComputeFn.NullifyAt((Int64Array)weights, mask);
                break;
            default:
                {
                    var v = ComputeFn.CastFloat(weights);
                    mask = ComputeFn.Invert(ZeroRunRemoval.IsZeroPairMask(v));
                    arrays[intensityEntry] = ComputeFn.NullifyAt(v, mask);
                    break;
                }
        }

        var coordinatesToNull = arrays[coordinateEntry];
        if (coordinatesToNull.Data.DataType.TypeId == ArrowTypeId.Float)
        {
            arrays[coordinateEntry] = ComputeFn.NullifyAt((FloatArray)coordinatesToNull, mask);
        }
        else if (coordinatesToNull.Data.DataType.TypeId == ArrowTypeId.Double)
        {
            arrays[coordinateEntry] = ComputeFn.NullifyAt((DoubleArray)coordinatesToNull, mask);
        }
        else throw new InvalidDataException($"Unsupported data type {coordinatesToNull.Data.DataType.Name}");
        return arrays;
    }

    protected record _ArrayFilterResult(
        Dictionary<ArrayIndexEntry, Array> arrays,
        List<(ArrayIndexEntry, Array)> notCoveredArrays,
        ArrayIndexEntry? nullInterpolate = null,
        ArrayIndexEntry? nullZero = null,
        ArrayIndexEntry? intensityArray = null
    )
    { }

    /// <summary>
    /// Filter a collection of arrays down prior to preprocessing them, separating out arrays not covered
    /// by the schema and explicitly capture null interpolating and zeroing arrays, plus the intensity array
    /// if any are present.
    /// </summary>
    /// <param name="arrays"></param>
    /// <returns></returns>
    protected virtual _ArrayFilterResult FilterArrays(Dictionary<ArrayIndexEntry, Array> arrays)
    {
        List<(ArrayIndexEntry, Array)> notCoveredArrays = new();
        ArrayIndexEntry? nullInterpolate = null;
        ArrayIndexEntry? nullZero = null;
        ArrayIndexEntry? intensityArray = null;
        foreach (var col in arrays)
        {
            if (!ArrayIndex.Entries.Contains(col.Key))
            {
                notCoveredArrays.Add((col.Key, col.Value));
                continue;
            }
            if (col.Key.ArrayTypeCURIE == ArrayType.IntensityArray.CURIE()) intensityArray = col.Key;
            if (col.Key.Transform == NullInterpolation.NullInterpolateCURIE) nullInterpolate = col.Key;
            else if (col.Key.Transform == NullInterpolation.NullZeroCURIE) nullZero = col.Key;
        }
        return new _ArrayFilterResult(arrays, notCoveredArrays, nullInterpolate, nullZero, intensityArray);
    }

    /// <summary>
    /// Preprocess a collection of arrays, applying any signal transformations and separating out unmapped arrays.
    /// </summary>
    /// <param name="entryIndex">The index in the metadata collection these rows refer to</param>
    /// <param name="arrays">The columns to write and their values. All other columns in will be padded with <c>null</c></param>
    /// <param name="isProfile"> Whether the signal being written is a continuous profile or discrete centroids. If the writer is configured to remove zero intensity runs and/or use null marking, this behavior will only trigger on profile data.
    /// </param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public (Dictionary<ArrayIndexEntry, Array>, SpacingInterpolationModel<double>?, List<AuxiliaryArray>) Preprocess(ulong entryIndex, Dictionary<ArrayIndexEntry, Array> arrays, bool? isProfile = null)
    {
        SpacingInterpolationModel<double>? deltaModel = null;
        List<AuxiliaryArray> auxiliaryArrays = [];

        (arrays, var notCoveredArrays, var nullInterpolate, var nullZero, var intensityArray) = FilterArrays(arrays);

        foreach (var (k, v) in notCoveredArrays)
        {
            arrays.Remove(k);
            auxiliaryArrays.Add(AuxiliaryArray.FromValues(v, k));
        }

        if (intensityArray != null && ShouldRemoveZeroRuns && (isProfile ?? true))
        {
            var intensityArrayVal = arrays[intensityArray];
            arrays = RemoveZeroIntensityRuns(arrays, intensityArrayVal);
        }

        if (isProfile ?? false)
        {
            if (nullInterpolate != null && nullZero != null)
            {
                var coordinates = ComputeFn.CastDouble(arrays[nullInterpolate]);
                var weights = arrays[nullZero];
                deltaModel = SpacingInterpolationModel<double>.Fit(coordinates, weights);
                arrays = MarkNulls(arrays, nullZero, nullInterpolate);
            }
            else if (nullInterpolate != null || nullZero != null) throw new InvalidOperationException();
        }

        return (arrays, deltaModel, auxiliaryArrays);
    }

    public abstract EntryDerivedMetadata Add(ulong entryIndex, Dictionary<ArrayIndexEntry, Array> arrays, bool? isProfile = null);
    public abstract EntryDerivedMetadata Add(ulong entryIndex, IEnumerable<Array> arrays, bool? isProfile = null);
    public EntryDerivedMetadata Add(ulong entryIndex, IEnumerable<IArrowArray> arrays, bool? isProfile = null)
    {
        return Add(entryIndex, arrays.Select(a => (Array)a), isProfile);
    }

    public abstract RecordBatch GetRecordBatch();

    public virtual Schema ArrowSchema()
    {
        List<Field> fields = [new Field(BufferContext.IndexName(), new UInt64Type(), true)];

        foreach (var entry in ArrayIndex.Entries)
        {
            var name = entry.CreateColumnName();
            fields.Add(new Field(name, entry.GetArrowType(), true));
        }
        var root = new Field(LayoutName(), new StructType(fields), true);
        Dictionary<string, string> meta = new();
        meta[$"{BufferContext.Name()}_array_index"] = JsonSerializer.Serialize(ArrayIndex);
        return new Schema([root], meta);
    }

    protected void AppendNullsTo(IArrowArrayBuilder builder, IArrowType dtype, int k)
    {
        switch (dtype.TypeId)
        {
            case ArrowTypeId.Double:
                {
                    for (var j = 0; j < k; j++)
                        ((DoubleArray.Builder)builder).AppendNull();
                    break;
                }
            case ArrowTypeId.Float:
                {
                    for (var j = 0; j < k; j++)
                        ((FloatArray.Builder)builder).AppendNull();
                    break;
                }
            case ArrowTypeId.Int8:
                {
                    for (var j = 0; j < k; j++)
                        ((Int8Array.Builder)builder).AppendNull();
                    break;
                }
            case ArrowTypeId.Int16:
                {
                    for (var j = 0; j < k; j++)
                        ((Int16Array.Builder)builder).AppendNull();
                    break;
                }
            case ArrowTypeId.Int32:
                {
                    for (var j = 0; j < k; j++)
                        ((Int32Array.Builder)builder).AppendNull();
                    break;
                }
            case ArrowTypeId.Int64:
                {
                    for (var j = 0; j < k; j++)
                        ((Int64Array.Builder)builder).AppendNull();
                    break;
                }
            default: throw new NotImplementedException();
        }
    }

    protected void AppendArrayTo(IArrowArrayBuilder builder, IArrowType dtype, IArrowArray array)
    {
        switch (dtype.TypeId)
        {
            case ArrowTypeId.Double:
                {
                    DoubleArray valArray = ComputeFn.CastDouble(array);
                    DoubleArray.Builder valBuilder = (DoubleArray.Builder)builder;
                    foreach (var v in valArray) valBuilder.Append(v);
                    break;
                }
            case ArrowTypeId.Float:
                {
                    FloatArray valArray = ComputeFn.CastFloat(array);
                    FloatArray.Builder valBuilder = (FloatArray.Builder)builder;
                    foreach (var v in valArray) valBuilder.Append(v);
                    break;
                }
            case ArrowTypeId.Int8:
                {
                    Int8Array valArray = (Int8Array)array;
                    Int8Array.Builder valBuilder = (Int8Array.Builder)builder;
                    foreach (var v in valArray) valBuilder.Append(v);
                    break;
                }
            case ArrowTypeId.Int16:
                {
                    Int16Array valArray = (Int16Array)array;
                    Int16Array.Builder valBuilder = (Int16Array.Builder)builder;
                    foreach (var v in valArray) valBuilder.Append(v);
                    break;
                }
            case ArrowTypeId.Int32:
                {
                    Int32Array valArray = ComputeFn.CastInt32(array);
                    Int32Array.Builder valBuilder = (Int32Array.Builder)builder;
                    foreach (var v in valArray) valBuilder.Append(v);
                    break;
                }
            case ArrowTypeId.Int64:
                {
                    Int64Array valArray = ComputeFn.CastInt64(array);
                    Int64Array.Builder valBuilder = (Int64Array.Builder)builder;
                    foreach (var v in valArray) valBuilder.Append(v);
                    break;
                }
            default: throw new NotImplementedException();
        }
    }
}


public class PointLayoutBuilder : BaseDataLayoutWriter
{
    public PointLayoutBuilder(ArrayIndex arrayIndex) : base(arrayIndex) { }

    public override string LayoutName()
    {
        return "point";
    }

    public override EntryDerivedMetadata Add(ulong entryIndex, Dictionary<ArrayIndexEntry, Array> arrays, bool? isProfile = null)
    {
        (arrays, var deltaModel, var auxiliaryArrays) = Preprocess(entryIndex, arrays, isProfile);

        int k = 0;
        foreach (var val in arrays.Values)
        {
            if (k > 0 && k != val.Length) throw new InvalidDataException("Arrays do not have equal lengths");
            else k = val.Length;
        }
        Index.AppendRange(Enumerable.Repeat(entryIndex, k));
        foreach (var entry in ArrayIndex.Entries)
        {
            if (entry.SchemaIndex == null) throw new InvalidOperationException("Cannot be null");
            Array? array;
            if (arrays.TryGetValue(entry, out array))
            {
                var builder = Arrays[(int)entry.SchemaIndex - 1];
                var dtype = DataTypes[(int)entry.SchemaIndex - 1];
                AppendArrayTo(builder, dtype, array);
            }
            else
            {
                var builder = Arrays[(int)entry.SchemaIndex - 1];
                var dtype = DataTypes[(int)entry.SchemaIndex - 1];
                AppendNullsTo(builder, dtype, k);
            }
        }
        NumberOfPoints += (ulong)k;

        var ent = new EntryDerivedMetadata(
            deltaModel,
            auxiliaryArrays,
            (isProfile ?? false) ? k : null,
            (isProfile ?? false) ? null : k
        );
        return ent;
    }

    public override EntryDerivedMetadata Add(ulong entryIndex, IEnumerable<Array> arrays, bool? isProfile = null)
    {
        var kvs = ArrayIndex.Entries.Zip(arrays).ToDictionary();
        return Add(entryIndex, kvs, isProfile);
    }

    public void Clear()
    {
        Index.Clear();
        foreach (var (dtype, builder) in DataTypes.Zip(Arrays))
        {
            switch (dtype.TypeId)
            {
                case ArrowTypeId.Double:
                    {
                        var builderOf = (DoubleArray.Builder)builder;
                        builderOf.Clear();
                        break;
                    }
                case ArrowTypeId.Float:
                    {
                        var builderOf = (FloatArray.Builder)builder;
                        builderOf.Clear();
                        break;
                    }
                case ArrowTypeId.Int8:
                    {
                        var builderOf = (Int8Array.Builder)builder;
                        builderOf.Clear();
                        break;
                    }
                case ArrowTypeId.Int16:
                    {
                        var builderOf = (Int16Array.Builder)builder;
                        builderOf.Clear();
                        break;
                    }
                case ArrowTypeId.Int32:
                    {
                        var builderOf = (Int32Array.Builder)builder;
                        builderOf.Clear();
                        break;
                    }
                case ArrowTypeId.Int64:
                    {
                        var builderOf = (Int64Array.Builder)builder;
                        builderOf.Clear();
                        break;
                    }
                default: throw new NotImplementedException();
            }
        }
    }

    public override RecordBatch GetRecordBatch()
    {
        List<Array> cols = [Index.Build()];
        foreach (var (dtype, builder) in DataTypes.Zip(Arrays))
        {
            switch (dtype.TypeId)
            {
                case ArrowTypeId.Double:
                    {
                        var builderOf = (DoubleArray.Builder)builder;
                        cols.Add(builderOf.Build());
                        builderOf.Clear();
                        break;
                    }
                case ArrowTypeId.Float:
                    {
                        var builderOf = (FloatArray.Builder)builder;
                        cols.Add(builderOf.Build());
                        builderOf.Clear();
                        break;
                    }
                case ArrowTypeId.Int8:
                    {
                        var builderOf = (Int8Array.Builder)builder;
                        cols.Add(builderOf.Build());
                        builderOf.Clear();
                        break;
                    }
                case ArrowTypeId.Int16:
                    {
                        var builderOf = (Int16Array.Builder)builder;
                        cols.Add(builderOf.Build());
                        builderOf.Clear();
                        break;
                    }
                case ArrowTypeId.Int32:
                    {
                        var builderOf = (Int32Array.Builder)builder;
                        cols.Add(builderOf.Build());
                        builderOf.Clear();
                        break;
                    }
                case ArrowTypeId.Int64:
                    {
                        var builderOf = (Int64Array.Builder)builder;
                        cols.Add(builderOf.Build());
                        builderOf.Clear();
                        break;
                    }
                default: throw new NotImplementedException();
            }
        }

        var schema = ArrowSchema();
        var dtypeOf = schema.GetFieldByIndex(0).DataType;
        var layer = new StructArray(dtypeOf, Index.Length, cols, cols[0].NullBitmapBuffer, cols[0].NullCount);
        Index.Clear();

        return new RecordBatch(schema, [layer], layer.Length);
    }
}


public class ChunkLayoutBuilder : BaseDataLayoutWriter
{
    public string DefaultMainAxisEncodingCURIE { get; set; }

    public string CurrentMainAxisEncodingCURIE { get; set; }

    public double ChunkSize { get; set; } = 50.0;
    public ArrayIndexEntry MainAxisEntry { get; set; }

    int MainAxisBuilderIdx;
    int StartValueBuilderIdx;
    int EndValueBuilderIdx;
    int EncodingBuilderIdx;

    public ChunkLayoutBuilder(ArrayIndex arrayIndex, string mainAxisEncodingCURIE = DeltaCodec.CURIE, double chunkSize = 50.0) : base(arrayIndex)
    {
        DefaultMainAxisEncodingCURIE = mainAxisEncodingCURIE;
        CurrentMainAxisEncodingCURIE = DefaultMainAxisEncodingCURIE;
        ChunkSize = chunkSize;
        MainAxisEntry = arrayIndex.Entries.Find(
            entry => entry.BufferFormat == BufferFormat.ChunkValues) ?? throw new InvalidDataException(
            $"No main axis array found in {BufferContext} array index");

    }

    protected override void InitializeBuilders()
    {
        int i = 1;
        foreach (var entry in ArrayIndex.Entries)
        {
            entry.SchemaIndex = i;
            i++;
            BufferContext = entry.Context;
            switch (entry.BufferFormat)
            {
                case BufferFormat.ChunkEncoding:
                    {
                        DataTypes.Add(new StringType());
                        Arrays.Add(new StringArray.Builder());
                        break;
                    }
                case BufferFormat.ChunkStart:
                case BufferFormat.ChunkEnd:
                    {
                        switch (entry.DataTypeCURIE)
                        {
                            case "MS:1000523":
                                {
                                    DataTypes.Add(new DoubleType());
                                    Arrays.Add(new DoubleArray.Builder());
                                    break;
                                }
                            case "MS:1000521":
                                {
                                    DataTypes.Add(new FloatType());
                                    Arrays.Add(new FloatArray.Builder());
                                    break;
                                }
                            default: throw new NotImplementedException($"{entry.DataTypeCURIE} not supported for {entry.BufferFormat}");
                        }
                        break;
                    }
                case BufferFormat.ChunkSecondary:
                case BufferFormat.ChunkValues:
                    {
                        DataTypes.Add(entry.GetArrowType());
                        Arrays.Add(new ListArray.Builder(entry.GetArrowType()).Append());

                        break;
                    }
                case BufferFormat.ChunkTransform:
                    {
                        DataTypes.Add(new UInt8Type());
                        Arrays.Add(new ListArray.Builder(new UInt8Type()));
                        break;
                    }
                default: throw new InvalidDataException($"{entry.BufferFormat} is not supported");
            }
        }

        foreach (var entry in ArrayIndex.Entries)
        {
            var idx = (entry.SchemaIndex ?? throw new InvalidOperationException()) - 1;
            if (entry.BufferFormat == BufferFormat.ChunkValues)
                MainAxisBuilderIdx = idx;
            else if (entry.BufferFormat == BufferFormat.ChunkEncoding)
                EncodingBuilderIdx = idx;
            else if (entry.BufferFormat == BufferFormat.ChunkStart)
                StartValueBuilderIdx = idx;
            else if (entry.BufferFormat == BufferFormat.ChunkEnd)
                EndValueBuilderIdx = idx;
        }
    }

    protected override _ArrayFilterResult FilterArrays(Dictionary<ArrayIndexEntry, Array> arrays)
    {
        List<(ArrayIndexEntry, Array)> notCoveredArrays = new();
        ArrayIndexEntry? nullInterpolate = null;
        ArrayIndexEntry? nullZero = null;
        ArrayIndexEntry? intensityArray = null;
        foreach (var col in arrays)
        {
            if (!ArrayIndex.Entries.Contains(col.Key))
            {
                notCoveredArrays.Add((col.Key, col.Value));
                continue;
            }
            if (col.Key.ArrayTypeCURIE == ArrayType.IntensityArray.CURIE()) intensityArray = col.Key;
            if (col.Key.Transform == NullInterpolation.NullInterpolateCURIE) nullInterpolate = col.Key;
            else if (col.Key.Transform == NullInterpolation.NullZeroCURIE) nullZero = col.Key;
        }
        return new _ArrayFilterResult(arrays, notCoveredArrays, nullInterpolate, nullZero, intensityArray);
    }

    public override EntryDerivedMetadata Add(ulong entryIndex, Dictionary<ArrayIndexEntry, Array> arrays, bool? isProfile = null)
    {
        (arrays, var deltaModel, var auxiliaryArrays) = Preprocess(entryIndex, arrays, isProfile);

        if (isProfile != null && (bool)isProfile)
        {
            CurrentMainAxisEncodingCURIE = DefaultMainAxisEncodingCURIE;
        }
        else if (isProfile != null && !(bool)isProfile)
        {
            CurrentMainAxisEncodingCURIE = NoCompressionCodec.CURIE;
        }
        var mainAxis = arrays[MainAxisEntry];

        var spans = Chunking.ChunkEvery(mainAxis, ChunkSize);

        foreach (var val in arrays.Values)
        {
            if (mainAxis.Length != val.Length) throw new InvalidDataException("Arrays do not have equal lengths");
        }

        var isTracing = Logger?.IsEnabled(LogLevel.Trace) ?? false;

        if (isTracing)
            Logger?.LogTrace($"{mainAxis.Length} points to be written for {entryIndex}");

        HashSet<int> visited = new()
        {
            MainAxisBuilderIdx,
            EncodingBuilderIdx,
            StartValueBuilderIdx,
            EndValueBuilderIdx
        };
        var mainAxisBuilder = (ListArray.Builder)Arrays[MainAxisBuilderIdx];
        var startValBuilder = Arrays[StartValueBuilderIdx];
        var endValBuilder = Arrays[EndValueBuilderIdx];
        var steps = 0;
        var beforeWrote = mainAxisBuilder.Length;
        foreach (var (startIdx, endIdx) in spans)
        {
            var chunk = mainAxis.Slice(startIdx, endIdx - startIdx);
            steps += endIdx - startIdx;
            var startVal = ComputeFn.Min(chunk, NullHandling.Skip);
            var endVal = ComputeFn.Max(chunk, NullHandling.Skip);
            if (isTracing) Logger?.LogTrace($"Range {startIdx}-{endIdx} has startVal {startVal}-{endVal}");
            if (startVal == null) {
                if (isTracing)
                    Logger?.LogTrace($"Skipping Range {startIdx}-{endIdx}");
                continue;
            };
            Index.Append(entryIndex);

            switch (DataTypes[StartValueBuilderIdx].TypeId)
            {
                case ArrowTypeId.Float:
                    {
                        ((FloatArray.Builder)startValBuilder).Append((float?)startVal);
                        break;
                    }
                case ArrowTypeId.Double:
                    {
                        ((DoubleArray.Builder)startValBuilder).Append(startVal);
                        break;
                    }
                default: throw new NotImplementedException($"{chunk.Data.DataType.Name}");
            }
            switch (DataTypes[EndValueBuilderIdx].TypeId)
            {
                case ArrowTypeId.Float:
                    {
                        ((FloatArray.Builder)endValBuilder).Append((float?)endVal);
                        break;
                    }
                case ArrowTypeId.Double:
                    {
                        ((DoubleArray.Builder)endValBuilder).Append(endVal);
                        break;
                    }
                default: throw new NotImplementedException($"{chunk.Data.DataType.Name}");
            }

            if (isTracing) Logger?.LogTrace($"{entryIndex} {startVal}-{endVal} has {chunk.Length} items");
            if (CurrentMainAxisEncodingCURIE == DeltaCodec.CURIE)
            {
                ((StringArray.Builder)Arrays[EncodingBuilderIdx]).Append(DeltaCodec.CURIE);
                switch (MainAxisEntry.GetArrowType().TypeId)
                {
                    case ArrowTypeId.Double:
                        {
                            var builder = (DoubleArray.Builder)mainAxisBuilder.ValueBuilder;
                            DeltaCodec.Encode(startVal, ComputeFn.CastDouble(chunk.Slice(1, chunk.Length - 1)), builder);
                            mainAxisBuilder.Append();
                            break;
                        }
                    case ArrowTypeId.Float:
                        {
                            var builder = (FloatArray.Builder)mainAxisBuilder.ValueBuilder;
                            DeltaCodec.Encode((float?)startVal, ComputeFn.CastFloat(chunk.Slice(1, chunk.Length - 1)), builder);
                            mainAxisBuilder.Append();
                            break;
                        }
                    default: throw new NotImplementedException($"{chunk.Data.DataType.Name}");
                }
            }
            else if (CurrentMainAxisEncodingCURIE == NoCompressionCodec.CURIE)
            {
                ((StringArray.Builder)Arrays[EncodingBuilderIdx]).Append(NoCompressionCodec.CURIE);
                switch (MainAxisEntry.GetArrowType().TypeId)
                {
                    case ArrowTypeId.Double:
                        {
                            var builder = (DoubleArray.Builder)mainAxisBuilder.ValueBuilder;
                            NoCompressionCodec.Encode((double)startVal, ComputeFn.CastDouble(chunk.Slice(1, chunk.Length - 1)), builder);
                            mainAxisBuilder.Append();
                            break;
                        }
                    case ArrowTypeId.Float:
                        {
                            var builder = (FloatArray.Builder)mainAxisBuilder.ValueBuilder;
                            NoCompressionCodec.Encode((float)startVal, ComputeFn.CastFloat(chunk.Slice(1, chunk.Length - 1)), builder);
                            mainAxisBuilder.Append();
                            break;
                        }
                    default: throw new NotImplementedException($"{chunk.Data.DataType.Name}");
                }
            }
            else throw new NotImplementedException(CurrentMainAxisEncodingCURIE);

            foreach (var entry in ArrayIndex.Entries)
            {
                if (entry.SchemaIndex == null) throw new InvalidOperationException("Cannot be null");
                if (visited.Contains((int)entry.SchemaIndex - 1)) continue;
                Array? array;
                if (arrays.TryGetValue(entry, out array))
                {
                    var arrayChunk = array.Slice(startIdx, endIdx - startIdx);
                    var builder = (ListArray.Builder)Arrays[(int)entry.SchemaIndex - 1];
                    var dtype = DataTypes[(int)entry.SchemaIndex - 1];
                    AppendArrayTo(builder.ValueBuilder, dtype, arrayChunk);
                    builder.Append();
                }
                else
                {
                    if (isTracing) Logger?.LogTrace($"{entry.Path} not in entry {entryIndex}");
                    var builder = (ListArray.Builder)Arrays[(int)entry.SchemaIndex - 1];
                    builder.Append();
                }
            }
        }
        if (isTracing)
            Logger?.LogTrace($"Wrote {steps} data points over {mainAxisBuilder.Length - beforeWrote} blocks for {entryIndex}");
        NumberOfPoints += (ulong)mainAxis.Length;
        CurrentMainAxisEncodingCURIE = DefaultMainAxisEncodingCURIE;
        CheckAllColumnsAligned();
        var ent = new EntryDerivedMetadata(
           deltaModel,
           auxiliaryArrays,
           (isProfile ?? false) ? steps : null,
           (isProfile ?? false) ? null : steps
       );
        return ent;
    }

    public override EntryDerivedMetadata Add(ulong entryIndex, IEnumerable<Array> arrays, bool? isProfile = null)
    {
        var kvs = ArrayIndex.Entries.Where(e => e.BufferFormat switch
        {
            BufferFormat.ChunkSecondary => true,
            BufferFormat.ChunkValues => true,
            _ => false,
        }).Zip(arrays).ToDictionary();
        return Add(entryIndex, kvs, isProfile);
    }

    public override Schema ArrowSchema()
    {
        List<Field> fields = [new Field(BufferContext.IndexName(), new UInt64Type(), true)];

        foreach (var entry in ArrayIndex.Entries)
        {
            var name = entry.Path.Split(".").Last();
            switch (entry.BufferFormat)
            {
                case BufferFormat.ChunkEncoding:
                    {
                        fields.Add(new Field(name, new StringType(), true));
                        break;
                    }
                case BufferFormat.ChunkStart:
                case BufferFormat.ChunkEnd:
                    {
                        fields.Add(new Field(name, entry.GetArrowType(), true));
                        break;
                    }
                case BufferFormat.ChunkSecondary:
                case BufferFormat.ChunkValues:
                    {
                        fields.Add(new Field(name, new ListType(entry.GetArrowType()), true));
                        break;
                    }
                case BufferFormat.ChunkTransform:
                    {
                        fields.Add(new Field(name, new ListType(new UInt8Type()), true));
                        break;
                    }
                default: throw new InvalidDataException($"{entry.BufferFormat} is not supported");
            }
        }
        var root = new Field(LayoutName(), new StructType(fields), true);
        Dictionary<string, string> meta = new();
        meta[$"{BufferContext.Name()}_array_index"] = JsonSerializer.Serialize(ArrayIndex);
        return new Schema([root], meta);
    }

    public override RecordBatch GetRecordBatch()
    {
        List<Array> cols = [Index.Build()];
        var n = cols[0].Length;
        foreach(var c in cols)
        {
            if (c.Length != n) throw new InvalidOperationException($"Not all columns have {n} items");
        }

        foreach (var (entry, (dtype, builder)) in ArrayIndex.Entries.Zip(DataTypes.Zip(Arrays)))
        {
            switch (entry.BufferFormat)
            {
                case BufferFormat.ChunkEncoding:
                    {
                        cols.Add(((StringArray.Builder)builder).Build());
                        break;
                    }
                case BufferFormat.ChunkStart:
                case BufferFormat.ChunkEnd:
                    {
                        switch (dtype.TypeId)
                        {
                            case ArrowTypeId.Double:
                                {
                                    var builderOf = (DoubleArray.Builder)builder;
                                    cols.Add(builderOf.Build());
                                    break;
                                }
                            case ArrowTypeId.Float:
                                {
                                    var builderOf = (FloatArray.Builder)builder;
                                    cols.Add(builderOf.Build());
                                    break;
                                }
                            default: throw new InvalidDataException($"{dtype.Name} is not supported as a chunk boundary");
                        }
                        break;
                    }
                case BufferFormat.ChunkSecondary:
                case BufferFormat.ChunkValues:
                case BufferFormat.ChunkTransform:
                    {
                        cols.Add(((ListArray.Builder)builder).Build());
                        break;
                    }
                default: throw new InvalidDataException($"{entry.BufferFormat} is not supported");
            }
        }

        var schema = ArrowSchema();
        var dtypeOf = schema.GetFieldByIndex(0).DataType;
        var layer = new StructArray(dtypeOf, n, cols, cols[0].NullBitmapBuffer, cols[0].NullCount);
        Clear();
        CheckAllColumnsAligned();
        return new RecordBatch(schema, [layer], layer.Length);
    }

    public void Clear()
    {
        Index.Clear();
        foreach (var (entry, (dtype, builder)) in ArrayIndex.Entries.Zip(DataTypes.Zip(Arrays)))
        {
            switch (entry.BufferFormat)
            {
                case BufferFormat.ChunkEncoding:
                    {
                        ((StringArray.Builder)builder).Clear();
                        break;
                    }
                case BufferFormat.ChunkStart:
                case BufferFormat.ChunkEnd:
                    {
                        switch (dtype.TypeId)
                        {
                            case ArrowTypeId.Double:
                                {
                                    var builderOf = (DoubleArray.Builder)builder;
                                    builderOf.Clear();
                                    break;
                                }
                            case ArrowTypeId.Float:
                                {
                                    var builderOf = (FloatArray.Builder)builder;
                                    builderOf.Clear();
                                    break;
                                }
                            default: throw new InvalidDataException($"{dtype.Name} is not supported as a chunk boundary");
                        }
                        break;
                    }
                case BufferFormat.ChunkSecondary:
                case BufferFormat.ChunkValues:
                case BufferFormat.ChunkTransform:
                    {
                        ((ListArray.Builder)builder).Clear();
                        ((ListArray.Builder)builder).Append();
                        break;
                    }
                default: throw new InvalidDataException($"{entry.BufferFormat} is not supported");
            }
        }
    }

    protected void CheckAllColumnsAligned()
    {
        var n = Index.Length;
        foreach (var (entry, (dtype, builder)) in ArrayIndex.Entries.Zip(DataTypes.Zip(Arrays)))
        {
            var valid = true;
            switch (entry.BufferFormat) {
                case BufferFormat.ChunkTransform:
                case BufferFormat.ChunkSecondary:
                case BufferFormat.ChunkValues:
                    {
                        valid = (builder.Length - 1) == n;
                        break;
                    }
                default:
                    {
                        valid = builder.Length == n;
                        break;
                    }
            };
            if (!valid)
            {
                throw new InvalidDataException($"{entry.Path} of type {dtype}/{entry.BufferFormat} had {builder.Length} elements, expected {n} elements");
            }
        }
    }

    public override string LayoutName()
    {
        return "chunk";
    }
}