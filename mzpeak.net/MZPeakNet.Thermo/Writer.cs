using System.Reflection;
using System.Text.RegularExpressions;

using Apache.Arrow;
using Apache.Arrow.Types;

using MZPeak.Compute;
using MZPeak.ControlledVocabulary;
using MZPeak.Metadata;
using MZPeak.Reader.Visitors;
using MZPeak.Storage;
using MZPeak.Writer;
using MZPeak.Writer.Data;
using ParquetSharp;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;
namespace MZPeak.Thermo;


public struct IsolationWindow
{
    public double LowerMZ;
    public double TargetMZ;
    public double UpperMZ;
    public double IsolationWidth;

    public IsolationWindow(double isolationWidth, double monoisotopicMZ, double isolationOffset)
    {
        LowerMZ = monoisotopicMZ + isolationOffset - isolationWidth;
        UpperMZ = monoisotopicMZ + isolationOffset + isolationWidth;
        TargetMZ = monoisotopicMZ;
    }

    public List<Param> ToParamList()
    {
        return new()
        {
            new Param(
                IsolationWindowProperties.IsolationWindowTargetMZ.Name(),
                IsolationWindowProperties.IsolationWindowTargetMZ.CURIE(),
                TargetMZ,
                Unit.MZ.CURIE()
            ),
            new Param(
                IsolationWindowProperties.IsolationWindowLowerOffset.Name(),
                IsolationWindowProperties.IsolationWindowLowerOffset.CURIE(),
                TargetMZ - LowerMZ,
                Unit.MZ.CURIE()
            ),
            new Param(
                IsolationWindowProperties.IsolationWindowUpperOffset.Name(),
                IsolationWindowProperties.IsolationWindowUpperOffset.CURIE(),
                UpperMZ - TargetMZ,
                Unit.MZ.CURIE()
            ),
        };
    }
}

public record ActivationProperties(ActivationType Dissociation, double Energy)
{
    public List<Param> AsParamList()
    {
        var dissociation = Dissociation switch
        {
            ActivationType.CollisionInducedDissociation => DissociationMethod.CollisionInducedDissociation,
            ActivationType.ElectronCaptureDissociation => DissociationMethod.ElectronCaptureDissociation,
            ActivationType.MultiPhotonDissociation => DissociationMethod.Photodissociation,
            ActivationType.ElectronTransferDissociation => DissociationMethod.ElectronTransferDissociation,
            ActivationType.HigherEnergyCollisionalDissociation => DissociationMethod.BeamTypeCollisionInducedDissociation,
            ActivationType.NegativeElectronTransferDissociation => DissociationMethod.NegativeElectronTransferDissociation,
            ActivationType.UltraVioletPhotoDissociation => DissociationMethod.UltravioletPhotodissociation,
            ActivationType.Any => DissociationMethod.DissociationMethod,
            _ => throw new NotImplementedException($"{Dissociation} not mapped"),
        };

        return new()
        {
            new Param(dissociation.Name(), accession: dissociation.CURIE(), rawValue: null),
            new Param("collision energy", "MS:1000045", Energy, Unit.Volt.CURIE())
        };
    }
}

public record PrecursorProperties(double MonoisotopicMZ, int PrecursorCharge, IsolationWindow IsolationWindow, int MasterScanNumber, ActivationProperties Activation)
{ }


public record AcquisitionProperties(
    double InjectionTime,
    MassAnalyzerType Analyzer,
    IonizationModeType Ionization,
    double LowMZ,
    double HighMZ,
    int ScanEventNumber,
    float? Resolution)
{ }


public class ConversionContextHelper
{
    private const string InjectionTimeKey = "Ion Injection Time (ms)";
    private const string ScanEventKey = "Scan Evnet";
    private const string MasterScanKey = "Master Scan";
    private const string MonoisotopicMZKey = "Monoisotopic M/Z";
    private const string ChargeStateKey = "Charge State";
    private static readonly string[] IsolationLevelKeys = [
        "MS2 Isolation Width",
        "MS3 Isolation Width",
        "MS4 Isolation Width",
        "MS5 Isolation Width",
        "MS6 Isolation Width",
        "MS7 Isolation Width",
        "MS8 Isolation Width",
        "MS9 Isolation Width",
        "MS10 Isolation Width"
    ];

    private static readonly string OrbitrapResolutionKey = "Orbitrap Resolution";
    private static readonly string FTResolutionKey = "FT Resolution";
    public static Dictionary<MSOrderType, int> MSLevelMap = new Dictionary<MSOrderType, int>() {
            {MSOrderType.Ms, 1},
            {MSOrderType.Ms2, 2},
            {MSOrderType.Ms3, 3},
            {MSOrderType.Ms4, 4},
            {MSOrderType.Ms5, 5},
            {MSOrderType.Ms6, 6},
            {MSOrderType.Ms7, 7},
            {MSOrderType.Ms8, 8},
            {MSOrderType.Ms9, 9},
            {MSOrderType.Ms10, 10},
            {MSOrderType.Ng, 2},
            {MSOrderType.Nl, 2},
            {MSOrderType.Par, 2},
        };

    /// <summary>
    /// An index look up mapping trailer keys by index that lets us avoid
    /// looping over all trailer entries
    /// </summary>
    public Dictionary<string, int> TrailerMap;
    public List<HeaderItem> Headers;
    public Dictionary<int, List<int?>> PreviousMSLevels;
    public Dictionary<int, uint> MSLevelCounts;

    public ConversionContextHelper()
    {
        TrailerMap = new();
        Headers = new();
        PreviousMSLevels = new();
        MSLevelCounts = new();
    }

    public bool GetShortTrailerExtraFor(IRawDataPlus accessor, int scanNumber, string key, out short value)
    {
        object tmp;
        HeaderItem header;
        int headerIdx;

        if (TrailerMap.TryGetValue(key, out headerIdx))
        {
            tmp = accessor.GetTrailerExtraValue(scanNumber, headerIdx);
            header = Headers[headerIdx];
            if (tmp != null)
            {
                try
                {
                    switch (header.DataType)
                    {
                        case GenericDataTypes.SHORT:
                            {
                                value = (short)tmp;
                                return true;
                            }
                        case GenericDataTypes.LONG:
                            {
                                value = Convert.ToInt16(tmp);
                                return true;
                            }
                        case GenericDataTypes.ULONG:
                            {
                                value = Convert.ToInt16(tmp);
                                return true;
                            }
                        case GenericDataTypes.USHORT:
                            {
                                value = (short)(ushort)tmp;
                                return true;
                            }
                        default:
                            {
                                value = Convert.ToInt16(tmp);
                                return true;
                            }
                    }
                }
                catch (InvalidCastException)
                {
                    value = Convert.ToInt16(tmp);
                    return true;
                }

            }
        }
        value = 0;
        return false;
    }

    public bool GetIntTrailerExtraFor(IRawDataPlus accessor, int scanNumber, string key, out int value, int defaultValue = 0)
    {
        object tmp;
        HeaderItem header;
        int headerIdx;

        if (TrailerMap.TryGetValue(key, out headerIdx))
        {
            tmp = accessor.GetTrailerExtraValue(scanNumber, headerIdx);
            header = Headers[headerIdx];
            if (tmp != null)
            {
                try
                {
                    switch (header.DataType)
                    {
                        case GenericDataTypes.SHORT:
                            {
                                value = (short)tmp;
                                return true;
                            }
                        case GenericDataTypes.LONG:
                            {
                                value = (int)(long)tmp;
                                return true;
                            }
                        case GenericDataTypes.ULONG:
                            {
                                value = (int)(ulong)tmp;
                                return true;
                            }
                        case GenericDataTypes.USHORT:
                            {
                                value = (ushort)tmp;
                                return true;
                            }
                        default:
                            {
                                value = Convert.ToInt32(tmp);
                                return true;
                            }
                    }
                }
                catch (InvalidCastException)
                {
                    value = Convert.ToInt32(tmp);
                    return true;
                }
            }
        }
        value = defaultValue;
        return false;
    }

    public bool GetDoubleTrailerExtraFor(IRawDataPlus accessor, int scanNumber, string key, out double value)
    {
        object tmp;
        HeaderItem header;
        int headerIdx;

        if (TrailerMap.TryGetValue(key, out headerIdx))
        {
            tmp = accessor.GetTrailerExtraValue(scanNumber, headerIdx);
            header = Headers[headerIdx];
            if (tmp != null)
            {
                try
                {
                    switch (header.DataType)
                    {
                        case GenericDataTypes.FLOAT:
                            {
                                value = (float)tmp;
                                return true;
                            }
                        case GenericDataTypes.DOUBLE:
                            {
                                value = (double)tmp;
                                return true;
                            }
                            ;
                        default:
                            {
                                value = Convert.ToDouble(tmp);
                                return true;
                            }
                    }
                }
                catch (InvalidCastException)
                {
                    value = Convert.ToDouble(tmp);
                    return true;
                }
            }
        }
        value = 0;
        return false;
    }

    private void BuildScanTypeMap(IRawDataPlus accessor)
    {
        Dictionary<int, uint> msLevelCounts = new() {
                {1, 0},
                {2, 0},
                {3, 0},
                {4, 0},
                {5, 0},
                {6, 0},
                {7, 0},
                {8, 0},
                {9, 0},
                {10, 0},
            };
        Dictionary<int, List<int?>> previousMSLevels = new();
        Dictionary<int, int?> lastMSLevels = new() {
                {1, null},
                {2, null},
                {3, null},
                {4, null},
                {5, null},
                {6, null},
                {7, null},
                {8, null},
                {9, null},
                {10, null},
            };

        var last = accessor.RunHeaderEx.LastSpectrum;
        for (var i = accessor.RunHeaderEx.FirstSpectrum; i <= last; i++)
        {
            var filter = accessor.GetFilterForScanNumber(i);
            var msLevel = MSLevelMap[filter.MSOrder]; ;

            msLevelCounts[msLevel] += 1;

            List<int?> backwards = new();
            for (short j = 1; j < msLevel + 1; j++)
            {
                var o = lastMSLevels[j];
                backwards.Add(o);
            }
            previousMSLevels[i] = backwards;

            lastMSLevels[msLevel] = i;
        }

        PreviousMSLevels = previousMSLevels;
        MSLevelCounts = msLevelCounts;
    }

    public void Initialize(IRawDataPlus accessor)
    {
        var headers = accessor.GetTrailerExtraHeaderInformation();
        for (var i = 0; i < headers.Length; i++)
        {
            var header = headers[i];
            var label = header.Label.TrimEnd(':');
            TrailerMap[label] = i;
        }
        Headers = [.. headers];
        BuildScanTypeMap(accessor);
    }

    public Dictionary<(MassAnalyzerType, IonizationModeType), long> FindAllMassAnalyzers(IRawDataPlus accessor)
    {
        var analyzers = new Dictionary<(MassAnalyzerType, IonizationModeType), long>();
        var events = accessor.ScanEvents;

        int counter = 0;
        for (var segmentIdx = 0; segmentIdx < events.Segments; segmentIdx++)
        {
            for (var eventIdx = 0; eventIdx < events.GetEventCount(segmentIdx); eventIdx++)
            {
                var ev = events.GetEvent(segmentIdx, eventIdx);
                var a = ev.MassAnalyzer;
                var i = ev.IonizationMode;
                if (analyzers.ContainsKey((a, i)))
                {
                    continue;
                }
                analyzers.Add((a, i), counter);
                counter += 1;
            }
        }
        return analyzers;
    }

    public ActivationProperties ExtractActivation(int msLevel, IScanFilter filter)
    {
        ActivationProperties activation = new ActivationProperties(
            filter.GetActivation(msLevel - 2),
            filter.GetEnergy(msLevel - 2)
        );
        return activation;
    }

    /// <summary>
    /// Find the scan number of the precursor, which is assumed to be the most recent spectrum of a lower
    /// MS level, if it was not indicated some other way. This method is used when the Master Scan Number was
    /// not set in a trailer value.
    /// </summary>
    /// <param name="scanNumber">The scan number to search back from</param>
    /// <param name="msLevel">The MS level to search for lesser values from</param>
    /// <param name="accessor">The current RAW file accessor</param>
    /// <returns>The scan number of the most recent lower MS level spectrum</returns>
    int FindPreviousPrecursor(int scanNumber, int msLevel, IRawDataPlus accessor)
    {
        var cacheLookUp = PreviousMSLevels[scanNumber][msLevel - 1];
        if (cacheLookUp != null)
        {
            return cacheLookUp.Value;
        }
        int i = scanNumber - 1;
        while (i > 0)
        {
            var filter = accessor.GetFilterForScanNumber(i);
            var levelOf = MSLevelMap[filter.MSOrder];
            if (levelOf < msLevel)
            {
                return i;
            }
            else
            {
                i -= 1;
            }
        }

        return i;
    }

    public (PrecursorProperties?, AcquisitionProperties) ExtractPrecursorAndTrailerMetadata(int scanNumber, int msLevel, IScanFilter filter, IRawDataPlus accessor, ScanStatistics stats)
    {
        var trailers = accessor.GetTrailerExtraInformation(scanNumber);

        var n = trailers.Length;
        double monoisotopicMZ = 0.0;
        short precursorCharge = 0;
        double isolationWidth = 0.0;
        double injectionTime = 0.0;
        int masterScanNumber = -1;
        short scanEventNum = 1;
        double resolution = 0.0;
        float? resolution_opt = null;

        GetDoubleTrailerExtraFor(accessor, scanNumber, InjectionTimeKey, out injectionTime);
        GetShortTrailerExtraFor(accessor, scanNumber, ScanEventKey, out scanEventNum);

        if (msLevel > 1)
        {
            GetIntTrailerExtraFor(accessor, scanNumber, MasterScanKey, out masterScanNumber, -1);
            GetDoubleTrailerExtraFor(accessor, scanNumber, MonoisotopicMZKey, out monoisotopicMZ);
            GetShortTrailerExtraFor(accessor, scanNumber, ChargeStateKey, out precursorCharge);
            GetDoubleTrailerExtraFor(accessor, scanNumber, IsolationLevelKeys[msLevel - 2], out isolationWidth);
        }

        if (!GetDoubleTrailerExtraFor(accessor, scanNumber, OrbitrapResolutionKey, out resolution))
        {
            GetDoubleTrailerExtraFor(accessor, scanNumber, FTResolutionKey, out resolution);
        }
        ;
        resolution_opt = resolution == 0.0 ? null : (float)resolution;

        AcquisitionProperties acquisitionProperties = new AcquisitionProperties(injectionTime, filter.MassAnalyzer, IonizationModeType.Any, stats.LowMass, stats.HighMass, scanEventNum, resolution_opt);

        if (msLevel > 1 && isolationWidth == 0.0)
        {
            isolationWidth = filter.GetIsolationWidth(msLevel - 2) / 2;
        }
        if (msLevel > 1)
        {
            double isolationOffset = filter.GetIsolationWidthOffset(msLevel - 2);
            if (monoisotopicMZ == 0.0)
            {
                monoisotopicMZ = filter.GetMass(msLevel - 2);
            }

            if (masterScanNumber == -1)
            {
                masterScanNumber = FindPreviousPrecursor(scanNumber, msLevel, accessor);
            }

            ActivationProperties activation = ExtractActivation(msLevel, filter);
            IsolationWindow window = new IsolationWindow(isolationWidth, monoisotopicMZ, isolationOffset);
            PrecursorProperties props = new PrecursorProperties(monoisotopicMZ, precursorCharge, window, masterScanNumber, activation);
            return (props, acquisitionProperties);
        }
        else
        {
            return (null, acquisitionProperties);
        }
    }

    int FirstSpectrum(IRawDataPlus accessor) => accessor.RunHeader.FirstSpectrum;
    int LastSpectrum(IRawDataPlus accessor) => accessor.RunHeader.LastSpectrum;

    public (ChromatogramInfo, List<IArrowArray>) ReadSummaryTrace(TraceType traceType, IRawDataPlus accessor)
    {
        var ticSettings = new ChromatogramTraceSettings(traceType);
        var tic = accessor.GetChromatogramDataEx([ticSettings], FirstSpectrum(accessor), LastSpectrum(accessor));
        var signals = ChromatogramSignal.FromChromatogramData(tic);
        var signal = signals[0];

        var times = Compute.Compute.CastDouble(signal.Times);
        var intensities = Compute.Compute.CastDouble(signal.Intensities);
        var info = traceType switch
        {
            TraceType.TIC => new ChromatogramInfo(0, "TIC", parameters: [
                new Param(
                    ChromatogramTypes.TotalIonCurrentChromatogram.Name(),
                    ChromatogramTypes.TotalIonCurrentChromatogram.CURIE(), null),
            ]),
            TraceType.BasePeak => new ChromatogramInfo(0, "BPC", parameters: [
                new Param(
                    ChromatogramTypes.BasepeakChromatogram.Name(),
                    ChromatogramTypes.BasepeakChromatogram.CURIE(), null),
            ]),
            _ => throw new NotImplementedException()
        };

        return (info, [times, intensities]);
    }

    public FileDescription GetFileDescription(IRawDataPlus accessor)
    {
        var descr = new FileDescription();
        uint counter;
        // Required CV term "data file content" (MS:1000524): the cv_mapping rule has use_term=false +
        // allow_children, checking contents[]/accession. The child terms MS:1000579 (MS1 spectrum) /
        // MS:1000580 (MSn spectrum) must therefore carry the CURIE in their *accession* field — the
        // 3-arg Param ctor (name, accession, value) — not the 2-arg ctor which leaves accession null.
        if (MSLevelCounts.TryGetValue(1, out counter) && counter > 0)
            descr.Contents.Add(new Param(SpectrumType.Ms1Spectrum.Name(), SpectrumType.Ms1Spectrum.CURIE(), null));
        if (MSLevelCounts.TryGetValue(2, out counter) && counter > 0)
            descr.Contents.Add(new Param(SpectrumType.MsnSpectrum.Name(), SpectrumType.MsnSpectrum.CURIE(), null));

        var path = accessor.Path;
        if (path.Contains("\\"))
        {
            path = path.Replace("\\", "/");
        }
        path = "file:///" + path;

        var sfile = new SourceFile(
            "RAW1",
            accessor.FileName,
            path,
            [
                new Param(
                    NativeIdentifierFormats.ThermoNativeidFormat.Name(),
                    NativeIdentifierFormats.ThermoNativeidFormat.CURIE(),
                    null)
            ]);
        descr.SourceFiles.Add(sfile);
        return descr;
    }

    public List<(Device, int)> InstrumentCountsOf(IRawDataPlus accessor)
    {
        return [
            (Device.MS, accessor.GetInstrumentCountOfType(Device.MS)),
            (Device.UV, accessor.GetInstrumentCountOfType(Device.UV)),
            (Device.Pda, accessor.GetInstrumentCountOfType(Device.Pda)),
            (Device.Analog, accessor.GetInstrumentCountOfType(Device.Analog)),
            (Device.Other, accessor.GetInstrumentCountOfType(Device.Other)),
        ];
    }

    public List<ArrowStatusLog> TuneData(IRawDataPlus accessor)
    {
        var nEntries = accessor.GetTuneDataCount();

        var headers = accessor.GetTuneDataHeaderInformation();

        Dictionary<string, StatusLogBuilder> logs = new();

        for (var i = 0; i < nEntries; i++)
        {
            var values = accessor.GetTuneDataValues(i);
            foreach (var (datum, header) in values.Zip(headers))
            {
                var dType = header.DataType;
                if (dType == GenericDataTypes.NULL)
                {
                    continue;
                }
                switch (dType)
                {
                    case GenericDataTypes.YESNO:
                    case GenericDataTypes.ONOFF:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Int32(header.Label));
                            }
                            logs[header.Label].Add(i, (bool)datum);
                            break;
                        }
                    case GenericDataTypes.Bool:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Int32(header.Label));
                            }

                            logs[header.Label].Add(i, (bool)datum);
                            break;
                        }
                    case GenericDataTypes.CHAR:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.String(header.Label));
                            }
                            logs[header.Label].Add(i, datum.ToString() ?? "");
                            break;
                        }
                    case GenericDataTypes.CHAR_STRING:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.String(header.Label));
                            }
                            logs[header.Label].Add(i, datum.ToString() ?? "");
                            break;
                        }
                    case GenericDataTypes.WCHAR_STRING:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.String(header.Label));
                            }
                            logs[header.Label].Add(i, (string)datum);
                            break;
                        }
                    case GenericDataTypes.FLOAT:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Double(header.Label));
                            }
                            logs[header.Label].Add(i, (double)(float)datum);
                            break;
                        }
                    case GenericDataTypes.DOUBLE:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Double(header.Label));
                            }
                            logs[header.Label].Add(i, (double)datum);
                            break;
                        }
                    case GenericDataTypes.Int:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Int(header.Label));
                            }
                            logs[header.Label].Add(i, (int)datum);
                            break;
                        }
                    case GenericDataTypes.ULONG:
                        {

                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Int(header.Label));
                            }
                            logs[header.Label].Add(i, (uint)datum);
                            break;
                        }
                    case GenericDataTypes.SHORT:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Int(header.Label));
                            }
                            logs[header.Label].Add(i, (short)datum);
                            break;
                        }
                    case GenericDataTypes.USHORT:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Int(header.Label));
                            }
                            logs[header.Label].Add(i, (ushort)datum);
                            break;
                        }
                    default:
                        {
                            System.Console.Error.WriteLine("Skipping {0} {1}", header.Label, header.DataType);
                            break;
                        }
                }
            }
        }

        var logArrays = logs.Values.Select(b => b.Build()).ToList();
        return logArrays;
    }

    public List<ArrowStatusLog> StatusLogs(IRawDataPlus accessor)
    {
        var nEntries = accessor.GetStatusLogEntriesCount();

        Dictionary<string, StatusLogBuilder> logs = new();

        for (var i = 0; i < nEntries; i++)
        {
            var logsFor = accessor.GetStatusLogEntry(i);

            foreach (var (datum, header) in logsFor.Values.Zip(accessor.GetStatusLogHeaderInformation()))
            {
                var dType = header.DataType;
                if (dType == GenericDataTypes.NULL)
                {
                    continue;
                }
                switch (dType)
                {
                    case GenericDataTypes.YESNO:
                    case GenericDataTypes.ONOFF:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Int32(header.Label));
                            }
                            logs[header.Label].Add(logsFor.Time, (bool)datum);
                            break;
                        }
                    case GenericDataTypes.Bool:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Int32(header.Label));
                            }

                            logs[header.Label].Add(logsFor.Time, (bool)datum);
                            break;
                        }
                    case GenericDataTypes.CHAR:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.String(header.Label));
                            }
                            logs[header.Label].Add(logsFor.Time, datum.ToString() ?? "");
                            break;
                        }
                    case GenericDataTypes.CHAR_STRING:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.String(header.Label));
                            }
                            logs[header.Label].Add(logsFor.Time, datum.ToString() ?? "");
                            break;
                        }
                    case GenericDataTypes.WCHAR_STRING:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.String(header.Label));
                            }
                            logs[header.Label].Add(logsFor.Time, (string)datum);
                            break;
                        }
                    case GenericDataTypes.FLOAT:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Double(header.Label));
                            }
                            logs[header.Label].Add(logsFor.Time, (double)(float)datum);
                            break;
                        }
                    case GenericDataTypes.DOUBLE:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Double(header.Label));
                            }
                            logs[header.Label].Add(logsFor.Time, (double)datum);
                            break;
                        }
                    case GenericDataTypes.Int:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Int(header.Label));
                            }
                            logs[header.Label].Add(logsFor.Time, (int)datum);
                            break;
                        }
                    case GenericDataTypes.ULONG:
                        {

                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Int(header.Label));
                            }
                            logs[header.Label].Add(logsFor.Time, (uint)datum);
                            break;
                        }
                    case GenericDataTypes.SHORT:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Int(header.Label));
                            }
                            logs[header.Label].Add(logsFor.Time, (short)datum);
                            break;
                        }
                    case GenericDataTypes.USHORT:
                        {
                            if (!logs.ContainsKey(header.Label))
                            {
                                logs.Add(header.Label, StatusLogBuilder.Int(header.Label));
                            }
                            logs[header.Label].Add(logsFor.Time, (ushort)datum);
                            break;
                        }
                    default:
                        {
                            System.Console.Error.WriteLine("Skipping {0} {1}", header.Label, header.DataType);
                            break;
                        }
                }
            }
        }
        var logArrays = logs.Values.Select(b => b.Build()).ToList();
        return logArrays;
    }

    public List<InstrumentConfiguration> GetInstrumentConfigurations(IRawDataPlus accessor)
    {
        List<InstrumentConfiguration> configs = new();
        var inst = accessor.GetInstrumentData();
        var modelTerm = Instruments.GetInstrumentModel(inst.Model);
        List<Param> instParams = [
            modelTerm,
            new Param("instrument serial number", "MS:1000529", inst.SerialNumber),
        ];

        // TODO: Generate actual instrument component lists here.

        var conf1 = new InstrumentConfiguration()
        {
            Id = 0,
            Components = [],
            Parameters = instParams,
            SoftwareReference = "mzpeak_thermo_rawfilereader",
        };

        configs.Add(conf1);
        return configs;
    }

    public DataProcessingMethod GetDataProcessingMethod(IRawDataPlus accessor)
    {
        new ProcessingMethod()
        {
            Order = 1,
            SoftwareReference = "mzpeak_thermo_rawfilereader",
            Parameters = [

            ]
        };

        var dpMethod = new DataProcessingMethod()
        {
            Id = "DP01",
            ProcessingMethods = []
        };

        return dpMethod;
    }

    public List<Software> GetSoftwares(IRawDataPlus accessor)
    {
        var lib = Assembly.GetCallingAssembly();
        var thisSoftware = new Software()
        {
            Id = "mzpeak_thermo_rawfilereader",
            Version = lib.GetName()?.Version?.ToString() ?? "0.0rc",
            Parameters = [
                new Param("custom unreleased software tool", "MS:1000799", "mzpeak_thermo_rawfilereader")
            ]
        };

        var inst = accessor.GetInstrumentData();

        var xcaliburSoftware = new Software()
        {
            Id = "XcaliburSW",
            Version = inst.SoftwareVersion,
            Parameters = [new Param("Xcalibur", "MS:1000532", null)]
        };

        return [xcaliburSoftware, thisSoftware];
    }

    public Sample GetSample(IRawDataPlus accessor)
    {
        var sinfo = accessor.SampleInformation;
        var sample = new Sample()
        {
            Id = sinfo.SampleId,
            Name = sinfo.SampleName,
            Parameters = []
        };

        sample.Parameters.AddRange([
            new Param("sample vial", sinfo.Vial),
            new Param("sample comment", sinfo.Comment),
            new Param("sample barcode", sinfo.Barcode),
            new Param("sample row number", sinfo.RowNumber),
            new Param("thermo:sample type", sinfo.SampleType.ToString()),
            new Param("thermo:dilution factor", sinfo.DilutionFactor),
            new Param("user text", string.Join("\n", sinfo.UserText.Select(v => v.ToString()))),
            new Param(
                    SampleProperties.SampleVolume.Name(),
                    SampleProperties.SampleVolume.CURIE(),
                    sinfo.SampleVolume,
                    Unit.Milliliter.CURIE()),
            new Param(
                    SampleProperties.SampleMass.Name(),
                    SampleProperties.SampleMass.CURIE(),
                    sinfo.SampleWeight,
                    Unit.Gram.CURIE()),
        ]);

        return sample;
    }
}

public class StatusLogBuilder
{
    public string Name;
    public IArrowArrayBuilder Data;
    public DoubleArray.Builder Time;
    public IArrowType DataType;

    public static StatusLogBuilder Int(string name)
    {
        return new(name, new Int64Array.Builder(), new Int64Type());
    }

    public static StatusLogBuilder Double(string name)
    {
        return new(name, new DoubleArray.Builder(), new DoubleType());
    }

    public static StatusLogBuilder Int32(string name)
    {
        return new(name, new Int32Array.Builder(), new Int32Type());
    }

    public static StatusLogBuilder String(string name)
    {
        return new(name, new StringArray.Builder(), new StringType());
    }

    public void Add(double time, double value)
    {
        if (DataType.TypeId != ArrowTypeId.Double)
            throw new InvalidCastException();
        ((DoubleArray.Builder)Data).Append(value);
        Time.Append(time);
    }

    public void Add(double time, long value)
    {
        if (DataType.TypeId != ArrowTypeId.Int64)
            throw new InvalidCastException();
        ((Int64Array.Builder)Data).Append(value);
        Time.Append(time);
    }

    public void Add(double time, bool value)
    {
        if (DataType.TypeId != ArrowTypeId.Int32)
            throw new InvalidCastException();
        ((Int32Array.Builder)Data).Append(value ? 1 : 0);
        Time.Append(time);
    }

    public void Add(double time, string value)
    {
        if (DataType.TypeId != ArrowTypeId.String)
            throw new InvalidCastException();
        ((StringArray.Builder)Data).Append(value);
        Time.Append(time);
    }

    protected StatusLogBuilder(string name, IArrowArrayBuilder data, IArrowType dataType)
    {
        Name = name;
        Data = data;
        Time = new DoubleArray.Builder();
        DataType = dataType;
    }

    public ArrowStatusLog Build()
    {
        return new ArrowStatusLog(Name.Trim(':'), Time.Build(), DataType.TypeId switch
        {
            ArrowTypeId.Int64 => ((Int64Array.Builder)Data).Build(),
            ArrowTypeId.Double => ((DoubleArray.Builder)Data).Build(),
            ArrowTypeId.Int32 => ((Int32Array.Builder)Data).Build(),
            ArrowTypeId.String => ((StringArray.Builder)Data).Build(),
            _ => throw new NotImplementedException(DataType.Name)
        }, DataType);
    }
}


public record ArrowStatusLog
{

    public string Name { get; set; }
    public DoubleArray Time { get; set; }
    public Apache.Arrow.Array Data { get; set; }
    public IArrowType DataType { get; set; }

    public ArrowStatusLog(string name, DoubleArray time, Apache.Arrow.Array data, IArrowType dataType)
    {
        Name = name;
        Time = time;
        Data = data;
        DataType = dataType;
    }

    public (ChromatogramInfo, Dictionary<ArrayIndexEntry, Apache.Arrow.Array>) AsChromatogramInfo()
    {
        var info = new ChromatogramInfo(0, Name, null, 0, [
            new Param(
                ChromatogramTypes.ChromatogramType.Name(),
                ChromatogramTypes.ChromatogramType.CURIE(), null)
        ]);

        var timeKey = new ArrayIndexEntry()
        {
            ArrayName = ArrayType.TimeArray.Name(),
            ArrayTypeCURIE = ArrayType.TimeArray.CURIE(),
            Context = BufferContext.Chromatogram,
            DataTypeCURIE = BinaryDataType.Float64.CURIE(),
            UnitCURIE = Unit.Minute.CURIE(),
            Path = "",
            SortingRank = 0,
            BufferPriority = BufferPriority.Primary,
            SchemaIndex = 1,
        };
        timeKey.Path = $"point.{timeKey.CreateColumnName()}";

        var dataKey = new ArrayIndexEntry()
        {
            ArrayName = Name,
            ArrayTypeCURIE = ArrayType.NonStandardDataArray.CURIE(),
            Context = BufferContext.Chromatogram,
            DataTypeCURIE = DataType.TypeId switch
            {
                ArrowTypeId.Double => BinaryDataType.Float64.CURIE(),
                ArrowTypeId.Int64 => BinaryDataType.Int64.CURIE(),
                ArrowTypeId.Int32 => BinaryDataType.Int32.CURIE(),
                ArrowTypeId.String => BinaryDataType.ASCII.CURIE(),
                _ => throw new NotImplementedException()
            },
            Path = ""
        };
        var unitPattern = new Regex(@"(.+)\s*?\((.+)\)");
        var match = unitPattern.Match(Name);
        if (match.Success)
        {
            var name = match.Groups[1];
            var unitName = match.Groups[2];
            switch (unitName.Value)
            {
                case "°C":
                case "C":
                    {
                        dataKey.UnitCURIE = Unit.DegreeCelsius.CURIE();
                        Name = info.Id = name.Value.Trim();
                        break;
                    }
                case "psi":
                    {
                        dataKey.UnitCURIE = Unit.Pascal.CURIE();
                        Name = info.Id = name.Value.Trim();
                        break;
                    }
                case "V":
                    {
                        dataKey.UnitCURIE = Unit.Volt.CURIE();
                        Name = info.Id = name.Value.Trim();
                        break;
                    }
                // case "MHz":
                //     {
                //         dataKey.UnitCURIE = Unit.Hertz.CURIE();
                //         Name = info.Id = name.Value.Trim();
                //         break;
                //     }
                case "Hz":
                    {
                        dataKey.UnitCURIE = Unit.Hertz.CURIE();
                        Name = info.Id = name.Value.Trim();
                        break;
                    }
                case "Watts":
                case "Ohm":
                case "Amps":
                    {
                        break;
                    }
                case "%":
                    {
                        dataKey.UnitCURIE = Unit.Percent.CURIE();
                        Name = info.Id = name.Value.Trim();
                        break;
                    }
                case "mm":
                    {
                        // dataKey.UnitCURIE = Unit.Micrometer.CURIE();
                        break;
                    }
                case "uL/min":
                    {
                        dataKey.UnitCURIE = Unit.MicrolitersPerMinute.CURIE();
                        Name = info.Id = name.Value.Trim();
                        break;
                    }
                default:
                    {
                        break;
                    }
            }
        }

        dataKey.ArrayName = Name.Trim().Replace(" ()", "");
        dataKey.Path = $"point.{dataKey.CreateColumnName()}";

        Dictionary<ArrayIndexEntry, Apache.Arrow.Array> arrays = new()
        {
            { timeKey, Time },
            { dataKey, Data }
        };
        return (info, arrays);
    }
}

class CustomizedMZPeakWriter : MZPeakWriter
{
    public CustomizedMZPeakWriter(IMZPeakArchiveWriter storage, ArrayIndex? spectrumArrayIndex = null, ArrayIndex? chromatogramArrayIndex = null,
                                  bool includeSpectrumPeakData = false, ArrayIndex? spectrumPeakArrayIndex = null, bool useChunked = false,
                                  Dictionary<string, FileEncryptionProperties>? encryptionConfigurations = null, ParquetDataWriterConfig? dataWriterConfig = null) :
                                  base(storage, spectrumArrayIndex, chromatogramArrayIndex, includeSpectrumPeakData, spectrumPeakArrayIndex, useChunked, encryptionConfigurations, dataWriterConfig)
    {
    }

    protected override WriterPropertiesBuilder SpectrumPeakDataWriterPropertiesBuilder()
    {
        var builder = base.SpectrumPeakDataWriterPropertiesBuilder();
        if (SpectrumPeakArrayIndex != null)
        {
            foreach(var e in SpectrumPeakArrayIndex.EntriesFor(ArrayType.ChargeArray))
            {
                builder = builder.EnableDictionary(e.Path);
            }
            foreach (var e in SpectrumPeakArrayIndex.EntriesFor(ArrayType.ResolutionArray))
            {
                builder = builder.EnableDictionary(e.Path);
            }
        }
        return builder;
    }
}

public class ThermoMZPeakWriter : IDisposable
{
    MZPeakWriter Writer;
    Dictionary<int, ulong> ScanNumberToIndex;
    public ConversionContextHelper ConversionHelper { get; protected set; }
    bool IncludeResolution;
    bool IncludeCharge;

    PointLayoutBuilder? NoiseBuilder;

    public ulong CurrentSpectrum => Writer.CurrentSpectrum;
    public ulong CurrentChromatogram => Writer.CurrentChromatogram;

    protected static ArrayIndex DefaultSpectrumArrayIndex(bool useChunked = false)
    {
        var builder = useChunked ? ArrayIndexBuilder.ChunkBuilder(BufferContext.Spectrum) : ArrayIndexBuilder.PointBuilder(BufferContext.Spectrum);
        builder.Add(ArrayType.MZArray, BinaryDataType.Float64, Unit.MZ, 1);
        builder.Add(ArrayType.IntensityArray, BinaryDataType.Float32, Unit.NumberOfDetectorCounts);
        return builder.Build();
    }

    public static ArrayIndex PeakArrayIndex(bool includeResolution = false, bool includeCharge = false)
    {
        var builder = ArrayIndexBuilder.PointBuilder(BufferContext.Spectrum);
        builder.Add(ArrayType.MZArray, BinaryDataType.Float64, Unit.MZ, 1);
        builder.Add(ArrayType.IntensityArray, BinaryDataType.Float32, Unit.NumberOfDetectorCounts);
        builder.Add(ArrayType.BaselineArray, BinaryDataType.Float32);
        builder.Add(ArrayType.NoiseArray, BinaryDataType.Float32);
        builder.Add(new ArrayIndexEntry()
        {
            ArrayName = "peak option flags array",
            ArrayTypeCURIE = ArrayType.NonStandardDataArray.CURIE(),
            Context = BufferContext.Spectrum,
            DataTypeCURIE = BinaryDataType.Int32.CURIE(),
            Path = "point.peak_option_flags",
            BufferFormat = BufferFormat.Point,
        });
        if (includeResolution)
            builder.Add(ArrayType.ResolutionArray, BinaryDataType.Int32);
        if (includeCharge)
            builder.Add(ArrayType.ChargeArray, BinaryDataType.Int32);

        return builder.Build();
    }

    public ArrayIndex SpectrumArrayIndex => Writer.SpectrumArrayIndex;
    public ArrayIndex ChromatogramArrayIndex => Writer.ChromatogramArrayIndex;
    public ArrayIndex? SpectrumPeakArrayIndex => Writer.SpectrumPeakArrayIndex;

    public ParquetDataWriterConfig DataWriterConfig { get => Writer.DataWriterConfig; set => Writer.DataWriterConfig = value; }

    public void SpectraUseNullMarking() => Writer.SpectraUseNullMarking();

    public ThermoMZPeakWriter(IMZPeakArchiveWriter storage,
                              ArrayIndex? spectrumArrayIndex = null,
                              ArrayIndex? chromatogramArrayIndex = null,
                              ArrayIndex? spectrumPeakArrayIndex = null,
                              bool includeNoise = false,
                              bool useChunked = false,
                              Dictionary<string, ParquetSharp.FileEncryptionProperties>? encryptionConfigurations = null,
                              ParquetDataWriterConfig? dataWriterConfig = null)
    {
        if (spectrumArrayIndex == null)
        {
            spectrumArrayIndex = DefaultSpectrumArrayIndex(useChunked);
        }
        Writer = new CustomizedMZPeakWriter(
            storage,
            spectrumArrayIndex,
            chromatogramArrayIndex,
            includeSpectrumPeakData: spectrumPeakArrayIndex != null,
            spectrumPeakArrayIndex: spectrumPeakArrayIndex,
            useChunked: useChunked,
            encryptionConfigurations,
            dataWriterConfig);
        ScanNumberToIndex = new();
        ConversionHelper = new();
        IncludeResolution = Writer.SpectrumPeaksHasArrayType(ArrayType.ResolutionArray);
        IncludeCharge = Writer.SpectrumPeaksHasArrayType(ArrayType.ChargeArray);
        if (includeNoise)
        {
            NoiseBuilder = new PointLayoutBuilder(ArrayIndexBuilder.PointBuilder(BufferContext.Spectrum)
                .Add(ArrayType.SampledNoiseMZArray, BinaryDataType.Float32, Unit.MZ)
                .Add(ArrayType.SampledNoiseBaselineArray, BinaryDataType.Float32)
                .Add(ArrayType.SampledNoiseIntensityArray, BinaryDataType.Float32, Unit.NumberOfDetectorCounts)
                .Build());
        }
    }

    public void InitializeHelper(IRawDataPlus accessor)
    {
        ConversionHelper.Initialize(accessor);

        Samples = [ConversionHelper.GetSample(accessor)];
        InstrumentConfigurations = ConversionHelper.GetInstrumentConfigurations(accessor);
        FileDescription = ConversionHelper.GetFileDescription(accessor);
        Softwares = ConversionHelper.GetSoftwares(accessor);
        DataProcessingMethods = [ConversionHelper.GetDataProcessingMethod(accessor)];

        Run.DefaultDataProcessingId = DataProcessingMethods.Last().Id;
        Run.DefaultSourceFileId = FileDescription.SourceFiles[0].Id;
        Run.DefaultInstrumentId = (int)InstrumentConfigurations[0].Id;
        Run.StartTime = accessor.CreationDate;
    }

    public EntryDerivedMetadata AddSpectrumData(ulong entryIndex, SegmentedScan segments, ScanStatistics stats)
    {
        var isProfile = !stats.IsCentroidScan;
        var mzArray = Compute.Compute.CastDouble(segments.Positions);
        var intensityArray = Compute.Compute.CastFloat(segments.Intensities);
        return Writer.AddSpectrumData(entryIndex, [mzArray, intensityArray], isProfile: isProfile);
    }

    public EntryDerivedMetadata AddSpectrumPeakData(ulong entryIndex, CentroidStream centroids)
    {
        if (centroids.Length == 0) return EntryDerivedMetadata.Empty;
        var mzArray = Compute.Compute.CastDouble(centroids.Masses);
        var intensityArray = Compute.Compute.CastFloat(centroids.Intensities);
        var baselineArray = Compute.Compute.CastFloat(centroids.Baselines);
        var noiseArray = Compute.Compute.CastFloat(centroids.Noises);
        var peakOptions = Compute.Compute.CastInt32(centroids.Flags.Select(f => (byte)f).ToList());
        List<Apache.Arrow.Array> arrays = [mzArray, intensityArray, baselineArray, noiseArray, peakOptions];
        if (IncludeResolution)
        {
            arrays.Add(Compute.Compute.CastInt32(centroids.Resolutions));
        }
        if (IncludeCharge)
        {
            var builder = new Int32Array.Builder();
            builder.Reserve(centroids.Charges.Length);
            foreach (var val in centroids.Charges)
            {
                if (val == 0) builder.AppendNull();
                else builder.Append(int.CreateChecked(val));
            }
            arrays.Add(builder.Build());
        }
        var r = Writer.AddSpectrumPeakData(entryIndex, arrays);
        return r;
    }

    public EntryDerivedMetadata AddSpectrumPeakData(ulong entryIndex, ISimpleScanAccess centroids)
    {
        if (centroids.Masses.Length == 0) return EntryDerivedMetadata.Empty;
        var n = centroids.Masses.Length;
        var mzArray = Compute.Compute.CastDouble(centroids.Masses);
        var intensityArray = Compute.Compute.CastFloat(centroids.Intensities);
        var nullArrayMaker = new NullArray.Builder();
        nullArrayMaker.Resize(n);

        List<IArrowArray> arrays = [mzArray, intensityArray];
        var r = Writer.AddSpectrumPeakData(entryIndex, arrays);
        return r;
    }

    public void AddNoisePacketData(ulong entryIndex, NoiseAndBaseline[] noiseAndBaselines)
    {
        if (NoiseBuilder == null) return;

        var mzs = new FloatArray.Builder();
        var bases = new FloatArray.Builder();
        var intens = new FloatArray.Builder();

        foreach (var pt in noiseAndBaselines)
        {
            mzs.Append(pt.Mass);
            bases.Append(pt.Baseline);
            intens.Append(pt.Noise);
        }

        NoiseBuilder.Add(entryIndex, [mzs.Build(), bases.Build(), intens.Build()], false);
    }

    Param GetMSLevel(IScanFilter scanFilter)
    {
        var msLevel = ConversionContextHelper.MSLevelMap[scanFilter.MSOrder];
        return new Param(SpectrumProperties.MsLevel.Name(), SpectrumProperties.MsLevel.CURIE(), msLevel);
    }

    Param GetPolarity(IScanFilter scanFilter)
    {
        switch (scanFilter.Polarity)
        {
            case PolarityType.Negative:
                {
                    return new Param(ScanPolarity.ScanPolarity.Name(), ScanPolarity.ScanPolarity.CURIE(), -1);
                }
            case PolarityType.Positive:
                {
                    return new Param(ScanPolarity.ScanPolarity.Name(), ScanPolarity.ScanPolarity.CURIE(), 1);
                }
            case PolarityType.Any:
                {
                    return new Param(ScanPolarity.ScanPolarity.Name(), ScanPolarity.ScanPolarity.CURIE(), 0);
                }
            default: throw new InvalidOperationException();
        }
    }

    public ulong AddSpectrum(
        int scanNumber,
        double time,
        IScanFilter scanFilter,
        ScanStatistics scanStatistics,
        EntryDerivedMetadata entryDerivedMetadata,
        List<Param>? @params = null
    )
    {
        List<Param> paramList = @params ?? new();
        paramList.Add(GetMSLevel(scanFilter));
        paramList.Add(GetPolarity(scanFilter));
        paramList.Add(new Param(
            SpectrumProperties.BasePeakIntensity.Name(),
            SpectrumProperties.BasePeakIntensity.CURIE(),
            scanStatistics.BasePeakIntensity,
            Unit.NumberOfDetectorCounts.CURIE()
        ));
        paramList.Add(new Param(
            SpectrumProperties.BasePeakMZ.Name(),
            SpectrumProperties.BasePeakMZ.CURIE(),
            scanStatistics.BasePeakMass,
            Unit.MZ.CURIE()
        ));
        paramList.Add(new Param(
            SpectrumProperties.TotalIonCurrent.Name(),
            SpectrumProperties.TotalIonCurrent.CURIE(),
            scanStatistics.TIC,
            Unit.NumberOfDetectorCounts.CURIE()
        ));
        if (scanStatistics.IsCentroidScan)
            paramList.Add(SpectrumRepresentation.CentroidSpectrum.AsParam());
        else
            paramList.Add(SpectrumRepresentation.ProfileSpectrum.AsParam());

        // Required CV term "spectrum type" (MS:1000559): the cv_mapping rule has use_term=false +
        // allow_children, i.e. the spectrum needs a param whose *accession* is a concrete child of
        // MS:1000559. All Thermo scans are mass spectra → emit MS:1000294 as the accession (lands in
        // the generic parameters[] list since no inflected column claims it).
        paramList.Add(new Param("mass spectrum", "MS:1000294", null));

        var id = $"controllerType=0 controllerNumber=1 scan={scanNumber}";
        var index = Writer.AddSpectrum(id, time, null, paramList, entryDerivedMetadata);
        ScanNumberToIndex[scanNumber] = index;
        return index;
    }

    public void AddScan(
        ulong sourceIndex,
        int scanNumber,
        double time,
        IScanFilter scanFilter,
        ScanStatistics scanStatistics,
        AcquisitionProperties acquisitionProperties,
        List<Param>? @params = null
    )
    {
        var instrumentRef = 0u;
        List<Param> paramList = @params ?? new();
        double? faims = null;
        string? imCV = null;

        paramList.AddRange([
            new(ScanAttribute.ScanStartTime.Name(),
                ScanAttribute.ScanStartTime.CURIE(),
                time,
                Unit.Minute.CURIE()),
            new(ScanAttribute.FilterString.Name(), ScanAttribute.FilterString.CURIE(), scanFilter.ToString()),
        ]);
        if (scanFilter.CompensationVoltageCount == 1)
        {
            faims = scanFilter.CompensationVoltageValue(0);
            imCV = ScanAttribute.FaimsCompensationVoltage.CURIE();
        }
        else if (scanFilter.CompensationVoltageCount > 1)
            throw new NotImplementedException("Multiple FAIMS CV not yet supported");

        List<ScanWindow> scanWindows = [new ScanWindow(acquisitionProperties.LowMZ, acquisitionProperties.HighMZ, Unit.MZ)];

        paramList.Add(new Param(
            ScanAttribute.FilterString.Name(),
            ScanAttribute.FilterString.CURIE(),
            scanFilter.ToString())
        );

        if (acquisitionProperties.Resolution != null)
            paramList.Add(new Param(
                ScanAttribute.MassResolution.Name(),
                ScanAttribute.MassResolution.CURIE(),
                acquisitionProperties.Resolution));
        paramList.Add(new Param(
            ScanAttribute.IonInjectionTime.Name(),
            ScanAttribute.IonInjectionTime.CURIE(),
            acquisitionProperties.InjectionTime,
            Unit.Millisecond.CURIE()
        ));

        Writer.AddScan(
            sourceIndex,
            instrumentRef,
            paramList,
            ionMobility: faims,
            ionMobilityType: imCV,
            scanWindows: scanWindows.Select(w => w.AsParamList()).ToList()
        );
    }

    public (PrecursorProperties?, AcquisitionProperties) ExtractPrecursorAndTrailerMetadata(int scanNumber, IRawDataPlus accessor, IScanFilter filter, ScanStatistics stats)
    {
        var msLevel = ConversionContextHelper.MSLevelMap[filter.MSOrder];
        return ConversionHelper.ExtractPrecursorAndTrailerMetadata(scanNumber, msLevel, filter, accessor, stats);
    }

    public void AddPrecursor(
        ulong sourceIndex,
        PrecursorProperties precursorProperties,
        List<Param>? @activationParams = null
    )
    {

        ulong precursorIndex;
        string? precursorId = null;

        if (ScanNumberToIndex.TryGetValue(precursorProperties.MasterScanNumber, out precursorIndex))
        {
            precursorId = $"controllerType=0 controllerNumber=1 scan={precursorProperties.MasterScanNumber}";
        }
        var activationParamList = @activationParams ?? new();
        activationParamList.AddRange(precursorProperties.Activation.AsParamList());
        Writer.AddPrecursor(
            sourceIndex,
            precursorIndex,
            precursorId,
            precursorProperties.IsolationWindow.ToParamList(),
            activationParamList
        );
    }

    public void AddSelectedIon(
        ulong sourceIndex,
        PrecursorProperties precursorProperties
    )
    {
        ulong precursorIndex;
        if (!ScanNumberToIndex.TryGetValue(precursorProperties.MasterScanNumber, out precursorIndex)) { }

        List<Param> paramList = new()
        {
            new Param(
                IonSelectionProperties.ChargeState.Name(),
                IonSelectionProperties.ChargeState.CURIE(),
                rawValue: precursorProperties.PrecursorCharge
            ),
            new Param(
                IonSelectionProperties.SelectedIonMZ.Name(),
                IonSelectionProperties.SelectedIonMZ.CURIE(),
                precursorProperties.MonoisotopicMZ,
                Unit.MZ.CURIE()
            ),
        };
        Writer.AddSelectedIon(
            sourceIndex,
            precursorIndex,
            paramList
        );
    }

    public EntryDerivedMetadata AddChromatogramData(ulong entryIndex, Dictionary<ArrayIndexEntry, Apache.Arrow.Array> arrays) => Writer.AddChromatogramData(entryIndex, arrays);
    public EntryDerivedMetadata AddChromatogramData(ulong entryIndex, IEnumerable<IArrowArray> arrays) => Writer.AddChromatogramData(entryIndex, arrays);
    public EntryDerivedMetadata AddChromatogramData(ulong entryIndex, IEnumerable<Apache.Arrow.Array> arrays) => Writer.AddChromatogramData(entryIndex, arrays);

    public ulong AddChromatogram(
        string id,
        string? dataProcessingRef,
        List<Param>? chromatogramParams = null,
        EntryDerivedMetadata? entryDerivedMetadata = null
    )
    {
        return Writer.AddChromatogram(id, dataProcessingRef, chromatogramParams, entryDerivedMetadata);
    }

    public EntryDerivedMetadata AddWavelengthSpectrumData(ulong entryIndex, Dictionary<ArrayIndexEntry, Apache.Arrow.Array> arrays) => Writer.AddWavelengthSpectrumData(entryIndex, arrays);
    public EntryDerivedMetadata AddWavelengthSpectrumData(ulong entryIndex, IEnumerable<IArrowArray> arrays) => Writer.AddWavelengthSpectrumData(entryIndex, arrays);
    public EntryDerivedMetadata AddWavelengthSpectrumData(ulong entryIndex, IEnumerable<Apache.Arrow.Array> arrays) => Writer.AddWavelengthSpectrumData(entryIndex, arrays);

    /// <summary>Adds a wavelength spectrum entry with metadata.</summary>
    /// <param name="id">The spectrum native ID.</param>
    /// <param name="time">The retention time.</param>
    /// <param name="dataProcessingRef">Optional data processing reference.</param>
    /// <param name="spectrumParams">Optional spectrum parameters.</param>
    /// <param name="auxiliaryArrays">Optional auxiliary arrays.</param>
    public ulong AddWavelengthSpectrum(
        string id,
        double time,
        string? dataProcessingRef,
        List<Param>? spectrumParams = null,
        EntryDerivedMetadata? entryDerivedMetadata = null
    )
    {
        return Writer.AddWavelengthSpectrum(
            id,
            time,
            dataProcessingRef,
            spectrumParams,
            entryDerivedMetadata
        );
    }

    /// <summary>Adds a scan entry to a wavelength spectrum.</summary>
    /// <param name="sourceIndex">The parent spectrum index.</param>
    /// <param name="instrumentConfigurationRef">Optional instrument configuration reference.</param>
    /// <param name="scanParams">Scan parameters.</param>
    /// <param name="ionMobility">Optional ion mobility value.</param>
    /// <param name="ionMobilityType">Optional ion mobility type CURIE.</param>
    /// <param name="scanWindows">Optional scan windows parameters.</param>
    public void AddWavelengthScan(
        ulong sourceIndex,
        uint? instrumentConfigurationRef,
        List<Param> scanParams,
        double? ionMobility = null,
        string? ionMobilityType = null,
        List<List<Param>>? scanWindows = null
    )
    {
        Writer.AddWavelengthScan(
            sourceIndex,
            instrumentConfigurationRef,
            scanParams,
            ionMobility,
            ionMobilityType,
            scanWindows
        );
    }

    public void CloseCurrentWriter() => Writer.CloseCurrentWriter();
    public void FlushSpectrumData() => Writer.FlushSpectrumData();
    public void FlushSpectrumPeakData() => Writer.FlushSpectrumPeakData();

    /// <summary>Opens a raw stream for a proprietary (non-CV) ZIP entry. Flushes standard content first.</summary>
    public Stream StartProprietaryEntry(FileIndexEntry entry)
    {
        if (Writer.State < WriterState.OtherData)
        {
            FlushStandardContent();
            Writer.State = WriterState.OtherData;
        }
        return Writer.StartEntry(entry);
    }

    /// <summary>Opens a Parquet stream for a proprietary (non-CV) ZIP entry. Flushes standard content first.</summary>
    public ParquetSharp.IO.ManagedOutputStream StartProprietaryParquetEntry(FileIndexEntry entry)
    {
        if (Writer.State < WriterState.OtherData)
        {
            FlushStandardContent();
            Writer.State = WriterState.OtherData;
        }
        return Writer.StartParquetEntry(entry);
    }

    /// <summary>Starts writing spectrum peak data.</summary>
    public void StartSpectrumPeakData(bool useTmp=false) => Writer.StartSpectrumPeakData(useTmp);

    /// <summary>Writes spectrum metadata to the archive.</summary>
    public void WriteSpectrumMetadata() => Writer.WriteSpectrumMetadata();

    public void WriteChromatogramData() => Writer.WriteChromatogramData();
    public void WriteChromatogramMetadata() => Writer.WriteChromatogramMetadata();

    /// <summary>Closes the writer and finalizes the archive.</summary>
    public void Close()
    {
        FlushStandardContent();
        WriteNoiseData();
        Writer.Close();
    }

    public void FlushStandardContent() => Writer.FlushStandardContent();

    public void WriteNoiseData()
    {
        if (NoiseBuilder == null) return;
        if (Writer.State < WriterState.OtherData)
        {
            FlushStandardContent();
            Writer.State = WriterState.OtherData;
        }

        var noiseEntry = new FileIndexEntry("spectrum_noise_data.parquet", EntityType.Spectrum, DataKind.Proprietary);
        var managedStream = Writer.StartParquetEntry(noiseEntry);
        var writerProps = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(ParquetSharp.Compression.Zstd)
            .EnableDictionary()
            .EnableStatistics()
            .EnableWritePageIndex()
            .DisableDictionary($"{NoiseBuilder.LayoutName()}.{NoiseBuilder.BufferContext.IndexName()}")
            .Encoding(
                $"{NoiseBuilder.LayoutName()}.{NoiseBuilder.BufferContext.IndexName()}",
                ParquetSharp.Encoding.DeltaBinaryPacked
            ).DataPagesize(
                DataWriterConfig.PageSize
            ).MaxRowGroupLength(
                DataWriterConfig.RowGroupSize
            );

        var schema = NoiseBuilder.ArrowSchema();

        var arrowProps = new ParquetSharp.Arrow.ArrowWriterPropertiesBuilder().StoreSchema();

        var writer = new ParquetSharp.Arrow.FileWriter(managedStream, schema, writerProps.Build(), arrowProps.Build());
        var batch = NoiseBuilder.GetRecordBatch();
        writer.WriteRecordBatch(batch);
        writer.Close();
    }

    public void Dispose() => Close();

    /// <summary>Gets or sets the file description metadata.</summary>
    public FileDescription FileDescription { get => Writer.FileDescription; set => Writer.FileDescription = value; }
    /// <summary>Gets or sets the list of instrument configurations.</summary>
    public List<InstrumentConfiguration> InstrumentConfigurations { get => Writer.InstrumentConfigurations; set => Writer.InstrumentConfigurations = value; }
    /// <summary>Gets or sets the list of software used.</summary>
    public List<Software> Softwares { get => Writer.Softwares; set => Writer.Softwares = value; }
    /// <summary>Gets or sets the list of samples.</summary>
    public List<Sample> Samples { get => Writer.Samples; set => Writer.Samples = value; }
    /// <summary>Gets or sets the list of data processing methods.</summary>
    public List<DataProcessingMethod> DataProcessingMethods { get => Writer.DataProcessingMethods; set => Writer.DataProcessingMethods = value; }
    /// <summary>Gets or sets the run-level metadata.</summary>
    public MSRun Run { get => Writer.Run; set => Writer.Run = value; }

}
