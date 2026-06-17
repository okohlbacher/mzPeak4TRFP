namespace MZPeak.Metadata;

using Apache.Arrow;
using Apache.Arrow.Types;
using MZPeak.Compute;
using MZPeak.ControlledVocabulary;
using MZPeak.Reader.Visitors;
using MZPeak.Storage;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// Specifies the layout format for data buffers in the storage layer.
/// </summary>
[JsonConverter(typeof(BufferFormatConverter))]
public enum BufferFormat
{
    Point,
    ChunkValues,
    ChunkStart,
    ChunkEnd,
    ChunkEncoding,
    ChunkSecondary,
    ChunkTransform,
}

/// <summary>
/// JSON converter for <see cref="BufferFormat"/> serialization.
/// </summary>
public class BufferFormatConverter : JsonConverter<BufferFormat>
{
    public override BufferFormat Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (s == null) throw new JsonException("Null string");
        switch (s)
        {
            case "point": return BufferFormat.Point;
            case "chunk_values": return BufferFormat.ChunkValues;
            case "chunk_start": return BufferFormat.ChunkStart;
            case "chunk_end": return BufferFormat.ChunkEnd;
            case "chunk_encoding": return BufferFormat.ChunkEncoding;
            case "secondary_chunk":
            case "chunk_secondary": return BufferFormat.ChunkSecondary;
            case "chunk_transform": return BufferFormat.ChunkTransform;
            default: throw new JsonException($"{s} is not a recognized buffer format");
        }
    }

    public override void Write(Utf8JsonWriter writer, BufferFormat value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case BufferFormat.Point:
                {
                    writer.WriteStringValue("point");
                    break;
                }
            case BufferFormat.ChunkValues:
                {
                    writer.WriteStringValue("chunk_values");
                    break;
                }
            case BufferFormat.ChunkStart:
                {
                    writer.WriteStringValue("chunk_start");
                    break;
                }
            case BufferFormat.ChunkEnd:
                {
                    writer.WriteStringValue("chunk_end");
                    break;
                }
            case BufferFormat.ChunkEncoding:
                {
                    writer.WriteStringValue("chunk_encoding");
                    break;
                }
            case BufferFormat.ChunkSecondary:
                {
                    writer.WriteStringValue("chunk_secondary");
                    break;
                }
            case BufferFormat.ChunkTransform:
                {
                    writer.WriteStringValue("chunk_transform");
                    break;
                }

        }
    }
}

/// <summary>
/// Indicates whether a buffer is the primary or secondary representation of an array type.
/// </summary>
[JsonConverter(typeof(BufferPriorityJsonConverter))]
public enum BufferPriority
{
    Primary,
    Secondary
}

class BufferPriorityJsonConverter : JsonConverter<BufferPriority>
{
    public override BufferPriority Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (s == null) throw new JsonException("Null string");
        s = s.ToLower();
        return s switch
        {
            "primary" => BufferPriority.Primary,
            "secondary" => BufferPriority.Secondary,
            _ => throw new NotImplementedException($"{s} not a recognized BufferPriority")
        };
    }

    public override void Write(Utf8JsonWriter writer, BufferPriority value, JsonSerializerOptions options)
    {
        var text = value switch
        {
            BufferPriority.Primary => "primary",
            BufferPriority.Secondary => "secondary",
            _ => throw new NotImplementedException()
        };
        writer.WriteStringValue(text);
    }
}

/// <summary>
/// The data context that a buffer belongs to (spectrum or chromatogram).
/// </summary>
[JsonConverter(typeof(BufferContextJsonConverter))]
public enum BufferContext
{
    Spectrum,
    Chromatogram,
    WavelengthSpectrum,
}

class BufferContextJsonConverter : JsonConverter<BufferContext>
{
    public override BufferContext Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (s == null) throw new JsonException("Null string");
        s = s.ToLower();
        return s switch
        {
            "spectrum" => BufferContext.Spectrum,
            "chromatogram" => BufferContext.Chromatogram,
            "wavelength_spectrum" => BufferContext.WavelengthSpectrum,
            _ => throw new NotImplementedException($"{s} not a recognized BufferContext")
        };

    }

    public override void Write(Utf8JsonWriter writer, BufferContext value, JsonSerializerOptions options)
    {
        var text = value switch
        {
            BufferContext.Spectrum => "spectrum",
            BufferContext.Chromatogram => "chromatogram",
            BufferContext.WavelengthSpectrum => "wavelength_spectrum",
            _ => throw new NotImplementedException()
        };
        writer.WriteStringValue(text);
    }
}


public static class BufferContexteMethods
{
    public static string IndexName(this BufferContext bufferContext)
    {
        switch (bufferContext)
        {
            case BufferContext.Spectrum:
                {
                    return "spectrum_index";
                }
            case BufferContext.Chromatogram:
                {
                    return "chromatogram_index";
                }
            case BufferContext.WavelengthSpectrum:
                {
                    return "wavelength_spectrum_index";
                }
            default:
                {
                    throw new InvalidOperationException("Cannot create index column name for `Other`");
                }
        }
    }

    public static string Name(this BufferContext bufferContext)
    {
        switch (bufferContext)
        {
            case BufferContext.Spectrum:
                {
                    return "spectrum";
                }
            case BufferContext.WavelengthSpectrum:
                {
                    return "wavelength_spectrum";
                }
            case BufferContext.Chromatogram:
                {
                    return "chromatogram";
                }
            default:
                {
                    throw new InvalidOperationException("Cannot create index column name for `Other`");
                }
        }
    }

    public static ArrayType DefaultPrimaryAxis(this BufferContext bufferContext)
    {
        switch (bufferContext)
        {
            case BufferContext.Spectrum:
                {
                    return ArrayType.MZArray;
                }
            case BufferContext.Chromatogram:
                {
                    return ArrayType.TimeArray;
                }
            case BufferContext.WavelengthSpectrum:
                {
                    return ArrayType.WavelengthArray;
                }
            default: throw new InvalidOperationException("Unknown axis type for `Other`");
        }
    }
}

/// <summary>
/// Describes metadata for a single data array within the storage format.
/// </summary>
public record ArrayIndexEntry : IEquatable<ArrayIndexEntry>
{
    /// <summary>The data context (spectrum or chromatogram).</summary>
    [JsonPropertyName("context")]
    public required BufferContext Context { get; set; }

    /// <summary>The column path within the Parquet schema.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    /// <summary>The CURIE identifying the binary data type (e.g., float32, float64).</summary>
    [JsonPropertyName("data_type")]
    public required string DataTypeCURIE { get; set; }

    /// <summary>The CURIE identifying the array type (e.g., m/z array, intensity array).</summary>
    [JsonPropertyName("array_type")]
    public required string ArrayTypeCURIE { get; set; }

    /// <summary>Human-readable name for the array.</summary>
    [JsonPropertyName("array_name")]
    public required string ArrayName { get; set; }

    /// <summary>Optional CURIE for the unit of measurement.</summary>
    [JsonPropertyName("unit")]
    public string? UnitCURIE { get; set; } = null;

    /// <summary>Optional transform applied to the data (e.g., null interpolation).</summary>
    [JsonPropertyName("transform")]
    public string? Transform { get; set; } = null;

    /// <summary>The storage format for this buffer.</summary>
    [JsonPropertyName("buffer_format")]
    public BufferFormat BufferFormat { get; set; }

    /// <summary>Optional reference to the data processing method used.</summary>
    [JsonPropertyName("data_processing_id")]
    public string? DataProcessesingId { get; set; } = null;

    /// <summary>Whether this is the primary or secondary buffer for the array type.</summary>
    [JsonPropertyName("buffer_priority")]
    public BufferPriority? BufferPriority { get; set; } = null;

    /// <summary>Optional rank for sorting arrays of the same type.</summary>
    [JsonPropertyName("sorting_rank")]
    public uint? SortingRank { get; set; } = null;

    /// <summary>The column index in the Parquet schema (populated at runtime).</summary>
    [JsonIgnore]
    public int? SchemaIndex { get; set; } = null;

    /// <summary>Gets the array type enum from the CURIE, or null if unknown.</summary>
    public ArrayType? GetArrayType()
    {
        ArrayType v;
        if (ArrayTypeMethods.FromCURIE.TryGetValue(ArrayTypeCURIE, out v)) return v;
        else return null;
    }

    /// <summary>Gets the unit enum from the CURIE, or null if not specified.</summary>
    public Unit? GetUnit()
    {
        if (UnitCURIE == null) return null;
        Unit v;
        if (UnitMethods.FromCURIE.TryGetValue(UnitCURIE, out v)) return v;
        else return null;
    }

    /// <summary>Gets the Apache Arrow type corresponding to the data type CURIE.</summary>
    public ArrowType GetArrowType()
    {
        switch (DataTypeCURIE)
        {
            case "MS:1000521":
                {
                    return new FloatType();
                }
            case "MS:1000519":
                {
                    return new Int32Type();
                }
            case "MS:1000522":
                {
                    return new Int64Type();
                }
            case "MS:1000523":
                {
                    return new DoubleType();
                }
            case "MS:1001479":
                {
                    return new LargeStringType();
                }
            default:
                {
                    throw new InvalidDataException("Cannot map " + DataTypeCURIE + " to an Arrow type");
                }
        }
    }

    /// <summary>Creates a column name based on array metadata.</summary>
    public string CreateColumnName()
    {
        var notAlpha = new Regex("[^A-Za-z_]+");
        var arrayName = notAlpha.Replace(
                ArrayName.Replace("m/z", "mz")
                    .Replace(" array", "")
                    .Trim(),
                "_"
            );
        if (BufferPriority == Metadata.BufferPriority.Primary)
        {
            return arrayName;
        }
        else
        {
            var dtypeName = BinaryDataTypeMethods.FromCURIE[DataTypeCURIE].NameForColumn();
            var unitName = UnitCURIE != null ? UnitMethods.FromCURIE[UnitCURIE].NameForColumn() : null;
            if (unitName != null)
                return string.Join("_", [arrayName, dtypeName, unitName]);
            else
            {
                return string.Join("_", [arrayName, dtypeName]);
            }
        }
    }

    public override int GetHashCode()
    {
        return (ArrayName, DataTypeCURIE, Transform, UnitCURIE).GetHashCode();
    }

    public virtual bool Equals(ArrayIndexEntry? other)
    {
        if (other == null) return false;
        return ArrayName == other.ArrayName &&
             ArrayTypeCURIE == other.ArrayTypeCURIE &&
             DataProcessesingId == other.DataProcessesingId &&
             DataTypeCURIE == other.DataTypeCURIE &&
             Transform == other.Transform &&
             UnitCURIE == other.UnitCURIE;
    }
}

/// <summary>
/// Collection of array metadata entries with a common path prefix.
/// </summary>
public class ArrayIndex
{
    /// <summary>The common path prefix for all entries.</summary>
    [JsonPropertyName("prefix")]
    public string Prefix { get; set; }

    /// <summary>The list of array metadata entries.</summary>
    [JsonPropertyName("entries")]
    public List<ArrayIndexEntry> Entries { get; set; }

    protected Dictionary<string, ArrayIndexEntry> NameCache;

    /// <summary>Creates an empty array index.</summary>
    public ArrayIndex()
    {
        Prefix = "?";
        Entries = new();
        NameCache = new();
    }

    /// <summary>Creates an array index with the specified prefix and entries.</summary>
    /// <param name="prefix">The common path prefix for all entries.</param>
    /// <param name="entries">The list of array metadata entries.</param>
    public ArrayIndex(string prefix, List<ArrayIndexEntry> entries)
    {
        Prefix = prefix;
        Entries = entries;
        NameCache = new();
    }

    public bool HasArrayType(ArrayType arrayType)
    {
        return Entries.Find(entry => entry.GetArrayType() == arrayType) != null;
    }

    public IEnumerable<ArrayIndexEntry> EntriesFor(ArrayType arrayType)
    {
        return Entries.Where(a => a.ArrayTypeCURIE == arrayType.CURIE());
    }

    public ArrayIndexEntry? ArrayTypeFromName(string name)
    {
        if (NameCache.TryGetValue(name, out ArrayIndexEntry? tmpEntry))
        {
            return tmpEntry;
        }
        foreach (var entry in Entries)
        {
            var entryName = entry.Path.Split(".").Last();
            if (entry.BufferFormat == BufferFormat.ChunkValues)
            {
                entryName = entryName.Replace("_chunk_values", "");
            }
            if (entryName == name)
            {
                NameCache[name] = entry;
                return entry;
            }
        }
        return null;
    }

    public BufferFormat? InferBufferFormat()
    {
        if (Entries[0].BufferFormat == BufferFormat.Point)
            return BufferFormat.Point;
        else
        {
            switch (Entries[0].BufferFormat)
            {
                case BufferFormat.ChunkEncoding:
                case BufferFormat.ChunkSecondary:
                case BufferFormat.ChunkEnd:
                case BufferFormat.ChunkStart:
                case BufferFormat.ChunkTransform:
                case BufferFormat.ChunkValues:
                    return BufferFormat.ChunkValues;
            }
        }
        return null;
    }
}


/// <summary>
/// Builder for constructing <see cref="ArrayIndex"/> instances.
/// </summary>
public class ArrayIndexBuilder
{
    string Prefix;
    List<ArrayIndexEntry> Entries;
    BufferContext Context;
    BufferFormat Format;

    internal ArrayIndexBuilder(string prefix, BufferContext context, BufferFormat bufferFormat)
    {
        Prefix = prefix;
        Context = context;
        Entries = new();
        Format = bufferFormat;
    }

    /// <summary>Creates a builder for point-format arrays.</summary>
    /// <param name="context">The buffer context (spectrum or chromatogram).</param>
    public static ArrayIndexBuilder PointBuilder(BufferContext context)
    {
        return new("point", context, BufferFormat.Point);
    }

    /// <summary>Creates a builder for chunk-format arrays.</summary>
    /// <param name="context">The buffer context (spectrum or chromatogram).</param>
    public static ArrayIndexBuilder ChunkBuilder(BufferContext context)
    {
        return new("chunk", context, BufferFormat.ChunkValues);
    }

    /// <summary>Adds an array entry to the builder.</summary>
    /// <param name="arrayType">The type of the array (e.g., m/z, intensity).</param>
    /// <param name="dataType">The binary data type for storage.</param>
    /// <param name="unit">Optional unit of measurement.</param>
    /// <param name="sortingRank">Optional sorting rank.</param>
    /// <param name="transform">Optional transform identifier.</param>
    /// <param name="priority">Optional buffer priority.</param>
    public ArrayIndexBuilder Add(ArrayType arrayType, BinaryDataType dataType, Unit? unit = null, uint? sortingRank = null, string? transform = null, BufferPriority? priority = null)
    {
        var entry = new ArrayIndexEntry()
        {
            ArrayName = arrayType.Name(),
            ArrayTypeCURIE = arrayType.CURIE(),
            Context = Context,
            DataTypeCURIE = dataType.CURIE(),
            UnitCURIE = unit?.CURIE(),
            SchemaIndex = null,
            Path = Prefix,
            SortingRank = sortingRank,
            Transform = transform,
            BufferPriority = priority,
            BufferFormat = BufferFormat.Point,
        };
        entry.Path = $"{Prefix}.{entry.CreateColumnName()}";
        Entries.Add(entry);
        return this;
    }

    public ArrayIndexBuilder Add(ArrayIndexEntry entry)
    {
        entry.Path = $"{Prefix}.{entry.CreateColumnName()}";
        Entries.Add(entry);
        return this;
    }

    /// <summary>Assigns primary priority to the first entry of each array type without explicit priority.</summary>
    public void MarkPriorities()
    {
        Dictionary<ArrayType, int> indexOfFirst = new();
        Dictionary<ArrayType, bool> hadPriority = new();
        for (var i = 0; i < Entries.Count; i++)
        {
            var tp = ArrayTypeMethods.FromCURIE[Entries[i].ArrayTypeCURIE];
            if (Entries[i].BufferPriority == BufferPriority.Primary)
            {
                hadPriority[tp] = true;
            }
            if (!indexOfFirst.ContainsKey(tp) && Entries[i].BufferPriority == null)
            {
                indexOfFirst[tp] = i;
            }
        }
        foreach (var kv in indexOfFirst)
        {
            bool hadPriorityFor;
            if (!hadPriority.TryGetValue(kv.Key, out hadPriorityFor))
            {
                hadPriorityFor = false;
            }
            if (hadPriorityFor) continue;
            Entries[kv.Value].BufferPriority = BufferPriority.Primary;
        }
        foreach (var entry in Entries)
        {
            entry.Path = $"{Prefix}.{entry.CreateColumnName()}";
        }
    }

    bool ApplyChunkedFormat(ArrayType? primaryAxis = null)
    {
        if (Format != BufferFormat.ChunkValues)
            return true;
        if (primaryAxis == null)
        {
            primaryAxis = Context.DefaultPrimaryAxis();
        }
        bool foundMainAxis = false;
        foreach (var entry in Entries.ToList())
        {
            if (entry.GetArrayType() == primaryAxis && entry.BufferPriority == BufferPriority.Primary)
            {
                var newEntry = entry with { BufferFormat = BufferFormat.ChunkStart };
                newEntry.Path = $"{Prefix}.{newEntry.CreateColumnName()}_chunk_start";
                Entries.Add(newEntry);
                newEntry = entry with { BufferFormat = BufferFormat.ChunkEnd };
                newEntry.Path = $"{Prefix}.{newEntry.CreateColumnName()}_chunk_end";
                Entries.Add(newEntry);
                newEntry = entry with { BufferFormat = BufferFormat.ChunkEncoding };
                newEntry.Path = $"{Prefix}.chunk_encoding";
                Entries.Add(newEntry);
                entry.BufferFormat = BufferFormat.ChunkValues;
                entry.Path = $"{entry.Path}_chunk_values";
                foundMainAxis = true;
            }
            else if (entry.Transform != null && entry.Transform != NullInterpolation.NullZeroCURIE)
            {
                throw new NotImplementedException();
            }
            else
            {
                entry.BufferFormat = BufferFormat.ChunkSecondary;
            }
        }
        return foundMainAxis;
    }

    /// <summary>Builds the final <see cref="ArrayIndex"/> instance.</summary>
    public ArrayIndex Build()
    {
        MarkPriorities();
        if (!ApplyChunkedFormat()) throw new InvalidOperationException("Failed to infer which array to orient chunks around");
        return new ArrayIndex(Prefix, Entries);
    }
}


/// <summary>
/// Represents an auxiliary data array with metadata for serialization.
/// </summary>
public class AuxiliaryArray : IHasParameters
{
    /// <summary>Raw byte data of the array.</summary>
    public Memory<byte> Data;
    /// <summary>Parameter describing the array name and type.</summary>
    public Param Name;
    /// <summary>The binary data type of the array elements.</summary>
    public BinaryDataType DataType;
    /// <summary>Compression applied to the data.</summary>
    public Compression Compression;
    /// <summary>Optional unit of measurement.</summary>
    public Unit? Unit;
    /// <summary>Additional parameters describing the array.</summary>
    public List<Param> Parameters;
    /// <summary>The Apache Arrow type for this array.</summary>
    public ArrowType ArrowType => DataType.ArrowType();

    List<Param> IHasParameters.Parameters { get => Parameters; set => Parameters = value; }

    /// <summary>Creates an auxiliary array with the specified data and metadata.</summary>
    /// <param name="data">The raw byte data.</param>
    /// <param name="name">Parameter describing the array name and type.</param>
    /// <param name="dataType">The binary data type.</param>
    /// <param name="unit">Optional unit of measurement.</param>
    /// <param name="compression">Compression method applied to the data.</param>
    /// <param name="parameters">Optional additional parameters.</param>
    public AuxiliaryArray(Memory<byte> data, Param name, BinaryDataType dataType, Unit? unit, Compression compression = Compression.NoCompression, List<Param>? parameters = null)
    {
        Data = data;
        Name = name;
        DataType = dataType;
        Unit = unit;
        Compression = compression;
        Parameters = parameters ?? new();
    }

    /// <summary>Returns a typed view of the array data.</summary>
    public ReadOnlySpan<T> View<T>() where T : struct
    {
        if (Compression == Compression.NoCompression) return Data.Span.CastTo<T>();
        switch (Compression)
        {
            case Compression.NoCompression: return Data.Span.CastTo<T>();
            case Compression.Zlib: throw new NotImplementedException();
            case Compression.Zstd: throw new NotImplementedException();
            default: throw new NotImplementedException();
        }
    }

    /// <summary>Creates an auxiliary array from a list of values.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="values">The list of values to store.</param>
    /// <param name="entry">The array index entry providing metadata.</param>
    public static AuxiliaryArray FromValues<T>(List<T> values, ArrayIndexEntry entry) where T : struct
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values)).ToArray();
        var name = new Param(name: entry.ArrayName, accession: entry.ArrayTypeCURIE, rawValue: null, unit: entry.UnitCURIE);
        var dataType = BinaryDataTypeMethods.FromCURIE[entry.DataTypeCURIE];
        var unit = entry.GetUnit();
        return new AuxiliaryArray(bytes, name, dataType, unit, Compression.NoCompression);
    }

    /// <summary>Creates an auxiliary array from an Arrow array.</summary>
    /// <param name="values">The Arrow array to convert.</param>
    /// <param name="entry">The array index entry providing metadata.</param>
    public static AuxiliaryArray FromValues(IArrowArray values, ArrayIndexEntry entry)
    {
        switch (values.Data.DataType.TypeId)
        {
            case ArrowTypeId.Double:
                return FromValues((DoubleArray)values, entry);
            case ArrowTypeId.Float:
                return FromValues((FloatArray)values, entry);
            case ArrowTypeId.Int32:
                return FromValues((Int32Array)values, entry);
            case ArrowTypeId.Int64:
                return FromValues((Int64Array)values, entry);
            case ArrowTypeId.UInt32:
                return FromValues((UInt32Array)values, entry);
            case ArrowTypeId.UInt64:
                return FromValues((UInt64Array)values, entry);
            case ArrowTypeId.Int16:
                return FromValues((Int16Array)values, entry);
            case ArrowTypeId.Int8:
                return FromValues((Int8Array)values, entry);
            case ArrowTypeId.UInt16:
                return FromValues((UInt16Array)values, entry);
            case ArrowTypeId.UInt8:
                return FromValues((UInt8Array)values, entry);
            case ArrowTypeId.Boolean:
                return FromValues((BooleanArray)values, entry);
            case ArrowTypeId.String:
                return FromValues((StringArray)values, entry);
            default:
                throw new InvalidDataException("Unsupported data type " + values.Data.DataType.Name);
        }
    }

    /// <summary>Creates an auxiliary array from a primitive Arrow array.</summary>
    /// <typeparam name="T">The numeric value type.</typeparam>
    /// <param name="values">The primitive Arrow array to convert.</param>
    /// <param name="entry">The array index entry providing metadata.</param>
    public static AuxiliaryArray FromValues<T>(PrimitiveArray<T> values, ArrayIndexEntry entry) where T : struct, System.Numerics.INumber<T>
    {
        var vals = values.Select(v => v == null ? T.Zero : (T)v).ToList();
        return FromValues(vals, entry);
    }

    public static AuxiliaryArray FromValues(StringArray values, ArrayIndexEntry entry)
    {
        var buffer = new MemoryStream();
        for (var i = 0; i < values.Length; i++)
        {
            if (values.IsNull(i))
                buffer.Write([0]);
            else
            {
                buffer.Write(values.GetBytes(i));
                buffer.Write([0]);
            }
        }
        var name = new Param(entry.ArrayName, entry.ArrayTypeCURIE, null, entry.UnitCURIE);
        var dataType = BinaryDataTypeMethods.FromCURIE[entry.DataTypeCURIE];
        var unit = entry.GetUnit();
        var bytes = new Memory<byte>(buffer.GetBuffer());
        return new AuxiliaryArray(bytes, name, dataType, unit, Compression.NoCompression);
    }
}