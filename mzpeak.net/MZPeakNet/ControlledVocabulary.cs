using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace MZPeak.ControlledVocabulary;

public enum ArrayType
{
    BinaryDataArray,
    MZArray,
    IntensityArray,
    ChargeArray,
    SignalToNoiseArray,
    TimeArray,
    WavelengthArray,
    NonStandardDataArray,
    FlowRateArray,
    PressureArray,
    TemperatureArray,
    MeanChargeArray,
    ResolutionArray,
    BaselineArray,
    NoiseArray,
    SampledNoiseMZArray,
    SampledNoiseIntensityArray,
    SampledNoiseBaselineArray,
    IonMobilityArray,
    MassArray,
    ScanningQuadrupolePositionLowerBoundMZArray,
    ScanningQuadrupolePositionUpperBoundMZArray,
    MeanIonMobilityDriftTimeArray,
    MeanIonMobilityArray,
    MeanInverseReducedIonMobilityArray,
    RawIonMobilityArray,
    RawInverseReducedIonMobilityArray,
    RawIonMobilityDriftTimeArray,
    DeconvolutedIonMobilityArray,
    DeconvolutedInverseReducedIonMobilityArray,
    DeconvolutedIonMobilityDriftTimeArray,
}

public static class ArrayTypeMethods
{
    public static readonly Dictionary<string, ArrayType> FromCURIE = new Dictionary<string, ArrayType>(
        ((ArrayType[])Enum.GetValues(typeof(ArrayType))).Select((v) => new KeyValuePair<string, ArrayType>(v.CURIE(), v))
    );

    public static Param Param(this ArrayType arrayType, string? nonStandardName = null)
    {
        if (arrayType == ArrayType.NonStandardDataArray) return new Param(arrayType.Name(), rawValue: nonStandardName, accession: arrayType.CURIE());
        else return new Param(arrayType.Name(), rawValue: null, accession: arrayType.CURIE());
    }

    public static string Name(this ArrayType arrayType)
    {
        switch (arrayType)
        {
            case ArrayType.BinaryDataArray:
                {
                    return "binary data array";
                }
            case ArrayType.MZArray:
                {
                    return "m/z array";
                }
            case ArrayType.IntensityArray:
                {
                    return "intensity array";
                }
            case ArrayType.ChargeArray:
                {
                    return "charge array";
                }
            case ArrayType.SignalToNoiseArray:
                {
                    return "signal to noise array";
                }
            case ArrayType.TimeArray:
                {
                    return "time array";
                }
            case ArrayType.WavelengthArray:
                {
                    return "wavelength array";
                }
            case ArrayType.NonStandardDataArray:
                {
                    return "non-standard data array";
                }
            case ArrayType.FlowRateArray:
                {
                    return "flow rate array";
                }
            case ArrayType.PressureArray:
                {
                    return "pressure array";
                }
            case ArrayType.TemperatureArray:
                {
                    return "temperature array";
                }
            case ArrayType.MeanChargeArray:
                {
                    return "mean charge array";
                }
            case ArrayType.ResolutionArray:
                {
                    return "resolution array";
                }
            case ArrayType.BaselineArray:
                {
                    return "baseline array";
                }
            case ArrayType.NoiseArray:
                {
                    return "noise array";
                }
            case ArrayType.SampledNoiseMZArray:
                {
                    return "sampled noise m/z array";
                }
            case ArrayType.SampledNoiseIntensityArray:
                {
                    return "sampled noise intensity array";
                }
            case ArrayType.SampledNoiseBaselineArray:
                {
                    return "sampled noise baseline array";
                }
            case ArrayType.IonMobilityArray:
                {
                    return "ion mobility array";
                }
            case ArrayType.MassArray:
                {
                    return "mass array";
                }
            case ArrayType.ScanningQuadrupolePositionLowerBoundMZArray:
                {
                    return "scanning quadrupole position lower bound m/z array";
                }
            case ArrayType.ScanningQuadrupolePositionUpperBoundMZArray:
                {
                    return "scanning quadrupole position upper bound m/z array";
                }
            case ArrayType.MeanIonMobilityDriftTimeArray:
                {
                    return "mean ion mobility drift time array";
                }
            case ArrayType.MeanIonMobilityArray:
                {
                    return "mean ion mobility array";
                }
            case ArrayType.MeanInverseReducedIonMobilityArray:
                {
                    return "mean inverse reduced ion mobility array";
                }
            case ArrayType.RawIonMobilityArray:
                {
                    return "raw ion mobility array";
                }
            case ArrayType.RawInverseReducedIonMobilityArray:
                {
                    return "raw inverse reduced ion mobility array";
                }
            case ArrayType.RawIonMobilityDriftTimeArray:
                {
                    return "raw ion mobility drift time array";
                }
            case ArrayType.DeconvolutedIonMobilityArray:
                {
                    return "deconvoluted ion mobility array";
                }
            case ArrayType.DeconvolutedInverseReducedIonMobilityArray:
                {
                    return "deconvoluted inverse reduced ion mobility array";
                }
            case ArrayType.DeconvolutedIonMobilityDriftTimeArray:
                {
                    return "deconvoluted ion mobility drift time array";
                }
            default:
                throw new InvalidOperationException();
        }
    }

    public static string CURIE(this ArrayType arrayType)
    {
        switch (arrayType)
        {
            case ArrayType.BinaryDataArray:
                {
                    return "MS:1000513";
                }
            case ArrayType.MZArray:
                {
                    return "MS:1000514";
                }
            case ArrayType.IntensityArray:
                {
                    return "MS:1000515";
                }
            case ArrayType.ChargeArray:
                {
                    return "MS:1000516";
                }
            case ArrayType.SignalToNoiseArray:
                {
                    return "MS:1000517";
                }
            case ArrayType.TimeArray:
                {
                    return "MS:1000595";
                }
            case ArrayType.WavelengthArray:
                {
                    return "MS:1000617";
                }
            case ArrayType.NonStandardDataArray:
                {
                    return "MS:1000786";
                }
            case ArrayType.FlowRateArray:
                {
                    return "MS:1000820";
                }
            case ArrayType.PressureArray:
                {
                    return "MS:1000821";
                }
            case ArrayType.TemperatureArray:
                {
                    return "MS:1000822";
                }
            case ArrayType.MeanChargeArray:
                {
                    return "MS:1002478";
                }
            case ArrayType.ResolutionArray:
                {
                    return "MS:1002529";
                }
            case ArrayType.BaselineArray:
                {
                    return "MS:1002530";
                }
            case ArrayType.NoiseArray:
                {
                    return "MS:1002742";
                }
            case ArrayType.SampledNoiseMZArray:
                {
                    return "MS:1002743";
                }
            case ArrayType.SampledNoiseIntensityArray:
                {
                    return "MS:1002744";
                }
            case ArrayType.SampledNoiseBaselineArray:
                {
                    return "MS:1002745";
                }
            case ArrayType.IonMobilityArray:
                {
                    return "MS:1002893";
                }
            case ArrayType.MassArray:
                {
                    return "MS:1003143";
                }
            case ArrayType.ScanningQuadrupolePositionLowerBoundMZArray:
                {
                    return "MS:1003157";
                }
            case ArrayType.ScanningQuadrupolePositionUpperBoundMZArray:
                {
                    return "MS:1003158";
                }
            case ArrayType.MeanIonMobilityDriftTimeArray:
                {
                    return "MS:1002477";
                }
            case ArrayType.MeanIonMobilityArray:
                {
                    return "MS:1002816";
                }
            case ArrayType.MeanInverseReducedIonMobilityArray:
                {
                    return "MS:1003006";
                }
            case ArrayType.RawIonMobilityArray:
                {
                    return "MS:1003007";
                }
            case ArrayType.RawInverseReducedIonMobilityArray:
                {
                    return "MS:1003008";
                }
            case ArrayType.RawIonMobilityDriftTimeArray:
                {
                    return "MS:1003153";
                }
            case ArrayType.DeconvolutedIonMobilityArray:
                {
                    return "MS:1003154";
                }
            case ArrayType.DeconvolutedInverseReducedIonMobilityArray:
                {
                    return "MS:1003155";
                }
            case ArrayType.DeconvolutedIonMobilityDriftTimeArray:
                {
                    return "MS:1003156";
                }
            default:
                throw new InvalidOperationException();
        }
    }
}

public enum Unit
{
    Unit,
    MZ,
    IntensityUnit,
    EnergyUnit,
    ThS,
    VoltSecondPerSquareCentimeter,
    LengthUnit,
    MassUnit,
    TimeUnit,
    TemperatureUnit,
    BaseUnit,
    AreaUnit,
    VolumeUnit,
    FrequencyUnit,
    PressureUnit,
    AngleUnit,
    DensityUnit,
    DimensionlessUnit,
    ElectricPotentialDifferenceUnit,
    MagneticFluxDensityUnit,
    ElectricFieldStrengthUnit,
    VolumetricFlowRateUnit,
    NumberOfDetectorCounts,
    PercentOfBasePeak,
    CountsPerSecond,
    PercentOfBasePeakTimes100,
    Meter,
    Micrometer,
    Nanometer,
    Gram,
    Dalton,
    Kilodalton,
    Second,
    Millisecond,
    Minute,
    Nanosecond,
    Kelvin,
    DegreeCelsius,
    SquareAngstrom,
    Milliliter,
    Hertz,
    Pascal,
    PlaneAngleUnit,
    MassDensityUnit,
    PartsPerNotationUnit,
    CountUnit,
    Ratio,
    AbsorbanceUnit,
    Volt,
    Tesla,
    VoltPerMeter,
    MicrolitersPerMinute,
    Degree,
    GramPerLiter,
    PartsPerMillion,
    Percent,
    Fraction,
}

public static class UnitMethods
{
    public static readonly Dictionary<string, Unit> FromCURIE = new Dictionary<string, Unit>(
        ((Unit[])Enum.GetValues(typeof(Unit))).Select((v) => new KeyValuePair<string, Unit>(v.CURIE(), v))
    );

    public static string NameForColumn(this Unit unit)
    {
        var name = unit.Name();
        name = string.Join("_", name.Replace("/", "").Replace(" unit", "").Split(" "));
        return name;
    }

    public static string Name(this Unit unit)
    {
        switch (unit)
        {
            case Unit.Unit:
                {
                    return "unit";
                }
            case Unit.MZ:
                {
                    return "m/z";
                }
            case Unit.IntensityUnit:
                {
                    return "intensity unit";
                }
            case Unit.EnergyUnit:
                {
                    return "energy unit";
                }
            case Unit.ThS:
                {
                    return "Th/s";
                }
            case Unit.VoltSecondPerSquareCentimeter:
                {
                    return "volt-second per square centimeter";
                }
            case Unit.LengthUnit:
                {
                    return "length unit";
                }
            case Unit.MassUnit:
                {
                    return "mass unit";
                }
            case Unit.TimeUnit:
                {
                    return "time unit";
                }
            case Unit.TemperatureUnit:
                {
                    return "temperature unit";
                }
            case Unit.BaseUnit:
                {
                    return "base unit";
                }
            case Unit.AreaUnit:
                {
                    return "area unit";
                }
            case Unit.VolumeUnit:
                {
                    return "volume unit";
                }
            case Unit.FrequencyUnit:
                {
                    return "frequency unit";
                }
            case Unit.PressureUnit:
                {
                    return "pressure unit";
                }
            case Unit.AngleUnit:
                {
                    return "angle unit";
                }
            case Unit.DensityUnit:
                {
                    return "density unit";
                }
            case Unit.DimensionlessUnit:
                {
                    return "dimensionless unit";
                }
            case Unit.ElectricPotentialDifferenceUnit:
                {
                    return "electric potential difference unit";
                }
            case Unit.MagneticFluxDensityUnit:
                {
                    return "magnetic flux density unit";
                }
            case Unit.ElectricFieldStrengthUnit:
                {
                    return "electric field strength unit";
                }
            case Unit.VolumetricFlowRateUnit:
                {
                    return "volumetric flow rate unit";
                }
            case Unit.NumberOfDetectorCounts:
                {
                    return "number of detector counts";
                }
            case Unit.PercentOfBasePeak:
                {
                    return "percent of base peak";
                }
            case Unit.CountsPerSecond:
                {
                    return "counts per second";
                }
            case Unit.PercentOfBasePeakTimes100:
                {
                    return "percent of base peak times 100";
                }
            case Unit.Meter:
                {
                    return "meter";
                }
            case Unit.Micrometer:
                {
                    return "micrometer";
                }
            case Unit.Nanometer:
                {
                    return "nanometer";
                }
            case Unit.Gram:
                {
                    return "gram";
                }
            case Unit.Dalton:
                {
                    return "dalton";
                }
            case Unit.Kilodalton:
                {
                    return "kilodalton";
                }
            case Unit.Second:
                {
                    return "second";
                }
            case Unit.Millisecond:
                {
                    return "millisecond";
                }
            case Unit.Minute:
                {
                    return "minute";
                }
            case Unit.Nanosecond:
                {
                    return "nanosecond";
                }
            case Unit.Kelvin:
                {
                    return "kelvin";
                }
            case Unit.DegreeCelsius:
                {
                    return "degree Celsius";
                }
            case Unit.SquareAngstrom:
                {
                    return "square angstrom";
                }
            case Unit.Milliliter:
                {
                    return "milliliter";
                }
            case Unit.Hertz:
                {
                    return "hertz";
                }
            case Unit.Pascal:
                {
                    return "pascal";
                }
            case Unit.PlaneAngleUnit:
                {
                    return "plane angle unit";
                }
            case Unit.MassDensityUnit:
                {
                    return "mass density unit";
                }
            case Unit.PartsPerNotationUnit:
                {
                    return "parts per notation unit";
                }
            case Unit.CountUnit:
                {
                    return "count unit";
                }
            case Unit.Ratio:
                {
                    return "ratio";
                }
            case Unit.AbsorbanceUnit:
                {
                    return "absorbance unit";
                }
            case Unit.Volt:
                {
                    return "volt";
                }
            case Unit.Tesla:
                {
                    return "tesla";
                }
            case Unit.VoltPerMeter:
                {
                    return "volt per meter";
                }
            case Unit.MicrolitersPerMinute:
                {
                    return "microliters per minute";
                }
            case Unit.Degree:
                {
                    return "degree";
                }
            case Unit.GramPerLiter:
                {
                    return "gram per liter";
                }
            case Unit.PartsPerMillion:
                {
                    return "parts per million";
                }
            case Unit.Percent:
                {
                    return "percent";
                }
            case Unit.Fraction:
                {
                    return "fraction";
                }
        }
        throw new InvalidOperationException();
    }

    public static string CURIE(this Unit unit)
    {
        switch (unit)
        {
            case Unit.Unit:
                {
                    return "UO:0000000";
                }
            case Unit.MZ:
                {
                    return "MS:1000040";
                }
            case Unit.IntensityUnit:
                {
                    return "MS:1000043";
                }
            case Unit.EnergyUnit:
                {
                    return "MS:1000046";
                }
            case Unit.ThS:
                {
                    return "MS:1000807";
                }
            case Unit.VoltSecondPerSquareCentimeter:
                {
                    return "MS:1002814";
                }
            case Unit.LengthUnit:
                {
                    return "UO:0000001";
                }
            case Unit.MassUnit:
                {
                    return "UO:0000002";
                }
            case Unit.TimeUnit:
                {
                    return "UO:0000003";
                }
            case Unit.TemperatureUnit:
                {
                    return "UO:0000005";
                }
            case Unit.BaseUnit:
                {
                    return "UO:0000045";
                }
            case Unit.AreaUnit:
                {
                    return "UO:0000047";
                }
            case Unit.VolumeUnit:
                {
                    return "UO:0000095";
                }
            case Unit.FrequencyUnit:
                {
                    return "UO:0000105";
                }
            case Unit.PressureUnit:
                {
                    return "UO:0000109";
                }
            case Unit.AngleUnit:
                {
                    return "UO:0000121";
                }
            case Unit.DensityUnit:
                {
                    return "UO:0000182";
                }
            case Unit.DimensionlessUnit:
                {
                    return "UO:0000186";
                }
            case Unit.ElectricPotentialDifferenceUnit:
                {
                    return "UO:0000217";
                }
            case Unit.MagneticFluxDensityUnit:
                {
                    return "UO:0000227";
                }
            case Unit.ElectricFieldStrengthUnit:
                {
                    return "UO:0000267";
                }
            case Unit.VolumetricFlowRateUnit:
                {
                    return "UO:0000270";
                }
            case Unit.NumberOfDetectorCounts:
                {
                    return "MS:1000131";
                }
            case Unit.PercentOfBasePeak:
                {
                    return "MS:1000132";
                }
            case Unit.CountsPerSecond:
                {
                    return "MS:1000814";
                }
            case Unit.PercentOfBasePeakTimes100:
                {
                    return "MS:1000905";
                }
            case Unit.Meter:
                {
                    return "UO:0000008";
                }
            case Unit.Micrometer:
                {
                    return "UO:0000017";
                }
            case Unit.Nanometer:
                {
                    return "UO:0000018";
                }
            case Unit.Gram:
                {
                    return "UO:0000021";
                }
            case Unit.Dalton:
                {
                    return "UO:0000221";
                }
            case Unit.Kilodalton:
                {
                    return "UO:0000222";
                }
            case Unit.Second:
                {
                    return "UO:0000010";
                }
            case Unit.Millisecond:
                {
                    return "UO:0000028";
                }
            case Unit.Minute:
                {
                    return "UO:0000031";
                }
            case Unit.Nanosecond:
                {
                    return "UO:0000150";
                }
            case Unit.Kelvin:
                {
                    return "UO:0000012";
                }
            case Unit.DegreeCelsius:
                {
                    return "UO:0000027";
                }
            case Unit.SquareAngstrom:
                {
                    return "UO:0000324";
                }
            case Unit.Milliliter:
                {
                    return "UO:0000098";
                }
            case Unit.Hertz:
                {
                    return "UO:0000106";
                }
            case Unit.Pascal:
                {
                    return "UO:0000110";
                }
            case Unit.PlaneAngleUnit:
                {
                    return "UO:0000122";
                }
            case Unit.MassDensityUnit:
                {
                    return "UO:0000052";
                }
            case Unit.PartsPerNotationUnit:
                {
                    return "UO:0000166";
                }
            case Unit.CountUnit:
                {
                    return "UO:0000189";
                }
            case Unit.Ratio:
                {
                    return "UO:0000190";
                }
            case Unit.AbsorbanceUnit:
                {
                    return "UO:0000269";
                }
            case Unit.Volt:
                {
                    return "UO:0000218";
                }
            case Unit.Tesla:
                {
                    return "UO:0000228";
                }
            case Unit.VoltPerMeter:
                {
                    return "UO:0000268";
                }
            case Unit.MicrolitersPerMinute:
                {
                    return "UO:0000271";
                }
            case Unit.Degree:
                {
                    return "UO:0000185";
                }
            case Unit.GramPerLiter:
                {
                    return "UO:0000175";
                }
            case Unit.PartsPerMillion:
                {
                    return "UO:0000169";
                }
            case Unit.Percent:
                {
                    return "UO:0000187";
                }
            case Unit.Fraction:
                {
                    return "UO:0000191";
                }
        }
        throw new InvalidOperationException();
    }
}

public enum Compression
{
    NoCompression,
    Zlib,
    Zstd,
}

public static class CompressionMethods
{
    public static readonly Dictionary<string, Compression> FromCURIE = new Dictionary<string, Compression>(
        ((Compression[])Enum.GetValues(typeof(Compression))).Select((v) => new KeyValuePair<string, Compression>(v.CURIE(), v))
    );

    public static string Name(this Compression compression)
    {
        switch (compression)
        {
            case Compression.NoCompression: return "no compression";
            case Compression.Zlib: return "zlib compression";
            case Compression.Zstd: return "zstd compression";
            default: throw new NotImplementedException();
        }
    }

    public static string CURIE(this Compression compression)
    {
        switch (compression)
        {
            case Compression.NoCompression: return "MS:1000576";
            case Compression.Zlib: return "MS:1000574";
            case Compression.Zstd: return "MS:1003780";
            default: throw new NotImplementedException();
        }
    }
}

public enum SpectrumProperties
{
    SpectrumProperty,
    SpectrumAttribute,
    TotalIonCurrent,
    BasePeakMZ,
    BasePeakIntensity,
    HighestObservedMZ,
    LowestObservedMZ,
    HighestObservedWavelength,
    LowestObservedWavelength,
    PeakListScans,
    PeakListRawScans,
    NumberOfPeaks,
    NumberOfDataPoints,
    LowestObservedIonMobility,
    HighestObservedIonMobility,
    MsLevel,
    SourceDataFile,
    SpectrumTitle,
    LibrarySpectrumName,
    UniversalSpectrumIdentifier,
    SpectrumAggregationAttribute,
    SpectrumOriginAttribute,
    PreviousMsn1ScanPrecursorIntensity,
    PrecursorApexIntensity,
    NistMspComment,
    IonMobilityFrameRepresentation,
    RawDataFile,
    ProcessedDataFile,
    SpectrumAggregationType,
    NumberOfReplicateSpectraAvailable,
    NumberOfReplicateSpectraUsed,
    SummaryStatisticsOfReplicates,
    NumberOfReplicatesSpectraUsedFromSource,
    SpectrumOriginType,
    IonMobilityProfileFrame,
    IonMobilityCentroidFrame,
    SingletonSpectrum,
    ConsensusSpectrum,
    BestReplicateSpectrum,
    PredictedSpectrum,
    RetentionTime,
    NormalizedRetentionTime,
    ExperimentalPrecursorMonoisotopicMZ,
    MonoisotopicMZDeviation,
    AverageMZDeviation,
    SpectralDotProductToAggregatedSpectrum,
    ObservedSpectrum,
    DemultiplexedSpectrum,
    DecoySpectrum,
    SelectedFragmentTheoreticalMZObservedIntensitySpectrum,
    LocalRetentionTime,
    PredictedRetentionTime,
    ShuffleAndRepositionDecoySpectrum,
    PrecursorShiftDecoySpectrum,
    UnnaturalPeptidoformDecoySpectrum,
    UnrelatedSpeciesDecoySpectrum,
}

public static partial class SpectrumPropertiesMethods
{
    public static readonly Dictionary<string, SpectrumProperties> FromCURIE = new Dictionary<string, SpectrumProperties>(
        ((SpectrumProperties[])Enum.GetValues(typeof(SpectrumProperties))).Select((v) => new KeyValuePair<string, SpectrumProperties>(v.CURIE(), v))
    );
    public static string Name(this SpectrumProperties term)
    {
        switch (term)
        {
            case SpectrumProperties.SpectrumProperty: return "spectrum property";
            case SpectrumProperties.SpectrumAttribute: return "spectrum attribute";
            case SpectrumProperties.TotalIonCurrent: return "total ion current";
            case SpectrumProperties.BasePeakMZ: return "base peak m/z";
            case SpectrumProperties.BasePeakIntensity: return "base peak intensity";
            case SpectrumProperties.HighestObservedMZ: return "highest observed m/z";
            case SpectrumProperties.LowestObservedMZ: return "lowest observed m/z";
            case SpectrumProperties.HighestObservedWavelength: return "highest observed wavelength";
            case SpectrumProperties.LowestObservedWavelength: return "lowest observed wavelength";
            case SpectrumProperties.PeakListScans: return "peak list scans";
            case SpectrumProperties.PeakListRawScans: return "peak list raw scans";
            case SpectrumProperties.NumberOfPeaks: return "number of peaks";
            case SpectrumProperties.NumberOfDataPoints: return "number of data points";
            case SpectrumProperties.LowestObservedIonMobility: return "lowest observed ion mobility";
            case SpectrumProperties.HighestObservedIonMobility: return "highest observed ion mobility";
            case SpectrumProperties.MsLevel: return "ms level";
            case SpectrumProperties.SourceDataFile: return "source data file";
            case SpectrumProperties.SpectrumTitle: return "spectrum title";
            case SpectrumProperties.LibrarySpectrumName: return "library spectrum name";
            case SpectrumProperties.UniversalSpectrumIdentifier: return "universal spectrum identifier";
            case SpectrumProperties.SpectrumAggregationAttribute: return "spectrum aggregation attribute";
            case SpectrumProperties.SpectrumOriginAttribute: return "spectrum origin attribute";
            case SpectrumProperties.PreviousMsn1ScanPrecursorIntensity: return "previous MSn-1 scan precursor intensity";
            case SpectrumProperties.PrecursorApexIntensity: return "precursor apex intensity";
            case SpectrumProperties.NistMspComment: return "NIST msp comment";
            case SpectrumProperties.IonMobilityFrameRepresentation: return "ion mobility frame representation";
            case SpectrumProperties.RawDataFile: return "raw data file";
            case SpectrumProperties.ProcessedDataFile: return "processed data file";
            case SpectrumProperties.SpectrumAggregationType: return "spectrum aggregation type";
            case SpectrumProperties.NumberOfReplicateSpectraAvailable: return "number of replicate spectra available";
            case SpectrumProperties.NumberOfReplicateSpectraUsed: return "number of replicate spectra used";
            case SpectrumProperties.SummaryStatisticsOfReplicates: return "summary statistics of replicates";
            case SpectrumProperties.NumberOfReplicatesSpectraUsedFromSource: return "number of replicates spectra used from source";
            case SpectrumProperties.SpectrumOriginType: return "spectrum origin type";
            case SpectrumProperties.IonMobilityProfileFrame: return "ion mobility profile frame";
            case SpectrumProperties.IonMobilityCentroidFrame: return "ion mobility centroid frame";
            case SpectrumProperties.SingletonSpectrum: return "singleton spectrum";
            case SpectrumProperties.ConsensusSpectrum: return "consensus spectrum";
            case SpectrumProperties.BestReplicateSpectrum: return "best replicate spectrum";
            case SpectrumProperties.PredictedSpectrum: return "predicted spectrum";
            case SpectrumProperties.RetentionTime: return "retention time";
            case SpectrumProperties.NormalizedRetentionTime: return "normalized retention time";
            case SpectrumProperties.ExperimentalPrecursorMonoisotopicMZ: return "experimental precursor monoisotopic m/z";
            case SpectrumProperties.MonoisotopicMZDeviation: return "monoisotopic m/z deviation";
            case SpectrumProperties.AverageMZDeviation: return "average m/z deviation";
            case SpectrumProperties.SpectralDotProductToAggregatedSpectrum: return "spectral dot product to aggregated spectrum";
            case SpectrumProperties.ObservedSpectrum: return "observed spectrum";
            case SpectrumProperties.DemultiplexedSpectrum: return "demultiplexed spectrum";
            case SpectrumProperties.DecoySpectrum: return "decoy spectrum";
            case SpectrumProperties.SelectedFragmentTheoreticalMZObservedIntensitySpectrum: return "selected fragment theoretical m/z observed intensity spectrum";
            case SpectrumProperties.LocalRetentionTime: return "local retention time";
            case SpectrumProperties.PredictedRetentionTime: return "predicted retention time";
            case SpectrumProperties.ShuffleAndRepositionDecoySpectrum: return "shuffle-and-reposition decoy spectrum";
            case SpectrumProperties.PrecursorShiftDecoySpectrum: return "precursor shift decoy spectrum";
            case SpectrumProperties.UnnaturalPeptidoformDecoySpectrum: return "unnatural peptidoform decoy spectrum";
            case SpectrumProperties.UnrelatedSpeciesDecoySpectrum: return "unrelated species decoy spectrum";
        }
        throw new InvalidOperationException();
    }

    public static string CURIE(this SpectrumProperties term)
    {
        switch (term)
        {
            case SpectrumProperties.SpectrumProperty: return "MS:1003058";
            case SpectrumProperties.SpectrumAttribute: return "MS:1000499";
            case SpectrumProperties.TotalIonCurrent: return "MS:1000285";
            case SpectrumProperties.BasePeakMZ: return "MS:1000504";
            case SpectrumProperties.BasePeakIntensity: return "MS:1000505";
            case SpectrumProperties.HighestObservedMZ: return "MS:1000527";
            case SpectrumProperties.LowestObservedMZ: return "MS:1000528";
            case SpectrumProperties.HighestObservedWavelength: return "MS:1000618";
            case SpectrumProperties.LowestObservedWavelength: return "MS:1000619";
            case SpectrumProperties.PeakListScans: return "MS:1000797";
            case SpectrumProperties.PeakListRawScans: return "MS:1000798";
            case SpectrumProperties.NumberOfPeaks: return "MS:1003059";
            case SpectrumProperties.NumberOfDataPoints: return "MS:1003060";
            case SpectrumProperties.LowestObservedIonMobility: return "MS:1003437";
            case SpectrumProperties.HighestObservedIonMobility: return "MS:1003438";
            case SpectrumProperties.MsLevel: return "MS:1000511";
            case SpectrumProperties.SourceDataFile: return "MS:1000577";
            case SpectrumProperties.SpectrumTitle: return "MS:1000796";
            case SpectrumProperties.LibrarySpectrumName: return "MS:1003061";
            case SpectrumProperties.UniversalSpectrumIdentifier: return "MS:1003063";
            case SpectrumProperties.SpectrumAggregationAttribute: return "MS:1003064";
            case SpectrumProperties.SpectrumOriginAttribute: return "MS:1003071";
            case SpectrumProperties.PreviousMsn1ScanPrecursorIntensity: return "MS:1003085";
            case SpectrumProperties.PrecursorApexIntensity: return "MS:1003086";
            case SpectrumProperties.NistMspComment: return "MS:1003102";
            case SpectrumProperties.IonMobilityFrameRepresentation: return "MS:1003439";
            case SpectrumProperties.RawDataFile: return "MS:1003083";
            case SpectrumProperties.ProcessedDataFile: return "MS:1003084";
            case SpectrumProperties.SpectrumAggregationType: return "MS:1003065";
            case SpectrumProperties.NumberOfReplicateSpectraAvailable: return "MS:1003069";
            case SpectrumProperties.NumberOfReplicateSpectraUsed: return "MS:1003070";
            case SpectrumProperties.SummaryStatisticsOfReplicates: return "MS:1003295";
            case SpectrumProperties.NumberOfReplicatesSpectraUsedFromSource: return "MS:1003296";
            case SpectrumProperties.SpectrumOriginType: return "MS:1003072";
            case SpectrumProperties.IonMobilityProfileFrame: return "MS:1003440";
            case SpectrumProperties.IonMobilityCentroidFrame: return "MS:1003441";
            case SpectrumProperties.SingletonSpectrum: return "MS:1003066";
            case SpectrumProperties.ConsensusSpectrum: return "MS:1003067";
            case SpectrumProperties.BestReplicateSpectrum: return "MS:1003068";
            case SpectrumProperties.PredictedSpectrum: return "MS:1003074";
            case SpectrumProperties.RetentionTime: return "MS:1000894";
            case SpectrumProperties.NormalizedRetentionTime: return "MS:1000896";
            case SpectrumProperties.ExperimentalPrecursorMonoisotopicMZ: return "MS:1003208";
            case SpectrumProperties.MonoisotopicMZDeviation: return "MS:1003209";
            case SpectrumProperties.AverageMZDeviation: return "MS:1003210";
            case SpectrumProperties.SpectralDotProductToAggregatedSpectrum: return "MS:1003324";
            case SpectrumProperties.ObservedSpectrum: return "MS:1003073";
            case SpectrumProperties.DemultiplexedSpectrum: return "MS:1003075";
            case SpectrumProperties.DecoySpectrum: return "MS:1003192";
            case SpectrumProperties.SelectedFragmentTheoreticalMZObservedIntensitySpectrum: return "MS:1003424";
            case SpectrumProperties.LocalRetentionTime: return "MS:1000895";
            case SpectrumProperties.PredictedRetentionTime: return "MS:1000897";
            case SpectrumProperties.ShuffleAndRepositionDecoySpectrum: return "MS:1003193";
            case SpectrumProperties.PrecursorShiftDecoySpectrum: return "MS:1003194";
            case SpectrumProperties.UnnaturalPeptidoformDecoySpectrum: return "MS:1003195";
            case SpectrumProperties.UnrelatedSpeciesDecoySpectrum: return "MS:1003196";
        }
        throw new InvalidOperationException();
    }
}

public static partial class SpectrumPropertiesMethods
{
    public static Param Param(this SpectrumProperties term, object? value=null, Unit? unit = null)
    {
        return new Param(term.Name(), term.CURIE(), value, unit?.CURIE());
    }
}

public enum BinaryDataType
{
    Int32,
    Int64,
    Float32,
    Float64,
    ASCII
}

public static class BinaryDataTypeMethods
{
    public static readonly Dictionary<string, BinaryDataType> FromCURIE = new Dictionary<string, BinaryDataType>(
        ((BinaryDataType[])Enum.GetValues(typeof(BinaryDataType))).Select((v) => new KeyValuePair<string, BinaryDataType>(v.CURIE(), v))
    );

    public static string NameForColumn(this BinaryDataType dataType)
    {
        switch (dataType)
        {
            case BinaryDataType.Float32:
                {
                    return "float32";
                }
            case BinaryDataType.Float64:
                {
                    return "float64";
                }
            case BinaryDataType.Int32:
                {
                    return "integer32";
                }
            case BinaryDataType.Int64:
                {
                    return "integer64";
                }
            case BinaryDataType.ASCII:
                {
                    return "ascii";
                }
            default: throw new NotImplementedException();
        }
    }

    public static string Name(this BinaryDataType dataType)
    {
        switch (dataType)
        {
            case BinaryDataType.Float32:
                {
                    return "32-bit float";
                }
            case BinaryDataType.Float64:
                {
                    return "64-bit float";
                }
            case BinaryDataType.Int32:
                {
                    return "32-bit integer";
                }
            case BinaryDataType.Int64:
                {
                    return "64-bit integer";
                }
            case BinaryDataType.ASCII:
                {
                    return "null-terminated ASCII string";
                }
            default: throw new NotImplementedException();
        }
    }

    public static string CURIE(this BinaryDataType dataType)
    {
        switch (dataType)
        {
            case BinaryDataType.Float32:
                {
                    return "MS:1000521";
                }
            case BinaryDataType.Float64:
                {
                    return "MS:1000523";
                }
            case BinaryDataType.Int32:
                {
                    return "MS:1000519";
                }
            case BinaryDataType.Int64:
                {
                    return "MS:1000522";
                }
            case BinaryDataType.ASCII:
                {
                    return "MS:1001479";
                }
            default: throw new NotImplementedException();
        }
    }

    public static ArrowType ArrowType(this BinaryDataType dataType)
    {
        switch (dataType.CURIE())
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
                    throw new InvalidDataException("Cannot map " + dataType.CURIE() + " to an Arrow type");
                }
        }
    }
}

public enum Activation
{
    DissociationMethod,
    CollisionInducedDissociation,
    PlasmaDesorption,
    PostSourceDecay,
    SurfaceInducedDissociation,
    BlackbodyInfraredRadiativeDissociation,
    ElectronCaptureDissociation,
    SustainedOffResonanceIrradiation,
    LowEnergyCollisionInducedDissociation,
    Photodissociation,
    ElectronTransferDissociation,
    PulsedQDissociation,
    InSourceCollisionInducedDissociation,
    Lift,
    NegativeElectronTransferDissociation,
    BeamTypeCollisionInducedDissociation,
    TrapTypeCollisionInducedDissociation,
    SupplementalCollisionInducedDissociation,
    ElectronActivatedDissociation,
    InfraredMultiphotonDissociation,
    UltravioletPhotodissociation,
    HigherEnergyBeamTypeCollisionInducedDissociation,
    SupplementalBeamTypeCollisionInducedDissociation,
}

public static class ActivationMethods
{
    public static string Name(this Activation term)
    {
        switch (term)
        {
            case Activation.DissociationMethod: return "dissociation method";
            case Activation.CollisionInducedDissociation: return "collision-induced dissociation";
            case Activation.PlasmaDesorption: return "plasma desorption";
            case Activation.PostSourceDecay: return "post-source decay";
            case Activation.SurfaceInducedDissociation: return "surface-induced dissociation";
            case Activation.BlackbodyInfraredRadiativeDissociation: return "blackbody infrared radiative dissociation";
            case Activation.ElectronCaptureDissociation: return "electron capture dissociation";
            case Activation.SustainedOffResonanceIrradiation: return "sustained off-resonance irradiation";
            case Activation.LowEnergyCollisionInducedDissociation: return "low-energy collision-induced dissociation";
            case Activation.Photodissociation: return "photodissociation";
            case Activation.ElectronTransferDissociation: return "electron transfer dissociation";
            case Activation.PulsedQDissociation: return "pulsed q dissociation";
            case Activation.InSourceCollisionInducedDissociation: return "in-source collision-induced dissociation";
            case Activation.Lift: return "LIFT";
            case Activation.NegativeElectronTransferDissociation: return "negative electron transfer dissociation";
            case Activation.BeamTypeCollisionInducedDissociation: return "beam-type collision-induced dissociation";
            case Activation.TrapTypeCollisionInducedDissociation: return "trap-type collision-induced dissociation";
            case Activation.SupplementalCollisionInducedDissociation: return "supplemental collision-induced dissociation";
            case Activation.ElectronActivatedDissociation: return "electron activated dissociation";
            case Activation.InfraredMultiphotonDissociation: return "infrared multiphoton dissociation";
            case Activation.UltravioletPhotodissociation: return "ultraviolet photodissociation";
            case Activation.HigherEnergyBeamTypeCollisionInducedDissociation: return "higher energy beam-type collision-induced dissociation";
            case Activation.SupplementalBeamTypeCollisionInducedDissociation: return "supplemental beam-type collision-induced dissociation";
            default: throw new InvalidOperationException($"Unknown activation method {term}");
        }
    }

    public static string CURIE(this Activation term)
    {
        switch (term)
        {
            case Activation.DissociationMethod: return "MS:1000044";
            case Activation.CollisionInducedDissociation: return "MS:1000133";
            case Activation.PlasmaDesorption: return "MS:1000134";
            case Activation.PostSourceDecay: return "MS:1000135";
            case Activation.SurfaceInducedDissociation: return "MS:1000136";
            case Activation.BlackbodyInfraredRadiativeDissociation: return "MS:1000242";
            case Activation.ElectronCaptureDissociation: return "MS:1000250";
            case Activation.SustainedOffResonanceIrradiation: return "MS:1000282";
            case Activation.LowEnergyCollisionInducedDissociation: return "MS:1000433";
            case Activation.Photodissociation: return "MS:1000435";
            case Activation.ElectronTransferDissociation: return "MS:1000598";
            case Activation.PulsedQDissociation: return "MS:1000599";
            case Activation.InSourceCollisionInducedDissociation: return "MS:1001880";
            case Activation.Lift: return "MS:1002000";
            case Activation.NegativeElectronTransferDissociation: return "MS:1003247";
            case Activation.BeamTypeCollisionInducedDissociation: return "MS:1000422";
            case Activation.TrapTypeCollisionInducedDissociation: return "MS:1002472";
            case Activation.SupplementalCollisionInducedDissociation: return "MS:1002679";
            case Activation.ElectronActivatedDissociation: return "MS:1003294";
            case Activation.InfraredMultiphotonDissociation: return "MS:1000262";
            case Activation.UltravioletPhotodissociation: return "MS:1003246";
            case Activation.HigherEnergyBeamTypeCollisionInducedDissociation: return "MS:1002481";
            case Activation.SupplementalBeamTypeCollisionInducedDissociation: return "MS:1002678";
            default: throw new InvalidOperationException($"Unknown activation method {term}");
        }
    }
}

public enum SpectrumRepresentation
{
    SpectrumRepresentation,
    CentroidSpectrum,
    ProfileSpectrum,
}

public static class SpectrumRepresentationMethods
{

    public static string Name(this SpectrumRepresentation term)
    {
        switch (term)
        {
            case SpectrumRepresentation.SpectrumRepresentation: return "spectrum representation";
            case SpectrumRepresentation.CentroidSpectrum: return "centroid spectrum";
            case SpectrumRepresentation.ProfileSpectrum: return "profile spectrum";
            default: throw new InvalidOperationException();
        }
    }

    public static string CURIE(this SpectrumRepresentation term)
    {
        switch (term)
        {
            case SpectrumRepresentation.SpectrumRepresentation: return "MS:1000525";
            case SpectrumRepresentation.CentroidSpectrum: return "MS:1000127";
            case SpectrumRepresentation.ProfileSpectrum: return "MS:1000128";
            default: throw new InvalidOperationException();
        }
    }

    public static Param AsParam(this SpectrumRepresentation term)
    {
        return new Param(term.Name(), term.CURIE(), null);
    }
}

public enum ScanPolarity
{
    ScanPolarity,
    NegativeScan,
    PositiveScan,
}

public static class ScanPolarityMethods
{
    public static string Name(this ScanPolarity term)
    {
        switch (term)
        {
            case ScanPolarity.ScanPolarity: return "scan polarity";
            case ScanPolarity.NegativeScan: return "negative scan";
            case ScanPolarity.PositiveScan: return "positive scan";
            default: throw new InvalidOperationException();
        }
    }

    public static string CURIE(this ScanPolarity term)
    {
        switch (term)
        {
            case ScanPolarity.ScanPolarity: return "MS:1000465";
            case ScanPolarity.NegativeScan: return "MS:1000129";
            case ScanPolarity.PositiveScan: return "MS:1000130";
            default: throw new InvalidOperationException();
        }
    }
}

public enum SpectrumType
{
    SpectrumType,
    MassSpectrum,
    PdaSpectrum,
    ElectromagneticRadiationSpectrum,
    EmissionSpectrum,
    AbsorptionSpectrum,
    CalibrationSpectrum,
    ChargeInversionMassSpectrum,
    ConstantNeutralGainSpectrum,
    ConstantNeutralLossSpectrum,
    E2MassSpectrum,
    PrecursorIonSpectrum,
    ProductIonSpectrum,
    Ms1Spectrum,
    MsnSpectrum,
    CrmSpectrum,
    SimSpectrum,
    SrmSpectrum,
    EnhancedMultiplyChargedSpectrum,
    TimeDelayedFragmentationSpectrum,
}

public static partial class SpectrumTypeMethods
{
    public static readonly Dictionary<string, SpectrumType> FromCURIE = new Dictionary<string, SpectrumType>(
        ((SpectrumType[])Enum.GetValues(typeof(SpectrumType))).Select((v) => new KeyValuePair<string, SpectrumType>(v.CURIE(), v))
    );

    public static string Name(this SpectrumType term)
    {
        switch (term)
        {
            case SpectrumType.SpectrumType: return "spectrum type";
            case SpectrumType.MassSpectrum: return "mass spectrum";
            case SpectrumType.PdaSpectrum: return "PDA spectrum";
            case SpectrumType.ElectromagneticRadiationSpectrum: return "electromagnetic radiation spectrum";
            case SpectrumType.EmissionSpectrum: return "emission spectrum";
            case SpectrumType.AbsorptionSpectrum: return "absorption spectrum";
            case SpectrumType.CalibrationSpectrum: return "calibration spectrum";
            case SpectrumType.ChargeInversionMassSpectrum: return "charge inversion mass spectrum";
            case SpectrumType.ConstantNeutralGainSpectrum: return "constant neutral gain spectrum";
            case SpectrumType.ConstantNeutralLossSpectrum: return "constant neutral loss spectrum";
            case SpectrumType.E2MassSpectrum: return "e/2 mass spectrum";
            case SpectrumType.PrecursorIonSpectrum: return "precursor ion spectrum";
            case SpectrumType.ProductIonSpectrum: return "product ion spectrum";
            case SpectrumType.Ms1Spectrum: return "MS1 spectrum";
            case SpectrumType.MsnSpectrum: return "MSn spectrum";
            case SpectrumType.CrmSpectrum: return "CRM spectrum";
            case SpectrumType.SimSpectrum: return "SIM spectrum";
            case SpectrumType.SrmSpectrum: return "SRM spectrum";
            case SpectrumType.EnhancedMultiplyChargedSpectrum: return "enhanced multiply charged spectrum";
            case SpectrumType.TimeDelayedFragmentationSpectrum: return "time-delayed fragmentation spectrum";
            default: throw new InvalidOperationException();
        }
    }

    public static string CURIE(this SpectrumType term)
    {
        switch (term)
        {
            case SpectrumType.SpectrumType: return "MS:1000559";
            case SpectrumType.MassSpectrum: return "MS:1000294";
            case SpectrumType.PdaSpectrum: return "MS:1000620";
            case SpectrumType.ElectromagneticRadiationSpectrum: return "MS:1000804";
            case SpectrumType.EmissionSpectrum: return "MS:1000805";
            case SpectrumType.AbsorptionSpectrum: return "MS:1000806";
            case SpectrumType.CalibrationSpectrum: return "MS:1000928";
            case SpectrumType.ChargeInversionMassSpectrum: return "MS:1000322";
            case SpectrumType.ConstantNeutralGainSpectrum: return "MS:1000325";
            case SpectrumType.ConstantNeutralLossSpectrum: return "MS:1000326";
            case SpectrumType.E2MassSpectrum: return "MS:1000328";
            case SpectrumType.PrecursorIonSpectrum: return "MS:1000341";
            case SpectrumType.ProductIonSpectrum: return "MS:1000343";
            case SpectrumType.Ms1Spectrum: return "MS:1000579";
            case SpectrumType.MsnSpectrum: return "MS:1000580";
            case SpectrumType.CrmSpectrum: return "MS:1000581";
            case SpectrumType.SimSpectrum: return "MS:1000582";
            case SpectrumType.SrmSpectrum: return "MS:1000583";
            case SpectrumType.EnhancedMultiplyChargedSpectrum: return "MS:1000789";
            case SpectrumType.TimeDelayedFragmentationSpectrum: return "MS:1000790";
            default: throw new InvalidOperationException();
        }
    }
}


public enum ScanAttribute
{
    ScanAttribute,
    MassResolution,
    ScanRate,
    ScanStartTime,
    ZoomScan,
    DwellTime,
    FilterString,
    PresetScanConfiguration,
    MassResolvingPower,
    AnalyzerScanOffset,
    ElutionTime,
    InterchannelDelay,
    IonInjectionTime,
    SourceOffsetVoltage,
    FirstColumnElutionTime,
    SecondColumnElutionTime,
    InstrumentSpecificScanAttribute,
    IonMobilityAttribute,
    ScanNumber,
    SynchronousPrefilterSelection,
    FaimsCompensationVoltage,
    IonMobilityDriftTime,
    InverseReducedIonMobility,
    SelexionCompensationVoltage,
    SelexionSeparationVoltage,
    FaimsCompensationVoltageRampStart,
    FaimsCompensationVoltageRampEnd,
}

public static partial class ScanAttributeMethods
{

    public static string Name(this ScanAttribute term)
    {
        switch (term)
        {
            case ScanAttribute.ScanAttribute: return "scan attribute";
            case ScanAttribute.MassResolution: return "mass resolution";
            case ScanAttribute.ScanRate: return "scan rate";
            case ScanAttribute.ScanStartTime: return "scan start time";
            case ScanAttribute.ZoomScan: return "zoom scan";
            case ScanAttribute.DwellTime: return "dwell time";
            case ScanAttribute.FilterString: return "filter string";
            case ScanAttribute.PresetScanConfiguration: return "preset scan configuration";
            case ScanAttribute.MassResolvingPower: return "mass resolving power";
            case ScanAttribute.AnalyzerScanOffset: return "analyzer scan offset";
            case ScanAttribute.ElutionTime: return "elution time";
            case ScanAttribute.InterchannelDelay: return "interchannel delay";
            case ScanAttribute.IonInjectionTime: return "ion injection time";
            case ScanAttribute.SourceOffsetVoltage: return "source offset voltage";
            case ScanAttribute.FirstColumnElutionTime: return "first column elution time";
            case ScanAttribute.SecondColumnElutionTime: return "second column elution time";
            case ScanAttribute.InstrumentSpecificScanAttribute: return "instrument specific scan attribute";
            case ScanAttribute.IonMobilityAttribute: return "ion mobility attribute";
            case ScanAttribute.ScanNumber: return "scan number";
            case ScanAttribute.SynchronousPrefilterSelection: return "synchronous prefilter selection";
            case ScanAttribute.FaimsCompensationVoltage: return "FAIMS compensation voltage";
            case ScanAttribute.IonMobilityDriftTime: return "ion mobility drift time";
            case ScanAttribute.InverseReducedIonMobility: return "inverse reduced ion mobility";
            case ScanAttribute.SelexionCompensationVoltage: return "SelexION compensation voltage";
            case ScanAttribute.SelexionSeparationVoltage: return "SelexION separation voltage";
            case ScanAttribute.FaimsCompensationVoltageRampStart: return "FAIMS compensation voltage ramp start";
            case ScanAttribute.FaimsCompensationVoltageRampEnd: return "FAIMS compensation voltage ramp end";
            default: throw new InvalidOperationException();
        }
    }

    public static string CURIE(this ScanAttribute term)
    {
        switch (term)
        {
            case ScanAttribute.ScanAttribute: return "MS:1000503";
            case ScanAttribute.MassResolution: return "MS:1000011";
            case ScanAttribute.ScanRate: return "MS:1000015";
            case ScanAttribute.ScanStartTime: return "MS:1000016";
            case ScanAttribute.ZoomScan: return "MS:1000497";
            case ScanAttribute.DwellTime: return "MS:1000502";
            case ScanAttribute.FilterString: return "MS:1000512";
            case ScanAttribute.PresetScanConfiguration: return "MS:1000616";
            case ScanAttribute.MassResolvingPower: return "MS:1000800";
            case ScanAttribute.AnalyzerScanOffset: return "MS:1000803";
            case ScanAttribute.ElutionTime: return "MS:1000826";
            case ScanAttribute.InterchannelDelay: return "MS:1000880";
            case ScanAttribute.IonInjectionTime: return "MS:1000927";
            case ScanAttribute.SourceOffsetVoltage: return "MS:1001879";
            case ScanAttribute.FirstColumnElutionTime: return "MS:1002082";
            case ScanAttribute.SecondColumnElutionTime: return "MS:1002083";
            case ScanAttribute.InstrumentSpecificScanAttribute: return "MS:1002527";
            case ScanAttribute.IonMobilityAttribute: return "MS:1002892";
            case ScanAttribute.ScanNumber: return "MS:1003057";
            case ScanAttribute.SynchronousPrefilterSelection: return "MS:1002528";
            case ScanAttribute.FaimsCompensationVoltage: return "MS:1001581";
            case ScanAttribute.IonMobilityDriftTime: return "MS:1002476";
            case ScanAttribute.InverseReducedIonMobility: return "MS:1002815";
            case ScanAttribute.SelexionCompensationVoltage: return "MS:1003371";
            case ScanAttribute.SelexionSeparationVoltage: return "MS:1003394";
            case ScanAttribute.FaimsCompensationVoltageRampStart: return "MS:1003450";
            case ScanAttribute.FaimsCompensationVoltageRampEnd: return "MS:1003451";
            default: throw new InvalidOperationException();
        }
    }
}

public static partial class ScanAttributeMethods
{
    public static Param Param(this ScanAttribute term, object? value = null, Unit? unit = null)
    {
        return new Param(term.Name(), term.CURIE(), value, unit?.CURIE());
    }
}

public enum IsolationWindowProperties
{
    IsolationWindowAttribute,
    IsolationWindowUpperLimit,
    IsolationWindowLowerLimit,
    IsolationWindowTargetMZ,
    IsolationWindowLowerOffset,
    IsolationWindowUpperOffset,
    NoIsolation,
}

public static class IsolationWindowPropertiesMethods
{

    public static readonly Dictionary<string, IsolationWindowProperties> FromCURIE = new Dictionary<string, IsolationWindowProperties>(
        ((IsolationWindowProperties[])Enum.GetValues(typeof(IsolationWindowProperties))).Select((v) => new KeyValuePair<string, IsolationWindowProperties>(v.CURIE(), v))
    );


    public static string Name(this IsolationWindowProperties term)
    {
        switch (term)
        {
            case IsolationWindowProperties.IsolationWindowAttribute: return "isolation window attribute";
            case IsolationWindowProperties.IsolationWindowUpperLimit: return "isolation window upper limit";
            case IsolationWindowProperties.IsolationWindowLowerLimit: return "isolation window lower limit";
            case IsolationWindowProperties.IsolationWindowTargetMZ: return "isolation window target m/z";
            case IsolationWindowProperties.IsolationWindowLowerOffset: return "isolation window lower offset";
            case IsolationWindowProperties.IsolationWindowUpperOffset: return "isolation window upper offset";
            case IsolationWindowProperties.NoIsolation: return "no isolation";
            default: throw new InvalidOperationException();
        }
    }

    public static string CURIE(this IsolationWindowProperties term)
    {
        switch (term)
        {
            case IsolationWindowProperties.IsolationWindowAttribute: return "MS:1000792";
            case IsolationWindowProperties.IsolationWindowUpperLimit: return "MS:1000793";
            case IsolationWindowProperties.IsolationWindowLowerLimit: return "MS:1000794";
            case IsolationWindowProperties.IsolationWindowTargetMZ: return "MS:1000827";
            case IsolationWindowProperties.IsolationWindowLowerOffset: return "MS:1000828";
            case IsolationWindowProperties.IsolationWindowUpperOffset: return "MS:1000829";
            case IsolationWindowProperties.NoIsolation: return "MS:1003159";
            default: throw new InvalidOperationException();
        }
    }
}

public enum DissociationMethod
{
    DissociationMethod,
    CollisionInducedDissociation,
    PlasmaDesorption,
    PostSourceDecay,
    SurfaceInducedDissociation,
    BlackbodyInfraredRadiativeDissociation,
    ElectronCaptureDissociation,
    SustainedOffResonanceIrradiation,
    LowEnergyCollisionInducedDissociation,
    Photodissociation,
    ElectronTransferDissociation,
    PulsedQDissociation,
    InSourceCollisionInducedDissociation,
    Lift,
    NegativeElectronTransferDissociation,
    BeamTypeCollisionInducedDissociation,
    TrapTypeCollisionInducedDissociation,
    SupplementalCollisionInducedDissociation,
    ElectronActivatedDissociation,
    InfraredMultiphotonDissociation,
    UltravioletPhotodissociation,
    HigherEnergyBeamTypeCollisionInducedDissociation,
    SupplementalBeamTypeCollisionInducedDissociation,
}

public static class DissociationMethodMethods
{

    public static readonly Dictionary<string, DissociationMethod> FromCURIE = new Dictionary<string, DissociationMethod>(
        ((DissociationMethod[])Enum.GetValues(typeof(DissociationMethod))).Select((v) => new KeyValuePair<string, DissociationMethod>(v.CURIE(), v))
    );


    public static string Name(this DissociationMethod term)
    {
        switch (term)
        {
            case DissociationMethod.DissociationMethod: return "dissociation method";
            case DissociationMethod.CollisionInducedDissociation: return "collision-induced dissociation";
            case DissociationMethod.PlasmaDesorption: return "plasma desorption";
            case DissociationMethod.PostSourceDecay: return "post-source decay";
            case DissociationMethod.SurfaceInducedDissociation: return "surface-induced dissociation";
            case DissociationMethod.BlackbodyInfraredRadiativeDissociation: return "blackbody infrared radiative dissociation";
            case DissociationMethod.ElectronCaptureDissociation: return "electron capture dissociation";
            case DissociationMethod.SustainedOffResonanceIrradiation: return "sustained off-resonance irradiation";
            case DissociationMethod.LowEnergyCollisionInducedDissociation: return "low-energy collision-induced dissociation";
            case DissociationMethod.Photodissociation: return "photodissociation";
            case DissociationMethod.ElectronTransferDissociation: return "electron transfer dissociation";
            case DissociationMethod.PulsedQDissociation: return "pulsed q dissociation";
            case DissociationMethod.InSourceCollisionInducedDissociation: return "in-source collision-induced dissociation";
            case DissociationMethod.Lift: return "LIFT";
            case DissociationMethod.NegativeElectronTransferDissociation: return "negative electron transfer dissociation";
            case DissociationMethod.BeamTypeCollisionInducedDissociation: return "beam-type collision-induced dissociation";
            case DissociationMethod.TrapTypeCollisionInducedDissociation: return "trap-type collision-induced dissociation";
            case DissociationMethod.SupplementalCollisionInducedDissociation: return "supplemental collision-induced dissociation";
            case DissociationMethod.ElectronActivatedDissociation: return "electron activated dissociation";
            case DissociationMethod.InfraredMultiphotonDissociation: return "infrared multiphoton dissociation";
            case DissociationMethod.UltravioletPhotodissociation: return "ultraviolet photodissociation";
            case DissociationMethod.HigherEnergyBeamTypeCollisionInducedDissociation: return "higher energy beam-type collision-induced dissociation";
            case DissociationMethod.SupplementalBeamTypeCollisionInducedDissociation: return "supplemental beam-type collision-induced dissociation";
            default: throw new InvalidOperationException();
        }
    }

    public static string CURIE(this DissociationMethod term)
    {
        switch (term)
        {
            case DissociationMethod.DissociationMethod: return "MS:1000044";
            case DissociationMethod.CollisionInducedDissociation: return "MS:1000133";
            case DissociationMethod.PlasmaDesorption: return "MS:1000134";
            case DissociationMethod.PostSourceDecay: return "MS:1000135";
            case DissociationMethod.SurfaceInducedDissociation: return "MS:1000136";
            case DissociationMethod.BlackbodyInfraredRadiativeDissociation: return "MS:1000242";
            case DissociationMethod.ElectronCaptureDissociation: return "MS:1000250";
            case DissociationMethod.SustainedOffResonanceIrradiation: return "MS:1000282";
            case DissociationMethod.LowEnergyCollisionInducedDissociation: return "MS:1000433";
            case DissociationMethod.Photodissociation: return "MS:1000435";
            case DissociationMethod.ElectronTransferDissociation: return "MS:1000598";
            case DissociationMethod.PulsedQDissociation: return "MS:1000599";
            case DissociationMethod.InSourceCollisionInducedDissociation: return "MS:1001880";
            case DissociationMethod.Lift: return "MS:1002000";
            case DissociationMethod.NegativeElectronTransferDissociation: return "MS:1003247";
            case DissociationMethod.BeamTypeCollisionInducedDissociation: return "MS:1000422";
            case DissociationMethod.TrapTypeCollisionInducedDissociation: return "MS:1002472";
            case DissociationMethod.SupplementalCollisionInducedDissociation: return "MS:1002679";
            case DissociationMethod.ElectronActivatedDissociation: return "MS:1003294";
            case DissociationMethod.InfraredMultiphotonDissociation: return "MS:1000262";
            case DissociationMethod.UltravioletPhotodissociation: return "MS:1003246";
            case DissociationMethod.HigherEnergyBeamTypeCollisionInducedDissociation: return "MS:1002481";
            case DissociationMethod.SupplementalBeamTypeCollisionInducedDissociation: return "MS:1002678";
            default: throw new InvalidOperationException();
        }
    }
}

public enum IonSelectionProperties
{
    IonSelectionAttribute,
    ChargeState,
    PeakIntensity,
    PossibleChargeState,
    SelectedIonMZ,
    PeakIntensityRank,
    PeakTargetingSuitabilityRank,
    FaimsCompensationVoltage,
    SrmTransitionAttribute,
    SelectedPrecursorMZ,
    IonMobilityDriftTime,
    InverseReducedIonMobility,
    CollisionalCrossSectionalArea,
    ExperimentalPrecursorMonoisotopicMZ,
    SelexionCompensationVoltage,
    SelexionSeparationVoltage,
    FaimsCompensationVoltageRampStart,
    FaimsCompensationVoltageRampEnd,
    ProductIonDriftTime,
    PrecursorIonDetectionProbability,
    ProductIonDetectionProbability,
    NumberOfProductIonObservations,
    NumberOfPrecursorIonObservations,
}

public static class IonSelectionPropertiesMethods
{

    public static readonly Dictionary<string, IonSelectionProperties> FromCURIE = new Dictionary<string, IonSelectionProperties>(
        ((IonSelectionProperties[])Enum.GetValues(typeof(IonSelectionProperties))).Select((v) => new KeyValuePair<string, IonSelectionProperties>(v.CURIE(), v))
    );


    public static string Name(this IonSelectionProperties term)
    {
        switch (term)
        {
            case IonSelectionProperties.IonSelectionAttribute: return "ion selection attribute";
            case IonSelectionProperties.ChargeState: return "charge state";
            case IonSelectionProperties.PeakIntensity: return "peak intensity";
            case IonSelectionProperties.PossibleChargeState: return "possible charge state";
            case IonSelectionProperties.SelectedIonMZ: return "selected ion m/z";
            case IonSelectionProperties.PeakIntensityRank: return "peak intensity rank";
            case IonSelectionProperties.PeakTargetingSuitabilityRank: return "peak targeting suitability rank";
            case IonSelectionProperties.FaimsCompensationVoltage: return "FAIMS compensation voltage";
            case IonSelectionProperties.SrmTransitionAttribute: return "SRM transition attribute";
            case IonSelectionProperties.SelectedPrecursorMZ: return "selected precursor m/z";
            case IonSelectionProperties.IonMobilityDriftTime: return "ion mobility drift time";
            case IonSelectionProperties.InverseReducedIonMobility: return "inverse reduced ion mobility";
            case IonSelectionProperties.CollisionalCrossSectionalArea: return "collisional cross sectional area";
            case IonSelectionProperties.ExperimentalPrecursorMonoisotopicMZ: return "experimental precursor monoisotopic m/z";
            case IonSelectionProperties.SelexionCompensationVoltage: return "SelexION compensation voltage";
            case IonSelectionProperties.SelexionSeparationVoltage: return "SelexION separation voltage";
            case IonSelectionProperties.FaimsCompensationVoltageRampStart: return "FAIMS compensation voltage ramp start";
            case IonSelectionProperties.FaimsCompensationVoltageRampEnd: return "FAIMS compensation voltage ramp end";
            case IonSelectionProperties.ProductIonDriftTime: return "product ion drift time";
            case IonSelectionProperties.PrecursorIonDetectionProbability: return "precursor ion detection probability";
            case IonSelectionProperties.ProductIonDetectionProbability: return "product ion detection probability";
            case IonSelectionProperties.NumberOfProductIonObservations: return "number of product ion observations";
            case IonSelectionProperties.NumberOfPrecursorIonObservations: return "number of precursor ion observations";
            default: throw new InvalidOperationException();
        }
    }

    public static string CURIE(this IonSelectionProperties term)
    {
        switch (term)
        {
            case IonSelectionProperties.IonSelectionAttribute: return "MS:1000455";
            case IonSelectionProperties.ChargeState: return "MS:1000041";
            case IonSelectionProperties.PeakIntensity: return "MS:1000042";
            case IonSelectionProperties.PossibleChargeState: return "MS:1000633";
            case IonSelectionProperties.SelectedIonMZ: return "MS:1000744";
            case IonSelectionProperties.PeakIntensityRank: return "MS:1000906";
            case IonSelectionProperties.PeakTargetingSuitabilityRank: return "MS:1000907";
            case IonSelectionProperties.FaimsCompensationVoltage: return "MS:1001581";
            case IonSelectionProperties.SrmTransitionAttribute: return "MS:1002222";
            case IonSelectionProperties.SelectedPrecursorMZ: return "MS:1002234";
            case IonSelectionProperties.IonMobilityDriftTime: return "MS:1002476";
            case IonSelectionProperties.InverseReducedIonMobility: return "MS:1002815";
            case IonSelectionProperties.CollisionalCrossSectionalArea: return "MS:1002954";
            case IonSelectionProperties.ExperimentalPrecursorMonoisotopicMZ: return "MS:1003208";
            case IonSelectionProperties.SelexionCompensationVoltage: return "MS:1003371";
            case IonSelectionProperties.SelexionSeparationVoltage: return "MS:1003394";
            case IonSelectionProperties.FaimsCompensationVoltageRampStart: return "MS:1003450";
            case IonSelectionProperties.FaimsCompensationVoltageRampEnd: return "MS:1003451";
            case IonSelectionProperties.ProductIonDriftTime: return "MS:1001967";
            case IonSelectionProperties.PrecursorIonDetectionProbability: return "MS:1002223";
            case IonSelectionProperties.ProductIonDetectionProbability: return "MS:1002224";
            case IonSelectionProperties.NumberOfProductIonObservations: return "MS:1002227";
            case IonSelectionProperties.NumberOfPrecursorIonObservations: return "MS:1002228";
            default: throw new InvalidOperationException();
        }
    }
}

public enum ChromatogramTypes
{
    ChromatogramType,
    IonCurrentChromatogram,
    ElectromagneticRadiationChromatogram,
    TemperatureChromatogram,
    PressureChromatogram,
    FlowRateChromatogram,
    TotalIonCurrentChromatogram,
    SelectedIonCurrentChromatogram,
    BasepeakChromatogram,
    SelectedIonMonitoringChromatogram,
    SelectedReactionMonitoringChromatogram,
    ConsecutiveReactionMonitoringChromatogram,
    PrecursorIonCurrentChromatogram,
    AbsorptionChromatogram,
    EmissionChromatogram,
    TotalIonCurrents,
}

public static class ChromatogramTypesMethods
{

    public static readonly Dictionary<string, ChromatogramTypes> FromCURIE = new Dictionary<string, ChromatogramTypes>(
        ((ChromatogramTypes[])Enum.GetValues(typeof(ChromatogramTypes))).Select((v) => new KeyValuePair<string, ChromatogramTypes>(v.CURIE(), v))
    );


    public static string Name(this ChromatogramTypes term)
    {
        switch (term)
        {
            case ChromatogramTypes.ChromatogramType: return "chromatogram type";
            case ChromatogramTypes.IonCurrentChromatogram: return "ion current chromatogram";
            case ChromatogramTypes.ElectromagneticRadiationChromatogram: return "electromagnetic radiation chromatogram";
            case ChromatogramTypes.TemperatureChromatogram: return "temperature chromatogram";
            case ChromatogramTypes.PressureChromatogram: return "pressure chromatogram";
            case ChromatogramTypes.FlowRateChromatogram: return "flow rate chromatogram";
            case ChromatogramTypes.TotalIonCurrentChromatogram: return "total ion current chromatogram";
            case ChromatogramTypes.SelectedIonCurrentChromatogram: return "selected ion current chromatogram";
            case ChromatogramTypes.BasepeakChromatogram: return "basepeak chromatogram";
            case ChromatogramTypes.SelectedIonMonitoringChromatogram: return "selected ion monitoring chromatogram";
            case ChromatogramTypes.SelectedReactionMonitoringChromatogram: return "selected reaction monitoring chromatogram";
            case ChromatogramTypes.ConsecutiveReactionMonitoringChromatogram: return "consecutive reaction monitoring chromatogram";
            case ChromatogramTypes.PrecursorIonCurrentChromatogram: return "precursor ion current chromatogram";
            case ChromatogramTypes.AbsorptionChromatogram: return "absorption chromatogram";
            case ChromatogramTypes.EmissionChromatogram: return "emission chromatogram";
            case ChromatogramTypes.TotalIonCurrents: return "total ion currents";
            default: throw new InvalidOperationException();
        }
    }

    public static string CURIE(this ChromatogramTypes term)
    {
        switch (term)
        {
            case ChromatogramTypes.ChromatogramType: return "MS:1000626";
            case ChromatogramTypes.IonCurrentChromatogram: return "MS:1000810";
            case ChromatogramTypes.ElectromagneticRadiationChromatogram: return "MS:1000811";
            case ChromatogramTypes.TemperatureChromatogram: return "MS:1002715";
            case ChromatogramTypes.PressureChromatogram: return "MS:1003019";
            case ChromatogramTypes.FlowRateChromatogram: return "MS:1003020";
            case ChromatogramTypes.TotalIonCurrentChromatogram: return "MS:1000235";
            case ChromatogramTypes.SelectedIonCurrentChromatogram: return "MS:1000627";
            case ChromatogramTypes.BasepeakChromatogram: return "MS:1000628";
            case ChromatogramTypes.SelectedIonMonitoringChromatogram: return "MS:1001472";
            case ChromatogramTypes.SelectedReactionMonitoringChromatogram: return "MS:1001473";
            case ChromatogramTypes.ConsecutiveReactionMonitoringChromatogram: return "MS:1001474";
            case ChromatogramTypes.PrecursorIonCurrentChromatogram: return "MS:4000025";
            case ChromatogramTypes.AbsorptionChromatogram: return "MS:1000812";
            case ChromatogramTypes.EmissionChromatogram: return "MS:1000813";
            case ChromatogramTypes.TotalIonCurrents: return "MS:4000104";
            default: throw new InvalidOperationException();
        }
    }
}

public enum SampleProperties
{
    SampleAttribute,
    SampleNumber,
    SampleName,
    SampleState,
    SampleMass,
    SampleVolume,
    SampleConcentration,
    SampleBatch,
    LabelFreeSample,
    SampleLabel,
    Emulsion,
    GaseousSampleState,
    LiquidSampleState,
    SolidSampleState,
    Solution,
    Suspension,
    IcatReagent,
    IcplReagent,
    SilacReagent,
    TmtReagent,
    ItraqReagent,
    DiartReagent,
    DileuReagent,
    IcatHeavyReagent,
    IcatLightReagent,
    IcplReagent0,
    IcplReagent4,
    IcplReagent6,
    IcplReagent10,
    SilacHeavyReagent,
    SilacMediumReagent,
    SilacLightReagent,
    TmtReagent126,
    TmtReagent127,
    TmtReagent128,
    TmtReagent129,
    TmtReagent130,
    TmtReagent131,
    TmtReagent127N,
    TmtReagent127C,
    TmtReagent128N,
    TmtReagent128C,
    TmtReagent129N,
    TmtReagent129C,
    TmtReagent130N,
    TmtReagent130C,
    ItraqReagent113,
    ItraqReagent114,
    ItraqReagent115,
    ItraqReagent116,
    ItraqReagent117,
    ItraqReagent118,
    ItraqReagent119,
    ItraqReagent121,
    DiartReagent114,
    DiartReagent115,
    DiartReagent116,
    DiartReagent117,
    DiartReagent118,
    DiartReagent119,
    DileuReagent115,
    DileuReagent116,
    DileuReagent117,
    DileuReagent118,
}

public static class SamplePropertiesMethods
{

    public static readonly Dictionary<string, SampleProperties> FromCURIE = new Dictionary<string, SampleProperties>(
        ((SampleProperties[])Enum.GetValues(typeof(SampleProperties))).Select((v) => new KeyValuePair<string, SampleProperties>(v.CURIE(), v))
    );


    public static string Name(this SampleProperties term)
    {
        switch (term)
        {
            case SampleProperties.SampleAttribute: return "sample attribute";
            case SampleProperties.SampleNumber: return "sample number";
            case SampleProperties.SampleName: return "sample name";
            case SampleProperties.SampleState: return "sample state";
            case SampleProperties.SampleMass: return "sample mass";
            case SampleProperties.SampleVolume: return "sample volume";
            case SampleProperties.SampleConcentration: return "sample concentration";
            case SampleProperties.SampleBatch: return "sample batch";
            case SampleProperties.LabelFreeSample: return "label free sample";
            case SampleProperties.SampleLabel: return "sample label";
            case SampleProperties.Emulsion: return "emulsion";
            case SampleProperties.GaseousSampleState: return "gaseous sample state";
            case SampleProperties.LiquidSampleState: return "liquid sample state";
            case SampleProperties.SolidSampleState: return "solid sample state";
            case SampleProperties.Solution: return "solution";
            case SampleProperties.Suspension: return "suspension";
            case SampleProperties.IcatReagent: return "ICAT reagent";
            case SampleProperties.IcplReagent: return "ICPL reagent";
            case SampleProperties.SilacReagent: return "SILAC reagent";
            case SampleProperties.TmtReagent: return "TMT reagent";
            case SampleProperties.ItraqReagent: return "iTRAQ reagent";
            case SampleProperties.DiartReagent: return "DiART reagent";
            case SampleProperties.DileuReagent: return "DiLeu reagent";
            case SampleProperties.IcatHeavyReagent: return "ICAT heavy reagent";
            case SampleProperties.IcatLightReagent: return "ICAT light reagent";
            case SampleProperties.IcplReagent0: return "ICPL reagent 0";
            case SampleProperties.IcplReagent4: return "ICPL reagent 4";
            case SampleProperties.IcplReagent6: return "ICPL reagent 6";
            case SampleProperties.IcplReagent10: return "ICPL reagent 10";
            case SampleProperties.SilacHeavyReagent: return "SILAC heavy reagent";
            case SampleProperties.SilacMediumReagent: return "SILAC medium reagent";
            case SampleProperties.SilacLightReagent: return "SILAC light reagent";
            case SampleProperties.TmtReagent126: return "TMT reagent 126";
            case SampleProperties.TmtReagent127: return "TMT reagent 127";
            case SampleProperties.TmtReagent128: return "TMT reagent 128";
            case SampleProperties.TmtReagent129: return "TMT reagent 129";
            case SampleProperties.TmtReagent130: return "TMT reagent 130";
            case SampleProperties.TmtReagent131: return "TMT reagent 131";
            case SampleProperties.TmtReagent127N: return "TMT reagent 127N";
            case SampleProperties.TmtReagent127C: return "TMT reagent 127C";
            case SampleProperties.TmtReagent128N: return "TMT reagent 128N";
            case SampleProperties.TmtReagent128C: return "TMT reagent 128C";
            case SampleProperties.TmtReagent129N: return "TMT reagent 129N";
            case SampleProperties.TmtReagent129C: return "TMT reagent 129C";
            case SampleProperties.TmtReagent130N: return "TMT reagent 130N";
            case SampleProperties.TmtReagent130C: return "TMT reagent 130C";
            case SampleProperties.ItraqReagent113: return "iTRAQ reagent 113";
            case SampleProperties.ItraqReagent114: return "iTRAQ reagent 114";
            case SampleProperties.ItraqReagent115: return "iTRAQ reagent 115";
            case SampleProperties.ItraqReagent116: return "iTRAQ reagent 116";
            case SampleProperties.ItraqReagent117: return "iTRAQ reagent 117";
            case SampleProperties.ItraqReagent118: return "iTRAQ reagent 118";
            case SampleProperties.ItraqReagent119: return "iTRAQ reagent 119";
            case SampleProperties.ItraqReagent121: return "iTRAQ reagent 121";
            case SampleProperties.DiartReagent114: return "DiART reagent 114";
            case SampleProperties.DiartReagent115: return "DiART reagent 115";
            case SampleProperties.DiartReagent116: return "DiART reagent 116";
            case SampleProperties.DiartReagent117: return "DiART reagent 117";
            case SampleProperties.DiartReagent118: return "DiART reagent 118";
            case SampleProperties.DiartReagent119: return "DiART reagent 119";
            case SampleProperties.DileuReagent115: return "DiLeu reagent 115";
            case SampleProperties.DileuReagent116: return "DiLeu reagent 116";
            case SampleProperties.DileuReagent117: return "DiLeu reagent 117";
            case SampleProperties.DileuReagent118: return "DiLeu reagent 118";
            default: throw new InvalidOperationException();
        }
    }

    public static string CURIE(this SampleProperties term)
    {
        switch (term)
        {
            case SampleProperties.SampleAttribute: return "MS:1000548";
            case SampleProperties.SampleNumber: return "MS:1000001";
            case SampleProperties.SampleName: return "MS:1000002";
            case SampleProperties.SampleState: return "MS:1000003";
            case SampleProperties.SampleMass: return "MS:1000004";
            case SampleProperties.SampleVolume: return "MS:1000005";
            case SampleProperties.SampleConcentration: return "MS:1000006";
            case SampleProperties.SampleBatch: return "MS:1000053";
            case SampleProperties.LabelFreeSample: return "MS:1002038";
            case SampleProperties.SampleLabel: return "MS:1002602";
            case SampleProperties.Emulsion: return "MS:1000047";
            case SampleProperties.GaseousSampleState: return "MS:1000048";
            case SampleProperties.LiquidSampleState: return "MS:1000049";
            case SampleProperties.SolidSampleState: return "MS:1000050";
            case SampleProperties.Solution: return "MS:1000051";
            case SampleProperties.Suspension: return "MS:1000052";
            case SampleProperties.IcatReagent: return "MS:1002603";
            case SampleProperties.IcplReagent: return "MS:1002606";
            case SampleProperties.SilacReagent: return "MS:1002611";
            case SampleProperties.TmtReagent: return "MS:1002615";
            case SampleProperties.ItraqReagent: return "MS:1002622";
            case SampleProperties.DiartReagent: return "MS:1002771";
            case SampleProperties.DileuReagent: return "MS:1002778";
            case SampleProperties.IcatHeavyReagent: return "MS:1002604";
            case SampleProperties.IcatLightReagent: return "MS:1002605";
            case SampleProperties.IcplReagent0: return "MS:1002607";
            case SampleProperties.IcplReagent4: return "MS:1002608";
            case SampleProperties.IcplReagent6: return "MS:1002609";
            case SampleProperties.IcplReagent10: return "MS:1002610";
            case SampleProperties.SilacHeavyReagent: return "MS:1002612";
            case SampleProperties.SilacMediumReagent: return "MS:1002613";
            case SampleProperties.SilacLightReagent: return "MS:1002614";
            case SampleProperties.TmtReagent126: return "MS:1002616";
            case SampleProperties.TmtReagent127: return "MS:1002617";
            case SampleProperties.TmtReagent128: return "MS:1002618";
            case SampleProperties.TmtReagent129: return "MS:1002619";
            case SampleProperties.TmtReagent130: return "MS:1002620";
            case SampleProperties.TmtReagent131: return "MS:1002621";
            case SampleProperties.TmtReagent127N: return "MS:1002763";
            case SampleProperties.TmtReagent127C: return "MS:1002764";
            case SampleProperties.TmtReagent128N: return "MS:1002765";
            case SampleProperties.TmtReagent128C: return "MS:1002766";
            case SampleProperties.TmtReagent129N: return "MS:1002767";
            case SampleProperties.TmtReagent129C: return "MS:1002768";
            case SampleProperties.TmtReagent130N: return "MS:1002769";
            case SampleProperties.TmtReagent130C: return "MS:1002770";
            case SampleProperties.ItraqReagent113: return "MS:1002623";
            case SampleProperties.ItraqReagent114: return "MS:1002624";
            case SampleProperties.ItraqReagent115: return "MS:1002625";
            case SampleProperties.ItraqReagent116: return "MS:1002626";
            case SampleProperties.ItraqReagent117: return "MS:1002627";
            case SampleProperties.ItraqReagent118: return "MS:1002628";
            case SampleProperties.ItraqReagent119: return "MS:1002629";
            case SampleProperties.ItraqReagent121: return "MS:1002630";
            case SampleProperties.DiartReagent114: return "MS:1002772";
            case SampleProperties.DiartReagent115: return "MS:1002773";
            case SampleProperties.DiartReagent116: return "MS:1002774";
            case SampleProperties.DiartReagent117: return "MS:1002775";
            case SampleProperties.DiartReagent118: return "MS:1002776";
            case SampleProperties.DiartReagent119: return "MS:1002777";
            case SampleProperties.DileuReagent115: return "MS:1002779";
            case SampleProperties.DileuReagent116: return "MS:1002780";
            case SampleProperties.DileuReagent117: return "MS:1002781";
            case SampleProperties.DileuReagent118: return "MS:1002782";
            default: throw new InvalidOperationException();
        }
    }
}

public enum NativeIdentifierFormats
{
    NativeSpectrumIdentifierFormat,
    ThermoNativeidFormat,
    WatersNativeidFormat,
    WiffNativeidFormat,
    BrukerAgilentYepNativeidFormat,
    BrukerBafNativeidFormat,
    BrukerFidNativeidFormat,
    MultiplePeakListNativeidFormat,
    SinglePeakListNativeidFormat,
    ScanNumberOnlyNativeidFormat,
    SpectrumIdentifierNativeidFormat,
    BrukerU2NativeidFormat,
    NoNativeidFormat,
    ShimadzuBiotechNativeidFormat,
    MobilionMbiNativeidFormat,
    SciexTofTofNativeidFormat,
    AgilentMasshunterNativeidFormat,
    SpectrumFromDatabaseIntegerNativeidFormat,
    MascotQueryNumber,
    SpectrumFromProteinscapeDatabaseNativeidFormat,
    SpectrumFromDatabaseStringNativeidFormat,
    SciexTofTofT2DNativeidFormat,
    ScaffoldNativeidFormat,
    BrukerContainerNativeidFormat,
    UimfNativeidFormat,
    BrukerTdfNativeidFormat,
    ShimadzuBiotechQtofNativeidFormat,
    BrukerTsfNativeidFormat,
}

public static class NativeIdentifierFormatsMethods
{

    public static readonly Dictionary<string, NativeIdentifierFormats> FromCURIE = new Dictionary<string, NativeIdentifierFormats>(
        ((NativeIdentifierFormats[])Enum.GetValues(typeof(NativeIdentifierFormats))).Select((v) => new KeyValuePair<string, NativeIdentifierFormats>(v.CURIE(), v))
    );

    public static string Name(this NativeIdentifierFormats term)
    {
        switch (term)
        {
            case NativeIdentifierFormats.NativeSpectrumIdentifierFormat: return "native spectrum identifier format";
            case NativeIdentifierFormats.ThermoNativeidFormat: return "Thermo nativeID format";
            case NativeIdentifierFormats.WatersNativeidFormat: return "Waters nativeID format";
            case NativeIdentifierFormats.WiffNativeidFormat: return "WIFF nativeID format";
            case NativeIdentifierFormats.BrukerAgilentYepNativeidFormat: return "Bruker/Agilent YEP nativeID format";
            case NativeIdentifierFormats.BrukerBafNativeidFormat: return "Bruker BAF nativeID format";
            case NativeIdentifierFormats.BrukerFidNativeidFormat: return "Bruker FID nativeID format";
            case NativeIdentifierFormats.MultiplePeakListNativeidFormat: return "multiple peak list nativeID format";
            case NativeIdentifierFormats.SinglePeakListNativeidFormat: return "single peak list nativeID format";
            case NativeIdentifierFormats.ScanNumberOnlyNativeidFormat: return "scan number only nativeID format";
            case NativeIdentifierFormats.SpectrumIdentifierNativeidFormat: return "spectrum identifier nativeID format";
            case NativeIdentifierFormats.BrukerU2NativeidFormat: return "Bruker U2 nativeID format";
            case NativeIdentifierFormats.NoNativeidFormat: return "no nativeID format";
            case NativeIdentifierFormats.ShimadzuBiotechNativeidFormat: return "Shimadzu Biotech nativeID format";
            case NativeIdentifierFormats.MobilionMbiNativeidFormat: return "Mobilion MBI nativeID format";
            case NativeIdentifierFormats.SciexTofTofNativeidFormat: return "SCIEX TOF/TOF nativeID format";
            case NativeIdentifierFormats.AgilentMasshunterNativeidFormat: return "Agilent MassHunter nativeID format";
            case NativeIdentifierFormats.SpectrumFromDatabaseIntegerNativeidFormat: return "spectrum from database integer nativeID format";
            case NativeIdentifierFormats.MascotQueryNumber: return "Mascot query number";
            case NativeIdentifierFormats.SpectrumFromProteinscapeDatabaseNativeidFormat: return "spectrum from ProteinScape database nativeID format";
            case NativeIdentifierFormats.SpectrumFromDatabaseStringNativeidFormat: return "spectrum from database string nativeID format";
            case NativeIdentifierFormats.SciexTofTofT2DNativeidFormat: return "SCIEX TOF/TOF T2D nativeID format";
            case NativeIdentifierFormats.ScaffoldNativeidFormat: return "Scaffold nativeID format";
            case NativeIdentifierFormats.BrukerContainerNativeidFormat: return "Bruker Container nativeID format";
            case NativeIdentifierFormats.UimfNativeidFormat: return "UIMF nativeID format";
            case NativeIdentifierFormats.BrukerTdfNativeidFormat: return "Bruker TDF nativeID format";
            case NativeIdentifierFormats.ShimadzuBiotechQtofNativeidFormat: return "Shimadzu Biotech QTOF nativeID format";
            case NativeIdentifierFormats.BrukerTsfNativeidFormat: return "Bruker TSF nativeID format";
            default: throw new InvalidOperationException();
        }
    }

    public static string CURIE(this NativeIdentifierFormats term)
    {
        switch (term)
        {
            case NativeIdentifierFormats.NativeSpectrumIdentifierFormat: return "MS:1000767";
            case NativeIdentifierFormats.ThermoNativeidFormat: return "MS:1000768";
            case NativeIdentifierFormats.WatersNativeidFormat: return "MS:1000769";
            case NativeIdentifierFormats.WiffNativeidFormat: return "MS:1000770";
            case NativeIdentifierFormats.BrukerAgilentYepNativeidFormat: return "MS:1000771";
            case NativeIdentifierFormats.BrukerBafNativeidFormat: return "MS:1000772";
            case NativeIdentifierFormats.BrukerFidNativeidFormat: return "MS:1000773";
            case NativeIdentifierFormats.MultiplePeakListNativeidFormat: return "MS:1000774";
            case NativeIdentifierFormats.SinglePeakListNativeidFormat: return "MS:1000775";
            case NativeIdentifierFormats.ScanNumberOnlyNativeidFormat: return "MS:1000776";
            case NativeIdentifierFormats.SpectrumIdentifierNativeidFormat: return "MS:1000777";
            case NativeIdentifierFormats.BrukerU2NativeidFormat: return "MS:1000823";
            case NativeIdentifierFormats.NoNativeidFormat: return "MS:1000824";
            case NativeIdentifierFormats.ShimadzuBiotechNativeidFormat: return "MS:1000929";
            case NativeIdentifierFormats.MobilionMbiNativeidFormat: return "MS:1001186";
            case NativeIdentifierFormats.SciexTofTofNativeidFormat: return "MS:1001480";
            case NativeIdentifierFormats.AgilentMasshunterNativeidFormat: return "MS:1001508";
            case NativeIdentifierFormats.SpectrumFromDatabaseIntegerNativeidFormat: return "MS:1001526";
            case NativeIdentifierFormats.MascotQueryNumber: return "MS:1001528";
            case NativeIdentifierFormats.SpectrumFromProteinscapeDatabaseNativeidFormat: return "MS:1001531";
            case NativeIdentifierFormats.SpectrumFromDatabaseStringNativeidFormat: return "MS:1001532";
            case NativeIdentifierFormats.SciexTofTofT2DNativeidFormat: return "MS:1001559";
            case NativeIdentifierFormats.ScaffoldNativeidFormat: return "MS:1001562";
            case NativeIdentifierFormats.BrukerContainerNativeidFormat: return "MS:1002303";
            case NativeIdentifierFormats.UimfNativeidFormat: return "MS:1002532";
            case NativeIdentifierFormats.BrukerTdfNativeidFormat: return "MS:1002818";
            case NativeIdentifierFormats.ShimadzuBiotechQtofNativeidFormat: return "MS:1002898";
            case NativeIdentifierFormats.BrukerTsfNativeidFormat: return "MS:1003283";
            default: throw new InvalidOperationException();
        }
    }
}


public record ColumnParam
{
    public string Name;
    public string? CURIE;
    public string? UnitCURIE;
    public int Index;
    public string OriginalName;
    public bool IsUnitOnly = false;

    public static ColumnParam FromFieldIndex(Field field, int index)
    {
        var tokens_ = field.Name.Split("_");
        if (tokens_.Length < 3)
        {
            return new ColumnParam(field.Name, null, null, index, field.Name);
        }
        var tokens = tokens_.ToList();
        var cvPrefix = tokens[0];
        if (cvPrefix != "MS" && cvPrefix != "UO")
        {
            return new ColumnParam(field.Name, null, null, index, field.Name);
        }
        var accession = tokens[1];
        var curie = string.Format("{0}:{1}", cvPrefix, accession);
        var indexOfUnit = tokens.FindIndex((v) => v == "unit");
        if (indexOfUnit == -1)
        {
            var name = string.Join('_', tokens.Slice(2, tokens.Count - 2));
            return new ColumnParam(name.Replace("_", " "), curie, null, index, field.Name, false);
        }
        else if (indexOfUnit < tokens.Count - 1)
        {
            var name = string.Join('_', tokens.Slice(2, indexOfUnit - 2));
            var unit = string.Join(':', tokens.Slice(indexOfUnit + 1, tokens.Count - indexOfUnit - 1));
            return new ColumnParam(name.Replace("_", " "), curie, unit, index, field.Name, false);
        }
        else
        {
            var name = string.Join('_', tokens.Slice(2, tokens.Count));
            return new ColumnParam(name.Replace("_", " "), curie, null, index, field.Name, true);
        }
    }

    public static string Inflect(string accessionCURIE, string name, string? unit = null)
    {
        var tokens = accessionCURIE.Split(":").ToList();
        tokens.AddRange(name.Split(" ").Select((v) => v.Replace("m/z", "mz")));
        if (unit != null)
        {
            tokens.Add("unit");
            tokens.AddRange(unit.Split(":"));
        }
        return string.Join("_", tokens);
    }

    public static List<ColumnParam> FromFields(IEnumerable<Field> fields)
    {
        List<ColumnParam> cols = new();

        int i = 0;
        foreach (var f in fields)
        {
            cols.Add(FromFieldIndex(f, i));
            i += 1;
        }

        return cols;
    }

    public ColumnParam(string name, string? curie, string? unit, int index, string originalName, bool isUnitOnly = false)
    {
        Name = name;
        CURIE = curie;
        UnitCURIE = unit;
        Index = index;
        OriginalName = originalName;
        IsUnitOnly = isUnitOnly;
    }
}


[JsonConverter(typeof(ParamJsonConverter))]
public class Param
{
    public string Name { get; set; }
    public string? AccessionCURIE { get; set; }
    internal object? rawValue;
    public string? UnitCURIE { get; set; }

    public bool IsDouble()
    {
        return rawValue is double || rawValue is float;
    }

    public bool IsLong()
    {

        return rawValue is long || rawValue is int || rawValue is uint || rawValue is ulong;
    }

    public bool IsString()
    {
        return rawValue is string;
    }

    public bool IsBoolean()
    {
        return rawValue is bool;
    }

    public bool IsNull()
    {
        return rawValue == null;
    }

    public bool ValidateValueType()
    {
        return IsNull() || IsLong() || IsDouble() || IsBoolean() || IsString();
    }

    public Param(string name, object? rawValue)
    {
        Name = name;
        this.rawValue = rawValue;
        ValidateValueType();
    }

    public Param(string name, string? accession, object? rawValue)
    {
        Name = name;
        AccessionCURIE = accession;
        this.rawValue = rawValue;
        ValidateValueType();
    }

    public Param(string name, string? accession, object? rawValue, string? unit)
    {
        Name = name;
        AccessionCURIE = accession;
        this.rawValue = rawValue;
        UnitCURIE = unit;
        ValidateValueType();
    }

    public string AsString()
    {
        var s = Convert.ToString(rawValue);
        return s == null ? "" : s;
    }

    public long AsLong()
    {
        return Convert.ToInt64(rawValue);
    }

    public double AsDouble()
    {
        return Convert.ToDouble(rawValue);
    }

    public bool AsBoolean()
    {
        return Convert.ToBoolean(rawValue);
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append("Param {\n");
        builder.Append("\tName = ");
        builder.Append(Name);
        builder.Append(",\n\tAccessionCURIE = ");
        builder.Append(AccessionCURIE);
        builder.Append(",\n\trawValue = ");
        builder.Append(rawValue);
        builder.Append(",\n\tUnitCURIE = ");
        builder.Append(UnitCURIE);
        builder.Append("\n}");
        return builder.ToString();
    }
}


public class ParamJsonConverter : JsonConverter<Param>
{
    public override Param? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException();
        }
        string? name = null;
        object? value = null;
        string? accession = null;
        string? unit = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (name == null)
                {
                    throw new JsonException("parameter name cannot be null");
                }
                return new Param(name, accession, value, unit);
            }

            // Get the key.
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException();
            }

            string? propertyName = reader.GetString();
            reader.Read();
            switch (propertyName)
            {
                case null:
                    {
                        throw new JsonException("property name cannot be null");
                    }
                case "name":
                    {
                        name = reader.GetString();
                        if (name == null)
                        {
                            throw new JsonException("parameter name cannot be null");
                        }
                        break;
                    }
                case "value":
                    {
                        if (reader.TokenType == JsonTokenType.Number)
                        {
                            double vdouble;
                            long vlong;
                            if (reader.TryGetDouble(out vdouble))
                            {
                                value = vdouble;
                            }
                            else if (reader.TryGetInt64(out vlong))
                            {
                                value = vlong;
                            }
                        }
                        else if ((reader.TokenType == JsonTokenType.True) || reader.TokenType == JsonTokenType.False)
                        {
                            value = reader.GetBoolean();
                        }
                        else if (reader.TokenType == JsonTokenType.String)
                        {
                            value = reader.GetString();
                        }
                        else if (reader.TokenType == JsonTokenType.Null)
                        {
                            value = null;
                            reader.Skip();
                        }

                        break;
                    }

                case "accession":
                    {
                        accession = reader.GetString();
                        break;
                    }
                case "unit":
                    {
                        unit = reader.GetString();
                        break;
                    }
                default:
                    {
                        break;
                    }
            }
        }
        throw new JsonException("Unclosed parameter object");
    }

    public override void Write(Utf8JsonWriter writer, Param value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteString("name", value.Name);
        writer.WriteString("accession", value.AccessionCURIE);

        if (value.IsBoolean())
        {
            writer.WriteBoolean("value", value.AsBoolean());
        }
        else if (value.IsDouble())
        {
            writer.WriteNumber("value", value.AsDouble());
        }
        else if (value.IsLong())
        {
            writer.WriteNumber("value", value.AsLong());
        }
        else if (value.IsString())
        {
            writer.WriteString("value", value.AsString());
        }
        else if (value.IsNull())
        {
            writer.WriteNull("value");
        }
        else
        {
            throw new InvalidCastException($"Could not determine how to coerce {value.rawValue} of type {value.rawValue?.GetType()}");
        }

        writer.WriteString("unit", value.UnitCURIE);
        writer.WriteEndObject();
    }
}


public static class ParamListMethods
{
    public static Param? FindCURIE(this List<Param> list, string curie)
    {
        return list.Find(p => p.AccessionCURIE == curie);
    }
}


public class ControlledVocabularyEntry : IEquatable<ControlledVocabularyEntry>
{

    public static List<ControlledVocabularyEntry> ControlledVocabularyEntries = [
        PSIMS,
        Unit,
    ];

    public static ControlledVocabularyEntry PSIMS => new(
        "MS",
        "Proteomics Standards Initiative Mass Spectrometry Ontology",
        "http://purl.obolibrary.org/obo/ms/4.1.249/psi-ms.obo",
        "4.1.249"
    );

    public static ControlledVocabularyEntry Unit => new(
        "UO",
        "Units of measurement ontology",
        "http://purl.obolibrary.org/obo/uo/releases/2026-01-16/uo.obo",
        "2026-01-16"
    );

    public static ControlledVocabularyEntry EFO => new(
        "EFO",
        "Experimental Factor Ontology",
        "http://www.ebi.ac.uk/efo/releases/v3.90.0/efo.obo",
        "v3.90.0"
    );

    public static ControlledVocabularyEntry BFO => new(
        "BFO",
        "Basic Formal Ontology",
        "http://purl.obolibrary.org/obo/bfo/2019-08-26/bfo.obo",
        "2019-08-26"
    );

    public static ControlledVocabularyEntry BTO => new(
        "BTO",
        "The BRENDA Tissue Ontology (BTO)",
        "http://purl.obolibrary.org/obo/bto/releases/2021-10-26/bto.owl",
        "2021-10-26"
    );

    public static ControlledVocabularyEntry PRIDE => new(
        "PRIDE",
        "Proteomics Identification Database Ontology",
        "http://purl.obolibrary.org/obo/pride/releases/2026-06-01/pride.obo",
        "2026-06-01"
    );

    public static ControlledVocabularyEntry MSImaging => new(
            "IMS",
            "Imaging Mass Spectrometry Ontology",
            "https://raw.githubusercontent.com/imzML/imzML/refs/heads/master/imagingMS.obo",
            "1.1.0"
        );

    public ControlledVocabularyEntry(string id, string name, string uri, string? version=null)
    {
        Id = id;
        Name = name;
        URI = uri;
        Version = version;
    }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("full_name")]
    public string Name { get; set; }

    [JsonPropertyName("uri")]
    public string URI { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    public bool Equals(ControlledVocabularyEntry? other)
    {
        if (other == null) return false;
        return other.Id == Id && other.Name == Name && other.URI == URI && other.Version == Version;
    }
}