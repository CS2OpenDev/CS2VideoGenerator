#region

using System.Security.Cryptography;
using System.Text;

#endregion

namespace Cs2VideoGenerator.Core.Services;

internal static class HashCalculator
{
    public static string HashFileContent(string filePath)
    {
        using SHA256 sha256 = SHA256.Create();
        // FileShare.ReadWrite: another process (Steam, antivirus, ...) holding a read or even
        // write handle must not cause hashing to fail with a spurious IOException.
        using FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] hash = sha256.ComputeHash(stream);
        return Convert.ToBase64String(hash);
    }

    public static string HashDirectory(string directoryPath)
    {
        List<string> files = Directory
            .GetFiles(directoryPath, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        using SHA256 sha256 = SHA256.Create();
        foreach (string filePath in files)
        {
            string relativePath = Path.GetRelativePath(directoryPath, filePath);
            byte[] pathBytes = Encoding.UTF8.GetBytes(relativePath);
            sha256.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            // FileShare.ReadWrite: see HashFileContent — concurrent readers/writers must not break hashing.
            using FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int bytesRead;
            byte[] buffer = new byte[8192];
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            }
        }

        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToBase64String(sha256.Hash ?? throw new Exception("Failed to compute directory hash."));
    }
}
