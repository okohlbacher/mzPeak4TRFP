using Apache.Arrow;
using Apache.Arrow.Types;
using MZPeak.ControlledVocabulary;

namespace MZPeak.Reader.Visitors;


public class ParamVisitor : IArrowArrayVisitor<StructArray>
{
    public List<Param> Params;

    public ParamVisitor()
    {
        Params = new();
    }

    void VisitName(IArrowArray arr)
    {
        if (arr.Data.DataType.TypeId == ArrowTypeId.String)
        {
            var names = (StringArray)arr;
            for (var i = 0; i < names.Length; i++)
            {
                if (names.IsNull(i)) throw new InvalidDataException("The name of a Parameter cannot be null");
                var val = names.GetString(i);
                Params.Add(new Param(val, null));
            }
        }
        else if (arr.Data.DataType.TypeId == ArrowTypeId.LargeString)
        {
            var names = (LargeStringArray)arr;
            for (var i = 0; i < names.Length; i++)
            {
                if (names.IsNull(i)) throw new InvalidDataException("The name of a Parameter cannot be null");
                var val = names.GetString(i);
                Params.Add(new Param(val, null));
            }
        }
        else throw new InvalidDataException();
    }

    void VisitAccession(IArrowArray arr)
    {
        if (arr.Data.DataType.TypeId == ArrowTypeId.String)
        {
            var accs = (StringArray)arr;
            for (var i = 0; i < accs.Length; i++)
            {
                Params[i].AccessionCURIE = accs.GetString(i);
            }
        }
        else if (arr.Data.DataType.TypeId == ArrowTypeId.LargeString)
        {
            var accs = (LargeStringArray)arr;
            for (var i = 0; i < accs.Length; i++)
            {
                Params[i].AccessionCURIE = accs.GetString(i);
            }
        }
        else throw new InvalidDataException("Unsupported type for accession: " + arr.Data.DataType.Name);
    }

    void VisitUnit(IArrowArray arr)
    {
        if (arr.Data.DataType.TypeId == ArrowTypeId.String)
        {
            var accs = (StringArray)arr;
            for (var i = 0; i < accs.Length; i++) Params[i].UnitCURIE = accs.GetString(i);
        }
        else if (arr.Data.DataType.TypeId == ArrowTypeId.LargeString)
        {
            var accs = (LargeStringArray)arr;
            for (var i = 0; i < accs.Length; i++) Params[i].UnitCURIE = accs.GetString(i);
        }
        else throw new InvalidDataException("Unsupported type for unit: " + arr.Data.DataType.Name);
    }

    void VisitValue(IArrowArray arr)
    {
        if (arr.Data.DataType.TypeId != ArrowTypeId.Struct) throw new InvalidDataException("Parameter values must be a StructArray");
        StructArray array = (StructArray)arr;
        var dtype = (StructType)arr.Data.DataType;
        foreach (var (f, facet) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "integer")
            {
                var vals = (Int64Array)facet;
                for (var i = 0; i < vals.Length; i++)
                {
                    var v = vals.GetValue(i);
                    if (v != null)
                    {
                        Params[i].rawValue = v;
                    }
                }
            }
            else if (f.Name == "float")
            {
                var vals = (DoubleArray)facet;
                for (var i = 0; i < vals.Length; i++)
                {
                    var v = vals.GetValue(i);
                    if (v != null)
                    {
                        Params[i].rawValue = v;
                    }
                }
            }
            else if (f.Name == "string")
            {
                if (facet.Data.DataType.TypeId == ArrowTypeId.LargeString)
                {
                    var vals = (LargeStringArray)facet;
                    for (var i = 0; i < vals.Length; i++)
                    {
                        if (vals.IsValid(i))
                        {
                            var v = vals.GetString(i);
                            Params[i].rawValue = v;
                        }
                    }
                }
                else if (facet.Data.DataType.TypeId == ArrowTypeId.String)
                {
                    var vals = (StringArray)facet;
                    for (var i = 0; i < vals.Length; i++)
                    {
                        if (vals.IsValid(i))
                        {
                            var v = vals.GetString(i);
                            Params[i].rawValue = v;
                        }
                    }
                }
                else throw new NotImplementedException(facet.Data.DataType.Name);
            }
            else if (f.Name == "boolean")
            {
                var vals = (BooleanArray)facet;
                for (var i = 0; i < vals.Length; i++)
                {
                    var v = vals.GetValue(i);
                    if (v != null)
                    {
                        Params[i].rawValue = v;
                    }
                }
            }
        }
    }

    public void Visit(StructArray array)
    {
        Params = new();
        var n = array.Length;
        var dtype = (StructType)array.Data.DataType;

        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "name")
            {
                VisitName(arr);
                break;
            }
        }
        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "accession") VisitAccession(arr);
            else if (f.Name == "unit") VisitUnit(arr);
            else if (f.Name == "value") VisitValue(arr);
            else
            {
                // Unknown thing
            }
        }
    }

    public void Visit(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.Struct) Visit((StructArray)array);
        else throw new InvalidDataException();
    }
}

public class ParamListVisitor : IArrowArrayVisitor<LargeListArray>, IArrowArrayVisitor<ListArray>
{
    public List<List<Param>> ParamsLists;

    public ParamListVisitor()
    {
        ParamsLists = new();
    }
    public void Visit(ListArray array)
    {
        var dtype = (ListType)array.Data.DataType;
        if (dtype.ValueDataType.TypeId != ArrowTypeId.Struct) throw new InvalidDataException();
        for (var i = 0; i < array.Length; i++)
        {
            if (array.IsNull(i)) ParamsLists.Add(new());
            else
            {
                var slice = array.GetSlicedValues(i);
                var visitor = new ParamVisitor();
                visitor.Visit(slice);
                ParamsLists.Add(visitor.Params);
            }
        }
    }

    public void Visit(LargeListArray array)
    {
        var dtype = (LargeListType)array.Data.DataType;
        if (dtype.ValueDataType.TypeId != ArrowTypeId.Struct) throw new InvalidDataException();
        for (var i = 0; i < array.Length; i++)
        {
            if (array.IsNull(i)) ParamsLists.Add(new());
            else
            {
                var slice = array.GetSlicedValues(i);
                var visitor = new ParamVisitor();
                visitor.Visit(slice);
                ParamsLists.Add(visitor.Params);
            }
        }
    }

    public void Visit(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.LargeList) Visit((LargeListArray)array);
        else if (array.Data.DataType.TypeId == ArrowTypeId.List) Visit((ListArray)array);
        else throw new InvalidDataException();
    }
}
