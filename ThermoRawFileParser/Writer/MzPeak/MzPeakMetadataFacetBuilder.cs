using System;
using System.Collections.Generic;
using System.Linq;
using Parquet.Data;
using Parquet.Schema;

namespace ThermoRawFileParser.Writer
{
    public partial class MzPeakSpectrumWriter
    {
        private byte[] BuildMetadataFacet(List<MzPeakRecord> records)
        {
            int n = records.Count;

            var spectrum = BuildSpectrumField();
            var scan = BuildScanField();
            var precursor = BuildPrecursorField();
            var selectedIon = BuildSelectedIonField();
            var schema = new ParquetSchema(spectrum, scan, precursor, selectedIon);

            var cols = new Dictionary<DataField, (Array, int[], int[])>();
            var presentAll = records.Select(_ => true).ToArray();

            // precursor / selected_ion are independent tables: the k-th MSn (ascending ordinal) sits
            // at row k, null-padded on rows M..N-1.
            var msnRecords = records.Where(r => r.IsMsn).OrderBy(r => r.Ordinal).ToList();

            // spectrum facet (present on all N rows)
            MzPeakColumns.AddScalar(cols, schema, "spectrum/index", records.Select(r => r.Ordinal).ToArray(), presentAll);
            MzPeakColumns.AddScalar(cols, schema, "spectrum/id", records.Select(r => r.Id).ToArray(), presentAll);
            MzPeakColumns.AddScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.MsLevel, "ms_level"),
                records.Select(r => (byte)r.MsLevel).ToArray(), presentAll);
            MzPeakColumns.AddScalar(cols, schema, "spectrum/time", records.Select(r => r.Time).ToArray(), presentAll);
            MzPeakColumns.AddNullableScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.ScanPolarity, "scan_polarity"),
                records.Select(r => r.Polarity).ToList());
            MzPeakColumns.AddScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.SpectrumRepresentation, "spectrum_representation"),
                records.Select(r => r.Representation).ToArray(), presentAll);
            MzPeakColumns.AddScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.SpectrumType, "spectrum_type"),
                records.Select(r => r.SpectrumType).ToArray(), presentAll);
            MzPeakColumns.AddNullableScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.LowestObservedMz, "lowest_observed_mz", MzPeakCv.MzUnit),
                records.Select(r => r.LowestMz).ToList());
            MzPeakColumns.AddNullableScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.HighestObservedMz, "highest_observed_mz", MzPeakCv.MzUnit),
                records.Select(r => r.HighestMz).ToList());
            MzPeakColumns.AddScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.NumberOfDataPoints, "number_of_data_points"),
                records.Select(r => r.DataPointCount).ToArray(), presentAll);

            // number_of_peaks: present only where peaks were written (leaf-null otherwise).
            var npkLeaf = MzPeakColumns.Leaf(schema, "spectrum/" + Cv(MzPeakCv.NumberOfPeaks, "number_of_peaks"));
            var npkRows = records.Select(r => r.PeakCount.HasValue
                ? MzPeakParquet.Present(npkLeaf)
                : MzPeakParquet.AtLevel(npkLeaf.MaxDefinitionLevel - 1, false)).ToArray();
            var (npkDef, _) = MzPeakParquet.NestedLevels(npkLeaf, npkRows);
            cols[npkLeaf] = (records.Where(r => r.PeakCount.HasValue).Select(r => r.PeakCount.Value).ToArray(), npkDef, null);

            MzPeakColumns.AddNullableScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.BasePeakMz, "base_peak_mz", MzPeakCv.MzUnit),
                records.Select(r => r.BasePeakMz).ToList());
            MzPeakColumns.AddNullableScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.BasePeakIntensity, "base_peak_intensity", MzPeakCv.CountsUnit),
                records.Select(r => r.BasePeakIntensity).ToList());
            MzPeakColumns.AddScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.TotalIonCurrent, "total_ion_current", MzPeakCv.CountsUnit),
                records.Select(r => r.TotalIonCurrent).ToArray(), presentAll);

            MzPeakColumns.AddNullLeafScalar(cols, schema, "spectrum/data_processing_ref", n);
            MzPeakColumns.AddParamList(cols, schema, "spectrum/parameters", records.Select(r => r.SpectrumParams).ToList(), presentAll, CollectPrefix);
            MzPeakColumns.AddEmptyListEveryRow(cols, schema, "spectrum/auxiliary_arrays", n);
            MzPeakColumns.AddScalar(cols, schema, "spectrum/number_of_auxiliary_arrays",
                records.Select(_ => 0u).ToArray(), presentAll);
            MzPeakColumns.AddEmptyListEveryRow(cols, schema, "spectrum/mz_delta_model", n);

            // scan facet (present on all N rows)
            MzPeakColumns.AddScalar(cols, schema, "scan/source_index", records.Select(r => r.Ordinal).ToArray(), presentAll);
            MzPeakColumns.AddScalar(cols, schema, "scan/scan_index", records.Select(r => r.Ordinal).ToArray(), presentAll);
            MzPeakColumns.AddScalar(cols, schema, "scan/" + Cv(MzPeakCv.ScanStartTime, "scan_start_time", MzPeakCv.MinuteUnit),
                records.Select(r => r.ScanStartTime).ToArray(), presentAll);
            MzPeakColumns.AddNullableScalar(cols, schema, "scan/" + Cv(MzPeakCv.PresetScanConfiguration, "preset_scan_configuration"),
                records.Select(r => r.PresetScanConfiguration).ToList());
            MzPeakColumns.AddScalar(cols, schema, "scan/" + Cv(MzPeakCv.FilterString, "filter_string"),
                records.Select(r => r.FilterString).ToArray(), presentAll);
            MzPeakColumns.AddNullableScalar(cols, schema, "scan/" + Cv(MzPeakCv.IonInjectionTime, "ion_injection_time", MzPeakCv.MillisecondUnit),
                records.Select(r => r.IonInjectionTime).ToList());
            MzPeakColumns.AddNullableScalar(cols, schema, "scan/ion_mobility_value",
                records.Select(_ => (double?)null).ToList());
            MzPeakColumns.AddNullLeafScalar(cols, schema, "scan/ion_mobility_type", n);
            MzPeakColumns.AddScalar(cols, schema, "scan/instrument_configuration_ref",
                records.Select(r => r.InstrumentConfigRef).ToArray(), presentAll);
            MzPeakColumns.AddNullLeafScalar(cols, schema, "scan/spectrum_reference", n);
            MzPeakColumns.AddParamList(cols, schema, "scan/parameters", records.Select(_ => new List<MzPeakParam>()).ToList(), presentAll, CollectPrefix);
            MzPeakColumns.AddScanWindows(cols, schema, records, Cv);

            // precursor facet (present on rows 0..M-1, null on M..N-1)
            MzPeakColumns.AddMsnScalar(cols, schema, "precursor/source_index", n, msnRecords.Select(r => r.Ordinal).ToArray());
            MzPeakColumns.AddMsnPrecursorIndex(cols, schema, "precursor/precursor_index", n, msnRecords);
            MzPeakColumns.AddMsnString(cols, schema, "precursor/precursor_id", n, msnRecords.Select(r => r.PrecursorId).ToArray());
            MzPeakColumns.AddMsnNullable(cols, schema, "precursor/isolation_window/" + Cv(MzPeakCv.IsolationWindowTargetMz, "isolation_window_target_mz"),
                n, msnRecords.Select(r => r.IsolationTarget).ToArray());
            MzPeakColumns.AddMsnNullable(cols, schema, "precursor/isolation_window/" + Cv(MzPeakCv.IsolationWindowLowerOffset, "isolation_window_lower_offset", MzPeakCv.MzUnit),
                n, msnRecords.Select(r => r.IsolationLowerOffset).ToArray());
            MzPeakColumns.AddMsnNullable(cols, schema, "precursor/isolation_window/" + Cv(MzPeakCv.IsolationWindowUpperOffset, "isolation_window_upper_offset", MzPeakCv.MzUnit),
                n, msnRecords.Select(r => r.IsolationUpperOffset).ToArray());
            MzPeakColumns.AddMsnParamList(cols, schema, "precursor/isolation_window/parameters", n, msnRecords.Select(_ => new List<MzPeakParam>()).ToList(), CollectPrefix);
            MzPeakColumns.AddMsnParamList(cols, schema, "precursor/activation/parameters", n, msnRecords.Select(r => r.ActivationParams).ToList(), CollectPrefix);

            // selected_ion facet (present on rows 0..M-1, null on M..N-1)
            MzPeakColumns.AddMsnScalar(cols, schema, "selected_ion/source_index", n, msnRecords.Select(r => r.Ordinal).ToArray());
            MzPeakColumns.AddMsnPrecursorIndex(cols, schema, "selected_ion/precursor_index", n, msnRecords);
            MzPeakColumns.AddMsnNullable(cols, schema, "selected_ion/" + Cv(MzPeakCv.SelectedIonMz, "selected_ion_mz", MzPeakCv.MzUnit),
                n, msnRecords.Select(r => r.SelectedIonMz).ToArray());
            MzPeakColumns.AddMsnNullable(cols, schema, "selected_ion/" + Cv(MzPeakCv.ChargeState, "charge_state"),
                n, msnRecords.Select(r => r.ChargeState).ToArray());
            MzPeakColumns.AddMsnNullable(cols, schema, "selected_ion/" + Cv(MzPeakCv.SelectedIonIntensity, "intensity", MzPeakCv.CountsUnit),
                n, msnRecords.Select(r => r.SelectedIonIntensity).ToArray());

            // Ion-mobility columns exist to match the selected_ion struct shape; the Thermo RAW path
            // carries no per-selected-ion mobility value, so they stay leaf-null on every MSn row.
            MzPeakColumns.AddMsnNullable(cols, schema, "selected_ion/ion_mobility_value", n, msnRecords.Select(_ => (double?)null).ToArray());
            MzPeakColumns.AddMsnString(cols, schema, "selected_ion/ion_mobility_type", n, msnRecords.Select(_ => (string)null).ToArray());
            MzPeakColumns.AddMsnParamList(cols, schema, "selected_ion/parameters", n, msnRecords.Select(_ => new List<MzPeakParam>()).ToList(), CollectPrefix);

            var numpress = !ParseInput.MzPeakPointLayout && ParseInput.MzPeakNumpress;
            var arrayIndex = ParseInput.MzPeakPointLayout
                ? MzPeakLayout.PointDataArrayIndex
                : (numpress ? MzPeakLayout.NumpressSpectrumArrayIndex : MzPeakLayout.ChunkedSpectrumArrayIndex);
            var custom = new Dictionary<string, string>
            {
                ["spectrum_count"] = n.ToString(),
                ["spectrum_data_point_count"] = "0",
                ["spectrum_array_index"] = arrayIndex
            };
            AddMetadataBlocks(custom, records);

            return MzPeakColumns.WriteFacet(schema, custom, cols);
        }

        private StructField BuildSpectrumField()
        {
            return new StructField("spectrum",
                new DataField<ulong>("index", true),
                new DataField<string>("id", true),
                new DataField<byte>(Cv(MzPeakCv.MsLevel, "ms_level"), true),
                new DataField<double>("time", true),
                new DataField<sbyte>(Cv(MzPeakCv.ScanPolarity, "scan_polarity"), true),
                new DataField<string>(Cv(MzPeakCv.SpectrumRepresentation, "spectrum_representation"), true),
                new DataField<string>(Cv(MzPeakCv.SpectrumType, "spectrum_type"), true),
                new DataField<double>(Cv(MzPeakCv.LowestObservedMz, "lowest_observed_mz", MzPeakCv.MzUnit), true),
                new DataField<double>(Cv(MzPeakCv.HighestObservedMz, "highest_observed_mz", MzPeakCv.MzUnit), true),
                new DataField<ulong>(Cv(MzPeakCv.NumberOfDataPoints, "number_of_data_points"), true),
                new DataField<ulong>(Cv(MzPeakCv.NumberOfPeaks, "number_of_peaks"), true),
                new DataField<double>(Cv(MzPeakCv.BasePeakMz, "base_peak_mz", MzPeakCv.MzUnit), true),
                new DataField<float>(Cv(MzPeakCv.BasePeakIntensity, "base_peak_intensity", MzPeakCv.CountsUnit), true),
                new DataField<float>(Cv(MzPeakCv.TotalIonCurrent, "total_ion_current", MzPeakCv.CountsUnit), true),
                new DataField<string>("data_processing_ref", true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")),
                new ListField("auxiliary_arrays", BuildAuxArrayField("item")),
                new DataField<uint>("number_of_auxiliary_arrays", true),
                new ListField("mz_delta_model", new DataField<double>("item", true)));
        }

        private StructField BuildScanField()
        {
            var window = new StructField("item",
                new DataField<float>(Cv(MzPeakCv.ScanWindowLowerLimit, "scan_window_lower_limit", MzPeakCv.MzUnit), true),
                new DataField<float>(Cv(MzPeakCv.ScanWindowUpperLimit, "scan_window_upper_limit", MzPeakCv.MzUnit), true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")));
            return new StructField("scan",
                new DataField<ulong>("source_index", true),
                new DataField<ulong>("scan_index", true),
                new DataField<float>(Cv(MzPeakCv.ScanStartTime, "scan_start_time", MzPeakCv.MinuteUnit), true),
                new DataField<uint>(Cv(MzPeakCv.PresetScanConfiguration, "preset_scan_configuration"), true),
                new DataField<string>(Cv(MzPeakCv.FilterString, "filter_string"), true),
                new DataField<float>(Cv(MzPeakCv.IonInjectionTime, "ion_injection_time", MzPeakCv.MillisecondUnit), true),
                new DataField<double>("ion_mobility_value", true),
                new DataField<string>("ion_mobility_type", true),
                new DataField<uint>("instrument_configuration_ref", true),
                new DataField<string>("spectrum_reference", true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")),
                new ListField("scan_windows", window));
        }

        private StructField BuildPrecursorField()
        {
            var isolationWindow = new StructField("isolation_window",
                new DataField<float>(Cv(MzPeakCv.IsolationWindowTargetMz, "isolation_window_target_mz"), true),
                new DataField<float>(Cv(MzPeakCv.IsolationWindowLowerOffset, "isolation_window_lower_offset", MzPeakCv.MzUnit), true),
                new DataField<float>(Cv(MzPeakCv.IsolationWindowUpperOffset, "isolation_window_upper_offset", MzPeakCv.MzUnit), true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")));
            var activation = new StructField("activation",
                new ListField("parameters", MzPeakParquet.BuildParamField("item")));
            return new StructField("precursor",
                new DataField<ulong>("source_index", true),
                new DataField<ulong>("precursor_index", true),
                new DataField<string>("precursor_id", true),
                isolationWindow,
                activation);
        }

        private StructField BuildSelectedIonField()
        {
            return new StructField("selected_ion",
                new DataField<ulong>("source_index", true),
                new DataField<ulong>("precursor_index", true),
                new DataField<double>(Cv(MzPeakCv.SelectedIonMz, "selected_ion_mz", MzPeakCv.MzUnit), true),
                new DataField<int>(Cv(MzPeakCv.ChargeState, "charge_state"), true),
                new DataField<float>(Cv(MzPeakCv.SelectedIonIntensity, "intensity", MzPeakCv.CountsUnit), true),
                new DataField<double>("ion_mobility_value", true),
                new DataField<string>("ion_mobility_type", true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")));
        }

        private StructField BuildAuxArrayField(string name)
        {
            return new StructField(name,
                new ListField("data", new DataField<byte>("item")),
                MzPeakParquet.BuildParamField("name"),
                new DataField<string>("data_type", true),
                new DataField<string>("compression", true),
                new DataField<string>("unit", true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")),
                new DataField<string>("data_processing_ref", true));
        }
    }
}
