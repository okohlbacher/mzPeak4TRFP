using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ThermoRawFileParser.Writer;

namespace ThermoRawFileParserTest
{
    [TestFixture]
    public class MzPeakChunkTests
    {
        private static long MzBits(double v) => BitConverter.DoubleToInt64Bits(v);
        private static int IntBits(float v) => BitConverter.SingleToInt32Bits(v);

        // Encode + decode every window and concatenate; returns the reconstructed (mz,intensity) pairs
        // dropping null pairs, mirroring the writer/reader contract.
        private static List<(double mz, float inten)> RoundTrip(double[] mz, float[] inten, double width)
        {
            var outp = new List<(double, float)>();
            foreach (var (s, e) in MzPeakChunkCodec.Chunk(mz, width))
            {
                var slice = new double?[e - s];
                for (int i = s; i < e; i++) slice[i - s] = mz[i];
                MzPeakChunkCodec.DeltaEncode(slice, out var start, out _, out var values);
                var dmz = MzPeakChunkCodec.DeltaDecode(start, values);
                for (int i = 0; i < dmz.Length; i++)
                {
                    if (!dmz[i].HasValue) continue;
                    outp.Add((dmz[i].Value, inten[s + i]));
                }
            }
            return outp;
        }

        [Test]
        public void RoundTrip_Gapless_BitwiseExact()
        {
            var rnd = new Random(7);
            int n = 5000;
            var mz = new double[n];
            var inten = new float[n];
            double cur = 100.0;
            for (int i = 0; i < n; i++)
            {
                cur += rnd.NextDouble() * 0.5;
                mz[i] = cur;
                inten[i] = (float)(rnd.NextDouble() * 1e6);
            }

            var rt = RoundTrip(mz, inten, 50.0);
            Assert.That(rt.Count, Is.EqualTo(n), "no points lost");

            var src = Enumerable.Range(0, n)
                .Select(i => (MzBits(mz[i]), IntBits(inten[i])))
                .OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToArray();
            var got = rt
                .Select(p => (MzBits(p.mz), IntBits(p.inten)))
                .OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToArray();
            Assert.That(got, Is.EqualTo(src), "decoded multiset is BITWISE equal to input");
        }

        [Test]
        public void AllEqual_DuplicateMz_BitwiseExact()
        {
            int n = 200;
            var mz = Enumerable.Repeat(345.678901234, n).ToArray();
            var inten = Enumerable.Range(0, n).Select(i => (float)(i + 1)).ToArray();

            var rt = RoundTrip(mz, inten, 50.0);
            Assert.That(rt.Count, Is.EqualTo(n));
            foreach (var p in rt)
                Assert.That(MzBits(p.mz), Is.EqualTo(MzBits(345.678901234)),
                    "every repeated m/z reconstructs bit-exactly (delta 0.0)");
        }

        [Test]
        public void SinglePoint_OneChunk_ValuesEmpty()
        {
            var mz = new[] { 512.3456 };
            var inten = new[] { 999.5f };
            var chunks = MzPeakChunkCodec.Chunk(mz, 50.0);
            Assert.That(chunks.Count, Is.EqualTo(1), "whole-spectrum single point yields one chunk");
            Assert.That(chunks[0], Is.EqualTo((0, 1)));

            MzPeakChunkCodec.DeltaEncode(new double?[] { mz[0] }, out var start, out var end, out var values);
            Assert.That(values.Length, Is.EqualTo(0), "mz_chunk_values empty for a single point");
            Assert.That(start, Is.EqualTo(mz[0]));
            Assert.That(end, Is.EqualTo(mz[0]));

            var dmz = MzPeakChunkCodec.DeltaDecode(start, values);
            Assert.That(dmz.Length, Is.EqualTo(1));
            Assert.That(MzBits(dmz[0].Value), Is.EqualTo(MzBits(mz[0])));
        }

        [Test]
        public void Guards_Empty_NoChunk_And_NonMonotonic_Throws()
        {
            Assert.That(MzPeakChunkCodec.Chunk(Array.Empty<double>(), 50.0), Is.Empty,
                "empty spectrum yields no chunk row");

            Assert.Throws<ArgumentException>(
                () => MzPeakChunkCodec.Chunk(new[] { 300.0, 200.0, 100.0 }, 50.0),
                "non-monotonic input throws");

            Assert.Throws<ArgumentException>(
                () => MzPeakChunkCodec.DeltaEncode(Array.Empty<double?>(), out _, out _, out _),
                "DeltaEncode rejects an empty slice");
        }

        [Test]
        public void RefNull_NoneAbsoluteDelta_DecodesLikeReference()
        {
            // Reproduces refs/mzPeak/small.chunked.mzpeak row 1 (spectrum 0): a leading null, then an
            // absolute restart, then consecutive deltas. The leading (null,null) pair is dropped.
            double start = 252.96545361882283;
            var values = new double?[]
            {
                null,
                252.96545361882283,
                0.00038742706865946275,
                0.00038742825540794,
                0.00038742944209957386,
                0.00038743062887647284
            };
            var inten = new float?[] { null, 1328.245849609375f, 3272.78173828125f, 4725.3662109375f,
                4891.2607421875f, 3677.263671875f };

            var dmz = MzPeakChunkCodec.DeltaDecode(start, values);
            var pairs = new List<(double, float)>();
            for (int i = 0; i < dmz.Length; i++)
                if (dmz[i].HasValue && inten[i].HasValue) pairs.Add((dmz[i].Value, inten[i].Value));

            Assert.That(pairs.Count, Is.EqualTo(5), "the leading (null,null) pair is dropped");
            Assert.That(MzBits(pairs[0].Item1), Is.EqualTo(MzBits(252.96545361882283)),
                "post-null value is an absolute restart");
            Assert.That(MzBits(pairs[1].Item1),
                Is.EqualTo(MzBits(252.96545361882283 + 0.00038742706865946275)),
                "subsequent deltas apply cumulatively");
            Assert.That(pairs[0].Item2, Is.EqualTo(1328.245849609375f));
            Assert.That(pairs[1].Item2, Is.EqualTo(3272.78173828125f));
        }

        [Test]
        public void RefNull_NoneNone_DecodesToSingletonAtStart()
        {
            // The [None, None] singleton-at-boundary case: decodes to a single peak at mz_chunk_start.
            double start = 401.12345;
            var values = new double?[] { null, null };
            var dmz = MzPeakChunkCodec.DeltaDecode(start, values);
            var present = dmz.Where(v => v.HasValue).Select(v => v.Value).ToArray();
            Assert.That(present.Length, Is.EqualTo(1), "exactly one reconstructed peak");
            Assert.That(MzBits(present[0]), Is.EqualTo(MzBits(start)),
                "the single peak sits at mz_chunk_start");
        }
    }
}
