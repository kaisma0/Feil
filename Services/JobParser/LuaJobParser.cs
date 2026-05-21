using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Feil.Models;
using Serilog;

namespace Feil.Services.JobParser;

public class LuaJobParser
{
    private static readonly HashSet<int> DepotBlacklist = [
        228981, 228982, 228983, 228984, 228985, 228986, 228987, 228988, 228989,
        229000, 229001, 229002, 229003, 229004, 229005, 229006, 229007, 229010,
        229011, 229012, 229020, 229030, 229031, 229032, 229033, 228990, 239142,
        798541, 798542, 798543, 1034630
    ];

    private static readonly Regex AddAppIdRegex = new(
        @"addappid\(\s*(\d+)(?:,\s*(\d+))?(?:,\s*""([^""]+)"")?\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex SetManifestIdRegex = new(
        @"setManifestid\(\s*(\d+),\s*""?([^"",\)]+)""?,\s*(\d+)\s*\)",
        RegexOptions.Compiled);

    public DownloadJob Parse(string[] lines)
    {
        Log.Debug("Parsing Lua job with {LineCount} lines", lines.Length);
        int mainAppId = 0;
        string? mainAppKey = null;
        var depots = new Dictionary<int, DepotInfo>();
        var entitlements = new List<int>();

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("--"))
                continue;

            var appMatch = AddAppIdRegex.Match(trimmedLine);
            if (appMatch.Success)
            {
                var id = int.Parse(appMatch.Groups[1].Value);
                if (DepotBlacklist.Contains(id))
                    continue;

                if (mainAppId == 0)
                {
                    mainAppId = id;
                    if (appMatch.Groups[3].Success)
                    {
                        mainAppKey = appMatch.Groups[3].Value;
                    }
                }

                if (appMatch.Groups[2].Success)
                {
                    var branch = int.Parse(appMatch.Groups[2].Value);
                    var key = appMatch.Groups[3].Success ? appMatch.Groups[3].Value : null;

                    depots[id] = new DepotInfo
                    {
                        AppId = id,
                        Branch = branch,
                        DecryptionKey = key
                    };
                }
                else if (id != mainAppId)
                {
                    entitlements.Add(id);
                }
                continue;
            }

            var manifestMatch = SetManifestIdRegex.Match(trimmedLine);
            if (manifestMatch.Success)
            {
                var depotId = int.Parse(manifestMatch.Groups[1].Value);
                var manifestId = manifestMatch.Groups[2].Value;
                var sizeBytes = long.Parse(manifestMatch.Groups[3].Value);

                if (depots.TryGetValue(depotId, out var depot))
                {
                    depot.ManifestId = manifestId;
                    depot.SizeBytes = sizeBytes;
                }
                continue;
            }
        }

        Log.Debug("Parsed main AppId {AppId} with {DepotCount} depots and {EntitlementCount} entitlements", mainAppId, depots.Count, entitlements.Count);
        return new DownloadJob
        {
            AppId = mainAppId,
            AppKey = mainAppKey,
            Depots = depots.Values.ToList(),
            Entitlements = entitlements
        };
    }

    public string[] FilterLines(string[] lines, HashSet<int> excludedDepots)
    {
        Log.Debug("Filtering Lua lines, excluding {DepotCount} depots", excludedDepots?.Count ?? 0);
        if (excludedDepots == null || excludedDepots.Count == 0)
            return lines;

        var result = new List<string>();

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("--"))
            {
                result.Add(line);
                continue;
            }

            var appMatch = AddAppIdRegex.Match(trimmedLine);
            if (appMatch.Success)
            {
                var id = int.Parse(appMatch.Groups[1].Value);
                if (excludedDepots.Contains(id))
                    continue;
            }

            var manifestMatch = SetManifestIdRegex.Match(trimmedLine);
            if (manifestMatch.Success)
            {
                var depotId = int.Parse(manifestMatch.Groups[1].Value);
                if (excludedDepots.Contains(depotId))
                    continue;
            }

            result.Add(line);
        }

        return result.ToArray();
    }
}
