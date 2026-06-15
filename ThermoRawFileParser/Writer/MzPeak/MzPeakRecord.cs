using System.Collections.Generic;

namespace ThermoRawFileParser.Writer
{
    // One PARAM value, used to collect CV prefixes for the generated cv_list.
    internal sealed class MzPeakParam
    {
        public string Accession;
        public string Name;
        public string Unit;
        public double? Float;
        public long? Integer;
        public string String;
        public bool? Boolean;
    }

    // Everything one scan contributes to the metadata facets, staged per scan and committed only when
    // the scan fully succeeds.
    internal sealed class MzPeakRecord
    {
        public ulong Ordinal;
        public int ScanNumber;
        public int MsLevel;
        public string Id;
        public double Time;
        public sbyte? Polarity;
        public string Representation;
        public string SpectrumType;
        public double? LowestMz;
        public double? HighestMz;
        public double? BasePeakMz;
        public float? BasePeakIntensity;
        public float TotalIonCurrent;
        public ulong DataPointCount;
        public ulong? PeakCount;
        public List<MzPeakParam> SpectrumParams = new List<MzPeakParam>();

        public float ScanStartTime;
        public uint? PresetScanConfiguration;
        public string FilterString;
        public float? IonInjectionTime;
        public double? IonMobilityValue;
        public string IonMobilityType;
        public uint InstrumentConfigRef;
        public float WindowLower;
        public float WindowUpper;

        public bool IsMsn;
        public bool HasReaction;
        public ulong? PrecursorIndex;
        public string PrecursorId;
        public float? IsolationTarget;
        public float? IsolationLowerOffset;
        public float? IsolationUpperOffset;
        public List<MzPeakParam> ActivationParams = new List<MzPeakParam>();
        public double? SelectedIonMz;
        public int? ChargeState;
        public float? SelectedIonIntensity;
    }
}
