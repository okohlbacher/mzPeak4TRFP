
using Apache.Arrow;
using Apache.Arrow.Types;
using MZPeak.Metadata;
using MZPeak.Reader.Visitors;
using MZPeak.Storage;
using Microsoft.Extensions.Logging;
using ParquetSharp;

namespace MZPeak.Reader;


public enum SpectrumDataModalityPreference
{
    PreferPeaks,
    PreferProfiles,
}


/// <summary>
/// Combines metadata and data array readers for unified access.
/// </summary>
/// <typeparam name="T">The metadata type (e.g., SpectrumDescription).</typeparam>
public class DataFacet<T> : IAsyncEnumerable<(T, StructArray)> where T: HasArrayIndex
{
    MetadataReaderBase<T> MetadataReader;
    DataArraysReader DataReader;
    DataArraysReader? PeakReader;

    /// <summary>
    /// Whether to prefer loading profile data or centroid data when both are available for spectra
    /// </summary>
    public SpectrumDataModalityPreference ModalityPreference {get; set;} = SpectrumDataModalityPreference.PreferProfiles;


    /// <summary>Creates a data facet combining metadata and data readers.</summary>
    /// <param name="metadataReader">The metadata reader.</param>
    /// <param name="dataReader">The data arrays reader.</param>
    public DataFacet(MetadataReaderBase<T> metadataReader, DataArraysReader dataReader, DataArraysReader? peakReader=null)
    {
        MetadataReader = metadataReader;
        DataReader = dataReader;
        PeakReader = peakReader;
    }

    /// <summary>Gets the number of entries in the facet.</summary>
    public long Length => MetadataReader.Length;

    /// <summary>Gets the metadata and data arrays for a specific index.</summary>
    /// <param name="index">The entry index.</param>
    public async ValueTask<(T, StructArray)> Get(ulong index)
    {
        var meta = MetadataReader.Get(index);
        if (meta == null) throw new IndexOutOfRangeException();
        var dpCount = MetadataReader.NumberOfDataPointsFor(index);
        var peakCount = MetadataReader.NumberOfPeaks(index);
        if (dpCount != null)
        {
            var data = await DataReader.ReadForIndex(index);
            if (data == null) throw new IndexOutOfRangeException();
            return (meta, data);
        }
        else if (peakCount != null && PeakReader != null)
        {
            var data = await PeakReader.ReadForIndex(index);
            if (data == null) throw new IndexOutOfRangeException();
            return (meta, data);
        }
        else
        {
            var data = DataReader.EmptyArrays();
            return (meta, data);
        }

    }

    /// <summary>Asynchronously enumerates all entries with their metadata and data.</summary>
    public async IAsyncEnumerable<(T, StructArray)> EnumerateAsync()
    {
        var metaRecs = MetadataReader.BulkLoad();
        var n = (ulong)Length;
        var dataIter = DataReader.Enumerate();
        var peakIter = PeakReader?.Enumerate();

        await dataIter.Seek(0);
        if (peakIter != null)
            await peakIter.Seek(0);

        for(var i = 0ul; i < n; i++)
        {
            var meta = metaRecs[(int)i];
            var dpCount = MetadataReader.NumberOfDataPointsFor(i);
            var peakCount = MetadataReader.NumberOfPeaks(i);

            if (dpCount != null && dpCount > 0 && (ModalityPreference == SpectrumDataModalityPreference.PreferProfiles || ((peakCount ?? 0) == 0)))
            {
                if (await dataIter.Seek(i))
                {
                    var nextValue = await dataIter.Peek();
                    if (nextValue == null) throw new InvalidOperationException($"Data iterator seeked but did not find a value");
                    var (dataIdx, data) = nextValue.Value;
                    if (dataIdx != i) throw new InvalidOperationException($"Data iterator is out of sync: {dataIdx} != {i}");
                    meta.ArrayIndex = DataReader.ArrayIndex;
                    yield return (meta, data);
                }
                else
                {
                    throw new InvalidOperationException($"Data iterator is out of sync with records: {i} expected {dpCount}, found nothing");
                }
            }

            else if (peakCount != null && peakCount > 0 && peakIter != null && (ModalityPreference == SpectrumDataModalityPreference.PreferPeaks || (dpCount ?? 0) == 0))
            {
                if (await peakIter.Seek(i))
                {
                    var nextValue = await peakIter.Peek();
                    if (nextValue == null) throw new InvalidOperationException($"Peak iterator seeked but did not find a value");
                    var (dataIdx, data) = nextValue.Value;
                    if (dataIdx != i) throw new InvalidOperationException($"Peak iterator is out of sync: {dataIdx} != {i}");
                    meta.ArrayIndex = PeakReader?.ArrayIndex;
                    yield return (meta, data);
                }
                else
                {
                    throw new InvalidOperationException($"Peak iterator is out of sync with records: {i} expected {peakCount}, found nothing {await peakIter.PeekIndex()}");
                }
            }
            else
            {
                meta.ArrayIndex = DataReader.ArrayIndex;
                yield return (meta, DataReader.EmptyArrays());
            }
        }
    }

    public async IAsyncEnumerator<(T, StructArray)> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        await foreach (var x in EnumerateAsync())
        {
            yield return x;
        }
    }
}


/// <summary>
/// Reader for mzPeak archive files containing mass spectrometry data.
/// </summary>
public class MzPeakReader
{
    internal static ILogger? Logger = null;

    IMZPeakArchiveStorage storage;

    SpectrumMetadataReader? spectrumMetadata;
    ChromatogramMetadataReader? chromatogramMetadata;
    SpectrumMetadataReader? wavelengthSpectrumMetadata;

    DataArraysReaderMeta? spectrumArraysMeta = null;
    DataArraysReaderMeta? chromatogramArraysMeta = null;
    DataArraysReaderMeta? spectrumPeaksArraysMeta = null;
    DataArraysReaderMeta? wavelengthSpectrumArraysMeta = null;

    /// <summary>
    /// Whether to prefer loading profile data or centroid data when both are available for spectra
    /// </summary>
    public SpectrumDataModalityPreference SpectrumDataModalityPreference { get; set; } = SpectrumDataModalityPreference.PreferProfiles;

    /// <summary>Creates a reader for the mzPeak file at the specified path.</summary>
    /// <param name="path">The file path to the mzPeak archive.</param>
    public MzPeakReader(string path, Dictionary<string, FileDecryptionProperties>? decryptionConfigs=null) : this(new LocalZipArchive(path), decryptionConfigs)
    { }

    /// <summary>Creates a reader using the specified storage backend.</summary>
    /// <param name="storage">The archive storage implementation.</param>
    public MzPeakReader(IMZPeakArchiveStorage storage, Dictionary<string, FileDecryptionProperties>? decryptionConfigs = null)
    {
        if (decryptionConfigs != null)
        {
            foreach(var (k, v) in decryptionConfigs)
            {
                storage.DecryptionConfigurations[k] = v;
            }
        }
        this.storage = storage;
        var stream = storage.SpectrumMetadata();
        spectrumMetadata = stream == null ? null : new SpectrumMetadataReader(stream);
        stream = storage.ChromatogramMetadata();
        chromatogramMetadata = stream == null ? null : new ChromatogramMetadataReader(stream);
        stream = storage.WavelengthSpectrumMetadata();
        wavelengthSpectrumMetadata = stream == null ? null : new SpectrumMetadataReader(stream);
    }

    /// <summary>Gets the number of spectra (alias for SpectrumCount).</summary>
    public long Length => spectrumMetadata?.Length ?? 0;
    /// <summary>Gets the number of spectra in the file.</summary>
    public long SpectrumCount => spectrumMetadata?.Length ?? 0;
    /// <summary>Gets the number of chromatograms in the file.</summary>
    public long ChromatogramCount => chromatogramMetadata?.Length ?? 0;
    /// <summary>Gets the number of wavelength spectra in the file.</summary>
    public long WavelengthSpectrumCount => wavelengthSpectrumMetadata?.Length ?? 0;

    public bool HasSpectrumData => spectrumMetadata != null;
    public bool HasChromatogramData => chromatogramMetadata != null;
    public bool HasWavelengthData => wavelengthSpectrumMetadata != null;

    /// <summary>Gets the file description metadata.</summary>
    public FileDescription FileDescription => spectrumMetadata?.FileDescription ?? chromatogramMetadata?.FileDescription ?? FileDescription.Empty();
    /// <summary>Gets the list of instrument configurations.</summary>
    public List<InstrumentConfiguration> InstrumentConfigurations => spectrumMetadata?.InstrumentConfigurations ?? chromatogramMetadata?.InstrumentConfigurations ?? new();
    /// <summary>Gets the list of software used.</summary>
    public List<Software> Softwares => spectrumMetadata?.Softwares ?? chromatogramMetadata?.Softwares ?? new();
    /// <summary>Gets the list of samples.</summary>
    public List<Sample> Samples => spectrumMetadata?.Samples ?? chromatogramMetadata?.Samples ?? new();
    /// <summary>Gets the list of data processing methods.</summary>
    public List<DataProcessingMethod> DataProcessingMethods => spectrumMetadata?.DataProcessingMethods ?? chromatogramMetadata?.DataProcessingMethods ?? new();
    /// <summary>Gets the run-level metadata.</summary>
    public MSRun Run => spectrumMetadata?.Run ?? chromatogramMetadata?.Run ?? new();

    /// <summary>Gets the mass spectrum metadata as an Arrow ChunkedArray.</summary>
    public ChunkedArray? SpectrumTable => spectrumMetadata?.SpectrumMetadata;

    /// <summary>Gets the scan metadata for mass spectra as an Arrow ChunkedArray.</summary>
    public ChunkedArray? ScanTable => spectrumMetadata?.ScanMetadata;

    /// <summary>Gets the precursor metadata for mass spectra as an Arrow ChunkedArray.</summary>
    public ChunkedArray? PrecursorTable => spectrumMetadata?.PrecursorMetadata;

    /// <summary>Gets the selected ion metadata for mass spectra as an Arrow ChunkedArray.</summary>
    public ChunkedArray? SelectedIonTable => spectrumMetadata?.PrecursorMetadata;

    /// <summary>Gets the chromatogram metadata as an Arrow ChunkedArray.</summary>
    public ChunkedArray? ChromatogramTable => chromatogramMetadata?.ChromatogramMetadata;

    /// <summary>Gets the precursor metadata for chromatograms as an Arrow ChunkedArray.</summary>
    public ChunkedArray? ChromatogramPrecursorTable => chromatogramMetadata?.PrecursorMetadata;

    /// <summary>Gets the selected ion metadata for chromatograms as an Arrow ChunkedArray.</summary>
    public ChunkedArray? ChromatogramSelectedIonTable => chromatogramMetadata?.PrecursorMetadata;

    /// <summary>Gets the wavelength spectrum metadata as an Arrow ChunkedArray.</summary>
    public ChunkedArray? WavelengthSpectrumTable => wavelengthSpectrumMetadata?.SpectrumMetadata;

    /// <summary>Gets the scan metadata for wavelength spectra as an Arrow ChunkedArray.</summary>
    public ChunkedArray? WavelengthSpectrumScanTable => wavelengthSpectrumMetadata?.ScanMetadata;

    /// <summary>Gets the spectrum description for the specified index.</summary>
    /// <param name="index">The spectrum index.</param>
    public SpectrumDescription GetSpectrumDescription(ulong index)
    {
        if (spectrumMetadata == null) throw new InvalidOperationException("Spectrum metadata table is absent");
        return spectrumMetadata.Get(index);
    }

    /// <summary>Gets the chromatogram description for the specified index.</summary>
    /// <param name="index">The chromatogram index.</param>
    public ChromatogramDescription GetChromatogramDescription(ulong index)
    {
        if (chromatogramMetadata == null) throw new InvalidOperationException("Chromatogram metadata table is absent");
        return chromatogramMetadata.Get(index);
    }

    /// <summary>Gets the spectrum description for the specified index.</summary>
    /// <param name="index">The spectrum index.</param>
    public SpectrumDescription GetWavelengthSpectrumDescription(ulong index)
    {
        if (wavelengthSpectrumMetadata == null) throw new InvalidOperationException("Wavelength spectrum metadata table is absent");
        return wavelengthSpectrumMetadata.Get(index);
    }

    /// <summary>Gets the buffer format used for mass spectrum data arrays.</summary>
    public BufferFormat? SpectrumDataFormat
    {
        get
        {
            if (spectrumArraysMeta != null) return spectrumArraysMeta.Format;
            else
            {
                var reader = OpenSpectrumDataReader();
                if (reader == null) return null;
                return reader.Metadata.Format;
            }
        }
    }

    /// <summary>Gets the buffer format used for chromatogram data arrays.</summary>
    public BufferFormat? ChromatogramDataFormat
    {
        get
        {
            if (chromatogramArraysMeta != null) return chromatogramArraysMeta.Format;
            else
            {
                var reader = OpenChromatogramDataReader();
                if (reader == null) return null;
                return reader.Metadata.Format;
            }
        }
    }

    /// <summary>Gets the buffer format used for wavelength spectrum data arrays.</summary>
    public BufferFormat? WavelengthSpectrumDataFormat
    {
        get
        {
            if (wavelengthSpectrumArraysMeta != null) return wavelengthSpectrumArraysMeta.Format;
            else
            {
                var reader = OpenWavelengthSpectrumDataReader();
                if (reader == null) return null;
                return reader.Metadata.Format;
            }
        }
    }

    /// <summary>Gets whether the file contains mass spectrum peak data.</summary>
    public bool HasSpectrumPeaks => spectrumPeaksArraysMeta != null ? true : SpectrumPeaksDataReaderMeta != null;

    /// <summary>Gets the metadata for the spectrum data reader.</summary>
    public DataArraysReaderMeta? SpectrumDataReaderMeta => OpenSpectrumDataReader()?.Metadata;
    /// <summary>Gets the metadata for the spectrum peaks data reader.</summary>
    public DataArraysReaderMeta? SpectrumPeaksDataReaderMeta => OpenSpectrumPeaksDataReader()?.Metadata;
    /// <summary>Gets the metadata for the chromatogram data reader.</summary>
    public DataArraysReaderMeta? ChromatogramDataReaderMeta => OpenChromatogramDataReader()?.Metadata;

    /// <summary>Asynchronously enumerates all spectra with their descriptions and data.</summary>
    public async IAsyncEnumerable<(SpectrumDescription, StructArray)> EnumerateSpectraAsync()
    {
        var dataReader = OpenSpectrumDataReader();
        if (dataReader != null && spectrumMetadata != null)
        {
            await foreach (var item in new DataFacet<SpectrumDescription>(spectrumMetadata, dataReader, OpenSpectrumPeaksDataReader()) { ModalityPreference = SpectrumDataModalityPreference }.EnumerateAsync())
                yield return item;
        }
    }

    /// <summary>Asynchronously enumerates all chromatograms with their descriptions and data.</summary>
    public async IAsyncEnumerable<(ChromatogramDescription, StructArray)> EnumerateChromatogramsAsync()
    {
        var dataReader = OpenChromatogramDataReader();
        if (dataReader != null && chromatogramMetadata != null)
        {
            await foreach (var item in new DataFacet<ChromatogramDescription>(chromatogramMetadata, dataReader).EnumerateAsync())
                yield return item;
        }
    }

    /// <summary>Asynchronously enumerates all spectra with their descriptions and data.</summary>
    public async IAsyncEnumerable<(SpectrumDescription, StructArray)> EnumerateWavelengthSpectraAsync()
    {
        var dataReader = OpenWavelengthSpectrumDataReader();
        if (dataReader != null && wavelengthSpectrumMetadata != null)
        {
            await foreach (var item in new DataFacet<SpectrumDescription>(wavelengthSpectrumMetadata, dataReader).EnumerateAsync())
                yield return item;
        }
    }

    DataArraysReader? OpenSpectrumDataReader()
    {
        var dataFacet = storage.SpectrumData();
        DataArraysReader reader;
        if (dataFacet == null)
        {
            return null;
        }
        if (spectrumArraysMeta == null)
        {
            reader = new DataArraysReader(dataFacet, BufferContext.Spectrum);
            reader.SpacingModels = spectrumMetadata?.GetSpacingModelIndex();
            spectrumArraysMeta = reader.Metadata;
        }
        else
        {
            reader = new DataArraysReader(dataFacet, spectrumArraysMeta);
        }
        return reader;
    }

    DataArraysReader? OpenSpectrumPeaksDataReader()
    {
        var dataFacet = storage.SpectrumPeaks();
        DataArraysReader reader;
        if (dataFacet == null)
        {
            return null;
        }
        if (spectrumPeaksArraysMeta == null)
        {
            reader = new DataArraysReader(dataFacet, BufferContext.Spectrum)
            {
                SpacingModels = spectrumMetadata?.GetSpacingModelIndex()
            };
            spectrumPeaksArraysMeta = reader.Metadata;
        }
        else
        {
            reader = new DataArraysReader(dataFacet, spectrumPeaksArraysMeta);
        }
        return reader;
    }

    DataArraysReader? OpenChromatogramDataReader()
    {
        var dataFacet = storage.ChromatogramData();
        DataArraysReader reader;
        if (dataFacet == null)
        {
            return null;
        }
        if (chromatogramArraysMeta == null)
        {
            reader = new DataArraysReader(dataFacet, BufferContext.Chromatogram);
            chromatogramArraysMeta = reader.Metadata;
        }
        else
        {
            reader = new DataArraysReader(dataFacet, chromatogramArraysMeta);
        }
        return reader;
    }

    DataArraysReader? OpenWavelengthSpectrumDataReader()
    {
        var dataFacet = storage.WavelengthSpectrumData();
        DataArraysReader reader;
        if (dataFacet == null)
        {
            return null;
        }
        if (wavelengthSpectrumArraysMeta == null)
        {
            reader = new DataArraysReader(dataFacet, BufferContext.WavelengthSpectrum);
            wavelengthSpectrumArraysMeta = reader.Metadata;
        }
        else
        {
            reader = new DataArraysReader(dataFacet, wavelengthSpectrumArraysMeta);
        }
        return reader;
    }

    /// <summary>Gets the data arrays for a spectrum by index.</summary>
    /// <param name="index">The spectrum index.</param>
    public async ValueTask<StructArray?> GetSpectrumData(ulong index, SpectrumDataModalityPreference? spectrumDataModalityPreference = SpectrumDataModalityPreference.PreferProfiles)
    {
        if (spectrumDataModalityPreference == SpectrumDataModalityPreference.PreferPeaks && spectrumMetadata?.NumberOfPeaks(index) > 0)
        {
            return await GetSpectrumPeaks(index);
        }
        var reader = OpenSpectrumDataReader();
        if (reader == null) return null;
        var nbPoints = spectrumMetadata?.NumberOfDataPointsFor(index);
        if (nbPoints == null) return null;
        return await reader.ReadForIndex(index);
    }

    /// <summary>Gets the data arrays for a wavelength spectrum by index.</summary>
    /// <param name="index">The spectrum index.</param>
    public async ValueTask<StructArray?> GetWavelengthSpectrumData(ulong index)
    {
        var reader = OpenWavelengthSpectrumDataReader();
        if (reader == null) return null;
        return await reader.ReadForIndex(index);
    }


    /// <summary>Gets the peak data arrays for a spectrum by index.</summary>
    /// <param name="index">The spectrum index.</param>
    public async ValueTask<StructArray?> GetSpectrumPeaks(ulong index)
    {
        var reader = OpenSpectrumPeaksDataReader();
        if (reader == null) return null;
        var nbPoints = spectrumMetadata?.NumberOfPeaks(index);
        if (nbPoints == null) return null;
        return await reader.ReadForIndex(index);
    }

    /// <summary>Gets the data arrays for a chromatogram by index.</summary>
    /// <param name="index">The chromatogram index.</param>
    public async ValueTask<StructArray?> GetChromatogramData(ulong index)
    {
        var reader = OpenChromatogramDataReader();
        if (reader == null) return null;
        return await reader.ReadForIndex(index);
    }
}