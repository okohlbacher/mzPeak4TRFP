using Apache.Arrow;
using Apache.Arrow.Types;
using MZPeak.ControlledVocabulary;
using MZPeak.Metadata;

namespace MZPeak.Reader.Visitors;


public class AuxiliaryArrayVisitor : IArrowArrayVisitor<StructArray>
{
    public List<AuxiliaryArray> Values;
    public List<Memory<byte>> DataArrays;
    public List<Param> Names;
    public List<BinaryDataType> DataTypes;
    public List<Compression> Compressions;
    public List<Unit?> Units;
    public ParamListVisitor ParameterLists;
    public List<string?> DataProcessingRefs;

    public AuxiliaryArrayVisitor()
    {
        DataArrays = new();
        Names = new();
        DataTypes = new();
        Compressions = new();
        Units = new();
        ParameterLists = new();
        DataProcessingRefs = new();
        Values = new();
    }

    public void Visit(RecordBatch array)
    {
        var dtype = new StructType(array.Schema.FieldsList);
        Visit(new StructArray(dtype, array.Length, array.Arrays, default));
    }

    public void Visit(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.Struct) Visit((StructArray)array);
        else throw new InvalidDataException();
    }

    public void Visit(StructArray array)
    {
        Values = new();
        var dtype = (StructType)array.Data.DataType;

        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "data") VisitData(arr);
            else if (f.Name == "name") VisitName(arr);
            else if (f.Name == "data_type") VisitDataType(arr);
            else if (f.Name == "compression") VisitCompression(arr);
            else if (f.Name == "unit") VisitUnit(arr);
            else if (f.Name == "parameters") VisitParameters(arr);
            else if (f.Name == "data_processing_ref") VisitDataProcessingRef(arr);
            else
            {

            }
        }
        Build();
    }

    void VisitName(IArrowArray array)
    {
        var names = new ParamVisitor();
        array.Accept(names);
        Names = names.Params;
    }

    void VisitData(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.List)
        {
            var arr = (ListArray)array;
            if (((ListType)arr.Data.DataType).ValueDataType.TypeId != ArrowTypeId.UInt8)
                throw new InvalidDataException($"Invalid data type {array.Data.DataType.Name} for auxiliary array data");
            for (var i = 0; i < arr.Length; i++)
            {
                if (arr.IsNull(i))
                {
                    DataArrays.Add(new());
                    continue;
                }
                var vals = (UInt8Array)arr.GetSlicedValues(i);
                Memory<byte> memory = new Memory<byte>(vals.Data.Buffers[0].Memory.ToArray());
                DataArrays.Add(memory);
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeList)
        {
            var arr = (LargeListArray)array;
            if (((LargeListType)arr.Data.DataType).ValueDataType.TypeId != ArrowTypeId.UInt8)
                throw new InvalidDataException($"Invalid data type {array.Data.DataType.Name} for auxiliary array data");
            for (var i = 0; i < arr.Length; i++)
            {
                if (arr.IsNull(i))
                {
                    DataArrays.Add(new());
                    continue;
                }
                var vals = (UInt8Array)arr.GetSlicedValues(i);
                Memory<byte> memory = new Memory<byte>(vals.Data.Buffers[0].Memory.ToArray());
                DataArrays.Add(memory);
            }
        }
        else throw new InvalidDataException($"Invalid data type {array.Data.DataType.Name} for auxiliary array data");
    }

    void VisitDataType(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.String)
        {
            var arr = (StringArray)array;
            for (var i = 0; i < array.Length; i++)
            {
                if (arr.IsNull(i)) throw new InvalidDataException("Data type cannot be null");
                var curie = arr.GetString(i);
                BinaryDataType dtype;
                if (BinaryDataTypeMethods.FromCURIE.TryGetValue(curie, out dtype))
                    DataTypes.Add(dtype);
                else
                    throw new InvalidDataException($"Data type CURIE not recognized {curie}");
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeString)
        {
            var arr = (LargeStringArray)array;
            for (var i = 0; i < array.Length; i++)
            {
                if (arr.IsNull(i)) throw new InvalidDataException("Data type cannot be null");
                var curie = arr.GetString(i);
                BinaryDataType dtype;
                if (BinaryDataTypeMethods.FromCURIE.TryGetValue(curie, out dtype))
                    DataTypes.Add(dtype);
                else
                    throw new InvalidDataException($"Data type CURIE not recognized {curie}");
            }
        }
        else throw new InvalidDataException($"{array.Data.DataType.Name} not valid for auxiliary data type");
    }

    void VisitUnit(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.String)
        {
            var arr = (StringArray)array;
            for (var i = 0; i < array.Length; i++)
            {
                if (arr.IsNull(i)) throw new InvalidDataException("Unit cannot be null");
                var curie = arr.GetString(i);
                Unit unit;
                if (UnitMethods.FromCURIE.TryGetValue(curie, out unit))
                    Units.Add(unit);
                else
                    throw new InvalidDataException($"Unit CURIE not recognized {curie}");
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeString)
        {
            var arr = (LargeStringArray)array;
            for (var i = 0; i < array.Length; i++)
            {
                if (arr.IsNull(i)) throw new InvalidDataException("Unit cannot be null");
                var curie = arr.GetString(i);
                Unit unit;
                if (UnitMethods.FromCURIE.TryGetValue(curie, out unit))
                    Units.Add(unit);
                else
                    throw new InvalidDataException($"Unit CURIE not recognized {curie}");
            }
        }
        else throw new InvalidDataException($"{array.Data.DataType.Name} not valid for auxiliary unit");
    }

    void VisitCompression(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.String)
        {
            var arr = (StringArray)array;
            for (var i = 0; i < array.Length; i++)
            {
                if (arr.IsNull(i)) throw new InvalidDataException("Compression cannot be null");
                var curie = arr.GetString(i);
                Compression unit;
                if (CompressionMethods.FromCURIE.TryGetValue(curie, out unit))
                    Compressions.Add(unit);
                else
                    throw new InvalidDataException($"Compression CURIE not recognized {curie}");
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeString)
        {
            var arr = (LargeStringArray)array;
            for (var i = 0; i < array.Length; i++)
            {
                if (arr.IsNull(i)) throw new InvalidDataException("Compression cannot be null");
                var curie = arr.GetString(i);
                Compression unit;
                if (CompressionMethods.FromCURIE.TryGetValue(curie, out unit))
                    Compressions.Add(unit);
                else
                    throw new InvalidDataException($"Compression CURIE not recognized {curie}");
            }
        }
        else throw new InvalidDataException($"{array.Data.DataType.Name} not valid for auxiliary array compression");
    }

    void VisitDataProcessingRef(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.String)
        {
            var arr = (StringArray)array;
            for (var i = 0; i < array.Length; i++)
            {
                if (arr.IsNull(i)) DataProcessingRefs.Add(null);
                else DataProcessingRefs.Add(arr.GetString(i));
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeString)
        {
            var arr = (LargeStringArray)array;
            for (var i = 0; i < array.Length; i++)
            {
                if (arr.IsNull(i)) DataProcessingRefs.Add(null);
                else DataProcessingRefs.Add(arr.GetString(i));
            }
        }
        else throw new InvalidDataException($"{array.Data.DataType.Name} not valid for auxiliary array data processing");
    }

    void VisitParameters(IArrowArray array)
    {
        ParameterLists.Visit(array);
    }

    void Build()
    {
        for (var i = 0; i < DataArrays.Count; i++)
        {
            Values.Add(
                new AuxiliaryArray(DataArrays[i], Names[i], DataTypes[i], Units[i], Compressions[i], ParameterLists.ParamsLists[i])
            );
        }
    }
}
