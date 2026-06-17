using System.CommandLine;
using Microsoft.Extensions.Logging;


using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.RandomAccessReaderPlugin;
using ThermoFisher.CommonCore.RawFileReader;

using MZPeak.Storage;
using MZPeak.Thermo;
using MZPeak.Compute;
using ParquetSharp;
using ThermoFisher.CommonCore.Data;
using MZPeak.ControlledVocabulary;
using MZPeak.Writer.Data;
using Apache.Arrow.Types;


namespace MZPeakCliConverter;

internal class Program
{
    static ILogger? Logger = null;

    static void Main(string[] args)
    {
        var startTime = DateTime.Now;
        RootCommand rootCommand = new("Demo application for mzPeak .NET")
        {
            CreateReadCommand(),
            CreateReadSpectrum(),
            CreateTranscodeCommand(),
            CreateThermoTranslateCommand(),
        };

        var verbosityOpt = new Option<bool>("--verbose")
        {
            Description = "Verbose logging",
        };

        rootCommand.Add(verbosityOpt);

        var opts = rootCommand.Parse(args);
        var isVerbose = opts.GetValue(verbosityOpt);

        ConfigureLogging(isVerbose);
        opts.Invoke();

        var elapsed = DateTime.Now - startTime;
        Logger?.LogInformation($"{elapsed:c} elapsed");
    }

    static void ConfigureLogging(bool verbose=false)
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            if (verbose)
            {
                builder
                    .AddFilter("MZPeakNet", LogLevel.Debug)
                    .AddFilter("MZPeak", LogLevel.Debug);
            }

            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss";
            });
        });
        MZPeak.Util.LoggingConfig.ConfigureLogging(loggerFactory);
        Logger = loggerFactory.CreateLogger("MZPeakNet.App");
        CLITask.Logger = Logger;

    }

    static Command CreateReadCommand()
    {
        var cmd = new Command("read", "Read an existing mzPeak file");
        Argument<FileInfo> filePath = new Argument<FileInfo>("file").AcceptExistingOnly();
        cmd.Arguments.Add(filePath);
        Option<string> decryptionKey = new Option<string>("--decryption-key", ["-d"])
        {
            Description = "Provide a shared decryption key for all Parquet files in the archive"
        };
        cmd.Options.Add(decryptionKey);
        cmd.SetAction(parseResult =>
        {
            var fp = parseResult.GetValue(filePath);
            var decryptionKeyVal = parseResult.GetValue(decryptionKey);
            if (fp == null)
            {
                parseResult.RootCommandResult.AddError("File argument was missing");
            }
            else
            {
                ReadFile(fp, decryptionKeyVal).Wait();
            }
        });
        return cmd;
    }

    static Command CreateTranscodeCommand()
    {
        var cmd = new Command("transcode", "Read an existing mzPeak file and write another mzPeak file");
        Argument<FileInfo> filePath = new Argument<FileInfo>("file").AcceptExistingOnly();
        cmd.Arguments.Add(filePath);

        Argument<FileInfo> outPath = new Argument<FileInfo>("out").AcceptLegalFilePathsOnly();
        cmd.Arguments.Add(outPath);

        cmd.SetAction(parseResult =>
        {
            var fp = parseResult.GetRequiredValue(filePath);
            var outp = parseResult.GetRequiredValue(outPath);
            TranscodeFile(fp, outp).Wait();
        });

        return cmd;
    }

    static Command CreateThermoTranslateCommand()
    {
        var cmd = new Command("thermo", "Read a Thermo RAW file and write mzPeak file");
        Argument<FileInfo> filePath = new Argument<FileInfo>("file").AcceptExistingOnly();
        cmd.Arguments.Add(filePath);

        Option<bool> nullMarking = new Option<bool>("--use-null-marking", "-u")
        {
            Description = "Use null marking to annotate low information content points that can be back-filled at read time to skip storing them."
        };
        cmd.Options.Add(nullMarking);
        Option<bool> useChunked = new Option<bool>("--use-chunking", "-c")
        {
            Description = "Use the chunked layout for the main signal data files which can compress profile data effectively."
        };
        cmd.Options.Add(useChunked);

        Option<long> pageSize = new Option<long>("--data-page-size")
        {
            Description = "The data page size in bytes",
            DefaultValueFactory = (arg) => { return 1048576; }
        };
        cmd.Options.Add(pageSize);

        Option<long> rowGroupSize = new Option<long>("--row-group-size")
        {
            Description = "The row group size in rows",
            DefaultValueFactory = (arg) => { return 1048576; }
        };
        cmd.Options.Add(rowGroupSize);

        Option<int> zstdLevelOpt = new("--zstd-level", "-z")
        {
            Description = "The compression level to be applied to each data page"
        };
        cmd.Options.Add(zstdLevelOpt);

        Argument<FileInfo> outPath = new Argument<FileInfo>("out").AcceptLegalFilePathsOnly();
        cmd.Arguments.Add(outPath);

        cmd.SetAction(parseResult =>
        {
            var fp = parseResult.GetRequiredValue(filePath);
            var outp = parseResult.GetRequiredValue(outPath);
            var nullMark = parseResult.GetValue(nullMarking);
            var chunked = parseResult.GetValue(useChunked);
            var pageSizeVal = parseResult.GetValue(pageSize);
            var rowGroupSizeVal = parseResult.GetValue(rowGroupSize);
            var zstdLevel = parseResult.GetValue(zstdLevelOpt);
            ThermoTranslate(fp, outp, nullMark, chunked, pageSizeVal, rowGroupSizeVal, zstdLevel);
        });

        return cmd;
    }

    static Command CreateReadSpectrum()
    {
        var cmd = new Command("spectrum", "Read a spectrum from an mzPeak file");
        Argument<FileInfo> filePath = new Argument<FileInfo>("file").AcceptExistingOnly();
        cmd.Arguments.Add(filePath);
        Argument<ulong> indexArg = new Argument<ulong>("index");
        cmd.Arguments.Add(indexArg);
        cmd.SetAction(parseResult =>
        {
            var fp = parseResult.GetValue(filePath);
            var idx = parseResult.GetValue(indexArg);
            if (fp == null)
            {
                parseResult.RootCommandResult.AddError("File argument was missing");
            }
            else
            {

                ReadSpectrum(fp, idx).Wait();
            }
        });
        return cmd;
    }

    static async Task ReadSpectrum(FileInfo sourceFile, ulong spectrumIndex)
    {
        var job = new ReadSpectrumTask(sourceFile, spectrumIndex);
        await job.Main();
    }

    static void ThermoTranslate(FileInfo sourceFile, FileInfo destinationFile, bool useNullMarking = false, bool useChunked = false, long pageSize = 1048576, long rowGroupSize = 1048576, int zstdLevel = 3)
    {
        var job = new ThermoTranslateTask(sourceFile, destinationFile, useNullMarking, useChunked, pageSize, rowGroupSize, zstdLevel);
        job.Main();
    }

    static async Task TranscodeFile(FileInfo sourceFile, FileInfo destinationFile)
    {
        var job = new TranscodeFileTask(sourceFile, destinationFile);
        await job.Main();
    }

    static async Task ReadFile(FileInfo fileInfo, string? decryptionKey = null)
    {
        var job = new ReadFileTask(fileInfo, decryptionKey);
        await job.Main();
    }
}

public class CLITask
{
    public static ILogger? Logger = null;
}

public class ThermoTranslateTask : CLITask
{
    FileInfo SourceFile;
    FileInfo DestinationFile;
    bool UseNullMarking = false;
    bool UseChunked = false;
    long PageSize = 1048576;
    long RowGroupSize = 4194304;
    int ZstdLevel = 3;

    public ThermoTranslateTask(FileInfo sourceFile, FileInfo destinationFile, bool useNullMarking = false, bool useChunked = false, long pageSize = 1048576, long rowGroupSize = 4194304, int zstdLevel=3)
    {
        SourceFile = sourceFile;
        DestinationFile = destinationFile;
        UseNullMarking = useNullMarking;
        UseChunked = useChunked;
        PageSize = pageSize;
        RowGroupSize = rowGroupSize;
        ZstdLevel = zstdLevel;
    }

    public void TranslateSpectraTo(ThermoFisher.CommonCore.Data.Interfaces.IRawDataExtended accessor, ThermoMZPeakWriter writer)
    {
        writer.InitializeHelper(accessor);

        writer.Samples.Add(writer.ConversionHelper.GetSample(accessor));
        writer.FileDescription = writer.ConversionHelper.GetFileDescription(accessor);

        writer.Run.DefaultSourceFileId = "RAW1";
        writer.Run.StartTime = accessor.FileHeader.CreationDate;
        writer.Run.Id = accessor.FileName;

        writer.StartSpectrumPeakData(useTmp: true);

        var startScan = accessor.RunHeader.FirstSpectrum;
        var lastScan = accessor.RunHeader.LastSpectrum;
        EntryDerivedMetadata entryMeta;
        EntryDerivedMetadata peakMeta;
        for (var scanNumber = startScan; scanNumber <= lastScan; scanNumber++)
        {
            var scanFilter = accessor.GetFilterForScanNumber(scanNumber);
            var segments = accessor.GetSegmentedScanFromScanNumber(scanNumber);
            var statistics = accessor.GetScanStatsForScanNumber(scanNumber);
            var time = accessor.RetentionTimeFromScanNumber(scanNumber);

            if (scanNumber % 1000 == 0)
            {
                Logger?.LogInformation(
                    $"Writing {scanNumber} with {segments.PositionCount} points ({(float)scanNumber / (float)lastScan * 100.0:0.00}%)"
                );
            }

            entryMeta = EntryDerivedMetadata.Empty;
            peakMeta = EntryDerivedMetadata.Empty;
            if (!statistics.IsCentroidScan)
            {
                entryMeta = writer.AddSpectrumData(
                    writer.CurrentSpectrum,
                    segments,
                    statistics);
            }

            var peaks = accessor.GetCentroidStream(scanNumber, true);
            if (peaks != null && peaks.Length > 0)
            {
                peakMeta = writer.AddSpectrumPeakData(
                        writer.CurrentSpectrum,
                        peaks
                    );
                entryMeta.AuxiliaryArrays.AddRange(peakMeta.AuxiliaryArrays);
                entryMeta = entryMeta with { PeakCount = peakMeta.PeakCount };
            }
            else
            {
                var simpleScan = segments.ToSimpleScan();
                peakMeta = writer.AddSpectrumPeakData(
                        writer.CurrentSpectrum,
                        simpleScan
                    );
                entryMeta.AuxiliaryArrays.AddRange(peakMeta.AuxiliaryArrays);
                entryMeta = entryMeta with { PeakCount = peakMeta.PeakCount };
            }

            var key = writer.AddSpectrum(
                scanNumber,
                time,
                scanFilter,
                statistics,
                entryMeta
            );


            var packets = accessor.GetAdvancedPacketData(scanNumber);
            if (packets.NoiseData != null && packets.NoiseData.Length > 0)
            {
                writer.AddNoisePacketData(writer.CurrentSpectrum, packets.NoiseData);
            }

            var (precursorProps, acquisitionProperties) = writer.ExtractPrecursorAndTrailerMetadata(
                scanNumber,
                accessor,
                scanFilter,
                statistics
            );

            writer.AddScan(
                key,
                scanNumber,
                time,
                scanFilter,
                statistics,
                acquisitionProperties
            );

            if (precursorProps != null)
            {
                writer.AddPrecursor(
                    key,
                    precursorProps
                );
                writer.AddSelectedIon(
                    key,
                    precursorProps
                );
            }
        }

        var (traceInfo, chromArrays) = writer.ConversionHelper.ReadSummaryTrace(TraceType.TIC, accessor);
        entryMeta = writer.AddChromatogramData(writer.CurrentChromatogram, chromArrays);
        writer.AddChromatogram(
            traceInfo.Id,
            null,
            traceInfo.Parameters,
            new  EntryDerivedMetadata(null, traceInfo.AuxiliaryArrays, entryMeta.DataPointCount)
        );

        (traceInfo, chromArrays) = writer.ConversionHelper.ReadSummaryTrace(TraceType.BasePeak, accessor);
        entryMeta = writer.AddChromatogramData(writer.CurrentChromatogram, chromArrays);
        writer.AddChromatogram(
            traceInfo.Id,
            null,
            traceInfo.Parameters,
            new EntryDerivedMetadata(null, traceInfo.AuxiliaryArrays, entryMeta.DataPointCount)
        );
    }

    public void TranslateTracesTo(ThermoFisher.CommonCore.Data.Interfaces.IRawDataExtended accessor, ThermoMZPeakWriter writer)
    {
        Logger?.LogInformation("Writing traces");

        foreach (var log in writer.ConversionHelper.StatusLogs(accessor))
        {
            (var traceInfo, var traceArrays) = log.AsChromatogramInfo();
            var entryMeta = writer.AddChromatogramData(writer.CurrentChromatogram, traceArrays);
            traceInfo.DataPointCount = entryMeta.DataPointCount;
            writer.AddChromatogram(
                traceInfo.Id,
                null,
                traceInfo.Parameters,
                entryMeta
            );
        }
    }

    public void TranslatePDATo(ThermoFisher.CommonCore.Data.Interfaces.IRawDataExtended accessor, ThermoMZPeakWriter writer)
    {
        if (accessor.GetInstrumentCountOfType(Device.Pda) > 0)
        {
            Logger?.LogInformation("Reading PDA spectra");
            accessor.SelectInstrument(Device.Pda, 1);

            for (var i = accessor.RunHeader.FirstSpectrum; i <= accessor.RunHeader.LastSpectrum; i++)
            {
                var scan = accessor.GetSimplifiedScan(i);
                Console.WriteLine($"{scan.Masses.Length}");
            }
        }
        if (accessor.GetInstrumentCountOfType(Device.Pda) > 0)
        {
            Logger?.LogInformation($"Found photodiode array device");
            accessor.SelectInstrument(Device.Pda, 1);
            for (var i = accessor.RunHeader.FirstSpectrum; i < accessor.RunHeader.LastSpectrum; i++)
            {
                try
                {
                    var scan = accessor.GetSimplifiedScan(i);
                    Console.WriteLine($"{scan.Masses.Length}");
                }
                catch (NoSelectedMsDeviceException e)
                {
                    Logger?.LogDebug($"Failed to read UV spectrum {i}, quitting: {e}");
                    break;
                }
            }
        }
    }

    public (ThermoFisher.CommonCore.Data.Interfaces.IRawDataExtended accessor, ThermoFisher.CommonCore.Data.Interfaces.IRawFileThreadManager readerManager)? OpenThermoHandle()
    {
        var readerManager = RawFileReaderAdapter.RandomAccessThreadedFileFactory(SourceFile.FullName, RandomAccessFileManager.Instance);
        var accessor = readerManager.CreateThreadAccessor();
        if (!accessor.SelectMsData())
        {
            Logger?.LogWarning("No MS data detected! Exiting Early!");
            return null;
        }
        accessor.IncludeReferenceAndExceptionData = true;
        return (accessor, readerManager);
    }

    public ThermoMZPeakWriter OpenWriterFrom(IMZPeakArchiveWriter writerStorage)
    {
        var writer = new ThermoMZPeakWriter(
                writerStorage,
                spectrumPeakArrayIndex: ThermoMZPeakWriter.PeakArrayIndex(true, true),
                useChunked: UseChunked,
                includeNoise: true
            );
        writer.DataWriterConfig = writer.DataWriterConfig with {
            PageSize = PageSize,
            RowGroupSize = RowGroupSize,
            CompressionLevel = ZstdLevel,
        };

        if (UseNullMarking)
        {
            Logger?.LogInformation("Using null marking");
            writer.SpectraUseNullMarking();
        }
        return writer;
    }

    public void Main()
    {
        var handles = OpenThermoHandle();
        if (handles == null) return;
        var (accessor, _readerManager) = handles.Value;

        using (var fileStream = File.Create(DestinationFile.FullName))
        {
            var writerStorage = new ZipStreamArchiveWriter<FileStream>(fileStream);
            var writer = OpenWriterFrom(writerStorage);

            TranslateSpectraTo(accessor, writer);
            TranslateTracesTo(accessor, writer);
            TranslatePDATo(accessor, writer);

            Logger?.LogInformation("Closing writer...");
            writer.Close();
        }

        DestinationFile.Refresh();
        Logger?.LogInformation($"Wrote {DestinationFile.Length / 1000000.0} MB");
    }
}

public class ReadFileTask : CLITask
{
    FileInfo FileInfo;
    string? DecryptionKey;

    public ReadFileTask(FileInfo fileInfo, string? decryptionKey = null)
    {
        FileInfo = fileInfo;
        DecryptionKey = decryptionKey;
    }

    public async Task Main()
    {
        Dictionary<string, FileDecryptionProperties> decryptionConfigs = new();
        if (DecryptionKey != null)
        {
            Logger?.LogInformation("Setting basic decryption configuration");
            var baseDecrypt = new FileDecryptionPropertiesBuilder();
            baseDecrypt.FooterKey(
                System.Text.UTF8Encoding.UTF8.GetBytes(DecryptionKey));
            var config = baseDecrypt.Build();

            decryptionConfigs = FileIndex.UniformDecryption(config);
        }
        Logger?.LogInformation($"Reading {FileInfo}");
        var reader = new MZPeak.Reader.MzPeakReader(FileInfo.FullName, decryptionConfigs: decryptionConfigs);
        Logger?.LogInformation($"{reader.SpectrumCount} spectra detected, {reader.ChromatogramCount} chromatograms detected");
        Logger?.LogInformation($"Spectrum storage format = {reader.SpectrumDataFormat}");
        if (reader.HasWavelengthData)
        {
            Logger?.LogInformation($"Wavelength spectrum count {reader.WavelengthSpectrumCount} in format {reader.WavelengthSpectrumDataFormat}");
        }

        var isProfile = 0;
        var isCentroid = 0;
        var i = 0;
        await foreach (var (descr, spec) in reader.EnumerateSpectraAsync())
        {
            i++;
            if (i % 1000 == 0) Logger?.LogInformation($"{i} spectra read...");
            isProfile += descr.IsProfile ? 1 : 0;
            isCentroid += descr.IsCentroid ? 1 : 0;
            // var names = ((StructType)spec.Data.DataType).Fields.Select(f => f.Name);
            // Logger?.LogDebug($"{i}\t{string.Join(',', names)}\t{string.Join(',', names.Select(name => descr.ArrayIndex?.ArrayTypeFromName(name)?.ArrayName))}\t{spec.Length}");
        }
        Logger?.LogInformation($"{isProfile} profile spectra, {isCentroid} centroid spectra");
    }
}

public class TranscodeFileTask : CLITask
{
    FileInfo SourceFile;
    FileInfo DestinationFile;

    public TranscodeFileTask(FileInfo sourceFile, FileInfo destinationFile)
    {
        SourceFile = sourceFile;
        DestinationFile = destinationFile;
    }

    public async Task Main()
    {
        var reader = new MZPeak.Reader.MzPeakReader(SourceFile.FullName);
        var spectrumArrays = reader.SpectrumDataReaderMeta?.ArrayIndex;
        if (spectrumArrays == null)
        {
            Logger?.LogError("Cannot transcode a file without spectra yet");
            return;
        }
        using (var fileStream = File.Create(DestinationFile.FullName))
        {
            var writerStorage = new ZipStreamArchiveWriter<FileStream>(fileStream);

            var writer = new MZPeak.Writer.MZPeakWriter(
                writerStorage,
                spectrumArrays
            )
            {
                FileDescription = reader.FileDescription,
                InstrumentConfigurations = reader.InstrumentConfigurations,
                DataProcessingMethods = reader.DataProcessingMethods,
                Samples = reader.Samples,
                Run = reader.Run,
                Softwares = reader.Softwares
            };

            await foreach (var (descr, data) in reader.EnumerateSpectraAsync())
            {
                Logger?.LogInformation($"Writing {descr.Index} = {descr.Id} with {data.Length} points");
                var index = writer.CurrentSpectrum;
                var entryMeta = writer.AddSpectrumData(index, data.Fields.Skip(1), descr.IsProfile);
                descr.DataPointCount = entryMeta.DataPointCount;
                descr.PeakCount = entryMeta.PeakCount;

                writer.AddSpectrum(
                    descr.Id,
                    descr.Time,
                    descr.DataProcessingRef,
                    descr.Parameters,
                    entryMeta
                );
                foreach (var scan in descr.Scans)
                {
                    writer.AddScan(
                        index,
                        scan.InstrumentConfigurationRef,
                        scan.Parameters,
                        scan.IonMobility,
                        scan.IonMobilityTypeCURIE,
                        scanWindows: scan.ScanWindows?.Select(w => w.AsParamList()).ToList()
                    );
                }
                foreach (var precursor in descr.Precursors)
                {
                    writer.AddPrecursor(
                        index,
                        precursor.PrecursorIndex,
                        precursor.PrecursorId,
                        precursor.IsolationWindowParameters,
                        precursor.ActivationParameters
                    );
                }
                foreach (var selectedIon in descr.SelectedIons)
                {
                    writer.AddSelectedIon(
                        index,
                        selectedIon.PrecursorIndex,
                        selectedIon.Parameters,
                        selectedIon.IonMobility,
                        selectedIon.IonMobilityTypeCURIE
                    );
                }
            }
            writer.FlushSpectrumData();
            await foreach (var (descr, data) in reader.EnumerateChromatogramsAsync())
            {
                Logger?.LogInformation($"Writing {descr.Index} = {descr.Id} with {data.Length} points");
                var index = writer.CurrentChromatogram;
                var entryMeta = writer.AddChromatogramData(index, data.Fields.Skip(1));
                descr.DataPointCount = entryMeta.DataPointCount;
                writer.AddChromatogram(
                    descr.Id,
                    descr.DataProcessingRef,
                    descr.Parameters,
                    entryMeta
                );

            }
            writer.Close();
        }
        DestinationFile.Refresh();
        Logger?.LogInformation($"Wrote {DestinationFile.Length / 1000000.0} MB");
    }
}

public class ReadSpectrumTask : CLITask
{
    FileInfo SourceFile;
    ulong SpectrumIndex;

    public ReadSpectrumTask(FileInfo sourceFile, ulong spectrumIndex)
    {
        SourceFile = sourceFile;
        SpectrumIndex = spectrumIndex;
    }

    public async Task Main()
    {
        var reader = new MZPeak.Reader.MzPeakReader(SourceFile.FullName);
        var spec = await reader.GetSpectrumData(SpectrumIndex);
        if (spec != null)
        {
            Compute.PrettyPrint(spec);
            Console.WriteLine($"{spec.Length} points");
        }
    }
}