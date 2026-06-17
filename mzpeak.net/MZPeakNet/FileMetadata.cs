namespace MZPeak.Metadata;

using System.Text.Json;
using System.Text.Json.Serialization;
using MZPeak.ControlledVocabulary;
using ParquetSharp;

/// <summary>
/// Describes the contents and source files of an mzPeak file.
/// Analogous to the mzML fileDescription element.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record FileDescription
{
    /// <summary>Creates an empty file description.</summary>
    public FileDescription()
    {
        Contents = new();
        SourceFiles = new();
    }

    /// <summary>Creates an empty file description instance.</summary>
    public static FileDescription Empty()
    {
        return new FileDescription
        {
            Contents = new(),
            SourceFiles = new(),
        };
    }

    /// <summary>
    /// Parameters describing the contents of the file, such as types of spectra.
    /// Analogous to mzML fileContent.
    /// </summary>
    [JsonPropertyName("contents")]
    public List<Param> Contents { get; set; }

    /// <summary>
    /// List of all files used as data sources for this mzPeak file.
    /// Analogous to mzML sourceFileList.
    /// </summary>
    [JsonPropertyName("source_files")]
    public List<SourceFile> SourceFiles { get; set; }
}


/// <summary>
/// A data file that was read in order to produce this mzPeak file.
/// Analogous to the mzML sourceFile element.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record SourceFile
{
    /// <summary>Creates a source file entry.</summary>
    /// <param name="id">Unique identifier for this source file.</param>
    /// <param name="name">Name of the source file without path.</param>
    /// <param name="location">URI-encoded path to the source file.</param>
    /// <param name="parameters">Additional parameters describing the file.</param>
    public SourceFile(string id, string name, string location, List<Param> parameters)
    {
        Id = id;
        Name = name;
        Location = location;
        Parameters = parameters;
    }

    /// <summary>
    /// A unique identifier for this source file.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    /// <summary>
    /// The name of the source file, not including parent directory.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; }

    /// <summary>
    /// The path to the source file, URI encoded. May include file:// protocols and UNC paths.
    /// </summary>
    [JsonPropertyName("location")]
    public string Location { get; set; }

    /// <summary>
    /// Additional parameters describing this source file, like checksums, nativeID format, or file format.
    /// </summary>
    [JsonPropertyName("parameters")]
    public List<Param> Parameters { get; set; }
}

/// <summary>
/// The type of instrument component in the mass spectrometer.
/// </summary>
[JsonConverter(typeof(ComponentTypeJsonConverter))]
public enum ComponentType
{
    /// <summary>
    /// The ion source component.
    /// </summary>
    IonSouce,

    /// <summary>
    /// The mass analyzer component.
    /// </summary>
    Analyzer,

    /// <summary>
    /// The detector component.
    /// </summary>
    Detector,
}

class ComponentTypeJsonConverter : JsonConverter<ComponentType>
{
    public override ComponentType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (s == null) throw new JsonException("Null string");
        s = s.ToLower();
        return s switch
        {
            "ionsource" => ComponentType.IonSouce,
            "analyzer" => ComponentType.Analyzer,
            "detector" => ComponentType.Detector,
            _ => throw new NotImplementedException($"{s} not a recognized ComponentType")
        };

    }

    public override void Write(Utf8JsonWriter writer, ComponentType value, JsonSerializerOptions options)
    {
        var text = value switch
        {
            ComponentType.IonSouce => "ionsource",
            ComponentType.Analyzer=> "analyzer",
            ComponentType.Detector=> "detector",
            _ => throw new NotImplementedException()
        };
        writer.WriteStringValue(text);
    }
}


/// <summary>
/// Describes an instrument component like the ion source, mass analyzer, or detector.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record InstrumentComponent
{
    /// <summary>
    /// The kind of component this is.
    /// </summary>
    [JsonPropertyName("component_type")]
    public required ComponentType ComponentType { get; set; }

    /// <summary>
    /// The order in which the analytes travel through the component.
    /// </summary>
    [JsonPropertyName("order")]
    public required int Order { get; set; }

    /// <summary>
    /// Additional parameters describing this component, like the particular hardware type.
    /// </summary>
    [JsonPropertyName("parameters")]
    public required List<Param> Parameters { get; set; }
}


/// <summary>
/// Describes a single instrument configuration that was used.
/// Analogous to the mzML instrumentConfiguration element.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record InstrumentConfiguration
{
    /// <summary>
    /// A unique identifier for this instrument configuration.
    /// </summary>
    [JsonPropertyName("id")]
    public required uint Id { get; set; }

    /// <summary>
    /// The list of instrument components in this configuration.
    /// </summary>
    [JsonPropertyName("components")]
    public required List<InstrumentComponent> Components { get; set; }

    /// <summary>
    /// The identifier for a software that was associated with the data acquisition process.
    /// </summary>
    [JsonPropertyName("software_reference")]
    public string? SoftwareReference { get; set; }

    /// <summary>
    /// Additional parameters describing this configuration, like the instrument model and serial number.
    /// </summary>
    [JsonPropertyName("parameters")]
    public required List<Param> Parameters { get; set; }
}

/// <summary>
/// A piece of software. Analogous to the mzML software element.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record Software
{
    /// <summary>
    /// A unique identifier for this software, even amongst different versions of the same software.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    /// <summary>
    /// The version of the software.
    /// </summary>
    [JsonPropertyName("version")]
    public required string Version { get; set; }

    /// <summary>
    /// Additional parameters describing this software, such as its controlled vocabulary identifier.
    /// </summary>
    [JsonPropertyName("parameters")]
    public required List<Param> Parameters { get; set; }
}

/// <summary>
/// A description of a sample used to generate this dataset.
/// Analogous to the mzML sample element.
/// </summary>
public record Sample
{
    /// <summary>
    /// A unique identifier for this sample.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    /// <summary>
    /// A human-readable name for this sample that might be easier to recognize.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>
    /// Additional parameters describing this sample.
    /// </summary>
    [JsonPropertyName("parameters")]
    public required List<Param> Parameters { get; set; }
}


/// <summary>
/// Describes a single step of data processing.
/// </summary>
public record ProcessingMethod
{
    /// <summary>
    /// The order in which the step is applied in the data processing pipeline.
    /// </summary>
    [JsonPropertyName("order")]
    public required uint Order { get; set; }

    /// <summary>
    /// The identifier for a software entry that performed this operation.
    /// </summary>
    [JsonPropertyName("software_reference")]
    public required string SoftwareReference { get; set; }

    /// <summary>
    /// Additional parameters describing this data processing step denoting actions, parameters, and other descriptors.
    /// </summary>
    [JsonPropertyName("parameters")]
    public required List<Param> Parameters { get; set; }
}


/// <summary>
/// Describes a data processing workflow. Analogous to the mzML dataProcessing element.
/// </summary>
public record DataProcessingMethod
{
    /// <summary>
    /// A unique identifier for the data processing method.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    /// <summary>
    /// The list of processing steps in this workflow.
    /// </summary>
    [JsonPropertyName("methods")]
    public required List<ProcessingMethod> ProcessingMethods { get; set; }
}


public record ScanSettings
{
    /// <summary>
    /// A unique identifier for the scan settings
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("source_file_refs")]
    public required List<string> SourceFileRefs { get; set; }

    [JsonPropertyName("targets")]
    public required List<List<Param>> Targets { get; set; }

    [JsonPropertyName("parameters")]
    public required List<Param> Parameters { get; set; }
}


/// <summary>
/// Run-level metadata section. Analogous to the mzML run element.
/// </summary>
public record MSRun
{
    /// <summary>
    /// A unique identifier for the run.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    /// <summary>
    /// The default data processing identifier.
    /// </summary>
    [JsonPropertyName("default_data_processing_id")]
    public string DefaultDataProcessingId { get; set; }

    /// <summary>
    /// The default instrument configuration identifier.
    /// </summary>
    [JsonPropertyName("default_instrument_id")]
    public int DefaultInstrumentId { get; set; }

    /// <summary>
    /// The default source file the content references.
    /// </summary>
    [JsonPropertyName("default_source_file_id")]
    public string DefaultSourceFileId { get; set; }

    /// <summary>
    /// The time that data acquisition started, encoded in RFC 3339 format.
    /// </summary>
    [JsonPropertyName("start_time")]
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// Parameters describing the run not otherwise covered by the attributes.
    /// </summary>
    [JsonPropertyName("parameters")]
    public List<Param> Parameters { get; set; }

    /// <summary>Creates an MS run with the specified parameters.</summary>
    /// <param name="id">Unique identifier for the run.</param>
    /// <param name="defaultDataProcessingId">Default data processing identifier.</param>
    /// <param name="defaultInstrumentId">Default instrument configuration identifier.</param>
    /// <param name="defaultSourceFileId">Default source file identifier.</param>
    /// <param name="startTime">Optional acquisition start time.</param>
    /// <param name="parameters">Optional additional parameters.</param>
    public MSRun(string id, string defaultDataProcessingId, int defaultInstrumentId, string defaultSourceFileId, DateTime? startTime = null, List<Param>? parameters = null)
    {
        Id = id;
        DefaultDataProcessingId = defaultDataProcessingId;
        DefaultInstrumentId = defaultInstrumentId;
        DefaultSourceFileId = defaultSourceFileId;
        StartTime = startTime;
        Parameters = parameters ?? new();
    }

    /// <summary>Creates an empty MS run.</summary>
    public MSRun()
    {
        Id = "";
        DefaultDataProcessingId = "";
        DefaultInstrumentId = 0;
        DefaultSourceFileId = "";
        StartTime = null;
        Parameters = new();
    }
}


/// <summary>
/// The complete metadata container for an mzPeak file, combining all metadata sections.
/// </summary>
public class MzPeakMetadata
{
    /// <summary>
    /// Describes the contents and source files of the mzPeak file.
    /// </summary>
    public FileDescription FileDescription { get; set; }

    /// <summary>
    /// List of instrument configurations used in this experiment.
    /// </summary>
    public List<InstrumentConfiguration> InstrumentConfigurations { get; set; }

    /// <summary>
    /// List of software used to acquire or process the data.
    /// </summary>
    public List<Software> Softwares { get; set; }

    /// <summary>
    /// List of samples used in this experiment.
    /// </summary>
    public List<Sample> Samples { get; set; }

    /// <summary>
    /// List of data processing workflows applied to the data.
    /// </summary>
    public List<DataProcessingMethod> DataProcessingMethods { get; set; }

    public List<ScanSettings> ScanSettings { get; set; }

    /// <summary>
    /// Run-level metadata for the experiment.
    /// </summary>
    public MSRun Run { get; set; }

    /// <summary>Creates an empty metadata container.</summary>
    public MzPeakMetadata()
    {
        FileDescription = new FileDescription
        {
            Contents = new(),
            SourceFiles = new()
        };
        InstrumentConfigurations = new();
        Softwares = new();
        Samples = new();
        ScanSettings = new();
        DataProcessingMethods = new();
        Run = new();
    }

    /// <summary>Creates metadata with the specified sections.</summary>
    /// <param name="description">The file description.</param>
    /// <param name="instrumentConfigurations">List of instrument configurations.</param>
    /// <param name="softwares">List of software entries.</param>
    /// <param name="samples">List of samples.</param>
    /// <param name="dataProcessingMethods">List of data processing methods.</param>
    /// <param name="run">The run-level metadata.</param>
    public MzPeakMetadata(FileDescription description,
                          List<InstrumentConfiguration> instrumentConfigurations,
                          List<Software> softwares,
                          List<Sample> samples,
                          List<DataProcessingMethod> dataProcessingMethods,
                          List<ScanSettings> scanSettings,
                          MSRun run)
    {
        FileDescription = description;
        InstrumentConfigurations = instrumentConfigurations;
        Softwares = softwares;
        Samples = samples;
        DataProcessingMethods = dataProcessingMethods;
        ScanSettings = scanSettings;
        Run = run;
    }

    /// <summary>Reads metadata from a Parquet file's key-value metadata.</summary>
    /// <param name="reader">The Parquet file reader.</param>
    public static MzPeakMetadata FromParquet(ParquetFileReader reader)
    {
        var meta = reader.FileMetaData.KeyValueMetadata;
        string? buf = "";
        FileDescription? fileDescription = new FileDescription();
        if (meta.TryGetValue("file_description", out buf))
        {
            fileDescription = JsonSerializer.Deserialize<FileDescription>(buf);
            if (fileDescription == null) throw new InvalidDataException("file_description failed to deserialize");
        }

        List<InstrumentConfiguration>? instrumentConfigurations = new();
        if (meta.TryGetValue("instrument_configuration_list", out buf))
        {
            instrumentConfigurations = JsonSerializer.Deserialize<List<InstrumentConfiguration>>(buf);
            if (instrumentConfigurations == null) throw new InvalidDataException("instrument_configuration_list failed to deserialize");
        }

        List<Software>? softwares = new();
        if (meta.TryGetValue("software_list", out buf))
        {
            softwares = JsonSerializer.Deserialize<List<Software>>(buf);
            if (softwares == null) throw new InvalidDataException("software_list failed to deserialize");
        }

        List<Sample>? samples = new();
        if (meta.TryGetValue("sample_list", out buf))
        {
            samples = JsonSerializer.Deserialize<List<Sample>>(buf) ?? new();
            if (samples == null) throw new InvalidDataException("sample_list failed to deserialize");
        }

        List<ScanSettings>? scanSettings = new();
        if (meta.TryGetValue("scan_settings_list", out buf))
        {
            scanSettings = JsonSerializer.Deserialize<List<ScanSettings>>(buf) ?? new();
            if (scanSettings == null) throw new InvalidDataException("scan_settings_list failed to deserialize");
        }

        List<DataProcessingMethod> dataProcessingMethods = new();
        if (meta.TryGetValue("data_processing_method_list", out buf))
        {
            dataProcessingMethods = JsonSerializer.Deserialize<List<DataProcessingMethod>>(buf) ?? new();
            if (dataProcessingMethods == null) throw new InvalidDataException("data_processing_method_list failed to deserialize");
        }

        MSRun run = new();
        if (meta.TryGetValue("run", out buf))
        {
            run = JsonSerializer.Deserialize<MSRun>(buf) ?? new();
            if (run == null) throw new InvalidDataException("run failed to deserialize");
        }

        return new MzPeakMetadata(
            fileDescription,
            instrumentConfigurations,
            softwares,
            samples,
            dataProcessingMethods,
            scanSettings,
            run
        );
    }
}