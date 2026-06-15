using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parquet.Data;
using Parquet.Schema;

namespace ThermoRawFileParser.Writer
{
    // A chunked spectra_data facet streamed to a seekable temp file. One chunk struct row per
    // non-empty m/z window per scan: scalar spectrum_index/start/end/encoding plus the two
    // nullable-item lists (mz_chunk_values delta-encoded, intensity verbatim). The row-group cap counts
    // chunk ROWS, not points; a scan contributes as many rows as it has windows.
    internal sealed class ChunkFacetStream : IDisposable, ISpectraDataFacet
    {
        public string TempPath { get; }
        private readonly ParquetSchema _schema;
        private readonly DataField _idx, _start, _end, _enc, _mzItem, _intItem, _npkItem;
        private readonly ListField _mzList;
        private readonly int _cap;
        private readonly long _byteCap;
        private long _bufferedBytes;
        private readonly double _chunkSize;
        private readonly bool _numpress;
        private FileStream _sink;
        private MzPeakParquet.Handle _handle;

        private readonly List<ulong> _bIdx = new List<ulong>();
        private readonly List<double> _bStart = new List<double>();
        private readonly List<double> _bEnd = new List<double>();
        private readonly List<string> _bEnc = new List<string>();
        private readonly List<MzPeakParquet.LeafRow> _mzRows = new List<MzPeakParquet.LeafRow>();
        private readonly List<double> _mzVals = new List<double>();
        private readonly List<MzPeakParquet.LeafRow> _intRows = new List<MzPeakParquet.LeafRow>();
        private readonly List<float> _intVals = new List<float>();
        private readonly List<MzPeakParquet.LeafRow> _npkRows = new List<MzPeakParquet.LeafRow>();
        private readonly List<byte> _npkVals = new List<byte>();

        // Approximate uncompressed bytes for a chunk row: the four scalars (~40 B with the encoding
        // string) + 8 B per delta-encoded m/z value + 4 B per intensity + 1 B per numpress byte.
        private const int ChunkRowOverheadBytes = 40;

        public long PointCount { get; private set; }

        // The chunk struct. In delta mode it is the 6-field struct (mz_chunk_values / intensity are
        // nullable-item lists; the writer emits no nulls, the type stays null-aware for read parity).
        // In numpress mode a 7th field (mz_numpress_linear_bytes, large_list<uint8 not null>) carries
        // the encoded m/z while mz_chunk_values is emitted NULL on every row.
        private static StructField ChunkStructField(bool numpress)
        {
            if (!numpress)
                return new StructField("chunk",
                    new DataField<ulong>("spectrum_index"),
                    new DataField<double>("mz_chunk_start"),
                    new DataField<double>("mz_chunk_end"),
                    new ListField("mz_chunk_values", new DataField<double>("item", true)),
                    new DataField<string>("chunk_encoding"),
                    new ListField("intensity", new DataField<float>("item", true)));

            return new StructField("chunk",
                new DataField<ulong>("spectrum_index"),
                new DataField<double>("mz_chunk_start"),
                new DataField<double>("mz_chunk_end"),
                new ListField("mz_chunk_values", new DataField<double>("item", true)),
                new DataField<string>("chunk_encoding"),
                new ListField("intensity", new DataField<float>("item", true)),
                new ListField("mz_numpress_linear_bytes", new DataField<byte>("item")));
        }

        public ChunkFacetStream(int cap, long byteCap, double chunkSize, bool numpress)
        {
            _cap = cap;
            _byteCap = byteCap;
            _chunkSize = chunkSize;
            _numpress = numpress;
            TempPath = Path.GetTempFileName();
            _schema = new ParquetSchema(ChunkStructField(numpress));
            _idx = MzPeakColumns.Leaf(_schema, "chunk/spectrum_index");
            _start = MzPeakColumns.Leaf(_schema, "chunk/mz_chunk_start");
            _end = MzPeakColumns.Leaf(_schema, "chunk/mz_chunk_end");
            _enc = MzPeakColumns.Leaf(_schema, "chunk/chunk_encoding");
            _mzItem = MzPeakColumns.Leaf(_schema, "chunk/mz_chunk_values/list/item");
            _mzList = (ListField)MzPeakColumns.FindField(_schema, "chunk/mz_chunk_values");
            _intItem = MzPeakColumns.Leaf(_schema, "chunk/intensity/list/item");
            _npkItem = numpress ? MzPeakColumns.Leaf(_schema, "chunk/mz_numpress_linear_bytes/list/item") : null;
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

        // Builds one chunk row per non-empty window. Empty scans contribute no row.
        public void Append(ulong ordinal, double[] mz, float[] intensity)
        {
            foreach (var (s, e) in MzPeakChunkCodec.Chunk(mz, _chunkSize))
            {
                int k = e - s;
                long rowBytes = ChunkRowOverheadBytes + 4L * k;

                if (_numpress)
                {
                    var win = new double[k];
                    for (int i = 0; i < k; i++) win[i] = mz[i + s];

                    _bIdx.Add(ordinal);
                    _bStart.Add(win[0]);
                    _bEnd.Add(win[k - 1]);
                    _bEnc.Add(MzPeakCv.NumpressLinear);

                    // mz_chunk_values is NULL on every numpress row (parent present, list null).
                    _mzRows.Add(MzPeakParquet.NullList(_mzList));

                    var bytes = MSNumpress.EncodeLinear(win);
                    var nLevels = new int[bytes.Length];
                    var nHas = new bool[bytes.Length];
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        nLevels[i] = _npkItem.MaxDefinitionLevel; nHas[i] = true;
                        _npkVals.Add(bytes[i]);
                    }
                    _npkRows.Add(MzPeakParquet.ListOf(nLevels, nHas));
                    rowBytes += bytes.Length;
                }
                else
                {
                    var slice = new double?[k];
                    for (int i = s; i < e; i++) slice[i - s] = mz[i];
                    MzPeakChunkCodec.DeltaEncode(slice, out var start, out var end, out var values);

                    _bIdx.Add(ordinal);
                    _bStart.Add(start);
                    _bEnd.Add(end);
                    _bEnc.Add(MzPeakCv.ChunkEncoding);

                    if (values.Length == 0)
                    {
                        _mzRows.Add(MzPeakParquet.EmptyList(_mzList));
                    }
                    else
                    {
                        var levels = new int[values.Length];
                        var has = new bool[values.Length];
                        for (int i = 0; i < values.Length; i++)
                        {
                            levels[i] = _mzItem.MaxDefinitionLevel; has[i] = true;
                            _mzVals.Add(values[i].Value);
                        }
                        _mzRows.Add(MzPeakParquet.ListOf(levels, has));
                    }
                    rowBytes += 8L * values.Length;
                }

                var iLevels = new int[k];
                var iHas = new bool[k];
                for (int i = 0; i < k; i++)
                {
                    iLevels[i] = _intItem.MaxDefinitionLevel; iHas[i] = true;
                    _intVals.Add(intensity[s + i]);
                }
                _intRows.Add(MzPeakParquet.ListOf(iLevels, iHas));

                PointCount += k;
                _bufferedBytes += rowBytes;
                if (_bIdx.Count >= _cap || _bufferedBytes >= _byteCap) Flush();
            }
        }

        private void Flush()
        {
            if (_bIdx.Count == 0) return;
            var present = _bIdx.Select(_ => true).ToArray();
            var (idxDef, _i) = MzPeakParquet.NestedLevels(_idx, present.Select(_ => MzPeakParquet.Present(_idx)).ToArray());
            var (startDef, _s) = MzPeakParquet.NestedLevels(_start, present.Select(_ => MzPeakParquet.Present(_start)).ToArray());
            var (endDef, _e) = MzPeakParquet.NestedLevels(_end, present.Select(_ => MzPeakParquet.Present(_end)).ToArray());
            var (encDef, _c) = MzPeakParquet.NestedLevels(_enc, present.Select(_ => MzPeakParquet.Present(_enc)).ToArray());
            var (mzDef, mzRep) = MzPeakParquet.NestedLevels(_mzItem, _mzRows);
            var (intDef, intRep) = MzPeakParquet.NestedLevels(_intItem, _intRows);

            var cols = new Dictionary<DataField, (Array, int[], int[])>
            {
                [_idx] = (_bIdx.ToArray(), idxDef, null),
                [_start] = (_bStart.ToArray(), startDef, null),
                [_end] = (_bEnd.ToArray(), endDef, null),
                [_enc] = (_bEnc.ToArray(), encDef, null),
                [_mzItem] = (_mzVals.ToArray(), mzDef, mzRep),
                [_intItem] = (_intVals.ToArray(), intDef, intRep)
            };
            if (_numpress)
            {
                var (npkDef, npkRep) = MzPeakParquet.NestedLevels(_npkItem, _npkRows);
                cols[_npkItem] = (_npkVals.ToArray(), npkDef, npkRep);
            }
            _handle.WriteRowGroupAsync(_schema, cols).GetAwaiter().GetResult();

            _bIdx.Clear(); _bStart.Clear(); _bEnd.Clear(); _bEnc.Clear();
            _mzRows.Clear(); _mzVals.Clear(); _intRows.Clear(); _intVals.Clear();
            _npkRows.Clear(); _npkVals.Clear();
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
