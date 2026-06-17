namespace MzPeakTests;

using System.Text.Json;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Types;
using MZPeak.Compute;
using MZPeak.ControlledVocabulary;
using MZPeak.Metadata;
using MZPeak.Reader;
using MZPeak.Reader.Visitors;
using MZPeak.Storage;

public class ArchiveTest
{
    IMZPeakArchiveStorage PointArchive;
    IMZPeakArchiveStorage ChunkArchive;

    public ArchiveTest()
    {
        string fileName = "small.mzpeak";
        string baseDirectory = AppContext.BaseDirectory; // Gets the directory where tests are running
        string fullPath = Path.Combine(baseDirectory, fileName);
        PointArchive = new LocalZipArchive(fullPath);
        fileName = "small.chunked.mzpeak";
        baseDirectory = AppContext.BaseDirectory; // Gets the directory where tests are running
        fullPath = Path.Combine(baseDirectory, fileName);
        ChunkArchive = new LocalZipArchive(fullPath);
    }

    [Fact]
    public void RawZipArchive_LoadIndex()
    {
        var index = PointArchive.FileIndex();
        Assert.Equal(5, index.Files.Count);
        Assert.Equal(6, PointArchive.FileNames().Count);
    }

    [Fact]
    public async Task RawZipArchive_LoadSpectrumPoint()
    {
        var meta = PointArchive.SpectrumMetadata();
        Assert.NotNull(meta);
        var metaReader = new SpectrumMetadataReader(meta);
        var models = metaReader.GetSpacingModelIndex();
        Assert.Equal(14, models.Count);
        var reader = PointArchive.SpectrumData();
        Assert.NotNull(reader);

        var dataReader = new DataArraysReader(reader, BufferContext.Spectrum)
        {
            SpacingModels = models
        };
        Assert.Equal(BufferFormat.Point, dataReader.Metadata.Format);
        Assert.Single(dataReader.RowGroupIndex);
        Assert.True(dataReader.ArrayIndex.Entries.All((e) => e.SchemaIndex != null));
        Assert.NotNull(await dataReader.ReadForIndex(0));
        Assert.NotNull(await dataReader.ReadForIndex(1));
        var it = dataReader.Enumerate();
        await foreach ((ulong i, StructArray chunk) in it)
        {
            var dtype = (StructType)chunk.Data.DataType;
            foreach (var (f, arr) in dtype.Fields.Zip(chunk.Fields))
            {
                Assert.Equal(0, arr.NullCount);
            }
        }
    }

    [Fact]
    public async Task RawZipArchive_LoadSpectrumChunk()
    {
        var meta = ChunkArchive.SpectrumMetadata();
        Assert.NotNull(meta);
        var metaReader = new SpectrumMetadataReader(meta);
        var models = metaReader.GetSpacingModelIndex();
        Assert.Equal(14, models.Count);

        var reader = ChunkArchive.SpectrumData();
        Assert.NotNull(reader);
        var dataReader = new DataArraysReader(reader, BufferContext.Spectrum)
        {
            SpacingModels = models
        };

        Assert.Equal(BufferFormat.ChunkValues, dataReader.Metadata.Format);
        Assert.Single(dataReader.RowGroupIndex);
        Assert.True(dataReader.ArrayIndex.Entries.All((e) => e.SchemaIndex != null));
        var data = await dataReader.ReadForIndex(10);
        Assert.NotNull(data);

        var it = dataReader.Enumerate();
        await foreach ((ulong i, StructArray chunk) in it)
        {
            var dtype = (StructType)chunk.Data.DataType;
            foreach (var (f, arr) in dtype.Fields.Zip(chunk.Fields))
            {
                Assert.Equal(0, arr.NullCount);
                Assert.NotEqual(0, arr.Length);
            }
        }
    }

    [Fact]
    public void RawZipArchive_LoadSpectrumIndex()
    {
        var reader = PointArchive.SpectrumData();
        Assert.NotNull(reader);

        var kvMeta = reader.ParquetReader.FileMetaData.KeyValueMetadata;
        var arrayIndexText = kvMeta["spectrum_array_index"];
        Assert.NotNull(arrayIndexText);
        var arrayIndex = JsonSerializer.Deserialize<ArrayIndex>(arrayIndexText);
        Assert.NotNull(arrayIndex);
        Assert.Equal("point", arrayIndex.Prefix);
        Assert.Equal(BufferFormat.Point, arrayIndex.Entries[0].BufferFormat);
        Assert.Equal(BufferFormat.Point, arrayIndex.Entries[1].BufferFormat);
    }

    [Fact]
    public void RawZipArchive_SpectrumMetadata()
    {
        var stream = PointArchive.SpectrumMetadata();
        Assert.NotNull(stream);
        var meta = new SpectrumMetadataReader(stream);
        Assert.NotNull(meta);
        Assert.NotNull(meta.SpectrumMetadata);
        var chunk = ((StructArray)meta.SpectrumMetadata.Array(0)).AsRecordBatch();
        var col = chunk.Column("index");
        Assert.NotNull(col);
        Assert.Equal(48, col.Length);
        var schema = chunk.Schema;
        for (var i = 0; i < schema.FieldsList.Count; i++)
        {
            // Console.WriteLine("{0} => {1} : {2}", i, schema.FieldsList[i].Name, schema.FieldsList[i].DataType);
        }
        var idxArray = (UInt64Array)col;
        Assert.NotNull(idxArray.GetValue(0));
        Assert.Equal(0ul, idxArray.GetValue(0));

        col = ((StructArray?)meta.ScanMetadata?.Array(0))?.AsRecordBatch().Column("parameters");
        Assert.NotNull(col);
        var builder = new ParamListVisitor();
        builder.Visit(col);
        var paramsList = builder.ParamsLists;
        var k = 0;
        foreach (var pars in paramsList)
        {
            k += pars.Count;
        }
        Assert.True(k > 0);
    }

    [Fact]
    public async Task RawZipArchive_LoadSpectrumPoint_GetDataIter()
    {
        ulong i = 0;
        var reader = PointArchive.SpectrumData();

        Assert.NotNull(reader);

        var dataReader = new DataArraysReader(reader, BufferContext.Spectrum);
        var iter = dataReader.Enumerate();

        List<ulong> profileSpectrumIdx = [
            0,
            1,
            7,
            8,
            14,
            15,
            21,
            22,
            28,
            29,
            34,
            35,
            41,
            42
        ];

        await foreach (var pair in iter)
        {
            if (pair.Item1 > 10) break;
            Assert.Equal(profileSpectrumIdx[(int)i++], pair.Item1);
            Assert.NotEqual(0, pair.Item2.Length);
        }
        await iter.Seek(21);
        i = 6;
        await foreach (var pair in iter)
        {
            Assert.Equal(profileSpectrumIdx[(int)i++], pair.Item1);
            Assert.NotEqual(0, pair.Item2.Length);
        }
        Assert.Equal(profileSpectrumIdx.Count, (int)i);
    }
}

public class ParamTest
{
    [Fact]
    public void Param_FromJson()
    {
        var msg = "{\"name\": \"foobar\", \"value\": null}";
        var param = JsonSerializer.Deserialize<Param>(msg);
        Assert.NotNull(param);

        msg = "{\"name\": \"foobar\", \"value\": 150.1}";
        param = JsonSerializer.Deserialize<Param>(msg);
        Assert.NotNull(param);
        Assert.True(param.IsDouble());
        Assert.False(param.IsLong());
        Assert.Equal(150, param.AsLong());

        msg = "{\"name\": \"foobar\", \"value\": \"bazbang\"}";
        param = JsonSerializer.Deserialize<Param>(msg);
        Assert.NotNull(param);
        Assert.True(param.IsString());
    }

    [Fact]
    public void Param_ToJson()
    {
        var param = new Param("foobar", "UNK:000", true, "UO:0");
        var msg = JsonSerializer.Serialize(param);
        var expected = "{\"name\":\"foobar\",\"accession\":\"UNK:000\",\"value\":true,\"unit\":\"UO:0\"}";
        Assert.Equal(expected, msg);
    }
}