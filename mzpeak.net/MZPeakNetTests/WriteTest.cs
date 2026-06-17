using MZPeak.Storage;
using MZPeak.Reader;
using MZPeak.Writer;
using Apache.Arrow;
using MZPeak.ControlledVocabulary;
using MZPeak.Reader.Visitors;
using MZPeak.Metadata;
using MZPeak.Writer.Data;
using MZPeak.Compute;
using Apache.Arrow.Types;
using System.Text.Json;

namespace MzPeakTests;


public class WriteTest
{
    IMZPeakArchiveStorage PointArchive;
    public WriteTest()
    {
        string fileName = "small.mzpeak";
        string baseDirectory = AppContext.BaseDirectory; // Gets the directory where tests are running
        string fullPath = Path.Combine(baseDirectory, fileName);
        PointArchive = new LocalZipArchive(fullPath);
    }

    [Fact]
    public void BuildArrayIndexTest()
    {
        var builder = ArrayIndexBuilder.PointBuilder(BufferContext.Spectrum);
        builder.Add(ArrayType.MZArray, BinaryDataType.Float64, Unit.MZ, 0);
        builder.Add(ArrayType.IntensityArray, BinaryDataType.Float32, Unit.NumberOfDetectorCounts);
        var index = builder.Build();
        Assert.Equal("point.mz", index.Entries[0].Path);
        Assert.Equal(0u, index.Entries[0].SortingRank);
        Assert.Equal("point.intensity", index.Entries[1].Path);
        Assert.Null(index.Entries[1].SortingRank);
    }

    [Fact]
    public void PointLayoutBuilderTest()
    {
        var builder = ArrayIndexBuilder.PointBuilder(BufferContext.Spectrum);
        builder.Add(ArrayType.MZArray, BinaryDataType.Float64, Unit.MZ, 1);
        builder.Add(ArrayType.IntensityArray, BinaryDataType.Float32, Unit.NumberOfDetectorCounts);
        var index = builder.Build();
        var writer = new PointLayoutBuilder(index);
        writer.Add(0, [
            new DoubleArray.Builder().AppendRange([250.0]).Build(),
            new FloatArray.Builder().AppendRange([1023.1f]).Build()
        ]);
        writer.Add(1, [
            new DoubleArray.Builder().AppendRange([252.0]).Build(),
            new FloatArray.Builder().AppendRange([1026.1f]).Build()
        ]);
        Assert.Equal(2ul, writer.NumberOfPoints);
    }

    [Fact]
    public void ChunkedLayoutBuilderTest()
    {
        var builder = ArrayIndexBuilder.ChunkBuilder(BufferContext.Spectrum);
        builder.Add(ArrayType.MZArray, BinaryDataType.Float64, Unit.MZ, 1);
        builder.Add(ArrayType.IntensityArray, BinaryDataType.Float32, Unit.NumberOfDetectorCounts);
        var index = builder.Build();
        var writer = new ChunkLayoutBuilder(index);
        writer.Add(0, [
            new DoubleArray.Builder().AppendRange([250.0,   251.0, 272.0, 500.0, 501.0, 512.0]).Build(),
            new FloatArray.Builder().AppendRange([1023.1f, 20.0f, 200f,  300f,  100f,  500f]).Build()
        ]);
        writer.Add(1, [
            new DoubleArray.Builder().AppendRange([252.0]).Build(),
            new FloatArray.Builder().AppendRange([1026.1f]).Build()
        ]);
        Assert.Equal(7ul, writer.NumberOfPoints);
        var bat = writer.GetRecordBatch();
        Assert.Equal(3, bat.Length);
        var cols = (StructArray)bat.Column(0);
    }

    [Fact]
    public void BuildChunkedArrayIndexTest()
    {
        var builder = ArrayIndexBuilder.ChunkBuilder(BufferContext.Spectrum);
        builder.Add(ArrayType.MZArray, BinaryDataType.Float64, Unit.MZ, 1);
        builder.Add(ArrayType.IntensityArray, BinaryDataType.Float32, Unit.NumberOfDetectorCounts);
        var index = builder.Build();
        bool hasEncoding = false;
        bool hasStart = false;
        bool hasEnd = false;
        bool hasChunkValues = false;
        bool hasPointFormat = false;
        foreach (var entry in index.Entries)
        {
            switch (entry.BufferFormat)
            {
                case BufferFormat.ChunkEncoding:
                    {
                        hasEncoding = true;
                        break;
                    }
                case BufferFormat.ChunkEnd:
                    {
                        hasEnd = true;
                        break;
                    }
                case BufferFormat.ChunkStart:
                    {
                        hasStart = true;
                        break;
                    }
                case BufferFormat.ChunkValues:
                    {
                        hasChunkValues = true;
                        break;
                    }
                case BufferFormat.Point:
                    {
                        hasPointFormat = true;
                        break;
                    }
                default: continue;
            }
        }

        Assert.True(hasEncoding, "Chunk encoding not found");
        Assert.True(hasChunkValues, "Chunk values not found");
        Assert.True(hasStart, "Chunk start not found");
        Assert.True(hasEnd, "Chunk end not found");
        Assert.False(hasPointFormat, "Chunked layout should not contain a point format array");
    }

    [Fact]
    public async Task TranscribePointsArrow()
    {
        var reader = new MzPeakReader(PointArchive);

        var builder = ArrayIndexBuilder.PointBuilder(BufferContext.Spectrum);
        builder.Add(ArrayType.MZArray, BinaryDataType.Float64, Unit.MZ, 1);
        builder.Add(ArrayType.IntensityArray, BinaryDataType.Float32, Unit.NumberOfDetectorCounts);

        StructArray? data = await reader.GetSpectrumData(0);
        Assert.NotNull(data);
        var chunk = (StructArray)data;
        var n0 = chunk.Length;

        var intensities = (FloatArray)chunk.Fields[2];
        var count = Compute.Equal(intensities, 0f).Sum(v => v != null ? ((bool)v ? 1 : 0) : 0);

        var index = builder.Build();
        var writer = new PointLayoutBuilder(index);

        writer.Add(0, [(Apache.Arrow.Array)chunk.Fields[1], (Apache.Arrow.Array)chunk.Fields[2]]);

        data = await reader.GetSpectrumData(1);
        Assert.NotNull(data);
        chunk = data;
        var n1 = chunk.Length;

        writer.Add(1, [(Apache.Arrow.Array)chunk.Fields[1], (Apache.Arrow.Array)chunk.Fields[2]]);

        var batch = writer.GetRecordBatch();
        Assert.Equal(1, batch.ColumnCount);
        var points = (StructArray)batch.Column(0);
        Assert.Equal(3, points.Fields.Count);

        var idxArr = (UInt64Array)points.Fields[0];
        var mask0 = Compute.Equal(idxArr, 0ul);
        var points0 = (StructArray)Compute.Filter(points, mask0);

        var intensities0 = (FloatArray)points0.Fields[2];
        var count0 = Compute.Equal(intensities0, 0f).Sum(v => v != null ? ((bool)v ? 1 : 0) : 0);
        Assert.True(count0 > 0);
        Assert.Equal(n0, points0.Length);

        var mask1 = Compute.Equal(idxArr, 1ul);
        var points1 = Compute.Filter(points, mask1);
        Assert.Equal(n1, points1.Length);
    }

    [Fact]
    public async Task TranscribePointsParquet()
    {
        var stream = new MemoryStream();
        var writer = new MZPeakWriter(new ZipStreamArchiveWriter<MemoryStream>(stream));

        // This ensures that spacing models are learned on profile data
        writer.SpectraUseNullMarking();

        var reader = new MzPeakReader(PointArchive);
        Assert.NotNull(reader);
        var dat0 = await reader.GetSpectrumData(0);
        var meta0 = reader.GetSpectrumDescription(0);
        Assert.NotNull(dat0);
        Assert.NotNull(meta0);

        var refParam0 = meta0.Parameters.Find((p) =>
            {
                return p.AccessionCURIE == SpectrumProperties.NumberOfDataPoints.CURIE();
            });
        Assert.NotNull(refParam0);
        Assert.True(meta0.IsProfile);

        // Write the data itself
        var derivedMeta = writer.AddSpectrumData(
            // Write for the spectrum we are currently writing metadata for
            writer.CurrentSpectrum,
            dat0.Fields.Skip(1), // Skip the entry index array
            meta0.IsProfile
        );

        var (deltaModel, auxArrays, nPoints, nPeaks) = derivedMeta;

        // No unexpected arrays in the input
        Assert.Empty(auxArrays);

        // We are using null marking so this will populate as the input is profile mode
        Assert.NotNull(deltaModel);

        Assert.NotNull(nPoints);
        Assert.Null(nPeaks);

        Assert.Equal(meta0.DataPointCount, nPoints);
        Assert.Equal((int)nPoints, refParam0.AsLong());


        meta0.DataPointCount = nPoints;
        meta0.PeakCount = nPeaks;

        var index = writer.AddSpectrum(
            meta0.Id,
            meta0.Time,
            null,
            meta0.Parameters,
            derivedMeta
        );

        writer.AddScan(
            index,
            meta0.Scans[0].InstrumentConfigurationRef,
            meta0.Scans[0].Parameters,
            meta0.Scans[0].IonMobility,
            meta0.Scans[0].IonMobilityTypeCURIE
        );

        if (meta0.Precursors.Count > 0)
        {
            var prec = meta0.Precursors[0];
            writer.AddPrecursor(
                index,
                prec.PrecursorIndex,
                prec.PrecursorId,
                prec.IsolationWindowParameters,
                prec.ActivationParameters
            );
        }
        if (meta0.SelectedIons.Count > 0)
        {
            var prec = meta0.SelectedIons[0];
            writer.AddSelectedIon(
                index,
                prec.PrecursorIndex,
                prec.Parameters,
                prec.IonMobility,
                prec.IonMobilityTypeCURIE
            );
        }
        writer.Close();

        stream.Position = 0;
        var dupReader = new MzPeakReader(new ZipArchiveStream<MemoryStream>(stream));
        var rec0 = dupReader.GetSpectrumDescription(0);
        var param0 = rec0.Parameters.Find((p) =>
            {
            return p.AccessionCURIE == SpectrumProperties.NumberOfDataPoints.CURIE();
            });
        Assert.Equal(0ul, rec0.Index);
        Assert.NotNull(param0);
        Assert.Equal(nPoints ?? nPeaks ?? 0, param0.AsLong());
    }

    [Fact]
    public void WriteMemory_Test()
    {
        var stream = new MemoryStream();
        var writer = new MZPeakWriter(new ZipStreamArchiveWriter<MemoryStream>(stream));
        writer.AddSpectrum("foobar", 299.0, null, [new Param("baz", 5)], EntryDerivedMetadata.Empty);
        writer.WriteSpectrumMetadata();
        writer.Dispose();
        stream.Flush();
        stream.Seek(0, SeekOrigin.Begin);

        Assert.True(stream.CanRead);

        var readerStorage = new ZipArchiveStream<MemoryStream>(stream);
        var reader = new MzPeakReader(readerStorage);
        Assert.NotNull(reader.SpectrumTable);
        var meta = ((StructArray)reader.SpectrumTable.Array(0)).AsRecordBatch();
        Assert.NotNull(meta);

        var idArr = (StringArray)meta.Column("id");
        Assert.Equal("foobar", idArr.GetString(0));
        var indexArr = (UInt64Array)meta.Column("index");
        Assert.Equal(0ul, indexArr.GetValue(0));

        var paramsList = (ListArray)meta.Column("parameters");
        var visitor = new ParamListVisitor();
        paramsList.Accept(visitor);
        Assert.Single(visitor.ParamsLists);
        Assert.Single(visitor.ParamsLists[0]);
        var paramVal = visitor.ParamsLists[0][0];
        Assert.Equal("baz", paramVal.Name);
        Assert.True(paramVal.IsLong());
        Assert.Equal(5L, paramVal.AsLong());
    }

    [Fact]
    public void FileIndexLabelCoercion()
    {
        var msg = JsonSerializer.Serialize(EntityType.Spectrum);
        Assert.Equal("\"spectrum\"", msg);

        msg = JsonSerializer.Serialize(new EntityType(EntityTypeTag.Other, "foobar"));
        Assert.Equal("\"foobar\"", msg);

        var inst = JsonSerializer.Deserialize<EntityType>("\"Spectrum\"");
        Assert.Equal(EntityType.Spectrum, inst);

        inst = JsonSerializer.Deserialize<EntityType>("\"Foobar\"");
        Assert.Equal(new EntityType(EntityTypeTag.Other, "foobar"), inst);

        msg = JsonSerializer.Serialize(DataKind.Metadata);
        Assert.Equal("\"metadata\"", msg);

        msg = JsonSerializer.Serialize(new DataKind(DataKindTag.Other, "foobar"));
        Assert.Equal("\"foobar\"", msg);

        var inst2 = JsonSerializer.Deserialize<DataKind>("\"Metadata\"");
        Assert.Equal(DataKind.Metadata, inst2);

        inst2 = JsonSerializer.Deserialize<DataKind>("\"Foobar\"");
        Assert.Equal(new DataKind(DataKindTag.Other, "foobar"), inst2);
    }
}