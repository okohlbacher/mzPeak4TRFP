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
            string outputPath = null;
            bool committed = false;

            try
            {
                if (vendorTall) vendorTrailers = new VendorTrailerFacetStream();
                if (vendorOn)   wideCols = VendorWideTrailerFacet.Classify(raw);

                ConfigureWriter(".mzpeak");
                outputPath = (Writer.BaseStream as FileStream)?.Name;
                var storage = new ZipStreamArchiveWriter<Stream>(Writer.BaseStream);

                ThermoMZPeakWriter thermoWriter = null;
                try
                {
                    thermoWriter = new ThermoMZPeakWriter(
                        storage,
                        useChunked: !ParseInput.MzPeakPointLayout,
                        spectrumPeakArrayIndex: ThermoMZPeakWriter.PeakArrayIndex());
                    thermoWriter.InitializeHelper(raw);

                    // Bound row-group size by spectrum count. mzPeak.NET's byte cap (RowGroupSize) is
                    // compared against an element count that undercounts fat chunk/list payloads, so for
                    // files with few but very large (profile / FT-ICR) spectra it never trips and the
                    // whole facet lands in one oversized row group — slow to random-read and flagged by
                    // mzpeak-validate (data_row_group_not_monolithic). Flushing every N spectra keeps row
                    // groups bounded regardless, and also lowers peak memory.
                    thermoWriter.DataWriterConfig = thermoWriter.DataWriterConfig with { EntryBufferSize = 500 };

                    ulong ordinal = 0;

                    for (var scanNumber = firstScanNumber; scanNumber <= lastScanNumber; scanNumber++)
                    {
                        // Cheap filter read + MS-level classification, guarded: an unreadable filter or
                        // an unmapped MSOrder must skip the one scan, not abort the whole file.
                        IScanFilter filter;
                        int level;
                        try
                        {
                            filter = raw.GetFilterForScanNumber(scanNumber);
                            if (!ConversionContextHelper.MSLevelMap.TryGetValue(filter.MSOrder, out level))
                            {
                                Log.Warn($"Scan #{scanNumber}: unmapped MS order {filter.MSOrder}, skipping");
                                ParseInput.NewError();
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Scan #{scanNumber} filter cannot be read: {ex.Message}");
                            Log.Debug(ex.StackTrace);
                            ParseInput.NewError();
                            continue;
                        }

                        // Out-of-range levels are skipped cheaply, before reading heavy scan data.
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

                        // Everything AddSpectrumData writes lands in spectra_data, so its row count is
                        // number_of_data_points regardless of profile/centroid. mzPeak.NET reports that
                        // count as DataPointCount for profile scans but as PeakCount for centroid scans
                        // (isProfile=false) — coalesce so number_of_data_points is always the spectra_data
                        // row count (the point layout's per_spectrum_data_points check enforces this).
                        var dataPointCount = dataMeta.DataPointCount ?? dataMeta.PeakCount;
                        int? peakCount = null;

                        if (centroids != null && centroids.Length > 0)
                        {
                            // Centroids go to the separate spectra_peaks facet; its row count is
                            // number_of_peaks. Merge peak-derived auxiliary arrays into the spectrum row.
                            var peakMeta = thermoWriter.AddSpectrumPeakData(ordinal, centroids);
                            dataMeta.AuxiliaryArrays.AddRange(peakMeta.AuxiliaryArrays);
                            peakCount = peakMeta.PeakCount;
                        }

                        // number_of_data_points <- spectra_data rows; number_of_peaks <- spectra_peaks
                        // rows (null when no peak facet was written for this spectrum).
                        dataMeta = dataMeta with { DataPointCount = dataPointCount, PeakCount = peakCount };

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

                    var (chromInfo, chromArrays) =
                        thermoWriter.ConversionHelper.ReadSummaryTrace(TraceType.TIC, raw);
                    var chromMeta = thermoWriter.AddChromatogramData(0ul, chromArrays);
                    thermoWriter.AddChromatogram(
                        chromInfo.Id, chromInfo.DataProcessingRef, chromInfo.Parameters, chromMeta);

                    if (vendorOn)
                    {
                        if (vendorTall) vendorTrailers!.Close();
                        WriteVendorFacets(thermoWriter, raw, vendorTall, vendorWide,
                            vendorTrailers, scanNumberToOrdinal, wideCols!);
                        Log.Info($"Vendor metadata ({vendorMode}): " +
                                 (vendorTall ? $"{vendorTrailers!.RowCount} tall trailer rows; " : "") +
                                 "file metadata + status log + trailer schema");
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
                committed = true;   // archive fully written and flushed — safe to keep

                // Optional JSON sidecar is independent of the archive: write it only after the archive
                // is committed, and never let a sidecar failure delete the valid archive.
                if (ParseInput.MzPeakVendorMetadataJson != null)
                {
                    try { WriteVendorJsonSidecar(raw); }
                    catch (Exception ex) { Log.Warn($"Vendor metadata JSON sidecar failed: {ex.Message}"); }
                }
            }
            finally
            {
                vendorTrailers?.Dispose();
                TryDelete(vendorTrailers?.TempPath);
                // Guard each cleanup step so a close failure can't skip the partial-archive delete.
                try { Writer?.Close(); } catch (Exception ex) { Log.Debug($"writer close failed: {ex.Message}"); }
                if (!committed && outputPath != null) TryDelete(outputPath);
            }
        }

        // Optional human-readable JSON dump of the vendor metadata, alongside the archive.
        private void WriteVendorJsonSidecar(IRawDataPlus raw)
        {
            string jsonPath = ParseInput.MzPeakVendorMetadataJson;
            if (string.IsNullOrEmpty(jsonPath))
            {
                var baseOut = ParseInput.OutputFile
                    ?? Path.Combine(ParseInput.OutputDirectory ?? ".", ParseInput.RawFileNameWithoutExtension);
                foreach (var ext in new[] { ".gz", ".mzpeak" })
                    if (baseOut.ToLowerInvariant().EndsWith(ext))
                        baseOut = baseOut.Substring(0, baseOut.Length - ext.Length).TrimEnd('.');
                jsonPath = baseOut + ".vendor.json";
            }
            File.WriteAllText(jsonPath, BuildVendorMetadataJson(raw, Path.GetFileName(ParseInput.RawFilePath)));
            Log.Info($"Vendor metadata JSON → {jsonPath}");
        }

        internal static void TryDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
