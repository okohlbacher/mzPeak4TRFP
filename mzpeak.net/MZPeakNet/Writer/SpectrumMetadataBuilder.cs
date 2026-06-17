using Apache.Arrow;
using Apache.Arrow.Types;
using MZPeak.ControlledVocabulary;
using MZPeak.Metadata;
using MZPeak.Writer.Data;

namespace MZPeak.Writer.Visitors;

/// <summary>
/// Top-level builder for spectrum metadata tables following the mzPeak packed parallel table layout.
/// Composes SpectrumBuilder, ScanBuilder, PrecursorBuilder, and SelectedIonBuilder to create
/// a complete metadata table with foreign key relationships between sub-groups.
/// </summary>
public class SpectrumMetadataBuilder
{
    public SpectrumBuilder Spectrum { get; }
    public ScanBuilder Scan { get; }
    public PrecursorBuilder Precursor { get; }
    public SelectedIonBuilder SelectedIon { get; }
    public ulong SpectrumCounter { get; protected set; }
    public ulong ScanCounter { get; protected set; }
    public int Length { get; private set; }

    public SpectrumMetadataBuilder()
    {
        Spectrum = new();
        Scan = new();
        Precursor = new();
        SelectedIon = new();
        SpectrumCounter = 0;
    }

    /// <summary>
    /// Append a spectrum row with all associated metadata.
    /// </summary>
    public ulong AppendSpectrum(
        string id,
        double time,
        string? dataProcessingRef,
        List<Param> spectrumParams,
        EntryDerivedMetadata entryMetadata
    )
    {
        Spectrum.Append(SpectrumCounter, id, time, dataProcessingRef, spectrumParams, entryMetadata);
        var index = SpectrumCounter;
        SpectrumCounter += 1;
        return index;
    }

    /// <summary>
    /// Append a null spectrum row (for packed parallel table alignment).
    /// </summary>
    public void AppendNull()
    {
        Spectrum.AppendNull();
        Scan.AppendNull();
        Precursor.AppendNull();
        SelectedIon.AppendNull();
    }

    /// <summary>
    /// Append a scan row associated with a spectrum.
    /// </summary>
    public void AppendScan(
        ulong sourceIndex,
        uint? instrumentConfigurationRef,
        double? ionMobility,
        string? ionMobilityType,
        ulong? scanIndex=null,
        string? spectrumReference=null,
        List<Param>? scanParams = null,
        List<List<Param>>? scanWindows = null
    )
    {
        if (sourceIndex >= SpectrumCounter) throw new InvalidOperationException($"Source index {sourceIndex} is greater than {SpectrumCounter - 1}");
        if (scanIndex == null)
        {
            scanIndex = ScanCounter;
            ScanCounter += 1;
        }
        Scan.Append(sourceIndex, instrumentConfigurationRef, ionMobility, ionMobilityType, scanIndex, spectrumReference, scanParams, scanWindows);
    }

    /// <summary>
    /// Append a precursor row associated with a spectrum.
    /// </summary>
    public void AppendPrecursor(
        ulong sourceIndex,
        ulong? precursorIndex,
        string? precursorId,
        List<Param> isolationWindowParams,
        List<Param> activationParams
    )
    {
        if (sourceIndex >= SpectrumCounter) throw new InvalidOperationException($"Source index {sourceIndex} is greater than {SpectrumCounter - 1}");
        if (precursorIndex >= SpectrumCounter) throw new InvalidOperationException($"Precursor index {precursorIndex} is greater than {SpectrumCounter - 1}");
        Precursor.Append(sourceIndex, precursorIndex, precursorId, isolationWindowParams, activationParams);
    }

    /// <summary>
    /// Append a selected ion row associated with a precursor.
    /// </summary>
    public void AppendSelectedIon(
        ulong sourceIndex,
        ulong? precursorIndex,
        double? ionMobility,
        string? ionMobilityType,
        List<Param> selectedIonParams
    )
    {
        if (sourceIndex >= SpectrumCounter) throw new InvalidOperationException($"Source index {sourceIndex} is greater than {SpectrumCounter - 1}");
        if (precursorIndex >= SpectrumCounter) throw new InvalidOperationException($"Precursor index {precursorIndex} is greater than {SpectrumCounter - 1}");
        SelectedIon.Append(sourceIndex, precursorIndex, ionMobility, ionMobilityType, selectedIonParams);
    }

    /// <summary>
    /// Get the Arrow schema for the packed parallel metadata table.
    /// </summary>
    public Schema ArrowSchema(IReadOnlyDictionary<string, string>? metadata = null)
    {
        var fields = new List<Field>();
        fields.AddRange(Spectrum.ArrowType());
        fields.AddRange(Scan.ArrowType());
        fields.AddRange(Precursor.ArrowType());
        fields.AddRange(SelectedIon.ArrowType());
        return new Schema(fields, metadata);
    }

    /// <summary>
    /// Verify that all sub-builders have the same number of rows.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when facet lengths don't match.</exception>
    public bool ValidateLengths()
    {
        var spectrumLength = Spectrum.Length;
        var scanLength = Scan.Length;
        var precursorLength = Precursor.Length;
        var selectedIonLength = SelectedIon.Length;

        if (spectrumLength != scanLength)
            return false;
        if (spectrumLength != precursorLength)
            return false;
        if (spectrumLength != selectedIonLength)
            return false;
        return true;
    }

    public void EqualizeLengths()
    {
        var spectrumLength = Spectrum.Length;
        var scanLength = Scan.Length;
        var precursorLength = Precursor.Length;
        var selectedIonLength = SelectedIon.Length;
        var nMax = Math.Max(
            Math.Max(spectrumLength, scanLength),
            Math.Max(precursorLength, selectedIonLength)
        );
        while (spectrumLength < nMax)
        {
            Spectrum.AppendNull();
            spectrumLength += 1;
        }
        while (scanLength < nMax)
        {
            Scan.AppendNull();
            scanLength += 1;
        }
        while (precursorLength < nMax)
        {
            Precursor.AppendNull();
            precursorLength += 1;
        }
        while (selectedIonLength < nMax)
        {
            SelectedIon.AppendNull();
            selectedIonLength += 1;
        }
    }

    public void Clear()
    {
        Spectrum.Clear();
        Scan.Clear();
        Precursor.Clear();
        SelectedIon.Clear();
    }

    /// <summary>
    /// Build the packed parallel metadata table as a RecordBatch.
    /// </summary>
    public RecordBatch Build()
    {
        EqualizeLengths();

        var schema = ArrowSchema();
        var arrays = new List<IArrowArray>();

        var spectrumArrays = Spectrum.Build();
        var scanArrays = Scan.Build();
        var precursorArrays = Precursor.Build();
        var selectedIonArrays = SelectedIon.Build();
        int spectrumLength = spectrumArrays[0].Length;

        arrays.AddRange(spectrumArrays);
        arrays.AddRange(scanArrays);
        arrays.AddRange(precursorArrays);
        arrays.AddRange(selectedIonArrays);
        Clear();
        return new RecordBatch(schema, arrays, spectrumLength);
    }

}


public class WavelengthSpectrumMetadataBuilder
{
    public WavelengthSpectrumBuilder Spectrum { get; }
    public ScanBuilder Scan { get; }
    public ulong SpectrumCounter { get; protected set; }

    public int Length { get; private set; }

    public WavelengthSpectrumMetadataBuilder()
    {
        Spectrum = new();
        Scan = new();
        SpectrumCounter = 0;
    }

    /// <summary>
    /// Append a spectrum row with all associated metadata.
    /// </summary>
    public ulong AppendSpectrum(
        string id,
        double time,
        string? dataProcessingRef,
        List<Param> spectrumParams,
        EntryDerivedMetadata? entryDerivedMetadata=null
    )
    {
        Spectrum.Append(SpectrumCounter, id, time, dataProcessingRef, spectrumParams, entryDerivedMetadata);
        var index = SpectrumCounter;
        SpectrumCounter += 1;
        return index;
    }

    /// <summary>
    /// Append a null spectrum row (for packed parallel table alignment).
    /// </summary>
    public void AppendNull()
    {
        Spectrum.AppendNull();
        Scan.AppendNull();
    }

    /// <summary>
    /// Append a scan row associated with a spectrum.
    /// </summary>
    public void AppendScan(
        ulong sourceIndex,
        uint? instrumentConfigurationRef,
        double? ionMobility,
        string? ionMobilityType,
        ulong? scanIndex = null,
        string? spectrumReference = null,
        List<Param>? scanParams=null,
        List<List<Param>>? scanWindows = null
    )
    {
        if (sourceIndex >= SpectrumCounter) throw new InvalidOperationException($"Source index {sourceIndex} is greater than {SpectrumCounter - 1}");
        Scan.Append(sourceIndex, instrumentConfigurationRef, ionMobility, ionMobilityType, scanIndex, spectrumReference, scanParams, scanWindows);
    }


    /// <summary>
    /// Get the Arrow schema for the packed parallel metadata table.
    /// </summary>
    public Schema ArrowSchema(IReadOnlyDictionary<string, string>? metadata = null)
    {
        var fields = new List<Field>();
        fields.AddRange(Spectrum.ArrowType());
        fields.AddRange(Scan.ArrowType());
        return new Schema(fields, metadata);
    }

    /// <summary>
    /// Verify that all sub-builders have the same number of rows.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when facet lengths don't match.</exception>
    public bool ValidateLengths()
    {
        var spectrumLength = Spectrum.Length;
        var scanLength = Scan.Length;

        if (spectrumLength != scanLength)
            return false;
        return true;
    }

    public void EqualizeLengths()
    {
        var spectrumLength = Spectrum.Length;
        var scanLength = Scan.Length;
        var nMax = Math.Max(spectrumLength, scanLength);
        while (spectrumLength < nMax)
        {
            Spectrum.AppendNull();
            spectrumLength += 1;
        }
        while (scanLength < nMax)
        {
            Scan.AppendNull();
            scanLength += 1;
        }
    }

    public void Clear()
    {
        Spectrum.Clear();
        Scan.Clear();
    }

    /// <summary>
    /// Build the packed parallel metadata table as a RecordBatch.
    /// </summary>
    public RecordBatch Build()
    {
        EqualizeLengths();

        var schema = ArrowSchema();
        var arrays = new List<IArrowArray>();

        var spectrumArrays = Spectrum.Build();
        var scanArrays = Scan.Build();
        int spectrumLength = spectrumArrays[0].Length;

        arrays.AddRange(spectrumArrays);
        arrays.AddRange(scanArrays);
        Clear();
        return new RecordBatch(schema, arrays, spectrumLength);
    }

}