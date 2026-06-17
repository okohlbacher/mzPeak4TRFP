using Apache.Arrow;

namespace MZPeak.Writer.Visitors;


public interface IArrowBuilder<T>
{
    public void AppendNull();

    public void Append(T value);

    public List<Field> ArrowType();

    public List<IArrowArray> Build();

    public void Clear();

    public int Length { get; }
}

