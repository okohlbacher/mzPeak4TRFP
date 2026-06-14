using System;
using System.Collections.Generic;

namespace ThermoRawFileParser.Writer
{
    // Null-aware delta encode/decode + fixed m/z-window chunker for the chunk spectra_data layout.
    // The structure is null-aware so the decoder can read reference / externally-produced files whose
    // values carry null gap markers ([None, absolute, delta] / [None, None]); the encode/chunk path is
    // exercised by callers with no nulls (the writer never produces gap markers).
    public static class MzPeakChunkCodec
    {
        public const double DefaultChunkSize = 50.0;

        // Delta-encode a window's m/z slice. start/end are the first/last non-null m/z; values holds the
        // consecutive deltas (length k-1 for a gapless slice) and excludes the start, which lives in start.
        // Mirrors null_delta_encode: a value after a null is stored ABSOLUTE, a leading null is kept as a
        // marker. The writer passes all-present slices, so this reduces to plain consecutive deltas.
        public static void DeltaEncode(double?[] mz, out double start, out double end, out double?[] values)
        {
            if (mz == null || mz.Length == 0)
                throw new ArgumentException("DeltaEncode requires a non-empty slice.", nameof(mz));

            start = 0.0; end = 0.0;
            bool sawStart = false;
            for (int i = 0; i < mz.Length; i++)
            {
                if (mz[i].HasValue)
                {
                    if (!sawStart) { start = mz[i].Value; sawStart = true; }
                    end = mz[i].Value;
                }
            }

            var buffer = new List<double?>(mz.Length);
            int idx = 0;
            double? last = mz[idx++];
            if (!last.HasValue) buffer.Add(null);
            for (; idx < mz.Length; idx++)
            {
                var item = mz[idx];
                if (item.HasValue)
                {
                    if (last.HasValue) buffer.Add(item.Value - last.Value);
                    else buffer.Add(item.Value);
                    last = item;
                }
                else
                {
                    buffer.Add(null);
                    last = null;
                }
            }
            values = buffer.ToArray();
        }

        // Reconstruct the m/z array from start + delta values. Mirrors null_delta_decode byte-for-byte:
        // start is the first reconstructed m/z; cumulative add for present deltas; absolute restart after a
        // null; nulls preserved aligned to intensity. The [None, None] case decodes to a single peak at
        // start; a leading [None, present, ...] treats the present value as an absolute restart.
        public static double?[] DeltaDecode(double start, double?[] values)
        {
            var arr = values ?? Array.Empty<double?>();
            var buf = new List<double?>(arr.Length + 1);
            double? last = start;

            if (arr.Length == 0)
            {
                buf.Add(start);
                return buf.ToArray();
            }

            if (!arr[0].HasValue)
            {
                if (arr.Length > 1 && !arr[1].HasValue) buf.Add(last);
                last = null;
            }
            else
            {
                buf.Add(start);
            }

            foreach (var item in arr)
            {
                if (item.HasValue)
                {
                    if (last.HasValue) { double d = item.Value + last.Value; buf.Add(d); last = d; }
                    else { buf.Add(item.Value); last = item; }
                }
                else
                {
                    buf.Add(null);
                    last = null;
                }
            }
            return buf.ToArray();
        }

        // Partition a sorted, non-decreasing m/z axis into [start,end) intervals each spanning <= width from
        // the interval's own first m/z. Mirrors null_chunk_every_k: threshold advances by width, a boundary
        // length-1 chunk is rolled into the adjacent chunk, the final residual is always emitted. An empty
        // input yields zero intervals (no chunk row). A single-point spectrum yields one length-1 interval.
        // Throws ArgumentException on non-monotonic (descending) input; the codec assumes ascending order.
        public static List<(int start, int end)> Chunk(double[] mz, double width)
        {
            // A non-finite or non-positive width never advances the threshold and would loop forever;
            // reject it up front so the partition loop is always guaranteed to terminate.
            if (!(width > 0.0) || double.IsInfinity(width))
                throw new ArgumentOutOfRangeException(nameof(width), width,
                    "Chunk width must be a finite value greater than 0.");

            var result = new List<(int, int)>();
            if (mz == null || mz.Length == 0) return result;

            for (int i = 1; i < mz.Length; i++)
                if (mz[i] < mz[i - 1])
                    throw new ArgumentException("Chunk requires non-decreasing m/z input.", nameof(mz));

            int n = mz.Length;
            int offset = 0;
            double threshold = mz[0] + width;
            for (int i = 1; i < n; i++)
            {
                if (mz[i] > threshold)
                {
                    if (i - offset != 1)
                    {
                        result.Add((offset, i));
                        offset = i;
                    }
                    while (threshold < mz[i]) threshold += width;
                }
            }
            result.Add((offset, n));
            return result;
        }
    }
}
