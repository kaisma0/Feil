#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.IsolatedStorage;
using ProtoBuf;

namespace Feil.Core;

[ProtoContract]
sealed class AccountSettingsStore
{
    // Member 1 was a Dictionary<string, byte[]> for SentryData.

    [ProtoMember(2, IsRequired = false)]
    public ConcurrentDictionary<string, int> ContentServerPenalty { get; private set; }

    private string FileName;
    private static readonly object SyncRoot = new();

    AccountSettingsStore()
    {
        ContentServerPenalty = new ConcurrentDictionary<string, int>();
    }

    public static bool IsLoaded => Instance != null;

    public static AccountSettingsStore Instance;
    static readonly IsolatedStorageFile IsolatedStorage = IsolatedStorageFile.GetUserStoreForAssembly();

    public static void LoadFromFile(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);

        lock (SyncRoot)
        {
            if (IsLoaded)
                return;

            if (IsolatedStorage.FileExists(filename))
            {
                try
                {
                    using var fs = IsolatedStorage.OpenFile(filename, FileMode.Open, FileAccess.Read);
                    using var ds = new DeflateStream(fs, CompressionMode.Decompress);
                    Instance = Serializer.Deserialize<AccountSettingsStore>(ds);
                }
                catch (IOException ex)
                {
                    Logger.WriteLine("Failed to load account settings: {0}", ex.Message);
                    Instance = new AccountSettingsStore();
                }
                catch (InvalidDataException ex)
                {
                    Logger.WriteLine("Account settings file is invalid: {0}", ex.Message);
                    Instance = new AccountSettingsStore();
                }
                catch (ProtoException ex)
                {
                    Logger.WriteLine("Account settings deserialization failed: {0}", ex.Message);
                    Instance = new AccountSettingsStore();
                }
            }
            else
            {
                Instance = new AccountSettingsStore();
            }

            Instance.FileName = filename;
        }
    }

    public static void Save()
    {
        lock (SyncRoot)
        {
            if (!IsLoaded)
            {
                throw new InvalidOperationException("Account settings must be loaded before saving.");
            }

            try
            {
                using var fs = IsolatedStorage.OpenFile(Instance.FileName, FileMode.Create, FileAccess.Write);
                using var ds = new DeflateStream(fs, CompressionMode.Compress);
                Serializer.Serialize(ds, Instance);
            }
            catch (IOException ex)
            {
                Logger.WriteLine("Failed to save account settings: {0}", ex.Message);
            }
        }
    }
}
