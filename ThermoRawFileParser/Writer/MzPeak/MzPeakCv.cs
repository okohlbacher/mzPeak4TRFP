namespace ThermoRawFileParser.Writer
{
    // Single source of truth for the CURIE accession strings used by the mzPeak writer. Each accession
    // is declared exactly once here so the schema-building, column-emitting and metadata paths share
    // one constant instead of repeating the literal. The emitted strings are unchanged.
    internal static class MzPeakCv
    {
        // Units.
        public const string MzUnit = "MS:1000040";              // m/z
        public const string CountsUnit = "MS:1000131";          // number of detector counts (intensity)
        public const string MinuteUnit = "UO:0000031";          // minute
        public const string MillisecondUnit = "UO:0000028";     // millisecond
        public const string DimensionlessUnit = "UO:0000186";   // dimensionless unit
        public const string ElectronvoltUnit = "UO:0000266";    // electronvolt

        // Spectrum / scan metadata accessions.
        public const string ScanPolarity = "MS:1000465";
        public const string MsLevel = "MS:1000511";
        public const string SpectrumRepresentation = "MS:1000525";
        public const string SpectrumType = "MS:1000559";
        public const string LowestObservedMz = "MS:1000528";
        public const string HighestObservedMz = "MS:1000527";
        public const string NumberOfDataPoints = "MS:1003060";
        public const string NumberOfPeaks = "MS:1003059";
        public const string BasePeakMz = "MS:1000504";
        public const string BasePeakIntensity = "MS:1000505";
        public const string TotalIonCurrent = "MS:1000285";
        public const string ScanStartTime = "MS:1000016";
        public const string PresetScanConfiguration = "MS:1000616";
        public const string FilterString = "MS:1000512";
        public const string IonInjectionTime = "MS:1000927";
        public const string ScanWindowLowerLimit = "MS:1000501";
        public const string ScanWindowUpperLimit = "MS:1000500";

        // Precursor / selected-ion accessions.
        public const string IsolationWindowTargetMz = "MS:1000827";
        public const string IsolationWindowLowerOffset = "MS:1000828";
        public const string IsolationWindowUpperOffset = "MS:1000829";
        public const string SelectedIonMz = "MS:1000744";
        public const string ChargeState = "MS:1000041";
        public const string SelectedIonIntensity = "MS:1000042";
        public const string CollisionEnergy = "MS:1000045";

        // Spectrum-representation / type values.
        public const string CentroidSpectrum = "MS:1000127";
        public const string ProfileSpectrum = "MS:1000128";
        public const string Ms1Spectrum = "MS:1000579";
        public const string MsnSpectrum = "MS:1000580";

        // Source-file / instrument / data-processing accessions.
        public const string ThermoNativeIdFormat = "MS:1000768";
        public const string ThermoRawFormat = "MS:1000563";
        public const string InstrumentSerialNumber = "MS:1000529";
        public const string ThermoRawFileParser = "MS:1003145";
        public const string FileFormatConversion = "MS:1000530";

        // Chromatogram metadata accessions.
        public const string ChromatogramType = "MS:1000626";
        public const string MzArrayData = "MS:1000235"; // m/z array (chromatogram TIC scalar marker)

        // Chrom-data array prefixes (must reach cv_list).
        public const string FloatArray = "MS:1000523";
        public const string TimeArray = "MS:1000595";
        public const string IntensityValues = "MS:1000521";
        public const string IntensityArray = "MS:1000515";
        public const string MsLevelData = "MS:1000522";
        public const string MsLevelArray = "MS:1000786";

        // The nine chrom-data CURIEs whose prefixes must reach cv_list.
        public static readonly string[] ChromDataAccessions =
        {
            FloatArray, TimeArray, MinuteUnit, IntensityValues, IntensityArray,
            CountsUnit, MsLevelData, MsLevelArray, DimensionlessUnit
        };

        // Transform / encoding accessions (chunk + numpress layouts).
        public const string ChunkEncoding = "MS:1003089";          // delta chunk encoding
        public const string MzTransform = "MS:1003901";            // m/z transform
        public const string IntensityTransform = "MS:1003902";     // intensity transform
        public const string NumpressLinear = "MS:1002312";         // numpress-linear m/z bytes
    }
}
