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
