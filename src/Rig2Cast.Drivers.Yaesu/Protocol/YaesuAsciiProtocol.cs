using System.Buffers;
using System.Text;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Drivers.Yaesu.Protocol;

public sealed class YaesuAsciiProtocol(IRadioTransport transport, TimeSpan? responseTimeout = null)
{
    private const int MaximumResponseLength = 512;
    private static readonly Encoding Ascii = Encoding.ASCII;
    private readonly TimeSpan _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(2);

    public async ValueTask SendAsync(string command, CancellationToken cancellationToken = default)
    {
        string framed = Frame(command);
        await transport.WriteAsync(Ascii.GetBytes(framed), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string> QueryAsync(
        string command,
        string expectedResponsePrefix,
        CancellationToken cancellationToken = default)
    {
        ValidatePrefix(expectedResponsePrefix);
        await SendAsync(command, cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_responseTimeout);
        byte[] rented = ArrayPool<byte>.Shared.Rent(MaximumResponseLength);
        try
        {
            int length = 0;
            while (length < MaximumResponseLength)
            {
                int count = await transport.ReadAsync(rented.AsMemory(length, 1), timeout.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new YaesuProtocolException("The radio closed the connection before completing its response.");
                }

                length += count;
                if (rented[length - 1] == (byte)';')
                {
                    string response = Ascii.GetString(rented, 0, length);
                    if (!response.StartsWith(expectedResponsePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new YaesuProtocolException(
                            $"Expected a '{expectedResponsePrefix}' response but received '{response}'.");
                    }

                    return response;
                }
            }

            throw new YaesuProtocolException($"A CAT response exceeded {MaximumResponseLength} bytes.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No complete CAT response was received within {_responseTimeout}.", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static string Frame(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        string value = command.EndsWith(';') ? command : $"{command};";
        foreach (char character in value)
        {
            if (character > 0x7f || char.IsControl(character))
            {
                throw new ArgumentException("Yaesu CAT commands must contain printable ASCII characters only.", nameof(command));
            }
        }

        if (value[..^1].Contains(';', StringComparison.Ordinal))
        {
            throw new ArgumentException("A CAT command may contain only its final semicolon terminator.", nameof(command));
        }

        return value.ToUpperInvariant();
    }

    private static void ValidatePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (prefix.Length != 2 || prefix.Any(character => !char.IsAsciiLetter(character)))
        {
            throw new ArgumentException("A Yaesu response prefix must contain exactly two ASCII letters.", nameof(prefix));
        }
    }
}
