using System;
using System.Collections.Generic;
using System.IO;
using Parquet.Data;
using Parquet.Schema;

namespace ThermoRawFileParser.Writer
{
    // A chrom-data facet streamed to a seekable temp file. Bounded by scan count anyway; streamed for
    // uniformity with the point facets. The point layout keeps intensity as f32 and carries ms_level.
    internal sealed class ChromDataFacetStream : IChromDataFacet
    {
        public string TempPath { get; }
        private readonly ParquetSchema _schema;
        private readonly DataField _idx, _time, _int, _lvl;
        private readonly int _cap;
        private readonly long _byteCap;
        private long _bufferedBytes;
        private FileStream _sink;
        private MzPeakParquet.Handle _handle;
        private readonly List<ulong> _bIdx = new List<ulong>();
        private readonly List<double> _bTime = new List<double>();
        private readonly List<float> _bInt = new List<float>();
        private readonly List<long> _bLvl = new List<long>();

        // Approximate uncompressed bytes per chrom-data row: u64 index + f64 time + f32 intensity +
        // i64 ms_level.
        private const int ChromRowBytes = 8 + 8 + 4 + 8;

        public long PointCount { get; private set; }

        private static StructField ChromDataStructField() =>
            new StructField("point",
                new DataField<ulong>("chromatogram_index"),
                new DataField<double>("time"),
                new DataField<float>("intensity"),
                new DataField<long>("ms_level"));

        public ChromDataFacetStream(int cap, long byteCap)
        {
            _cap = cap;
            _byteCap = byteCap;
            TempPath = Path.GetTempFileName();
            _schema = new ParquetSchema(ChromDataStructField());
            _idx = MzPeakColumns.Leaf(_schema, "point/chromatogram_index");
            _time = MzPeakColumns.Leaf(_schema, "point/time");
            _int = MzPeakColumns.Leaf(_schema, "point/intensity");
            _lvl = MzPeakColumns.Leaf(_schema, "point/ms_level");
            try
            {
                _sink = new FileStream(TempPath, FileMode.Create, FileAccess.Write);
                _handle = MzPeakParquet.OpenAsync(_sink, _schema, null).GetAwaiter().GetResult();
            }
            catch
            {
                _handle?.Dispose();
                _sink?.Dispose();
                MzPeakSpectrumWriter.TryDelete(TempPath);
                throw;
            }
        }

        public void Append(double time, double intensity, long msLevel)
        {
            _bIdx.Add(0UL); _bTime.Add(time); _bInt.Add((float)intensity); _bLvl.Add(msLevel);
            PointCount++;
            _bufferedBytes += ChromRowBytes;
            if (_bIdx.Count >= _cap || _bufferedBytes >= _byteCap) Flush();
        }

        private void Flush()
        {
            if (_bIdx.Count == 0) return;
            var cols = new Dictionary<DataField, (Array, int[], int[])>
            {
                [_idx] = (_bIdx.ToArray(), null, null),
                [_time] = (_bTime.ToArray(), null, null),
                [_int] = (_bInt.ToArray(), null, null),
                [_lvl] = (_bLvl.ToArray(), null, null)
            };
            _handle.WriteRowGroupAsync(_schema, cols).GetAwaiter().GetResult();
            _bIdx.Clear(); _bTime.Clear(); _bInt.Clear(); _bLvl.Clear();
            _bufferedBytes = 0;
        }

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
