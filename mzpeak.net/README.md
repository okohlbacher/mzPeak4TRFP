Prototype for reading [`mzPeak`](https://github.com/mobiusklein/mzpeak_prototyping/blob/main/doc/index.md) files in .NET.

## Usage

### Reading

```csharp
using MZPeak.Reader;

// Open an mzPeak file
var reader = new MzPeakReader("path/to/file.mzpeak");

// Access file-level metadata
Console.WriteLine($"{reader.SpectrumCount} spectra, {reader.ChromatogramCount} chromatograms");
Console.WriteLine($"Storage format: {reader.SpectrumDataFormat}");

// Iterate over spectra asynchronously
await foreach (var (description, data) in reader.EnumerateSpectraAsync())
{
    Console.WriteLine($"Spectrum {description.Index}: {description.Id}");
    Console.WriteLine($"  Time: {description.Time}");
    Console.WriteLine($"  Points: {data.Length}");
    Console.WriteLine($"  Profile: {description.IsProfile}, Centroid: {description.IsCentroid}");

    // Access scan information
    foreach (var scan in description.Scans)
    {
        Console.WriteLine($"  Scan config: {scan.InstrumentConfigurationRef}");
    }

    // Access precursor information (for MS2+ spectra)
    foreach (var precursor in description.Precursors)
    {
        Console.WriteLine($"  Precursor index: {precursor.PrecursorIndex}");
    }
}

// Iterate over chromatograms asynchronously
await foreach (var (description, data) in reader.EnumerateChromatogramsAsync())
{
    Console.WriteLine($"Chromatogram {description.Index}: {description.Id}");
    Console.WriteLine($"  Points: {data.Length}");
}
```


### Writing

TODO: Code sample

See [MZPeakNet.AppTest/Program.cs] `TranscodeFile` or `ThermoTranslate`

## Status

- [x] Reading
  - [x] Array indices
  - [x] File-level metadata
  - [x] Spectrum metadata
  - [x] Chromatogram metadata
  - [x] Spectrum data arrays
    - [x] Point Layout
    - [x] Chunked Layout
      - [x] Basic encoding
      - [x] Delta encoding
      - [x] Numpress and opaque chunk transforms
  - [x] Chromatogram data arrays
  - [x] Spectrum peak arrays
  - [x] Auxiliary arrays
  - [x] ZIP archive storage
  - [x] Directory storage
- [ ] Writing
  - [x] File-level metadata
  - [x] Spectrum metadata
  - [x] Chromatogram metadata
  - [x] Data Arrays
    - [x] Point Layout
    - [x] Chunked Layout
  - [x] Spectrum peak arrays
  - [x] Auxiliary arrays
  - [x] ZIP archive storage
  - [ ] Directory storage
