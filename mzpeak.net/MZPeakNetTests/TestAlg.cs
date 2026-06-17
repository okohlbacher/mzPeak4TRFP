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


public class NullInterpolationTest
{
    IMZPeakArchiveStorage PointArchive;
    IMZPeakArchiveStorage ChunkArchive;

    public NullInterpolationTest()
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
    public void TestMasking()
    {
        var v = new List<int>()
        {
            1,2,3,4,
            6,7,8,9
        };

        var spans = Compute.IndicesToSpans(v);
        Assert.Equal(2, spans.Count);
        Assert.Equal((1, 4), spans[0]);
        Assert.Equal((6, 9), spans[1]);

        v =
        [
            1,2,4,
            6,7,8,9
        ];
        spans = Compute.IndicesToSpans(v);
        Assert.Equal((1, 2), spans[0]);
        Assert.Equal((4, 4), spans[1]);
        Assert.Equal((6, 9), spans[2]);

        var builder = new Int32Array.Builder();
        builder.AppendRange([
            0,
            1,
            2,
            4,
            5,
            6,
            7,
            8,
            9,
            10
        ]);
        var vals = builder.Build();
        var subset = (Int32Array)Compute.Take(vals, spans);

        foreach (var i in v)
        {
            var j = vals.GetValue(i);
            Assert.NotNull(j);
            Assert.Contains(j, subset);
        }
    }

    [Fact]
    public async Task TestLearnDelta()
    {
        var reader = new MzPeakReader(PointArchive);
        var specData = await reader.GetSpectrumData(0);
        Assert.NotNull(specData);

        var chunk = specData;
        Assert.Equal(0, chunk.NullCount);

        var mzsArr = (DoubleArray)chunk.Fields[1];
        Assert.Equal(0, mzsArr.NullCount);
        var mzs = mzsArr.ToList();
        var intensitiesArr = (FloatArray)chunk.Fields[2];
        Assert.Equal(0, intensitiesArr.NullCount);
        var intensities = intensitiesArr.Select((v) => (double?)v).ToList();
        Assert.Equal(mzs.Count, intensities.Count);

        var deltas = NullInterpolation.CollectDeltas(mzs, sort: false);
        var model = SpacingInterpolationModel<double>.Fit(
            mzs.Skip(1).ToList(),
            deltas,
            intensities.Skip(1).ToList()
        );
        Assert.Equal(3, model.Coefficients.Count);
        for (var i = 0; i < model.Coefficients.Count; i++)
        {
            Assert.True(Math.Abs(model.Coefficients[i]) < 1e-6);
        }
    }

    [Fact]
    public async Task TestChunking()
    {
        var reader = new MzPeakReader(PointArchive);
        var specData = await reader.GetSpectrumData(0);
        Assert.NotNull(specData);

        var chunk = specData;
        Assert.Equal(0, chunk.NullCount);

        var mzsArr = (DoubleArray)chunk.Fields[1];
        Assert.Equal(0, mzsArr.NullCount);
        var intensitiesArr = (FloatArray)chunk.Fields[2];

        var splits = Chunking.ChunkEvery(mzsArr, 50.0);

        var mask = Compute.Invert(ZeroRunRemoval.IsZeroPairMask(intensitiesArr));
        var maskedMzs = Compute.NullifyAt(mzsArr, mask);
        Assert.Equal(11213, maskedMzs.NullCount);
        var splitsMasked = Chunking.ChunkEvery(maskedMzs, 50.0);
        foreach (var (ii, jj) in splitsMasked.Zip(splits))
        {
            Assert.Equal(ii, jj);
        }
    }
}