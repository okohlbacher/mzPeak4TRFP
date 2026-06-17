using Apache.Arrow;
using Apache.Arrow.Types;
using MZPeak.ControlledVocabulary;
using MZPeak.Metadata;
using MZPeak.Writer.Data;

namespace MZPeak.Writer.Visitors;

public class IsolationWindowBuilder : ParamVisitorCollection, IArrowBuilder<List<Param>>
{
    public int Length => ParamVisitors[0].Length;

    public IsolationWindowBuilder() : base(new()
    {
        new CustomBuilderFromParam(IsolationWindowProperties.IsolationWindowTargetMZ.CURIE(), IsolationWindowProperties.IsolationWindowTargetMZ.Name(), new DoubleType(), Unit.MZ.CURIE()),
        new CustomBuilderFromParam(IsolationWindowProperties.IsolationWindowLowerOffset.CURIE(), IsolationWindowProperties.IsolationWindowLowerOffset.Name(), new DoubleType(), Unit.MZ.CURIE()),
        new CustomBuilderFromParam(IsolationWindowProperties.IsolationWindowUpperOffset.CURIE(), IsolationWindowProperties.IsolationWindowUpperOffset.Name(), new DoubleType(), Unit.MZ.CURIE()),
    })
    { }

    public void Append(List<Param> value)
    {
        VisitParameters(value);
    }

    public List<Field> ArrowType()
    {
        var fields = new List<Field>();
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.ArrowType());
        }
        fields.AddRange(ParamList.ArrowType());
        FreezeSchema();
        return new() { new Field("isolation_window", new StructType(fields), true) };
    }

    public List<IArrowArray> Build()
    {
        var fields = new List<IArrowArray>();
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.Build());
        }
        fields.AddRange(ParamList.Build());
        return new() { new StructArray(ArrowType()[0].DataType, fields[0].Length, fields, default) };
    }

    public void Clear()
    {
        foreach (var vis in ParamVisitors)
        {
            vis.Clear();
        }
        ParamList.Clear();
    }
}

public class ActivationBuilder : ParamVisitorCollection, IArrowBuilder<List<Param>>
{
    public int Length => ParamVisitors[0].Length;

    public ActivationBuilder() : base(new()
    {
        new CustomBuilderFromParam("MS:1000045", "collision energy", new DoubleType(), "UO:0000266"),
        new ChildTermParamBuilder(DissociationMethod.DissociationMethod.CURIE(),
                                  DissociationMethod.DissociationMethod.Name(),
                                  DissociationMethodMethods.FromCURIE.Keys.ToList()),
    })
    { }

    public void Append(List<Param> value)
    {
        VisitParameters(value);
    }

    public List<Field> ArrowType()
    {
        var fields = new List<Field>();
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.ArrowType());
        }
        fields.AddRange(ParamList.ArrowType());
        FreezeSchema();
        return new() { new Field("activation", new StructType(fields), true) };
    }

    public List<IArrowArray> Build()
    {
        var fields = new List<IArrowArray>();
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.Build());
        }
        fields.AddRange(ParamList.Build());
        return new() { new StructArray(ArrowType()[0].DataType, fields[0].Length, fields, default) };
    }

    public void Clear()
    {
        foreach (var vis in ParamVisitors)
        {
            vis.Clear();
        }
        ParamList.Clear();
    }
}

public class PrecursorBuilder : IArrowBuilder<(ulong, ulong?, string?, List<Param>, List<Param>)>
{
    UInt64Array.Builder SourceIndex;
    UInt64Array.Builder PrecursorIndex;
    StringArray.Builder PrecursorId;
    IsolationWindowBuilder IsolationWindow;
    ActivationBuilder Activation;

    public int Length => SourceIndex.Length;

    public PrecursorBuilder()
    {
        SourceIndex = new();
        PrecursorIndex = new();
        PrecursorId = new();
        IsolationWindow = new();
        Activation = new();
    }

    public void Append((ulong, ulong?, string?, List<Param>, List<Param>) value)
    {
        Append(value.Item1, value.Item2, value.Item3, value.Item4, value.Item5);
    }

    public void Append(ulong sourceIndex, ulong? precursorIndex, string? precursorId, List<Param> isolationWindowParams, List<Param> activationParams)
    {
        SourceIndex.Append(sourceIndex);
        PrecursorIndex.Append(precursorIndex);
        if (precursorId != null) PrecursorId.Append(precursorId);
        else PrecursorId.AppendNull();
        IsolationWindow.Append(isolationWindowParams);
        Activation.Append(activationParams);
    }

    public void AppendNull()
    {
        SourceIndex.AppendNull();
        PrecursorIndex.AppendNull();
        PrecursorId.AppendNull();
        IsolationWindow.AppendNull();
        Activation.AppendNull();
    }

    public List<Field> ArrowType()
    {
        var fields = new List<Field>()
        {
            new Field("source_index", new UInt64Type(), true),
            new Field("precursor_index", new UInt64Type(), true),
            new Field("precursor_id", new StringType(), true)
        };
        fields.AddRange(IsolationWindow.ArrowType());
        fields.AddRange(Activation.ArrowType());
        return new() { new Field("precursor", new StructType(fields), true) };
    }

    public List<IArrowArray> Build()
    {
        List<IArrowArray> fields =
        [
            SourceIndex.Build(),
            PrecursorIndex.Build(),
            PrecursorId.Build(),
            .. IsolationWindow.Build(),
            .. Activation.Build(),
        ];
        var size = SourceIndex.Length;
        Clear();
        return new() { new StructArray(ArrowType()[0].DataType, size, fields, default) };
    }

    public void Clear()
    {
        SourceIndex.Clear();
        PrecursorIndex.Clear();
        PrecursorId.Clear();
        IsolationWindow.Clear();
        Activation.Clear();
    }
}

public class SpectrumBuilder : ParamVisitorCollection, IArrowBuilder<(ulong, string, double, string?, List<Param>, EntryDerivedMetadata)>
{
    UInt64Array.Builder Index;
    StringArray.Builder Id;
    DoubleArray.Builder Time;
    StringArray.Builder DataProcessingRef;
    ListArray.Builder MzDeltaModel;
    UInt32Array.Builder NumberOfAuxiliaryArrays;
    AuxiliaryArrayListBuilder AuxiliaryArrays;

    public int Length => Index.Length;

    public SpectrumBuilder() : base(new()
    {
        // Required CV terms
        new CustomBuilderFromParam("MS:1000511", "ms level", new Int8Type()),
        new ChildTermParamBuilder("MS:1000525", "spectrum representation", [
            SpectrumRepresentation.CentroidSpectrum.CURIE(),
            SpectrumRepresentation.ProfileSpectrum.CURIE()
        ]),
        new CustomBuilderFromParam("MS:1000465", "scan polarity", new Int8Type()),
        // Required CV term "spectrum type" (MS:1000559) is checked schema-only against inflected
        // column NAMES, with use_term=false + allow_children: the column must be keyed on a concrete
        // child accession (MS:1000294 "mass spectrum"), not the abstract parent MS:1000559.
        new CustomBuilderFromParam("MS:1000294", "mass spectrum", new StringType()),

        // Optional spectrum properties (commonly present)
        new CustomBuilderFromParam(SpectrumProperties.NumberOfDataPoints.CURIE(), SpectrumProperties.NumberOfDataPoints.Name(), new UInt64Type()),
        new CustomBuilderFromParam(SpectrumProperties.NumberOfPeaks.CURIE(), SpectrumProperties.NumberOfPeaks.Name(), new UInt64Type()),
        new CustomBuilderFromParam(SpectrumProperties.BasePeakMZ.CURIE(), SpectrumProperties.BasePeakMZ.Name(), new DoubleType(), Unit.MZ.CURIE()),
        new CustomBuilderFromParam(SpectrumProperties.BasePeakIntensity.CURIE(), SpectrumProperties.BasePeakIntensity.Name(), new DoubleType(), Unit.NumberOfDetectorCounts.CURIE()),
        new CustomBuilderFromParam(SpectrumProperties.TotalIonCurrent.CURIE(), SpectrumProperties.TotalIonCurrent.Name(), new DoubleType(), Unit.NumberOfDetectorCounts.CURIE()),
        new CustomBuilderFromParam(SpectrumProperties.LowestObservedMZ.CURIE(), SpectrumProperties.LowestObservedMZ.Name(), new DoubleType(), Unit.MZ.CURIE()),
        new CustomBuilderFromParam(SpectrumProperties.HighestObservedMZ.CURIE(), SpectrumProperties.HighestObservedMZ.Name(), new DoubleType(), Unit.MZ.CURIE()),
    })
    {
        Index = new();
        Id = new();
        Time = new();
        DataProcessingRef = new();
        NumberOfAuxiliaryArrays = new();
        MzDeltaModel = new ListArray.Builder(new DoubleType());
        MzDeltaModel.Append();
        AuxiliaryArrays = new();
    }

    public void Append((ulong, string, double, string?, List<Param>, EntryDerivedMetadata) value)
    {
        Append(value.Item1, value.Item2, value.Item3, value.Item4, value.Item5, value.Item6);
    }

    public void Append(ulong index, string id, double time, string? dataProcessingRef, List<Param> parameters, EntryDerivedMetadata entryMeta)
    {
        Index.Append(index);
        Id.Append(id);
        Time.Append(time);
        if (dataProcessingRef != null) DataProcessingRef.Append(dataProcessingRef);
        else DataProcessingRef.AppendNull();
        var p = parameters.FindCURIE(SpectrumProperties.NumberOfDataPoints.CURIE());
        if (entryMeta?.DataPointCount != null)
        {
            if (p == null)
            {
                parameters.Add(SpectrumProperties.NumberOfDataPoints.Param(entryMeta.DataPointCount));
            }
            else
            {
                p.rawValue = entryMeta.DataPointCount;
            }
        }
        p = parameters.FindCURIE(SpectrumProperties.NumberOfPeaks.CURIE());
        if (entryMeta?.PeakCount != null)
        {
            if (p == null)
            {
                parameters.Add(SpectrumProperties.NumberOfPeaks.Param(entryMeta.PeakCount));
            }
            else
            {
                p.rawValue = entryMeta.PeakCount;
            }
        }
        NumberOfAuxiliaryArrays.Append((uint)(entryMeta?.AuxiliaryArrays.Count ?? 0));
        AuxiliaryArrays.Append(entryMeta?.AuxiliaryArrays ?? []);
        if (entryMeta?.SpacingInterpolationModel != null)
        {
            var valueBuilder = (DoubleArray.Builder)MzDeltaModel.ValueBuilder;
            foreach (var v in entryMeta.SpacingInterpolationModel.Coefficients)
            {
                valueBuilder.Append(v);
            }
            MzDeltaModel.Append();
        }
        else
        {
            // Parquet C++ library does not support writing interleaved null and non-null values https://github.com/apache/arrow/issues/24425
            // MzDeltaModel.AppendNull();
            MzDeltaModel.Append();
        }
        VisitParameters(parameters);
    }

    public override void AppendNull()
    {
        Index.AppendNull();
        Id.AppendNull();
        Time.AppendNull();
        DataProcessingRef.AppendNull();
        NumberOfAuxiliaryArrays.AppendNull();
        MzDeltaModel.AppendNull();
        AuxiliaryArrays.AppendNull();
        base.AppendNull();
    }

    public List<Field> ArrowType()
    {
        var fields = new List<Field>()
        {
            new Field("index", new UInt64Type(), true),
            new Field("id", new StringType(), true),
            new Field("time", new DoubleType(), true),
        };
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.ArrowType());
        }
        fields.AddRange(ParamList.ArrowType());
        fields.AddRange([
            new Field("data_processing_ref", new StringType(), true),
            new Field("mz_delta_model", new ListType(new DoubleType()), true),
            new Field("number_of_auxiliary_arrays", new UInt32Type(), true),
            new Field("auxiliary_arrays", AuxiliaryArrays.ArrowType()[0].DataType, true)
        ]);
        FreezeSchema();
        return new() { new Field("spectrum", new StructType(fields), true) };
    }

    public List<IArrowArray> Build()
    {
        List<IArrowArray> fields = new()
        {
            Index.Build(),
            Id.Build(),
            Time.Build(),
        };
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.Build());
        }
        fields.AddRange(ParamList.Build());
        fields.AddRange([
            DataProcessingRef.Build(),
            MzDeltaModel.Build(),
            NumberOfAuxiliaryArrays.Build(),
            AuxiliaryArrays.Build()[0]
        ]);
        var size = Index.Length;

        return new() { new StructArray(ArrowType()[0].DataType, size, fields, default) };
    }

    public void Clear()
    {
        Index.Clear();
        Id.Clear();
        Time.Clear();
        DataProcessingRef.Clear();
        NumberOfAuxiliaryArrays.Clear();
        MzDeltaModel.Clear();
        AuxiliaryArrays.Clear();
        foreach (var vis in ParamVisitors)
        {
            vis.Clear();
        }
        ParamList.Clear();
    }
}

public class ScanBuilder : ParamVisitorCollection, IArrowBuilder<(ulong, uint?, double?, string?, ulong?, string?, List<Param>, List<List<Param>>)>
{
    UInt64Array.Builder SourceIndex;
    UInt64Array.Builder ScanIndex;
    UInt32Array.Builder InstrumentConfigurationRef;
    DoubleArray.Builder IonMobility;
    StringArray.Builder IonMobilityType;
    StringArray.Builder SpectrumReference;
    ScanWindowListBuilder ScanWindowListBuilder;

    public int Length => SourceIndex.Length;

    public ScanBuilder() : base(new()
    {
        new CustomBuilderFromParam(ScanAttribute.ScanStartTime.CURIE(), ScanAttribute.ScanStartTime.Name(), new DoubleType(), Unit.Minute.CURIE()),
        new CustomBuilderFromParam(ScanAttribute.FilterString.CURIE(), ScanAttribute.FilterString.Name(), new StringType()),
        new CustomBuilderFromParam(ScanAttribute.PresetScanConfiguration.CURIE(), ScanAttribute.PresetScanConfiguration.CURIE(), new Int64Type()),
        new CustomBuilderFromParam(ScanAttribute.IonInjectionTime.CURIE(), ScanAttribute.IonInjectionTime.Name(), new DoubleType(), Unit.Millisecond.CURIE()),
    })
    {
        // TODO: Fill in the scan index and spectrum reference arrays
        SourceIndex = new();
        ScanIndex = new();
        SpectrumReference = new();
        InstrumentConfigurationRef = new();
        IonMobility = new();
        IonMobilityType = new();
        ScanWindowListBuilder = new(fixedUnit: Unit.MZ);
    }

    public void Append((ulong, uint?, double?, string?, ulong?, string?, List<Param>, List<List<Param>>) value)
    {
        Append(value.Item1, value.Item2, value.Item3, value.Item4, value.Item5, value.Item6, value.Item7, value.Item8);
    }

    public void Append(ulong sourceIndex, uint? instrumentConfigurationRef, double? ionMobility, string? ionMobilityType, ulong? scanIndex=null, string? spectrumReference=null, List<Param>? parameters = null, List<List<Param>>? scanWindows = null)
    {
        SourceIndex.Append(sourceIndex);
        if (instrumentConfigurationRef != null) InstrumentConfigurationRef.Append(instrumentConfigurationRef);
        else InstrumentConfigurationRef.AppendNull();

        if (ionMobility.HasValue) IonMobility.Append(ionMobility.Value);
        else IonMobility.AppendNull();

        if (ionMobilityType != null) IonMobilityType.Append(ionMobilityType);
        else IonMobilityType.AppendNull();

        if (scanIndex != null) ScanIndex.Append(scanIndex);
        else ScanIndex.AppendNull();

        if (spectrumReference == null) SpectrumReference.AppendNull();
        else SpectrumReference.Append(spectrumReference);

        ScanWindowListBuilder.Append(scanWindows ?? new());
        VisitParameters(parameters ?? []);
    }

    public override void AppendNull()
    {
        SourceIndex.AppendNull();
        InstrumentConfigurationRef.AppendNull();
        IonMobility.AppendNull();
        IonMobilityType.AppendNull();
        SpectrumReference.AppendNull();
        ScanIndex.AppendNull();
        base.AppendNull();
        ScanWindowListBuilder.Append();
    }

    public List<Field> ArrowType()
    {
        var fields = new List<Field>()
        {
            new Field("source_index", new UInt64Type(), true),
            new Field("scan_index", new UInt64Type(), true),
            new Field("spectrum_reference", new StringType(), true),
            new Field("instrument_configuration_ref", new UInt32Type(), true),
            new Field("ion_mobility_value", new DoubleType(), true),
            new Field("ion_mobility_type", new StringType(), true)
        };

        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.ArrowType());
        }
        fields.AddRange(ParamList.ArrowType());
        fields.AddRange(ScanWindowListBuilder.ArrowType());
        FreezeSchema();
        return new() { new Field("scan", new StructType(fields), true) };
    }

    public List<IArrowArray> Build()
    {
        List<IArrowArray> fields =
        [
            SourceIndex.Build(),
            ScanIndex.Build(),
            SpectrumReference.Build(),
            InstrumentConfigurationRef.Build(),
            IonMobility.Build(),
            IonMobilityType.Build(),
        ];

        foreach (var vis in ParamVisitors)
            fields.AddRange(vis.Build());

        fields.AddRange(ParamList.Build());
        fields.AddRange(ScanWindowListBuilder.Build());
        var size = SourceIndex.Length;
        return new() { new StructArray(ArrowType()[0].DataType, size, fields, default) };
    }

    public void Clear()
    {
        SourceIndex.Clear();
        ScanIndex.Clear();
        SpectrumReference.Clear();
        InstrumentConfigurationRef.Clear();
        IonMobility.Clear();
        IonMobilityType.Clear();
        ScanWindowListBuilder.Clear();
        foreach (var vis in ParamVisitors)
            vis.Clear();
        ParamList.Clear();
    }
}

public class ScanWindowBuilder : ParamVisitorCollection, IArrowBuilder<List<Param>>
{
    public ScanWindowBuilder(List<CustomBuilderFromParam>? paramVisitors = null, Unit? fixedUnit = null) : base([
        new CustomBuilderFromParam("MS:1000501", "scan window lower limit", new DoubleType(), fixedUnit?.CURIE()),
        new CustomBuilderFromParam("MS:1000500", "scan window upper limit", new DoubleType(), fixedUnit?.CURIE()),
    ])
    {
        ParamVisitors.AddRange(paramVisitors ?? new());
    }

    public int Length => ParamList.Length;

    public void Append(List<Param> value)
    {
        VisitParameters(value);
    }

    public List<Field> ArrowType()
    {
        var fields = new List<Field>()
        { };
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.ArrowType());
        }
        fields.AddRange(ParamList.ArrowType());
        FreezeSchema();
        return new() { new Field("scanWindow", new StructType(fields), true) };
    }

    public List<IArrowArray> Build()
    {
        var size = ParamList.Length;
        List<IArrowArray> fields = new();
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.Build());
        }
        fields.AddRange(ParamList.Build());
        return new() { new StructArray(ArrowType()[0].DataType, size, fields, default) };
    }

    public void Clear()
    {
        foreach (var vis in ParamVisitors)
            vis.Clear();
        ParamList.Clear();
    }
}

public class ScanWindowListBuilder : IArrowBuilder<List<List<Param>>>
{
    public ScanWindowBuilder ValueBuilder { get; }
    private ArrowBuffer.Builder<int> ValueOffsetsBufferBuilder { get; }

    private ArrowBuffer.BitmapBuilder ValidityBufferBuilder { get; }
    public int NullCount { get; protected set; }

    public int Length => ValueOffsetsBufferBuilder.Length;

    public ScanWindowListBuilder(List<CustomBuilderFromParam>? paramVisitors = null, Unit? fixedUnit = null)
    {
        ValueBuilder = new(paramVisitors, fixedUnit);
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

    public void Append(List<List<Param>> arrays)
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
            new Field("scan_windows", new ListType(ValueBuilder.ArrowType()[0].DataType), true)
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

public class SelectedIonBuilder : ParamVisitorCollection, IArrowBuilder<(ulong, ulong?, List<Param>)>
{
    UInt64Array.Builder SourceIndex;
    UInt64Array.Builder PrecursorIndex;
    DoubleArray.Builder IonMobility;
    StringArray.Builder IonMobilityType;

    public int Length => SourceIndex.Length;

    public SelectedIonBuilder() : base(new()
        {
            new CustomBuilderFromParam("MS:1000744", "selected ion m/z", new DoubleType(), "MS:1000040"),
            new CustomBuilderFromParam("MS:1000042", "peak intensity", new DoubleType(), "MS:1000131"),
            new CustomBuilderFromParam("MS:1000041", "charge state", new Int64Type())
        })
    {
        SourceIndex = new();
        PrecursorIndex = new();
        IonMobility = new();
        IonMobilityType = new();
    }

    public void Append((ulong, ulong?, List<Param>) value)
    {
        Append(value.Item1, value.Item2, null, null, value.Item3);
    }

    public void Append(ulong sourceIndex, ulong? precursorIndex, double? ionMobility, string? ionMobilityType, List<Param> parameters)
    {
        SourceIndex.Append(sourceIndex);
        PrecursorIndex.Append(precursorIndex);
        if (ionMobility.HasValue) IonMobility.Append(ionMobility.Value);
        else IonMobility.AppendNull();
        if (ionMobilityType != null) IonMobilityType.Append(ionMobilityType);
        else IonMobilityType.AppendNull();
        VisitParameters(parameters);
    }

    public override void AppendNull()
    {
        SourceIndex.AppendNull();
        PrecursorIndex.AppendNull();
        IonMobility.AppendNull();
        IonMobilityType.AppendNull();
        base.AppendNull();
    }

    public List<Field> ArrowType()
    {
        var fields = new List<Field>()
        {
            new Field("source_index", new UInt64Type(), true),
            new Field("precursor_index", new UInt64Type(), true),
            new Field("ion_mobility_value", new DoubleType(), true),
            new Field("ion_mobility_type", new StringType(), true)
        };
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.ArrowType());
        }
        fields.AddRange(ParamList.ArrowType());
        FreezeSchema();
        return new() { new Field("selected_ion", new StructType(fields), true) };
    }

    public List<IArrowArray> Build()
    {
        var tp = ArrowType()[0];
        List<IArrowArray> fields = new() { SourceIndex.Build(), PrecursorIndex.Build(), IonMobility.Build(), IonMobilityType.Build() };
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.Build());
        }
        fields.AddRange(ParamList.Build());
        var size = SourceIndex.Length;
        return new() { new StructArray(tp.DataType, size, fields, default) };
    }

    public void Clear()
    {
        SourceIndex.Clear();
        PrecursorIndex.Clear();
        IonMobility.Clear();
        IonMobilityType.Clear();
        foreach (var vis in ParamVisitors)
        {
            vis.Clear();
        }
        ParamList.Clear();
    }
}

public class ChromatogramBuilder : ParamVisitorCollection, IArrowBuilder<(ulong, string, string?, List<Param>, EntryDerivedMetadata?)>
{
    UInt64Array.Builder Index;
    StringArray.Builder Id;
    StringArray.Builder DataProcessingRef;
    UInt32Array.Builder NumberOfAuxiliaryArrays;
    AuxiliaryArrayListBuilder AuxiliaryArrays;

    public int Length => Index.Length;

    public ChromatogramBuilder() : base(new()
    {
        // Required CV terms
        new CustomBuilderFromParam("MS:1000465", "scan polarity", new Int8Type()),
        new ChildTermParamBuilder(
            "MS:1000626",
            "chromatogram type",
            ChromatogramTypesMethods.FromCURIE.Values.Select(v => v.CURIE()).ToList()
        ),
        // Optional properties (commonly present)
        new CustomBuilderFromParam("MS:1003060", "number of data points", new UInt64Type()),
    })
    {
        Index = new();
        Id = new();
        DataProcessingRef = new();
        NumberOfAuxiliaryArrays = new();
        AuxiliaryArrays = new();
    }

    public void Append((ulong, string, string?, List<Param>, EntryDerivedMetadata?) value)
    {
        Append(value.Item1, value.Item2, value.Item3, value.Item4, value.Item5);
    }

    public void Append(ulong index, string id, string? dataProcessingRef, List<Param> parameters, EntryDerivedMetadata? entryDerivedMetadata = null)
    {
        Index.Append(index);
        Id.Append(id);
        if (dataProcessingRef != null) DataProcessingRef.Append(dataProcessingRef);
        else DataProcessingRef.AppendNull();
        var p = parameters.FindCURIE(SpectrumProperties.NumberOfDataPoints.CURIE());
        if (entryDerivedMetadata?.DataPointCount != null)
        {
            if (p == null)
            {
                parameters.Add(SpectrumProperties.NumberOfDataPoints.Param(entryDerivedMetadata.DataPointCount));
            }
            else
            {
                p.rawValue = entryDerivedMetadata.DataPointCount;
            }
        }
        NumberOfAuxiliaryArrays.Append((uint)(entryDerivedMetadata?.AuxiliaryArrays.Count ?? 0));
        AuxiliaryArrays.Append(entryDerivedMetadata?.AuxiliaryArrays ?? []);
        VisitParameters(parameters);
    }

    public override void AppendNull()
    {
        Index.AppendNull();
        Id.AppendNull();
        DataProcessingRef.AppendNull();
        NumberOfAuxiliaryArrays.AppendNull();
        AuxiliaryArrays.AppendNull();
        base.AppendNull();
    }

    public List<Field> ArrowType()
    {
        var fields = new List<Field>()
        {
            new Field("index", new UInt64Type(), true),
            new Field("id", new StringType(), true),
        };
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.ArrowType());
        }
        fields.AddRange(ParamList.ArrowType());
        fields.AddRange([
            new Field("data_processing_ref", new StringType(), true),
            new Field("number_of_auxiliary_arrays", new UInt32Type(), true),
            new Field("auxiliary_arrays", AuxiliaryArrays.ArrowType()[0].DataType, true)
        ]);
        FreezeSchema();
        return new() { new Field("chromatogram", new StructType(fields), true) };
    }

    public List<IArrowArray> Build()
    {
        List<IArrowArray> fields = new()
        {
            Index.Build(),
            Id.Build(),
        };
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.Build());
        }
        fields.AddRange(ParamList.Build());
        fields.AddRange([
            DataProcessingRef.Build(),
            NumberOfAuxiliaryArrays.Build(),
            AuxiliaryArrays.Build()[0]
        ]);
        var size = Index.Length;

        return new() { new StructArray(ArrowType()[0].DataType, size, fields, default) };
    }

    public void Clear()
    {
        Index.Clear();
        Id.Clear();
        DataProcessingRef.Clear();
        NumberOfAuxiliaryArrays.Clear();
        AuxiliaryArrays.Clear();
        foreach (var vis in ParamVisitors)
        {
            vis.Clear();
        }
        ParamList.Clear();
    }
}

public class WavelengthSpectrumBuilder : ParamVisitorCollection, IArrowBuilder<(ulong, string, double, string?, List<Param>, EntryDerivedMetadata?)>
{
    UInt64Array.Builder Index;
    StringArray.Builder Id;
    DoubleArray.Builder Time;
    StringArray.Builder DataProcessingRef;
    UInt32Array.Builder NumberOfAuxiliaryArrays;
    AuxiliaryArrayListBuilder AuxiliaryArrays;

    public int Length => Index.Length;

    public WavelengthSpectrumBuilder() : base(new()
    {
        // Required CV terms
        new ChildTermParamBuilder("MS:1000525", "spectrum representation", [
            SpectrumRepresentation.CentroidSpectrum.CURIE(),
            SpectrumRepresentation.ProfileSpectrum.CURIE()
        ]),
        new CustomBuilderFromParam("MS:1000559", "spectrum type", new StringType()),

        // Optional spectrum properties (commonly present)
        new CustomBuilderFromParam(SpectrumProperties.NumberOfDataPoints.CURIE(), SpectrumProperties.NumberOfDataPoints.Name(), new UInt64Type()),
        new CustomBuilderFromParam("MS:1000504", "base peak m/z", new DoubleType(), Unit.Nanometer.CURIE()),
        new CustomBuilderFromParam("MS:1000505", "base peak intensity", new DoubleType(), "MS:1000131"),
        new CustomBuilderFromParam("MS:1000285", "total ion current", new DoubleType(), "MS:1000131"),
        new CustomBuilderFromParam("MS:1000619", "lowest observed wavelength", new DoubleType(), Unit.Nanometer.CURIE()),
        new CustomBuilderFromParam("MS:1000618", "highest observed wavelength", new DoubleType(), Unit.Nanometer.CURIE()),
    })
    {
        Index = new();
        Id = new();
        Time = new();
        DataProcessingRef = new();
        NumberOfAuxiliaryArrays = new();
        AuxiliaryArrays = new();
    }

    public void Append((ulong, string, double, string?, List<Param>, EntryDerivedMetadata?) value)
    {
        Append(value.Item1, value.Item2, value.Item3, value.Item4, value.Item5, value.Item6);
    }

    public void Append(ulong index, string id, double time, string? dataProcessingRef, List<Param> parameters, EntryDerivedMetadata? entryDerivedMetadata = null)
    {
        Index.Append(index);
        Id.Append(id);
        Time.Append(time);
        if (dataProcessingRef != null) DataProcessingRef.Append(dataProcessingRef);
        else DataProcessingRef.AppendNull();
        var p = parameters.FindCURIE(SpectrumProperties.NumberOfDataPoints.CURIE());
        if (entryDerivedMetadata?.DataPointCount != null)
        {
            if (p == null)
            {
                parameters.Add(SpectrumProperties.NumberOfDataPoints.Param(entryDerivedMetadata.DataPointCount));
            }
            else
            {
                p.rawValue = entryDerivedMetadata.DataPointCount;
            }
        }
        NumberOfAuxiliaryArrays.Append((uint)(entryDerivedMetadata?.AuxiliaryArrays.Count ?? 0));
        AuxiliaryArrays.Append(entryDerivedMetadata?.AuxiliaryArrays ?? []);
        VisitParameters(parameters);
    }

    public override void AppendNull()
    {
        Index.AppendNull();
        Id.AppendNull();
        Time.AppendNull();
        DataProcessingRef.AppendNull();
        NumberOfAuxiliaryArrays.AppendNull();
        AuxiliaryArrays.AppendNull();
        base.AppendNull();
    }

    public List<Field> ArrowType()
    {
        var fields = new List<Field>()
        {
            new Field("index", new UInt64Type(), true),
            new Field("id", new StringType(), true),
            new Field("time", new DoubleType(), true),
        };
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.ArrowType());
        }
        fields.AddRange(ParamList.ArrowType());
        fields.AddRange([
            new Field("data_processing_ref", new StringType(), true),
            new Field("number_of_auxiliary_arrays", new UInt32Type(), true),
            new Field("auxiliary_arrays", AuxiliaryArrays.ArrowType()[0].DataType, true)
        ]);
        FreezeSchema();
        return new() { new Field("spectrum", new StructType(fields), true) };
    }

    public List<IArrowArray> Build()
    {
        List<IArrowArray> fields = new()
        {
            Index.Build(),
            Id.Build(),
            Time.Build(),
        };
        foreach (var vis in ParamVisitors)
        {
            fields.AddRange(vis.Build());
        }
        fields.AddRange(ParamList.Build());
        fields.AddRange([
            DataProcessingRef.Build(),
            NumberOfAuxiliaryArrays.Build(),
            AuxiliaryArrays.Build()[0]
        ]);
        var size = Index.Length;

        return new() { new StructArray(ArrowType()[0].DataType, size, fields, default) };
    }

    public void Clear()
    {
        Index.Clear();
        Id.Clear();
        Time.Clear();
        DataProcessingRef.Clear();
        NumberOfAuxiliaryArrays.Clear();
        AuxiliaryArrays.Clear();
        foreach (var vis in ParamVisitors)
        {
            vis.Clear();
        }
        ParamList.Clear();
    }
}
