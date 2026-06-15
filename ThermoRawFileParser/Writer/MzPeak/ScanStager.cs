using System;
using System.Collections.Generic;
using System.Linq;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoRawFileParser.Util;

namespace ThermoRawFileParser.Writer
{
    public partial class MzPeakSpectrumWriter
    {
        // Everything staged from a single scan, held in locals until the scan fully succeeds. A read/build
        // failure discards the whole struct: no ordinal, no rows, no precursor-map entry are committed.
        private sealed class StagedScan
        {
            public double[] Mz, PeakMz;
            public float[] Inten, PeakInten;
            public MzPeakRecord Rec;
            public string FilterKey;
        }

        // Reads and builds everything for one scan into per-scan locals, touching NO shared/streaming
        // state. Returns null for an out-of-range or empty scan (a clean skip); returns the staged scan on
        // success. Any read/build failure throws and is handled by the caller as an error-counted skip, so
        // a failed scan never commits an ordinal, rows, or a precursor-map entry.
        private StagedScan StageScan(IRawDataPlus raw, int scanNumber, ulong ordinal,
            IDictionary<int, ulong> scanNumberToOrdinal)
        {
            var scanFilter = raw.GetFilterForScanNumber(scanNumber);
            int level = (int)scanFilter.MSOrder;
            if (level > ParseInput.MaxLevel || !ParseInput.MsLevel.Contains(level)) return null;

            var scanEvent = raw.GetScanEventForScanNumber(scanNumber);
            var mzData = ReadMZData(raw, scanEvent, scanNumber, false, false, false);

            var (mz, inten) = OrderedPairs(mzData.masses, mzData.intensities);

            double[] peakMz = null;
            float[] peakInten = null;
            ulong? peakCount = null;
            if (scanEvent.ScanData == ScanDataType.Profile && Scan.FromFile(raw, scanNumber).HasCentroidStream)
            {
                var peakData = ReadMZData(raw, scanEvent, scanNumber, true, false, false);
                var (pMz, pInten) = OrderedPairs(peakData.masses, peakData.intensities);
                if (pMz.Length > 0)
                {
                    peakMz = pMz;
                    peakInten = pInten;
                    peakCount = (ulong)pMz.Length;
                }
            }

            var scanStats = raw.GetScanStatsForScanNumber(scanNumber);
            var trailer = new ScanTrailer(raw.GetTrailerExtraInformation(scanNumber));

            // Mirror the mzML parent-derivation bookkeeping: map each scan's filter prefix to its scan
            // number so an MSn child can resolve its parent via the scan string.
            var filterMatch = level == 1
                ? null
                : (level == 2
                    ? _filterStringIsolationMzPattern.Match(scanEvent.ToString())
                    : _filterStringParentMzPattern.Match(scanEvent.ToString()));
            var filterKey = level == 1 ? "" : (filterMatch != null && filterMatch.Success ? filterMatch.Groups[1].Value : "");

            // The filter key is staged but nothing has committed yet. A failure raised here is
            // indistinguishable from a real post-key read failure and must leave no trace of this scan.
            AfterFilterKeyStaged?.Invoke(scanNumber);

            var rec = new MzPeakRecord
            {
                Ordinal = ordinal,
                ScanNumber = scanNumber,
                MsLevel = level,
                Id = ConstructSpectrumTitle((int)Device.MS, 1, scanNumber),
                Time = raw.RetentionTimeFromScanNumber(scanNumber),
                Polarity = scanFilter.Polarity == PolarityType.Negative
                    ? (sbyte?)-1
                    : (scanFilter.Polarity == PolarityType.Positive ? (sbyte?)1 : null),
                Representation = mzData.isCentroided ? MzPeakCv.CentroidSpectrum : MzPeakCv.ProfileSpectrum,
                SpectrumType = level == 1 ? MzPeakCv.Ms1Spectrum : MzPeakCv.MsnSpectrum,
                LowestMz = mz.Length > 0 ? (double?)mz[0] : null,
                HighestMz = mz.Length > 0 ? (double?)mz[mz.Length - 1] : null,
                BasePeakMz = mzData.basePeakMass,
                BasePeakIntensity = mzData.basePeakIntensity.HasValue ? (float?)mzData.basePeakIntensity.Value : null,
                TotalIonCurrent = (float)scanStats.TIC,
                DataPointCount = (ulong)mz.Length,
                PeakCount = peakCount,
                ScanStartTime = (float)raw.RetentionTimeFromScanNumber(scanNumber),
                PresetScanConfiguration = null,
                FilterString = scanEvent.ToString(),
                IonInjectionTime = trailer.AsDouble("Ion Injection Time (ms):").HasValue
                    ? (float?)(float)trailer.AsDouble("Ion Injection Time (ms):").Value : null,
                InstrumentConfigRef = AnalyzerIndex(scanFilter.MassAnalyzer, scanFilter.IonizationMode),
                WindowLower = (float)scanStats.LowMass,
                WindowUpper = (float)scanStats.HighMass
            };

            if (level >= 2)
            {
                BeforeBuildPrecursor?.Invoke(scanNumber, scanNumberToOrdinal);
                BuildPrecursor(raw, scanNumber, scanEvent, scanFilter, trailer, scanNumberToOrdinal,
                    filterKey, rec);
            }

            return new StagedScan
            {
                Mz = mz,
                Inten = inten,
                PeakMz = peakMz,
                PeakInten = peakInten,
                Rec = rec,
                FilterKey = filterKey
            };
        }

        // Returns the (mz,intensity) pairs in non-decreasing m/z order with the full multiset
        // preserved. The Thermo SegmentedScan/CentroidStream are already ascending; a paired
        // index sort is applied only when an ascending violation is detected, never dropping or
        // merging equal-m/z points.
        public static (double[] mz, float[] intensity) OrderedPairs(double[] masses, double[] intensities)
        {
            var n = masses?.Length ?? 0;
            var mz = new double[n];
            var inten = new float[n];
            bool ascending = true;
            for (int i = 0; i < n; i++)
            {
                mz[i] = masses[i];
                inten[i] = (float)intensities[i];
                if (i > 0 && masses[i] < masses[i - 1]) ascending = false;
            }

            if (ascending) return (mz, inten);

            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int c = masses[a].CompareTo(masses[b]);
                return c != 0 ? c : a.CompareTo(b);
            });
            var sortedMz = new double[n];
            var sortedInten = new float[n];
            for (int i = 0; i < n; i++)
            {
                sortedMz[i] = masses[order[i]];
                sortedInten[i] = (float)intensities[order[i]];
            }
            return (sortedMz, sortedInten);
        }

        private void CaptureTic(IRawDataPlus raw, List<MzPeakRecord> records, List<double> time,
            List<float> intensity, List<long> msLevel)
        {
            ChromatogramSignal[] trace;
            if (TestForceSummedTic)
            {
                trace = Array.Empty<ChromatogramSignal>();
            }
            else
            {
                try
                {
                    raw.SelectInstrument(Device.MS, 1);
                    var settings = new ChromatogramTraceSettings(TraceType.TIC);
                    var data = raw.GetChromatogramData(new IChromatogramSettings[] { settings }, -1, -1);
                    trace = ChromatogramSignal.FromChromatogramData(data);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Device TIC trace unavailable, falling back to summed TIC: {ex.Message}");
                    trace = Array.Empty<ChromatogramSignal>();
                }
            }

            if (trace.Length == 0)
            {
                foreach (var r in records)
                {
                    time.Add(r.Time);
                    intensity.Add(r.TotalIonCurrent);
                    msLevel.Add(r.MsLevel);
                }
                _chromFromDeviceTrace = false;
                return;
            }

            var signal = trace[0];
            if (signal.Times.Count == records.Count)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    time.Add(signal.Times[i]);
                    intensity.Add((float)signal.Intensities[i]);
                    msLevel.Add(records[i].MsLevel);
                }
                _chromFromDeviceTrace = true;
                return;
            }

            var byScan = new Dictionary<int, (double time, double intensity)>();
            for (int k = 0; k < signal.Scans.Count; k++)
            {
                byScan[signal.Scans[k]] = (signal.Times[k], signal.Intensities[k]);
            }

            bool allDevice = true;
            foreach (var r in records)
            {
                if (byScan.TryGetValue(r.ScanNumber, out var sample))
                {
                    time.Add(sample.time);
                    intensity.Add((float)sample.intensity);
                }
                else
                {
                    time.Add(r.Time);
                    intensity.Add(r.TotalIonCurrent);
                    allDevice = false;
                }
                msLevel.Add(r.MsLevel);
            }
            _chromFromDeviceTrace = allDevice;
        }

        private uint AnalyzerIndex(MassAnalyzerType analyzer, IonizationModeType ionization)
        {
            int idx = _analyzerOrder.IndexOf(analyzer);
            if (idx < 0)
            {
                _analyzerOrder.Add(analyzer);
                _ionizationOrder.Add(ionization);
                idx = _analyzerOrder.Count - 1;
            }
            return (uint)idx;
        }

        private void BuildPrecursor(IRawDataPlus raw, int scanNumber, IScanEvent scanEvent,
            IScanFilter scanFilter, ScanTrailer trailer, IDictionary<int, ulong> scanNumberToOrdinal,
            string filterKey, MzPeakRecord rec)
        {
            rec.IsMsn = true;

            // Parent scan via Master Scan Number trailer, falling back to the scan-string parent the
            // mzML path derives. Map the parent scan number to its emitted ordinal; if the parent was
            // filtered out / not emitted, keep the precursor entry but leave precursor_index null.
            int parentScan;
            var master = trailer.AsPositiveInt("Master Scan Number:");
            if (master.HasValue) parentScan = master.Value;
            else parentScan = GetParentFromScanString(filterKey);

            if (parentScan > 0 && scanNumberToOrdinal.TryGetValue(parentScan, out var p))
            {
                rec.PrecursorIndex = p;
                rec.PrecursorId = ConstructSpectrumTitle((int)Device.MS, 1, parentScan);
            }

            var reaction = GetReaction(scanEvent, scanNumber);
            if (reaction == null) return;
            rec.HasReaction = true;

            double? isolationWidth = reaction.IsolationWidth;
            if (isolationWidth < 0) isolationWidth = null;

            rec.IsolationTarget = (float)reaction.PrecursorMass;
            if (isolationWidth != null)
            {
                var offset = isolationWidth.Value / 2 + reaction.IsolationWidthOffset;
                rec.IsolationLowerOffset = (float)(isolationWidth.Value - offset);
                rec.IsolationUpperOffset = (float)offset;
            }

            if (OntologyMapping.DissociationTypes.TryGetValue(reaction.ActivationType, out var diss))
            {
                rec.ActivationParams.Add(new MzPeakParam { Accession = diss.accession, Name = diss.name });
            }

            if (reaction.CollisionEnergyValid)
            {
                rec.ActivationParams.Add(new MzPeakParam
                {
                    Accession = MzPeakCv.CollisionEnergy,
                    Name = "collision energy",
                    Float = reaction.CollisionEnergy,
                    Unit = MzPeakCv.ElectronvoltUnit
                });
            }

            int? charge = trailer.AsPositiveInt("Charge State:");
            if (TestChargeOverride != null) charge = TestChargeOverride(scanNumber);
            double? monoisotopicMz = trailer.AsDouble("Monoisotopic M/Z:");
            rec.SelectedIonMz = CalculateSelectedIonMz(reaction, monoisotopicMz, isolationWidth);
            rec.ChargeState = charge;

            // Selected-ion intensity is read FROM the parent spectrum, so it is only meaningful when
            // the parent was actually emitted (a committed ordinal). A parent that was skipped/filtered
            // out or whose read fails must never force this otherwise-readable child to fail: gate on
            // the parent ordinal being known, and treat any parent-read error as "intensity unknown".
            if (rec.SelectedIonMz > ZeroDelta && rec.PrecursorIndex.HasValue)
            {
                try
                {
                    rec.SelectedIonIntensity = (float)CalculatePrecursorPeakIntensity(raw, parentScan,
                        reaction.PrecursorMass, isolationWidth,
                        ParseInput.NoPeakPicking.Contains(rec.MsLevel - 1));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Scan #{scanNumber}: parent #{parentScan} intensity unreadable, " +
                             $"selected-ion intensity left null: {ex.Message}");
                }
            }
        }
    }
}
