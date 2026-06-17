namespace MZPeak.Compute;

using System.Numerics;
using Apache.Arrow;
using Apache.Arrow.Memory;
using Apache.Arrow.Types;

using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.Logging;

public class SpacingInterpolationModel<T> where T : struct, INumber<T>
{
    List<T> coefficients;

    public SpacingInterpolationModel(List<T> coefficients)
    {
        this.coefficients = new();
        Coefficients = coefficients;
    }

    public List<T> Coefficients
    {
        get => coefficients;
        set
        {
            coefficients = value;
            if (value.Count < 1)
            {

                throw new ArgumentOutOfRangeException(message: "Spacing Interpolation Model's coefficients must not be empty!", paramName: "value");
            }
        }
    }

    public T Predict(T value)
    {
        var acc = T.One * Coefficients[0];
        for (int i = 1; i < Coefficients.Count; i++)
        {
            var x = value;
            for (int j = 1; j < i; j++)
            {
                x *= value;
            }
            acc += x * Coefficients[i];
        }
        return acc;
    }

    public T MeanSquaredError(IReadOnlyList<T?> coordinates, List<T> deltas)
    {
        var acc = T.Zero;
        var n = T.Zero;
        foreach (var (x, y) in coordinates.Zip(deltas))
        {
            if (x == null) continue;
            var e = y - Predict((T)x);
            acc += e * e;
            n += T.One;
        }
        return acc / n;
    }

    public static SpacingInterpolationModel<double>? FromArray(IArrowArray array)
    {
        var coefs = new List<double>();
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Float:
                {
                    foreach (var v in (FloatArray)array)
                    {
                        if (v != null)
                        {
                            coefs.Add((double)v);
                        }
                    }
                    break;
                }
            case ArrowTypeId.Double:
                {
                    foreach (var v in (DoubleArray)array)
                    {
                        if (v != null)
                        {
                            coefs.Add((double)v);
                        }
                    }
                    break;
                }
            default:
                {
                    throw new InvalidDataException("Only float and double arrays are supported in mz_delta_model");
                }
        }
        return coefs.Count > 0 ? new SpacingInterpolationModel<double>(coefs) : null;
    }

    public static SpacingInterpolationModel<U> FitMedian<U>(IReadOnlyList<U?> coordinates) where U : struct, INumber<U>
    {
        var value = NullInterpolation.LocalMedianDelta(coordinates);
        return new(new() { value });
    }

    static (Matrix<U> data, MathNet.Numerics.LinearAlgebra.Vector<U> y, MathNet.Numerics.LinearAlgebra.Vector<U> cholWeights) ComputeFitArgs<U>(IReadOnlyList<U?> coordinates, List<U> deltas, IReadOnlyList<U?>? weights = null, U? deltaThreshold = null, int rank = 2) where U : struct, INumber<U>, IRootFunctions<U>
    {
        if (deltaThreshold == null) deltaThreshold = U.One;

        var columns = new List<List<U>>();
        for (var i = 0; i <= rank; i++) columns.Add(new());

        var weightsTransformed = new List<U>();
        var deltasFiltered = new List<U>();
        for (var i = 0; i < coordinates.Count; i++)
        {
            var viMaybe = coordinates[i];
            if (viMaybe == null) throw new InvalidDataException("Values cannot be null");
            if (deltas[i] > deltaThreshold) continue;
            deltasFiltered.Add(deltas[i]);
            var vi = (U)viMaybe;
            var v = U.One;
            var w = weights == null ? U.One : U.RootN(weights[i] ?? U.One, 2);

            weightsTransformed.Add(w);
            for (var r = 0; r <= rank; r++)
            {
                columns[r].Add(v);
                v *= vi;
            }
        }

        var cholWeights = MathNet.Numerics.LinearAlgebra.Vector<U>.Build.DenseOfEnumerable(weightsTransformed);
        var y = MathNet.Numerics.LinearAlgebra.Vector<U>.Build.DenseOfEnumerable(deltasFiltered);

        var data = Matrix<U>.Build.DenseOfColumns(columns);

        return (data, y, cholWeights);
    }

    public static SpacingInterpolationModel<U> FitRegression<U>(IReadOnlyList<U?> coordinates, List<U> deltas, IReadOnlyList<U?>? weights = null, U? deltaThreshold = null, int rank = 2) where U : struct, INumber<U>, IRootFunctions<U>
    {
        var (data, y, cholWeights) = ComputeFitArgs(coordinates, deltas, weights, deltaThreshold, rank);
        var QR = data.MapIndexed((i, j, v) => cholWeights[i] * v).QR();
        var cholY = cholWeights.PointwiseMultiply(y);
        var V = QR.Q.Transpose().Multiply(cholY);
        var betas = QR.R.Solve(V);

        SpacingInterpolationModel<U> model = new(betas.ToList());
        return model;
    }

    public static SpacingInterpolationModel<U> Fit<U>(PrimitiveArray<U> coordinates, Array? weights = null, U? deltaThreshold = null, int rank = 2) where U : struct, INumber<U>, IRootFunctions<U>
    {
        var deltas = NullInterpolation.CollectDeltas(coordinates, false);
        if (weights != null)
        {
            switch (coordinates.Data.DataType.TypeId)
            {
                case ArrowTypeId.Float:
                    {
                        weights = Compute.CastFloat(weights);
                        break;
                    }
                case ArrowTypeId.Double:
                    {
                        weights = Compute.CastDouble(weights);
                        break;
                    }
                default: throw new InvalidDataException($"Invalid data {coordinates.Data.DataType.Name} for fit");
            }
        }
        return Fit((PrimitiveArray<U>)coordinates.Slice(1, coordinates.Length - 1),
                    deltas, weights != null ? (PrimitiveArray<U>)weights.Slice(1, weights.Length - 1) : null, deltaThreshold, rank);
    }

    public static SpacingInterpolationModel<U> Fit<U>(IReadOnlyList<U?> coordinates, List<U> deltas, IReadOnlyList<U?>? weights = null, U? deltaThreshold = null, int rank = 2) where U : struct, INumber<U>, IRootFunctions<U>
    {
        var simpleModel = FitMedian(coordinates);
        if (deltas.Count <= 3) return simpleModel;
        var regressionModel = FitRegression(coordinates, deltas, weights, deltaThreshold, rank);
        var simpleErr = simpleModel.MeanSquaredError(coordinates, deltas);
        var regressionErr = regressionModel.MeanSquaredError(coordinates, deltas);
        if (simpleErr < regressionErr) return simpleModel;
        else return regressionModel;
    }
}

public static class ZeroRunRemoval
{
    public static List<int> WhereNotZeroRun<T>(IList<T?> data) where T : INumber<T>
    {
        List<int> acc = new();

        int n = data.Count;
        int n1 = n - 1;
        bool wasZero = false;
        int i = 0;
        while (i < n)
        {
            var v = data[i];
            if (v != null)
            {
                if (v == T.Zero)
                {
                    if (wasZero || (acc.Count == 0 && i < n1 && data[i + 1] == T.Zero) || i == n1)
                    { }
                    else
                    {
                        acc.Add(i);
                    }
                    wasZero = true;
                }
                else
                {
                    acc.Add(i);
                    wasZero = false;
                }
            }
            i += 1;
        }
        return acc;
    }

    public static List<int> WhereNotZeroRun<T>(PrimitiveArray<T> data) where T : struct, INumber<T>
    {
        List<int> acc = new();

        int n = data.Length;
        int n1 = n - 1;
        bool wasZero = false;
        int i = 0;
        while (i < n)
        {
            var v = data.GetValue(i);
            if (v != null)
            {
                if (v == T.Zero)
                {
                    if ((wasZero || acc.Count == 0) && (i < n1 && data.GetValue(i + 1) == T.Zero || i == n1))
                    { }
                    else
                    {
                        acc.Add(i);
                    }
                    wasZero = true;
                }
                else
                {
                    acc.Add(i);
                    wasZero = false;
                }
            }
            i += 1;
        }
        return acc;
    }

    public static BooleanArray IsZeroPairMask<T>(IList<T?> data) where T : INumber<T>
    {
        int n = data.Count;
        int n1 = n - 1;
        bool wasZero = false;
        var acc = new BooleanArray.Builder();
        for (var i = 0; i < data.Count; i++)
        {
            var v = data[i];
            if (v == null)
            {
                acc.Append(true);
            }
            else
            {
                if (v == T.Zero)
                {
                    if (wasZero || (i < n1 && data[i + 1] == T.Zero))
                    {
                        acc.Append(true);
                    }
                    else
                    {
                        acc.Append(false);
                    }
                    wasZero = true;
                }
                else
                {
                    acc.Append(false);
                    wasZero = false;
                }
            }
        }
        return acc.Build();
    }

    public static BooleanArray IsZeroPairMask<T>(PrimitiveArray<T> data) where T : struct, INumber<T>
    {
        int n = data.Length;
        int n1 = n - 1;
        bool wasZero = false;
        var acc = new BooleanArray.Builder();
        for (var i = 0; i < data.Length; i++)
        {
            var v = data.GetValue(i);
            if (v == null)
            {
                acc.Append(true);
            }
            else
            {
                if (v == T.Zero)
                {
                    if (wasZero || (i < n1 && data.GetValue(i + 1) == T.Zero))
                    {
                        acc.Append(true);
                    }
                    else
                    {
                        acc.Append(false);
                    }
                    wasZero = true;
                }
                else
                {
                    acc.Append(false);
                    wasZero = false;
                }
            }
        }
        return acc.Build();
    }
}

public static class NullInterpolation
{
    internal static ILogger? Logger;

    public const string NullInterpolateCURIE = "MS:1003901";
    public const string NullZeroCURIE = "MS:1003902";

    public static List<T> CollectDeltas<T>(IEnumerable<T?> values, bool sort = true) where T : struct, INumber<T>
    {
        List<T> deltas = new();
        T last = default;
        int i = 0;
        foreach (var value in values)
        {
            if (value == null)
            {
                continue;
            }
            if (i == 0)
            {
                last = (T)value;
                i++;
            }
            else
            {
                var delta = (T)value - last;
                if (delta < T.Zero) throw new Exception($"{delta} = {value} - {last}");
                deltas.Add(delta);
                last = (T)value;
            }
        }
        if (sort) deltas.Sort();
        return deltas;
    }

    public static T SortedMedian<T>(IReadOnlyList<T> values) where T : struct, INumber<T>
    {
        if (values.Count == 0)
        {
            return T.Zero;
        }
        else if (values.Count <= 2)
        {
            return values[0];
        }
        else
        {
            int mid = values.Count / 2;
            if (values.Count % 2 == 0)
            {
                return values[mid];
            }
            else
            {
                return (values[mid] + values[mid + 1]) / (T.One + T.One);
            }
        }
    }

    public static List<(int, int)> FindNullBounds(Array arrayValues)
    {
        List<(int, int)> bounds = new();
        if (arrayValues.Length == 0) return bounds;
        List<int> nullHere = new();
        for (int i = 0; i < arrayValues.Length; i++)
        {
            if (arrayValues.IsNull(i))
            {
                nullHere.Add(i);
            }
        }
        if (nullHere.Count == 0)
        {
            bounds.Add((0, arrayValues.Length));
            return bounds;
        }
        var startsWithNull = true;
        var endsWithNull = true;
        if (nullHere[0] != 0)
        {
            startsWithNull = false;
            List<int> tmp = [0, .. nullHere];
            nullHere = tmp;
        }
        if (nullHere.Last() != arrayValues.Length - 1)
        {
            endsWithNull = false;
            nullHere.Add(arrayValues.Length);
        }
        if (nullHere.Count % 2 != 0)
        {
            throw new InvalidDataException($"The {nullHere.Count} nulls in this data array are not properly paired. Start with null? {startsWithNull}. Ends with null? {endsWithNull}");
        }
        for (int i = 0; i < nullHere.Count; i += 2)
        {
            // Logger?.LogDebug($"null span {i}: {nullHere[i]}-{nullHere[i + 1]}");
            bounds.Add((nullHere[i], nullHere[i + 1]));
        }
        return bounds;
    }

    public static void FillNullsWithModel<T, TBuilder>(PrimitiveArray<T> arrayValues, SpacingInterpolationModel<T> model, IArrowArrayBuilder<T, PrimitiveArray<T>, TBuilder> builder) where T : struct, INumber<T> where TBuilder : IArrowArrayBuilder<PrimitiveArray<T>>
    {
        var nBefore = arrayValues.Length;
        var bounds = FindNullBounds(arrayValues);
        var nVisited = 0;
        foreach (var (startIdx, endIdx) in bounds)
        {
            var chunk = (PrimitiveArray<T>)arrayValues.Slice(startIdx, endIdx - startIdx + 1);
            nVisited += chunk.Length;
            var startSize = builder.Length;
            var n = chunk.Length;
            // NullCount can only be 0, 1, or 2
            var nHasReal = n - chunk.NullCount;

            if (nHasReal == 1)
            {
                if (n == 2)
                {
                    if (chunk.IsNull(0))
                    {
                        var vAt = chunk.GetValue(1);
                        if (vAt == null) throw new InvalidDataException("Cannot both be null");
                        var vFill = (T)vAt - model.Predict((T)vAt);
                        builder.Append(vFill);
                        builder.Append((T)vAt);
                    }
                    else
                    {
                        var vAt = chunk.GetValue(0);
                        if (vAt == null) throw new InvalidDataException("Cannot both be null");
                        var vFill = (T)vAt + model.Predict((T)vAt);
                        builder.Append((T)vAt);
                        builder.Append(vFill);
                    }
                }
                else if (n == 3)
                {
                    var vAt = chunk.GetValue(1);
                    if (vAt == null) throw new InvalidDataException("Cannot both be null");
                    var vFill = (T)vAt - model.Predict((T)vAt);
                    builder.Append(vFill);
                    builder.Append((T)vAt);
                    vFill = (T)vAt + model.Predict((T)vAt);
                    builder.Append(vFill);
                }
                else throw new InvalidOperationException("This is impossible");
            }
            else
            {
                var delta = LocalMedianDelta(chunk);
                if (chunk.IsNull(0))
                {
                    var vAt = chunk.GetValue(1);
                    if (vAt == null) throw new InvalidDataException("Cannot both be null");
                    var vFill = (T)vAt - delta;
                    builder.Append(vFill);
                }
                else
                {
                    var vAt = chunk.GetValue(0);
                    if (vAt == null) throw new InvalidOperationException("This should not happen");
                    builder.Append((T)vAt);
                }


                foreach (var v in (PrimitiveArray<T>)chunk.Slice(1, chunk.Length - 2))
                {
                    if (v == null) throw new InvalidDataException("Cannot both be null");
                    builder.Append((T)v);
                }
                if (chunk.IsNull(chunk.Length - 1))
                {
                    var vAt = chunk.GetValue(chunk.Length - 2);
                    if (vAt == null) throw new InvalidDataException("Cannot both be null");
                    builder.Append((T)vAt + delta);
                }
                else
                {
                    var vAt = chunk.GetValue(chunk.Length - 1);
                    if (vAt == null) throw new InvalidOperationException("This should not happen");
                    builder.Append((T)vAt);
                }
            }

            var endSize = builder.Length;
            if ((endSize - startSize) != chunk.Length) throw new InvalidOperationException(string.Format("chunk size {0} did not get fully copied", chunk.Length));
        }

        var nAfter = builder.Length;
        if (nBefore != nAfter) throw new InvalidOperationException(string.Format("Failed to preserve all data points during slicing {0} != {1}, {2}", nBefore, nAfter, nVisited));
    }

    public static T LocalMedianDelta<T>(IEnumerable<T?> arrayValues) where T : struct, INumber<T>
    {
        var deltas = CollectDeltas(arrayValues);
        if (deltas.Count == 0)
        {
            return T.Zero;
        }
        var median = SortedMedian(deltas);
        var deltasBelow = deltas.Where(v => v <= median).ToList();
        if (deltasBelow.Count == 0)
        {
            return median;
        }
        else
        {
            return SortedMedian(deltasBelow);
        }
    }
}

public static class NoCompressionCodec
{
    public const string CURIE = "MS:1000576";

    public static int Encode<T, TBuilder>(T startValue, IEnumerable<T?> values, IArrowArrayBuilder<T, PrimitiveArray<T>, TBuilder> accumulator)
        where T : struct, INumber<T> where TBuilder : IArrowArrayBuilder<PrimitiveArray<T>>
    {
        int nNulls = 0;
        foreach (var value in values)
        {
            if (value == null)
            {
                nNulls += 1;
                accumulator.AppendNull();
            }
            else
            {
                accumulator.Append((T)value);
            }
        }
        return nNulls;
    }

    public static int Decode<T, TBuilder>(T startValue, PrimitiveArray<T> values, IArrowArrayBuilder<T, PrimitiveArray<T>, TBuilder> accumulator)
        where T : struct, INumber<T> where TBuilder : IArrowArrayBuilder<PrimitiveArray<T>>
    {
        int nNulls = 0;
        accumulator.Append(startValue);
        foreach (var value in values)
        {
            if (value == null)
            {
                nNulls += 1;
                accumulator.AppendNull();
            }
            else
            {
                accumulator.Append((T)value);
            }
        }
        return nNulls;
    }
}

public static class DeltaCodec
{
    public const string CURIE = "MS:1003089";

    public static int Encode<T, TBuilder>(T? startValue, IEnumerable<T?> values, IArrowArrayBuilder<T, PrimitiveArray<T>, TBuilder> accumulator)
        where T : struct, INumber<T> where TBuilder : IArrowArrayBuilder<PrimitiveArray<T>>
    {
        int nNulls = 0;

        T? last = startValue;

        if (last == null)
        {
            nNulls += 1;
            accumulator.AppendNull();
        }
        foreach (var value in values)
        {
            if (value != null)
            {
                if (last != null)
                {
                    accumulator.Append((T)value - (T)last);
                }
                else
                {
                    accumulator.Append((T)value);
                }
                last = value;
            }
            else
            {
                accumulator.AppendNull();
                last = value;
                nNulls += 1;
            }
        }

        return nNulls;
    }

    public static int Decode<T, TBuilder>(T startValue, PrimitiveArray<T> values, IArrowArrayBuilder<T, PrimitiveArray<T>, TBuilder> accumulator)
        where T : struct, INumber<T> where TBuilder : IArrowArrayBuilder<PrimitiveArray<T>>
    {
        int nNulls = 0;

        T? last = startValue;
        if (values.Length > 0 && values.ElementAt(0) == null)
        {
            if (values.Length > 1 && values.ElementAt(1) == null)
            {
                accumulator.Append(startValue);
            }
            last = default;
        }
        else
        {
            accumulator.Append(startValue);
        }

        foreach (var value in values)
        {
            if (value != null)
            {
                if (last == null)
                {
                    last = value;
                    accumulator.Append((T)value);
                }
                else
                {
                    last = last + value;
                    accumulator.Append((T)last);
                }
            }
            else
            {
                nNulls += 1;
                last = value;
                accumulator.AppendNull();
            }
        }
        return nNulls;
    }
}


/// <summary>
/// Specifies how null values should be handled in aggregate computations.
/// </summary>
public enum NullHandling
{
    /// <summary>
    /// Skip null values when computing the result.
    /// Returns null only if the array is empty or all values are null.
    /// </summary>
    Skip,

    /// <summary>
    /// Propagate null: if any value in the array is null, return null.
    /// </summary>
    Propagate
}

public static class Chunking
{
    public static List<(int, int)> ChunkEvery<T>(PrimitiveArray<T> data, T width) where T : struct, INumber<T>
    {
        var chunks = new List<(int, int)>();
        T? start = null;
        var n = data.Length;
        var i = 0;
        while (i < n)
        {
            var v = data.GetValue(i);
            if (v != null)
            {
                start = v;
                break;
            }
            else
                i++;
        }
        if (start == null)
        {
            chunks.Add((0, n));
            return chunks;
        }
        var offset = 0;
        var threshold = (start ?? default) + width;
        i = 0;
        while (i < n)
        {
            var v = data.GetValue(i);
            if (v != null)
            {
                if (v > threshold)
                {
                    if ((i + 1) < n && data.IsNull(i + 1))
                    {
                        while ((i + 1) < n && data.IsNull(i + 1))
                            i++;
                    }
                    if (i - offset > 1)
                    {
                        chunks.Add((offset, i));
                        offset = i;
                    }
                    while (threshold < v)
                    {
                        threshold += width;
                    }
                }
            }
            else if (((i + 1) < n) && data.IsValid(i + 1))
            {
                i++;
                v = data.GetValue(i);
                if (v != null && v > threshold)
                {
                    i--;
                    chunks.Add((offset, i));
                    offset = i;
                    while (threshold < v)
                    {
                        threshold += width;
                    }
                }
            }
            i++;
        }
        if (offset != n)
            chunks.Add((offset, n));
        return chunks;
    }

    public static List<(int, int)> ChunkEvery(Array data, double width)
    {
        switch (data.Data.DataType.TypeId)
        {
            case ArrowTypeId.Double:
                return ChunkEvery((DoubleArray)data, width);
            case ArrowTypeId.Float:
                return ChunkEvery((FloatArray)data, width);
            case ArrowTypeId.Int32:
                return ChunkEvery((Int32Array)data, width);
            case ArrowTypeId.Int64:
                return ChunkEvery((Int64Array)data, width);
            default: throw new NotImplementedException($"{data.Data.DataType.Name} not supported");
        }
    }
}

public static class Compute
{
    public static ILogger? Logger = null;

    public static void PrettyPrintFormat(IArrowArray array, StreamWriter stream, int indent = 0, string indenter = "    ")
    {

        List<string> indenting = Enumerable.Repeat(indenter, indent).ToList();
        string indentString = string.Concat(indenting);

        stream.WriteLine($"{indentString}[ ({array.Length})");
        var pad = indentString + indenter;
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Float:
                {
                    var valArray = (FloatArray)array;
                    foreach (var v in valArray)
                    {
                        if (v == null)
                            stream.WriteLine($"{pad}null");
                        else
                            stream.WriteLine($"{pad}{v}");
                    }
                    break;
                }
            case ArrowTypeId.Double:
                {
                    var valArray = (DoubleArray)array;
                    foreach (var v in valArray)
                    {
                        if (v == null)
                            stream.WriteLine($"{pad}null");
                        else
                            stream.WriteLine($"{pad}{v}");
                    }
                    break;
                }
            case ArrowTypeId.Int32:
                {
                    var valArray = (Int32Array)array;
                    foreach (var v in valArray)
                    {
                        if (v == null)
                            stream.WriteLine($"{pad}null");
                        else
                            stream.WriteLine($"{pad}{v}");
                    }
                    break;
                }
            case ArrowTypeId.Int64:
                {
                    var valArray = (Int64Array)array;
                    foreach (var v in valArray)
                    {
                        if (v == null)
                            stream.WriteLine($"{pad}null");
                        else
                            stream.WriteLine($"{pad}{v}");
                    }
                    break;
                }
            case ArrowTypeId.Int16:
                {
                    var valArray = (Int16Array)array;
                    foreach (var v in valArray)
                    {
                        if (v == null)
                            stream.WriteLine($"{pad}null");
                        else
                            stream.WriteLine($"{pad}{v}");
                    }
                    break;
                }
            case ArrowTypeId.Int8:
                {
                    var valArray = (Int8Array)array;
                    foreach (var v in valArray)
                    {
                        if (v == null)
                            stream.WriteLine($"{pad}null");
                        else
                            stream.WriteLine($"{pad}{v}");
                    }
                    break;
                }
            case ArrowTypeId.UInt8:
                {
                    var valArray = (UInt8Array)array;
                    foreach (var v in valArray)
                    {
                        if (v == null)
                            stream.WriteLine($"{pad}null");
                        else
                            stream.WriteLine($"{pad}{v}");
                    }
                    break;
                }
            case ArrowTypeId.UInt16:
                {
                    var valArray = (UInt16Array)array;
                    foreach (var v in valArray)
                    {
                        if (v == null)
                            stream.WriteLine($"{pad}null");
                        else
                            stream.WriteLine($"{pad}{v}");
                    }
                    break;
                }
            case ArrowTypeId.UInt32:
                {
                    var valArray = (UInt32Array)array;
                    foreach (var v in valArray)
                    {
                        if (v == null)
                            stream.WriteLine($"{pad}null");
                        else
                            stream.WriteLine($"{pad}{v}");
                    }
                    break;
                }
            case ArrowTypeId.UInt64:
                {
                    var valArray = (UInt64Array)array;
                    foreach (var v in valArray)
                    {
                        if (v == null)
                            stream.WriteLine($"{pad}null");
                        else
                            stream.WriteLine($"{pad}{v}");
                    }
                    break;
                }
            case ArrowTypeId.Boolean:
                {
                    var valArray = (BooleanArray)array;

                    foreach (var v in valArray)
                    {
                        if (v == null)
                            stream.WriteLine($"{pad}null");
                        else
                            stream.WriteLine($"{pad}{v}");
                    }
                    break;
                }
            case ArrowTypeId.HalfFloat:
                {
                    var valArray = (HalfFloatArray)array;
                    foreach (var v in valArray)
                    {
                        if (v == null)
                            stream.WriteLine($"{pad}null");
                        else
                            stream.WriteLine($"{pad}{v}");
                    }
                    break;
                }
            case ArrowTypeId.List:
                {
                    var valArray = (ListArray)array;
                    for (var i = 0; i < valArray.Length; i++)
                    {
                        if (valArray.IsNull(i))
                        {
                            stream.WriteLine($"{pad}null");
                        }
                        else
                        {
                            var slc = valArray.GetSlicedValues(i);
                            PrettyPrintFormat(slc, stream, indent + 1, indenter);
                        }
                    }
                    break;
                }
            case ArrowTypeId.String:
                {
                    var valArray = (StringArray)array;
                    for (var i = 0; i < valArray.Length; i++)
                    {
                        if (valArray.IsNull(i))
                        {
                            stream.WriteLine($"{pad}null");
                        }
                        else
                        {
                            var slc = valArray.GetString(i);
                            stream.WriteLine($"{pad}\"{slc}\"");
                        }
                    }
                    break;
                }
            case ArrowTypeId.Struct:
                {
                    var dtype = (StructType)array.Data.DataType;
                    var valArray = (StructArray)array;
                    foreach (var (f, col) in dtype.Fields.Zip(valArray.Fields))
                    {
                        stream.WriteLine($"{indentString}{f.Name}: {f.DataType.Name}");
                        PrettyPrintFormat(col, stream, indent + 1, indenter);
                    }
                    break;
                }
            default: throw new NotImplementedException($"{array.Data.DataType.Name}");
        }
        stream.WriteLine($"{indentString}]");
    }

    public static string PrettyPrintFormat(IArrowArray array, int indent = 0, string indenter = "    ")
    {
        using (var bufferStream = new MemoryStream())
        {
            var writer = new StreamWriter(bufferStream);
            PrettyPrintFormat(array, writer, indent, indenter);
            writer.Flush();
            bufferStream.Seek(0, SeekOrigin.Begin);
            var reader = new StreamReader(bufferStream);
            var buff = reader.ReadToEnd();
            return buff;
        }
    }

    public static void PrettyPrint(IArrowArray array, int indent = 0, string indenter = "    ")
    {
        var text = PrettyPrintFormat(array, indent, indenter);
        Console.WriteLine(text);
    }

    static void NullToZero<T, TBuilder>(PrimitiveArray<T> array, IArrowArrayBuilder<T, PrimitiveArray<T>, TBuilder> accumulator)
        where T : struct, INumber<T> where TBuilder : IArrowArrayBuilder<PrimitiveArray<T>>
    {
        accumulator.Reserve(array.Length);
        foreach (var value in array)
        {
            accumulator.Append(value == null ? T.Zero : (T)value);
        }
    }

    public static BooleanArray Invert(BooleanArray mask, MemoryAllocator? allocator = null)
    {
        var builder = new BooleanArray.Builder();
        builder.Reserve(mask.Length);
        foreach (var val in mask)
        {
            if (val != null)
            {
                builder.Append(!(bool)val);
            }
            else
            {
                builder.AppendNull();
            }
        }
        return builder.Build(allocator);
    }

    public static BooleanArray And(BooleanArray lhs, BooleanArray rhs, MemoryAllocator? allocator = null)
    {
        if (lhs.Length != rhs.Length) throw new InvalidOperationException("Arrays must have the same length");
        var builder = new BooleanArray.Builder();
        builder.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetValue(i);
            var b = rhs.GetValue(i);
            if (a != null && b != null)
            {
                builder.Append((bool)a && (bool)b);
            }
            else
            {
                builder.AppendNull();
            }
        }
        return builder.Build(allocator);
    }

    public static BooleanArray Or(BooleanArray lhs, BooleanArray rhs, MemoryAllocator? allocator = null)
    {
        if (lhs.Length != rhs.Length) throw new InvalidOperationException("Arrays must have the same length");
        var builder = new BooleanArray.Builder();
        builder.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetValue(i);
            var b = rhs.GetValue(i);
            if (a != null && b != null)
            {
                builder.Append((bool)a || (bool)b);
            }
            else
            {
                builder.AppendNull();
            }
        }
        return builder.Build(allocator);
    }

    public static BooleanArray Xor(BooleanArray lhs, BooleanArray rhs, MemoryAllocator? allocator = null)
    {
        if (lhs.Length != rhs.Length) throw new InvalidOperationException("Arrays must have the same length");
        var builder = new BooleanArray.Builder();
        builder.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetValue(i);
            var b = rhs.GetValue(i);
            if (a != null && b != null)
            {
                builder.Append((bool)a ^ (bool)b);
            }
            else
            {
                builder.AppendNull();
            }
        }
        return builder.Build(allocator);
    }

    public static int BinarySearch<T>(PrimitiveArray<T> array, T? value) where T : struct, INumber<T>
    {
        var n = array.Length;
        var lo = 0;
        var hi = n - 1;
        var cmp = Comparer<T?>.Default.Compare;
        while (lo <= hi)
        {
            int i = lo + ((hi - lo) >> 1);
            int order = cmp(array.GetValue(i), value);

            if (order == 0)
                return i;
            if (order < 0)
            {
                lo = i + 1;
            }
            else
            {
                hi = i - 1;
            }
        }
        return -1;
    }

    /// <summary>
    /// Returns the minimum value in the array.
    /// </summary>
    /// <typeparam name="T">The numeric type of array elements.</typeparam>
    /// <param name="array">The input array.</param>
    /// <param name="nullHandling">How to handle null values.</param>
    /// <returns>The minimum value, or null if the array is empty, all values are null,
    /// or nullHandling is Propagate and any null exists.</returns>
    public static T? Min<T>(PrimitiveArray<T> array, NullHandling nullHandling = NullHandling.Skip)
        where T : struct, INumber<T>
    {
        if (array.Length == 0)
            return null;

        T? min = null;
        for (int i = 0; i < array.Length; i++)
        {
            var value = array.GetValue(i);
            if (value == null)
            {
                if (nullHandling == NullHandling.Propagate)
                    return null;
                continue;
            }

            if (min == null || (T)value < min)
                min = value;
        }
        return min;
    }

    /// <summary>
    /// Find the first value in the array that is not null
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="array">The array to search</param>
    /// <returns>The first non-null value and the index it was found at</returns>
    public static (T, int)? FirstNotNull<T>(PrimitiveArray<T> array) where T : struct, INumber<T>
    {
        for (var i = 0; i < array.Length; i++)
        {
            var v = array.GetValue(i);
            if (v != null)
            {
                return ((T)v, i);
            }
        }
        return null;
    }

    /// <summary>
    /// Find the last value in the array that is not null
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="array">The array to search</param>
    /// <returns>The last non-null value and the index it was found at</returns>
    public static (T, int)? LastNotNull<T>(PrimitiveArray<T> array) where T : struct, INumber<T>
    {
        for (var i = array.Length - 1; i >= 0; i--)
        {
            var v = array.GetValue(i);
            if (v != null)
            {
                return ((T)v, i);
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the maximum value in the array.
    /// </summary>
    /// <typeparam name="T">The numeric type of array elements.</typeparam>
    /// <param name="array">The input array.</param>
    /// <param name="nullHandling">How to handle null values.</param>
    /// <returns>The maximum value, or null if the array is empty, all values are null,
    /// or nullHandling is Propagate and any null exists.</returns>
    public static T? Max<T>(PrimitiveArray<T> array, NullHandling nullHandling = NullHandling.Skip)
        where T : struct, INumber<T>
    {
        if (array.Length == 0)
            return null;

        T? max = null;
        for (int i = 0; i < array.Length; i++)
        {
            var value = array.GetValue(i);
            if (value == null)
            {
                if (nullHandling == NullHandling.Propagate)
                    return null;
                continue;
            }

            if (max == null || (T)value > max)
                max = value;
        }
        return max;
    }

    /// <summary>
    /// Returns the index of the minimum value in the array (first occurrence).
    /// </summary>
    /// <typeparam name="T">The numeric type of array elements.</typeparam>
    /// <param name="array">The input array.</param>
    /// <param name="nullHandling">How to handle null values.</param>
    /// <returns>The index of the minimum value, or null if the array is empty, all values are null,
    /// or nullHandling is Propagate and any null exists.</returns>
    public static int? ArgMin<T>(PrimitiveArray<T> array, NullHandling nullHandling = NullHandling.Skip)
        where T : struct, INumber<T>
    {
        if (array.Length == 0)
            return null;

        T? min = null;
        int? minIndex = null;
        for (int i = 0; i < array.Length; i++)
        {
            var value = array.GetValue(i);
            if (value == null)
            {
                if (nullHandling == NullHandling.Propagate)
                    return null;
                continue;
            }

            if (min == null || (T)value < min)
            {
                min = value;
                minIndex = i;
            }
        }
        return minIndex;
    }

    /// <summary>
    /// Returns the index of the maximum value in the array (first occurrence).
    /// </summary>
    /// <typeparam name="T">The numeric type of array elements.</typeparam>
    /// <param name="array">The input array.</param>
    /// <param name="nullHandling">How to handle null values.</param>
    /// <returns>The index of the maximum value, or null if the array is empty, all values are null,
    /// or nullHandling is Propagate and any null exists.</returns>
    public static int? ArgMax<T>(PrimitiveArray<T> array, NullHandling nullHandling = NullHandling.Skip)
        where T : struct, INumber<T>
    {
        if (array.Length == 0)
            return null;

        T? max = null;
        int? maxIndex = null;
        for (int i = 0; i < array.Length; i++)
        {
            var value = array.GetValue(i);
            if (value == null)
            {
                if (nullHandling == NullHandling.Propagate)
                    return null;
                continue;
            }

            if (max == null || (T)value > max)
            {
                max = value;
                maxIndex = i;
            }
        }
        return maxIndex;
    }

    /// <summary>
    /// Returns the sum of all values in the array.
    /// </summary>
    /// <typeparam name="T">The numeric type of array elements.</typeparam>
    /// <param name="array">The input array.</param>
    /// <param name="nullHandling">How to handle null values.</param>
    /// <returns>The sum of values, or null if the array is empty, all values are null,
    /// or nullHandling is Propagate and any null exists.</returns>
    public static T? Sum<T>(PrimitiveArray<T> array, NullHandling nullHandling = NullHandling.Skip)
        where T : struct, INumber<T>
    {
        if (array.Length == 0)
            return null;

        T sum = T.Zero;
        bool hasValue = false;
        for (int i = 0; i < array.Length; i++)
        {
            var value = array.GetValue(i);
            if (value == null)
            {
                if (nullHandling == NullHandling.Propagate)
                    return null;
                continue;
            }

            sum += (T)value;
            hasValue = true;
        }
        return hasValue ? sum : null;
    }

    /// <summary>
    /// Returns the arithmetic mean of all values in the array.
    /// </summary>
    /// <typeparam name="T">The numeric type of array elements.</typeparam>
    /// <param name="array">The input array.</param>
    /// <param name="nullHandling">How to handle null values.</param>
    /// <returns>The mean as a double, or null if the array is empty, all values are null,
    /// or nullHandling is Propagate and any null exists.</returns>
    public static double? Mean<T>(PrimitiveArray<T> array, NullHandling nullHandling = NullHandling.Skip)
        where T : struct, INumber<T>
    {
        if (array.Length == 0)
            return null;

        T sum = T.Zero;
        long count = 0;
        for (int i = 0; i < array.Length; i++)
        {
            var value = array.GetValue(i);
            if (value == null)
            {
                if (nullHandling == NullHandling.Propagate)
                    return null;
                continue;
            }

            sum += (T)value;
            count++;
        }
        return count > 0 ? double.CreateChecked(sum) / count : null;
    }

    public static PrimitiveArray<T> NullifyAt<T>(PrimitiveArray<T> array, BooleanArray mask)
        where T : struct, INumber<T>
    {
        var nullCount = mask.Sum(v => (v != null && (bool)v) ? 1 : 0);
        return (PrimitiveArray<T>)ArrowArrayFactory.BuildArray(
            new ArrayData(array.Data.DataType, array.Length, nullCount, offset: array.Data.Offset, [mask.ValueBuffer.Clone(), array.ValueBuffer.Clone()], [])
        );
    }

    public static Array IndicesToMask(IList<int> indices, int n, MemoryAllocator? allocator = null)
    {
        BooleanArray.Builder acc = new();
        int j = 0;
        int m = indices.Count;
        int i = 0;
        for (i = 0; i < n && j < m; i++)
        {
            if (i < indices[j])
            {
                acc.Append(false);
            }
            else if (i == indices[j])
            {
                acc.Append(true);
                j += 1;
            }
            else if (i > indices[j])
            {
                var step = i - indices[j];
                acc.AppendRange(Enumerable.Repeat(false, step));
            }
        }
        while (i < n) acc.Append(false);
        if (acc.Length != n) throw new InvalidOperationException();
        return acc.Build(allocator);
    }

    public static List<(T, T)> IndicesToSpans<T>(IList<T> indices) where T : struct, INumber<T>
    {
        List<(T, T)> acc = new();
        T? start = null;
        T? last = null;
        foreach (var i in indices)
        {
            if (last == null)
            {
                start = i;
                last = i;
            }
            else
            {
                if (i - last == T.One)
                {
                    last = i;
                }
                else if (start != null)
                {
                    acc.Add(((T)start, (T)last));
                    start = i;
                    last = i;
                }
            }
        }
        if (start != null && last != null)
        {
            acc.Add(((T)start, indices.Last()));
        }
        return acc;
    }

    public static Array NullToZero<T>(PrimitiveArray<T> array, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Double:
                {
                    var builder = new DoubleArray.Builder();
                    NullToZero((DoubleArray)(IArrowArray)array, builder);
                    return builder.Build(allocator);
                }
            case ArrowTypeId.Float:
                {
                    var builder = new FloatArray.Builder();
                    NullToZero((FloatArray)(IArrowArray)array, builder);
                    return builder.Build(allocator);
                }
            case ArrowTypeId.Int32:
                {
                    var builder = new Int32Array.Builder();
                    NullToZero((Int32Array)(IArrowArray)array, builder);
                    return builder.Build(allocator);
                }
            case ArrowTypeId.Int64:
                {
                    var builder = new Int64Array.Builder();
                    NullToZero((Int64Array)(IArrowArray)array, builder);
                    return builder.Build(allocator);
                }
            case ArrowTypeId.UInt32:
                {
                    var builder = new UInt32Array.Builder();
                    NullToZero((UInt32Array)(IArrowArray)array, builder);
                    return builder.Build(allocator);
                }
            case ArrowTypeId.UInt64:
                {
                    var builder = new UInt64Array.Builder();
                    NullToZero((UInt64Array)(IArrowArray)array, builder);
                    return builder.Build(allocator);
                }
            case ArrowTypeId.Int16:
                {
                    var builder = new Int16Array.Builder();
                    NullToZero((Int16Array)(IArrowArray)array, builder);
                    return builder.Build(allocator);
                }
            case ArrowTypeId.Int8:
                {
                    var builder = new Int8Array.Builder();
                    NullToZero((Int8Array)(IArrowArray)array, builder);
                    return builder.Build(allocator);
                }
            case ArrowTypeId.UInt16:
                {
                    var builder = new UInt16Array.Builder();
                    NullToZero((UInt16Array)(IArrowArray)array, builder);
                    return builder.Build(allocator);
                }
            case ArrowTypeId.UInt8:
                {
                    var builder = new UInt8Array.Builder();
                    NullToZero((UInt8Array)(IArrowArray)array, builder);
                    return builder.Build(allocator);
                }
            default:
                throw new InvalidDataException("Unsupported data type " + array.Data.DataType.Name);
        }
    }

    public static Array NullToZero(IArrowArray array, MemoryAllocator? allocator = null)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Float:
                {
                    return Compute.NullToZero((FloatArray)array) ?? throw new InvalidDataException();
                }
            case ArrowTypeId.Double:
                {
                    return Compute.NullToZero((DoubleArray)array) ?? throw new InvalidDataException();
                }
            case ArrowTypeId.Int32:
                {
                    return Compute.NullToZero((Int32Array)array) ?? throw new InvalidDataException();
                }
            case ArrowTypeId.Int64:
                {
                    return Compute.NullToZero((Int64Array)array) ?? throw new InvalidDataException();
                }
            case ArrowTypeId.UInt32:
                {
                    return Compute.NullToZero((UInt32Array)array) ?? throw new InvalidDataException();
                }
            case ArrowTypeId.UInt64:
                {
                    return Compute.NullToZero((UInt64Array)array) ?? throw new InvalidDataException();
                }
            case ArrowTypeId.UInt8:
                {
                    return Compute.NullToZero((UInt8Array)array) ?? throw new InvalidDataException();
                }
            case ArrowTypeId.Int8:
                {
                    return Compute.NullToZero((Int8Array)array) ?? throw new InvalidDataException();
                }
            default:
                {
                    throw new InvalidOperationException(string.Format("Data type {0} not supported", array.Data.DataType.Name));
                }
        }
    }

    public static Int64Array CastInt64<T>(PrimitiveArray<T> array, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var builder = new Int64Array.Builder();
        builder.Reserve(array.Length);
        foreach (var val in array)
        {
            try
            {
                if (val != null && T.IsFinite(val.Value)) builder.Append(long.CreateChecked((T)val));
                else builder.AppendNull();
            }
            catch(OverflowException)
            {
                Logger?.LogWarning($"Overflowed {val} from type {array.Data.DataType.Name} converting to int64");
                builder.AppendNull();
            }
        }
        return builder.Build(allocator);
    }

    public static Int32Array CastInt32<T>(PrimitiveArray<T> array, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var builder = new Int32Array.Builder();
        builder.Reserve(array.Length);
        foreach (var val in array)
        {
            try
            {
                if (val != null && T.IsFinite(val.Value)) builder.Append(int.CreateChecked((T)val));
                else builder.AppendNull();
            }
            catch(OverflowException)
            {
                Logger?.LogWarning($"Overflowed {val} from type {array.Data.DataType.Name} converting to int32");
                builder.AppendNull();
            }
        }
        return builder.Build(allocator);
    }

    public static FloatArray CastFloat<T>(PrimitiveArray<T> array, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var builder = new FloatArray.Builder();
        builder.Reserve(array.Length);
        foreach (var val in array)
        {
            if (val != null) builder.Append(float.CreateChecked((T)val));
            else builder.AppendNull();
        }
        return builder.Build(allocator);
    }

    public static DoubleArray CastDouble<T>(PrimitiveArray<T> array, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var builder = new DoubleArray.Builder();
        builder.Reserve(array.Length);
        foreach (var val in array)
        {
            if (val != null) builder.Append(double.CreateChecked((T)val));
            else builder.AppendNull();
        }
        return builder.Build(allocator);
    }

    public static DoubleArray CastDouble<T>(IList<T> array, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var builder = new DoubleArray.Builder();
        builder.Reserve(array.Count);
        foreach (var val in array)
            builder.Append(double.CreateChecked(val));
        return builder.Build(allocator);
    }

    public static FloatArray CastFloat<T>(IList<T> array, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var builder = new FloatArray.Builder();
        builder.Reserve(array.Count);
        foreach (var val in array)
            builder.Append(float.CreateChecked(val));
        return builder.Build(allocator);
    }

    public static Int32Array CastInt32<T>(IList<T> array, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var builder = new Int32Array.Builder();
        builder.Reserve(array.Count);
        foreach (var val in array)
            try
            {
                builder.Append(int.CreateChecked(val));
            }
            catch(OverflowException)
            {
                if (T.IsFinite(val))
                {
                    throw;
                }
                else
                {
                    builder.AppendNull();
                }
            }
        return builder.Build(allocator);
    }

    public static Int64Array CastInt64<T>(IList<T> array, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var builder = new Int64Array.Builder();
        builder.Reserve(array.Count);
        foreach (var val in array)
            builder.Append(long.CreateChecked(val));
        return builder.Build(allocator);
    }

    public static Int64Array CastInt64(IArrowArray array, MemoryAllocator? allocator = null)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Double:
                return CastInt64((DoubleArray)array, allocator);
            case ArrowTypeId.Float:
                return CastInt64((FloatArray)array, allocator);
            case ArrowTypeId.Int32:
                return CastInt64((Int32Array)array, allocator);
            case ArrowTypeId.Int64:
                return (Int64Array)array;
            case ArrowTypeId.UInt32:
                return CastInt64((UInt32Array)array, allocator);
            case ArrowTypeId.UInt64:
                return CastInt64((UInt64Array)array, allocator);
            case ArrowTypeId.Int16:
                return CastInt64((Int16Array)array, allocator);
            case ArrowTypeId.Int8:
                return CastInt64((Int8Array)array, allocator);
            case ArrowTypeId.UInt16:
                return CastInt64((UInt16Array)array, allocator);
            case ArrowTypeId.UInt8:
                return CastInt64((UInt8Array)array, allocator);
            default:
                throw new InvalidDataException("Unsupported data type " + array.Data.DataType.Name);
        }
    }

    public static Int32Array CastInt32(IArrowArray array, MemoryAllocator? allocator = null)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Double:
                return CastInt32((DoubleArray)array, allocator);
            case ArrowTypeId.Float:
                return CastInt32((FloatArray)array, allocator);
            case ArrowTypeId.Int32:
                return (Int32Array)array;
            case ArrowTypeId.Int64:
                return CastInt32((Int64Array)array, allocator);
            case ArrowTypeId.UInt32:
                return CastInt32((UInt32Array)array, allocator);
            case ArrowTypeId.UInt64:
                return CastInt32((UInt64Array)array, allocator);
            case ArrowTypeId.Int16:
                return CastInt32((Int16Array)array, allocator);
            case ArrowTypeId.Int8:
                return CastInt32((Int8Array)array, allocator);
            case ArrowTypeId.UInt16:
                return CastInt32((UInt16Array)array, allocator);
            case ArrowTypeId.UInt8:
                return CastInt32((UInt8Array)array, allocator);
            default:
                throw new InvalidDataException("Unsupported data type " + array.Data.DataType.Name);
        }
    }

    public static FloatArray CastFloat(IArrowArray array, MemoryAllocator? allocator = null)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Double:
                return CastFloat((DoubleArray)array, allocator);
            case ArrowTypeId.Float:
                return (FloatArray)array;
            case ArrowTypeId.Int32:
                return CastFloat((Int32Array)array, allocator);
            case ArrowTypeId.Int64:
                return CastFloat((Int64Array)array, allocator);
            case ArrowTypeId.UInt32:
                return CastFloat((UInt32Array)array, allocator);
            case ArrowTypeId.UInt64:
                return CastFloat((UInt64Array)array, allocator);
            case ArrowTypeId.Int16:
                return CastFloat((Int16Array)array, allocator);
            case ArrowTypeId.Int8:
                return CastFloat((Int8Array)array, allocator);
            case ArrowTypeId.UInt16:
                return CastFloat((UInt16Array)array, allocator);
            case ArrowTypeId.UInt8:
                return CastFloat((UInt8Array)array, allocator);
            default:
                throw new InvalidDataException("Unsupported data type " + array.Data.DataType.Name);
        }
    }

    public static DoubleArray CastDouble(IArrowArray array, MemoryAllocator? allocator = null)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Double:
                return (DoubleArray)array;
            case ArrowTypeId.Float:
                return CastDouble((FloatArray)array, allocator);
            case ArrowTypeId.Int32:
                return CastDouble((Int32Array)array, allocator);
            case ArrowTypeId.Int64:
                return CastDouble((Int64Array)array, allocator);
            case ArrowTypeId.UInt32:
                return CastDouble((UInt32Array)array, allocator);
            case ArrowTypeId.UInt64:
                return CastDouble((UInt64Array)array, allocator);
            case ArrowTypeId.Int16:
                return CastDouble((Int16Array)array, allocator);
            case ArrowTypeId.Int8:
                return CastDouble((Int8Array)array, allocator);
            case ArrowTypeId.UInt16:
                return CastDouble((UInt16Array)array, allocator);
            case ArrowTypeId.UInt8:
                return CastDouble((UInt8Array)array, allocator);
            default:
                throw new InvalidDataException("Unsupported data type " + array.Data.DataType.Name);
        }
    }

    /// <summary>
    /// Returns the minimum value in the array as a double.
    /// </summary>
    /// <param name="array">The input array.</param>
    /// <param name="nullHandling">How to handle null values.</param>
    /// <returns>The minimum value as double, or null if the array is empty, all values are null,
    /// or nullHandling is Propagate and any null exists.</returns>
    public static double? Min(IArrowArray array, NullHandling nullHandling = NullHandling.Skip)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Double:
                return Min((DoubleArray)array, nullHandling);
            case ArrowTypeId.Float:
                {
                    var result = Min((FloatArray)array, nullHandling);
                    return result.HasValue ? (double)result.Value : null;
                }
            case ArrowTypeId.Int32:
                {
                    var result = Min((Int32Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.Int64:
                {
                    var result = Min((Int64Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.UInt32:
                {
                    var result = Min((UInt32Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.UInt64:
                {
                    var result = Min((UInt64Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.Int16:
                {
                    var result = Min((Int16Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.Int8:
                {
                    var result = Min((Int8Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.UInt16:
                {
                    var result = Min((UInt16Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.UInt8:
                {
                    var result = Min((UInt8Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            default:
                throw new InvalidDataException("Unsupported data type " + array.Data.DataType.Name);
        }
    }

    /// <summary>
    /// Returns the maximum value in the array as a double.
    /// </summary>
    /// <param name="array">The input array.</param>
    /// <param name="nullHandling">How to handle null values.</param>
    /// <returns>The maximum value as double, or null if the array is empty, all values are null,
    /// or nullHandling is Propagate and any null exists.</returns>
    public static double? Max(IArrowArray array, NullHandling nullHandling = NullHandling.Skip)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Double:
                return Max((DoubleArray)array, nullHandling);
            case ArrowTypeId.Float:
                {
                    var result = Max((FloatArray)array, nullHandling);
                    return result.HasValue ? (double)result.Value : null;
                }
            case ArrowTypeId.Int32:
                {
                    var result = Max((Int32Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.Int64:
                {
                    var result = Max((Int64Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.UInt32:
                {
                    var result = Max((UInt32Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.UInt64:
                {
                    var result = Max((UInt64Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.Int16:
                {
                    var result = Max((Int16Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.Int8:
                {
                    var result = Max((Int8Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.UInt16:
                {
                    var result = Max((UInt16Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.UInt8:
                {
                    var result = Max((UInt8Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            default:
                throw new InvalidDataException("Unsupported data type " + array.Data.DataType.Name);
        }
    }

    /// <summary>
    /// Returns the index of the minimum value in the array.
    /// </summary>
    /// <param name="array">The input array.</param>
    /// <param name="nullHandling">How to handle null values.</param>
    /// <returns>The index of the minimum value, or null if the array is empty, all values are null,
    /// or nullHandling is Propagate and any null exists.</returns>
    public static int? ArgMin(IArrowArray array, NullHandling nullHandling = NullHandling.Skip)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Double:
                return ArgMin((DoubleArray)array, nullHandling);
            case ArrowTypeId.Float:
                return ArgMin((FloatArray)array, nullHandling);
            case ArrowTypeId.Int32:
                return ArgMin((Int32Array)array, nullHandling);
            case ArrowTypeId.Int64:
                return ArgMin((Int64Array)array, nullHandling);
            case ArrowTypeId.UInt32:
                return ArgMin((UInt32Array)array, nullHandling);
            case ArrowTypeId.UInt64:
                return ArgMin((UInt64Array)array, nullHandling);
            case ArrowTypeId.Int16:
                return ArgMin((Int16Array)array, nullHandling);
            case ArrowTypeId.Int8:
                return ArgMin((Int8Array)array, nullHandling);
            case ArrowTypeId.UInt16:
                return ArgMin((UInt16Array)array, nullHandling);
            case ArrowTypeId.UInt8:
                return ArgMin((UInt8Array)array, nullHandling);
            default:
                throw new InvalidDataException("Unsupported data type " + array.Data.DataType.Name);
        }
    }

    /// <summary>
    /// Returns the index of the maximum value in the array.
    /// </summary>
    /// <param name="array">The input array.</param>
    /// <param name="nullHandling">How to handle null values.</param>
    /// <returns>The index of the maximum value, or null if the array is empty, all values are null,
    /// or nullHandling is Propagate and any null exists.</returns>
    public static int? ArgMax(IArrowArray array, NullHandling nullHandling = NullHandling.Skip)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Double:
                return ArgMax((DoubleArray)array, nullHandling);
            case ArrowTypeId.Float:
                return ArgMax((FloatArray)array, nullHandling);
            case ArrowTypeId.Int32:
                return ArgMax((Int32Array)array, nullHandling);
            case ArrowTypeId.Int64:
                return ArgMax((Int64Array)array, nullHandling);
            case ArrowTypeId.UInt32:
                return ArgMax((UInt32Array)array, nullHandling);
            case ArrowTypeId.UInt64:
                return ArgMax((UInt64Array)array, nullHandling);
            case ArrowTypeId.Int16:
                return ArgMax((Int16Array)array, nullHandling);
            case ArrowTypeId.Int8:
                return ArgMax((Int8Array)array, nullHandling);
            case ArrowTypeId.UInt16:
                return ArgMax((UInt16Array)array, nullHandling);
            case ArrowTypeId.UInt8:
                return ArgMax((UInt8Array)array, nullHandling);
            default:
                throw new InvalidDataException("Unsupported data type " + array.Data.DataType.Name);
        }
    }

    /// <summary>
    /// Returns the sum of all values in the array as a double.
    /// </summary>
    /// <param name="array">The input array.</param>
    /// <param name="nullHandling">How to handle null values.</param>
    /// <returns>The sum as double, or null if the array is empty, all values are null,
    /// or nullHandling is Propagate and any null exists.</returns>
    public static double? Sum(IArrowArray array, NullHandling nullHandling = NullHandling.Skip)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Double:
                return Sum((DoubleArray)array, nullHandling);
            case ArrowTypeId.Float:
                {
                    var result = Sum((FloatArray)array, nullHandling);
                    return result.HasValue ? (double)result.Value : null;
                }
            case ArrowTypeId.Int32:
                {
                    var result = Sum((Int32Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.Int64:
                {
                    var result = Sum((Int64Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.UInt32:
                {
                    var result = Sum((UInt32Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.UInt64:
                {
                    var result = Sum((UInt64Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.Int16:
                {
                    var result = Sum((Int16Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.Int8:
                {
                    var result = Sum((Int8Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.UInt16:
                {
                    var result = Sum((UInt16Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            case ArrowTypeId.UInt8:
                {
                    var result = Sum((UInt8Array)array, nullHandling);
                    return result.HasValue ? result.Value : null;
                }
            default:
                throw new InvalidDataException("Unsupported data type " + array.Data.DataType.Name);
        }
    }

    /// <summary>
    /// Returns the arithmetic mean of all values in the array.
    /// </summary>
    /// <param name="array">The input array.</param>
    /// <param name="nullHandling">How to handle null values.</param>
    /// <returns>The mean as a double, or null if the array is empty, all values are null,
    /// or nullHandling is Propagate and any null exists.</returns>
    public static double? Mean(IArrowArray array, NullHandling nullHandling = NullHandling.Skip)
    {
        switch (array.Data.DataType.TypeId)
        {
            case ArrowTypeId.Double:
                return Mean((DoubleArray)array, nullHandling);
            case ArrowTypeId.Float:
                return Mean((FloatArray)array, nullHandling);
            case ArrowTypeId.Int32:
                return Mean((Int32Array)array, nullHandling);
            case ArrowTypeId.Int64:
                return Mean((Int64Array)array, nullHandling);
            case ArrowTypeId.UInt32:
                return Mean((UInt32Array)array, nullHandling);
            case ArrowTypeId.UInt64:
                return Mean((UInt64Array)array, nullHandling);
            case ArrowTypeId.Int16:
                return Mean((Int16Array)array, nullHandling);
            case ArrowTypeId.Int8:
                return Mean((Int8Array)array, nullHandling);
            case ArrowTypeId.UInt16:
                return Mean((UInt16Array)array, nullHandling);
            case ArrowTypeId.UInt8:
                return Mean((UInt8Array)array, nullHandling);
            default:
                throw new InvalidDataException("Unsupported data type " + array.Data.DataType.Name);
        }
    }

    public static BooleanArray Equal<T>(PrimitiveArray<T> lhs, T rhs, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var cmp = new BooleanArray.Builder();
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetValue(i);
            var flag = a == rhs;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static BooleanArray Equal<T>(PrimitiveArray<T> lhs, PrimitiveArray<T> rhs, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var cmp = new BooleanArray.Builder();
        if (lhs.Length != rhs.Length) throw new InvalidOperationException("Arrays must have the same length");
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetValue(i);
            var b = rhs.GetValue(i);
            var flag = a == b;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static BooleanArray Equal(StringArray lhs, string rhs, MemoryAllocator? allocator = null)
    {
        var cmp = new BooleanArray.Builder();
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetString(i);
            var flag = a == rhs;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static BooleanArray Equal(StringArray lhs, StringArray rhs, MemoryAllocator? allocator = null)
    {
        var cmp = new BooleanArray.Builder();
        if (lhs.Length != rhs.Length) throw new InvalidOperationException("Arrays must have the same length");
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetString(i);
            var b = rhs.GetString(i);
            var flag = a == b;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static BooleanArray Equal(LargeStringArray lhs, string rhs, MemoryAllocator? allocator = null)
    {
        var cmp = new BooleanArray.Builder();
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetString(i);
            var flag = a == rhs;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static BooleanArray Equal(LargeStringArray lhs, LargeStringArray rhs, MemoryAllocator? allocator = null)
    {
        var cmp = new BooleanArray.Builder();
        if (lhs.Length != rhs.Length) throw new InvalidOperationException("Arrays must have the same length");
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetString(i);
            var b = rhs.GetString(i);
            var flag = a == b;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static BooleanArray Equal(IArrowArray lhs, string rhs, MemoryAllocator? allocator = null)
    {
        switch (lhs.Data.DataType.TypeId)
        {
            case ArrowTypeId.String:
                return Equal((StringArray)lhs, rhs, allocator);
            case ArrowTypeId.LargeString:
                return Equal((LargeStringArray)lhs, rhs, allocator);
            default:
                throw new InvalidDataException("Unsupported data type " + lhs.Data.DataType.Name);
        }
    }

    public static BooleanArray GreaterThan<T>(PrimitiveArray<T> lhs, T rhs, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var cmp = new BooleanArray.Builder();
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetValue(i);
            var flag = a > rhs;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static BooleanArray GreaterThan<T>(PrimitiveArray<T> lhs, PrimitiveArray<T> rhs, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var cmp = new BooleanArray.Builder();
        if (lhs.Length != rhs.Length) throw new InvalidOperationException("Arrays must have the same length");
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetValue(i);
            var b = rhs.GetValue(i);
            var flag = a > b;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static BooleanArray LessThan<T>(PrimitiveArray<T> lhs, T rhs, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var cmp = new BooleanArray.Builder();
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetValue(i);
            var flag = a < rhs;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static BooleanArray LessThan<T>(PrimitiveArray<T> lhs, PrimitiveArray<T> rhs, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var cmp = new BooleanArray.Builder();
        if (lhs.Length != rhs.Length) throw new InvalidOperationException("Arrays must have the same length");
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetValue(i);
            var b = rhs.GetValue(i);
            var flag = a < b;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static BooleanArray GreaterThanOrEqual<T>(PrimitiveArray<T> lhs, T rhs, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var cmp = new BooleanArray.Builder();
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetValue(i);
            var flag = a >= rhs;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static BooleanArray GreaterThanOrEqual<T>(PrimitiveArray<T> lhs, PrimitiveArray<T> rhs, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var cmp = new BooleanArray.Builder();
        if (lhs.Length != rhs.Length) throw new InvalidOperationException("Arrays must have the same length");
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetValue(i);
            var b = rhs.GetValue(i);
            var flag = a >= b;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static BooleanArray LessThanOrEqual<T>(PrimitiveArray<T> lhs, T rhs, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var cmp = new BooleanArray.Builder();
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetValue(i);
            var flag = a <= rhs;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static BooleanArray LessThanOrEqual<T>(PrimitiveArray<T> lhs, PrimitiveArray<T> rhs, MemoryAllocator? allocator = null) where T : struct, INumber<T>
    {
        var cmp = new BooleanArray.Builder();
        if (lhs.Length != rhs.Length) throw new InvalidOperationException("Arrays must have the same length");
        cmp.Reserve(lhs.Length);
        for (int i = 0; i < lhs.Length; i++)
        {
            var a = lhs.GetValue(i);
            var b = rhs.GetValue(i);
            var flag = a <= b;
            cmp.Append(flag);
        }
        return cmp.Build(allocator);
    }

    public static Array Filter(Array array, BooleanArray mask, MemoryAllocator? allocator = null)
    {
        if (array.Length != mask.Length) throw new InvalidOperationException("Array and mask must have the same length");
        List<(int, int)> spans = new();
        int? start = null;
        for (int i = 0; i < mask.Length; i++)
        {
            var v = mask.GetValue(i);
            if (v != null && (bool)v)
            {
                if (start != null) { }
                else start = i;
            }
            else if (v != null && !(bool)v)
            {
                if (start != null)
                {
                    // Slices in Take include the trailing index
                    spans.Add(((int)start, i - 1));
                    start = null;
                }
                else { }
            }
        }
        if (start != null)
        {
            spans.Add(((int)start, mask.Length - 1));
        }
        return Take(array, spans, allocator);
    }

    public static Array Take(Array array, IList<(int, int)> spans, MemoryAllocator? allocator = null)
    {
        if (spans.Count == 0)
        {
            return array.Slice(0, 0);
        }
        List<Array> chunks = new();
        foreach (var (start, end) in spans)
        {
            if (end < start || end < 0 || start < 0) throw new InvalidOperationException(string.Format("Invalid span: {0} {1}", start, end));
            chunks.Add(array.Slice(start, end - start + 1));
        }
        return (Array)ArrowArrayConcatenator.Concatenate(chunks, allocator);
    }

    public static Array Take(Array array, IList<int> indices, MemoryAllocator? allocator = null)
    {
        if (indices.Count == 0)
        {
            return array.Slice(0, 0);
        }
        List<Array> chunks = new();
        for (var i = 0; i < indices.Count; i++)
        {
            chunks.Add(array.Slice(indices[i], 1));
        }
        return (Array)ArrowArrayConcatenator.Concatenate(chunks, allocator);
    }

    public static List<Array> Take(List<Array> batch, IList<int> indices, MemoryAllocator? allocator = null)
    {
        return batch.Select(arr => Take(arr, indices, allocator)).ToList();
    }

    public static List<Array> Filter(List<Array> batch, BooleanArray mask, MemoryAllocator? allocator = null)
    {
        return batch.Select(arr => Filter(arr, mask, allocator)).ToList();
    }

    public static Dictionary<T, Array> Take<T>(Dictionary<T, Array> arrays, IList<int> indices, MemoryAllocator? allocator = null) where T : notnull
    {
        Dictionary<T, Array> result = new();
        foreach (var kv in arrays)
        {
            result[kv.Key] = Take(kv.Value, indices, allocator);
        }
        return result;
    }

    public static Dictionary<T, Array> Filter<T>(Dictionary<T, Array> arrays, BooleanArray mask, MemoryAllocator? allocator = null) where T : notnull
    {
        Dictionary<T, Array> result = new();
        foreach (var kv in arrays)
        {
            result[kv.Key] = Filter(kv.Value, mask, allocator);
        }
        return result;
    }

    public static RecordBatch Filter(RecordBatch batch, BooleanArray mask, MemoryAllocator? allocator = null)
    {
        if (batch.Length != mask.Length) throw new InvalidOperationException("Array and mask must have the same length");
        List<(int, int)> spans = new();
        int? start = null;
        for (int i = 0; i < mask.Length; i++)
        {
            var v = mask.GetValue(i);
            if (v != null && (bool)v)
            {
                if (start != null) { }
                else start = i;
            }
            else if (v != null && !(bool)v)
            {
                if (start != null)
                {
                    // Slices in Take include the trailing index
                    spans.Add(((int)start, i - 1));
                    start = null;
                }
                else { }
            }
        }
        if (start != null)
        {
            spans.Add(((int)start, mask.Length - 1));
        }
        return Take(batch, spans, allocator);
    }

    public static RecordBatch Take(RecordBatch batch, IList<(int, int)> spans, MemoryAllocator? allocator = null)
    {
        if (spans.Count == 0)
        {
            return batch.Slice(0, 0);
        }
        List<Array> columns = new();
        var size = 0;
        foreach (var col in batch.Arrays)
        {
            columns.Add(Take((Array)col, spans, allocator));
            size = columns.Last().Length;
        }
        return new RecordBatch(batch.Schema, columns, size);
    }

    public static RecordBatch Take(RecordBatch batch, IList<int> indices, MemoryAllocator? allocator = null)
    {
        var spans = IndicesToSpans(indices);
        return Take(batch, spans, allocator);
    }

}

public static class StructArrayExtensions
{
    public static RecordBatch AsRecordBatch(this StructArray array)
    {
        var dtype = (StructType)array.Data.DataType;
        var schema = new Schema(dtype.Fields, null);
        return new RecordBatch(schema, array.Fields, array.Length);
    }
}