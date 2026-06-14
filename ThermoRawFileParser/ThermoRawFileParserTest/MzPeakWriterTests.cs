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
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.RawFileReader;
using ThermoRawFileParser;
using ThermoRawFileParser.Writer;

namespace ThermoRawFileParserTest
{
    [TestFixture]
    public class MzPeakWriterTests
    {
        private static string TestRawFile =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "small.RAW");

        private sealed class Facet
        {
            public ulong[] SpectrumIndex;
            public double[] Mz;
            public float[] Intensity;
            public int SpectrumCount;
            public int PointCount;
        }

        private static string Convert(ParseInput parseInput, out string archive)
        {
            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            parseInput.OutputDirectory = dir;
            RawFileParser.Parse(parseInput);
            Assert.That(parseInput.Errors, Is.EqualTo(0));
            archive = Path.Combine(dir, "small.mzpeak");
            Assert.That(File.Exists(archive));
            return dir;
        }

        private static byte[] ReadEntry(string archive, string name)
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                var entry = zip.GetEntry(name);
                if (entry == null) return null;
                using (var s = entry.Open())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        private static Facet ReadPointFacet(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var reader = ParquetReader.CreateAsync(ms).Result)
            {
                var schema = reader.Schema;
                var rg = reader.OpenRowGroupReader(0);
                var idx = rg.ReadColumnAsync(Leaf(schema, "point/spectrum_index")).Result.Data
                    .Cast<ulong>().ToArray();
                var mz = rg.ReadColumnAsync(Leaf(schema, "point/mz")).Result.Data
                    .Cast<double>().ToArray();
                var inten = rg.ReadColumnAsync(Leaf(schema, "point/intensity")).Result.Data
                    .Cast<float>().ToArray();
                var meta = reader.CustomMetadata;
                return new Facet
                {
                    SpectrumIndex = idx,
                    Mz = mz,
                    Intensity = inten,
                    SpectrumCount = int.Parse(meta["spectrum_count"]),
                    PointCount = int.Parse(meta["spectrum_data_point_count"])
                };
            }
        }

        private static DataField Leaf(ParquetSchema schema, string path)
        {
            return schema.GetDataFields().First(d => d.Path.ToString() == path);
        }

        private sealed class ChromFacet
        {
            public ulong[] ChromatogramIndex;
            public double[] Time;
            public float[] Intensity;
            public long[] MsLevel;
            public string TicSource;
            public int PointCount;
        }

        private static ChromFacet ReadChromFacet(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var reader = ParquetReader.CreateAsync(ms).Result)
            {
                var schema = reader.Schema;
                var rg = reader.OpenRowGroupReader(0);
                var idx = rg.ReadColumnAsync(Leaf(schema, "point/chromatogram_index")).Result.Data
                    .Cast<ulong>().ToArray();
                var time = rg.ReadColumnAsync(Leaf(schema, "point/time")).Result.Data
                    .Cast<double>().ToArray();
                var inten = rg.ReadColumnAsync(Leaf(schema, "point/intensity")).Result.Data
                    .Cast<float>().ToArray();
                var lvl = rg.ReadColumnAsync(Leaf(schema, "point/ms_level")).Result.Data
                    .Cast<long>().ToArray();
                var meta = reader.CustomMetadata;
                return new ChromFacet
                {
                    ChromatogramIndex = idx,
                    Time = time,
                    Intensity = inten,
                    MsLevel = lvl,
                    TicSource = meta["chromatogram_tic_source"],
                    PointCount = int.Parse(meta["chromatogram_data_point_count"])
                };
            }
        }

        // Runs a pyarrow snippet against an arbitrary parquet entry of the archive (the {PARQUET}
        // token is replaced with the temp file path), returning the printed JSON object.
        private static JObject PyArrowEntry(string archive, string entry, string snippet)
        {
            var python = ResolvePython();
            if (python == null) Assert.Ignore("python3/pyarrow not available");

            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".parquet");
            File.WriteAllBytes(path, ReadEntry(archive, entry));
            try
            {
                var code = snippet.Replace("{PARQUET}", path.Replace("\\", "\\\\"));
                var psi = new ProcessStartInfo(python, "-c \"" + code.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using (var proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    Assert.That(proc.ExitCode, Is.EqualTo(0), $"pyarrow failed: {stderr}");
                    return JObject.Parse(stdout.Trim());
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void SpectraData_Ascending_And_MultisetPreserved()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var facet = ReadPointFacet(ReadEntry(archive, "spectra_data.parquet"));

                Assert.That(facet.Mz.Length, Is.EqualTo(facet.PointCount));
                Assert.That(facet.Mz.Length, Is.EqualTo(facet.SpectrumIndex.Length));
                Assert.That(facet.Intensity.Length, Is.EqualTo(facet.PointCount));

                for (int i = 1; i < facet.SpectrumIndex.Length; i++)
                {
                    if (facet.SpectrumIndex[i] == facet.SpectrumIndex[i - 1])
                        Assert.That(facet.Mz[i], Is.GreaterThanOrEqualTo(facet.Mz[i - 1]),
                            $"spectrum {facet.SpectrumIndex[i]} not non-decreasing at {i}");
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void SpectraData_LeafTypes_Are_Double_And_Float()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                using (var ms = new MemoryStream(ReadEntry(archive, "spectra_data.parquet")))
                using (var reader = ParquetReader.CreateAsync(ms).Result)
                {
                    var schema = reader.Schema;
                    Assert.That(Leaf(schema, "point/mz").ClrType, Is.EqualTo(typeof(double)));
                    Assert.That(Leaf(schema, "point/intensity").ClrType, Is.EqualTo(typeof(float)));
                    Assert.That(Leaf(schema, "point/spectrum_index").ClrType, Is.EqualTo(typeof(ulong)));
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void MsLevelFilter_Excludes_FilteredOut_Spectra()
        {
            var fullInput = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var fullDir = Convert(fullInput, out var fullArchive);
            int fullSpectra, fullRows;
            try
            {
                var f = ReadPointFacet(ReadEntry(fullArchive, "spectra_data.parquet"));
                fullSpectra = f.SpectrumCount;
                fullRows = f.PointCount;
            }
            finally
            {
                Directory.Delete(fullDir, true);
            }

            var ms2Input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak)
            {
                MsLevel = new HashSet<int> { 2 }
            };
            var ms2Dir = Convert(ms2Input, out var ms2Archive);
            try
            {
                var f = ReadPointFacet(ReadEntry(ms2Archive, "spectra_data.parquet"));
                Assert.That(f.SpectrumCount, Is.GreaterThan(0));
                Assert.That(f.SpectrumCount, Is.LessThan(fullSpectra),
                    "MS2-only run must contain fewer spectra than the unfiltered run");
                Assert.That(f.PointCount, Is.LessThan(fullRows),
                    "MS2-only run must contain fewer rows than the unfiltered run");

                var meta = ReadEntry(ms2Archive, "spectra_metadata.parquet");
                var mf = ReadMetadataIndices(meta);
                Assert.That(mf.Length, Is.EqualTo(f.SpectrumCount));
            }
            finally
            {
                Directory.Delete(ms2Dir, true);
            }
        }

        private static ulong[] ReadMetadataIndices(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var reader = ParquetReader.CreateAsync(ms).Result)
            {
                var schema = reader.Schema;
                var rg = reader.OpenRowGroupReader(0);
                return rg.ReadColumnAsync(Leaf(schema, "spectrum/index")).Result.Data
                    .Cast<ulong>().ToArray();
            }
        }

        [Test]
        public void Metadata_Covers_Identical_SpectrumSet_As_Data()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var data = ReadPointFacet(ReadEntry(archive, "spectra_data.parquet"));
                var metaIdx = ReadMetadataIndices(ReadEntry(archive, "spectra_metadata.parquet"));

                int distinct = data.SpectrumIndex.Distinct().Count();
                Assert.That(metaIdx.Length, Is.EqualTo(data.SpectrumCount));
                Assert.That(distinct, Is.EqualTo(data.SpectrumCount));
                Assert.That(metaIdx.OrderBy(x => x).ToArray(),
                    Is.EqualTo(Enumerable.Range(0, data.SpectrumCount).Select(i => (ulong)i).ToArray()));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static int CountProfileWithCentroidStreamScans()
        {
            using (var raw = RawFileReaderFactory.ReadFile(TestRawFile))
            {
                raw.SelectInstrument(Device.MS, 1);
                int first = raw.RunHeaderEx.FirstSpectrum;
                int last = raw.RunHeaderEx.LastSpectrum;
                int qualifying = 0;
                for (int scan = first; scan <= last; scan++)
                {
                    var scanEvent = raw.GetScanEventForScanNumber(scan);
                    if (scanEvent.ScanData == ScanDataType.Profile && Scan.FromFile(raw, scan).HasCentroidStream)
                        qualifying++;
                }
                return qualifying;
            }
        }

        [Test]
        public void Peaks_Routes_Only_Profile_Scans_With_CentroidStream_NoDuplication()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var peaksBytes = ReadEntry(archive, "spectra_peaks.parquet");
                Assert.That(peaksBytes, Is.Not.Null, "small.RAW must produce spectra_peaks.parquet");

                var indexJson = JObject.Parse(
                    System.Text.Encoding.UTF8.GetString(ReadEntry(archive, "mzpeak_index.json")));
                var peaksEntry = ((JArray)indexJson["files"])
                    .FirstOrDefault(f => (string)f["name"] == "spectra_peaks.parquet");
                Assert.That(peaksEntry, Is.Not.Null, "index must list spectra_peaks when the facet is written");
                Assert.That((string)peaksEntry["data_kind"], Is.EqualTo("peaks"));

                var data = ReadPointFacet(ReadEntry(archive, "spectra_data.parquet"));
                var peaks = ReadPointFacet(peaksBytes);

                Assert.That(peaks.Mz.Length, Is.EqualTo(peaks.PointCount));
                var peakSpectra = peaks.SpectrumIndex.Distinct().ToHashSet();
                Assert.That(peakSpectra.Count, Is.EqualTo(peaks.SpectrumCount),
                    "spectra_peaks must carry one ordinal per qualifying scan, no duplication");
                Assert.That(peakSpectra.Count, Is.GreaterThan(0), "small.RAW has profile scans with a centroid stream");

                var dataSpectra = data.SpectrumIndex.Distinct().ToHashSet();
                Assert.That(peakSpectra.IsSubsetOf(dataSpectra) && !peakSpectra.SetEquals(dataSpectra), Is.True,
                    "peaks ordinals must be a strict subset of spectra_data ordinals (centroid-only scans absent)");

                int qualifying = CountProfileWithCentroidStreamScans();
                Assert.That(peakSpectra.Count, Is.LessThanOrEqualTo(qualifying),
                    "no scan outside the profile+centroid-stream set may appear in spectra_peaks");
                Assert.That(qualifying, Is.GreaterThan(0));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static string ResolvePython()
        {
            foreach (var cand in new[] { "python3", "python" })
            {
                try
                {
                    var psi = new ProcessStartInfo(cand, "-c \"import pyarrow\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    using (var p = Process.Start(psi))
                    {
                        p.WaitForExit();
                        if (p.ExitCode == 0) return cand;
                    }
                }
                catch { }
            }
            return null;
        }

        // Writes the metadata parquet to a temp file and runs a python pyarrow snippet that prints
        // a single JSON object to stdout. The snippet sees the file path via the {PARQUET} token.
        private static JObject PyArrowMetadata(string archive, string snippet)
        {
            var python = ResolvePython();
            if (python == null) Assert.Ignore("python3/pyarrow not available");

            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".parquet");
            File.WriteAllBytes(path, ReadEntry(archive, "spectra_metadata.parquet"));
            try
            {
                var code = snippet.Replace("{PARQUET}", path.Replace("\\", "\\\\"));
                var psi = new ProcessStartInfo(python, "-c \"" + code.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using (var proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    Assert.That(proc.ExitCode, Is.EqualTo(0), $"pyarrow failed: {stderr}");
                    return JObject.Parse(stdout.Trim());
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void Metadata_Scan_And_Spectrum_Facets_Shape_And_Values()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var snippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "t = pq.read_table(r'{PARQUET}')\n" +
                    "d = t.to_pylist()\n" +
                    "N = len(d)\n" +
                    "sp = [f.name for f in t.schema.field('spectrum').type]\n" +
                    "sc = [f.name for f in t.schema.field('scan').type]\n" +
                    "out = {}\n" +
                    "out['rows'] = N\n" +
                    "out['spectrum_fields'] = sp\n" +
                    "out['scan_fields'] = sc\n" +
                    "out['scan_link_ok'] = all(d[i]['scan']['source_index']==i and d[i]['scan']['scan_index']==i for i in range(N))\n" +
                    "out['polarities'] = sorted(set(int(d[i]['spectrum']['MS_1000465_scan_polarity']) for i in range(N)))\n" +
                    "out['reprs'] = sorted(set(d[i]['spectrum']['MS_1000525_spectrum_representation'] for i in range(N)))\n" +
                    "out['types'] = sorted(set(d[i]['spectrum']['MS_1000559_spectrum_type'] for i in range(N)))\n" +
                    "out['ndp_all_present'] = all(d[i]['spectrum']['MS_1003060_number_of_data_points'] is not None for i in range(N))\n" +
                    "out['npk_present'] = sum(1 for i in range(N) if d[i]['spectrum']['MS_1003059_number_of_peaks'] is not None)\n" +
                    "out['npk_null'] = sum(1 for i in range(N) if d[i]['spectrum']['MS_1003059_number_of_peaks'] is None)\n" +
                    "out['npk_zero'] = any(d[i]['spectrum']['MS_1003059_number_of_peaks']==0 for i in range(N) if d[i]['spectrum']['MS_1003059_number_of_peaks'] is not None)\n" +
                    "out['sw_path'] = str(t.schema.field('scan').type.field('scan_windows').type)\n" +
                    "out['param_path'] = str(t.schema.field('spectrum').type.field('parameters').type)\n" +
                    "out['polarity_type'] = str(t.schema.field('spectrum').type.field('MS_1000465_scan_polarity').type)\n" +
                    "out['scan_start_type'] = str(t.schema.field('scan').type.field('MS_1000016_scan_start_time_unit_UO_0000031').type)\n" +
                    "out['cfg_refs'] = sorted(set(int(d[i]['scan']['instrument_configuration_ref']) for i in range(N)))\n" +
                    "print(json.dumps(out))\n";
                var o = PyArrowMetadata(archive, snippet);

                Assert.That((int)o["rows"], Is.EqualTo(48));
                Assert.That((bool)o["scan_link_ok"], Is.True);

                var spectrumFields = o["spectrum_fields"].Select(x => (string)x).ToArray();
                Assert.That(spectrumFields, Does.Contain("MS_1000511_ms_level"));
                Assert.That(spectrumFields, Does.Contain("MS_1000465_scan_polarity"));
                Assert.That(spectrumFields, Does.Contain("MS_1000525_spectrum_representation"));
                Assert.That(spectrumFields, Does.Contain("MS_1000559_spectrum_type"));
                Assert.That(spectrumFields, Does.Contain("MS_1000528_lowest_observed_mz_unit_MS_1000040"));
                Assert.That(spectrumFields, Does.Contain("MS_1000527_highest_observed_mz_unit_MS_1000040"));
                Assert.That(spectrumFields, Does.Contain("MS_1003060_number_of_data_points"));
                Assert.That(spectrumFields, Does.Contain("MS_1003059_number_of_peaks"));
                Assert.That(spectrumFields, Does.Contain("MS_1000504_base_peak_mz_unit_MS_1000040"));
                Assert.That(spectrumFields, Does.Contain("MS_1000505_base_peak_intensity_unit_MS_1000131"));
                Assert.That(spectrumFields, Does.Contain("MS_1000285_total_ion_current_unit_MS_1000131"));

                var scanFields = o["scan_fields"].Select(x => (string)x).ToArray();
                Assert.That(scanFields, Does.Contain("source_index"));
                Assert.That(scanFields, Does.Contain("scan_index"));
                Assert.That(scanFields, Does.Contain("MS_1000016_scan_start_time_unit_UO_0000031"));
                Assert.That(scanFields, Does.Contain("MS_1000512_filter_string"));
                Assert.That(scanFields, Does.Contain("MS_1000927_ion_injection_time_unit_UO_0000028"));
                Assert.That(scanFields, Does.Contain("instrument_configuration_ref"));

                // polarity int8 sign in {-1,1}; representation/type CURIE strings.
                Assert.That(o["polarity_type"].ToString(), Is.EqualTo("int8"));
                foreach (var p in o["polarities"]) Assert.That(Math.Abs((int)p), Is.EqualTo(1));
                foreach (var r in o["reprs"]) Assert.That((string)r, Does.Match(@"^MS:\d+$"));
                foreach (var ty in o["types"]) Assert.That((string)ty, Does.Match(@"^MS:\d+$"));

                // scan_start_time raw float32 minutes.
                Assert.That(o["scan_start_type"].ToString(), Is.EqualTo("float"));

                // count discipline: data_points always present; peaks NULL where unwritten, never 0.
                Assert.That((bool)o["ndp_all_present"], Is.True);
                Assert.That((int)o["npk_present"], Is.GreaterThan(0));
                Assert.That((int)o["npk_null"], Is.GreaterThan(0));
                Assert.That((bool)o["npk_zero"], Is.False, "number_of_peaks must be NULL, never 0, when no peaks written");

                // list element named item: paths contain "item:".
                Assert.That(o["sw_path"].ToString(), Does.Contain("item:"));
                Assert.That(o["param_path"].ToString(), Does.Contain("item:"));

                // two distinct analyzers -> config refs {0,1}.
                Assert.That(o["cfg_refs"].Select(x => (int)x).ToArray(), Is.EqualTo(new[] { 0, 1 }));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Metadata_Precursor_And_SelectedIon_NullPadded_And_Linked()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var snippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "t = pq.read_table(r'{PARQUET}')\n" +
                    "d = t.to_pylist()\n" +
                    "N = len(d)\n" +
                    "pp = [d[i]['precursor'] is not None and d[i]['precursor']['source_index'] is not None for i in range(N)]\n" +
                    "M = sum(pp)\n" +
                    "out = {}\n" +
                    "out['N'] = N\nout['M'] = M\n" +
                    "out['msn_ordinals'] = sorted(int(d[i]['spectrum']['index']) for i in range(N) if d[i]['spectrum']['MS_1000511_ms_level'] >= 2)\n" +
                    "out['msn_count'] = sum(1 for i in range(N) if d[i]['spectrum']['MS_1000511_ms_level'] >= 2)\n" +
                    "out['prec_pattern'] = ''.join('1' if x else '0' for x in pp)\n" +
                    "out['srcs'] = [d[i]['precursor']['source_index'] for i in range(M)]\n" +
                    "out['pidx'] = [d[i]['precursor']['precursor_index'] for i in range(M)]\n" +
                    "out['no_swap'] = all(d[i]['precursor']['source_index']!=d[i]['precursor']['precursor_index'] for i in range(M) if d[i]['precursor']['precursor_index'] is not None)\n" +
                    "out['sel_src_mirror'] = all(d[i]['selected_ion']['source_index']==d[i]['precursor']['source_index'] for i in range(M))\n" +
                    "out['sel_pidx_mirror'] = all((d[i]['selected_ion']['precursor_index'] is None) == (d[i]['precursor']['precursor_index'] is None) and (d[i]['precursor']['precursor_index'] is None or d[i]['selected_ion']['precursor_index']==d[i]['precursor']['precursor_index']) for i in range(M))\n" +
                    "out['sel_null_tail'] = all(d[i]['selected_ion'] is None or d[i]['selected_ion']['source_index'] is None for i in range(M, N))\n" +
                    "out['prec_null_tail'] = all(d[i]['precursor'] is None or d[i]['precursor']['source_index'] is None for i in range(M, N))\n" +
                    "ap = d[0]['precursor']['activation']['parameters']\n" +
                    "out['row0_act_acc'] = [e['accession'] for e in ap]\n" +
                    "out['row0_ce_unit'] = [e['unit'] for e in ap if e['accession']=='MS:1000045']\n" +
                    "out['ms1_ordinals'] = sorted(set(i for i in range(N) if d[i]['spectrum']['MS_1000511_ms_level']==1))\n" +
                    "print(json.dumps(out))\n";
                var o = PyArrowMetadata(archive, snippet);

                int n = (int)o["N"], m = (int)o["M"];
                Assert.That(m, Is.EqualTo((int)o["msn_count"]), "precursor count == MSn spectrum count");
                Assert.That((string)o["prec_pattern"], Is.EqualTo(new string('1', m) + new string('0', n - m)),
                    "precursor present on rows 0..M-1, null on M..N-1");
                Assert.That((bool)o["prec_null_tail"], Is.True);
                Assert.That((bool)o["sel_null_tail"], Is.True);

                var srcs = o["srcs"].Select(x => (ulong)x).ToArray();
                Assert.That(srcs, Is.EqualTo(srcs.OrderBy(x => x).ToArray()), "MSn ordinals ascending");

                // EXACT set equality: the precursor source_index set IS the MSn-ordinal set derived from
                // spectrum.ms_level >= 2 -- no extra precursor rows, none missing.
                var msnOrdinals = o["msn_ordinals"].Select(x => (ulong)x).ToHashSet();
                Assert.That(srcs.ToHashSet(), Is.EquivalentTo(msnOrdinals),
                    "precursor.source_index set must equal the ms_level>=2 ordinal set exactly");

                Assert.That((bool)o["no_swap"], Is.True, "precursor_index != source_index");
                Assert.That((bool)o["sel_src_mirror"], Is.True,
                    "selected_ion.source_index == precursor.source_index per row");
                Assert.That((bool)o["sel_pidx_mirror"], Is.True,
                    "selected_ion.precursor_index mirrors precursor.precursor_index per row (null-for-null)");

                var ms1 = o["ms1_ordinals"].Select(x => (ulong)x).ToHashSet();
                foreach (var pi in o["pidx"])
                {
                    if (pi.Type == JTokenType.Null) continue;
                    Assert.That(ms1, Does.Contain((ulong)pi), "precursor_index must be an MS1 ordinal");
                }

                Assert.That(o["row0_act_acc"].Select(x => (string)x).ToArray(), Does.Contain("MS:1000133"),
                    "small.RAW activation is CID MS:1000133");
                Assert.That(o["row0_ce_unit"].Select(x => (string)x).ToArray(), Does.Contain("UO:0000266"),
                    "collision energy carries unit UO:0000266");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Metadata_MS2Only_KeepsPrecursor_NullParent_NoSwap()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak)
            {
                MsLevel = new HashSet<int> { 2 }
            };
            var dir = Convert(input, out var archive);
            try
            {
                var snippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "t = pq.read_table(r'{PARQUET}')\n" +
                    "d = t.to_pylist()\n" +
                    "N = len(d)\n" +
                    "pp = [d[i]['precursor'] is not None and d[i]['precursor']['source_index'] is not None for i in range(N)]\n" +
                    "M = sum(pp)\n" +
                    "out = {}\nout['N']=N\nout['M']=M\n" +
                    "out['all_pidx_null'] = all(d[i]['precursor']['precursor_index'] is None for i in range(M))\n" +
                    "out['all_present'] = M==N\n" +
                    "print(json.dumps(out))\n";
                var o = PyArrowMetadata(archive, snippet);

                Assert.That((int)o["M"], Is.GreaterThan(0), "MS2-only run still emits precursor entries");
                Assert.That((bool)o["all_present"], Is.True, "every emitted spectrum is an MSn -> precursor on all rows");
                Assert.That((bool)o["all_pidx_null"], Is.True,
                    "parent MS1 filtered out -> precursor_index null, entry kept, no swap");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // Every MS_/UO_/IMS_ CV code embedded in any column name across the metadata schema, as the
        // validator's cv_list_declared rule collects them.
        private static HashSet<string> CollectedCvCodes(byte[] metaBytes)
        {
            var codes = new HashSet<string>();
            using (var ms = new MemoryStream(metaBytes))
            using (var reader = ParquetReader.CreateAsync(ms).Result)
            {
                foreach (var d in reader.Schema.GetDataFields())
                {
                    foreach (System.Text.RegularExpressions.Match mch in
                        System.Text.RegularExpressions.Regex.Matches(d.Path.ToString(), @"(MS|UO|IMS)_\d+"))
                    {
                        codes.Add(mch.Groups[1].Value);
                    }
                }
            }
            return codes;
        }

        // Recursively walk an arbitrary JSON tree and collect the ontology prefix of every CURIE that
        // appears as a value of an "accession" or "unit" key (e.g. "MS:1000133" -> "MS"). This reaches
        // every PARAM list element, component, file_description, run, and nested block alike.
        private static void CollectCuriePrefixes(JToken node, HashSet<string> into)
        {
            if (node is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    if ((prop.Name == "accession" || prop.Name == "unit") &&
                        prop.Value.Type == JTokenType.String)
                    {
                        var mch = System.Text.RegularExpressions.Regex.Match(
                            (string)prop.Value, @"^(MS|UO|IMS):\d+$");
                        if (mch.Success) into.Add(mch.Groups[1].Value);
                    }
                    CollectCuriePrefixes(prop.Value, into);
                }
            }
            else if (node is JArray arr)
            {
                foreach (var item in arr) CollectCuriePrefixes(item, into);
            }
        }

        // The authoritative collected set: every CURIE prefix used in any metadata block, gathered from
        // BOTH the schema column names AND recursively from every accession/unit in the JSON metadata.
        private static HashSet<string> CollectAllCvPrefixes(byte[] metaBytes, JObject metadataBlocks)
        {
            var codes = CollectedCvCodes(metaBytes);
            CollectCuriePrefixes(metadataBlocks, codes);
            return codes;
        }

        private static Dictionary<string, string> FooterKv(byte[] metaBytes)
        {
            using (var ms = new MemoryStream(metaBytes))
            using (var reader = ParquetReader.CreateAsync(ms).Result)
            {
                return new Dictionary<string, string>(reader.CustomMetadata);
            }
        }

        [Test]
        public void Metadata_ListLeaf_Paths_Are_Canonical_Item_And_CvValues()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var metaBytes = ReadEntry(archive, "spectra_metadata.parquet");
                using (var ms = new MemoryStream(metaBytes))
                using (var reader = ParquetReader.CreateAsync(ms).Result)
                {
                    var paths = reader.Schema.GetDataFields().Select(d => d.Path.ToString()).ToHashSet();

                    // Canonical list-element leaf path is ".../list/item/...", not a flat column.
                    Assert.That(paths.Any(p => p.StartsWith("spectrum/parameters/list/item/")), Is.True,
                        "spectrum.parameters leaves sit under .../list/item/");
                    Assert.That(paths, Does.Contain(
                        "scan/scan_windows/list/item/MS_1000501_scan_window_lower_limit_unit_MS_1000040"));
                    Assert.That(paths, Does.Contain(
                        "scan/scan_windows/list/item/MS_1000500_scan_window_upper_limit_unit_MS_1000040"));
                    Assert.That(paths.Any(p => p.StartsWith("precursor/activation/parameters/list/item/")), Is.True);
                    Assert.That(paths, Does.Contain(
                        "selected_ion/MS_1000744_selected_ion_mz_unit_MS_1000040"));
                    Assert.That(paths, Does.Contain("selected_ion/MS_1000041_charge_state"));
                    Assert.That(paths, Does.Contain(
                        "precursor/isolation_window/MS_1000827_isolation_window_target_mz"),
                        "isolation_window_target_mz carries no unit suffix");

                    // Ground-truth selected_ion carries ion-mobility columns and a parameters list even
                    // when the source has no mobility values; the struct shape must match exactly.
                    Assert.That(paths, Does.Contain("selected_ion/ion_mobility_value"));
                    Assert.That(paths, Does.Contain("selected_ion/ion_mobility_type"));
                    Assert.That(paths.Any(p => p.StartsWith("selected_ion/parameters/list/item/")), Is.True,
                        "selected_ion.parameters leaves sit under .../list/item/");
                }

                // CV values: selected_ion_mz finite/positive on MSn rows; charge_state null on small.RAW.
                var snippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "t = pq.read_table(r'{PARQUET}')\n" +
                    "d = t.to_pylist()\n" +
                    "N = len(d)\n" +
                    "M = sum(1 for i in range(N) if d[i]['selected_ion'] is not None and d[i]['selected_ion']['source_index'] is not None)\n" +
                    "out = {}\n" +
                    "out['sel_mz_pos'] = all(d[i]['selected_ion']['MS_1000744_selected_ion_mz_unit_MS_1000040'] > 0 for i in range(M))\n" +
                    "out['charge_all_null'] = all(d[i]['selected_ion']['MS_1000041_charge_state'] is None for i in range(M))\n" +
                    "out['iso_target_pos'] = all(d[i]['precursor']['isolation_window']['MS_1000827_isolation_window_target_mz'] > 0 for i in range(M))\n" +
                    "out['im_value_null'] = all(d[i]['selected_ion']['ion_mobility_value'] is None for i in range(M))\n" +
                    "out['im_type_null'] = all(d[i]['selected_ion']['ion_mobility_type'] is None for i in range(M))\n" +
                    "out['sel_params_empty'] = all(d[i]['selected_ion']['parameters'] == [] for i in range(M))\n" +
                    "print(json.dumps(out))\n";
                var o = PyArrowMetadata(archive, snippet);
                Assert.That((bool)o["sel_mz_pos"], Is.True, "selected_ion_mz positive on every MSn row");
                Assert.That((bool)o["charge_all_null"], Is.True, "small.RAW has no Charge State trailer");
                Assert.That((bool)o["iso_target_pos"], Is.True, "isolation target positive on every MSn row");
                Assert.That((bool)o["im_value_null"], Is.True, "ion_mobility_value null on every MSn row");
                Assert.That((bool)o["im_type_null"], Is.True, "ion_mobility_type null on every MSn row");
                Assert.That((bool)o["sel_params_empty"], Is.True, "selected_ion.parameters empty on every MSn row");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Metadata_NumberOfPeaks_Matches_PerOrdinal_Peaks_Facet()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var peaks = ReadPointFacet(ReadEntry(archive, "spectra_peaks.parquet"));
                var perOrdinal = peaks.SpectrumIndex
                    .GroupBy(x => x)
                    .ToDictionary(g => g.Key, g => (long)g.Count());

                var snippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "t = pq.read_table(r'{PARQUET}')\n" +
                    "d = t.to_pylist()\n" +
                    "N = len(d)\n" +
                    "out = {'npk': {str(i): d[i]['spectrum']['MS_1003059_number_of_peaks'] for i in range(N)}}\n" +
                    "print(json.dumps(out))\n";
                var o = PyArrowMetadata(archive, snippet);
                var npk = (JObject)o["npk"];

                foreach (var kv in npk)
                {
                    ulong ord = ulong.Parse(kv.Key);
                    if (kv.Value.Type == JTokenType.Null)
                    {
                        Assert.That(perOrdinal.ContainsKey(ord), Is.False,
                            $"ordinal {ord} has null number_of_peaks but peaks were written");
                    }
                    else
                    {
                        Assert.That(perOrdinal.TryGetValue(ord, out var cnt) && cnt == (long)kv.Value, Is.True,
                            $"ordinal {ord} number_of_peaks must equal its spectra_peaks point count");
                    }
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Metadata_CvList_Covers_Collected_Set_In_Index_And_Footer()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var metaBytes = ReadEntry(archive, "spectra_metadata.parquet");

                var index = JObject.Parse(
                    System.Text.Encoding.UTF8.GetString(ReadEntry(archive, "mzpeak_index.json")));
                var metadata = (JObject)index["metadata"];
                var idxCv = (JArray)metadata["cv_list"];
                Assert.That(idxCv, Is.Not.Null.And.Not.Empty);

                var footer = FooterKv(metaBytes);
                Assert.That(footer.ContainsKey("cv_list"), "footer carries cv_list");

                // Authoritative set: schema column CURIEs PLUS every accession/unit recursively gathered
                // from the index metadata blocks AND the footer JSON blocks (PARAM lists, components, ...).
                var collected = CollectAllCvPrefixes(metaBytes, metadata);
                var footerBlocks = new JObject();
                foreach (var key in new[]
                    {
                        "file_description", "instrument_configuration_list", "software_list",
                        "data_processing_method_list", "run"
                    })
                {
                    if (footer.TryGetValue(key, out var raw)) footerBlocks[key] = JToken.Parse(raw);
                }
                CollectCuriePrefixes(footerBlocks, collected);
                Assert.That(collected, Is.Not.Empty, "metadata must use CV-named columns and params");

                var idxIds = idxCv.Select(e => (string)e["id"]).ToHashSet();
                Assert.That(idxIds, Is.EquivalentTo(collected),
                    $"index cv_list {string.Join(",", idxIds.OrderBy(x => x))} must EQUAL collected " +
                    $"{string.Join(",", collected.OrderBy(x => x))} -- no hard-coded extras, nothing missing");
                foreach (var e in idxCv)
                {
                    Assert.That((string)e["id"], Is.Not.Null.And.Not.Empty);
                    Assert.That((string)e["version"], Is.Not.Null.And.Not.Empty);
                    Assert.That((string)e["uri"], Is.Not.Null.And.Not.Empty);
                }

                var footCv = JArray.Parse(footer["cv_list"]);
                var footIds = footCv.Select(e => (string)e["id"]).ToHashSet();
                Assert.That(footIds, Is.EquivalentTo(collected),
                    "footer cv_list must EQUAL the collected CV-prefix set exactly");
                Assert.That(footIds, Is.EquivalentTo(idxIds), "index and footer cv_list agree");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Metadata_FileLevel_Blocks_Present_In_Index_And_Footer()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var metaBytes = ReadEntry(archive, "spectra_metadata.parquet");
                var index = JObject.Parse(
                    System.Text.Encoding.UTF8.GetString(ReadEntry(archive, "mzpeak_index.json")));
                var m = (JObject)index["metadata"];

                Assert.That((string)m["version"], Is.Not.Null.And.Not.Empty);

                var ics = (JArray)m["instrument_configuration_list"];
                Assert.That(ics.Count, Is.EqualTo(2), "small.RAW yields two distinct analyzers");
                foreach (var ic in ics)
                {
                    Assert.That((string)ic["software_reference"], Is.EqualTo("ThermoRawFileParser"));
                    Assert.That(((JArray)ic["parameters"]).Count, Is.GreaterThanOrEqualTo(2));
                    var comps = ((JArray)ic["components"])
                        .Select(c => (string)c["component_type"]).ToArray();
                    Assert.That(comps, Is.EqualTo(new[] { "ionsource", "analyzer", "detector" }),
                        "components ordered ionsource/analyzer/detector");
                    foreach (var c in (JArray)ic["components"])
                        Assert.That(((JArray)c["parameters"]).Count, Is.GreaterThanOrEqualTo(1));
                }

                Assert.That(((JArray)m["software_list"]).Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(((JArray)m["data_processing_method_list"]).Count, Is.GreaterThanOrEqualTo(1));
                var fd = (JObject)m["file_description"];
                Assert.That(((JArray)fd["contents"]).Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(((JArray)fd["source_files"]).Count, Is.EqualTo(1));
                Assert.That((string)((JArray)fd["source_files"])[0]["id"], Is.EqualTo("RAW1"));
                Assert.That(m["sample_list"].Type, Is.EqualTo(JTokenType.Array));
                Assert.That(m["scan_settings_list"].Type, Is.EqualTo(JTokenType.Array));

                var footer = FooterKv(metaBytes);
                foreach (var key in new[]
                    {
                        "instrument_configuration_list", "software_list",
                        "data_processing_method_list", "file_description", "run"
                    })
                {
                    Assert.That(footer.ContainsKey(key), $"footer carries {key}");
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static string ResolveValidator()
        {
            foreach (var cand in new[] { "mzpeak-validate" })
            {
                try
                {
                    var psi = new ProcessStartInfo(cand, "--help")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    using (var p = Process.Start(psi))
                    {
                        p.WaitForExit();
                        return cand;
                    }
                }
                catch { }
            }
            return null;
        }

        [Test]
        public void Validator_Gate_ScanError_And_CvList_Absent_NoNewError()
        {
            var validator = ResolveValidator();
            if (validator == null) Assert.Ignore("mzpeak-validate not available");

            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            var jsonPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
            try
            {
                var psi = new ProcessStartInfo(validator, $"\"{archive}\" --json \"{jsonPath}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                }

                Assert.That(File.Exists(jsonPath), "validator must emit a JSON report");
                var report = JObject.Parse(File.ReadAllText(jsonPath));
                var errorIds = ((JArray)report["findings"])
                    .Where(f => (string)f["level"] == "error")
                    .Select(f => (string)f["ruleId"])
                    .ToHashSet();

                // The pre-existing reference-archive failures; our writer introduces none of them.
                var allowlist = new HashSet<string> { "index_schema_valid", "cv_list_declared" };

                Assert.That(errorIds, Does.Not.Contain("columns_spectra_metadata"),
                    "scan-facet error must be cleared");
                Assert.That(errorIds, Does.Not.Contain("cv_list_declared"),
                    "cv_list_declared must be cleared by the generated cv_list");
                var newErrors = errorIds.Except(allowlist).ToArray();
                Assert.That(newErrors, Is.Empty,
                    $"no new ERROR id beyond the pre-existing allowlist: {string.Join(",", newErrors)}");
            }
            finally
            {
                if (File.Exists(jsonPath)) File.Delete(jsonPath);
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Chromatogram_Data_Facet_Shape_And_PerScan_Alignment()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                using (var ms = new MemoryStream(ReadEntry(archive, "chromatograms_data.parquet")))
                using (var reader = ParquetReader.CreateAsync(ms).Result)
                {
                    var schema = reader.Schema;
                    Assert.That(Leaf(schema, "point/chromatogram_index").ClrType, Is.EqualTo(typeof(ulong)));
                    Assert.That(Leaf(schema, "point/time").ClrType, Is.EqualTo(typeof(double)));
                    Assert.That(Leaf(schema, "point/intensity").ClrType, Is.EqualTo(typeof(float)));
                    Assert.That(Leaf(schema, "point/ms_level").ClrType, Is.EqualTo(typeof(long)));
                }

                var chrom = ReadChromFacet(ReadEntry(archive, "chromatograms_data.parquet"));
                var data = ReadPointFacet(ReadEntry(archive, "spectra_data.parquet"));
                int n = data.SpectrumCount;

                Assert.That(chrom.Time.Length, Is.EqualTo(n), "one TIC point per emitted scan");
                Assert.That(chrom.TicSource, Is.EqualTo("device"),
                    "default conversion must take the device-trace path, not the summed fallback");
                Assert.That(chrom.ChromatogramIndex.All(x => x == 0UL), Is.True, "single chromatogram, index all 0");
                var levels = chrom.MsLevel.Distinct().OrderBy(x => x).ToArray();
                Assert.That(levels, Is.EqualTo(new long[] { 1, 2 }), "ms_level multiset {1,2}, never 0");

                // Per-scan spectrum (time, ms_level) read from spectra_metadata, aligned by ordinal.
                var snippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "t = pq.read_table(r'{PARQUET}')\n" +
                    "d = t.to_pylist()\n" +
                    "out = {'time':[d[i]['spectrum']['time'] for i in range(len(d))],\n" +
                    "       'lvl':[int(d[i]['spectrum']['MS_1000511_ms_level']) for i in range(len(d))]}\n" +
                    "print(json.dumps(out))\n";
                var o = PyArrowEntry(archive, "spectra_metadata.parquet", snippet);
                var sTime = o["time"].Select(x => (double)x).ToArray();
                var sLvl = o["lvl"].Select(x => (long)x).ToArray();

                Assert.That(sTime.Length, Is.EqualTo(n));
                for (int i = 0; i < n; i++)
                {
                    Assert.That(chrom.Time[i], Is.EqualTo(sTime[i]).Within(1e-6),
                        $"TIC time[{i}] must equal spectrum time");
                    Assert.That(chrom.MsLevel[i], Is.EqualTo(sLvl[i]),
                        $"TIC ms_level[{i}] must equal spectrum ms_level");
                }
                for (int i = 1; i < n; i++)
                    Assert.That(chrom.Time[i], Is.GreaterThanOrEqualTo(chrom.Time[i - 1]), "time non-decreasing");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Chromatogram_Metadata_Facet_Shape_And_Values()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var data = ReadPointFacet(ReadEntry(archive, "spectra_data.parquet"));
                int n = data.SpectrumCount;

                var snippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "t = pq.read_table(r'{PARQUET}')\n" +
                    "d = t.to_pylist()\n" +
                    "c = d[0]['chromatogram']\n" +
                    "ct = t.schema.field('chromatogram').type\n" +
                    "aux = ct.field('auxiliary_arrays').type.value_type\n" +
                    "out = {}\n" +
                    "out['rows'] = len(d)\n" +
                    "out['id'] = c['id']\n" +
                    "out['type'] = c['MS_1000626_chromatogram_type']\n" +
                    "out['pol'] = int(c['MS_1000465_scan_polarity'])\n" +
                    "out['pol_type'] = str(ct.field('MS_1000465_scan_polarity').type)\n" +
                    "out['ndp'] = c['MS_1003060_number_of_data_points']\n" +
                    "out['dpr_null'] = c['data_processing_ref'] is None\n" +
                    "out['params_empty'] = c['parameters'] == []\n" +
                    "out['aux_empty'] = c['auxiliary_arrays'] == []\n" +
                    "out['naux'] = int(c['number_of_auxiliary_arrays'])\n" +
                    "out['aux_fields'] = [f.name for f in aux]\n" +
                    "out['prec_in_schema'] = 'precursor' in [f.name for f in t.schema]\n" +
                    "out['seli_in_schema'] = 'selected_ion' in [f.name for f in t.schema]\n" +
                    "out['prec_null'] = d[0]['precursor'] is None or d[0]['precursor']['source_index'] is None\n" +
                    "out['seli_null'] = d[0]['selected_ion'] is None or d[0]['selected_ion']['source_index'] is None\n" +
                    "selt = t.schema.field('selected_ion').type\n" +
                    "out['seli_fields'] = [f.name for f in selt]\n" +
                    "print(json.dumps(out))\n";
                var o = PyArrowEntry(archive, "chromatograms_metadata.parquet", snippet);

                Assert.That((int)o["rows"], Is.EqualTo(1), "exactly one chromatogram row");
                Assert.That((string)o["id"], Is.EqualTo("TIC"));
                Assert.That((string)o["type"], Is.EqualTo("MS:1000235"));
                Assert.That((int)o["pol"], Is.EqualTo(0));
                Assert.That((string)o["pol_type"], Is.EqualTo("int8"));
                Assert.That((long)o["ndp"], Is.EqualTo((long)n));
                Assert.That((bool)o["dpr_null"], Is.True);
                Assert.That((bool)o["params_empty"], Is.True);
                Assert.That((bool)o["aux_empty"], Is.True);
                Assert.That((int)o["naux"], Is.EqualTo(0));

                var auxFields = o["aux_fields"].Select(x => (string)x).ToArray();
                foreach (var f in new[] { "data", "name", "data_type", "compression", "unit", "parameters", "data_processing_ref" })
                    Assert.That(auxFields, Does.Contain(f), $"AUX_ARRAY element exposes {f}");

                Assert.That((bool)o["prec_in_schema"], Is.True);
                Assert.That((bool)o["seli_in_schema"], Is.True);
                Assert.That((bool)o["prec_null"], Is.True, "precursor present-but-null on the row");
                Assert.That((bool)o["seli_null"], Is.True, "selected_ion present-but-null on the row");

                // The chromatogram selected_ion struct must carry the same ground-truth columns as the
                // spectra selected_ion, including the ion-mobility columns and the parameters list.
                var seliFields = o["seli_fields"].Select(x => (string)x).ToArray();
                foreach (var f in new[]
                {
                    "source_index", "precursor_index", "MS_1000744_selected_ion_mz_unit_MS_1000040",
                    "MS_1000041_charge_state", "MS_1000042_intensity_unit_MS_1000131",
                    "ion_mobility_value", "ion_mobility_type", "parameters"
                })
                    Assert.That(seliFields, Does.Contain(f), $"chromatogram selected_ion exposes {f}");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Index_Lists_Chromatogram_Facets()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var index = JObject.Parse(
                    System.Text.Encoding.UTF8.GetString(ReadEntry(archive, "mzpeak_index.json")));
                var files = (JArray)index["files"];

                var cdata = files.FirstOrDefault(f => (string)f["name"] == "chromatograms_data.parquet");
                Assert.That(cdata, Is.Not.Null);
                Assert.That((string)cdata["entity_type"], Is.EqualTo("chromatogram"));
                Assert.That((string)cdata["data_kind"], Is.EqualTo("data arrays"));

                var cmeta = files.FirstOrDefault(f => (string)f["name"] == "chromatograms_metadata.parquet");
                Assert.That(cmeta, Is.Not.Null);
                Assert.That((string)cmeta["entity_type"], Is.EqualTo("chromatogram"));
                Assert.That((string)cmeta["data_kind"], Is.EqualTo("metadata"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Chromatograms_Add_No_New_CvPrefix()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var index = JObject.Parse(
                    System.Text.Encoding.UTF8.GetString(ReadEntry(archive, "mzpeak_index.json")));
                var ids = ((JArray)index["metadata"]["cv_list"]).Select(e => (string)e["id"]).ToHashSet();
                Assert.That(ids, Is.EquivalentTo(new[] { "MS", "UO" }),
                    "chromatograms introduce no new CV prefix and no version bump");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // The generated cv_list must be genuinely exhaustive: it is finalized only after the chromatogram
        // facets have contributed their CURIEs, so every prefix that any chromatogram column or term uses
        // (chromatogram type, time/intensity/ms-level array terms and their units) appears in cv_list.
        [Test]
        public void CvList_Covers_Chromatogram_Introduced_Prefixes()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var index = JObject.Parse(
                    System.Text.Encoding.UTF8.GetString(ReadEntry(archive, "mzpeak_index.json")));
                var ids = ((JArray)index["metadata"]["cv_list"]).Select(e => (string)e["id"]).ToHashSet();

                // Prefixes the chromatogram facets introduce via their CV-named columns and array terms.
                var chromCuries = new[]
                {
                    "MS:1000235", "MS:1000626", "MS:1000595", "MS:1000786",
                    "UO:0000031", "UO:0000186"
                };
                foreach (var curie in chromCuries)
                {
                    var prefix = curie.Substring(0, curie.IndexOf(':'));
                    Assert.That(ids, Does.Contain(prefix),
                        $"cv_list must declare the chromatogram-introduced prefix {prefix} (from {curie})");
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // L1 is an independent value check: our spectra_data m/z (f64) is compared bit-exact, per
        // spectrum, against the m/z array re-read directly from small.RAW (Scan.FromFile().SegmentedScan
        // .Positions) -- the source is NOT derived from our archive. The Thermo SegmentedScan returns
        // Positions sorted but Intensities in acquisition order (the two arrays are not index-paired and
        // their nonzero counts differ from the writer's realigned output), so re-deriving the exact
        // per-point intensity pairing from the SegmentedScan alone would require re-implementing the
        // reader's internal realignment. The differential suite (MzPeakDifferentialTests) compares
        // intensities value-for-value only over the reference-profile spectra. To avoid overclaiming
        // beyond that scope, this test asserts L2 here as a structural invariant for every emitted
        // spectrum (intensity is f32, finite, and the per-spectrum point count equals
        // number_of_data_points) and additionally runs an independent intensity check against an mzML
        // re-export of the same RAW for every spectrum that has a clean point-for-point correspondence,
        // so the value-equality claim rests on a source not derived from our archive.
        [Test]
        public void RoundTrip_L1_Mz_BitExact_Vs_IndependentRawRead_L2_Intensity_F32_Structural()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                using (var ms = new MemoryStream(ReadEntry(archive, "spectra_data.parquet")))
                using (var reader = ParquetReader.CreateAsync(ms).Result)
                {
                    Assert.That(Leaf(reader.Schema, "point/mz").ClrType, Is.EqualTo(typeof(double)), "m/z f64");
                    Assert.That(Leaf(reader.Schema, "point/intensity").ClrType, Is.EqualTo(typeof(float)), "intensity f32");
                }

                var data = ReadPointFacet(ReadEntry(archive, "spectra_data.parquet"));
                var ourMz = new Dictionary<ulong, List<double>>();
                var ourInt = new Dictionary<ulong, List<float>>();
                for (int i = 0; i < data.SpectrumIndex.Length; i++)
                {
                    var o = data.SpectrumIndex[i];
                    if (!ourMz.ContainsKey(o)) { ourMz[o] = new List<double>(); ourInt[o] = new List<float>(); }
                    ourMz[o].Add(data.Mz[i]);
                    ourInt[o].Add(data.Intensity[i]);
                }

                var meta = PyArrowEntry(archive, "spectra_metadata.parquet",
                    "import pyarrow.parquet as pq, json\n" +
                    "t = pq.read_table(r'{PARQUET}')\n" +
                    "d = t.to_pylist()\n" +
                    "out = {'ndp':[int(d[i]['spectrum']['MS_1003060_number_of_data_points']) for i in range(len(d))]}\n" +
                    "print(json.dumps(out))\n");
                var ndp = meta["ndp"].Select(x => (int)x).ToArray();
                int n = ndp.Length;

                using (var raw = RawFileReaderFactory.ReadFile(TestRawFile))
                {
                    raw.SelectInstrument(Device.MS, 1);
                    int first = raw.RunHeaderEx.FirstSpectrum;
                    int last = raw.RunHeaderEx.LastSpectrum;
                    var ordinalScan = new List<int>();
                    for (int scan = first; scan <= last; scan++)
                    {
                        var sf = raw.GetFilterForScanNumber(scan);
                        int lvl = (int)sf.MSOrder;
                        if (lvl > input.MaxLevel || !input.MsLevel.Contains(lvl)) continue;
                        var seg = Scan.FromFile(raw, scan).SegmentedScan;
                        if (seg.Positions == null || seg.Positions.Length == 0) continue;
                        ordinalScan.Add(scan);
                    }

                    Assert.That(ordinalScan.Count, Is.EqualTo(n),
                        "ordinal->scan mapping must cover every emitted spectrum");

                    for (ulong ord = 0; ord < (ulong)n; ord++)
                    {
                        int scan = ordinalScan[(int)ord];
                        var seg = Scan.FromFile(raw, scan).SegmentedScan;
                        // Source m/z, ascending (positions are returned sorted); independent of our archive.
                        var srcMz = (double[])seg.Positions.Clone();
                        Array.Sort(srcMz);

                        var aMz = ourMz[ord].ToArray();
                        var aInt = ourInt[ord].ToArray();

                        Assert.That(aMz.Length, Is.EqualTo(srcMz.Length),
                            $"ordinal {ord}: our m/z count must equal independent source count");
                        Assert.That(aMz.Length, Is.EqualTo(ndp[(int)ord]),
                            $"ordinal {ord}: count must equal number_of_data_points");

                        for (int k = 0; k < aMz.Length; k++)
                        {
                            // L1: m/z bit-exact f64 vs the independently re-read source array.
                            Assert.That(aMz[k], Is.EqualTo(srcMz[k]),
                                $"ordinal {ord} m/z[{k}] not bit-exact vs independent source");
                            if (k > 0)
                                Assert.That(aMz[k], Is.GreaterThanOrEqualTo(aMz[k - 1]),
                                    $"ordinal {ord} m/z non-decreasing at {k}");
                            // L2 structural invariant: intensity is finite f32 for every emitted point.
                            Assert.That(float.IsFinite(aInt[k]), Is.True, $"ordinal {ord} intensity[{k}] finite");
                        }
                    }
                }

                // Independent intensity check for the spectra the differential suite does NOT cover by
                // value: re-export the same RAW to a profile mzML (a source not derived from our archive)
                // and compare its decoded intensity arrays, narrowed to f32, against our spectra_data for
                // every spectrum whose m/z arrays correspond point-for-point. Spectra without a clean
                // correspondence are reported as structural-only and are not claimed value-equal.
                AssertIntensityMatchesIndependentMzml(dir, archive, ourMz, ourInt);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // Re-exports the test RAW to a profile mzML in-process, decodes its binaryDataArrays in Python
        // (honoring each array's own 32/64-bit + zlib CV flags), and asserts that, for every emitted
        // spectrum with a bit-exact m/z correspondence, our f32-narrowed spectra_data intensities equal
        // the mzML intensities narrowed to f32. The mzML is an independent re-derivation of the RAW, so
        // matching intensities certify L2 value-equality beyond the differential suite's profile subset.
        private static void AssertIntensityMatchesIndependentMzml(string dir, string archive,
            Dictionary<ulong, List<double>> ourMz, Dictionary<ulong, List<float>> ourInt)
        {
            var python = ResolvePython();
            if (python == null) Assert.Ignore("python3 not available");

            var mzml = Path.Combine(dir, "small.indep.profile.mzML");
            var mzmlInput = new ParseInput(TestRawFile, null, dir, OutputFormat.MzML)
            {
                NoPeakPicking = ParseInput.AllLevels
            };
            RawFileParser.Parse(mzmlInput);
            // TRFP names the mzML output after the RAW stem; relocate it to the deterministic path.
            var produced = Path.Combine(dir, "small.mzML");
            if (File.Exists(produced) && !File.Exists(mzml)) File.Move(produced, mzml);
            Assert.That(File.Exists(mzml), "independent profile mzML must exist");

            // Decode the mzML into ordered per-spectrum (mz, intensity) f64 arrays. Index order in the
            // mzML matches our ordinal order (same scan-range filter, same source), so we compare by
            // position and only where the m/z arrays correspond exactly.
            var script = Path.Combine(dir, "decode_mzml.py");
            File.WriteAllText(script, DecodeMzmlScript);
            var psi = new ProcessStartInfo(python, $"\"{script}\" \"{mzml}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            string stdout, stderr;
            using (var proc = Process.Start(psi))
            {
                stdout = proc.StandardOutput.ReadToEnd();
                stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                Assert.That(proc.ExitCode, Is.EqualTo(0), $"mzML decode failed: {stderr}");
            }

            var spectra = (JArray)JObject.Parse(stdout.Trim())["spectra"];
            int compared = 0, structuralOnly = 0;
            for (int i = 0; i < spectra.Count; i++)
            {
                var ord = (ulong)i;
                if (!ourMz.ContainsKey(ord)) { structuralOnly++; continue; }
                var refMz = ((JArray)spectra[i]["mz"]).Select(x => (double)x).ToArray();
                var refInt = ((JArray)spectra[i]["intensity"]).Select(x => (double)x).ToArray();
                var aMz = ourMz[ord];
                var aInt = ourInt[ord];

                // Only claim value-equality where the two m/z arrays correspond point-for-point.
                bool corresponds = refMz.Length == aMz.Count;
                for (int k = 0; corresponds && k < refMz.Length; k++)
                    if (refMz[k] != aMz[k]) corresponds = false;

                if (!corresponds) { structuralOnly++; continue; }

                compared++;
                for (int k = 0; k < refInt.Length; k++)
                {
                    // Both sides narrowed to f32: our archive stores f32, the mzML carries f64 that the
                    // writer would have narrowed identically.
                    Assert.That(aInt[k], Is.EqualTo((float)refInt[k]),
                        $"ordinal {ord} intensity[{k}] must equal the independent mzML intensity (f32)");
                }
            }

            Assert.That(compared, Is.GreaterThan(0),
                "at least one emitted spectrum must have a bit-exact m/z correspondence to compare by value");
            TestContext.Out.WriteLine(
                $"independent intensity check: {compared} spectra value-equal, {structuralOnly} structural-only");
        }

        // Standard-library mzML decoder: yields per-spectrum (mz, intensity) f64 arrays, decoding each
        // binaryDataArray per its own precision (32/64-bit float) and compression (zlib/none) CV flags.
        private const string DecodeMzmlScript =
            "import sys, json, base64, zlib, struct\n" +
            "import xml.etree.ElementTree as ET\n" +
            "path = sys.argv[1]\n" +
            "ns = {'m': 'http://psi.hupo.org/ms/mzml'}\n" +
            "tree = ET.parse(path); root = tree.getroot()\n" +
            "def localname(t):\n" +
            "    return t.rsplit('}', 1)[-1]\n" +
            "def decode_array(bda):\n" +
            "    accs = set()\n" +
            "    for cv in bda:\n" +
            "        if localname(cv.tag) == 'cvParam': accs.add(cv.get('accession'))\n" +
            "    bits = 64 if 'MS:1000523' in accs else (32 if 'MS:1000521' in accs else 64)\n" +
            "    zipped = 'MS:1000574' in accs\n" +
            "    is_mz = 'MS:1000514' in accs\n" +
            "    is_int = 'MS:1000515' in accs\n" +
            "    b64 = None\n" +
            "    for c in bda:\n" +
            "        if localname(c.tag) == 'binary': b64 = c.text or ''\n" +
            "    raw = base64.b64decode(b64) if b64 else b''\n" +
            "    if zipped and raw: raw = zlib.decompress(raw)\n" +
            "    fmt = ('<%dd' % (len(raw)//8)) if bits == 64 else ('<%df' % (len(raw)//4))\n" +
            "    vals = list(struct.unpack(fmt, raw)) if raw else []\n" +
            "    return is_mz, is_int, vals\n" +
            "spectra = []\n" +
            "for sp in root.iter():\n" +
            "    if localname(sp.tag) != 'spectrum': continue\n" +
            "    mz, inten = [], []\n" +
            "    for bda in sp.iter():\n" +
            "        if localname(bda.tag) != 'binaryDataArray': continue\n" +
            "        is_mz, is_int, vals = decode_array(bda)\n" +
            "        if is_mz: mz = vals\n" +
            "        elif is_int: inten = vals\n" +
            "    spectra.append({'mz': mz, 'intensity': inten})\n" +
            "print(json.dumps({'spectra': spectra}))\n";

        [Test]
        public void Validator_Gate_Stays_Zero_Errors_With_Chromatograms()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);

            // Structural assertions run even when the validator binary is unavailable on the host.
            Assert.That(ReadEntry(archive, "chromatograms_data.parquet"), Is.Not.Null);
            Assert.That(ReadEntry(archive, "chromatograms_metadata.parquet"), Is.Not.Null);

            var validator = ResolveValidator();
            if (validator == null)
            {
                Directory.Delete(dir, true);
                Assert.Ignore("mzpeak-validate not available");
            }

            var jsonPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
            try
            {
                var psi = new ProcessStartInfo(validator, $"\"{archive}\" --json \"{jsonPath}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                }

                Assert.That(File.Exists(jsonPath), "validator must emit a JSON report");
                var report = JObject.Parse(File.ReadAllText(jsonPath));
                var findings = (JArray)report["findings"];

                var errors = findings.Where(f => (string)f["level"] == "error")
                    .Select(f => (string)f["ruleId"]).ToArray();
                var warnings = findings.Where(f => (string)f["level"] == "warning")
                    .Select(f => (string)f["ruleId"]).ToArray();

                Assert.That(errors, Is.Empty, $"0 errors required; got {string.Join(",", errors)}");
                Assert.That(warnings, Is.Empty, $"0 warnings required; got {string.Join(",", warnings)}");

                var chromRules = findings.Select(f => (string)f["ruleId"])
                    .Where(r => r != null && r.Contains("chromatogram")).ToArray();
                Assert.That(chromRules, Is.Empty,
                    $"no chromatogram-related finding: {string.Join(",", chromRules)}");
            }
            finally
            {
                if (File.Exists(jsonPath)) File.Delete(jsonPath);
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void DataFacet_MultiRowGroup_Equals_SingleRowGroup()
        {
            // Production-cap run: single row group for small.RAW.
            var baseInput = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var baseDir = Convert(baseInput, out var baseArchive);
            Facet single;
            try
            {
                single = ReadAllRowGroups(ReadEntry(baseArchive, "spectra_data.parquet"));
            }
            finally
            {
                Directory.Delete(baseDir, true);
            }

            // Lowered-cap run through the real flush path: forces >1 row group.
            try
            {
                MzPeakSpectrumWriter.TestRowGroupRowCap = 64;
                var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
                var dir = Convert(input, out var archive);
                try
                {
                    int rgCount;
                    using (var ms = new MemoryStream(ReadEntry(archive, "spectra_data.parquet")))
                    using (var reader = ParquetReader.CreateAsync(ms).Result)
                        rgCount = reader.RowGroupCount;
                    Assert.That(rgCount, Is.GreaterThan(1),
                        "lowered cap must force the real flush path to emit multiple row groups");

                    var multi = ReadAllRowGroups(ReadEntry(archive, "spectra_data.parquet"));
                    Assert.That(multi.SpectrumCount, Is.EqualTo(single.SpectrumCount));
                    Assert.That(multi.PointCount, Is.EqualTo(single.PointCount));
                    AssertSameMultiset(single, multi);
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
            }
            finally
            {
                MzPeakSpectrumWriter.TestRowGroupRowCap = null;
            }
        }

        private static void AssertSameMultiset(Facet a, Facet b)
        {
            Assert.That(a.Mz.Length, Is.EqualTo(b.Mz.Length));
            var ta = Enumerable.Range(0, a.Mz.Length)
                .Select(i => (a.SpectrumIndex[i], a.Mz[i], a.Intensity[i]))
                .OrderBy(x => x.Item1).ThenBy(x => x.Item2).ThenBy(x => x.Item3).ToArray();
            var tb = Enumerable.Range(0, b.Mz.Length)
                .Select(i => (b.SpectrumIndex[i], b.Mz[i], b.Intensity[i]))
                .OrderBy(x => x.Item1).ThenBy(x => x.Item2).ThenBy(x => x.Item3).ToArray();
            Assert.That(tb, Is.EqualTo(ta), "(spectrum_index, mz, intensity) multiset identical across caps");
        }

        private static Facet ReadAllRowGroups(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var reader = ParquetReader.CreateAsync(ms).Result)
            {
                var schema = reader.Schema;
                var idx = new List<ulong>(); var mz = new List<double>(); var inten = new List<float>();
                for (int g = 0; g < reader.RowGroupCount; g++)
                {
                    var rg = reader.OpenRowGroupReader(g);
                    idx.AddRange(rg.ReadColumnAsync(Leaf(schema, "point/spectrum_index")).Result.Data.Cast<ulong>());
                    mz.AddRange(rg.ReadColumnAsync(Leaf(schema, "point/mz")).Result.Data.Cast<double>());
                    inten.AddRange(rg.ReadColumnAsync(Leaf(schema, "point/intensity")).Result.Data.Cast<float>());
                }
                var meta = reader.CustomMetadata;
                return new Facet
                {
                    SpectrumIndex = idx.ToArray(),
                    Mz = mz.ToArray(),
                    Intensity = inten.ToArray(),
                    SpectrumCount = int.Parse(meta["spectrum_count"]),
                    PointCount = int.Parse(meta["spectrum_data_point_count"])
                };
            }
        }

        // Known v1 baseline totals for small.RAW (captured from the certified v1 conversion). Asserting
        // the literal numbers — not just self-consistency — catches self-consistent semantic drift.
        private const int V1SpectrumCount = 48;
        private const int V1DataPointCount = 305213;
        private const int V1PeakSpectrumCount = 7;
        private const int V1PeakPointCount = 12890;
        private const int V1TicPointCount = 48;

        [Test]
        public void SmallRaw_Identical_To_V1_Invariants()
        {
            var input = new ParseInput(TestRawFile, null, null, OutputFormat.MzPeak);
            var dir = Convert(input, out var archive);
            try
            {
                var data = ReadAllRowGroups(ReadEntry(archive, "spectra_data.parquet"));
                var metaIdx = ReadMetadataIndices(ReadEntry(archive, "spectra_metadata.parquet"));

                int distinct = data.SpectrumIndex.Distinct().Count();
                Assert.That(distinct, Is.EqualTo(data.SpectrumCount),
                    "distinct spectrum_index == spectrum_count");
                Assert.That(metaIdx.Length, Is.EqualTo(data.SpectrumCount),
                    "metadata spectrum rows == spectrum_count");
                Assert.That(metaIdx.OrderBy(x => x).ToArray(),
                    Is.EqualTo(Enumerable.Range(0, data.SpectrumCount).Select(i => (ulong)i).ToArray()),
                    "ordinals dense 0..N-1");
                Assert.That(data.Mz.Length, Is.EqualTo(data.PointCount),
                    "footer spectrum_data_point_count == point rows");

                // Known v1 baseline totals on the data facet.
                Assert.That(data.SpectrumCount, Is.EqualTo(V1SpectrumCount), "v1 baseline spectrum count");
                Assert.That(data.PointCount, Is.EqualTo(V1DataPointCount), "v1 baseline total data points");

                // Footer KV present and correct on the streamed data facet.
                using (var ms = new MemoryStream(ReadEntry(archive, "spectra_data.parquet")))
                using (var reader = ParquetReader.CreateAsync(ms).Result)
                {
                    var kv = reader.CustomMetadata;
                    Assert.That(kv["spectrum_count"], Is.EqualTo(V1SpectrumCount.ToString()));
                    Assert.That(kv["spectrum_data_point_count"], Is.EqualTo(V1DataPointCount.ToString()));
                    Assert.That(kv.ContainsKey("spectrum_array_index"), Is.True);
                }

                // Known v1 baseline totals + footer KV on the streamed peaks facet.
                var peaks = ReadAllRowGroups(ReadEntry(archive, "spectra_peaks.parquet"));
                Assert.That(peaks.SpectrumCount, Is.EqualTo(V1PeakSpectrumCount), "v1 baseline peak-bearing spectra");
                Assert.That(peaks.PointCount, Is.EqualTo(V1PeakPointCount), "v1 baseline total peak points");
                Assert.That(peaks.Mz.Length, Is.EqualTo(V1PeakPointCount), "peak point rows == footer count");
                using (var ms = new MemoryStream(ReadEntry(archive, "spectra_peaks.parquet")))
                using (var reader = ParquetReader.CreateAsync(ms).Result)
                {
                    var kv = reader.CustomMetadata;
                    Assert.That(kv["spectrum_count"], Is.EqualTo(V1PeakSpectrumCount.ToString()));
                    Assert.That(kv["spectrum_data_point_count"], Is.EqualTo(V1PeakPointCount.ToString()));
                    Assert.That(kv.ContainsKey("spectrum_array_index"), Is.True);
                }

                // Known v1 baseline TIC point count + footer KV on the streamed chrom-data facet.
                var chrom = ReadChromFacet(ReadEntry(archive, "chromatograms_data.parquet"));
                Assert.That(chrom.Time.Length, Is.EqualTo(V1TicPointCount), "v1 baseline TIC point count");
                using (var ms = new MemoryStream(ReadEntry(archive, "chromatograms_data.parquet")))
                using (var reader = ParquetReader.CreateAsync(ms).Result)
                {
                    var kv = reader.CustomMetadata;
                    Assert.That(kv.ContainsKey("chromatogram_data_point_count"), Is.True);
                    Assert.That(kv.ContainsKey("chromatogram_array_index"), Is.True);
                    Assert.That(kv.ContainsKey("chromatogram_tic_source"), Is.True);
                }

                // cv_list covers the chrom-data prefixes (derived prefixes MS/UO, not whole CURIEs).
                var index = JObject.Parse(
                    System.Text.Encoding.UTF8.GetString(ReadEntry(archive, "mzpeak_index.json")));
                var ids = ((JArray)index["metadata"]["cv_list"]).Select(e => (string)e["id"]).ToHashSet();
                foreach (var curie in new[]
                {
                    "MS:1000523", "MS:1000595", "UO:0000031", "MS:1000521", "MS:1000515",
                    "MS:1000131", "MS:1000522", "MS:1000786", "UO:0000186"
                })
                {
                    var prefix = curie.Substring(0, curie.IndexOf(':'));
                    Assert.That(ids, Does.Contain(prefix),
                        $"cv_list must declare chrom-data prefix {prefix} (from {curie})");
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void RegisterChromDataPrefixes_Registers_All_Nine_Accessions()
        {
            var prefixes = MzPeakSpectrumWriter.ChromDataAccessions;
            Assert.That(prefixes, Is.EquivalentTo(new[]
            {
                "MS:1000523", "MS:1000595", "UO:0000031", "MS:1000521", "MS:1000515",
                "MS:1000131", "MS:1000522", "MS:1000786", "UO:0000186"
            }), "all nine chrom-data accessions are registered before cv_list is finalized");
        }

        // Runs the production MzPeakSpectrumWriter.Write loop against small.RAW into a temp directory.
        // failScan: throw AFTER that scan's filter key is staged (a real post-key read failure).
        // dropParentBeforeChild: just before that child's precursor is built, drop its resolved parent's
        //   ordinal from the live map — a parent that was read but never emitted, so the child must not read
        //   a selected-ion intensity through it. Returns the archive path.
        private static string WriteWithInjection(string dir, int? failScan, int? dropParentBeforeChild)
        {
            var input = new ParseInput(TestRawFile, null, dir, OutputFormat.MzPeak);
            var writer = new MzPeakSpectrumWriter(input);
            if (failScan.HasValue)
            {
                writer.AfterFilterKeyStaged = scan =>
                {
                    if (scan == failScan.Value)
                        throw new InvalidOperationException($"injected read failure on scan {scan}");
                };
            }
            if (dropParentBeforeChild.HasValue)
            {
                writer.BeforeBuildPrecursor = (scan, scanToOrdinal) =>
                {
                    if (scan != dropParentBeforeChild.Value) return;
                    // Strip every emitted parent so this child cannot resolve a precursor_index, while its
                    // parent scan number is still positive — exactly the read-but-not-emitted condition.
                    scanToOrdinal.Clear();
                };
            }
            using (var raw = RawFileReaderFactory.ReadFile(TestRawFile))
            {
                raw.SelectInstrument(Device.MS, 1);
                writer.Write(raw, raw.RunHeaderEx.FirstSpectrum, raw.RunHeaderEx.LastSpectrum);
            }
            return Path.Combine(dir, "small.mzpeak");
        }

        // Maps each MSn child's own ordinal (source_index) to its precursor_index (parent ordinal, null
        // when the parent was not emitted) and its selected-ion intensity (null when not read).
        private static (Dictionary<ulong, ulong?> precursorIndex, Dictionary<ulong, float?> selIntensity)
            ReadMsnChildMaps(byte[] metaBytes)
        {
            using (var ms = new MemoryStream(metaBytes))
            using (var reader = ParquetReader.CreateAsync(ms).Result)
            {
                var schema = reader.Schema;
                var rg = reader.OpenRowGroupReader(0);

                var preSrc = rg.ReadColumnAsync(Leaf(schema, "precursor/source_index")).Result;
                var preIdx = rg.ReadColumnAsync(Leaf(schema, "precursor/precursor_index")).Result;
                var siSrc = rg.ReadColumnAsync(Leaf(schema, "selected_ion/source_index")).Result;
                var siInt = rg.ReadColumnAsync(Leaf(schema,
                    "selected_ion/MS_1000042_intensity_unit_MS_1000131")).Result;

                var precursorIndex = ZipNullable<ulong>(preSrc.Data, preIdx.Data);
                var selIntensity = ZipNullable<float>(siSrc.Data, siInt.Data);
                return (precursorIndex, selIntensity);
            }
        }

        // Pairs a nullable source_index column (one defined value per MSn row, null on the padded tail)
        // with a value column that holds a value only where present, producing source_index -> value?
        // (null where the value itself is absent). Rows whose source_index is null are skipped.
        private static Dictionary<ulong, T?> ZipNullable<T>(Array keys, Array values) where T : struct
        {
            var map = new Dictionary<ulong, T?>();
            for (int i = 0; i < keys.Length; i++)
            {
                var key = keys.GetValue(i);
                if (key == null) continue;
                var v = values.GetValue(i);
                map[(ulong)key] = v == null ? (T?)null : (T)v;
            }
            return map;
        }

        [Test]
        public void FailedParent_DoesNotPoison_LaterChild_PrecursorResolution()
        {
            // Part A — staging isolation: a post-key read failure on an MS1 must consume no ordinal and no
            // precursor-map entry. Skip scan 8 (an MS1) through the real Write loop and assert the output
            // stays dense and every precursor_index still points at a real emitted ordinal.
            var dirA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dirA);
            try
            {
                var archive = WriteWithInjection(dirA, failScan: 8, dropParentBeforeChild: null);

                var data = ReadAllRowGroups(ReadEntry(archive, "spectra_data.parquet"));
                var metaIdx = ReadMetadataIndices(ReadEntry(archive, "spectra_metadata.parquet"));

                Assert.That(data.SpectrumCount, Is.EqualTo(metaIdx.Length),
                    "metadata rows == data spectrum_count after the skip");
                Assert.That(metaIdx.OrderBy(x => x).ToArray(),
                    Is.EqualTo(Enumerable.Range(0, data.SpectrumCount).Select(i => (ulong)i).ToArray()),
                    "ordinals dense 0..N-1 despite the skipped scan");
                Assert.That(data.SpectrumCount, Is.EqualTo(V1SpectrumCount - 1),
                    "exactly one scan was skipped");

                var (pre, _) = ReadMsnChildMaps(ReadEntry(archive, "spectra_metadata.parquet"));
                foreach (var v in pre.Values.Where(v => v.HasValue))
                    Assert.That(v.Value, Is.LessThan((ulong)data.SpectrumCount),
                        "no precursor_index may dangle past the emitted set");
            }
            finally
            {
                Directory.Delete(dirA, true);
            }

            // Part B — the C1 isolation guard: drive the real Write loop so that, at the moment an MSn child
            // builds its precursor, its resolved parent has a positive scan number but no emitted ordinal
            // (read-but-not-committed). The child must end with NO precursor_index and NO selected-ion
            // intensity — it must not read intensity through the absent parent. Before the C1 fix the
            // intensity was computed from the parent's scan regardless, and this fails.
            const int childScan = 3; // the first MSn child in small.RAW
            var dirB = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dirB);
            try
            {
                var archive = WriteWithInjection(dirB, failScan: null, dropParentBeforeChild: childScan);
                var (pre, si) = ReadMsnChildMaps(ReadEntry(archive, "spectra_metadata.parquet"));

                // childScan is the third scan; with all scans emitted its ordinal is childScan-1.
                ulong childOrdinal = (ulong)(childScan - 1);
                Assert.That(pre.ContainsKey(childOrdinal), Is.True, "the targeted child must be present");
                Assert.That(pre[childOrdinal], Is.Null,
                    "a child whose parent was not emitted must have a null precursor_index");
                Assert.That(si[childOrdinal], Is.Null,
                    "a child with no emitted parent must not read a selected-ion intensity through it");

                // The universal C1 invariant across all children: intensity present implies parent present.
                foreach (var child in si.Keys)
                {
                    bool parentPresent = pre.TryGetValue(child, out var p) && p.HasValue;
                    if (si[child] != null)
                        Assert.That(parentPresent, Is.True,
                            "selected-ion intensity may only exist when the parent was emitted");
                }
            }
            finally
            {
                Directory.Delete(dirB, true);
            }
        }

        [Test]
        public void BadScanFile_Converts_To_Completion_With_Skip_And_Parity()
        {
            var raw = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Claude", "mzML2mzPeak", "data", "raw-replacements",
                "bruker-impact-sub__PXD076459", "S4_5foldGHRP.raw");
            if (!File.Exists(raw)) Assert.Ignore($"corpus bad-scan file not present: {raw}");

            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            var input = new ParseInput(raw, null, dir, OutputFormat.MzPeak);
            try
            {
                // An unreadable scan must NEVER abort the conversion with an unhandled throw; it is logged
                // and ParseInput.NewError()-counted instead. This corpus file is a Finnigan RAW whose
                // scan-event index is corrupt: every scan raises "Cannot get scan event". The conversion
                // must absorb every one of them without crashing.
                Assert.DoesNotThrow(() => RawFileParser.Parse(input),
                    "bad scans must not abort the conversion with an unhandled exception");
                Assert.That(input.Errors, Is.GreaterThanOrEqualTo(1),
                    "each skipped scan is error-counted");

                var archive = Path.Combine(dir, "S4_5foldGHRP.mzpeak");
                if (File.Exists(archive))
                {
                    // If any scan was readable, the produced archive must hold facet/metadata parity and
                    // dense ordinals despite the skips.
                    var data = ReadAllRowGroups(ReadEntry(archive, "spectra_data.parquet"));
                    var metaIdx = ReadMetadataIndices(ReadEntry(archive, "spectra_metadata.parquet"));

                    int distinct = data.SpectrumIndex.Distinct().Count();
                    Assert.That(distinct, Is.EqualTo(data.SpectrumCount), "distinct spectrum_index == spectrum_count");
                    Assert.That(metaIdx.Length, Is.EqualTo(data.SpectrumCount), "metadata rows == spectrum_count");
                    Assert.That(metaIdx.OrderBy(x => x).ToArray(),
                        Is.EqualTo(Enumerable.Range(0, data.SpectrumCount).Select(i => (ulong)i).ToArray()),
                        "ordinals dense 0..N-1 despite the skips");
                }
                else
                {
                    // Every scan was unreadable -> graceful completion with no output is the correct
                    // degenerate behavior (the writer raises a clean "No in-range spectrum" rather than a
                    // corrupt partial archive). The no-abort + error-count assertions above are the gate.
                    Assert.That(input.Errors, Is.GreaterThan(25000),
                        "every scan in this corrupt file is skipped and counted");
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void OrderedPairs_Preserves_Duplicate_Mz_In_Input_Order()
        {
            var masses = new[] { 100.0, 100.0, 100.0, 200.0 };
            var intensities = new[] { 11.0, 22.0, 33.0, 44.0 };

            var (mz, inten) = MzPeakSpectrumWriter.OrderedPairs(masses, intensities);

            Assert.That(mz.Length, Is.EqualTo(4), "no equal-m/z rows may be dropped or merged");
            Assert.That(mz, Is.EqualTo(new[] { 100.0, 100.0, 100.0, 200.0 }));
            Assert.That(inten, Is.EqualTo(new[] { 11f, 22f, 33f, 44f }),
                "the three 100.0 rows must retain their relative intensity order");
        }

        [Test]
        public void OrderedPairs_Sorts_Unsorted_Input_Without_Dropping_Points()
        {
            var masses = new[] { 200.0, 100.0, 100.0, 150.0 };
            var intensities = new[] { 1.0, 2.0, 3.0, 4.0 };

            var (mz, inten) = MzPeakSpectrumWriter.OrderedPairs(masses, intensities);

            Assert.That(mz, Is.EqualTo(new[] { 100.0, 100.0, 150.0, 200.0 }));
            Assert.That(inten, Is.EqualTo(new[] { 2f, 3f, 4f, 1f }),
                "duplicate 100.0 rows keep relative order; multiset preserved");
        }
    }
}
