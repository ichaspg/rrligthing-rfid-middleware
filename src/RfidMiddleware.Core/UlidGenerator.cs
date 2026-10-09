using System.Security.Cryptography;

namespace RfidMiddleware.Core;

public static class UlidGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewUlid(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];
        var milliseconds = timestamp.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(milliseconds >> 40);
        bytes[1] = (byte)(milliseconds >> 32);
        bytes[2] = (byte)(milliseconds >> 24);
        bytes[3] = (byte)(milliseconds >> 16);
        bytes[4] = (byte)(milliseconds >> 8);
        bytes[5] = (byte)milliseconds;
        RandomNumberGenerator.Fill(bytes[6..]);

        Span<char> chars = stackalloc char[26];
        var buffer = new byte[17];
        bytes.CopyTo(buffer.AsSpan(1));
        for (var i = 25; i >= 0; i--)
        {
            var remainder = 0;
            for (var j = 0; j < buffer.Length; j++)
            {
                var value = (remainder << 8) | buffer[j];
                buffer[j] = (byte)(value / 32);
                remainder = value % 32;
            }
            chars[i] = Alphabet[remainder];
        }
        return new string(chars);
    }
}
