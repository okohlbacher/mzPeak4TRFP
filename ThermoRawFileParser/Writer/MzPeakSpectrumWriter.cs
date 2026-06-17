using log4net;
using MZPeak.Thermo;
using MZPeak.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;

namespace ThermoRawFileParser.Writer
{
    public partial class MzPeakSpectrumWriter : SpectrumWriter
    {
        private static readonly ILog Log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public MzPeakSpectrumWriter(ParseInput parseInput) : base(parseInput)
        {
        }

        public override void Write(IRawDataPlus raw, int firstScanNumber, int lastScanNumber)
        {
            if (!raw.HasMsData)
                throw new RawFileParserException("No MS data in RAW file, no output will be produced");

            var vendorOn   = ParseInput.MzPeakVendorMetadata;
            var vendorMode = ParseInput.MzPeakVendorMetadataMode ?? "tall";
            var vendorTall = vendorOn && vendorMode != "wide";
            var vendorWide = vendorOn && vendorMode != "tall";

            var scanNumberToOrdinal = new Dictionary<int, ulong>();
            VendorTrailerFacetStream vendorTrailers = null;
            List<VendorWideTrailerFacet.Column> wideCols = null;

            try
            {
                if (vendorTall) vendorTrailers = new VendorTrailerFacetStream();
                if (vendorOn)   wideCols = VendorWideTrailerFacet.Classify(raw);

                ConfigureWriter(".mzpeak");
                var storage = new ZipStreamArchiveWriter<Stream>(Writer.BaseStream);

                ThermoMZPeakWriter thermoWriter = null;
                try
                {
                    thermoWriter = new ThermoMZPeakWriter(
                        storage,
                        useChunked: !ParseInput.MzPeakPointLayout,
                        spectrumPeakArrayIndex: ThermoMZPeakWriter.PeakArrayIndex());
                    thermoWriter.InitializeHelper(raw);

                    ulong ordinal = 0;

                    for (var scanNumber = firstScanNumber; scanNumber <= lastScanNumber; scanNumber++)
                    {
                        var filter = raw.GetFilterForScanNumber(scanNumber);
                        int level  = ConversionContextHelper.MSLevelMap[filter.MSOrder];
                        if (level > ParseInput.MaxLevel || !ParseInput.MsLevel.Contains(level))
                            continue;

                        // Read phase — gather all data before committing anything.
                        ScanStatistics stats;
                        SegmentedScan  segments;
                        CentroidStream centroids;
                        (PrecursorProperties? precursor, AcquisitionProperties acq) meta;
                        try
                        {
                            stats    = raw.GetScanStatsForScanNumber(scanNumber);
                            segments = raw.GetSegmentedScanFromScanNumber(scanNumber, stats);
                            centroids = !stats.IsCentroidScan
                                ? raw.GetCentroidStream(scanNumber, false)
                                : null;
                            meta = thermoWriter.ExtractPrecursorAndTrailerMetadata(
                                scanNumber, raw, filter, stats);
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Scan #{scanNumber} cannot be processed: {ex.Message}");
                            Log.Debug(ex.StackTrace);
                            ParseInput.NewError();
                            continue;
                        }

                        // Commit phase — write failures are fatal (file may be partially written).
                        var dataMeta = thermoWriter.AddSpectrumData(ordinal, segments, stats);
                        if (centroids != null && centroids.Length > 0)
                        {
                            // Merge the peak facet's derived metadata into the spectrum row, mirroring
                            // the canonical mzPeak.NET converter: the real centroid peak count and any
                            // peak-derived auxiliary arrays must reach spectra_metadata, otherwise
                            // number_of_peaks wrongly mirrors number_of_data_points.
                            var peakMeta = thermoWriter.AddSpectrumPeakData(ordinal, centroids);
                            dataMeta.AuxiliaryArrays.AddRange(peakMeta.AuxiliaryArrays);
                            dataMeta = dataMeta with { PeakCount = peakMeta.PeakCount };
                        }
                        else
                        {
                            // No separate peak facet for this spectrum (profile points live in
                            // spectra_data). number_of_peaks must not be reported, else the validator
                            // checks it against spectra_peaks row count and fails. Works around the
                            // mzPeak.NET chunk-layout writer deriving PeakCount from the profile point
                            // count (DataWriter chunk path) instead of leaving it null.
                            dataMeta = dataMeta with { PeakCount = null };
                        }

                        var spectrumIndex = thermoWriter.AddSpectrum(
                            scanNumber, stats.StartTime, filter, stats, dataMeta);
                        thermoWriter.AddScan(
                            spectrumIndex, scanNumber, stats.StartTime, filter, stats, meta.acq);
                        if (meta.precursor != null)
                        {
                            thermoWriter.AddPrecursor(spectrumIndex, meta.precursor);
                            thermoWriter.AddSelectedIon(spectrumIndex, meta.precursor);
                        }

                        scanNumberToOrdinal[scanNumber] = ordinal;
                        if (vendorTall) vendorTrailers!.Append(ordinal, scanNumber, raw);
                        ordinal++;
                    }

                    if (ordinal == 0)
                        throw new RawFileParserException("No in-range spectrum to write");

                    // TIC chromatogram.
                    var (chromInfo, chromArrays) =
                        thermoWriter.ConversionHelper.ReadSummaryTrace(TraceType.TIC, raw);
                    var chromMeta = thermoWriter.AddChromatogramData(0ul, chromArrays);
                    thermoWriter.AddChromatogram(
                        chromInfo.Id, chromInfo.DataProcessingRef, chromInfo.Parameters, chromMeta);

                    // Vendor metadata facets (scan-trailer tall was streamed during the loop).
                    if (vendorOn)
                    {
                        if (vendorTall) vendorTrailers!.Close();
                        WriteVendorFacets(thermoWriter, raw, vendorTall, vendorWide,
                            vendorTrailers, scanNumberToOrdinal, wideCols!);
                        Log.Info($"Vendor metadata ({vendorMode}): " +
                                 (vendorTall ? $"{vendorTrailers!.RowCount} tall trailer rows; " : "") +
                                 "file metadata + status log + trailer schema");
                    }

                    // Optional readable JSON sidecar.
                    if (ParseInput.MzPeakVendorMetadataJson != null)
                    {
                        string jsonPath = ParseInput.MzPeakVendorMetadataJson;
                        if (string.IsNullOrEmpty(jsonPath))
                        {
                            var baseOut = ParseInput.OutputFile
                                ?? Path.Combine(ParseInput.OutputDirectory ?? ".",
                                    ParseInput.RawFileNameWithoutExtension);
                            foreach (var ext in new[] { ".gz", ".mzpeak" })
                                if (baseOut.ToLower().EndsWith(ext))
                                    baseOut = baseOut.Substring(0, baseOut.Length - ext.Length)
                                                     .TrimEnd('.');
                            jsonPath = baseOut + ".vendor.json";
                        }
                        var sourceName = Path.GetFileName(ParseInput.RawFilePath);
                        File.WriteAllText(jsonPath, BuildVendorMetadataJson(raw, sourceName));
                        Log.Info($"Vendor metadata JSON → {jsonPath}");
                    }

                    thermoWriter.Close();
                    thermoWriter = null;

                    Log.Info($"Wrote mzPeak archive with {ordinal} spectra");
                }
                finally
                {
                    if (thermoWriter != null)
                        try { thermoWriter.Dispose(); } catch { }
                }

                Writer.Flush();
            }
            finally
            {
                vendorTrailers?.Dispose();
                TryDelete(vendorTrailers?.TempPath);
                Writer?.Close();
            }
        }

        internal static void TryDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
