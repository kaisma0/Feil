#nullable disable
using System;
using Serilog;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SteamKit2;

namespace Feil.Core;

static class Util
{
    // Validate a file against Steam3 Chunk data
    public static List<DepotManifest.ChunkData> ValidateSteam3FileChecksums(FileStream fs, DepotManifest.ChunkData[] chunkdata)
    {
        var neededChunks = new List<DepotManifest.ChunkData>();

        foreach (var data in chunkdata)
        {
            fs.Seek((long)data.Offset, SeekOrigin.Begin);

            var adler = AdlerHash(fs, (int)data.UncompressedLength);
            if (!adler.SequenceEqual(BitConverter.GetBytes(data.Checksum)))
            {
                neededChunks.Add(data);
            }
        }

        return neededChunks;
    }

    public static byte[] AdlerHash(Stream stream, int length)
    {
        uint a = 0, b = 0;
        for (var i = 0; i < length; i++)
        {
            var c = (uint)stream.ReadByte();

            a = (a + c) % 65521;
            b = (b + a) % 65521;
        }

        return BitConverter.GetBytes(a | (b << 16));
    }

    public static byte[] FileSHAHash(string filename)
    {
        using (var fs = File.Open(filename, FileMode.Open))
        using (var sha = SHA1.Create())
        {
            var output = sha.ComputeHash(fs);

            return output;
        }
    }

    public static DepotManifest LoadManifestFromFile(string directory, uint depotId, ulong manifestId, bool badHashWarning)
    {
        // Try loading Steam format manifest first.
        var filename = Path.Combine(directory, string.Format("{0}_{1}.manifest", depotId, manifestId));

        if (File.Exists(filename))
        {
            byte[] expectedChecksum;

            try
            {
                expectedChecksum = File.ReadAllBytes(filename + ".sha");
            }
            catch (IOException)
            {
                expectedChecksum = null;
            }

            var currentChecksum = FileSHAHash(filename);

            if (expectedChecksum == null || expectedChecksum.SequenceEqual(currentChecksum))
            {
                return DepotManifest.LoadFromFile(filename);
            }
            else if (badHashWarning)
            {
                Log.Warning("Manifest {ManifestId} on disk did not match the expected checksum.", manifestId);
            }
        }

        // Try converting legacy manifest format.
        filename = Path.Combine(directory, string.Format("{0}_{1}.bin", depotId, manifestId));

        if (File.Exists(filename))
        {
            byte[] expectedChecksum;

            try
            {
                expectedChecksum = File.ReadAllBytes(filename + ".sha");
            }
            catch (IOException)
            {
                expectedChecksum = null;
            }

            byte[] currentChecksum;
            var oldManifest = ProtoManifest.LoadFromFile(filename, out currentChecksum);

            if (oldManifest != null && (expectedChecksum == null || !expectedChecksum.SequenceEqual(currentChecksum)))
            {
                oldManifest = null;

                if (badHashWarning)
                {
                    Log.Warning("Manifest {ManifestId} on disk did not match the expected checksum.", manifestId);
                }
            }

            if (oldManifest != null)
            {
                return oldManifest.ConvertToSteamManifest(depotId);
            }
        }

        return null;
    }

    public static bool SaveManifestToFile(string directory, DepotManifest manifest)
    {
        try
        {
            var filename = Path.Combine(directory, string.Format("{0}_{1}.manifest", manifest.DepotID, manifest.ManifestGID));
            manifest.SaveToFile(filename);
            File.WriteAllBytes(filename + ".sha", FileSHAHash(filename));
            return true; // If serialization completes without throwing an exception, return true
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save manifest to {Directory}", directory);
            return false; // Return false if an error occurs
        }
    }

    public static string GetSteamOS()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        return "linux";
    }

    public static string GetSteamArch()
    {
        return RuntimeInformation.OSArchitecture == Architecture.X86 ? "32" : "64";
    }

    public static byte[] DecodeHexString(string hex)
    {
        if (hex == null)
            return null;

        var chars = hex.Length;
        var bytes = new byte[chars / 2];

        for (var i = 0; i < chars; i += 2)
            bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);

        return bytes;
    }

    /// <summary>
    /// Decrypts using AES/ECB/PKCS7
    /// </summary>
    public static byte[] SymmetricDecryptECB(byte[] input, byte[] key)
    {
        using var aes = Aes.Create();
        aes.BlockSize = 128;
        aes.KeySize = 256;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;

        using var aesTransform = aes.CreateDecryptor(key, null);
        var output = aesTransform.TransformFinalBlock(input, 0, input.Length);

        return output;
    }
}
