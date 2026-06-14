using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public void NestedLevels_Reproduces_CheatSheet_Levels()
        {
            var paramItem = new StructField("item",
                new DataField<string>("name"), new DataField<double>("val", true));
            var paramsList = new ListField("parameters", paramItem);
            var mzRange = new StructField("mz_range",
                new DataField<double>("lo"), new DataField<double>("hi"));
            var spectrum = new StructField("spectrum",
                new DataField<ulong>("index"), mzRange, paramsList);
            var schema = new ParquetSchema(spectrum);

            var indexLeaf = Leaf(schema, "spectrum/index");
            var loLeaf = Leaf(schema, "spectrum/mz_range/lo");
            var nameLeaf = Leaf(schema, "spectrum/parameters/list/item/name");
            var valLeaf = Leaf(schema, "spectrum/parameters/list/item/val");

            // top struct leaf: max-def 1 -> [1,0,1]
            Assert.That(indexLeaf.MaxDefinitionLevel, Is.EqualTo(1));
            var (idxDef, idxRep) = MzPeakParquet.NestedLevels(indexLeaf, new[]
            {
                MzPeakParquet.Present(indexLeaf), MzPeakParquet.Absent(), MzPeakParquet.Present(indexLeaf)
            });
            Assert.That(idxDef, Is.EqualTo(new[] { 1, 0, 1 }));
            Assert.That(idxRep, Is.Null);

            // nested struct-in-struct leaf: max-def 2 -> [2,0,2]
            Assert.That(loLeaf.MaxDefinitionLevel, Is.EqualTo(2));
            var (loDef, _) = MzPeakParquet.NestedLevels(loLeaf, new[]
            {
                MzPeakParquet.Present(loLeaf), MzPeakParquet.Absent(), MzPeakParquet.Present(loLeaf)
            });
            Assert.That(loDef, Is.EqualTo(new[] { 2, 0, 2 }));

            // list-of-struct leaves: max-def 5; present-elem 5, leaf-null-in-elem 4, empty-list 2, null-list 0
            Assert.That(nameLeaf.MaxDefinitionLevel, Is.EqualTo(5));
            Assert.That(paramsList.MaxDefinitionLevel, Is.EqualTo(3));
            // empty-list level for these leaves is the cheat-sheet 2 (= list MaxDef - 1)
            Assert.That(MzPeakParquet.EmptyList(paramsList).Cells[0], Is.EqualTo(2));
            // row0: 2 elements (both name present); row1: null list; row2: empty list
            var (nDef, nRep) = MzPeakParquet.NestedLevels(nameLeaf, new[]
            {
                MzPeakParquet.ListOf(new[] { 5, 5 }, new[] { true, true }),
                MzPeakParquet.NullList(),
                MzPeakParquet.EmptyList(paramsList)
            });
            Assert.That(nDef, Is.EqualTo(new[] { 5, 5, 0, 2 }));
            Assert.That(nRep, Is.EqualTo(new[] { 0, 1, 0, 0 }));

            // val: row0 elem0 present (5), elem1 null (4); row1 null list; row2 empty list
            var (vDef, vRep) = MzPeakParquet.NestedLevels(valLeaf, new[]
            {
                MzPeakParquet.ListOf(new[] { 5, 4 }, new[] { true, false }),
                MzPeakParquet.NullList(),
                MzPeakParquet.EmptyList(paramsList)
            });
            Assert.That(vDef, Is.EqualTo(new[] { 5, 4, 0, 2 }));
            Assert.That(vRep, Is.EqualTo(new[] { 0, 1, 0, 0 }));
        }

        [Test]
        public void NestedLevels_RoundTrips_SpectraMetadataFacetShapes_ViaPyArrow()
        {
            var python = ResolvePython();
            if (python == null) Assert.Ignore("python3/pyarrow not available");

            // precursor top-level struct: isolation_window (nested struct) + activation.parameters (PARAM list).
            // scan top-level struct: scan_windows (list-of-struct). Mirrors the real mzPeak
            // spectra-metadata facet shapes.
            var isoWindow = new StructField("isolation_window",
                new DataField<float>("target"), new DataField<float>("lower_offset"));
            var activation = new StructField("activation",
                new ListField("parameters", MzPeakParquet.BuildParamField("item")));
            var precursor = new StructField("precursor",
                new DataField<ulong>("source_index"), isoWindow, activation);

            var windowItem = new StructField("item",
                new DataField<float>("lower"), new DataField<float>("upper"));
            var scanWindows = new ListField("scan_windows", windowItem);
            var scan = new StructField("scan",
                new DataField<ulong>("source_index"), scanWindows);

            var schema = new ParquetSchema(precursor, scan);

            // 4 rows. precursor present on rows 0,1,2 and NULL on row 3 (padded tail).
            //   row0: iso present; activation present; activation.parameters = 2 items (CID accession, CE float)
            //   row1: iso present; activation present; activation.parameters = EMPTY list
            //   row2: iso present; activation PRESENT; activation.parameters = NULL list (struct-present, list-null)
            //   row3: precursor struct NULL (root absent for this facet)
            // rows 2 and 3 are the level-aware discriminator: activation!=null && parameters==null must
            // encode a DIFFERENT definition level than activation==null / precursor==null.
            var srcIdx = Leaf(schema, "precursor/source_index");
            var isoTarget = Leaf(schema, "precursor/isolation_window/target");
            var actAcc = Leaf(schema, "precursor/activation/parameters/list/item/accession");
            var actFloat = Leaf(schema, "precursor/activation/parameters/list/item/value/float");
            var actList = (ListField)((StructField)((StructField)schema.Fields
                .First(f => f.Name == "precursor")).Fields.First(f => f.Name == "activation"))
                .Fields.First(f => f.Name == "parameters");

            var winLower = Leaf(schema, "scan/scan_windows/list/item/lower");
            var scanSrc = Leaf(schema, "scan/source_index");

            var (srcDef, _) = MzPeakParquet.NestedLevels(srcIdx, new[]
            {
                MzPeakParquet.Present(srcIdx), MzPeakParquet.Present(srcIdx),
                MzPeakParquet.Present(srcIdx), MzPeakParquet.Absent()
            });
            var (isoDef, _) = MzPeakParquet.NestedLevels(isoTarget, new[]
            {
                MzPeakParquet.Present(isoTarget), MzPeakParquet.Present(isoTarget),
                MzPeakParquet.Present(isoTarget), MzPeakParquet.Absent()
            });
            // accession: row0 both elements present; row1 empty; row2 struct-present-list-null; row3 precursor null
            var (accDef, accRep) = MzPeakParquet.NestedLevels(actAcc, new[]
            {
                MzPeakParquet.ListOf(new[] { actAcc.MaxDefinitionLevel, actAcc.MaxDefinitionLevel }, new[] { true, true }),
                MzPeakParquet.EmptyList(actList),
                MzPeakParquet.NullList(actList),
                MzPeakParquet.NullList()
            });
            // value/float: row0 elem0 null (CID has no float), elem1 present (CE)
            var (fltDef, fltRep) = MzPeakParquet.NestedLevels(actFloat, new[]
            {
                MzPeakParquet.ListOf(new[] { actFloat.MaxDefinitionLevel - 1, actFloat.MaxDefinitionLevel }, new[] { false, true }),
                MzPeakParquet.EmptyList(actList),
                MzPeakParquet.NullList(actList),
                MzPeakParquet.NullList()
            });
            // scan present on all 4 rows; one window each
            var (scanSrcDef, _) = MzPeakParquet.NestedLevels(scanSrc, new[]
            {
                MzPeakParquet.Present(scanSrc), MzPeakParquet.Present(scanSrc),
                MzPeakParquet.Present(scanSrc), MzPeakParquet.Present(scanSrc)
            });
            var (winDef, winRep) = MzPeakParquet.NestedLevels(winLower, Enumerable.Range(0, 4)
                .Select(_ => MzPeakParquet.ListOf(new[] { winLower.MaxDefinitionLevel }, new[] { true })).ToArray());

            var cols = new Dictionary<DataField, (Array, int[], int[])>
            {
                [srcIdx] = (new ulong[] { 2, 3, 4 }, srcDef, null),
                [isoTarget] = (new[] { 810.7f, 837.3f, 725.3f }, isoDef, null),
                [Leaf(schema, "precursor/isolation_window/lower_offset")] =
                    (new[] { 1.0f, 1.0f, 1.0f }, isoDef, null),
                [actAcc] = (new[] { "MS:1000133", "MS:1000045" }, accDef, accRep),
                [Leaf(schema, "precursor/activation/parameters/list/item/value/integer")] =
                    (new long[0], NullLeaf(schema, "precursor/activation/parameters/list/item/value/integer", accDef, accRep), accRep),
                [actFloat] = (new[] { 35.0 }, fltDef, fltRep),
                [Leaf(schema, "precursor/activation/parameters/list/item/value/string")] =
                    (new string[0], NullLeaf(schema, "precursor/activation/parameters/list/item/value/string", accDef, accRep), accRep),
                [Leaf(schema, "precursor/activation/parameters/list/item/value/boolean")] =
                    (new bool[0], NullLeaf(schema, "precursor/activation/parameters/list/item/value/boolean", accDef, accRep), accRep),
                [Leaf(schema, "precursor/activation/parameters/list/item/name")] =
                    (new[] { "collision-induced dissociation", "collision energy" }, accDef, accRep),
                [Leaf(schema, "precursor/activation/parameters/list/item/unit")] =
                    (new[] { "UO:0000266" }, UnitLevels(accDef, actAcc.MaxDefinitionLevel), accRep),
                [scanSrc] = (new ulong[] { 0, 1, 2, 3 }, scanSrcDef, null),
                [winLower] = (new[] { 200f, 200f, 210f, 210f }, winDef, winRep),
                [Leaf(schema, "scan/scan_windows/list/item/upper")] =
                    (new[] { 2000f, 2000f, 1635f, 1635f }, winDef, winRep)
            };

            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".parquet");
            using (var fs = File.Create(path))
            {
                MzPeakParquet.WriteAsync(fs, schema, new Dictionary<string, string>(),
                    cols.ToDictionary(kv => kv.Key, kv => kv.Value)).GetAwaiter().GetResult();
            }

            try
            {
                var py = $@"
import pyarrow.parquet as pq, json
t = pq.read_table(r'{path}')
d = t.to_pylist()
out = {{}}
out['rows'] = len(d)
out['precursor_present'] = [r['precursor'] is not None and r['precursor']['source_index'] is not None for r in d]
out['activation_present'] = [ (r['precursor'] is not None and r['precursor']['activation'] is not None) for r in d ]
out['params_null'] = [ (r['precursor'] is None or r['precursor']['activation'] is None or r['precursor']['activation']['parameters'] is None) for r in d ]
out['activation_lens'] = [ (None if (r['precursor'] is None or r['precursor']['activation'] is None or r['precursor']['activation']['parameters'] is None) else len(r['precursor']['activation']['parameters'])) for r in d ]
out['scan_window_lens'] = [ (None if (r['scan'] is None or r['scan']['scan_windows'] is None) else len(r['scan']['scan_windows'])) for r in d ]
p0 = d[0]['precursor']['activation']['parameters']
out['row0_accessions'] = [e['accession'] for e in p0]
out['row0_floats'] = [ (None if e['value']['float'] is None else e['value']['float']) for e in p0 ]
out['row0_iso_target'] = round(d[0]['precursor']['isolation_window']['target'], 1)
out['row0_window_lower'] = d[0]['scan']['scan_windows'][0]['lower']
print(json.dumps(out))
";
                var psi = new ProcessStartInfo(python, "-c " + Quote(py))
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
                    Assert.That(proc.ExitCode, Is.EqualTo(0), $"pyarrow read failed: {stderr}");

                    var o = Newtonsoft.Json.Linq.JObject.Parse(stdout.Trim());
                    Assert.That((int)o["rows"], Is.EqualTo(4));
                    Assert.That(o["precursor_present"].Select(x => (bool)x).ToArray(),
                        Is.EqualTo(new[] { true, true, true, false }));
                    // null list and empty list distinguished: row1 empty (0), rows 2&3 null (None)
                    var actLens = o["activation_lens"].Select(x => x.Type == Newtonsoft.Json.Linq.JTokenType.Null ? (int?)null : (int)x).ToArray();
                    Assert.That(actLens, Is.EqualTo(new int?[] { 2, 0, null, null }));

                    // LEVEL-AWARE null list (H1): row2 has activation PRESENT with a null parameters list,
                    // row3 has the whole precursor (hence activation) absent. These two must be encoded at
                    // DIFFERENT definition levels so a reader can tell present-struct-null-list from
                    // absent-struct -- not collapsed to a flat def=0.
                    var actPresent = o["activation_present"].Select(x => (bool)x).ToArray();
                    var paramsNull = o["params_null"].Select(x => (bool)x).ToArray();
                    Assert.That(actPresent, Is.EqualTo(new[] { true, true, true, false }),
                        "row2 activation present with null params; row3 activation absent (precursor null)");
                    Assert.That(paramsNull, Is.EqualTo(new[] { false, false, true, true }),
                        "parameters list null on rows 2 (struct-present) and 3 (struct-absent)");
                    Assert.That(actPresent[2] && paramsNull[2], Is.True,
                        "activation != null && parameters == null is distinguishable from activation == null");
                    Assert.That(actPresent[3], Is.False,
                        "activation == null on row3 must NOT read back as a present struct");
                    var winLens = o["scan_window_lens"].Select(x => (int)x).ToArray();
                    Assert.That(winLens, Is.EqualTo(new[] { 1, 1, 1, 1 }));
                    Assert.That(o["row0_accessions"].Select(x => (string)x).ToArray(),
                        Is.EqualTo(new[] { "MS:1000133", "MS:1000045" }));
                    var floats = o["row0_floats"].Select(x => x.Type == Newtonsoft.Json.Linq.JTokenType.Null ? (double?)null : (double)x).ToArray();
                    Assert.That(floats, Is.EqualTo(new double?[] { null, 35.0 }));
                    Assert.That((double)o["row0_iso_target"], Is.EqualTo(810.7).Within(0.05));
                    Assert.That((float)o["row0_window_lower"], Is.EqualTo(200f));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        // For a value/* sibling leaf never populated in any element, every element entry is one level
        // below the present level (null inside present element) mirroring the accession's rep pattern.
        private static int[] NullLeaf(ParquetSchema schema, string path, int[] siblingDef, int[] siblingRep)
        {
            var leaf = Leaf(schema, path);
            int present = leaf.MaxDefinitionLevel;
            var def = new int[siblingDef.Length];
            for (int i = 0; i < siblingDef.Length; i++)
            {
                // mirror sibling: element-present sibling levels become leaf-null (present-1);
                // empty/null list levels carry through unchanged.
                def[i] = siblingDef[i] >= present ? present - 1 : siblingDef[i];
            }
            return def;
        }

        private static int[] UnitLevels(int[] accDef, int present)
        {
            // unit present only for the collision-energy element (2nd entry); first present element
            // (CID) has no unit -> present-1. Empty/null entries carry through unchanged.
            var def = (int[])accDef.Clone();
            int seenPresent = 0;
            for (int i = 0; i < def.Length; i++)
            {
                if (def[i] == present)
                {
                    seenPresent++;
                    if (seenPresent == 1) def[i] = present - 1; // CID: no unit
                }
            }
            return def;
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

        private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

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

        private static ParquetSchema PointSchema()
        {
            var point = new StructField("point",
                new DataField<ulong>("spectrum_index"),
                new DataField<double>("mz"),
                new DataField<float>("intensity"));
            return new ParquetSchema(point);
        }

        private static Dictionary<DataField, (Array, int[], int[])> PointSlice(ParquetSchema schema,
            ulong[] index, double[] mz, float[] intensity)
        {
            return new Dictionary<DataField, (Array, int[], int[])>
            {
                [Leaf(schema, "point/spectrum_index")] = (index, null, null),
                [Leaf(schema, "point/mz")] = (mz, null, null),
                [Leaf(schema, "point/intensity")] = (intensity, null, null)
            };
        }

        [Test]
        public void MultiRowGroupRoundTrip_EqualsSingleRowGroup()
        {
            var schema = PointSchema();
            var idx = new ulong[] { 0, 0, 1, 1, 2, 2 };
            var mz = new[] { 100.0, 200.0, 110.0, 210.0, 120.0, 220.0 };
            var inten = new[] { 1f, 2f, 3f, 4f, 5f, 6f };

            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".parquet");
            try
            {
                using (var fs = File.Create(path))
                {
                    var handle = MzPeakParquet.OpenAsync(fs, schema, null).GetAwaiter().GetResult();
                    handle.WriteRowGroupAsync(schema, PointSlice(schema,
                        idx.Take(3).ToArray(), mz.Take(3).ToArray(), inten.Take(3).ToArray()))
                        .GetAwaiter().GetResult();
                    handle.WriteRowGroupAsync(schema, PointSlice(schema,
                        idx.Skip(3).ToArray(), mz.Skip(3).ToArray(), inten.Skip(3).ToArray()))
                        .GetAwaiter().GetResult();
                    handle.CloseAsync(null).GetAwaiter().GetResult();
                }

                var single = new MemoryStream();
                MzPeakParquet.WriteAsync(single, schema, null,
                    PointSlice(schema, idx, mz, inten)).GetAwaiter().GetResult();

                ulong[] gIdx; double[] gMz; float[] gInt;
                int rowGroupCount;
                using (var fs = File.OpenRead(path))
                using (var reader = ParquetReader.CreateAsync(fs).Result)
                {
                    rowGroupCount = reader.RowGroupCount;
                    var ai = new List<ulong>(); var am = new List<double>(); var an = new List<float>();
                    for (int g = 0; g < reader.RowGroupCount; g++)
                    {
                        var rg = reader.OpenRowGroupReader(g);
                        ai.AddRange(rg.ReadColumnAsync(Leaf(schema, "point/spectrum_index")).Result.Data.Cast<ulong>());
                        am.AddRange(rg.ReadColumnAsync(Leaf(schema, "point/mz")).Result.Data.Cast<double>());
                        an.AddRange(rg.ReadColumnAsync(Leaf(schema, "point/intensity")).Result.Data.Cast<float>());
                    }
                    gIdx = ai.ToArray(); gMz = am.ToArray(); gInt = an.ToArray();
                }

                Assert.That(rowGroupCount, Is.GreaterThan(1), "multi-row-group output must have >1 row group");

                single.Position = 0;
                using (var reader = ParquetReader.CreateAsync(single).Result)
                {
                    var rg = reader.OpenRowGroupReader(0);
                    var sIdx = rg.ReadColumnAsync(Leaf(schema, "point/spectrum_index")).Result.Data.Cast<ulong>().ToArray();
                    var sMz = rg.ReadColumnAsync(Leaf(schema, "point/mz")).Result.Data.Cast<double>().ToArray();
                    var sInt = rg.ReadColumnAsync(Leaf(schema, "point/intensity")).Result.Data.Cast<float>().ToArray();
                    Assert.That(gIdx, Is.EqualTo(sIdx));
                    Assert.That(gMz, Is.EqualTo(sMz));
                    Assert.That(gInt, Is.EqualTo(sInt));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void FinalMetadataSetAfterRowGroups_LandsInFooter()
        {
            var schema = PointSchema();
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".parquet");
            try
            {
                using (var fs = File.Create(path))
                {
                    var handle = MzPeakParquet.OpenAsync(fs, schema, null).GetAwaiter().GetResult();
                    handle.WriteRowGroupAsync(schema, PointSlice(schema,
                        new ulong[] { 0 }, new[] { 1.0 }, new[] { 1f })).GetAwaiter().GetResult();
                    handle.WriteRowGroupAsync(schema, PointSlice(schema,
                        new ulong[] { 1 }, new[] { 2.0 }, new[] { 2f })).GetAwaiter().GetResult();
                    var final = new Dictionary<string, string>
                    {
                        ["spectrum_count"] = "2",
                        ["spectrum_data_point_count"] = "2",
                        ["chromatogram_tic_source"] = "device"
                    };
                    handle.CloseAsync(final).GetAwaiter().GetResult();
                }

                using (var fs = File.OpenRead(path))
                using (var reader = ParquetReader.CreateAsync(fs).Result)
                {
                    var kv = reader.CustomMetadata;
                    Assert.That(kv["spectrum_count"], Is.EqualTo("2"));
                    Assert.That(kv["spectrum_data_point_count"], Is.EqualTo("2"));
                    Assert.That(kv["chromatogram_tic_source"], Is.EqualTo("device"));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void NonSeekableSinkRejected()
        {
            var schema = PointSchema();
            using (var nonSeekable = new NonSeekableStream())
            {
                Assert.That(() => MzPeakParquet.OpenAsync(nonSeekable, schema, null).GetAwaiter().GetResult(),
                    Throws.ArgumentException);
            }

            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".parquet");
            try
            {
                using (var fs = File.Create(path))
                {
                    var handle = MzPeakParquet.OpenAsync(fs, schema, null).GetAwaiter().GetResult();
                    Assert.That(handle, Is.Not.Null, "a seekable FileStream is accepted");
                    handle.CloseAsync(null).GetAwaiter().GetResult();
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        private sealed class NonSeekableStream : MemoryStream
        {
            public override bool CanSeek => false;
        }
    }
}
