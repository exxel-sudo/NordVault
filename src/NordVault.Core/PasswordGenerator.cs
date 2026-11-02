using System.Security.Cryptography;

namespace NordVault.Core;

public static class PasswordGenerator
{
    // Exactly 64 distinct ASCII symbols: each independently sampled symbol has 6 bits.
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!#$%+-_";
    public const int DefaultLength = 24;

    public static char[] Generate(int length = DefaultLength)
    {
        if (length is < 16 or > 128) throw new ArgumentOutOfRangeException(nameof(length));
        var output = new char[length];
        for (var i = 0; i < output.Length; i++) output[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return output;
    }
}
