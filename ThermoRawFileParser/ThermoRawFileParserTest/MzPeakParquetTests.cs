using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ThermoRawFileParser.Writer;

namespace ThermoRawFileParserTest
{
    [TestFixture]
    public class MzPeakParquetTests
    {
        private static DataField Leaf(ParquetSchema schema, string dotPath)
        {
            return schema.GetDataFields().First(d => d.Path.ToString() == dotPath);
        }

        [Test]
        public void CvColumn_EmbedsAccessionAndLabel()
        {
            Assert.That(MzPeakParquet.CvColumn("MS:1000511", "ms_level"),
                Is.EqualTo("MS_1000511_ms_level"));
            Assert.That(MzPeakParquet.CvColumn("MS:1000016", "scan_start_time", "UO:0000031"),
                Is.EqualTo("MS_1000016_scan_start_time_unit_UO_0000031"));
        }

        [Test]
        public void BuildParamField_LeafOrder()
        {
            var field = MzPeakParquet.BuildParamField("parameters");
            var schema = new ParquetSchema(new StructField("row", field));
            var leaves = schema.GetDataFields().Select(d => d.Path.ToString()).ToArray();

            Assert.That(leaves, Is.EqualTo(new[]
            {
                "row/parameters/value/integer",
                "row/parameters/value/float",
                "row/parameters/value/string",
                "row/parameters/value/boolean",
                "row/parameters/accession",
                "row/parameters/name",
                "row/parameters/unit"
            }));
        }

        [Test]
        public void Column_DefRepLevels_MatchCheatSheet()
        {
            var paramItem = new StructField("item",
                new DataField<string>("name"),
                new DataField<double>("val", true));
            var paramsList = new ListField("parameters", paramItem);
            var spectrum = new StructField("spectrum",
                new DataField<ulong>("index"), paramsList);
            var schema = new ParquetSchema(spectrum);

            var nameLeaf = Leaf(schema, "spectrum/parameters/list/item/name");
            var indexLeaf = Leaf(schema, "spectrum/index");

            var nameCol = MzPeakParquet.Column(nameLeaf, new[] { "a", "b" },
                new[] { 5, 5, 0, 2 }, new[] { 0, 1, 0, 0 });
            var indexCol = MzPeakParquet.Column(indexLeaf, new ulong[] { 10, 11 },
                new[] { 1, 0, 1 }, null);

            Assert.That(nameCol.DefinitionLevels, Is.EqualTo(new[] { 5, 5, 0, 2 }));
            Assert.That(nameCol.RepetitionLevels, Is.EqualTo(new[] { 0, 1, 0, 0 }));
            Assert.That(indexCol.DefinitionLevels, Is.EqualTo(new[] { 1, 0, 1 }));
            Assert.That(indexCol.RepetitionLevels, Is.Null);
        }

        [Test]
        public void RoundTrip_NestedStruct_ListOfStruct_ParallelNullableTopLevel()
        {
            var mzRange = new StructField("mz_range",
                new DataField<double>("lo"), new DataField<double>("hi"));
            var paramItem = new StructField("item",
                new DataField<string>("name"), new DataField<double>("val", true));
            var paramsList = new ListField("parameters", paramItem);
            var spectrum = new StructField("spectrum",
                new DataField<ulong>("index"), mzRange, paramsList);
            var scan = new StructField("scan",
                new DataField<ulong>("source_index"), new DataField<float>("rt"));
            var schema = new ParquetSchema(spectrum, scan);

            var cols = new Dictionary<DataField, (Array, int[], int[])>
            {
                [Leaf(schema, "spectrum/index")] = (new ulong[] { 10, 11 }, new[] { 1, 0, 1 }, null),
                [Leaf(schema, "spectrum/mz_range/lo")] = (new[] { 100.5, 50.0 }, new[] { 2, 0, 2 }, null),
                [Leaf(schema, "spectrum/mz_range/hi")] = (new[] { 2000.0, 60.0 }, new[] { 2, 0, 2 }, null),
                [Leaf(schema, "spectrum/parameters/list/item/name")] =
                    (new[] { "a", "b" }, new[] { 5, 5, 0, 2 }, new[] { 0, 1, 0, 0 }),
                [Leaf(schema, "spectrum/parameters/list/item/val")] =
                    (new[] { 1.5 }, new[] { 5, 4, 0, 2 }, new[] { 0, 1, 0, 0 }),
                [Leaf(schema, "scan/source_index")] = (new ulong[] { 10 }, new[] { 0, 1, 0 }, null),
                [Leaf(schema, "scan/rt")] = (new[] { 1.23f }, new[] { 0, 1, 0 }, null)
            };

            var ms = new MemoryStream();
            MzPeakParquet.WriteAsync(ms, schema, new Dictionary<string, string> { ["k"] = "v" },
                cols.ToDictionary(kv => kv.Key, kv => kv.Value)).GetAwaiter().GetResult();

            ms.Position = 0;
            using (var reader = ParquetReader.CreateAsync(ms).Result)
            {
                var rg = reader.OpenRowGroupReader(0);

                // On read, Data holds only the defined values; nulls live in DefinitionLevels.
                var indexCol = rg.ReadColumnAsync(Leaf(schema, "spectrum/index")).Result;
                Assert.That(indexCol.Data, Is.EqualTo(new ulong[] { 10, 11 }));
                Assert.That(indexCol.DefinitionLevels, Is.EqualTo(new[] { 1, 0, 1 }));

                var loCol = rg.ReadColumnAsync(Leaf(schema, "spectrum/mz_range/lo")).Result;
                Assert.That(loCol.Data, Is.EqualTo(new[] { 100.5, 50.0 }));
                Assert.That(loCol.DefinitionLevels, Is.EqualTo(new[] { 2, 0, 2 }));

                var scanCol = rg.ReadColumnAsync(Leaf(schema, "scan/source_index")).Result;
                Assert.That(scanCol.Data, Is.EqualTo(new ulong[] { 10 }));
                Assert.That(scanCol.DefinitionLevels, Is.EqualTo(new[] { 0, 1, 0 }));

                // list-of-struct: 2 items in row0 (second val null), empty list in row2.
                // String leaves read back with nulls embedded inline; def levels are the lock.
                var nameCol = rg.ReadColumnAsync(Leaf(schema, "spectrum/parameters/list/item/name")).Result;
                Assert.That(nameCol.Data, Is.EqualTo(new[] { "a", "b", null, null }));
                Assert.That(nameCol.DefinitionLevels, Is.EqualTo(new[] { 5, 5, 0, 2 }));
                Assert.That(nameCol.RepetitionLevels, Is.EqualTo(new[] { 0, 1, 0, 0 }));

                // val is an explicitly-nullable leaf; it reads back with nulls embedded inline.
                var valCol = rg.ReadColumnAsync(Leaf(schema, "spectrum/parameters/list/item/val")).Result;
                Assert.That(valCol.Data, Is.EqualTo(new double?[] { 1.5, null, null, null }));
                Assert.That(valCol.DefinitionLevels, Is.EqualTo(new[] { 5, 4, 0, 2 }));
            }
        }

        [Test]
        public void RoundTrip_Param_NullableAccessionUnitString()
        {
            var spectrum = new StructField("spectrum", MzPeakParquet.BuildParamField("p"));
            var schema = new ParquetSchema(spectrum);

            // Union-as-struct discipline: each PARAM row populates EXACTLY ONE of the four value
            // leaves; the other three are null. Row0 populates integer; row1 populates string.
            // value/* leaves: maxDef=4 (4=non-null, 3=null). accession/name/unit: maxDef=3 (3=non-null, 2=null).
            var cols = new Dictionary<DataField, (Array, int[], int[])>
            {
                [Leaf(schema, "spectrum/p/value/integer")] = (new long[] { 7 }, new[] { 4, 3 }, null),
                [Leaf(schema, "spectrum/p/value/float")] = (new double[0], new[] { 3, 3 }, null),
                [Leaf(schema, "spectrum/p/value/string")] = (new[] { "hello" }, new[] { 3, 4 }, null),
                [Leaf(schema, "spectrum/p/value/boolean")] = (new bool[0], new[] { 3, 3 }, null),
                [Leaf(schema, "spectrum/p/accession")] = (new[] { "MS:1000511" }, new[] { 3, 2 }, null),
                [Leaf(schema, "spectrum/p/name")] = (new[] { "ms level", "intensity" }, new[] { 3, 3 }, null),
                [Leaf(schema, "spectrum/p/unit")] = (new[] { "UO:0000031" }, new[] { 3, 2 }, null)
            };

            var ms = new MemoryStream();
            MzPeakParquet.WriteAsync(ms, schema, new Dictionary<string, string>(),
                cols.ToDictionary(kv => kv.Key, kv => kv.Value)).GetAwaiter().GetResult();

            ms.Position = 0;
            using (var reader = ParquetReader.CreateAsync(ms).Result)
            {
                var rg = reader.OpenRowGroupReader(0);

                // integer: row0 populated (def 4), row1 null (def 3).
                var integer = rg.ReadColumnAsync(Leaf(schema, "spectrum/p/value/integer")).Result;
                Assert.That(integer.Data, Is.EqualTo(new long?[] { 7, null }));
                Assert.That(integer.DefinitionLevels, Is.EqualTo(new[] { 4, 3 }));

                // string: row0 null (def 3), row1 populated (def 4).
                var str = rg.ReadColumnAsync(Leaf(schema, "spectrum/p/value/string")).Result;
                Assert.That(str.Data, Is.EqualTo(new[] { null, "hello" }));
                Assert.That(str.DefinitionLevels, Is.EqualTo(new[] { 3, 4 }));

                // float never populated: both rows null (def 3).
                var fl = rg.ReadColumnAsync(Leaf(schema, "spectrum/p/value/float")).Result;
                Assert.That(fl.DefinitionLevels, Is.EqualTo(new[] { 3, 3 }));

                // boolean never populated: both rows null (def 3).
                var boolean = rg.ReadColumnAsync(Leaf(schema, "spectrum/p/value/boolean")).Result;
                Assert.That(boolean.DefinitionLevels, Is.EqualTo(new[] { 3, 3 }));

                // accession: row0 present (def 3), row1 null (def 2).
                var acc = rg.ReadColumnAsync(Leaf(schema, "spectrum/p/accession")).Result;
                Assert.That(acc.Data, Is.EqualTo(new[] { "MS:1000511", null }));
                Assert.That(acc.DefinitionLevels, Is.EqualTo(new[] { 3, 2 }));

                var unit = rg.ReadColumnAsync(Leaf(schema, "spectrum/p/unit")).Result;
                Assert.That(unit.Data, Is.EqualTo(new[] { "UO:0000031", null }));
                Assert.That(unit.DefinitionLevels, Is.EqualTo(new[] { 3, 2 }));

                // name populated on both rows.
                var name = rg.ReadColumnAsync(Leaf(schema, "spectrum/p/name")).Result;
                Assert.That(name.Data, Is.EqualTo(new[] { "ms level", "intensity" }));
            }
        }

        [Test]
        public void WrittenFile_ReportsZstdCompression()
        {
            var spectrum = new StructField("spectrum", new DataField<ulong>("index"));
            var schema = new ParquetSchema(spectrum);
            var cols = new Dictionary<DataField, (Array, int[], int[])>
            {
                [Leaf(schema, "spectrum/index")] = (new ulong[] { 0 }, new[] { 1 }, null)
            };

            var ms = new MemoryStream();
            MzPeakParquet.WriteAsync(ms, schema, new Dictionary<string, string>(),
                cols.ToDictionary(kv => kv.Key, kv => kv.Value)).GetAwaiter().GetResult();

            ms.Position = 0;
            using (var reader = ParquetReader.CreateAsync(ms).Result)
            {
                var columns = reader.Metadata.RowGroups[0].Columns;
                Assert.That(columns, Is.Not.Empty);
                foreach (var c in columns)
                    Assert.That(c.MetaData.Codec, Is.EqualTo(Parquet.Meta.CompressionCodec.ZSTD));

                var rg = reader.OpenRowGroupReader(0);
                var col = rg.ReadColumnAsync(Leaf(schema, "spectrum/index")).Result;
                Assert.That(col.Data, Is.EqualTo(new ulong[] { 0 }));
            }
        }
    }
}
