using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoRawFileParser.Writer.MzML;

namespace ThermoRawFileParser.Writer
{
    public partial class MzPeakSpectrumWriter
    {
        // File-level metadata blocks shared verbatim between the parquet footer KV and the index
        // metadata{}. Built once after all CV-named columns/params are emitted so the generated
        // cv_list covers exactly the collected CV-prefix set.
        private JObject _metadataBlocks;

        private void AddMetadataBlocks(IDictionary<string, string> custom, List<MzPeakRecord> records)
        {
            var instruments = BuildInstrumentConfigurations();
            var software = BuildSoftwareList();
            var dataProcessing = BuildDataProcessingList();
            var fileDescription = BuildFileDescription(records);
            var run = BuildRun();

            // cv_list is generated LAST: every accession/unit routed through Cv() or a param helper
            // has been recorded in the CvCollector by this point.
            var cvList = BuildCvList();

            _metadataBlocks = new JObject
            {
                ["version"] = MzPeakLayout.MzPeakVersion,
                ["cv_list"] = cvList,
                ["file_description"] = fileDescription,
                ["instrument_configuration_list"] = instruments,
                ["software_list"] = software,
                ["data_processing_method_list"] = dataProcessing,
                ["run"] = run,
                ["sample_list"] = new JArray(),
                ["scan_settings_list"] = new JArray()
            };

            custom["cv_list"] = Compact(cvList);
            custom["file_description"] = Compact(fileDescription);
            custom["instrument_configuration_list"] = Compact(instruments);
            custom["software_list"] = Compact(software);
            custom["data_processing_method_list"] = Compact(dataProcessing);
            custom["run"] = Compact(run);
            custom["sample_list"] = "[]";
            custom["scan_settings_list"] = "[]";
        }

        private static string Compact(JToken token) =>
            token.ToString(Newtonsoft.Json.Formatting.None);

        private JArray BuildCvList()
        {
            var defs = new Dictionary<string, (string version, string fullName, string uri)>
            {
                ["MS"] = (MzPeakLayout.MsCvVersion, "PSI-MS controlled vocabulary",
                    "https://raw.githubusercontent.com/HUPO-PSI/psi-ms-CV/master/psi-ms.obo"),
                ["UO"] = (MzPeakLayout.UoCvVersion, "Unit Ontology", "http://purl.obolibrary.org/obo/uo.obo")
            };

            var list = new JArray();
            foreach (var prefix in _cv.OrderedPrefixes())
            {
                defs.TryGetValue(prefix, out var d);
                list.Add(new JObject
                {
                    ["id"] = prefix,
                    ["version"] = d.version ?? "unknown",
                    ["full_name"] = d.fullName ?? prefix,
                    ["uri"] = d.uri ?? ""
                });
            }
            return list;
        }

        private JArray BuildInstrumentConfigurations()
        {
            OntologyMapping.UpdateFTMSDefinition(_instrumentModel);
            var model = OntologyMapping.GetInstrumentModel(_instrumentModel);
            var detectors = OntologyMapping.GetDetectors(model.accession);

            var list = new JArray();
            for (int i = 0; i < _analyzerOrder.Count; i++)
            {
                var ionization = OntologyMapping.IonizationTypes.TryGetValue(_ionizationOrder[i], out var ion)
                    ? ion
                    : OntologyMapping.IonizationTypes[IonizationModeType.Any];
                var analyzer = OntologyMapping.MassAnalyzerTypes[_analyzerOrder[i]];
                var detector = i < detectors.Count ? detectors[i] : OntologyMapping.GetDetectors("default")[0];

                var configParams = new JArray
                {
                    CvParam(model.accession, model.name, model.value),
                    CvParam(MzPeakCv.InstrumentSerialNumber, "instrument serial number", _instrumentSerial)
                };
                var components = new JArray
                {
                    Component("ionsource", 1, ionization),
                    Component("analyzer", 2, analyzer),
                    Component("detector", 3, detector)
                };
                list.Add(new JObject
                {
                    ["id"] = i,
                    ["software_reference"] = "ThermoRawFileParser",
                    ["parameters"] = configParams,
                    ["components"] = components
                });
            }
            return list;
        }

        private JObject Component(string type, int order, CVParamType cv)
        {
            return new JObject
            {
                ["component_type"] = type,
                ["order"] = order,
                ["parameters"] = new JArray { CvParam(cv.accession, cv.name, cv.value) }
            };
        }

        private JArray BuildSoftwareList()
        {
            return new JArray
            {
                new JObject
                {
                    ["id"] = "ThermoRawFileParser",
                    ["version"] = MainClass.Version,
                    ["parameters"] = new JArray { CvParam(MzPeakCv.ThermoRawFileParser, "ThermoRawFileParser", null) }
                }
            };
        }

        private JArray BuildDataProcessingList()
        {
            var methods = new JArray
            {
                new JObject
                {
                    ["order"] = 0,
                    ["software_reference"] = "ThermoRawFileParser",
                    ["parameters"] = new JArray { CvParam(MzPeakCv.FileFormatConversion, "file format conversion", null) }
                },
                new JObject
                {
                    ["order"] = 1,
                    ["software_reference"] = "ThermoRawFileParser",
                    ["parameters"] = new JArray { JParam("intensity narrowing", null, "f64 to f32", null) }
                }
            };

            if (!ParseInput.MzPeakPointLayout && ParseInput.MzPeakNumpress)
            {
                methods.Add(new JObject
                {
                    ["order"] = 2,
                    ["software_reference"] = "ThermoRawFileParser",
                    ["parameters"] = new JArray
                    {
                        CvParam(MzPeakCv.NumpressLinear, "MS-Numpress linear prediction compression", null),
                        JParam("m/z encoding", null, "lossy Numpress-linear (bounded ~5e-7 Th); intensity lossless f32", null)
                    }
                });
            }

            return new JArray
            {
                new JObject
                {
                    ["id"] = "trfp_conversion",
                    ["methods"] = methods
                }
            };
        }

        private JObject BuildFileDescription(List<MzPeakRecord> records)
        {
            var contents = new JArray
            {
                CvParam(MzPeakCv.Ms1Spectrum, "MS1 spectrum", null),
                CvParam(MzPeakCv.MsnSpectrum, "MSn spectrum", null)
            };
            var sourceParams = new JArray
            {
                CvParam(MzPeakCv.ThermoNativeIdFormat, "Thermo nativeID format", null),
                CvParam(MzPeakCv.ThermoRawFormat, "Thermo RAW format", null)
            };
            return new JObject
            {
                ["contents"] = contents,
                ["source_files"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = "RAW1",
                        ["name"] = _sourceName,
                        ["location"] = _sourceLocation,
                        ["parameters"] = sourceParams
                    }
                }
            };
        }

        private JObject BuildRun()
        {
            return new JObject
            {
                ["id"] = Path.GetFileNameWithoutExtension(_sourceName),
                ["default_instrument_id"] = 0,
                ["default_data_processing_id"] = "trfp_conversion",
                ["default_source_file_id"] = "RAW1",
                ["start_time"] = _creationDate.ToUniversalTime()
                    .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            };
        }

        // A controlled-value PARAM (accession + name, no value). Records the CV prefix so the
        // generated cv_list stays exhaustive.
        private JObject CvParam(string accession, string name, string value)
        {
            CollectPrefix(accession);
            return JParam(name, accession, string.IsNullOrEmpty(value) ? null : value, null);
        }

        // A PARAM object matching the param.json schema (name required; accession/value/unit nullable).
        private JObject JParam(string name, string accession, object value, string unit)
        {
            CollectPrefix(unit);
            return new JObject
            {
                ["name"] = name,
                ["accession"] = accession == null ? JValue.CreateNull() : new JValue(accession),
                ["value"] = value == null ? JValue.CreateNull() : JToken.FromObject(value),
                ["unit"] = unit == null ? JValue.CreateNull() : new JValue(unit)
            };
        }

        private byte[] BuildIndex(bool hasPeaks, bool hasChromatograms, bool hasVendor = false)
        {
            var files = new JArray
            {
                new JObject
                {
                    ["name"] = "spectra_data.parquet",
                    ["entity_type"] = "spectrum",
                    ["data_kind"] = "data arrays"
                },
                new JObject
                {
                    ["name"] = "spectra_metadata.parquet",
                    ["entity_type"] = "spectrum",
                    ["data_kind"] = "metadata"
                }
            };

            if (hasPeaks)
            {
                files.Add(new JObject
                {
                    ["name"] = "spectra_peaks.parquet",
                    ["entity_type"] = "spectrum",
                    ["data_kind"] = "peaks"
                });
            }

            if (hasChromatograms)
            {
                files.Add(new JObject
                {
                    ["name"] = "chromatograms_metadata.parquet",
                    ["entity_type"] = "chromatogram",
                    ["data_kind"] = "metadata"
                });
                files.Add(new JObject
                {
                    ["name"] = "chromatograms_data.parquet",
                    ["entity_type"] = "chromatogram",
                    ["data_kind"] = "data arrays"
                });
            }

            if (hasVendor)
            {
                // Additive, verbatim vendor metadata under the spec-sanctioned open-enum entity_type
                // "proprietary" (the mzPeak index reserves it for vendor data without a PSI-MS CV term;
                // conformance tooling ignores non-spectrum/chromatogram entity_types). Retrieval is by the
                // descriptive file name. Tall trailer bag + file-level metadata + status-log timeseries +
                // error log + the trailer schema sidecar.
                void Vendor(string name, string kind) =>
                    files.Add(new JObject { ["name"] = name, ["entity_type"] = "proprietary", ["data_kind"] = kind });
                Vendor("vendor_scan_trailers.parquet", "scan trailers");
                Vendor("vendor_file_metadata.parquet", "file metadata");
                Vendor("vendor_trailer_schema.parquet", "trailer schema");
                Vendor("vendor_status_log.parquet", "status log");
                Vendor("vendor_error_log.parquet", "error log");
            }

            var metadata = _metadataBlocks != null
                ? (JObject)_metadataBlocks.DeepClone()
                : new JObject { ["version"] = MzPeakLayout.MzPeakVersion };

            var index = new JObject
            {
                ["version"] = MzPeakLayout.MzPeakVersion,
                ["files"] = files,
                ["metadata"] = metadata
            };

            return new UTF8Encoding(false).GetBytes(index.ToString());
        }
    }
}
