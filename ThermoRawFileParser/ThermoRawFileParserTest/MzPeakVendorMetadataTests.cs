using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ThermoRawFileParser;
using static ThermoRawFileParserTest.MzPeakTestSupport;

namespace ThermoRawFileParserTest
{
    [TestFixture]
    public class MzPeakVendorMetadataTests
    {
        private static string TestRawFile =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "small.RAW");

        private static string Convert(bool vendor, out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            var input = new ParseInput(TestRawFile, null, dir, OutputFormat.MzPeak)
            {
                MzPeakVendorMetadata = vendor
            };
            RawFileParser.Parse(input);
            Assert.That(input.Errors, Is.EqualTo(0));
            var archive = Path.Combine(dir, "small.mzpeak");
            Assert.That(File.Exists(archive));
            return archive;
        }

        private static readonly string[] VendorFacets =
        {
            "vendor_scan_trailers.parquet", "vendor_file_metadata.parquet", "vendor_trailer_schema.parquet",
            "vendor_status_log.parquet", "vendor_error_log.parquet"
        };

        [Test]
        public void VendorMetadata_Off_By_Default_EmitsNoVendorFacets()
        {
            var archive = Convert(false, out var dir);
            try
            {
                foreach (var f in VendorFacets)
                    Assert.That(ReadEntry(archive, f), Is.Null, $"{f} must be absent without --vendor-metadata");

                var index = JObject.Parse(System.Text.Encoding.UTF8.GetString(ReadEntry(archive, "mzpeak_index.json")));
                Assert.That(((JArray)index["files"]).Any(f => (string)f["entity_type"] == "proprietary"), Is.False);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void VendorMetadata_On_EmitsVerbatimFacets_ListedInIndex()
        {
            var archive = Convert(true, out var dir);
            try
            {
                foreach (var f in VendorFacets)
                {
                    var bytes = ReadEntry(archive, f);
                    Assert.That(bytes, Is.Not.Null, $"{f} must be present with --vendor-metadata");
                    Assert.That(bytes.Length, Is.GreaterThan(0), $"{f} must be non-empty");
                }

                var index = JObject.Parse(System.Text.Encoding.UTF8.GetString(ReadEntry(archive, "mzpeak_index.json")));
                var vendor = ((JArray)index["files"]).Where(f => (string)f["entity_type"] == "proprietary")
                    .Select(f => (string)f["name"]).ToArray();
                Assert.That(vendor, Is.EquivalentTo(VendorFacets), "index lists the vendor facets as 'proprietary'");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void VendorMetadataJson_Sidecar_WrittenAndWellFormed()
        {
            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            // JSON sidecar requested (default path) WITHOUT the parquet facet flag — they are independent.
            var input = new ParseInput(TestRawFile, null, dir, OutputFormat.MzPeak) { MzPeakVendorMetadataJson = "" };
            RawFileParser.Parse(input);
            Assert.That(input.Errors, Is.EqualTo(0));
            try
            {
                var jsonPath = Path.Combine(dir, "small.vendor.json");
                Assert.That(File.Exists(jsonPath), "default <output>.vendor.json sidecar must be written");

                var o = JObject.Parse(File.ReadAllText(jsonPath));   // must be well-formed JSON
                foreach (var k in new[] { "source_file", "instrument", "sample", "run_header", "tune",
                                          "status_log_header", "instrument_methods", "trailer_schema" })
                    Assert.That(o[k], Is.Not.Null, $"vendor json must contain '{k}'");

                Assert.That((string)o["instrument"]["Model"], Is.Not.Empty);
                Assert.That(((JArray)o["trailer_schema"]).Count, Is.GreaterThan(0));
                Assert.That(((JArray)o["instrument_methods"]).Count, Is.GreaterThan(0));

                // The parquet facets were NOT requested, so they must be absent (independence).
                var archive = Path.Combine(dir, "small.mzpeak");
                Assert.That(ReadEntry(archive, "vendor_scan_trailers.parquet"), Is.Null);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void VendorMetadata_ScanTrailers_AreTall_And_Verbatim()
        {
            var archive = Convert(true, out var dir);
            try
            {
                // Tall schema (ordinal, scan_number, label, value, value_float); one row per
                // (committed spectrum, trailer label). ordinal joins the spectra facets; scan_number is
                // the verbatim Thermo identity. The metadata spectrum count must equal the distinct ordinals.
                var snippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "t = pq.read_table(r'{PARQUET}')\n" +
                    "d = t.to_pylist()\n" +
                    "o0 = d[0]['ordinal']\n" +
                    "labels = [r['label'] for r in d if r['ordinal'] == o0]\n" +
                    "inj = next((r for r in d if r['label'] == 'Ion Injection Time (ms):'), None)\n" +
                    "out = {'cols': t.schema.names, 'rows': len(d),\n" +
                    "       'distinct_ordinals': len(set(r['ordinal'] for r in d)),\n" +
                    "       'labels_first_ordinal': len(labels),\n" +
                    "       'inj_value': inj and inj['value'], 'inj_float': inj and inj['value_float']}\n" +
                    "print(json.dumps(out))\n";
                var o = PyArrow(archive, "vendor_scan_trailers.parquet", snippet);

                Assert.That(o["cols"].Select(x => (string)x).ToArray(),
                    Is.EqualTo(new[] { "ordinal", "scan_number", "label", "value", "value_float" }));
                Assert.That((int)o["rows"], Is.GreaterThan(0));
                Assert.That((int)o["labels_first_ordinal"], Is.GreaterThan(1), "many trailer labels per scan");

                // Vendor rows must cover exactly the emitted spectra (ordinal-aligned with the metadata facet).
                var meta = PyArrow(archive, "spectra_metadata.parquet",
                    "import pyarrow.parquet as pq, json\n" +
                    "print(json.dumps({'n': pq.read_table(r'{PARQUET}').num_rows}))\n");
                Assert.That((int)o["distinct_ordinals"], Is.EqualTo((int)meta["n"]),
                    "vendor trailers cover exactly the emitted spectra (no skipped scans leak in)");

                // Verbatim string preserved, with a TYPED numeric parse alongside.
                if (o["inj_value"]?.Type == JTokenType.String)
                {
                    Assert.That((string)o["inj_value"], Does.Match(@"^\d"), "injection time kept as verbatim string");
                    Assert.That((double)o["inj_float"], Is.GreaterThan(0.0));
                }
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
