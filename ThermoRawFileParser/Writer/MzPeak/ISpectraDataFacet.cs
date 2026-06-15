using System;
using System.Collections.Generic;

namespace ThermoRawFileParser.Writer
{
    // The spectra_data facet contract shared by the point and chunk layouts: append a scan's
    // (mz,intensity) arrays, expose the running data-point count and temp path, and close with the
    // footer KV. The Write() path selects the implementation by the layout flag.
    internal interface ISpectraDataFacet : IDisposable
    {
        string TempPath { get; }
        long PointCount { get; }
        void Append(ulong ordinal, double[] mz, float[] intensity);
        void Close(IReadOnlyDictionary<string, string> finalMetadata);
    }
}
