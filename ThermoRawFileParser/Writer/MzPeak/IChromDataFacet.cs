using System;
using System.Collections.Generic;

namespace ThermoRawFileParser.Writer
{
    // The chromatograms_data facet contract shared by the point and chunk layouts: append a
    // (time, intensity, ms_level) sample, expose the running data-point count and temp path, and close
    // with the footer KV. The Write() path selects the implementation by the layout flag (the chunk
    // layout ignores ms_level, mirroring the reference chunk struct).
    internal interface IChromDataFacet : IDisposable
    {
        string TempPath { get; }
        long PointCount { get; }
        void Append(double time, double intensity, long msLevel);
        void Close(IReadOnlyDictionary<string, string> finalMetadata);
    }
}
