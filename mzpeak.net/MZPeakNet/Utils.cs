using Microsoft.Extensions.Logging;
using MZPeak.Reader.Visitors;

namespace MZPeak.Util;


public static class LoggingConfig
{
    public static void ConfigureLogging(ILoggerFactory loggerFactory)
    {
        Writer.MZPeakWriter.Logger = loggerFactory.CreateLogger("MZPeak.Writer");

        Reader.MzPeakReader.Logger = loggerFactory.CreateLogger("MZPeak.Reader");
        Reader.BaseLayoutReader.Logger = loggerFactory.CreateLogger("MZPeak.Reader.DataLayout");
        Metadata.MetadataReaderBase<SpectrumDescription>.Logger = loggerFactory.CreateLogger("MZPeak.Reader.Metadata");
        Metadata.MetadataReaderBase<ChromatogramDescription>.Logger = loggerFactory.CreateLogger("MZPeak.Reader.Metadata");

        Compute.Compute.Logger = loggerFactory.CreateLogger("MZPeak.Compute");
        Compute.NullInterpolation.Logger = loggerFactory.CreateLogger("MZPeak.Compute.NullInterpolation");

        Storage.IMZPeakArchiveStorage.Logger = loggerFactory.CreateLogger("MZPeak.Storage.Reader");
        Storage.IMZPeakArchiveWriter.Logger = loggerFactory.CreateLogger("MZPeak.Storage.Writer");
        Writer.Data.BaseDataLayoutWriter.Logger = loggerFactory.CreateLogger("MZPeak.Storage.Writer.Data");
    }
}