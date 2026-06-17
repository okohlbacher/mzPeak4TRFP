
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MZPeak.ControlledVocabulary;
using MZPeak.Metadata;
using MZPeak.Storage;
using MZPeak.Thermo;
using MZPeak.Writer.Data;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.RandomAccessReaderPlugin;
using ThermoFisher.CommonCore.RawFileReader;

namespace MzPeakTests;

public class ThermoTranslationTest
{
    string TestRAWPath;

    public ThermoTranslationTest()
    {
        string fileName = "small.RAW";
        string baseDirectory = AppContext.BaseDirectory; // Gets the directory where tests are running
        TestRAWPath = Path.Combine(baseDirectory, fileName);
    }

    [Fact]
    public void TranslateThermoInMemory()
    {
        var readerManager = RawFileReaderAdapter.RandomAccessThreadedFileFactory(TestRAWPath, RandomAccessFileManager.Instance);
        var accessor = readerManager.CreateThreadAccessor();
        accessor.SelectInstrument(Device.MS, 1);
        accessor.IncludeReferenceAndExceptionData = true;

        var stream = new MemoryStream();
        var writerStorage = new ZipStreamArchiveWriter<MemoryStream>(stream);

        var writer = new ThermoMZPeakWriter(writerStorage, spectrumPeakArrayIndex: ThermoMZPeakWriter.PeakArrayIndex(true, true));
        writer.InitializeHelper(accessor);

        var startScan = accessor.RunHeader.FirstSpectrum;
        var lastScan = accessor.RunHeader.LastSpectrum;

        for (var scanNumber = startScan; scanNumber < lastScan; scanNumber++)
        {
            var scanFilter = accessor.GetFilterForScanNumber(scanNumber);
            var segments = accessor.GetSegmentedScanFromScanNumber(scanNumber);
            var statistics = accessor.GetScanStatsForScanNumber(scanNumber);
            var time = accessor.RetentionTimeFromScanNumber(scanNumber);

            var entryMeta = EntryDerivedMetadata.Empty;
            if (!statistics.IsCentroidScan)
            {
                entryMeta = writer.AddSpectrumData(
                    writer.CurrentSpectrum,
                    segments,
                    statistics);
            }

            var peakMeta = writer.AddSpectrumPeakData(
                    writer.CurrentSpectrum,
                    accessor.GetCentroidStream(scanNumber, true)
                );
            entryMeta.AuxiliaryArrays.AddRange(peakMeta.AuxiliaryArrays);
            entryMeta = entryMeta with { PeakCount = peakMeta.PeakCount };

            var key = writer.AddSpectrum(
                scanNumber,
                time,
                scanFilter,
                statistics,
                entryMeta
            );

            var (precursorProps, acquisitionProperties) = writer.ExtractPrecursorAndTrailerMetadata(scanNumber, accessor, scanFilter, statistics);

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
    }

    [Fact]
    public async Task TranslatePoint()
    {
        var job = new MZPeakCliConverter.ThermoTranslateTask(new FileInfo(TestRAWPath), new FileInfo("NUL"), false, false);
        var handle = job.OpenThermoHandle();
        Assert.NotNull(handle);
        var accessor = handle.Value.accessor;

        var stream = new MemoryStream();
        var writerStorage = new ZipStreamArchiveWriter<MemoryStream>(stream);
        var writer = job.OpenWriterFrom(writerStorage);
        job.TranslateSpectraTo(accessor, writer);
        job.TranslateTracesTo(accessor, writer);
        writer.Close();
        stream.Flush();
        stream.Seek(0, SeekOrigin.Begin);

        var readerStorage = new ZipArchiveStream<MemoryStream>(stream);
        var reader = new MZPeak.Reader.MzPeakReader(readerStorage);
        Assert.Equal(48, reader.SpectrumCount);
        Assert.True(reader.HasSpectrumData);
        Assert.NotNull(reader.SpectrumDataReaderMeta);
        Assert.Equal(BufferFormat.Point, reader.SpectrumDataReaderMeta.Format);

        foreach (var entry in reader.SpectrumDataReaderMeta.ArrayIndex.EntriesFor(ArrayType.MZArray).Where(e => e.BufferFormat == BufferFormat.Point || e.BufferFormat == BufferFormat.ChunkValues))
        {
            Assert.Null(entry.Transform);
        }

        foreach (var entry in reader.SpectrumDataReaderMeta.ArrayIndex.EntriesFor(ArrayType.IntensityArray).Where(e => e.BufferFormat == BufferFormat.Point || e.BufferFormat == BufferFormat.ChunkSecondary))
        {
            Assert.Null(entry.Transform);
        }

        var spec = reader.GetSpectrumDescription(0);
        Assert.Equal("controllerType=0 controllerNumber=1 scan=1", spec.Id);
        spec = reader.GetSpectrumDescription(47);
        Assert.Equal("controllerType=0 controllerNumber=1 scan=48", spec.Id);

        await foreach(var (descr, arrays) in reader.EnumerateSpectraAsync())
        {
            var peaksMatch = descr.PeakCount == arrays.Length;
            var dpMatch = descr.DataPointCount == arrays.Length;
            Assert.True(peaksMatch || dpMatch);
            Assert.True(arrays.Length > 0);
        }

    }

    [Fact]
    public async Task TranslateChunked()
    {
        var job = new MZPeakCliConverter.ThermoTranslateTask(new FileInfo(TestRAWPath), new FileInfo("NUL"), true, true);
        var handle = job.OpenThermoHandle();
        Assert.NotNull(handle);
        var accessor = handle.Value.accessor;

        var stream = new MemoryStream();
        var writerStorage = new ZipStreamArchiveWriter<MemoryStream>(stream);
        var writer = job.OpenWriterFrom(writerStorage);
        job.TranslateSpectraTo(accessor, writer);
        job.TranslateTracesTo(accessor, writer);
        writer.Close();
        stream.Flush();
        stream.Seek(0, SeekOrigin.Begin);

        var readerStorage = new ZipArchiveStream<MemoryStream>(stream);
        var reader = new MZPeak.Reader.MzPeakReader(readerStorage);
        Assert.Equal(48, reader.SpectrumCount);
        Assert.True(reader.HasSpectrumData);
        Assert.NotNull(reader.SpectrumDataReaderMeta);
        Assert.Equal(BufferFormat.ChunkValues, reader.SpectrumDataReaderMeta.Format);

        foreach (var entry in reader.SpectrumDataReaderMeta.ArrayIndex.EntriesFor(ArrayType.MZArray).Where(e => e.BufferFormat == BufferFormat.Point || e.BufferFormat == BufferFormat.ChunkValues))
        {
            Assert.Equal(MZPeak.Compute.NullInterpolation.NullInterpolateCURIE, entry.Transform);
        }

        foreach (var entry in reader.SpectrumDataReaderMeta.ArrayIndex.EntriesFor(ArrayType.IntensityArray).Where(e => e.BufferFormat == BufferFormat.Point || e.BufferFormat == BufferFormat.ChunkSecondary))
        {
            Assert.Equal(MZPeak.Compute.NullInterpolation.NullZeroCURIE, entry.Transform);
        }

        var spec = reader.GetSpectrumDescription(0);
        Assert.Equal("controllerType=0 controllerNumber=1 scan=1", spec.Id);
        spec = reader.GetSpectrumDescription(47);
        Assert.Equal("controllerType=0 controllerNumber=1 scan=48", spec.Id);

        await foreach (var (descr, arrays) in reader.EnumerateSpectraAsync())
        {
            var peaksMatch = descr.PeakCount == arrays.Length;
            var dpMatch = descr.DataPointCount == arrays.Length;
            Assert.True(peaksMatch || dpMatch);
            Assert.True(arrays.Length > 0);
        }
    }
}