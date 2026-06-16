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
            { "vendor_scan_trailers.parquet", "vendor_file_metadata.parquet", "vendor_trailer_schema.parquet" };

        [Test]
        public void VendorMetadata_Off_By_Default_EmitsNoVendorFacets()
        {
            var archive = Convert(false, out var dir);
            try
            {
                foreach (var f in VendorFacets)
                    Assert.That(ReadEntry(archive, f), Is.Null, $"{f} must be absent without --vendor-metadata");

                var index = JObject.Parse(System.Text.Encoding.UTF8.GetString(ReadEntry(archive, "mzpeak_index.json")));
                Assert.That(((JArray)index["files"]).Any(f => (string)f["entity_type"] == "vendor"), Is.False);
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
                var vendor = ((JArray)index["files"]).Where(f => (string)f["entity_type"] == "vendor")
                    .Select(f => (string)f["name"]).ToArray();
                Assert.That(vendor, Is.EquivalentTo(VendorFacets), "index lists the three vendor facets");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void VendorMetadata_ScanTrailers_AreTall_And_Verbatim()
        {
            var archive = Convert(true, out var dir);
            try
            {
                // Tall schema (scan_index, label, value, value_float); one row per (scan, trailer label).
                var snippet =
                    "import pyarrow.parquet as pq, json\n" +
                    "t = pq.read_table(r'{PARQUET}')\n" +
                    "d = t.to_pylist()\n" +
                    "labels = [r['label'] for r in d if r['scan_index'] == d[0]['scan_index']]\n" +
                    "inj = next((r for r in d if r['label'] == 'Ion Injection Time (ms):'), None)\n" +
                    "out = {'cols': t.schema.names, 'rows': len(d),\n" +
                    "       'labels_first_scan': len(labels),\n" +
                    "       'inj_value': inj and inj['value'], 'inj_float': inj and inj['value_float']}\n" +
                    "print(json.dumps(out))\n";
                var o = PyArrow(archive, "vendor_scan_trailers.parquet", snippet);

                Assert.That(o["cols"].Select(x => (string)x).ToArray(),
                    Is.EqualTo(new[] { "scan_index", "label", "value", "value_float" }));
                Assert.That((int)o["rows"], Is.GreaterThan(0));
                Assert.That((int)o["labels_first_scan"], Is.GreaterThan(1), "many trailer labels per scan");
                // Verbatim string preserved, with a best-effort numeric parse alongside.
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
