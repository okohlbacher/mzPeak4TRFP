using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parquet.Data;
using Parquet.Schema;

namespace ThermoRawFileParser.Writer
{
    // The column-emit helper family shared by the metadata and chromatogram facet builders. Each
    // method resolves a leaf (or list) by path and stages its values + nested definition/repetition
    // levels into the column dictionary. Helpers that record CV prefixes take an explicit collectPrefix
    // callback so the emitted bytes (and cv_list ordering) are independent of any instance state.
    internal static class MzPeakColumns
    {
        internal static DataField Leaf(ParquetSchema schema, string path)
        {
            foreach (var d in schema.GetDataFields())
                if (d.Path.ToString() == path) return d;
            throw new RawFileParserException($"Leaf not found: {path}");
        }

        internal static Field FindField(ParquetSchema schema, string path)
        {
            var parts = path.Split('/');
            Field current = schema.Fields.First(f => f.Name == parts[0]);
            for (int i = 1; i < parts.Length; i++)
            {
                current = ((StructField)current).Fields.First(f => f.Name == parts[i]);
            }
            return current;
        }

        internal static byte[] WriteFacet(ParquetSchema schema, IReadOnlyDictionary<string, string> meta,
            IDictionary<DataField, (Array, int[], int[])> cols)
        {
            using (var ms = new MemoryStream())
            {
                MzPeakParquet.WriteAsync(ms, schema, meta, cols).GetAwaiter().GetResult();
                return ms.ToArray();
            }
        }

        internal static Array EmptyArray(Type clr) => Array.CreateInstance(clr, 0);

        internal static void AddScalar(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            string path, Array values, bool[] present)
        {
            var leaf = Leaf(schema, path);
            var rows = present.Select(p => p ? MzPeakParquet.Present(leaf) : MzPeakParquet.Absent()).ToArray();
            var (def, _r) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (values, def, null);
        }

        // A scalar leaf present on every row's owning struct but whose VALUE may be absent: a null
        // value emits the leaf-null definition level (one below the present level) and contributes no
        // value slot, so a genuinely-absent value is encoded as null rather than zero.
        internal static void AddNullableScalar<T>(IDictionary<DataField, (Array, int[], int[])> cols,
            ParquetSchema schema, string path, IReadOnlyList<T?> values) where T : struct
        {
            var leaf = Leaf(schema, path);
            var rows = new List<MzPeakParquet.LeafRow>();
            var present = new List<T>();
            foreach (var v in values)
            {
                if (v.HasValue) { rows.Add(MzPeakParquet.Present(leaf)); present.Add(v.Value); }
                else rows.Add(MzPeakParquet.AtLevel(leaf.MaxDefinitionLevel - 1, false));
            }
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (present.ToArray(), def, null);
        }

        // A string leaf present on every row's owning struct but whose VALUE may be absent: a null
        // string emits the leaf-null definition level and contributes no value slot.
        internal static void AddNullableString(IDictionary<DataField, (Array, int[], int[])> cols,
            ParquetSchema schema, string path, IReadOnlyList<string> values)
        {
            var leaf = Leaf(schema, path);
            var rows = new List<MzPeakParquet.LeafRow>();
            var present = new List<string>();
            foreach (var v in values)
            {
                if (v != null) { rows.Add(MzPeakParquet.Present(leaf)); present.Add(v); }
                else rows.Add(MzPeakParquet.AtLevel(leaf.MaxDefinitionLevel - 1, false));
            }
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (present.ToArray(), def, null);
        }

        // A scalar leaf whose owning struct is present on every row but whose value is null on every
        // row (leaf-null def level, no value slot).
        internal static void AddNullLeafScalar(IDictionary<DataField, (Array, int[], int[])> cols,
            ParquetSchema schema, string path, int n)
        {
            var leaf = Leaf(schema, path);
            var rows = Enumerable.Range(0, n)
                .Select(_ => MzPeakParquet.AtLevel(leaf.MaxDefinitionLevel - 1, false)).ToArray();
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (EmptyArray(leaf.ClrType), def, null);
        }

        // A present-but-empty list on a single-row facet (the list value is defined, no elements).
        // Every descendant leaf gets the empty-list level, including auxiliary_arrays whose element
        // struct carries a non-nullable data leaf (data/list/item: uint8 not null).
        internal static void AddEmptyList(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            string listPath)
        {
            var list = (ListField)FindField(schema, listPath);
            foreach (var leaf in schema.GetDataFields())
            {
                if (!leaf.Path.ToString().StartsWith(listPath + "/")) continue;
                var rows = new[] { MzPeakParquet.EmptyList(list) };
                var (def, rep) = MzPeakParquet.NestedLevels(leaf, rows);
                cols[leaf] = (EmptyArray(leaf.ClrType), def, rep);
            }
        }

        // A present-but-empty list on every row of a multi-row facet (the owning struct is present,
        // the list value is defined, no elements). Used for spectrum auxiliary_arrays / mz_delta_model.
        internal static void AddEmptyListEveryRow(IDictionary<DataField, (Array, int[], int[])> cols,
            ParquetSchema schema, string listPath, int n)
        {
            var list = (ListField)FindField(schema, listPath);
            foreach (var leaf in schema.GetDataFields())
            {
                if (!leaf.Path.ToString().StartsWith(listPath + "/")) continue;
                var rows = Enumerable.Range(0, n).Select(_ => MzPeakParquet.EmptyList(list)).ToArray();
                var (def, rep) = MzPeakParquet.NestedLevels(leaf, rows);
                cols[leaf] = (EmptyArray(leaf.ClrType), def, rep);
            }
        }

        internal static void AddNullPrecursor(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema)
        {
            foreach (var leaf in schema.GetDataFields())
            {
                if (!leaf.Path.ToString().StartsWith("precursor/")) continue;
                AddNullLeaf(cols, leaf);
            }
        }

        internal static void AddNullSelectedIon(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema)
        {
            foreach (var leaf in schema.GetDataFields())
            {
                if (!leaf.Path.ToString().StartsWith("selected_ion/")) continue;
                AddNullLeaf(cols, leaf);
            }
        }

        // A single all-null row for a leaf in a present-but-null top-level struct: definition level
        // sits one below the leaf's own present level (the owning struct is null), no value slot.
        internal static void AddNullLeaf(IDictionary<DataField, (Array, int[], int[])> cols, DataField leaf)
        {
            var rows = new[] { MzPeakParquet.AtLevel(0, false) };
            var (def, rep) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (EmptyArray(leaf.ClrType), def, rep);
        }

        // A scalar leaf inside a top-level struct present only on the first M rows (the MSn count),
        // null on rows M..N-1. values has length M (one per MSn).
        internal static void AddMsnScalar(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            string path, int n, Array values)
        {
            var leaf = Leaf(schema, path);
            int m = values.Length;
            var rows = Enumerable.Range(0, n)
                .Select(i => i < m ? MzPeakParquet.Present(leaf) : MzPeakParquet.Absent()).ToArray();
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (values, def, null);
        }

        internal static void AddMsnString(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            string path, int n, string[] values)
        {
            var leaf = Leaf(schema, path);
            int m = values.Length;
            var rows = new List<MzPeakParquet.LeafRow>();
            var present = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if (i >= m) rows.Add(MzPeakParquet.Absent());
                else if (values[i] != null) { rows.Add(MzPeakParquet.Present(leaf)); present.Add(values[i]); }
                else rows.Add(MzPeakParquet.AtLevel(leaf.MaxDefinitionLevel - 1, false));
            }
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (present.ToArray(), def, null);
        }

        internal static void AddMsnPrecursorIndex(IDictionary<DataField, (Array, int[], int[])> cols,
            ParquetSchema schema, string path, int n, List<MzPeakRecord> msn)
        {
            var leaf = Leaf(schema, path);
            int m = msn.Count;
            var rows = new List<MzPeakParquet.LeafRow>();
            var present = new List<ulong>();
            for (int i = 0; i < n; i++)
            {
                if (i >= m) rows.Add(MzPeakParquet.Absent());
                else if (msn[i].PrecursorIndex.HasValue)
                { rows.Add(MzPeakParquet.Present(leaf)); present.Add(msn[i].PrecursorIndex.Value); }
                else rows.Add(MzPeakParquet.AtLevel(leaf.MaxDefinitionLevel - 1, false));
            }
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (present.ToArray(), def, null);
        }

        // A nullable scalar leaf on the MSn struct (present on the first M rows). Genuine null values
        // emit leaf-null one level below present; rows past M (i >= M) sit at def-level 0 (struct absent).
        internal static void AddMsnNullable<T>(IDictionary<DataField, (Array, int[], int[])> cols,
            ParquetSchema schema, string path, int n, T?[] values) where T : struct
        {
            var leaf = Leaf(schema, path);
            int m = values.Length;
            var rows = new List<MzPeakParquet.LeafRow>();
            var present = new List<T>();
            for (int i = 0; i < n; i++)
            {
                if (i >= m) rows.Add(MzPeakParquet.Absent());
                else if (values[i].HasValue) { rows.Add(MzPeakParquet.Present(leaf)); present.Add(values[i].Value); }
                else rows.Add(MzPeakParquet.AtLevel(leaf.MaxDefinitionLevel - 1, false));
            }
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (present.ToArray(), def, null);
        }

        // PARAM list inside a top-level struct present only on the first M rows. perRow has length M.
        internal static void AddMsnParamList(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            string listPath, int n, List<List<MzPeakParam>> perRow, Action<string> collectPrefix)
        {
            int m = perRow.Count;
            var structPresent = Enumerable.Range(0, n).Select(i => i < m).ToArray();
            var padded = new List<List<MzPeakParam>>();
            for (int i = 0; i < n; i++) padded.Add(i < m ? perRow[i] : new List<MzPeakParam>());
            AddParamList(cols, schema, listPath, padded, structPresent, collectPrefix);
        }

        internal static void AddParamList(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            string listPath, List<List<MzPeakParam>> perRow, bool[] structPresent, Action<string> collectPrefix)
        {
            var list = (ListField)FindField(schema, listPath);
            var accLeaf = Leaf(schema, listPath + "/list/item/accession");
            var nameLeaf = Leaf(schema, listPath + "/list/item/name");
            var unitLeaf = Leaf(schema, listPath + "/list/item/unit");
            var intLeaf = Leaf(schema, listPath + "/list/item/value/integer");
            var fltLeaf = Leaf(schema, listPath + "/list/item/value/float");
            var strLeaf = Leaf(schema, listPath + "/list/item/value/string");
            var boolLeaf = Leaf(schema, listPath + "/list/item/value/boolean");

            var accRows = new List<MzPeakParquet.LeafRow>();
            var nameRows = new List<MzPeakParquet.LeafRow>();
            var unitRows = new List<MzPeakParquet.LeafRow>();
            var intRows = new List<MzPeakParquet.LeafRow>();
            var fltRows = new List<MzPeakParquet.LeafRow>();
            var strRows = new List<MzPeakParquet.LeafRow>();
            var boolRows = new List<MzPeakParquet.LeafRow>();

            var accVals = new List<string>();
            var nameVals = new List<string>();
            var unitVals = new List<string>();
            var intVals = new List<long>();
            var fltVals = new List<double>();
            var strVals = new List<string>();
            var boolVals = new List<bool>();

            for (int row = 0; row < perRow.Count; row++)
            {
                if (!structPresent[row])
                {
                    accRows.Add(MzPeakParquet.NullList()); nameRows.Add(MzPeakParquet.NullList());
                    unitRows.Add(MzPeakParquet.NullList()); intRows.Add(MzPeakParquet.NullList());
                    fltRows.Add(MzPeakParquet.NullList()); strRows.Add(MzPeakParquet.NullList());
                    boolRows.Add(MzPeakParquet.NullList());
                    continue;
                }

                var items = perRow[row];
                if (items.Count == 0)
                {
                    accRows.Add(MzPeakParquet.EmptyList(list)); nameRows.Add(MzPeakParquet.EmptyList(list));
                    unitRows.Add(MzPeakParquet.EmptyList(list)); intRows.Add(MzPeakParquet.EmptyList(list));
                    fltRows.Add(MzPeakParquet.EmptyList(list)); strRows.Add(MzPeakParquet.EmptyList(list));
                    boolRows.Add(MzPeakParquet.EmptyList(list));
                    continue;
                }

                foreach (var it in items) { collectPrefix(it.Accession); collectPrefix(it.Unit); }
                AddListLeaf(accRows, accVals, accLeaf, items, p => p.Accession);
                AddListLeaf(nameRows, nameVals, nameLeaf, items, p => p.Name);
                AddListLeaf(unitRows, unitVals, unitLeaf, items, p => p.Unit);
                AddListLeafNumStruct(intRows, intVals, intLeaf, items, p => p.Integer);
                AddListLeafNumStruct(fltRows, fltVals, fltLeaf, items, p => p.Float);
                AddListLeaf(strRows, strVals, strLeaf, items, p => p.String);
                AddListLeafNumStruct(boolRows, boolVals, boolLeaf, items, p => p.Boolean);
            }

            Emit(cols, accLeaf, accVals.ToArray(), accRows);
            Emit(cols, nameLeaf, nameVals.ToArray(), nameRows);
            Emit(cols, unitLeaf, unitVals.ToArray(), unitRows);
            Emit(cols, intLeaf, intVals.ToArray(), intRows);
            Emit(cols, fltLeaf, fltVals.ToArray(), fltRows);
            Emit(cols, strLeaf, strVals.ToArray(), strRows);
            Emit(cols, boolLeaf, boolVals.ToArray(), boolRows);
        }

        internal static void AddListLeaf(List<MzPeakParquet.LeafRow> rows, List<string> vals, DataField leaf,
            List<MzPeakParam> items, Func<MzPeakParam, string> sel)
        {
            var levels = new int[items.Count];
            var has = new bool[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                var v = sel(items[i]);
                if (v != null) { vals.Add(v); levels[i] = leaf.MaxDefinitionLevel; has[i] = true; }
                else { levels[i] = leaf.MaxDefinitionLevel - 1; has[i] = false; }
            }
            rows.Add(MzPeakParquet.ListOf(levels, has));
        }

        internal static void AddListLeafNumStruct<T>(List<MzPeakParquet.LeafRow> rows, List<T> vals, DataField leaf,
            List<MzPeakParam> items, Func<MzPeakParam, T?> sel) where T : struct
        {
            var levels = new int[items.Count];
            var has = new bool[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                var v = sel(items[i]);
                if (v.HasValue) { vals.Add(v.Value); levels[i] = leaf.MaxDefinitionLevel; has[i] = true; }
                else { levels[i] = leaf.MaxDefinitionLevel - 1; has[i] = false; }
            }
            rows.Add(MzPeakParquet.ListOf(levels, has));
        }

        internal static void Emit(IDictionary<DataField, (Array, int[], int[])> cols, DataField leaf,
            Array values, List<MzPeakParquet.LeafRow> rows)
        {
            var (def, rep) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (values, def, rep);
        }

        internal static void AddScanWindows(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            List<MzPeakRecord> records, Func<string, string, string, string> cv)
        {
            var lowerLeaf = Leaf(schema, "scan/scan_windows/list/item/" + cv(MzPeakCv.ScanWindowLowerLimit, "scan_window_lower_limit", MzPeakCv.MzUnit));
            var upperLeaf = Leaf(schema, "scan/scan_windows/list/item/" + cv(MzPeakCv.ScanWindowUpperLimit, "scan_window_upper_limit", MzPeakCv.MzUnit));

            var lowerRows = new List<MzPeakParquet.LeafRow>();
            var upperRows = new List<MzPeakParquet.LeafRow>();
            var lowerVals = new List<float>();
            var upperVals = new List<float>();

            for (int row = 0; row < records.Count; row++)
            {
                lowerRows.Add(MzPeakParquet.ListOf(new[] { lowerLeaf.MaxDefinitionLevel }, new[] { true }));
                upperRows.Add(MzPeakParquet.ListOf(new[] { upperLeaf.MaxDefinitionLevel }, new[] { true }));
                lowerVals.Add(records[row].WindowLower);
                upperVals.Add(records[row].WindowUpper);
            }

            Emit(cols, lowerLeaf, lowerVals.ToArray(), lowerRows);
            Emit(cols, upperLeaf, upperVals.ToArray(), upperRows);

            // The per-window parameters list is present-but-empty: one window element per row, its
            // parameters list defined with no entries.
            const string winParams = "scan/scan_windows/list/item/parameters";
            var scanWindows = (ListField)FindField(schema, "scan/scan_windows");
            var windowItem = (StructField)scanWindows.Item;
            var winParamsList = (ListField)windowItem.Fields.First(f => f.Name == "parameters");
            foreach (var leaf in schema.GetDataFields())
            {
                if (!leaf.Path.ToString().StartsWith(winParams + "/")) continue;
                var rows = Enumerable.Range(0, records.Count)
                    .Select(_ => MzPeakParquet.EmptyList(winParamsList))
                    .ToList();
                Emit(cols, leaf, EmptyArray(leaf.ClrType), rows);
            }
        }
    }
}
