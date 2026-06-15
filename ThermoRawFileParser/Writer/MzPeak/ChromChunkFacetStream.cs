using System;
using System.Collections.Generic;
using System.IO;
using Parquet.Data;
using Parquet.Schema;

namespace ThermoRawFileParser.Writer
{
    // A chunked chromatograms_data facet: the single TIC chromatogram is emitted as ONE chunk row over
    // the time axis, mirroring the reference's one-chunk-per-chromatogram layout. time is delta-encoded
    // (chunk_encoding MS:1003089) or numpress-linear (MS:1002312, in time_numpress_linear_bytes);
    // intensity is stored verbatim as f64 (the reference uses large_list<double>). The chromatogram is
    // small (one sample per scan), so the whole axis is buffered and written as a single row.
    internal sealed class ChromChunkFacetStream : IChromDataFacet
    {
        public string TempPath { get; }
        private readonly ParquetSchema _schema;
        private readonly DataField _idx, _start, _end, _enc, _timeItem, _intItem, _npkItem;
        private readonly ListField _timeList;
        private readonly bool _numpress;
        private FileStream _sink;
        private MzPeakParquet.Handle _handle;
        private readonly List<double> _times = new List<double>();
        private readonly List<double> _ints = new List<double>();

        public long PointCount { get; private set; }

        // The chunk struct mirrors the spectra chunk but over the time axis: scalar chromatogram_index /
        // time_chunk_start / time_chunk_end / chunk_encoding, the nullable-item time_chunk_values and
        // intensity lists, plus (numpress mode) the time_numpress_linear_bytes list.
        private static StructField ChunkStructField(bool numpress)
        {
            if (!numpress)
                return new StructField("chunk",
                    new DataField<ulong>("chromatogram_index"),
                    new DataField<double>("time_chunk_start"),
                    new DataField<double>("time_chunk_end"),
                    new ListField("time_chunk_values", new DataField<double>("item", true)),
                    new DataField<string>("chunk_encoding"),
                    new ListField("intensity", new DataField<double>("item", true)));

            return new StructField("chunk",
                new DataField<ulong>("chromatogram_index"),
                new DataField<double>("time_chunk_start"),
                new DataField<double>("time_chunk_end"),
                new ListField("time_chunk_values", new DataField<double>("item", true)),
                new DataField<string>("chunk_encoding"),
                new ListField("intensity", new DataField<double>("item", true)),
                new ListField("time_numpress_linear_bytes", new DataField<byte>("item")));
        }

        public ChromChunkFacetStream(bool numpress)
        {
            _numpress = numpress;
            TempPath = Path.GetTempFileName();
            _schema = new ParquetSchema(ChunkStructField(numpress));
            _idx = MzPeakColumns.Leaf(_schema, "chunk/chromatogram_index");
            _start = MzPeakColumns.Leaf(_schema, "chunk/time_chunk_start");
            _end = MzPeakColumns.Leaf(_schema, "chunk/time_chunk_end");
            _enc = MzPeakColumns.Leaf(_schema, "chunk/chunk_encoding");
            _timeItem = MzPeakColumns.Leaf(_schema, "chunk/time_chunk_values/list/item");
            _timeList = (ListField)MzPeakColumns.FindField(_schema, "chunk/time_chunk_values");
            _intItem = MzPeakColumns.Leaf(_schema, "chunk/intensity/list/item");
            _npkItem = numpress ? MzPeakColumns.Leaf(_schema, "chunk/time_numpress_linear_bytes/list/item") : null;
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
            _times.Add(time);
            _ints.Add(intensity);
            PointCount++;
        }

        public void Close(IReadOnlyDictionary<string, string> finalMetadata)
        {
            if (_times.Count > 0) WriteChunkRow();
            _handle.CloseAsync(finalMetadata).GetAwaiter().GetResult();
            _handle = null;
            _sink.Dispose();
            _sink = null;
        }

        // Emit the single chunk row spanning the whole time axis.
        private void WriteChunkRow()
        {
            int k = _times.Count;
            double start = _times[0], end = _times[k - 1];
            string enc;
            double[] timeVals;
            MzPeakParquet.LeafRow timeRow;
            byte[] npkBytes = null;

            if (_numpress)
            {
                enc = MzPeakCv.NumpressLinear;
                timeRow = MzPeakParquet.NullList(_timeList);   // time lives in the numpress bytes
                timeVals = Array.Empty<double>();
                npkBytes = MSNumpress.EncodeLinear(_times.ToArray());
            }
            else
            {
                enc = MzPeakCv.ChunkEncoding;
                var slice = new double?[k];
                for (int i = 0; i < k; i++) slice[i] = _times[i];
                MzPeakChunkCodec.DeltaEncode(slice, out start, out end, out var values);
                if (values.Length == 0)
                {
                    timeRow = MzPeakParquet.EmptyList(_timeList);
                    timeVals = Array.Empty<double>();
                }
                else
                {
                    timeVals = new double[values.Length];
                    var levels = new int[values.Length];
                    var has = new bool[values.Length];
                    for (int i = 0; i < values.Length; i++)
                    {
                        levels[i] = _timeItem.MaxDefinitionLevel; has[i] = true;
                        timeVals[i] = values[i].Value;
                    }
                    timeRow = MzPeakParquet.ListOf(levels, has);
                }
            }

            var intLevels = new int[k];
            var intHas = new bool[k];
            for (int i = 0; i < k; i++) { intLevels[i] = _intItem.MaxDefinitionLevel; intHas[i] = true; }
            var intRow = MzPeakParquet.ListOf(intLevels, intHas);

            var (idxDef, _ii) = MzPeakParquet.NestedLevels(_idx, new[] { MzPeakParquet.Present(_idx) });
            var (startDef, _ss) = MzPeakParquet.NestedLevels(_start, new[] { MzPeakParquet.Present(_start) });
            var (endDef, _ee) = MzPeakParquet.NestedLevels(_end, new[] { MzPeakParquet.Present(_end) });
            var (encDef, _cc) = MzPeakParquet.NestedLevels(_enc, new[] { MzPeakParquet.Present(_enc) });
            var (timeDef, timeRep) = MzPeakParquet.NestedLevels(_timeItem, new[] { timeRow });
            var (intDef, intRep) = MzPeakParquet.NestedLevels(_intItem, new[] { intRow });

            var cols = new Dictionary<DataField, (Array, int[], int[])>
            {
                [_idx] = (new ulong[] { 0UL }, idxDef, null),
                [_start] = (new[] { start }, startDef, null),
                [_end] = (new[] { end }, endDef, null),
                [_enc] = (new[] { enc }, encDef, null),
                [_timeItem] = (timeVals, timeDef, timeRep),
                [_intItem] = (_ints.ToArray(), intDef, intRep)
            };
            if (_numpress)
            {
                var nLevels = new int[npkBytes.Length];
                var nHas = new bool[npkBytes.Length];
                for (int i = 0; i < npkBytes.Length; i++) { nLevels[i] = _npkItem.MaxDefinitionLevel; nHas[i] = true; }
                var (npkDef, npkRep) = MzPeakParquet.NestedLevels(_npkItem, new[] { MzPeakParquet.ListOf(nLevels, nHas) });
                cols[_npkItem] = (npkBytes, npkDef, npkRep);
            }
            _handle.WriteRowGroupAsync(_schema, cols).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            _handle?.Dispose();
            _sink?.Dispose();
        }
    }
}
