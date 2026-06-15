using System;
using System.Collections.Generic;
using System.IO;
using Parquet.Data;
using Parquet.Schema;

namespace ThermoRawFileParser.Writer
{
    // A point-facet (spectra_data / spectra_peaks) streamed to a seekable temp file in bounded row
    // groups. Staging buffers accumulate per scan and flush at the cap; the final residual buffer is
    // flushed by Close, which also attaches the footer KV before disposing the writer.
    internal sealed class PointFacetStream : IDisposable, ISpectraDataFacet
    {
        public string TempPath { get; }
        private readonly ParquetSchema _schema;
        private readonly DataField _idx, _mz, _int;
        private readonly int _cap;
        private readonly long _byteCap;
        private long _bufferedBytes;
        private FileStream _sink;
        private MzPeakParquet.Handle _handle;
        private readonly List<ulong> _bIdx = new List<ulong>();
        private readonly List<double> _bMz = new List<double>();
        private readonly List<float> _bInt = new List<float>();

        // Approximate uncompressed bytes per point row: u64 spectrum_index + f64 mz + f32 intensity.
        private const int PointRowBytes = 8 + 8 + 4;

        public long PointCount { get; private set; }

        private static StructField PointStructField() =>
            new StructField("point",
                new DataField<ulong>("spectrum_index"),
                new DataField<double>("mz"),
                new DataField<float>("intensity"));

        public PointFacetStream(int cap, long byteCap)
        {
            _cap = cap;
            _byteCap = byteCap;
            TempPath = Path.GetTempFileName();
            _schema = new ParquetSchema(PointStructField());
            _idx = MzPeakSpectrumWriter.Leaf(_schema, "point/spectrum_index");
            _mz = MzPeakSpectrumWriter.Leaf(_schema, "point/mz");
            _int = MzPeakSpectrumWriter.Leaf(_schema, "point/intensity");
            try
            {
                _sink = new FileStream(TempPath, FileMode.Create, FileAccess.Write);
                _handle = MzPeakParquet.OpenAsync(_sink, _schema, null).GetAwaiter().GetResult();
            }
            catch
            {
                // Self-clean a partially-opened facet: the caller never receives a reference to dispose,
                // so release the sink and delete the temp file here before the exception propagates.
                _handle?.Dispose();
                _sink?.Dispose();
                MzPeakSpectrumWriter.TryDelete(TempPath);
                throw;
            }
        }

        // The row + byte caps are HARD upper bounds on the in-memory buffer: a single scan's points
        // are split across as many row groups as needed (a scan MAY span row groups in the point
        // layout), filling the remaining capacity, flushing at whichever cap is reached first, then
        // continuing with the same ordinal.
        public void Append(ulong ordinal, double[] mz, float[] intensity)
        {
            int i = 0;
            while (i < mz.Length)
            {
                int byteRoom = _bufferedBytes >= _byteCap
                    ? 0
                    : (int)Math.Min(int.MaxValue, (_byteCap - _bufferedBytes) / PointRowBytes);
                int room = Math.Min(_cap - _bIdx.Count, byteRoom);
                int take = Math.Min(Math.Max(room, 1), mz.Length - i);
                for (int j = 0; j < take; j++, i++)
                {
                    _bIdx.Add(ordinal); _bMz.Add(mz[i]); _bInt.Add(intensity[i]);
                }
                _bufferedBytes += (long)take * PointRowBytes;
                if (_bIdx.Count >= _cap || _bufferedBytes >= _byteCap) Flush();
            }
            PointCount += mz.Length;
        }

        private void Flush()
        {
            if (_bIdx.Count == 0) return;
            var cols = new Dictionary<DataField, (Array, int[], int[])>
            {
                [_idx] = (_bIdx.ToArray(), null, null),
                [_mz] = (_bMz.ToArray(), null, null),
                [_int] = (_bInt.ToArray(), null, null)
            };
            _handle.WriteRowGroupAsync(_schema, cols).GetAwaiter().GetResult();
            _bIdx.Clear(); _bMz.Clear(); _bInt.Clear();
            _bufferedBytes = 0;
        }

        // Finalize order: flush residual buffer -> CloseAsync(final KV) disposes the writer -> dispose
        // the temp FileStream. After this returns the temp file is fully on disk.
        public void Close(IReadOnlyDictionary<string, string> finalMetadata)
        {
            Flush();
            _handle.CloseAsync(finalMetadata).GetAwaiter().GetResult();
            _handle = null;
            _sink.Dispose();
            _sink = null;
        }

        public void Dispose()
        {
            _handle?.Dispose();
            _sink?.Dispose();
        }
    }
}
