using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Feil.Services.SLSsteam;

public class SLSsteamService
{
    private const int Indent = 2;

    private readonly string _configFilePath;

    public SLSsteamService(string? customConfigPath = null)
    {
        _configFilePath = !string.IsNullOrEmpty(customConfigPath)
            ? customConfigPath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "SLSsteam", "config.yaml");
    }

    public bool IsInstalled() =>
        OperatingSystem.IsLinux() && File.Exists(_configFilePath);

    public string? GetConfigValue(string[] path)
    {
        if (!IsInstalled() || path is not { Length: > 0 }) return null;
        try
        {
            var editor = new YamlLineEditor(File.ReadAllLines(_configFilePath).ToList(), Indent);
            return editor.GetValue(path);
        }
        catch { return null; }
    }

    // Edits the SLSsteam config in-place while preserving existing comments and layout.
    public bool ModifyConfig(string[] path, string action, object? value, string? comment = null)
    {
        if (!IsInstalled() || path is not { Length: > 0 }) return false;
        try
        {
            var lines  = File.ReadAllLines(_configFilePath).ToList();
            var editor = new YamlLineEditor(lines, Indent);

            bool ok = action.ToLowerInvariant() switch
            {
                "set"    => editor.Set(path, value),
                "add"    => editor.Add(path, value, comment),
                "remove" => editor.Remove(path, value),
                _        => false
            };

            if (ok && editor.Modified)
                File.WriteAllLines(_configFilePath, lines);

            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SLSsteamService] Failed to modify config: {ex.Message}");
            return false;
        }
    }
}

// Small line-based editor for the predictable YAML shape used by SLSsteam.
internal sealed class YamlLineEditor
{
    private readonly List<string> _lines;
    private readonly int _indent;

    public bool Modified { get; private set; }

    public YamlLineEditor(List<string> lines, int indent)
    {
        _lines = lines;
        _indent = indent;
    }

    public string? GetValue(string[] path)
    {
        if (!TryFindPath(path, out int keyLine, out _)) return null;

        int colon = _lines[keyLine].IndexOf(':');
        if (colon < 0) return null;

        string raw = StripInlineComment(_lines[keyLine][(colon + 1)..]);
        if (raw.Length == 0) return null;

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            return UnquoteDoubleQuoted(raw);

        return raw;
    }

    public bool Set(string[] path, object? value)
    {
        if (!TryFindPath(path, out int keyLine, out _) &&
            !TryCreatePath(path, out keyLine))
        {
            return false;
        }

        int colon = _lines[keyLine].IndexOf(':');
        if (colon < 0) return false;

        string formatted = FormatScalar(value);
        string suffix    = formatted.Length > 0 ? $" {formatted}" : "";
        string newLine   = _lines[keyLine][..(colon + 1)] + suffix;
        if (_lines[keyLine] != newLine)
        {
            _lines[keyLine] = newLine;
            Modified = true;
        }
        return true;
    }

    public bool Add(string[] path, object? value, string? comment = null)
    {
        int searchStart  = 0;
        int searchEnd    = _lines.Count;
        int parentIndent = -1;

        for (int seg = 0; seg < path.Length; seg++)
        {
            int expectedIndent = parentIndent < 0
                ? 0
                : DetectChildIndent(searchStart, searchEnd, parentIndent);

            int keyLine = FindKey(path[seg], expectedIndent, searchStart, searchEnd);

            if (keyLine < 0)
            {
                if (seg < path.Length - 1) return false;

                string pad   = new(' ', expectedIndent);
                int insertAt = FindInsertPoint(searchStart, searchEnd, parentIndent);
                _lines.Insert(insertAt, $"{pad}{path[seg]}:");
                keyLine   = insertAt;
                searchEnd++;
            }

            parentIndent = GetIndent(_lines[keyLine]);
            int blockEnd = FindBlockEnd(keyLine + 1, parentIndent, searchEnd);

            if (seg == path.Length - 1)
                return AppendValue(keyLine, blockEnd, parentIndent, value, comment);

            searchStart = keyLine + 1;
            searchEnd   = blockEnd;
        }

        return false;
    }

    public bool Remove(string[] path, object? value)
    {
        if (!TryFindPath(path, out int keyLine, out int blockEnd)) return false;

        int nodeIndent = GetIndent(_lines[keyLine]);

        if (value is null)
        {
            // Leave trailing blank/comment lines with the next sibling.
            int contentEnd = FindInsertPoint(keyLine + 1, blockEnd, nodeIndent);
            _lines.RemoveRange(keyLine, contentEnd - keyLine);
            Modified = true;
            return true;
        }

        string strVal      = value.ToString()!;
        int    childIndent = DetectChildIndent(keyLine + 1, blockEnd, nodeIndent);

        for (int i = keyLine + 1; i < blockEnd; i++)
        {
            if (IsSkippable(_lines[i])) continue;

            int lineIndent = GetIndent(_lines[i]);
            if (lineIndent < childIndent) break;
            if (lineIndent > childIndent) continue;

            string trimmed = _lines[i].TrimStart();

            if (trimmed.StartsWith("- ") && StripInlineComment(trimmed[2..]) == strVal)
            {
                _lines.RemoveAt(i);
                Modified = true;
                return true;
            }

            string mappingPrefix = strVal + ":";
            if (trimmed == mappingPrefix ||
                (trimmed.StartsWith(mappingPrefix) && trimmed[mappingPrefix.Length] == ' '))
            {
                int subBlockEnd = FindBlockEnd(i + 1, lineIndent, blockEnd);
                int contentEnd  = FindInsertPoint(i + 1, subBlockEnd, lineIndent);
                _lines.RemoveRange(i, contentEnd - i);
                Modified = true;
                return true;
            }
        }

        return false;
    }

    private bool AppendValue(int keyLine, int blockEnd, int parentIndent, object? value, string? comment)
    {
        string childPad      = new(' ', parentIndent + _indent);
        int    childIndent   = parentIndent + _indent;
        int    insertAt      = FindInsertPoint(keyLine + 1, blockEnd, parentIndent);
        string cleanComment  = NormalizeInlineComment(comment);
        string commentSuffix = cleanComment.Length > 0 ? $" # {cleanComment}" : "";

        if (value is KeyValuePair<string, string> kvp)
        {
            if (HasMappingKey(keyLine + 1, blockEnd, childIndent, kvp.Key))
                return true;

            _lines.Insert(insertAt, $"{childPad}{kvp.Key}: {kvp.Value}{commentSuffix}");
        }
        else
        {
            string strVal = value?.ToString() ?? "";

            if (HasSequenceItem(keyLine + 1, blockEnd, childIndent, strVal))
                return true;

            _lines.Insert(insertAt, $"{childPad}- {strVal}{commentSuffix}");
        }

        Modified = true;
        return true;
    }

    private bool HasMappingKey(int start, int end, int exactIndent, string key)
    {
        string prefix = new string(' ', exactIndent) + key + ":";
        for (int i = start; i < end; i++)
        {
            if (IsSkippable(_lines[i])) continue;
            int ind = GetIndent(_lines[i]);
            if (ind < exactIndent) break;
            if (ind > exactIndent) continue;
            var line = _lines[i];
            if (line.StartsWith(prefix) &&
                (line.Length == prefix.Length || line[prefix.Length] == ' '))
                return true;
        }
        return false;
    }

    private bool HasSequenceItem(int start, int end, int exactIndent, string value)
    {
        for (int i = start; i < end; i++)
        {
            if (IsSkippable(_lines[i])) continue;
            int ind = GetIndent(_lines[i]);
            if (ind < exactIndent) break;
            if (ind > exactIndent) continue;
            string trimmed = _lines[i].TrimStart();
            if (trimmed.StartsWith("- ") && StripInlineComment(trimmed[2..]) == value)
                return true;
        }
        return false;
    }

    private bool TryFindPath(string[] path, out int keyLine, out int blockEnd)
    {
        keyLine  = -1;
        blockEnd = _lines.Count;

        int searchStart  = 0;
        int searchEnd    = _lines.Count;
        int parentIndent = -1;

        foreach (string segment in path)
        {
            int expectedIndent = parentIndent < 0
                ? 0
                : DetectChildIndent(searchStart, searchEnd, parentIndent);

            keyLine = FindKey(segment, expectedIndent, searchStart, searchEnd);
            if (keyLine < 0) return false;

            parentIndent = GetIndent(_lines[keyLine]);
            blockEnd     = FindBlockEnd(keyLine + 1, parentIndent, searchEnd);

            searchStart = keyLine + 1;
            searchEnd   = blockEnd;
        }

        return keyLine >= 0;
    }

    // Recreates missing scalar paths. Does not create sequence items or mapping keys, maybe some day if needed.
    private bool TryCreatePath(string[] path, out int keyLine)
    {
        keyLine = -1;

        int searchStart  = 0;
        int searchEnd    = _lines.Count;
        int parentIndent = -1;

        for (int seg = 0; seg < path.Length; seg++)
        {
            int expectedIndent = parentIndent < 0
                ? 0
                : DetectChildIndent(searchStart, searchEnd, parentIndent);

            keyLine = FindKey(path[seg], expectedIndent, searchStart, searchEnd);

            if (keyLine < 0)
            {
                string pad   = new(' ', expectedIndent);
                int insertAt = FindInsertPoint(searchStart, searchEnd, parentIndent);
                _lines.Insert(insertAt, $"{pad}{path[seg]}:");
                Modified = true;

                keyLine = insertAt;
                searchEnd++;
            }

            parentIndent = GetIndent(_lines[keyLine]);
            int blockEnd = FindBlockEnd(keyLine + 1, parentIndent, searchEnd);

            searchStart = keyLine + 1;
            searchEnd   = blockEnd;
        }

        return keyLine >= 0;
    }

    private int FindKey(string key, int exactIndent, int startLine, int endLine)
    {
        string prefix = new string(' ', exactIndent) + key + ":";
        for (int i = startLine; i < endLine; i++)
        {
            if (IsSkippable(_lines[i])) continue;
            int ind = GetIndent(_lines[i]);
            if (ind < exactIndent) break;
            if (ind > exactIndent) continue;
            var line = _lines[i];
            if (line.StartsWith(prefix) &&
                (line.Length == prefix.Length || line[prefix.Length] is ' ' or '\r' or '\n'))
                return i;
        }
        return -1;
    }

    private int FindBlockEnd(int startLine, int keyIndent, int maxLine)
    {
        for (int i = startLine; i < maxLine; i++)
        {
            if (IsSkippable(_lines[i])) continue;
            if (GetIndent(_lines[i]) <= keyIndent) return i;
        }
        return maxLine;
    }

    private int FindInsertPoint(int startLine, int blockEnd, int keyIndent)
    {
        int threshold = Math.Max(keyIndent, -1);
        int lastContent = -1;
        for (int i = startLine; i < blockEnd; i++)
        {
            if (!IsSkippable(_lines[i]) && GetIndent(_lines[i]) > threshold)
                lastContent = i;
        }
        return lastContent < 0 ? startLine : lastContent + 1;
    }

    private int DetectChildIndent(int startLine, int endLine, int parentIndent)
    {
        for (int i = startLine; i < endLine; i++)
        {
            if (IsSkippable(_lines[i])) continue;
            int ind = GetIndent(_lines[i]);
            if (ind > parentIndent) return ind;
            break;
        }
        return parentIndent + _indent;
    }

    // Strips inline comments while ignoring # inside double-quoted values.
    private static string StripInlineComment(string value)
    {
        bool inDoubleQuotes = false;
        bool escaped = false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inDoubleQuotes && c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }

            if (!inDoubleQuotes && c == '#' && (i == 0 || char.IsWhiteSpace(value[i - 1])))
            {
                return value[..i].TrimEnd();
            }
        }

        return value.Trim();
    }

    private static bool IsSkippable(string line)
    {
        var t = line.AsSpan().TrimStart();
        return t.IsEmpty || t[0] == '#';
    }

    private static int GetIndent(string line)
    {
        int n = 0;
        foreach (char c in line) { if (c == ' ') n++; else break; }
        return n;
    }

    private static string FormatScalar(object? value) => value switch
    {
        bool b                      => b ? "yes" : "no",
        null                        => "",
        string s when s.Length == 0 => "\"\"",
        string s                    => FormatStringScalar(s),
        _                           => value.ToString()!
    };

    private static string FormatStringScalar(string value)
    {
        string normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length == 0) return "\"\"";

        if (normalized.Length >= 2 && normalized.StartsWith('"') && normalized.EndsWith('"'))
            return normalized;

        if (IsPlainScalarSafe(normalized)) return normalized;

        return "\"" + normalized
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"") + "\"";
    }

    private static bool IsPlainScalarSafe(string value)
    {
        if (value.StartsWith('-') || value.StartsWith('{') || value.StartsWith('[') ||
            value.StartsWith('&') || value.StartsWith('*') || value.StartsWith('!') ||
            value.StartsWith('|') || value.StartsWith('>') || value.StartsWith('@') ||
            value.StartsWith('`') || value.StartsWith('"') || value.StartsWith('\''))
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '#' && (i == 0 || char.IsWhiteSpace(value[i - 1]))) return false;
            if (c == ':' && (i == value.Length - 1 || char.IsWhiteSpace(value[i + 1]))) return false;
        }

        return true;
    }

    private static string UnquoteDoubleQuoted(string value)
    {
        string inner = value[1..^1];
        var builder = new StringBuilder(inner.Length);

        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c != '\\' || i == inner.Length - 1)
            {
                builder.Append(c);
                continue;
            }

            char escaped = inner[++i];
            builder.Append(escaped switch
            {
                '"'  => '"',
                '\\' => '\\',
                'n'  => '\n',
                'r'  => '\r',
                't'  => '\t',
                _    => escaped
            });
        }

        return builder.ToString();
    }

    private static string NormalizeInlineComment(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return "";
        return comment.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
