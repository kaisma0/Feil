using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace Feil.Services.SLSsteam;

public class SLSsteamService
{
    private readonly string _configFilePath;

    public SLSsteamService(string? customConfigPath = null)
    {
        if (!string.IsNullOrEmpty(customConfigPath))
        {
            _configFilePath = customConfigPath;
        }
        else
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _configFilePath = Path.Combine(userHome, ".config", "SLSsteam", "config.yaml");
        }
    }

    public bool IsInstalled()
    {
        return OperatingSystem.IsLinux() && File.Exists(_configFilePath);
    }

    public string? GetConfigValue(string[] path)
    {
        if (!IsInstalled() || path == null || path.Length == 0)
            return null;

        try
        {
            var yamlStream = new YamlStream();
            using (var reader = new StreamReader(_configFilePath))
            {
                yamlStream.Load(reader);
            }

            if (yamlStream.Documents.Count == 0)
                return null;

            var document = yamlStream.Documents[0];
            var rootNode = (YamlMappingNode)document.RootNode;

            YamlNode currentNode = rootNode;

            for (int i = 0; i < path.Length; i++)
            {
                var key = new YamlScalarNode(path[i]);
                if (currentNode is YamlMappingNode mapNode && mapNode.Children.ContainsKey(key))
                {
                    currentNode = mapNode.Children[key];
                }
                else
                {
                    return null;
                }
            }

            if (currentNode is YamlScalarNode scalarNode)
                return scalarNode.Value;

            return null; // For lists/dicts returning null as we expect scalars for the simple UI
        }
        catch
        {
            return null;
        }
    }

    /// <param name="path">The hierarchical property path (e.g., ["AdditionalApps"] or ["DlcData", "12345", "6789"])</param>
    /// <param name="action">The action to perform: "set", "add", "remove"</param>
    /// <param name="value">The value to set, add, or remove. Can be null for removing entire nodes.</param>
    /// <param name="entryType">Optional hint for the structure type ("scalar", "list", "dictionary")</param>
    public bool ModifyConfig(string[] path, string action, object? value, string? entryType = null)
    {
        if (!IsInstalled() || path == null || path.Length == 0)
            return false;

        try
        {
            var yamlStream = new YamlStream();
            using (var reader = new StreamReader(_configFilePath))
            {
                yamlStream.Load(reader);
            }

            if (yamlStream.Documents.Count == 0)
                return false;

            var document = yamlStream.Documents[0];
            var rootNode = (YamlMappingNode)document.RootNode;

            YamlNode currentNode = rootNode;
            YamlMappingNode? parentMapping = null;
            YamlScalarNode? finalKey = null;

            for (int i = 0; i < path.Length; i++)
            {
                var key = path[i];
                var yamlKey = new YamlScalarNode(key);

                if (currentNode is YamlMappingNode mapNode)
                {
                    if (i == path.Length - 1)
                    {
                        parentMapping = mapNode;
                        finalKey = yamlKey;

                        if (!mapNode.Children.ContainsKey(yamlKey))
                        {
                            YamlNode newNode = entryType?.ToLower() switch
                            {
                                "list" => new YamlSequenceNode(),
                                "dictionary" => new YamlMappingNode(),
                                _ => new YamlScalarNode("")
                            };
                            mapNode.Add(yamlKey, newNode);
                        }
                        // if we are at the end, but the node is an empty scalar and we need to add to it using entryType:
                        if (action.ToLower() == "add" && mapNode.Children[yamlKey] is YamlScalarNode sc && string.IsNullOrEmpty(sc.Value))
                        {
                            if (entryType == "dictionary" || entryType?.ToLower() == "mapping")
                                mapNode.Children[yamlKey] = new YamlMappingNode();
                            else
                                mapNode.Children[yamlKey] = new YamlSequenceNode();
                        }

                        currentNode = mapNode.Children[yamlKey];
                    }
                    else
                    {
                        if (!mapNode.Children.ContainsKey(yamlKey) || (mapNode.Children[yamlKey] is YamlScalarNode sc2 && string.IsNullOrEmpty(sc2.Value)))
                        {
                            mapNode.Children[yamlKey] = new YamlMappingNode();
                        }
                        currentNode = mapNode.Children[yamlKey];
                    }
                }
                else
                {
                    // Invalid path traversal (trying to treat a scalar/list as a dictionary)
                    return false;
                }
            }

            var targetNode = currentNode;
            action = action.ToLower();

            if (action == "set")
            {
                YamlNode newNode;
                if (value is bool b)
                    newNode = new YamlScalarNode(b ? "yes" : "no");
                else
                    newNode = new YamlScalarNode(value?.ToString() ?? "");

                if (parentMapping != null && finalKey != null)
                {
                    parentMapping.Children[finalKey] = newNode;
                }
            }
            else if (action == "add")
            {
                // If it was parsed as an empty scalar, upgrade it based on entryType
                if (targetNode is YamlScalarNode sc && string.IsNullOrEmpty(sc.Value))
                {
                    if (entryType == "dictionary" || entryType?.ToLower() == "mapping")
                        targetNode = new YamlMappingNode();
                    else // default to sequence when adding
                        targetNode = new YamlSequenceNode();

                    if (parentMapping != null && finalKey != null)
                        parentMapping.Children[finalKey] = targetNode;
                }

                if (targetNode is YamlSequenceNode seqTarget)
                {
                    seqTarget.Add(new YamlScalarNode(value?.ToString() ?? ""));
                }
                else if (targetNode is YamlMappingNode mapTarget && value is KeyValuePair<string, string> kvp)
                {
                    mapTarget.Add(new YamlScalarNode(kvp.Key), new YamlScalarNode(kvp.Value));
                }
            }
            else if (action == "remove")
            {
                if (value == null)
                {
                    // Remove the entire node defined by the path
                    if (parentMapping != null && finalKey != null)
                        parentMapping.Children.Remove(finalKey);
                }
                else
                {
                    string strVal = value.ToString() ?? "";
                    if (targetNode is YamlSequenceNode seqTarget)
                    {
                        var nodeToRemove = seqTarget.Children.FirstOrDefault(c => c is YamlScalarNode s && s.Value == strVal);
                        if (nodeToRemove != null)
                            seqTarget.Children.Remove(nodeToRemove);
                    }
                    else if (targetNode is YamlMappingNode mapTarget)
                    {
                        var keyToRemove = new YamlScalarNode(strVal);
                        if (mapTarget.Children.ContainsKey(keyToRemove))
                            mapTarget.Children.Remove(keyToRemove);
                    }
                }
            }

            using (var writer = new StreamWriter(_configFilePath))
            {
                yamlStream.Save(writer, assignAnchors: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to modify SLSsteam config: {ex.Message}");
            return false;
        }
    }
}
