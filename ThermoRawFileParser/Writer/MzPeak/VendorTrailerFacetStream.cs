using System;
using System.Collections.Generic;
using System.IO;
using Apache.Arrow;
using Apache.Arrow.Types;
using ThermoFisher.CommonCore.Data.Interfaces;

namespace ThermoRawFileParser.Writer
{
    // Streamed vendor_scan_trailers facet (tall): one row per (emitted spectrum, trailer label),
    // captured DURING the main scan loop so it covers exactly the committed scans and is keyed by both
    // the dense `ordinal` (joins the spectra facets) and the verbatim Thermo `scan_number`. `value` is
    // the exact source string; `value_float` is the TYPED trailer value, avoiding culture-dependent
    // string parsing. Flushed in bounded row groups for constant memory. Uses ParquetSharp/Arrow.
    internal sealed class VendorTrailerFacetStream : IDisposable
    {
        public string TempPath { get; }
        public long RowCount { get; private set; }

        private readonly Schema _schema;
        private FileStream _sink;
        private ParquetSharp.IO.ManagedOutputStream _managedSink;
        private ParquetSharp.Arrow.FileWriter _writer;

        private readonly List<ulong> _ord = new List<ulong>();
        private readonly List<int> _scan = new List<int>();
        private readonly List<string> _lab = new List<string>();
        private readonly List<string> _val = new List<string>();
        private readonly List<double?> _flt = new List<double?>();

        private const int FlushRows = 1_000_000;

        public VendorTrailerFacetStream()
        {
            TempPath = Path.GetTempFileName();
            _schema = new Schema.Builder()
                .Field(new Field("ordinal",      new UInt64Type(), nullable: false))
                .Field(new Field("scan_number",  new Int32Type(),  nullable: false))
                .Field(new Field("label",        new StringType(), nullable: false))
                .Field(new Field("value",        new StringType(), nullable: false))
                .Field(new Field("value_float",  new DoubleType(), nullable: true))
                .Build();
            try
            {
                _sink = new FileStream(TempPath, FileMode.Create, FileAccess.Write);
                _managedSink = new ParquetSharp.IO.ManagedOutputStream(_sink);
                var writerProps = new ParquetSharp.WriterPropertiesBuilder()
                    .Compression(ParquetSharp.Compression.Zstd)
                    .Build();
                var arrowProps = new ParquetSharp.Arrow.ArrowWriterPropertiesBuilder()
                    .StoreSchema()
                    .Build();
                _writer = new ParquetSharp.Arrow.FileWriter(_managedSink, _schema, writerProps, arrowProps);
            }
            catch
            {
                _writer?.Close();
                _managedSink?.Dispose();
                _sink?.Dispose();
                MzPeakSpectrumWriter.TryDelete(TempPath);
                throw;
            }
        }

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

            var ordBuilder  = new UInt64Array.Builder(); foreach (var v in _ord)  ordBuilder.Append(v);
            var scanBuilder = new Int32Array.Builder();  foreach (var v in _scan) scanBuilder.Append(v);
            var labBuilder  = new StringArray.Builder(); foreach (var v in _lab)  labBuilder.Append(v);
            var valBuilder  = new StringArray.Builder(); foreach (var v in _val)  valBuilder.Append(v);
            var fltBuilder  = new DoubleArray.Builder();
            foreach (var v in _flt) { if (v.HasValue) fltBuilder.Append(v.Value); else fltBuilder.AppendNull(); }

            var batch = new RecordBatch(_schema, new IArrowArray[]
            {
                ordBuilder.Build(), scanBuilder.Build(), labBuilder.Build(),
                valBuilder.Build(), fltBuilder.Build()
            }, _ord.Count);

            _writer.WriteRecordBatch(batch);
            _ord.Clear(); _scan.Clear(); _lab.Clear(); _val.Clear(); _flt.Clear();
        }

        public void Close()
        {
            Flush();
            _writer.Close();
            _writer = null;
            _managedSink.Dispose();
            _managedSink = null;
            _sink.Dispose();
            _sink = null;
        }

        public void Dispose()
        {
            if (_writer != null) { try { _writer.Close(); } catch { } _writer = null; }
            _managedSink?.Dispose();
            _sink?.Dispose();
        }
    }
}
