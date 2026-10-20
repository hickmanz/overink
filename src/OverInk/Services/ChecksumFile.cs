using System.IO;
using System.Security.Cryptography;

namespace OverInk.Services;

/// <summary>Matches a file against a GNU-style SHA256SUMS.txt line (<c>hash  filename</c>).</summary>
public static class ChecksumFile
{
    public static bool Matches(string sumsText, string fileName, string filePath)
    {
        foreach (var raw in sumsText.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length < 64)
            {
                continue;
            }

            var hash = line[..64];
            if (!hash.All(Uri.IsHexDigit))
            {
                continue;
            }

            var rest = line[64..].Trim();
            if (rest.StartsWith('*'))
            {
                rest = rest[1..];
            }

            if (!rest.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = File.OpenRead(filePath);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            return actual.Equals(hash, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
