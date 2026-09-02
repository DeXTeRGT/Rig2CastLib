using System.Globalization;

namespace Rig2Cast.Protocols.Declarative;

public sealed record NumericFieldDescriptor
{
    public NumericFieldDescriptor(string name, int width, int minimum, int maximum, int step = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimum, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, minimum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        int largestRepresentable = width >= 10 ? int.MaxValue : (int)Math.Pow(10, width) - 1;
        if (maximum > largestRepresentable)
            throw new ArgumentOutOfRangeException(
                nameof(maximum), $"Maximum {maximum} does not fit in an unsigned {width}-digit field.");
        if ((maximum - minimum) % step != 0)
            throw new ArgumentException("The numeric range must end on its declared step.", nameof(step));

        Name = name;
        Width = width;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
    }

    public string Name { get; }
    public int Width { get; }
    public int Minimum { get; }
    public int Maximum { get; }
    public int Step { get; }

    public bool TryParse(ReadOnlySpan<char> encoded, out int value)
    {
        value = default;
        if (encoded.Length != Width ||
            !int.TryParse(encoded, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return false;
        return value >= Minimum && value <= Maximum && (value - Minimum) % Step == 0;
    }
}
