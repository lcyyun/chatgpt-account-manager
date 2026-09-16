using System.Security.Cryptography;

namespace GptPlusManager.Core.Security;

public readonly record struct TotpCode(string Code, int RemainingSeconds);

public sealed class TotpService
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string NormalizeBase32(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = new System.Text.StringBuilder(input.Length);
        foreach (var raw in input)
        {
            var value = char.ToUpperInvariant(raw);
            if ((value is >= 'A' and <= 'Z') || (value is >= '2' and <= '7'))
            {
                normalized.Append(value);
            }
            else if (!char.IsWhiteSpace(value) && value is not '-' and not '=')
            {
                throw new FormatException($"Invalid Base32 character: '{raw}'.");
            }
        }

        if (normalized.Length == 0)
        {
            throw new FormatException("Base32 secret is empty.");
        }

        return normalized.ToString();
    }

    public byte[] DecodeBase32(string input)
    {
        var normalized = NormalizeBase32(input);
        var output = new List<byte>((normalized.Length * 5) / 8);
        var bitBuffer = 0;
        var bitsInBuffer = 0;
        foreach (var value in normalized)
        {
            bitBuffer = (bitBuffer << 5) | Alphabet.IndexOf(value);
            bitsInBuffer += 5;
            if (bitsInBuffer >= 8)
            {
                output.Add((byte)(bitBuffer >> (bitsInBuffer - 8)));
                bitsInBuffer -= 8;
                bitBuffer &= (1 << bitsInBuffer) - 1;
            }
        }

        return output.ToArray();
    }

    public string GenerateCode(
        string secret,
        long unixSeconds,
        int digits = 6,
        int periodSeconds = 30)
    {
        if (digits is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(digits));
        }

        if (periodSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(periodSeconds));
        }

        var key = DecodeBase32(secret);
        var counter = unixSeconds / periodSeconds;
        Span<byte> message = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, message, hash);
        var offset = hash[^1] & 0x0f;
        var binaryCode = ((hash[offset] & 0x7f) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        var modulus = (int)Math.Pow(10, digits);
        return (binaryCode % modulus).ToString($"D{digits}");
    }

    public TotpCode GetCurrentCode(
        string secret,
        DateTimeOffset? now = null,
        int digits = 6,
        int periodSeconds = 30)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var seconds = timestamp.ToUnixTimeSeconds();
        var remainder = (int)(seconds % periodSeconds);
        var remaining = remainder == 0 ? periodSeconds : periodSeconds - remainder;
        return new TotpCode(GenerateCode(secret, seconds, digits, periodSeconds), remaining);
    }

    public bool IsValidSecret(string secret)
    {
        try
        {
            return DecodeBase32(secret).Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
