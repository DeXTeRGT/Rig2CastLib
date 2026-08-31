using System.Globalization;
using System.Text;

namespace Rig2Cast.Adapters.Rigctld;

public sealed record RigctldRequest(
    string Command,
    IReadOnlyList<string> Arguments,
    bool Extended,
    char Separator);

public sealed record RigctldValue(string Label, string Value);

public sealed record RigctldResult(
    string Command,
    IReadOnlyList<RigctldValue> Values,
    int ErrorCode = RigctldError.Ok,
    bool CloseConnection = false);

public static class RigctldError
{
    public const int Ok = 0;
    public const int InvalidParameter = -1;
    public const int NotImplemented = -4;
    public const int Timeout = -5;
    public const int Io = -6;
    public const int Rejected = -9;
    public const int NotAvailable = -11;
}

public static class RigctldProtocol
{
    private static readonly Dictionary<string, string> Commands =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["F"] = "set_freq", ["f"] = "get_freq",
            ["M"] = "set_mode", ["m"] = "get_mode",
            ["V"] = "set_vfo", ["v"] = "get_vfo",
            ["S"] = "set_split_vfo", ["s"] = "get_split_vfo",
            ["T"] = "set_ptt", ["t"] = "get_ptt",
            ["q"] = "quit"
        };

    public static RigctldRequest Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string input = line.TrimEnd('\r', '\n');
        bool extended = false;
        char separator = '\n';
        if (input.Length > 0 && input[0] != '\\' && input[0] != '?' && input[0] != '_' &&
            !char.IsLetterOrDigit(input[0]) && !char.IsWhiteSpace(input[0]))
        {
            extended = true;
            separator = input[0] == '+' ? '\n' : input[0];
            input = input[1..];
        }

        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new FormatException("A rigctld command is required.");

        string token = parts[0];
        string command;
        if (token.StartsWith('\\'))
            command = token[1..].ToLowerInvariant();
        else if (token.Length == 1 && Commands.TryGetValue(token, out string? mapped))
            command = mapped;
        else
            command = token.ToLowerInvariant();

        return new RigctldRequest(command, parts.Skip(1).ToArray(), extended, separator);
    }

    public static string Format(RigctldRequest request, RigctldResult result)
    {
        char separator = request.Separator;
        var records = new List<string>();
        if (request.Extended)
        {
            string arguments = request.Arguments.Count == 0 ? string.Empty : $" {string.Join(' ', request.Arguments)}";
            records.Add($"{result.Command}:{arguments}");
        }

        if (result.ErrorCode == RigctldError.Ok)
        {
            foreach (RigctldValue value in result.Values)
                records.Add(request.Extended ? $"{value.Label}: {value.Value}" : value.Value);
        }

        if (request.Extended || result.ErrorCode != RigctldError.Ok || result.Values.Count == 0)
            records.Add($"RPRT {result.ErrorCode.ToString(CultureInfo.InvariantCulture)}");

        return string.Join(separator, records) + (separator == '\n' ? "\n" : separator + "\n");
    }
}
