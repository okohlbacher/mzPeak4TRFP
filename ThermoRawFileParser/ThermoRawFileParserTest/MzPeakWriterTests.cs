using System;
using System.Collections.Generic;
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
