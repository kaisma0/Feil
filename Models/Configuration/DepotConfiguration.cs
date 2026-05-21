#nullable disable
using System;
using Serilog;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ProtoBuf;

namespace Feil.Core;

[ProtoContract]
public class DepotConfigStore
{
    [ProtoMember(1)]
    public Dictionary<uint, ulong> InstalledManifestIDs { get; private set; } = new Dictionary<uint, ulong>();

    private string FileName;
    private static readonly object SyncRoot = new();

    private static DepotConfigStore _instance;
    public static DepotConfigStore Instance => _instance ?? throw new InvalidOperationException("Depot configuration has not been loaded.");
    public static bool Loaded => _instance is not null;

    public static void Reset()
    {
        lock (SyncRoot)
        {
            _instance = null;
        }
    }

    public static void LoadFromFile(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);

        lock (SyncRoot)
        {
            if (Loaded && string.Equals(_instance!.FileName, filename, StringComparison.OrdinalIgnoreCase))
                return;

            _instance = null;

            if (File.Exists(filename))
            {
                try
                {
                    using var fs = File.Open(filename, FileMode.Open);
                    using var ds = new DeflateStream(fs, CompressionMode.Decompress);
                    _instance = Serializer.Deserialize<DepotConfigStore>(ds);
                }
                catch (IOException ex)
                {
                    Log.Error("Failed to load depot configuration: {Error}", ex.Message);
                    _instance = new DepotConfigStore();
                }
                catch (InvalidDataException ex)
                {
                    Log.Error("Depot configuration file is invalid: {Error}", ex.Message);
                    _instance = new DepotConfigStore();
                }
                catch (ProtoException ex)
                {
                    Log.Error("Depot configuration deserialization failed: {Error}", ex.Message);
                    _instance = new DepotConfigStore();
                }
            }
            else
            {
                _instance = new DepotConfigStore();
            }

            _instance.FileName = filename;
        }
    }

    public static void Save()
    {
        lock (SyncRoot)
        {
            if (!Loaded)
            {
                throw new InvalidOperationException("Depot configuration must be loaded before saving.");
            }

            try
            {
                using var fs = File.Open(_instance.FileName, FileMode.Create);
                using var ds = new DeflateStream(fs, CompressionMode.Compress);
                Serializer.Serialize(ds, _instance);
            }
            catch (IOException ex)
            {
                Log.Error("Failed to save depot configuration: {Error}", ex.Message);
            }
        }
    }
}

public static class DepotKeyStore
{
    private static readonly Dictionary<uint, byte[]> depotKeysCache = new Dictionary<uint, byte[]>();

    public static void AddAll(IEnumerable<string> values)
    {
        foreach (string value in values)
        {
            string[] split = value.Split(';');

            if (split.Length != 2)
            {
                throw new FormatException($"Invalid depot key line: {value}");
            }

            depotKeysCache[uint.Parse(split[0])] = StringToByteArray(split[1]);
        }
    }

    public static void Add(uint depotId, byte[] key)
    {
        depotKeysCache[depotId] = key;
    }

    private static byte[] StringToByteArray(string hex)
    {
        return Enumerable.Range(0, hex.Length)
            .Where(x => x % 2 == 0)
            .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
            .ToArray();
    }

    public static bool ContainsKey(uint depotId)
    {
        return depotKeysCache.ContainsKey(depotId);
    }

    public static byte[] Get(uint depotId)
    {
        return depotKeysCache[depotId];
    }
}
