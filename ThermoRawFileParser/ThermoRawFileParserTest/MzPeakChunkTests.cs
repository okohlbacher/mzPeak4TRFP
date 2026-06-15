using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Parquet;
using Parquet.Schema;
using ThermoRawFileParser;
using ThermoRawFileParser.Writer;
using static ThermoRawFileParserTest.MzPeakTestSupport;

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
        public void Chunk_NonFiniteOrNonPositiveWidth_Throws()
        {
            var mz = new[] { 100.0, 150.0, 200.0 };

            // A non-positive or non-finite width never advances the partition threshold; each must throw
            // rather than loop forever.
            Assert.Throws<ArgumentOutOfRangeException>(() => MzPeakChunkCodec.Chunk(mz, 0.0), "width 0");
            Assert.Throws<ArgumentOutOfRangeException>(() => MzPeakChunkCodec.Chunk(mz, -1.0), "width -1");
            Assert.Throws<ArgumentOutOfRangeException>(() => MzPeakChunkCodec.Chunk(mz, double.NaN), "width NaN");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MzPeakChunkCodec.Chunk(mz, double.PositiveInfinity), "width +inf");

            // A valid finite width partitions normally and covers every point.
            var chunks = MzPeakChunkCodec.Chunk(mz, 50.0);
            Assert.That(chunks.Count, Is.GreaterThan(0), "valid width yields chunks");
            Assert.That(chunks.Sum(c => c.end - c.start), Is.EqualTo(mz.Length), "all points covered");
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

        // --- MSNumpress port: exact vectors + round-trip + edge cases + pynumpress cross-check ---

        private static byte[] HexBytes(string hex) =>
            hex.Split(' ').Select(h => System.Convert.ToByte(h, 16)).ToArray();

        private static string ToHex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));

        [Test]
        public void Numpress_V1_OptimalLinearFixedPoint()
        {
            Assert.That(MSNumpress.OptimalLinearFixedPoint(new[] { 1.0, 2.0, 3.0 }),
                Is.EqualTo(1073741823.0), "floor(0x7FFFFFFF / 2)");
        }

        [Test]
        public void Numpress_V2_KnownVector_ExactBytes_And_Decode()
        {
            var data = new[] { 100.0, 100.5, 101.0, 101.5 };
            var expected = HexBytes("41 74 60 CB C0 00 00 00 70 F9 5C 7F CE FF FF 7F 88");
            var got = MSNumpress.EncodeLinear(data, 21367996.0);
            Assert.That(got, Is.EqualTo(expected),
                $"EncodeLinear == canonical ms-numpress bytes; got {ToHex(got)}");

            var dec = MSNumpress.DecodeLinear(got);
            Assert.That(dec.Length, Is.EqualTo(4));
            for (int i = 0; i < 4; i++)
                Assert.That(Math.Abs(dec[i] - data[i]), Is.LessThanOrEqualTo(0.5 / 21367996.0));
        }

        [Test]
        public void Numpress_V3_ReferenceRow0_FixedPoint_And_AnchoredBound()
        {
            var root = RepoRoot();
            if (root == null) Assert.Ignore("reference numpress archive not found");
            var refArchive = Path.Combine(root, "refs/mzPeak/small.numpress.mzpeak");
            if (!File.Exists(refArchive)) Assert.Ignore("small.numpress.mzpeak not present");

            var snippet =
                "import pyarrow.parquet as pq, json, struct\n" +
                "t = pq.read_table(r'{PARQUET}').column('chunk').combine_chunks()\n" +
                "nb=t.field('mz_numpress_linear_bytes').to_pylist()\n" +
                "cs=t.field('mz_chunk_start').to_pylist(); ce=t.field('mz_chunk_end').to_pylist()\n" +
                "row=bytes(nb[0])\n" +
                "print(json.dumps({'bytes':[int(x) for x in row],'start':cs[0],'end':ce[0]}))\n";
            var row0 = PyArrow(refArchive, "spectra_data.parquet", snippet);
            var bytes = ((JArray)row0["bytes"]).Select(x => (byte)(int)x).ToArray();
            double start = (double)row0["start"];
            double end = (double)row0["end"];

            double fp = ReadBeDouble(bytes);
            Assert.That(fp, Is.EqualTo(10599266.0), "reference row-0 fixed point");

            var dec = MSNumpress.DecodeLinear(bytes);
            var aligned = AnchorAlign(dec, start, end);
            Assert.That(aligned.Length, Is.GreaterThan(0));
            Assert.That(Math.Abs(aligned[0] - start), Is.LessThanOrEqualTo(0.5 / fp),
                "anchored decode start within 0.5/fp of mz_chunk_start");
            Assert.That(Math.Abs(aligned[aligned.Length - 1] - end), Is.LessThanOrEqualTo(0.5 / fp),
                "anchored decode end within 0.5/fp of mz_chunk_end");
        }

        [Test]
        public void Numpress_V4_ByteIdentity_vs_ReferenceVector_And_Pynumpress()
        {
            // (a) byte-identity vs the canonical ms-numpress known vector at a shared fp.
            var data = new[] { 100.0, 100.5, 101.0, 101.5 };
            double fp = 21367996.0;
            var got = MSNumpress.EncodeLinear(data, fp);
            Assert.That(got, Is.EqualTo(HexBytes("41 74 60 CB C0 00 00 00 70 F9 5C 7F CE FF FF 7F 88")),
                "byte-identity vs ms-numpress reference vector");

            // (b) byte-identity vs pynumpress.encode_linear at the same fp (python3.11).
            var py = ResolvePython311();
            if (py == null) Assert.Ignore("python3.11/pynumpress not available");
            var pyBytes = PyEncodeLinear(py, data, fp);
            Assert.That(got, Is.EqualTo(pyBytes),
                $"byte-identity vs pynumpress encode; ours {ToHex(got)} py {ToHex(pyBytes)}");
        }

        [Test]
        public void Numpress_RoundTrip_RealAndSynthetic_WithinHalfOverFp()
        {
            var rnd = new Random(11);
            foreach (int n in new[] { 3, 17, 251, 1024 })
            {
                var data = new double[n];
                double cur = 150.0 + rnd.NextDouble();
                for (int i = 0; i < n; i++) { cur += rnd.NextDouble() * 0.4 + 1e-4; data[i] = cur; }
                double fp = MSNumpress.OptimalLinearFixedPoint(data);
                Assert.That(fp, Is.GreaterThan(0.0));
                var enc = MSNumpress.EncodeLinear(data, fp);
                var dec = MSNumpress.DecodeLinear(enc);
                Assert.That(dec.Length, Is.EqualTo(n), $"length preserved for n={n}");
                double bound = 0.5 / fp;
                for (int i = 0; i < n; i++)
                    Assert.That(Math.Abs(dec[i] - data[i]), Is.LessThanOrEqualTo(bound),
                        $"n={n} index {i} within 0.5/fp");
            }
        }

        [Test]
        public void Numpress_EdgeCases_Empty_Single_Two()
        {
            // empty -> 8-byte fp-only output, decodes to empty.
            var emptyEnc = MSNumpress.EncodeLinear(Array.Empty<double>(), 1000.0);
            Assert.That(emptyEnc.Length, Is.EqualTo(8));
            Assert.That(MSNumpress.DecodeLinear(emptyEnc).Length, Is.EqualTo(0));

            // single value -> 12 bytes; OUR DecodeLinear is the oracle (pynumpress rejects 12-byte streams).
            var single = new[] { 555.55 };
            double sfp = MSNumpress.OptimalLinearFixedPoint(single);
            var sEnc = MSNumpress.EncodeLinear(single, sfp);
            Assert.That(sEnc.Length, Is.EqualTo(12));
            var sDec = MSNumpress.DecodeLinear(sEnc);
            Assert.That(sDec.Length, Is.EqualTo(1));
            Assert.That(Math.Abs(sDec[0] - single[0]), Is.LessThanOrEqualTo(0.5 / sfp));

            // two values -> 16 bytes (no residuals).
            var two = new[] { 200.0, 200.25 };
            double tfp = MSNumpress.OptimalLinearFixedPoint(two);
            var tEnc = MSNumpress.EncodeLinear(two, tfp);
            Assert.That(tEnc.Length, Is.EqualTo(16));
            var tDec = MSNumpress.DecodeLinear(tEnc);
            Assert.That(tDec.Length, Is.EqualTo(2));
            for (int i = 0; i < 2; i++)
                Assert.That(Math.Abs(tDec[i] - two[i]), Is.LessThanOrEqualTo(0.5 / tfp));
        }

        [Test]
        public void Numpress_Pynumpress_CrossCheck_ByteIdentity_Decode_OptimalFp()
        {
            var py = ResolvePython311();
            if (py == null) Assert.Ignore("python3.11/pynumpress not available");

            var rnd = new Random(23);
            int n = 300;
            var data = new double[n];
            double cur = 300.0;
            for (int i = 0; i < n; i++) { cur += rnd.NextDouble() * 0.5 + 1e-4; data[i] = cur; }

            double ourFp = MSNumpress.OptimalLinearFixedPoint(data);
            double pyFp = PyOptimalFp(py, data);
            Assert.That(ourFp, Is.EqualTo(pyFp), "OptimalLinearFixedPoint parity with pynumpress");

            var ours = MSNumpress.EncodeLinear(data, ourFp);
            var pyBytes = PyEncodeLinear(py, data, ourFp);
            Assert.That(ours, Is.EqualTo(pyBytes), "byte-identity vs pynumpress at shared fp");

            var pyDecoded = PyDecodeLinear(py, ours);
            double bound = 0.5 / ourFp;
            Assert.That(pyDecoded.Length, Is.EqualTo(n));
            for (int i = 0; i < n; i++)
                Assert.That(Math.Abs(pyDecoded[i] - data[i]), Is.LessThanOrEqualTo(bound),
                    "pynumpress decode of our bytes within 0.5/fp");
        }

        [Test]
        public void Numpress_Writer_Assembly_Is_AnyCpu()
        {
            var asm = typeof(MSNumpress).Assembly;
            Assert.That(asm.GetName().ProcessorArchitecture,
                Is.EqualTo(System.Reflection.ProcessorArchitecture.MSIL),
                "MSNumpress assembly must be AnyCPU (MSIL), no x64 marker");
        }

        private static double ReadBeDouble(byte[] b)
        {
            var fp = new byte[8];
            for (int i = 0; i < 8; i++) fp[i] = BitConverter.IsLittleEndian ? b[7 - i] : b[i];
            return BitConverter.ToDouble(fp, 0);
        }

        // Drop a phantom extrapolated value at either end when it is farther from its anchor than
        // the adjacent value (the canonical numpress-linear decode can emit a leading and/or trailing
        // phantom; the true chunk is bounded by mz_chunk_start / mz_chunk_end).
        private static double[] AnchorAlign(double[] dec, double start, double end)
        {
            int lo = 0, hi = dec.Length;
            if (hi - lo >= 2 && Math.Abs(dec[lo] - start) > Math.Abs(dec[lo + 1] - start)) lo++;
            if (hi - lo >= 2 && Math.Abs(dec[hi - 1] - end) > Math.Abs(dec[hi - 2] - end)) hi--;
            return dec.Skip(lo).Take(hi - lo).ToArray();
        }

        private static string ResolvePython311()
        {
            foreach (var cand in new[] { "python3.11", "python3", "python" })
            {
                try
                {
                    var psi = new ProcessStartInfo(cand, "-c \"import pynumpress\"")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    using (var p = Process.Start(psi)) { p.WaitForExit(); if (p.ExitCode == 0) return cand; }
                }
                catch { }
            }
            return null;
        }

        private static string RunPython(string python, string code)
        {
            var psi = new ProcessStartInfo(python, "-c \"" + code.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using (var proc = Process.Start(psi))
            {
                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                Assert.That(proc.ExitCode, Is.EqualTo(0), $"python failed: {stderr}");
                return stdout.Trim();
            }
        }

        private static byte[] PyEncodeLinear(string python, double[] data, double fp)
        {
            var arr = string.Join(",", data.Select(d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            var code =
                "import pynumpress, numpy as np, json\n" +
                $"b=pynumpress.encode_linear(np.array([{arr}]), {fp.ToString("R", System.Globalization.CultureInfo.InvariantCulture)})\n" +
                "print(json.dumps([int(x) for x in bytes(b)]))\n";
            return JArray.Parse(RunPython(python, code)).Select(x => (byte)(int)x).ToArray();
        }

        private static double[] PyDecodeLinear(string python, byte[] bytes)
        {
            var arr = string.Join(",", bytes.Select(b => (int)b));
            var code =
                "import pynumpress, numpy as np, json\n" +
                $"d=pynumpress.decode_linear(np.array([{arr}], dtype=np.uint8))\n" +
                "print(json.dumps([float(x) for x in d]))\n";
            return JArray.Parse(RunPython(python, code)).Select(x => (double)x).ToArray();
        }

        private static double PyOptimalFp(string python, double[] data)
        {
            var arr = string.Join(",", data.Select(d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            var code =
                "import pynumpress, numpy as np\n" +
                $"print(repr(float(pynumpress.optimal_linear_fixed_point(np.array([{arr}])))))\n";
            return double.Parse(RunPython(python, code), System.Globalization.CultureInfo.InvariantCulture);
        }

        // --- Integration locks (small.RAW; pyarrow / validator gated with Assert.Ignore) ---

        private const string ReferenceChunkedArchive =
            "refs/mzPeak/small.chunked.mzpeak";

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, ReferenceChunkedArchive)))
                d = d.Parent;
            return d?.FullName;
        }

        private static string TestRawFile =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "small.RAW");

        private enum DataMode { Numpress, Lossless, Point }

        // Delta-chunk (--lossless) conversion; point=true selects the point layout.
        private static string Convert(bool point, out string dir) =>
            Convert(point ? DataMode.Point : DataMode.Lossless, out dir);

        private static string Convert(DataMode mode, out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            var input = new ParseInput(TestRawFile, null, dir, OutputFormat.MzPeak)
            {
                MzPeakPointLayout = mode == DataMode.Point,
                MzPeakNumpress = mode == DataMode.Numpress
            };
            RawFileParser.Parse(input);
            Assert.That(input.Errors, Is.EqualTo(0));
            var archive = Path.Combine(dir, "small.mzpeak");
            Assert.That(File.Exists(archive));
            return archive;
        }

        [Test]
        public void SchemaDiff_vs_Reference()
        {
            var root = RepoRoot();
            if (root == null) Assert.Ignore("reference archive not found");
            var archive = Convert(false, out var dir);
            try
            {
                var refArchive = Path.Combine(root, ReferenceChunkedArchive);
                var snippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "f = pq.read_table(r'{PARQUET}').schema.field('chunk').type\n" +
                    "out = {'names':[f.field(i).name for i in range(f.num_fields)],\n" +
                    "       'mz_item':str(f.field(3).type.value_field.type),\n" +
                    "       'int_item':str(f.field(5).type.value_field.type)}\n" +
                    "print(json.dumps(out))\n";
                var ours = PyArrow(archive, "spectra_data.parquet", snippet);
                var refs = PyArrow(refArchive, "spectra_data.parquet", snippet);

                Assert.That(ours["names"].Select(x => (string)x).ToArray(),
                    Is.EqualTo(refs["names"].Select(x => (string)x).ToArray()),
                    "chunk field names match the reference");
                Assert.That((string)ours["mz_item"], Is.EqualTo("double"));
                Assert.That((string)ours["int_item"], Is.EqualTo("float"));
                Assert.That((string)refs["mz_item"], Is.EqualTo("double"));
                Assert.That((string)refs["int_item"], Is.EqualTo("float"));

                var encSnippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "t = pq.read_table(r'{PARQUET}').column('chunk').combine_chunks()\n" +
                    "print(json.dumps({'enc':sorted(set(t.field('chunk_encoding').to_pylist()))}))\n";
                var ourEnc = PyArrow(archive, "spectra_data.parquet", encSnippet)["enc"].Select(x => (string)x).ToArray();
                Assert.That(ourEnc, Is.EqualTo(new[] { "MS:1003089" }), "chunk_encoding is the reference delta CURIE");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void Metadata_SchemaParity_vs_Reference()
        {
            var root = RepoRoot();
            if (root == null) Assert.Ignore("reference archive not found");
            var refArchive = Path.Combine(root, "refs/mzPeak/small.numpress.mzpeak");
            if (!File.Exists(refArchive)) Assert.Ignore("small.numpress.mzpeak not present");

            var archive = Convert(DataMode.Numpress, out var dir);
            try
            {
                var snippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "s = pq.read_table(r'{PARQUET}').schema\n" +
                    "out = {'spectrum':[f.name for f in s.field('spectrum').type],\n" +
                    "       'scan':[f.name for f in s.field('scan').type]}\n" +
                    "print(json.dumps(out))\n";
                var ours = PyArrow(archive, "spectra_metadata.parquet", snippet);
                var refs = PyArrow(refArchive, "spectra_metadata.parquet", snippet);

                foreach (var top in new[] { "spectrum", "scan" })
                {
                    var oc = ours[top].Select(x => (string)x).OrderBy(x => x).ToArray();
                    var rc = refs[top].Select(x => (string)x).OrderBy(x => x).ToArray();
                    Assert.That(oc, Is.EqualTo(rc),
                        $"{top} struct column name set must equal the reference small.numpress.mzpeak set");
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void Footer_ArrayIndex_Lock()
        {
            var root = RepoRoot();
            if (root == null) Assert.Ignore("reference archive not found");
            var chunked = Convert(false, out var cdir);
            var pointed = Convert(true, out var pdir);
            try
            {
                var footerSnippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "m = pq.read_metadata(r'{PARQUET}').metadata\n" +
                    "print(m[b'spectrum_array_index'].decode())\n";

                var ours = PyArrow(chunked, "spectra_data.parquet", footerSnippet);
                Assert.That((string)ours["prefix"], Is.EqualTo("chunk"));
                var entries = (JArray)ours["entries"];
                Assert.That(entries.Select(e => (string)e["buffer_format"]).ToArray(),
                    Is.EqualTo(new[] { "chunk_start", "chunk_end", "chunk_values", "chunk_encoding", "chunk_secondary" }));

                var refArchive = Path.Combine(root, ReferenceChunkedArchive);
                var refFooter = PyArrow(refArchive, "spectra_data.parquet", footerSnippet);
                var refEntries = (JArray)refFooter["entries"];
                var refTransforms = refEntries.Select(e => (string)e["transform"]).ToArray();
                Assert.That(entries.Select(e => (string)e["transform"]).ToArray(), Is.EqualTo(refTransforms),
                    "transform CURIEs match the reference footer verbatim");

                // m/z entries carry sorting_rank 0; the intensity entry omits it.
                for (int i = 0; i < 4; i++)
                    Assert.That((int)entries[i]["sorting_rank"], Is.EqualTo(0), $"m/z entry {i} sorting_rank 0");
                Assert.That(entries[4]["sorting_rank"].Type, Is.EqualTo(JTokenType.Null),
                    "intensity entry omits sorting_rank");

                var pointFooter = PyArrow(pointed, "spectra_data.parquet", footerSnippet);
                Assert.That((string)pointFooter["prefix"], Is.EqualTo("point"),
                    "--point mode retains the point array_index");
                Assert.That(((JArray)pointFooter["entries"]).Select(e => (string)e["buffer_format"]).ToArray(),
                    Is.EqualTo(new[] { "point", "point" }));
            }
            finally { Directory.Delete(cdir, true); Directory.Delete(pdir, true); }
        }

        [Test]
        public void BitwiseMultiset_Chunked_Equals_Point()
        {
            var chunked = Convert(false, out var cdir);
            var pointed = Convert(true, out var pdir);
            try
            {
                // Decode chunked m/z with the null-aware delta decode, build per-spectrum BITWISE keys
                // (DoubleToInt64Bits / SingleToInt32Bits), and compare the multisets to the point facet.
                var decodeSnippet =
                    "import pyarrow.parquet as pq, json, struct\n" +
                    "from collections import defaultdict\n" +
                    DdFunc +
                    "t = pq.read_table(r'{PARQUET}').column('chunk').combine_chunks()\n" +
                    "si=t.field('spectrum_index').to_pylist(); cs=t.field('mz_chunk_start').to_pylist()\n" +
                    "cv=t.field('mz_chunk_values').to_pylist(); inn=t.field('intensity').to_pylist()\n" +
                    "d=defaultdict(list)\n" +
                    "for i in range(len(si)):\n" +
                    "    dmz=dd(cs[i], cv[i] if cv[i] is not None else [])\n" +
                    "    for m,it in zip(dmz, inn[i]):\n" +
                    "        if m is None or it is None: continue\n" +
                    "        d[si[i]].append([struct.unpack('<q',struct.pack('<d',float(m)))[0], struct.unpack('<i',struct.pack('<f',float(it)))[0]])\n" +
                    "print(json.dumps({str(k):sorted(v) for k,v in d.items()}))\n";
                var chunkKeys = PyArrow(chunked, "spectra_data.parquet", decodeSnippet);

                var pointSnippet =
                    "import pyarrow.parquet as pq, json, struct\n" +
                    "from collections import defaultdict\n" +
                    "t = pq.read_table(r'{PARQUET}').column('point').combine_chunks()\n" +
                    "si=t.field('spectrum_index').to_pylist(); mz=t.field('mz').to_pylist(); inn=t.field('intensity').to_pylist()\n" +
                    "d=defaultdict(list)\n" +
                    "for s,m,it in zip(si,mz,inn):\n" +
                    "    d[s].append([struct.unpack('<q',struct.pack('<d',float(m)))[0], struct.unpack('<i',struct.pack('<f',float(it)))[0]])\n" +
                    "print(json.dumps({str(k):sorted(v) for k,v in d.items()}))\n";
                var pointKeys = PyArrow(pointed, "spectra_data.parquet", pointSnippet);

                Assert.That(chunkKeys.Properties().Select(p => p.Name).OrderBy(x => x),
                    Is.EqualTo(pointKeys.Properties().Select(p => p.Name).OrderBy(x => x)),
                    "same spectrum_index set");

                int mzMismatch = 0, intMismatch = 0;
                foreach (var prop in chunkKeys.Properties())
                {
                    var c = ((JArray)prop.Value).Select(a => ((long)a[0], (int)a[1])).OrderBy(x => x).ToArray();
                    var p = ((JArray)pointKeys[prop.Name]).Select(a => ((long)a[0], (int)a[1])).OrderBy(x => x).ToArray();
                    Assert.That(c.Length, Is.EqualTo(p.Length), $"spectrum {prop.Name} point count");
                    var cMz = c.Select(x => x.Item1).ToArray(); var pMz = p.Select(x => x.Item1).ToArray();
                    var cIn = c.Select(x => x.Item2).OrderBy(x => x).ToArray();
                    var pIn = p.Select(x => x.Item2).OrderBy(x => x).ToArray();
                    if (!cMz.SequenceEqual(pMz)) mzMismatch++;
                    if (!cIn.SequenceEqual(pIn)) intMismatch++;
                }
                // Intensity is never delta-encoded -> must be bitwise-identical.
                Assert.That(intMismatch, Is.EqualTo(0), "intensity is bitwise-identical chunked vs point");
                // f64 delta encode+reconstruct is bit-exact on real Thermo m/z, so the m/z multiset
                // matches bitwise with zero tolerance (exact / L1).
                Assert.That(mzMismatch, Is.EqualTo(0),
                    "m/z is BITWISE-identical chunked vs point (delta round-trip is bit-exact on Thermo m/z)");
            }
            finally { Directory.Delete(cdir, true); Directory.Delete(pdir, true); }
        }

        // The chunk decode snippet shared by the bitwise-equivalence locks: null-aware delta decode of
        // every chunk row across ALL row groups (pyarrow read_table merges row groups), emitting a
        // per-spectrum map of sorted [mz_bits, intensity_bits] pairs with null pairs dropped.
        private const string ChunkDecodeSnippet =
            "import pyarrow.parquet as pq, json, struct\n" +
            "from collections import defaultdict\n" +
            DdFunc +
            "t = pq.read_table(r'{PARQUET}').column('chunk').combine_chunks()\n" +
            "si=t.field('spectrum_index').to_pylist(); cs=t.field('mz_chunk_start').to_pylist()\n" +
            "cv=t.field('mz_chunk_values').to_pylist(); inn=t.field('intensity').to_pylist()\n" +
            "d=defaultdict(list)\n" +
            "for i in range(len(si)):\n" +
            "    dmz=dd(cs[i], cv[i] if cv[i] is not None else [])\n" +
            "    for m,it in zip(dmz, inn[i]):\n" +
            "        if m is None or it is None: continue\n" +
            "        d[si[i]].append([struct.unpack('<q',struct.pack('<d',float(m)))[0], struct.unpack('<i',struct.pack('<f',float(it)))[0]])\n" +
            "print(json.dumps({str(k):sorted(v) for k,v in d.items()}))\n";

        private static int RowGroupCount(string archive, string entry)
        {
            using (var ms = new MemoryStream(ReadEntry(archive, entry)))
            using (var reader = ParquetReader.CreateAsync(ms).Result)
                return reader.RowGroupCount;
        }

        [Test]
        public void ChunkFacet_MultiRowGroup_Equals_SingleRowGroup()
        {
            // Single-row-group chunked run (production cap): one row group for small.RAW.
            var single = Convert(false, out var sdir);
            try
            {
                Assert.That(RowGroupCount(single, "spectra_data.parquet"), Is.EqualTo(1),
                    "production cap yields a single chunk row group for small.RAW");
                var singleKeys = PyArrow(single, "spectra_data.parquet", ChunkDecodeSnippet);

                // Lowered-cap chunked run through the REAL writer flush path: forces >=2 chunk row groups.
                string mdir = null, multi = null;
                try
                {
                    MzPeakSpectrumWriter.TestRowGroupRowCap = 8;
                    multi = Convert(false, out mdir);
                    Assert.That(RowGroupCount(multi, "spectra_data.parquet"), Is.GreaterThan(1),
                        "lowered cap must drive the chunk list-column flush path into multiple row groups");

                    var multiKeys = PyArrow(multi, "spectra_data.parquet", ChunkDecodeSnippet);

                    Assert.That(multiKeys.Properties().Select(p => p.Name).OrderBy(x => x),
                        Is.EqualTo(singleKeys.Properties().Select(p => p.Name).OrderBy(x => x)),
                        "same spectrum_index set across caps");

                    foreach (var prop in singleKeys.Properties())
                    {
                        var s = ((JArray)prop.Value).Select(a => ((long)a[0], (int)a[1]))
                            .OrderBy(x => x).ToArray();
                        var m = ((JArray)multiKeys[prop.Name]).Select(a => ((long)a[0], (int)a[1]))
                            .OrderBy(x => x).ToArray();
                        Assert.That(m, Is.EqualTo(s),
                            $"spectrum {prop.Name}: (mz,intensity) multiset BITWISE-identical across caps");
                    }
                }
                finally
                {
                    MzPeakSpectrumWriter.TestRowGroupRowCap = null;
                    if (mdir != null) Directory.Delete(mdir, true);
                }
            }
            finally { Directory.Delete(sdir, true); }
        }

        [Test]
        public void ChunkFacet_ByteCap_MultiRowGroup_Equals_SingleRowGroup()
        {
            // Single-row-group chunked run (production caps): one row group for small.RAW.
            var single = Convert(false, out var sdir);
            try
            {
                Assert.That(RowGroupCount(single, "spectra_data.parquet"), Is.EqualTo(1),
                    "production caps yield a single chunk row group for small.RAW");
                var singleKeys = PyArrow(single, "spectra_data.parquet", ChunkDecodeSnippet);

                // Lowered BYTE cap (row cap left at production) through the REAL writer flush path: the
                // accumulated chunk-row bytes must drive >=2 row groups, proving the byte budget alone
                // splits fat chunk rows that the row-count cap would leave in one giant row group.
                string mdir = null, multi = null;
                try
                {
                    MzPeakSpectrumWriter.TestRowGroupByteCap = 4096;
                    multi = Convert(false, out mdir);
                    Assert.That(RowGroupCount(multi, "spectra_data.parquet"), Is.GreaterThan(1),
                        "lowered byte cap must split the chunk facet into multiple row groups");

                    var multiKeys = PyArrow(multi, "spectra_data.parquet", ChunkDecodeSnippet);

                    Assert.That(multiKeys.Properties().Select(p => p.Name).OrderBy(x => x),
                        Is.EqualTo(singleKeys.Properties().Select(p => p.Name).OrderBy(x => x)),
                        "same spectrum_index set across caps");

                    foreach (var prop in singleKeys.Properties())
                    {
                        var s = ((JArray)prop.Value).Select(a => ((long)a[0], (int)a[1]))
                            .OrderBy(x => x).ToArray();
                        var m = ((JArray)multiKeys[prop.Name]).Select(a => ((long)a[0], (int)a[1]))
                            .OrderBy(x => x).ToArray();
                        Assert.That(m, Is.EqualTo(s),
                            $"spectrum {prop.Name}: (mz,intensity) multiset BITWISE-identical across byte caps");
                    }
                }
                finally
                {
                    MzPeakSpectrumWriter.TestRowGroupByteCap = null;
                    if (mdir != null) Directory.Delete(mdir, true);
                }
            }
            finally { Directory.Delete(sdir, true); }
        }

        // Large raw file that previously produced one ~400 MB chunk row group pyarrow could not read.
        private const string LargeRawFile =
            "/Users/kohlbach/Claude/mzML2mzPeak/data/sdrf-examples/PXD011799/raw/" +
            "20170424_Lumos_RSLC3_Maurer_Hartl_UW_MFPL_shotgun_TMT1_TiO2_Fr2.raw";

        [Test]
        public void LargeFile_Chunked_Numpress_PyArrowReadable_MultiRowGroup()
        {
            if (!File.Exists(LargeRawFile))
                Assert.Ignore($"large raw file not present: {LargeRawFile}");
            var python = ResolvePython();
            if (python == null) Assert.Ignore("python3/pyarrow not available");

            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            var parquet = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".parquet");
            try
            {
                var input = new ParseInput(LargeRawFile, null, dir, OutputFormat.MzPeak)
                {
                    MzPeakPointLayout = false,
                    MzPeakNumpress = true
                };
                RawFileParser.Parse(input);
                Assert.That(input.Errors, Is.EqualTo(0));

                var archive = Directory.GetFiles(dir, "*.mzpeak").Single();
                File.WriteAllBytes(parquet, ReadEntry(archive, "spectra_data.parquet"));

                // Fully stream every batch (forces all row groups to be read) and report row-group count
                // and total rows. A regression reproduces as "Unexpected end of stream" here.
                var snippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "pf = pq.ParquetFile(r'{PARQUET}')\n" +
                    "rows = 0\n" +
                    "for b in pf.iter_batches(): rows += b.num_rows\n" +
                    "print(json.dumps({'row_groups': pf.num_row_groups, 'rows': rows}))\n";
                var code = snippet.Replace("{PARQUET}", parquet.Replace("\\", "\\\\"));
                var psi = new ProcessStartInfo(python, "-c \"" + code.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using (var proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    Assert.That(proc.ExitCode, Is.EqualTo(0), $"pyarrow could not read large-file spectra_data: {stderr}");
                    var o = JObject.Parse(stdout.Trim());
                    Assert.That((int)o["row_groups"], Is.GreaterThan(1),
                        "byte-aware flush must split the large-file chunk facet into multiple row groups");
                    Assert.That((long)o["rows"], Is.GreaterThan(0), "large-file spectra_data must have rows");
                }
            }
            finally
            {
                if (File.Exists(parquet)) File.Delete(parquet);
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Validator_Chunked_And_Point_ZeroErrors()
        {
            var validator = ResolveValidator();
            if (validator == null) Assert.Ignore("mzpeak-validate not available");

            foreach (var point in new[] { false, true })
            {
                var archive = Convert(point, out var dir);
                var jsonPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
                try
                {
                    var psi = new ProcessStartInfo(validator, $"\"{archive}\" --json \"{jsonPath}\"")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    using (var p = Process.Start(psi))
                    { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); }

                    Assert.That(File.Exists(jsonPath), "validator must emit a JSON report");
                    var report = JObject.Parse(File.ReadAllText(jsonPath));
                    var errors = ((JArray)report["findings"])
                        .Where(f => (string)f["level"] == "error")
                        .Select(f => (string)f["ruleId"]).ToArray();
                    Assert.That(errors, Is.Empty,
                        $"{(point ? "--point" : "chunked")} archive must validate 0 errors: {string.Join(",", errors)}");
                }
                finally
                {
                    if (File.Exists(jsonPath)) File.Delete(jsonPath);
                    Directory.Delete(dir, true);
                }
            }
        }

        [Test]
        public void Size_Chunked_Smaller_Than_Point()
        {
            var chunked = Convert(false, out var cdir);
            var pointed = Convert(true, out var pdir);
            try
            {
                long chunkSize = ReadEntry(chunked, "spectra_data.parquet").Length;
                long pointSize = ReadEntry(pointed, "spectra_data.parquet").Length;
                Assert.That(chunkSize, Is.LessThan(pointSize),
                    $"chunked spectra_data ({chunkSize}) must be smaller than point ({pointSize})");
            }
            finally { Directory.Delete(cdir, true); Directory.Delete(pdir, true); }
        }

        // --- Numpress (default) integration locks ---

        [Test]
        public void Numpress_Schema_Footer_NullValues_Encoding()
        {
            var archive = Convert(DataMode.Numpress, out var dir);
            try
            {
                var snippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "t = pq.read_table(r'{PARQUET}')\n" +
                    "ct = t.schema.field('chunk').type\n" +
                    "col = t.column('chunk').combine_chunks()\n" +
                    "cv = col.field('mz_chunk_values').to_pylist()\n" +
                    "out = {'names':[ct.field(i).name for i in range(ct.num_fields)],\n" +
                    "       'enc':sorted(set(col.field('chunk_encoding').to_pylist())),\n" +
                    "       'cv_null':sum(1 for x in cv if x is None),\n" +
                    "       'cv_present_empty':sum(1 for x in cv if x is not None and len(x)==0),\n" +
                    "       'rows':len(cv),\n" +
                    "       'npk_item':str(ct.field(6).type.value_field.type)}\n" +
                    "print(json.dumps(out))\n";
                var o = PyArrow(archive, "spectra_data.parquet", snippet);
                var names = o["names"].Select(x => (string)x).ToArray();
                Assert.That(names, Is.EqualTo(new[]
                {
                    "spectrum_index", "mz_chunk_start", "mz_chunk_end", "mz_chunk_values",
                    "chunk_encoding", "intensity", "mz_numpress_linear_bytes"
                }), "numpress chunk struct has 7 fields incl mz_numpress_linear_bytes");
                Assert.That(o["enc"].Select(x => (string)x).ToArray(), Is.EqualTo(new[] { "MS:1002312" }),
                    "chunk_encoding == MS:1002312");
                Assert.That((int)o["cv_null"], Is.EqualTo((int)o["rows"]),
                    "mz_chunk_values null_count == row count");
                Assert.That((int)o["cv_present_empty"], Is.EqualTo(0),
                    "mz_chunk_values has ZERO present/empty lists (genuinely null, not zero-length)");
                Assert.That((string)o["npk_item"], Is.EqualTo("uint8"), "numpress bytes item is uint8");

                var footerSnippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "m = pq.read_metadata(r'{PARQUET}').metadata\n" +
                    "print(m[b'spectrum_array_index'].decode())\n";
                var f = PyArrow(archive, "spectra_data.parquet", footerSnippet);
                Assert.That((string)f["prefix"], Is.EqualTo("chunk"));
                var entries = (JArray)f["entries"];
                Assert.That(entries.Select(e => (string)e["buffer_format"]).ToArray(),
                    Is.EqualTo(new[] { "chunk_start", "chunk_end", "chunk_values", "chunk_encoding", "chunk_secondary", "chunk_transform" }));
                var npk = entries.First(e => (string)e["path"] == "chunk.mz_numpress_linear_bytes");
                Assert.That((string)npk["buffer_format"], Is.EqualTo("chunk_transform"));
                Assert.That((string)npk["transform"], Is.EqualTo("MS:1002312"));
                Assert.That((int)npk["sorting_rank"], Is.EqualTo(0));
                var inten = entries.First(e => (string)e["path"] == "chunk.intensity");
                Assert.That((string)inten["buffer_format"], Is.EqualTo("chunk_secondary"));
                Assert.That((string)inten["transform"], Is.EqualTo("MS:1003902"));
                Assert.That(inten["sorting_rank"].Type, Is.EqualTo(JTokenType.Null), "intensity entry unchanged");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void Numpress_L2_Bound_And_Intensity_BitExact_vs_Lossless()
        {
            var numpress = Convert(DataMode.Numpress, out var ndir);
            var lossless = Convert(DataMode.Lossless, out var ldir);
            try
            {
                // Decode numpress m/z per chunk via OUR C# anchor-aligned codec (pyarrow only supplies the
                // raw bytes / anchors / intensity). Compare positionally to --lossless (bit-exact).
                var npSnippet =
                    "import pyarrow.parquet as pq, json, struct\n" +
                    "t = pq.read_table(r'{PARQUET}').column('chunk').combine_chunks()\n" +
                    "si=t.field('spectrum_index').to_pylist(); cs=t.field('mz_chunk_start').to_pylist()\n" +
                    "ce=t.field('mz_chunk_end').to_pylist(); nb=t.field('mz_numpress_linear_bytes').to_pylist()\n" +
                    "inn=t.field('intensity').to_pylist()\n" +
                    "rows=[]\n" +
                    "for i in range(len(si)):\n" +
                    "    rows.append({'si':si[i],'cs':cs[i],'ce':ce[i],'nb':[int(x) for x in bytes(nb[i])],\n" +
                    "                 'int':[struct.unpack('<i',struct.pack('<f',float(x)))[0] for x in inn[i]]})\n" +
                    "print(json.dumps(rows))\n";
                var npRows = PyArrowArray(numpress, "spectra_data.parquet", npSnippet);

                var lossSnippet =
                    "import pyarrow.parquet as pq, json, struct\n" +
                    DdFunc +
                    "from collections import defaultdict\n" +
                    "t = pq.read_table(r'{PARQUET}').column('chunk').combine_chunks()\n" +
                    "si=t.field('spectrum_index').to_pylist(); cs=t.field('mz_chunk_start').to_pylist()\n" +
                    "cv=t.field('mz_chunk_values').to_pylist(); inn=t.field('intensity').to_pylist()\n" +
                    "mz=defaultdict(list); it=defaultdict(list)\n" +
                    "for i in range(len(si)):\n" +
                    "    dmz=dd(cs[i], cv[i] if cv[i] is not None else [])\n" +
                    "    for m,v in zip(dmz, inn[i]):\n" +
                    "        if m is None or v is None: continue\n" +
                    "        mz[si[i]].append(m); it[si[i]].append(struct.unpack('<i',struct.pack('<f',float(v)))[0])\n" +
                    "print(json.dumps({'mz':{str(k):v for k,v in mz.items()},'it':{str(k):v for k,v in it.items()}}))\n";
                var loss = PyArrow(lossless, "spectra_data.parquet", lossSnippet);
                var lossMz = (JObject)loss["mz"];
                var lossIt = (JObject)loss["it"];

                // At least one chunk row must be emitted.
                Assert.That(npRows.Count, Is.GreaterThan(0), "numpress has at least one chunk row");

                long pointCount = ArchivePointCount(numpress);

                var npMz = new Dictionary<string, List<double>>();
                var npBound = new Dictionary<string, List<double>>();
                var npIt = new Dictionary<string, List<int>>();
                long totalCompared = 0;

                foreach (var row in npRows)
                {
                    double cs = (double)row["cs"], ce = (double)row["ce"];
                    var bytes = ((JArray)row["nb"]).Select(x => (byte)(int)x).ToArray();
                    var inten = ((JArray)row["int"]).Select(x => (int)x).ToList();

                    double fp = NpFixedPoint(bytes);
                    Assert.That(double.IsFinite(fp) && fp > 0.0, Is.True, "per-chunk fp finite and > 0");

                    var dec = MzPeakChunkCodec.NumpressDecode(cs, ce, bytes);
                    Assert.That(dec.Length, Is.EqualTo(inten.Count),
                        "decoded m/z length == chunk intensity length");

                    // This chunk's OWN half-step bound is 0.5/fp; a high-fp chunk is held to its own
                    // tighter bound. Allow a small relative slack for round-half quantization and the
                    // floating-point division on reconstruction.
                    double bound = (0.5 / fp) * (1.0 + 1e-6);
                    // start anchor (always); end anchor when >= 1 value; 1/2-value chunks guarded.
                    Assert.That(Math.Abs(dec[0] - cs), Is.LessThanOrEqualTo(bound), "start anchor within 0.5/fp");
                    Assert.That(Math.Abs(dec[dec.Length - 1] - ce), Is.LessThanOrEqualTo(bound), "end anchor within 0.5/fp");

                    var key = ((ulong)(double)row["si"]).ToString();
                    if (!npMz.ContainsKey(key))
                    {
                        npMz[key] = new List<double>();
                        npBound[key] = new List<double>();
                        npIt[key] = new List<int>();
                    }
                    foreach (var m in dec) { npMz[key].Add(m); npBound[key].Add(bound); }
                    npIt[key].AddRange(inten);

                    totalCompared += dec.Length;
                }

                Assert.That(totalCompared, Is.EqualTo(pointCount),
                    "total compared m/z == spectrum_data_point_count (no chunk skipped, no value dropped)");

                Assert.That(npMz.Keys.OrderBy(x => x), Is.EqualTo(lossMz.Properties().Select(p => p.Name).OrderBy(x => x)),
                    "same spectrum_index set as --lossless");

                int intMismatch = 0;
                double worstRel = 0;
                foreach (var kv in npMz)
                {
                    var lm = ((JArray)lossMz[kv.Key]).Select(x => (double)x).ToArray();
                    var li = ((JArray)lossIt[kv.Key]).Select(x => (int)x).ToArray();
                    var bounds = npBound[kv.Key];
                    Assert.That(kv.Value.Count, Is.EqualTo(lm.Length),
                        $"spectrum {kv.Key} decoded length == lossless length");
                    for (int i = 0; i < lm.Length; i++)
                    {
                        double err = Math.Abs(kv.Value[i] - lm[i]);
                        // Each decoded m/z is held to ITS OWN chunk's 0.5/fp bound against the
                        // same-position lossless m/z, so a tight high-fp chunk cannot hide behind a
                        // loose low-fp chunk's bound.
                        Assert.That(err, Is.LessThanOrEqualTo(bounds[i]),
                            $"spectrum {kv.Key} value {i}: numpress m/z within its own chunk 0.5/fp bound " +
                            $"({err} > {bounds[i]})");
                        worstRel = Math.Max(worstRel, err / bounds[i]);
                    }
                    if (!npIt[kv.Key].SequenceEqual(li)) intMismatch++;
                }

                Assert.That(intMismatch, Is.EqualTo(0), "intensity bit-exact f32 vs --lossless");
                Assert.That(worstRel, Is.LessThanOrEqualTo(1.0),
                    $"every numpress m/z within its own per-chunk bound (worst ratio {worstRel})");
            }
            finally { Directory.Delete(ndir, true); Directory.Delete(ldir, true); }
        }

        [Test]
        public void Numpress_Smaller_Than_Delta_Chunked()
        {
            var numpress = Convert(DataMode.Numpress, out var ndir);
            var lossless = Convert(DataMode.Lossless, out var ldir);
            try
            {
                long np = ReadEntry(numpress, "spectra_data.parquet").Length;
                long delta = ReadEntry(lossless, "spectra_data.parquet").Length;
                Assert.That(np, Is.LessThan(delta),
                    $"numpress spectra_data ({np}) must be smaller than delta-chunked ({delta})");
            }
            finally { Directory.Delete(ndir, true); Directory.Delete(ldir, true); }
        }

        [Test]
        public void Validator_ThreeModes_ZeroErrors()
        {
            var validator = ResolveValidator();
            if (validator == null) Assert.Ignore("mzpeak-validate not available");

            foreach (var mode in new[] { DataMode.Numpress, DataMode.Lossless, DataMode.Point })
            {
                var archive = Convert(mode, out var dir);
                var jsonPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
                try
                {
                    var psi = new ProcessStartInfo(validator, $"\"{archive}\" --json \"{jsonPath}\"")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    using (var p = Process.Start(psi))
                    { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); }

                    Assert.That(File.Exists(jsonPath), "validator must emit a JSON report");
                    var report = JObject.Parse(File.ReadAllText(jsonPath));
                    var errors = ((JArray)report["findings"])
                        .Where(f => (string)f["level"] == "error")
                        .Select(f => (string)f["ruleId"]).ToArray();
                    Assert.That(errors, Is.Empty, $"{mode} archive must validate 0 errors: {string.Join(",", errors)}");
                }
                finally
                {
                    if (File.Exists(jsonPath)) File.Delete(jsonPath);
                    Directory.Delete(dir, true);
                }
            }
        }

        private static double NpFixedPoint(byte[] b)
        {
            var fp = new byte[8];
            for (int i = 0; i < 8; i++) fp[i] = BitConverter.IsLittleEndian ? b[7 - i] : b[i];
            return BitConverter.ToDouble(fp, 0);
        }

        private static long ArchivePointCount(string archive)
        {
            var snippet =
                "import pyarrow.parquet as pq\n" +
                "m = pq.read_metadata(r'{PARQUET}').metadata\n" +
                "print('{\\\"n\\\":' + m[b'spectrum_data_point_count'].decode() + '}')\n";
            return (long)PyArrow(archive, "spectra_data.parquet", snippet)["n"];
        }

        // PyArrow variant that returns a JSON array (the standard PyArrow asserts a JSON object).
        [Test]
        public void Peaks_And_Chromatograms_Unchanged()
        {
            var archive = Convert(false, out var dir);
            try
            {
                using (var ms = new MemoryStream(ReadEntry(archive, "spectra_peaks.parquet")))
                using (var reader = ParquetReader.CreateAsync(ms).Result)
                {
                    var schema = reader.Schema;
                    Assert.That(schema.GetDataFields().Any(d => d.Path.ToString() == "point/spectrum_index"),
                        "spectra_peaks stays the point struct in chunked mode");
                    Assert.That(schema.GetDataFields().Any(d => d.Path.ToString() == "point/mz"));
                    Assert.That(schema.GetDataFields().Any(d => d.Path.ToString() == "point/intensity"));
                }

                using (var ms = new MemoryStream(ReadEntry(archive, "chromatograms_data.parquet")))
                using (var reader = ParquetReader.CreateAsync(ms).Result)
                {
                    var schema = reader.Schema;
                    Assert.That(schema.GetDataFields().Any(d => d.Path.ToString() == "point/chromatogram_index"),
                        "chromatograms_data stays the point struct in chunked mode by design");
                    Assert.That(schema.GetDataFields().Any(d => d.Path.ToString() == "point/time"));
                    Assert.That(schema.GetDataFields().Any(d => d.Path.ToString() == "point/ms_level"));
                }
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
