namespace Rig2Cast.WebGui;

using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;

public sealed record LongValue(long Value);
public sealed record IntValue(int Value);
public sealed record BoolValue(bool Value);
public sealed record EnumValue(string Value);
public sealed record WebConnectionResult(
    string RadioId,
    string Result,
    ClientRole Role,
    bool IsOwner,
    bool ReadOnly,
    string Message,
    RadioSnapshot Snapshot);
public sealed record PttLeaseStatus(DateTimeOffset ExpiresAt);

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
