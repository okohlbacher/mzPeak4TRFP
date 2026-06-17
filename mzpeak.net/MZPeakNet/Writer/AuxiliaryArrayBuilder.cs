using Apache.Arrow;
using Apache.Arrow.Types;
using MZPeak.ControlledVocabulary;
using MZPeak.Metadata;

namespace MZPeak.Writer.Visitors;

public class AuxiliaryArrayBuilder : IArrowBuilder<AuxiliaryArray>
{
    public ListArray.Builder Data;
    public ParamBuilder Name;
    public StringArray.Builder DataType;
    public StringArray.Builder Compression;
    public StringArray.Builder Unit;
    public ParamListBuilder Parameters;
    public StringArray.Builder DataProcessingRef;

    public AuxiliaryArrayBuilder()
    {
        Data = new ListArray.Builder(new UInt8Type());
        Name = new();
        DataType = new();
        Compression = new();
        Unit = new();
        DataProcessingRef = new();
        Parameters = new();
        Data.Append();
    }

    public int Length => Name.Length;

    public void Append(AuxiliaryArray value)
    {
        var dataBuilder = (UInt8Array.Builder)Data.ValueBuilder;
        for (var i = 0; i < value.Data.Length; i++)
            dataBuilder.Append(value.Data.Span[i]);
        Data.Append();
        Name.Append(value.Name);
        DataType.Append(value.DataType.CURIE());
        Compression.Append(value.Compression.CURIE());
        Unit.Append(value.Unit?.CURIE());
        DataProcessingRef.AppendNull();
        Parameters.Append(value.Parameters);
    }

    public void AppendNull()
    {
        Data.Append();
        Name.AppendNull();
        DataType.AppendNull();
        Compression.AppendNull();
        Unit.AppendNull();
        DataProcessingRef.AppendNull();
    }

    public List<Field> ArrowType()
    {
        var dataType = new StructType([
            new Field("data", new ListType(new UInt8Type()), true),
            new Field("name", Name.ArrowType()[0].DataType, true),
            new Field("data_type", new StringType(), true),
            new Field("compression", new StringType(), true),
            new Field("unit", new StringType(), true),
            Parameters.ArrowType()[0],
            new Field("data_processing_ref", new StringType(), true),
        ]);
        return [new Field("auxiliary_array", dataType, true)];
    }

    public List<IArrowArray> Build()
    {
        List<IArrowArray> fields = [
            Data.Build(),
            ..Name.Build(),
            DataType.Build(),
            Compression.Build(),
            Unit.Build(),
            ..Parameters.Build(),
            DataProcessingRef.Build()
        ];
        var bat = new StructArray(ArrowType()[0].DataType, Length, fields, default);
        return [bat];
    }

    public void Clear()
    {
        Data.Clear();
        Name.Clear();
        DataType.Clear();
        Compression.Clear();
        Unit.Clear();
        DataProcessingRef.Clear();
        Data.Append();
    }
}

public class AuxiliaryArrayListBuilder : IArrowBuilder<List<AuxiliaryArray>>
{
    public AuxiliaryArrayBuilder ValueBuilder { get; }
    private ArrowBuffer.Builder<int> ValueOffsetsBufferBuilder { get; }

    private ArrowBuffer.BitmapBuilder ValidityBufferBuilder { get; }
    public int NullCount { get; protected set; }

    public int Length => ValueOffsetsBufferBuilder.Length;

    public AuxiliaryArrayListBuilder()
    {
        ValueBuilder = new();
        ValueOffsetsBufferBuilder = new();
        ValidityBufferBuilder = new();
        NullCount = 0;
        Append();
    }

    public void AppendNull()
    {
        ValueOffsetsBufferBuilder.Append(ValueBuilder.Length);
        ValidityBufferBuilder.Append(false);
        NullCount++;
    }

    public void Append(List<AuxiliaryArray> arrays)
    {
        foreach (var par in arrays)
        {
            ValueBuilder.Append(par);
        }
        Append();
    }

    public void Append()
    {
        ValueOffsetsBufferBuilder.Append(ValueBuilder.Length);
        ValidityBufferBuilder.Append(true);
    }

    public List<Field> ArrowType()
    {
        return new(){
            new Field("auxiliar_arrays", new ListType(ValueBuilder.ArrowType()[0].DataType), true)
        };
    }

    public List<IArrowArray> Build()
    {
        ValueOffsetsBufferBuilder.Append(ValueBuilder.Length);
        ArrowBuffer validityBuffer = NullCount > 0
                                ? ValidityBufferBuilder.Build(default)
                                : ArrowBuffer.Empty;
        var dtype = ValueBuilder.ArrowType()[0];
        var dataType = new ListType(dtype.DataType);
        var values = ValueBuilder.Build()[0];
        var listy = new ListArray(
            dataType,
            Length - 1,
            ValueOffsetsBufferBuilder.Build(default), values,
            validityBuffer, NullCount, 0
        );
        return [listy];
    }

    public void Clear()
    {
        ValueBuilder.Clear();
        ValidityBufferBuilder.Clear();
        ValueOffsetsBufferBuilder.Clear();
    }
}
