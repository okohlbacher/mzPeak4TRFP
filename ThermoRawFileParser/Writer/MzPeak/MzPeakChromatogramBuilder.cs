using System;
using System.Collections.Generic;
using Parquet.Data;
using Parquet.Schema;

namespace ThermoRawFileParser.Writer
{
    public partial class MzPeakSpectrumWriter
    {
        private byte[] BuildChromatogramMetadataFacet(int n)
        {
            var chromatogram = BuildChromatogramField();
            var precursor = BuildPrecursorField();
            var selectedIon = BuildSelectedIonField();
            var schema = new ParquetSchema(chromatogram, precursor, selectedIon);

            var cols = new Dictionary<DataField, (Array, int[], int[])>();
            var present = new[] { true };

            MzPeakColumns.AddScalar(cols, schema, "chromatogram/index", new ulong[] { 0UL }, present);
            MzPeakColumns.AddScalar(cols, schema, "chromatogram/id", new[] { "TIC" }, present);
            MzPeakColumns.AddScalar(cols, schema, "chromatogram/" + Cv(MzPeakCv.ScanPolarity, "scan_polarity"),
                new sbyte[] { 0 }, present);
            CollectPrefix(MzPeakCv.MzArrayData);
            MzPeakColumns.AddScalar(cols, schema, "chromatogram/" + Cv(MzPeakCv.ChromatogramType, "chromatogram_type"),
                new[] { MzPeakCv.MzArrayData }, present);

            var dprLeaf = MzPeakColumns.Leaf(schema, "chromatogram/data_processing_ref");
            var dprRows = new[] { MzPeakParquet.AtLevel(dprLeaf.MaxDefinitionLevel - 1, false) };
            var (dprDef, _) = MzPeakParquet.NestedLevels(dprLeaf, dprRows);
            cols[dprLeaf] = (new string[0], dprDef, null);

            MzPeakColumns.AddScalar(cols, schema, "chromatogram/" + Cv(MzPeakCv.NumberOfDataPoints, "number_of_data_points"),
                new ulong[] { (ulong)n }, present);

            MzPeakColumns.AddEmptyList(cols, schema, "chromatogram/parameters");
            MzPeakColumns.AddEmptyList(cols, schema, "chromatogram/auxiliary_arrays");
            MzPeakColumns.AddScalar(cols, schema, "chromatogram/number_of_auxiliary_arrays", new uint[] { 0u }, present);

            MzPeakColumns.AddNullPrecursor(cols, schema);
            MzPeakColumns.AddNullSelectedIon(cols, schema);

            var custom = new Dictionary<string, string>
            {
                ["chromatogram_count"] = "1",
                ["chromatogram_data_point_count"] = "0"
            };

            return MzPeakColumns.WriteFacet(schema, custom, cols);
        }

        private StructField BuildChromatogramField()
        {
            return new StructField("chromatogram",
                new DataField<ulong>("index", true),
                new DataField<string>("id", true),
                new DataField<sbyte>(Cv(MzPeakCv.ScanPolarity, "scan_polarity"), true),
                new DataField<string>(Cv(MzPeakCv.ChromatogramType, "chromatogram_type"), true),
                new DataField<string>("data_processing_ref", true),
                new DataField<ulong>(Cv(MzPeakCv.NumberOfDataPoints, "number_of_data_points"), true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")),
                new ListField("auxiliary_arrays", BuildAuxArrayField("item")),
                new DataField<uint>("number_of_auxiliary_arrays", true));
        }
    }
}
