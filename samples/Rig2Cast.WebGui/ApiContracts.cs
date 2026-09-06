namespace Rig2Cast.WebGui;

public sealed record LongValue(long Value);
public sealed record IntValue(int Value);
public sealed record BoolValue(bool Value);
public sealed record EnumValue(string Value);

public static class ApiClientIdentity
{
    public const string HeaderName = "X-Rig2Cast-Client";

    public static string Require(HttpContext context)
    {
        string value = context.Request.Headers[HeaderName].ToString();
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"The {HeaderName} header is required.");
    }
}
