using System.Numerics;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using MZPeak.ControlledVocabulary;
using MZPeak.Metadata;

namespace MZPeak.Reader.Visitors;

public record HasIonMobility
{
    public double? IonMobility { get; set; } = null;
    public string? IonMobilityTypeCURIE { get; set; } = null;
}

public interface IHasParameters
{
    public List<Param> Parameters { get; set; }

    public Param? FindParam(string accession)
    {
        foreach (var p in Parameters)
            if (p.AccessionCURIE == accession) return p;
        return null;
    }

    public bool HasParam(string accession) => FindParam(accession) != null;
}

public interface IHasSourceIndex
{
    public ulong SourceIndex { get; set; }
}

public interface IHasPrecursorIndex
{
    public ulong? PrecursorIndex { get; set; }
}

public record SpectrumInfo : IHasParameters
{
    public ulong Index { get; set; }
    public string Id { get; set; }
    public double Time { get; set; }
    public byte MSLevel { get; set; }
    public string? DataProcessingRef { get; set; } = null;
    public int NumberOfAuxiliaryArrays { get; set; }
    public List<double>? MzDeltaModel { get; set; } = null;
    public List<Param> Parameters { get; set; }
    public List<AuxiliaryArray> AuxiliaryArrays { get; set; }

    public Param? FindParam(string accession)
    {
        foreach (var p in Parameters)
            if (p.AccessionCURIE == accession)
            {
                return p;
            }
        return null;
    }

    public bool HasParam(string accession) => FindParam(accession) != null;

    public bool IsProfile => HasParam(SpectrumRepresentation.ProfileSpectrum.CURIE());
    public bool IsCentroid => HasParam(SpectrumRepresentation.CentroidSpectrum.CURIE());
    public double? BasePeakMZ => FindParam(SpectrumProperties.BasePeakMZ.CURIE())?.AsDouble();
    public double? BasePeakIntensity => FindParam(SpectrumProperties.BasePeakIntensity.CURIE())?.AsDouble();

    public long? DataPointCount
    {
        get => FindParam(SpectrumProperties.NumberOfDataPoints.CURIE())?.AsLong();
        set
        {
            var param = FindParam(SpectrumProperties.NumberOfDataPoints.CURIE());
            if (param != null)
            {
                if (value != null)
                {
                    param.rawValue = value;
                }
                else
                {
                    Parameters.Remove(param);
                }
            }
            else
            {
                if (value != null)
                    Parameters.Add(SpectrumProperties.NumberOfDataPoints.Param(value));
            }
        }
    }
    public long? PeakCount
    {
        get => FindParam(SpectrumProperties.NumberOfPeaks.CURIE())?.AsLong();
        set
        {
            var param = FindParam(SpectrumProperties.NumberOfPeaks.CURIE());
            if (param != null)
            {
                if (value != null)
                {
                    param.rawValue = value;
                }
                else
                {
                    Parameters.Remove(param);
                }
            }
            else
            {
                if (value != null)
                    Parameters.Add(SpectrumProperties.NumberOfPeaks.Param(value));
            }
        }
    }
    public SpectrumInfo(ulong index, string id, double time, byte msLevel, string? dataProcessingRef = null, int numberOfAuxiliaryArrays = 0, List<double>? mzDeltaModel = null, List<Param>? parameters = null, List<AuxiliaryArray>? auxiliaryArray = null)
    {
        Index = index;
        Id = id;
        Time = time;
        MSLevel = msLevel;
        DataProcessingRef = dataProcessingRef;
        NumberOfAuxiliaryArrays = numberOfAuxiliaryArrays;
        MzDeltaModel = mzDeltaModel;
        Parameters = parameters ?? new();
        AuxiliaryArrays = auxiliaryArray ?? new();
    }

    public override string ToString()
    {
        return "SpectrumInfo\n" + JsonSerializer.Serialize(this, new JsonSerializerOptions() { WriteIndented = true });
    }
}

record ParamListRecord : IHasParameters
{
    public List<Param> Parameters { get; set; }

    public ParamListRecord()
    {
        Parameters = new();
    }
}

public record ScanWindow
{
    public double LowerBound { get; set; }
    public double UpperBound { get; set; }
    public Unit Unit { get; set; }

    public ScanWindow(double lowerBound, double upperBound, Unit unit)
    {
        LowerBound = lowerBound;
        UpperBound = upperBound;
        Unit = unit;
    }

    public List<Param> AsParamList()
    {
        List<Param> vals = [
            new Param("scan window lower limit", "MS:1000501", LowerBound, Unit.CURIE()),
            new Param("scan window upper limit", "MS:1000500", UpperBound, Unit.CURIE()),
        ];
        return vals;
    }
}

public record ScanInfo : HasIonMobility, IHasSourceIndex, IHasParameters
{
    public ulong SourceIndex { get; set; }
    public uint? InstrumentConfigurationRef { get; set; }
    public List<Param> Parameters { get; set; }
    public List<ScanWindow> ScanWindows { get; set; }

    public ScanInfo(ulong sourceIndex,
                    uint? instrumentConfigurationRef = null,
                    double? ionMobility = null,
                    string? ionMobilityTypeCURIE = null,
                    List<Param>? parameters = null,
                    List<ScanWindow>? scanWindows = null)
    {
        SourceIndex = sourceIndex;
        InstrumentConfigurationRef = instrumentConfigurationRef;
        IonMobility = ionMobility;
        IonMobilityTypeCURIE = ionMobilityTypeCURIE;
        Parameters = parameters ?? new();
        ScanWindows = scanWindows ?? new();
    }

    public override string ToString()
    {
        return "ScanInfo\n" + JsonSerializer.Serialize(this, new JsonSerializerOptions() { WriteIndented = true });
    }
}

public record PrecursorInfo : IHasSourceIndex, IHasPrecursorIndex
{
    public ulong SourceIndex { get; set; }
    public ulong? PrecursorIndex { get; set; }
    public string? PrecursorId { get; set; }
    public List<Param> IsolationWindowParameters { get; set; }
    public List<Param> ActivationParameters { get; set; }

    public PrecursorInfo(ulong sourceIndex, ulong? precursorIndex, string? precursorId = null, List<Param>? isolationWindowParameters = null, List<Param>? activationParameters = null)
    {
        SourceIndex = sourceIndex;
        PrecursorIndex = precursorIndex;
        PrecursorId = precursorId;
        IsolationWindowParameters = isolationWindowParameters ?? new();
        ActivationParameters = activationParameters ?? new();
    }

    public override string ToString()
    {
        return "PrecursorInfo\n" + JsonSerializer.Serialize(this, new JsonSerializerOptions() { WriteIndented = true });
    }
}

public record SelectedIonInfo : HasIonMobility, IHasSourceIndex, IHasParameters, IHasPrecursorIndex
{
    public ulong SourceIndex { get; set; }
    public ulong? PrecursorIndex { get; set; }
    public List<Param> Parameters { get; set; }

    public SelectedIonInfo(ulong sourceIndex, ulong? precursorIndex, double? ionMobility = null, string? ionMobilityTypeCURIE = null, List<Param>? parameters = null)
    {
        SourceIndex = sourceIndex;
        PrecursorIndex = precursorIndex;
        IonMobility = ionMobility;
        IonMobilityTypeCURIE = ionMobilityTypeCURIE;
        Parameters = parameters ?? new();
    }

    public override string ToString()
    {
        return "SelectedIonInfo\n" + JsonSerializer.Serialize(this, new JsonSerializerOptions() { WriteIndented = true });
    }
}

public record ChromatogramInfo : IHasParameters
{
    public ulong Index { get; set; }
    public string Id { get; set; }
    public string? DataProcessingRef { get; set; } = null;
    public int NumberOfAuxiliaryArrays { get; set; }
    public List<Param> Parameters { get; set; }
    public List<AuxiliaryArray> AuxiliaryArrays { get; set; }

    public Param? FindParam(string accession)
    {
        foreach (var p in Parameters)
            if (p.AccessionCURIE == accession)
            {
                return p;
            }
        return null;
    }

    public bool HasParam(string accession) => FindParam(accession) != null;

    public ChromatogramInfo(ulong index, string id, string? dataProcessingRef = null, int numberOfAuxiliaryArrays = 0, List<Param>? parameters = null, List<AuxiliaryArray>? auxiliaryArray = null)
    {
        Index = index;
        Id = id;
        DataProcessingRef = dataProcessingRef;
        Parameters = parameters ?? new();
        NumberOfAuxiliaryArrays = numberOfAuxiliaryArrays;
        AuxiliaryArrays = auxiliaryArray ?? new();
    }

    public override string ToString()
    {
        return "ChromatogramInfo\n" + JsonSerializer.Serialize(this, new JsonSerializerOptions() { WriteIndented = true });
    }

    public long? DataPointCount
    {
        get => FindParam(SpectrumProperties.NumberOfDataPoints.CURIE())?.AsLong();
        set
        {
            var param = FindParam(SpectrumProperties.NumberOfDataPoints.CURIE());
            if (param != null)
            {
                if (value != null)
                {
                    param.rawValue = value;
                }
                else
                {
                    Parameters.Remove(param);
                }
            }
            else
            {
                if (value != null)
                Parameters.Add(SpectrumProperties.NumberOfDataPoints.Param(value));
            }
        }
    }
    public long? PeakCount
    {
        get => FindParam(SpectrumProperties.NumberOfPeaks.CURIE())?.AsLong();
        set
        {
            var param = FindParam(SpectrumProperties.NumberOfPeaks.CURIE());
            if (param != null)
            {
                if (value != null)
                {
                    param.rawValue = value;
                }
                else
                {
                    Parameters.Remove(param);
                }
            }
            else
            {
                if (value != null)
                    Parameters.Add(SpectrumProperties.NumberOfPeaks.Param(value));
            }
        }
    }

}

public interface HasArrayIndex
{
    public ArrayIndex? ArrayIndex { get; set; }
}

public record SpectrumDescription : HasArrayIndex
{
    SpectrumInfo SpectrumInfo;
    public List<ScanInfo> Scans;
    public List<PrecursorInfo> Precursors;
    public List<SelectedIonInfo> SelectedIons;

    public ArrayIndex? ArrayIndex { get; set; }

    public string Id => SpectrumInfo.Id;
    public ulong Index => SpectrumInfo.Index;
    public byte MSLevel => SpectrumInfo.MSLevel;
    public double Time => SpectrumInfo.Time;
    public List<Param> Parameters => SpectrumInfo.Parameters;
    public List<double>? MzDeltaModel => SpectrumInfo.MzDeltaModel;
    public string? DataProcessingRef => SpectrumInfo.DataProcessingRef;
    public bool IsProfile => SpectrumInfo.IsProfile;
    public bool IsCentroid => SpectrumInfo.IsCentroid;
    public double? BasePeakMZ => SpectrumInfo.BasePeakMZ;
    public double? BasePeakIntensity => SpectrumInfo.BasePeakIntensity;

    public long? DataPointCount
    {
        get => SpectrumInfo.DataPointCount;
        set => SpectrumInfo.DataPointCount = value;
    }

    public long? PeakCount
    {
        get => SpectrumInfo.PeakCount;
        set => SpectrumInfo.PeakCount = value;
    }

    public SpectrumDescription(SpectrumInfo spectrumInfo, List<ScanInfo> scans, List<PrecursorInfo> precursors, List<SelectedIonInfo> selectedIons, ArrayIndex? arrayIndex=null)
    {
        SpectrumInfo = spectrumInfo;
        Scans = scans;
        Precursors = precursors;
        SelectedIons = selectedIons;
        ArrayIndex = arrayIndex;
    }
}

public record ChromatogramDescription : HasArrayIndex
{
    ChromatogramInfo ChromatogramInfo;
    public List<PrecursorInfo> Precursors;
    public List<SelectedIonInfo> SelectedIons;

    public ArrayIndex? ArrayIndex { get; set; }

    public string Id => ChromatogramInfo.Id;
    public ulong Index => ChromatogramInfo.Index;
    public List<Param> Parameters => ChromatogramInfo.Parameters;
    public string? DataProcessingRef => ChromatogramInfo.DataProcessingRef;

    public ChromatogramDescription(ChromatogramInfo chromatogramInfo, List<PrecursorInfo> precursors, List<SelectedIonInfo> selectedIons)
    {
        ChromatogramInfo = chromatogramInfo;
        Precursors = precursors;
        SelectedIons = selectedIons;
    }

    public long? DataPointCount
    {
        get => ChromatogramInfo.DataPointCount;
        set => ChromatogramInfo.DataPointCount = value;
    }

    public long? PeakCount
    {
        get => ChromatogramInfo.PeakCount;
        set => ChromatogramInfo.PeakCount = value;
    }

}

public interface IVisitorAssemblyWithOffsets<T>
{
    List<int> Offsets { get; }
    List<T> Values { get; }
}

public interface IHasIonMobilityVisitor<T> : IVisitorAssemblyWithOffsets<T> where T : HasIonMobility
{

    public void VisitIonMobilityType(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.String)
        {
            StringArray arr = (StringArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (arr.IsNull(i)) continue;
                var chunk = arr.GetString(i);
                Values[j].IonMobilityTypeCURIE = chunk;
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeString)
        {
            LargeStringArray arr = (LargeStringArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (arr.IsNull(i)) continue;
                var chunk = arr.GetString(i);
                Values[j].IonMobilityTypeCURIE = chunk;
            }
        }
        else throw new NotImplementedException();
    }

    public void VisitIonMobilityValue(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.Float)
        {
            FloatArray arr = (FloatArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                var chunk = arr.GetValue(i);
                if (chunk == null) continue;
                Values[j].IonMobility = chunk;
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.Double)
        {
            DoubleArray arr = (DoubleArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                var chunk = arr.GetValue(i);
                if (chunk == null) continue;
                Values[j].IonMobility = chunk;
            }
        }
    }
}

public interface IPrimitiveTypeVisitor
{
    public IEnumerable<long?> VisitInteger<U>(PrimitiveArray<U> array) where U : struct, INumber<U>
    {
        for (int i = 0; i < array.Length; i++)
        {
            var value = array.GetValue(i);
            if (value == null)
                yield return null;
            else
            {
                var v = (U)value;
                yield return long.CreateChecked(v);
            }
        }
    }

    public IEnumerable<double?> VisitFloat<U>(PrimitiveArray<U> array) where U : struct, INumber<U>
    {
        for (int i = 0; i < array.Length; i++)
        {
            var value = array.GetValue(i);
            if (value == null)
                yield return null;
            else
            {
                var v = (U)value;
                yield return double.CreateChecked(v);
            }
        }
    }

    public IEnumerable<double?> VisitFloat(IArrowArray array)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Float:
                {
                    return VisitFloat((FloatArray)array);
                }
            case ArrowTypeId.Double:
                {
                    return VisitFloat((DoubleArray)array);
                }
            default: throw new InvalidDataException();
        }
    }

    public IEnumerable<long?> VisitInteger(IArrowArray array)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Int8:
                {
                    return VisitInteger((Int8Array)array);
                }
            case ArrowTypeId.Int16:
                {
                    return VisitInteger((Int16Array)array);
                }
            case ArrowTypeId.Int32:
                {
                    return VisitInteger((Int32Array)array);
                }
            case ArrowTypeId.Int64:
                {
                    return VisitInteger((Int64Array)array);
                }
            case ArrowTypeId.UInt8:
                {
                    return VisitInteger((UInt8Array)array);
                }
            case ArrowTypeId.UInt16:
                {
                    return VisitInteger((UInt16Array)array);
                }
            case ArrowTypeId.UInt32:
                {
                    return VisitInteger((UInt32Array)array);
                }
            case ArrowTypeId.UInt64:
                {
                    return VisitInteger((UInt64Array)array);
                }
            default: throw new InvalidCastException($"Could not convert {array.Data.DataType.Name} to an integer");
        }
    }

    public IEnumerable<string?> VisitString(LargeStringArray array)
    {
        for (int i = 0; i < array.Length; i++)
        {
            if (array.IsNull(i))
                yield return null;
            else
            {
                var v = array.GetString(i);
                yield return v;
            }
        }
    }

    public IEnumerable<string?> VisitString(StringArray array)
    {
        for (int i = 0; i < array.Length; i++)
        {
            if (array.IsNull(i))
                yield return null;
            else
            {
                var v = array.GetString(i);
                yield return v;
            }
        }
    }
}

public interface IHasParametersVisitorWithOffsets<T> : IVisitorAssemblyWithOffsets<T> where T : IHasParameters
{
    public IEnumerable<(int, long?)> VisitInteger<U>(PrimitiveArray<U> array) where U : struct, INumber<U>
    {
        for (int j = 0; j < Offsets.Count; j++)
        {
            var i = Offsets[j];
            var value = array.GetValue(i);
            if (value == null)
                yield return (j, null);
            else
            {
                var v = (U)value;
                yield return (j, long.CreateChecked(v));
            }
        }
    }

    public IEnumerable<(int, long?)> VisitInteger(IArrowArray array)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Int8:
                {
                    return VisitInteger((Int8Array)array);
                }
            case ArrowTypeId.Int16:
                {
                    return VisitInteger((Int16Array)array);
                }
            case ArrowTypeId.Int32:
                {
                    return VisitInteger((Int32Array)array);
                }
            case ArrowTypeId.Int64:
                {
                    return VisitInteger((Int64Array)array);
                }
            case ArrowTypeId.UInt8:
                {
                    return VisitInteger((UInt8Array)array);
                }
            case ArrowTypeId.UInt16:
                {
                    return VisitInteger((UInt16Array)array);
                }
            case ArrowTypeId.UInt32:
                {
                    return VisitInteger((UInt32Array)array);
                }
            case ArrowTypeId.UInt64:
                {
                    return VisitInteger((UInt64Array)array);
                }
            default: throw new InvalidCastException($"Could not convert {array.Data.DataType.Name} to an integer");
        }
    }

    public IEnumerable<(int, double?)> VisitFloat<U>(PrimitiveArray<U> array) where U : struct, INumber<U>
    {
        for (int j = 0; j < Offsets.Count; j++)
        {
            var i = Offsets[j];
            var value = array.GetValue(i);
            if (value == null)
                yield return (j, null);
            else
            {
                var v = (U)value;
                yield return (j, double.CreateChecked(v));
            }
        }
    }

    public IEnumerable<(int, string?)> VisitString(StringArray array)
    {
        for (int j = 0; j < Offsets.Count; j++)
        {
            var i = Offsets[j];
            if (array.IsNull(i))
                yield return (j, null);
            else
            {
                var v = array.GetString(i);
                yield return (j, v);
            }
        }
    }

    public IEnumerable<(int, string?)> VisitString(LargeStringArray array)
    {
        for (int j = 0; j < Offsets.Count; j++)
        {
            var i = Offsets[j];
            if (array.IsNull(i))
                yield return (j, null);
            else
            {
                var v = array.GetString(i);
                yield return (j, v);
            }
        }
    }

    void VisitParameters(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.List)
        {
            ListArray arr = (ListArray)array;
            var paramVisitor = new ParamVisitor();
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                var chunk = arr.GetSlicedValues(i);
                paramVisitor.Visit(chunk);
                Values[j].Parameters.AddRange(paramVisitor.Params);
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeList)
        {
            LargeListArray arr = (LargeListArray)array;
            var paramVisitor = new ParamVisitor();
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                var chunk = arr.GetSlicedValues(i);
                paramVisitor.Visit(chunk);
                Values[j].Parameters.AddRange(paramVisitor.Params);
            }
        }
        else throw new NotImplementedException();
    }

    void VisitAsParameter(Field field, IArrowArray array)
    {
        var param = ColumnParam.FromFieldIndex(field, 0);
        if (param.IsUnitOnly) throw new NotImplementedException();
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Int8:
                {
                    foreach ((var i, var val) in VisitInteger((Int8Array)array))
                        Values[i].Parameters.Add(new Param(param.Name, accession: param.CURIE, rawValue: val, param.UnitCURIE));
                    break;
                }
            case ArrowTypeId.Int16:
                {
                    foreach ((var i, var val) in VisitInteger((Int16Array)array))
                        Values[i].Parameters.Add(new Param(param.Name, accession: param.CURIE, rawValue: val, param.UnitCURIE));
                    break;
                }
            case ArrowTypeId.Int32:
                {
                    foreach ((var i, var val) in VisitInteger((Int32Array)array))
                        Values[i].Parameters.Add(new Param(param.Name, accession: param.CURIE, rawValue: val, param.UnitCURIE));
                    break;
                }
            case ArrowTypeId.Int64:
                {
                    foreach ((var i, var val) in VisitInteger((Int64Array)array))
                        Values[i].Parameters.Add(new Param(param.Name, accession: param.CURIE, rawValue: val, param.UnitCURIE));
                    break;
                }
            case ArrowTypeId.UInt8:
                {
                    foreach ((var i, var val) in VisitInteger((UInt8Array)array))
                        Values[i].Parameters.Add(new Param(param.Name, accession: param.CURIE, rawValue: val, param.UnitCURIE));
                    break;
                }
            case ArrowTypeId.UInt16:
                {
                    foreach ((var i, var val) in VisitInteger((UInt16Array)array))
                        Values[i].Parameters.Add(new Param(param.Name, accession: param.CURIE, rawValue: val, param.UnitCURIE));
                    break;
                }
            case ArrowTypeId.UInt32:
                {
                    foreach ((var i, var val) in VisitInteger((UInt32Array)array))
                        Values[i].Parameters.Add(new Param(param.Name, accession: param.CURIE, rawValue: val, param.UnitCURIE));
                    break;
                }
            case ArrowTypeId.UInt64:
                {
                    foreach ((var i, var val) in VisitInteger((UInt64Array)array))
                        Values[i].Parameters.Add(new Param(param.Name, accession: param.CURIE, rawValue: val, param.UnitCURIE));
                    break;
                }
            case ArrowTypeId.Float:
                {
                    foreach ((var i, var val) in VisitFloat((FloatArray)array))
                    {
                        Values[i].Parameters.Add(new Param(param.Name, accession: param.CURIE, rawValue: val, param.UnitCURIE));
                    }
                    break;
                }
            case ArrowTypeId.Double:
                {
                    foreach ((var i, var val) in VisitFloat((DoubleArray)array))
                        Values[i].Parameters.Add(new Param(param.Name, accession: param.CURIE, rawValue: val, param.UnitCURIE));
                    break;
                }
            case ArrowTypeId.Boolean:
                {
                    BooleanArray arr = (BooleanArray)array;
                    for (int j = 0; j < Offsets.Count; j++)
                    {
                        var i = Offsets[j];
                        var value = arr.GetValue(i);
                        Values[j].Parameters.Add(new Param(param.Name, accession: param.CURIE, rawValue: value, param.UnitCURIE));
                    }
                    break;
                }
            case ArrowTypeId.String:
                {
                    foreach ((var i, var val) in VisitString((StringArray)array))
                        Values[i].Parameters.Add(new Param(param.Name, accession: param.CURIE, rawValue: val, param.UnitCURIE));
                    break;
                }
            case ArrowTypeId.LargeString:
                {
                    foreach ((var i, var val) in VisitString((LargeStringArray)array))
                        Values[i].Parameters.Add(new Param(param.Name, accession: param.CURIE, rawValue: val, param.UnitCURIE));
                    break;
                }
            default: throw new NotImplementedException($"{array.Data.DataType.Name} from {field.Name}");
        }
    }
}

public interface IHasSourceIndexVisitor<T> : IVisitorAssemblyWithOffsets<T> where T : IHasSourceIndex
{
    public T CreateFromIndex(ulong index);

    public void VisitSourceIndex(IArrowArray array)
    {
        UInt64Array arr = (UInt64Array)array;
        for (int i = 0; i < arr.Length; i++)
        {
            var idx = arr.GetValue(i);
            if (idx == null) continue;
            Offsets.Add(i);
            Values.Add(CreateFromIndex((ulong)idx));
        }
    }
}

public interface IHasPrecursorIndexVisitor<T> : IVisitorAssemblyWithOffsets<T> where T : IHasPrecursorIndex
{
    public void VisitPrecursorIndex(IArrowArray array)
    {
        UInt64Array arr = (UInt64Array)array;
        for (int j = 0; j < Offsets.Count; j++)
        {
            var i = Offsets[j];
            var chunk = arr.GetValue(i);
            if (chunk == null) continue;
            Values[j].PrecursorIndex = (ulong)chunk;
        }
    }
}

class GenericParamStructVisitor : IVisitorAssemblyWithOffsets<ParamListRecord>, IHasParametersVisitorWithOffsets<ParamListRecord>, IArrowArrayVisitor<StructArray>, IArrowArrayVisitor<ListArray>, IArrowArrayVisitor<LargeListArray>
{
    public List<ParamListRecord> Values { get; set; }
    public List<int> Offsets { get; set; }

    public GenericParamStructVisitor(List<int> offsets)
    {
        Values = new();
        Offsets = offsets;
    }

    void Initialize(IArrowArray array)
    {
        for (var i = 0; i < Offsets.Count; i++)
        {
            // Offsets.Add(i);
            Values.Add(new());
        }
    }

    public void Visit(StructArray array)
    {
        Initialize(array);
        var dtype = (StructType)array.Data.DataType;
        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "parameters") ((IHasParametersVisitorWithOffsets<ParamListRecord>)this).VisitParameters(arr);
            else ((IHasParametersVisitorWithOffsets<ParamListRecord>)this).VisitAsParameter(f, arr);
        }
    }

    public void Visit(ListArray array)
    {
        Initialize(array);
        for (var j = 0; j < Offsets.Count; j++)
        {
            var i = Offsets[j];
            var chunk = array.GetSlicedValues(i);
            var visitor = new ParamVisitor();
            visitor.Visit(chunk);
            Values[j].Parameters.AddRange(visitor.Params);
        }

    }

    public void Visit(LargeListArray array)
    {
        Initialize(array);
        for (var j = 0; j < Offsets.Count; j++)
        {
            var i = Offsets[j];
            var chunk = array.GetSlicedValues(i);
            var visitor = new ParamVisitor();
            visitor.Visit(chunk);
            Values[j].Parameters.AddRange(visitor.Params);
        }
    }

    public void Visit(IArrowArray array)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Struct:
                {
                    Visit((StructArray)array);
                    break;
                }
            case ArrowTypeId.List:
                {
                    Visit((ListArray)array);
                    break;
                }
            case ArrowTypeId.LargeList:
                {
                    Visit((LargeListArray)array);
                    break;
                }
            default: throw new InvalidDataException($"Invalid data type {array.Data.DataType.Name} not valid for parameter collections");
        }
    }
}

public class ScanVisitor : IVisitorAssemblyWithOffsets<ScanInfo>, IHasIonMobilityVisitor<ScanInfo>, IHasParametersVisitorWithOffsets<ScanInfo>, IArrowArrayVisitor<StructArray>, IHasSourceIndexVisitor<ScanInfo>, IArrowArrayVisitor<RecordBatch>
{
    public List<ScanInfo> Values { get; set; }
    public List<int> Offsets { get; set; }

    public ScanVisitor()
    {
        Values = new();
        Offsets = new();
    }

    void VisitInstrumentConfigurationRef(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.UInt32)
        {
            UInt32Array arr = (UInt32Array)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                var chunk = arr.GetValue(i);
                Values[j].InstrumentConfigurationRef = chunk;
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.Int32)
        {
            Int32Array arr = (Int32Array)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                var chunk = arr.GetValue(i);
                Values[j].InstrumentConfigurationRef = (uint?)chunk;
            }
        }
        else throw new NotImplementedException(array.Data.DataType.Name);
    }

    void VisitScanWindowsList(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.List)
        {
            var valsArray = (ListArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (valsArray.IsNull(i)) continue;

                var vals = valsArray.GetSlicedValues(i);
                var builder = new ScanWindowVisitor();
                builder.Visit(vals);
                Values[j].ScanWindows = builder.Values;
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeList)
        {
            var valsArray = (LargeListArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (valsArray.IsNull(i)) continue;

                var vals = valsArray.GetSlicedValues(i);
                var builder = new ScanWindowVisitor();
                builder.Visit(vals);
                Values[j].ScanWindows = builder.Values;
            }
        }
        else throw new InvalidDataException();
    }

    public void Visit(StructArray array)
    {
        Values = new();
        Offsets.Clear();

        var dtype = (StructType)array.Data.DataType;

        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "source_index")
            {
                ((IHasSourceIndexVisitor<ScanInfo>)this).VisitSourceIndex(arr);
                break;
            }
        }

        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "instrument_configuration_ref") VisitInstrumentConfigurationRef(arr);
            else if (f.Name == "ion_mobility_value") ((IHasIonMobilityVisitor<ScanInfo>)this).VisitIonMobilityValue(arr);
            else if (f.Name == "ion_mobility_type") ((IHasIonMobilityVisitor<ScanInfo>)this).VisitIonMobilityType(arr);
            else if (f.Name == "parameters") ((IHasParametersVisitorWithOffsets<ScanInfo>)this).VisitParameters(arr);
            else if (f.Name == "scan_windows") VisitScanWindowsList(arr);
            else if (f.Name == "source_index") { }
            else
                ((IHasParametersVisitorWithOffsets<ScanInfo>)this).VisitAsParameter(f, arr);
        }
    }

    public void Visit(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.Struct) Visit((StructArray)array);
        else throw new InvalidDataException();
    }

    public ScanInfo CreateFromIndex(ulong index)
    {
        return new ScanInfo(index, null, null, null, null);
    }

    public void Visit(RecordBatch array)
    {
        var dtype = new StructType(array.Schema.FieldsList);
        Visit(new StructArray(dtype, array.Length, array.Arrays, default));
    }
}


public class ScanWindowVisitor : IArrowArrayVisitor<StructArray>, IPrimitiveTypeVisitor
{
    public List<ScanWindow> Values { get; set; }

    public ScanWindowVisitor()
    {
        Values = new();
    }

    public void Visit(StructArray array)
    {
        int i = 0;
        for (i = 0; i < array.Length; i++)
            Values.Add(new ScanWindow(0, 0, Unit.MZ));

        var dtype = (StructType)array.Data.DataType;
        i = 0;
        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "parameters") { }
            else
            {
                var col = ColumnParam.FromFieldIndex(f, i);
                i++;

                switch (col.CURIE)
                {
                    case "MS:1000501":
                        {
                            int j = 0;
                            foreach (var v in ((IPrimitiveTypeVisitor)this).VisitFloat(arr))
                            {
                                if (v != null)
                                {
                                    Values[j].LowerBound = (double)v;
                                    if (col.UnitCURIE != null)
                                        Values[j].Unit = UnitMethods.FromCURIE[col.UnitCURIE];
                                }
                                j++;
                            }
                            break;
                        }
                    case "MS:1000500":
                        {
                            int j = 0;
                            foreach (var v in ((IPrimitiveTypeVisitor)this).VisitFloat(arr))
                            {
                                if (v != null)
                                {
                                    Values[j].UpperBound = (double)v;
                                    if (col.UnitCURIE != null)
                                        Values[j].Unit = UnitMethods.FromCURIE[col.UnitCURIE];
                                }
                                j++;
                            }
                            break;
                        }
                    default:
                        {
                            break;
                        }
                }
            }
        }
    }

    public void Visit(IArrowArray array)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Struct:
                {
                    Visit((StructArray)array);
                    break;
                }
            default: throw new InvalidDataException();
        }
    }
}


public class SelectedIonVisitor : IVisitorAssemblyWithOffsets<SelectedIonInfo>, IHasIonMobilityVisitor<SelectedIonInfo>, IHasParametersVisitorWithOffsets<SelectedIonInfo>, IArrowArrayVisitor<StructArray>, IArrowArrayVisitor<RecordBatch>, IHasSourceIndexVisitor<SelectedIonInfo>, IHasPrecursorIndexVisitor<SelectedIonInfo>
{

    public List<SelectedIonInfo> Values { get; set; }
    public List<int> Offsets { get; set; }


    public SelectedIonVisitor()
    {
        Values = new();
        Offsets = new();
    }

    public SelectedIonInfo CreateFromIndex(ulong index)
    {
        return new SelectedIonInfo(index, 0, null, null, null);
    }

    public void Visit(StructArray array)
    {
        Values = new();
        Offsets.Clear();

        var dtype = (StructType)array.Data.DataType;

        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "source_index")
            {
                ((IHasSourceIndexVisitor<SelectedIonInfo>)this).VisitSourceIndex(arr);
                break;
            }
        }
        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "precursor_index") ((IHasPrecursorIndexVisitor<SelectedIonInfo>)this).VisitPrecursorIndex(arr);
            else if (f.Name == "source_index") { }
            else if (f.Name == "ion_mobility_value") ((IHasIonMobilityVisitor<SelectedIonInfo>)this).VisitIonMobilityValue(arr);
            else if (f.Name == "ion_mobility_type") ((IHasIonMobilityVisitor<SelectedIonInfo>)this).VisitIonMobilityType(arr);
            else if (f.Name == "parameters") ((IHasParametersVisitorWithOffsets<SelectedIonInfo>)this).VisitParameters(arr);
            else
            {
                ((IHasParametersVisitorWithOffsets<SelectedIonInfo>)this).VisitAsParameter(f, arr);
            }
        }
    }

    public void Visit(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.Struct) Visit((StructArray)array);
        else throw new InvalidDataException();
    }

    public void Visit(RecordBatch array)
    {
        var dtype = new StructType(array.Schema.FieldsList);
        Visit(new StructArray(dtype, array.Length, array.Arrays, default));
    }
}

public class PrecursorVisitor : IVisitorAssemblyWithOffsets<PrecursorInfo>, IHasSourceIndexVisitor<PrecursorInfo>, IHasPrecursorIndexVisitor<PrecursorInfo>, IArrowArrayVisitor<StructArray>, IArrowArrayVisitor<RecordBatch>
{
    public List<PrecursorInfo> Values { get; set; }
    public List<int> Offsets { get; set; }

    public PrecursorVisitor()
    {
        Values = new();
        Offsets = new();
    }

    public void VisitPrecursorId(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.String)
        {
            StringArray arr = (StringArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (arr.IsNull(i)) continue;
                var chunk = arr.GetString(i);
                Values[j].PrecursorId = chunk;
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeString)
        {
            LargeStringArray arr = (LargeStringArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (arr.IsNull(i)) continue;
                var chunk = arr.GetString(i);
                Values[j].PrecursorId = chunk;
            }
        }
        else throw new NotImplementedException();
    }

    public void Visit(StructArray array)
    {
        Values = new();
        Offsets.Clear();

        var dtype = (StructType)array.Data.DataType;

        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "source_index")
            {
                ((IHasSourceIndexVisitor<PrecursorInfo>)this).VisitSourceIndex(arr);
                break;
            }
        }
        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "precursor_index") ((IHasPrecursorIndexVisitor<PrecursorInfo>)this).VisitPrecursorIndex(arr);
            else if (f.Name == "precursor_id") VisitPrecursorId(arr);
            else if (f.Name == "activation") VisitActivationParameters(arr);
            else if (f.Name == "isolation_window") VisitIsolationWindowParameters(arr);
            else if (f.Name == "source_index") { }
            else { }
        }
    }

    public void Visit(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.Struct) Visit((StructArray)array);
        else throw new InvalidDataException();
    }

    void VisitActivationParameters(IArrowArray array)
    {
        var visitor = new GenericParamStructVisitor(Offsets);
        visitor.Visit(array);
        for (var i = 0; i < Values.Count; i++)
        {
            Values[i].ActivationParameters.AddRange(visitor.Values[i].Parameters);
        }
    }

    void VisitIsolationWindowParameters(IArrowArray array)
    {
        var visitor = new GenericParamStructVisitor(Offsets);
        visitor.Visit(array);
        for (var i = 0; i < Values.Count; i++)
        {
            Values[i].IsolationWindowParameters.AddRange(visitor.Values[i].Parameters);
        }
    }

    public PrecursorInfo CreateFromIndex(ulong index)
    {
        return new PrecursorInfo(index, 0, null, null, null);
    }

    public void Visit(RecordBatch array)
    {
        var dtype = new StructType(array.Schema.FieldsList);
        Visit(new StructArray(dtype, array.Length, array.Arrays, default));
    }
}

public class SpectrumVisitor : IVisitorAssemblyWithOffsets<SpectrumInfo>, IHasParametersVisitorWithOffsets<SpectrumInfo>, IArrowArrayVisitor<StructArray>, IArrowArrayVisitor<RecordBatch>
{
    public List<SpectrumInfo> Values { get; set; }
    public List<int> Offsets { get; set; }

    public SpectrumVisitor()
    {
        Values = new();
        Offsets = new();
    }

    void VisitId(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.String)
        {
            StringArray arr = (StringArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (arr.IsNull(i)) continue;
                var chunk = arr.GetString(i);
                Values[j].Id = chunk;
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeString)
        {
            LargeStringArray arr = (LargeStringArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (arr.IsNull(i)) continue;
                var chunk = arr.GetString(i);
                Values[j].Id = chunk;
            }
        }
        else throw new NotImplementedException();
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

    protected void VisitTime(IArrowArray array)
    {
        IHasParametersVisitorWithOffsets<SpectrumInfo> self = this;
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Float:
                {
                    foreach ((var i, var val) in self.VisitFloat((FloatArray)array))
                    {
                        Values[i].Time = val ?? default;
                    }
                    break;
                }
            case ArrowTypeId.Double:
                {
                    foreach ((var i, var val) in self.VisitFloat((DoubleArray)array))
                    {
                        Values[i].Time = val ?? default;
                    }
                    break;
                }
            default: throw new NotImplementedException();
        }
    }

    protected void VisitMSLevel(IArrowArray array)
    {
        var self = (IHasParametersVisitorWithOffsets<SpectrumInfo>)this;
        foreach ((var i, var val) in self.VisitInteger(array))
        {
            Values[i].MSLevel = (byte)(val ?? 0);
        }
    }

    protected void VisitNumberOfAuxiliaryArrays(IArrowArray array)
    {
        var self = (IHasParametersVisitorWithOffsets<SpectrumInfo>)this;
        foreach ((var i, var val) in self.VisitInteger(array))
            Values[i].NumberOfAuxiliaryArrays = (int)(val ?? 0);
    }

    protected void VisitMzDeltaModel(IArrowArray array)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.List:
                {
                    var arr = (ListArray)array;
                    for (var j = 0; j < Offsets.Count; j++)
                    {
                        var i = Offsets[j];
                        if (!arr.IsNull(i))
                        {
                            var p = (DoubleArray)arr.GetSlicedValues(i);
                            Values[j].MzDeltaModel = p.Where(v => v != null).Select(v => v == null ? default : (double)v).ToList();
                        }
                    }
                    break;
                }
            case ArrowTypeId.LargeList:
                {
                    var arr = (LargeListArray)array;
                    for (var j = 0; j < Offsets.Count; j++)
                    {
                        var i = Offsets[j];
                        if (!arr.IsNull(i))
                        {
                            var p = (DoubleArray)arr.GetSlicedValues(i);
                            Values[j].MzDeltaModel = p.Where(v => v != null).Select(v => v == null ? default : (double)v).ToList();
                        }
                    }
                    break;
                }
            case ArrowTypeId.Double:
                {
                    var arr = (DoubleArray)array;
                    for (var j = 0; j < Offsets.Count; j++)
                    {
                        var i = Offsets[j];
                        var p = arr.GetValue(i);
                        if (p != null)
                            Values[j].MzDeltaModel = new() { (double)p };
                    }
                    break;
                }
            default: throw new NotImplementedException(array.Data.DataType.Name);
        }
    }

    protected void VisitDataProcessingRef(IArrowArray array) { }

    protected void VisitSpectrumRepresentation(IArrowArray array)
    {
        var self = (IHasParametersVisitorWithOffsets<SpectrumInfo>)this;
        IEnumerable<(int, string?)> it;
        if (array.Data.DataType.TypeId == ArrowTypeId.String) it = self.VisitString((StringArray)array);
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeString) it = self.VisitString((LargeStringArray)array);
        else throw new InvalidDataException($"{array.Data.DataType.Name} is not a valid data type for spectrum representation");
        var profileParam = SpectrumRepresentation.ProfileSpectrum.AsParam();
        var centroidParam = SpectrumRepresentation.CentroidSpectrum.AsParam();
        foreach (var (i, val) in it)
        {
            if (val == null) continue;

            else if (val == profileParam.AccessionCURIE) Values[i].Parameters.Add(profileParam);
            else if (val == centroidParam.AccessionCURIE) Values[i].Parameters.Add(centroidParam);
            else throw new NotImplementedException($"{val} is not a recognized spectrum representation");
        }
    }

    public void Visit(StructArray array)
    {
        Values = new();
        Offsets.Clear();

        var dtype = (StructType)array.Data.DataType;

        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "index")
            {
                VisitIndex(arr);
                break;
            }
        }

        var self = (IHasParametersVisitorWithOffsets<SpectrumInfo>)this;

        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "id") VisitId(arr);
            else if (f.Name == "index") { }
            else if (f.Name == "time") VisitTime(arr);
            else if (f.Name == "MS_1000511_ms_level") VisitMSLevel(arr);
            else if (f.Name == "MS_1000525_spectrum_representation") VisitSpectrumRepresentation(arr);
            else if (f.Name == "data_processing_ref") VisitDataProcessingRef(arr);
            else if (f.Name == "mz_delta_model") VisitMzDeltaModel(arr);
            else if (f.Name == "number_of_auxiliary_arrays") VisitNumberOfAuxiliaryArrays(arr);
            else if (f.Name == "auxiliary_arrays") VisitAuxiliarArrays(arr);
            else if (f.Name == "parameters") self.VisitParameters(arr);
            else self.VisitAsParameter(f, arr);
        }
    }

    void VisitAuxiliarArrays(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.List)
        {
            ListArray arr = (ListArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (arr.IsNull(i)) continue;
                var chunk = arr.GetSlicedValues(i);
                var visitor = new AuxiliaryArrayVisitor();
                visitor.Visit(chunk);
                Values[j].AuxiliaryArrays.AddRange(visitor.Values);
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeList)
        {
            LargeListArray arr = (LargeListArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (arr.IsNull(i)) continue;
                var chunk = arr.GetSlicedValues(i);
                var visitor = new AuxiliaryArrayVisitor();
                visitor.Visit(chunk);
                Values[j].AuxiliaryArrays.AddRange(visitor.Values);
            }
        }
        else throw new NotImplementedException();
    }

    public SpectrumInfo CreateFromIndex(ulong index)
    {
        return new SpectrumInfo(index, "", 0.0, 0);
    }

    public void VisitIndex(IArrowArray array)
    {
        UInt64Array arr = (UInt64Array)array;
        for (int i = 0; i < arr.Length; i++)
        {
            var idx = arr.GetValue(i);
            if (idx == null) continue;
            Offsets.Add(i);
            Values.Add(CreateFromIndex((ulong)idx));
        }
    }
}

public class ChromatogramVisitor : IVisitorAssemblyWithOffsets<ChromatogramInfo>, IHasParametersVisitorWithOffsets<ChromatogramInfo>, IArrowArrayVisitor<StructArray>, IArrowArrayVisitor<RecordBatch>
{
    public List<ChromatogramInfo> Values { get; set; }
    public List<int> Offsets { get; set; }

    public ChromatogramVisitor()
    {
        Values = new();
        Offsets = new();
    }

    void VisitId(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.String)
        {
            StringArray arr = (StringArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (arr.IsNull(i)) continue;
                var chunk = arr.GetString(i);
                Values[j].Id = chunk;
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeString)
        {
            LargeStringArray arr = (LargeStringArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (arr.IsNull(i)) continue;
                var chunk = arr.GetString(i);
                Values[j].Id = chunk;
            }
        }
        else throw new NotImplementedException();
    }

    protected void VisitDataProcessingRef(IArrowArray array) { }

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
        Offsets.Clear();

        var dtype = (StructType)array.Data.DataType;

        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "index")
            {
                VisitIndex(arr);
                break;
            }
        }

        var self = (IHasParametersVisitorWithOffsets<ChromatogramInfo>)this;

        foreach (var (f, arr) in dtype.Fields.Zip(array.Fields))
        {
            if (f.Name == "id") VisitId(arr);
            else if (f.Name == "index") { }
            else if (f.Name == "data_processing_ref") VisitDataProcessingRef(arr);
            else if (f.Name == "number_of_auxiliary_arrays") VisitNumberOfAuxiliaryArrays(arr);
            else if (f.Name == "auxiliary_arrays") VisitAuxiliarArrays(arr);
            else if (f.Name == "parameters") self.VisitParameters(arr);
            else self.VisitAsParameter(f, arr);
        }
    }

    protected void VisitNumberOfAuxiliaryArrays(IArrowArray array)
    {
        var self = (IHasParametersVisitorWithOffsets<ChromatogramInfo>)this;
        foreach ((var i, var val) in self.VisitInteger(array))
            Values[i].NumberOfAuxiliaryArrays = (int)(val ?? 0);
    }

    void VisitAuxiliarArrays(IArrowArray array)
    {
        if (array.Data.DataType.TypeId == ArrowTypeId.List)
        {
            ListArray arr = (ListArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (arr.IsNull(i)) continue;
                var chunk = arr.GetSlicedValues(i);
                var visitor = new AuxiliaryArrayVisitor();
                visitor.Visit(chunk);
                Values[j].AuxiliaryArrays.AddRange(visitor.Values);
            }
        }
        else if (array.Data.DataType.TypeId == ArrowTypeId.LargeList)
        {
            LargeListArray arr = (LargeListArray)array;
            for (int j = 0; j < Offsets.Count; j++)
            {
                var i = Offsets[j];
                if (arr.IsNull(i)) continue;
                var chunk = arr.GetSlicedValues(i);
                var visitor = new AuxiliaryArrayVisitor();
                visitor.Visit(chunk);
                Values[j].AuxiliaryArrays.AddRange(visitor.Values);
            }
        }
        else throw new NotImplementedException();
    }

    public ChromatogramInfo CreateFromIndex(ulong index)
    {
        return new ChromatogramInfo(index, "");
    }

    public void VisitIndex(IArrowArray array)
    {
        UInt64Array arr = (UInt64Array)array;
        for (int i = 0; i < arr.Length; i++)
        {
            var idx = arr.GetValue(i);
            if (idx == null) continue;
            Offsets.Add(i);
            Values.Add(CreateFromIndex((ulong)idx));
        }
    }
}