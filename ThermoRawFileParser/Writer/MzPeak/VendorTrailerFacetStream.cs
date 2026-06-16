using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ThermoFisher.CommonCore.Data.Interfaces;

namespace ThermoRawFileParser.Writer
{
    // Streamed vendor_scan_trailers facet (tall): one row per (emitted spectrum, trailer label),
    // captured DURING the main scan loop so it covers exactly the committed scans and is keyed by both
    // the dense `ordinal` (joins the spectra facets) and the verbatim Thermo `scan_number`. `value` is
    // the exact source string; `value_float` is the TYPED trailer value (GetTrailerExtraValues), avoiding
    // culture-dependent string parsing. Flushed in bounded row groups for constant memory.
    internal sealed class VendorTrailerFacetStream : IDisposable
    {
        public string TempPath { get; }
        public long RowCount { get; private set; }

        private readonly ParquetSchema _schema;
        private FileStream _sink;
        private ParquetWriter _writer;
        private readonly List<ulong> _ord = new List<ulong>();
        private readonly List<int> _scan = new List<int>();
        private readonly List<string> _lab = new List<string>();
        private readonly List<string> _val = new List<string>();
        private readonly List<double?> _flt = new List<double?>();

        private const int FlushRows = 1_000_000;

        public VendorTrailerFacetStream()
        {
            TempPath = Path.GetTempFileName();
            _schema = new ParquetSchema(
                new DataField<ulong>("ordinal"), new DataField<int>("scan_number"),
                new DataField<string>("label"), new DataField<string>("value"), new DataField<double?>("value_float"));
            try
            {
                _sink = new FileStream(TempPath, FileMode.Create, FileAccess.Write);
                _writer = ParquetWriter.CreateAsync(_schema, _sink).GetAwaiter().GetResult();
            }
            catch
            {
                _writer?.Dispose();
                _sink?.Dispose();
                MzPeakSpectrumWriter.TryDelete(TempPath);
                throw;
            }
        }

        // Capture one committed scan's trailer bag. Reads the verbatim strings and the typed values.
        public void Append(ulong ordinal, int scanNumber, IRawDataPlus raw)
        {
            var info = raw.GetTrailerExtraInformation(scanNumber);
            if (info == null) return;
            object[] typed = null;
            try { typed = raw.GetTrailerExtraValues(scanNumber); } catch { }
            for (int i = 0; i < info.Length; i++)
            {
                _ord.Add(ordinal);
                _scan.Add(scanNumber);
                _lab.Add(info.Labels[i] ?? "");
                _val.Add(info.Values[i] ?? "");
                _flt.Add(MzPeakSpectrumWriter.NumericOrNull(typed != null && i < typed.Length ? typed[i] : null));
                RowCount++;
            }
            if (_ord.Count >= FlushRows) Flush();
        }

        private void Flush()
        {
            if (_ord.Count == 0) return;
            using (var rg = _writer.CreateRowGroup())
            {
                rg.WriteColumnAsync(new DataColumn((DataField)_schema[0], _ord.ToArray())).GetAwaiter().GetResult();
                rg.WriteColumnAsync(new DataColumn((DataField)_schema[1], _scan.ToArray())).GetAwaiter().GetResult();
                rg.WriteColumnAsync(new DataColumn((DataField)_schema[2], _lab.ToArray())).GetAwaiter().GetResult();
                rg.WriteColumnAsync(new DataColumn((DataField)_schema[3], _val.ToArray())).GetAwaiter().GetResult();
                rg.WriteColumnAsync(new DataColumn((DataField)_schema[4], _flt.ToArray())).GetAwaiter().GetResult();
            }
            _ord.Clear(); _scan.Clear(); _lab.Clear(); _val.Clear(); _flt.Clear();
        }

        public void Close()
        {
            Flush();
            _writer.Dispose();
            _writer = null;
            _sink.Dispose();
            _sink = null;
        }

        public void Dispose()
        {
            _writer?.Dispose();
            _sink?.Dispose();
        }
    }
}
