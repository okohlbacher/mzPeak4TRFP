using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace ThermoRawFileParser.Writer
{
    public struct MzPeakParam
    {
        public long? Integer;
        public double? Float;
        public string String;
        public bool? Boolean;
        public string Accession;
        public string Name;
        public string Unit;
    }

    public static class MzPeakParquet
    {
        public static StructField BuildParamField(string name)
        {
            var value = new StructField("value",
                new DataField<long>("integer", true),
                new DataField<double>("float", true),
                new DataField<string>("string", true),
                new DataField<bool>("boolean", true));

            return new StructField(name,
                value,
                new DataField<string>("accession", true),
                new DataField<string>("name", true),
                new DataField<string>("unit", true));
        }

        // The label must already be normalized to snake_case by the caller; this method
        // performs no label normalization and only rewrites the ':' in CURIE accessions to '_'.
        public static string CvColumn(string accession, string label, string unitAccession = null)
        {
            var head = accession.Replace(':', '_') + "_" + label;
            if (unitAccession == null) return head;
            return head + "_unit_" + unitAccession.Replace(':', '_');
        }

        public static DataColumn Column(DataField field, Array defined, int[] defLevels, int[] repLevels)
        {
            if (defLevels == null) return new DataColumn(field, defined);
            return new DataColumn(field, defined, defLevels, repLevels);
        }

        // A leaf's presence model for one row. Cells is the per-element definition-level list:
        //   - a non-repeated (scalar) leaf has exactly one cell (its def-level for this row);
        //   - a repeated (list) leaf has one cell per list element (each its def-level), OR a single
        //     cell for a null/empty list (the list itself stops below the element-present level).
        // Definition levels are expressed RELATIVE to the leaf's own MaxDefinitionLevel via the
        // builder helpers (Present/LeafNull/EmptyList/NullList), so callers never hard-code 1 and
        // the same description is correct regardless of how deep the leaf actually sits.
        public readonly struct LeafRow
        {
            public readonly bool Repeated;
            public readonly int[] Cells;          // resolved def-levels, one per emitted entry
            public readonly bool[] Values;         // per entry: does the leaf VALUE array carry a slot
            public LeafRow(bool repeated, int[] cells, bool[] values)
            {
                Repeated = repeated; Cells = cells; Values = values;
            }
        }

        // GENERAL per-leaf definition/repetition-level computer for arbitrary nested shapes built
        // from StructField / ListField / DataField. Uses Parquet.Net's own schema walk
        // (Field.MaxDefinitionLevel / MaxRepetitionLevel) as the level ceiling, so it is correct for
        // top-level struct, nested struct-in-struct, list-of-struct, null vs empty list, and a null
        // leaf inside a present element alike — never a flat 1. Rep-level is 0 for the first entry of
        // a row and the leaf's MaxRepetitionLevel for subsequent elements of the same list.
        public static (int[] defLevels, int[] repLevels) NestedLevels(DataField leaf, IReadOnlyList<LeafRow> rows)
        {
            int maxRep = leaf.MaxRepetitionLevel;
            var def = new List<int>();
            var rep = maxRep == 0 ? null : new List<int>();

            foreach (var row in rows)
            {
                if (row.Cells.Length == 0)
                {
                    def.Add(0);
                    rep?.Add(0);
                    continue;
                }

                for (int i = 0; i < row.Cells.Length; i++)
                {
                    def.Add(row.Cells[i]);
                    rep?.Add(i == 0 ? 0 : maxRep);
                }
            }

            return (def.ToArray(), rep?.ToArray());
        }

        // Builder helpers expressed against the leaf's own max levels. Definition-level semantics
        // (measured against Parquet.Net 5.0.1): a present non-null leaf reaches MaxDefinitionLevel;
        // a leaf that is null inside a present element reaches MaxDefinitionLevel-1; a present-but-
        // empty list reaches the list-present level (MaxDefinitionLevel minus the element+value
        // levels); a null list reaches 0.
        public static int PresentLevel(DataField leaf) => leaf.MaxDefinitionLevel;
        public static int LeafNullLevel(DataField leaf) => leaf.MaxDefinitionLevel - 1;

        // A non-repeated leaf that is present on this row (top struct / nested struct present).
        public static LeafRow Present(DataField leaf) =>
            new LeafRow(false, new[] { leaf.MaxDefinitionLevel }, new[] { true });

        // A non-repeated leaf whose owning struct is null on this row (def-level 0 padded tail).
        public static LeafRow Absent() => new LeafRow(false, new[] { 0 }, new[] { false });

        // A non-repeated leaf at an explicit def-level (e.g. a nested struct present but the leaf
        // value itself null -> MaxDefinitionLevel-1, with no value slot).
        public static LeafRow AtLevel(int level, bool hasValue) =>
            new LeafRow(false, new[] { level }, new[] { hasValue });

        // A list leaf with N elements, each at an explicit def-level; hasValue marks which elements
        // contribute a value slot (a null element value sits one level below the present level).
        public static LeafRow ListOf(int[] levels, bool[] hasValue) =>
            new LeafRow(true, levels, hasValue);

        // A present-but-empty list (no elements). Level is the list-present level for this leaf.
        public static LeafRow EmptyList(int level) => new LeafRow(true, new[] { level }, new[] { false });

        // The definition level reached by a present-but-empty list: one below the enclosing
        // ListField's MaxDefinitionLevel (the list value is defined but holds no repeated entry;
        // the deepest level of the ListField counts the presence of at least one element).
        public static LeafRow EmptyList(ListField list) =>
            new LeafRow(true, new[] { list.MaxDefinitionLevel - 1 }, new[] { false });

        // A null list (the owning list field is null). Def-level 0.
        public static LeafRow NullList() => new LeafRow(true, System.Array.Empty<int>(), System.Array.Empty<bool>());

        public static async Task WriteAsync(Stream output, ParquetSchema schema,
            IReadOnlyDictionary<string, string> customMetadata,
            IDictionary<DataField, (Array defined, int[] defLevels, int[] repLevels)> columns)
        {
            using (var writer = await ParquetWriter.CreateAsync(schema, output).ConfigureAwait(false))
            {
                writer.CompressionMethod = CompressionMethod.Zstd;
                if (customMetadata != null) writer.CustomMetadata = customMetadata;

                using (var rg = writer.CreateRowGroup())
                {
                    foreach (var field in schema.GetDataFields())
                    {
                        var triple = columns[field];
                        await rg.WriteColumnAsync(Column(field, triple.defined, triple.defLevels, triple.repLevels))
                            .ConfigureAwait(false);
                    }
                }
            }
        }
    }
}
