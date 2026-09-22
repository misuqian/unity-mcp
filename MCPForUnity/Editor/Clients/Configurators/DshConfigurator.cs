using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Models;
using MCPForUnity.Editor.Services;
using UnityEditor;

namespace MCPForUnity.Editor.Clients.Configurators
{
    /// <summary>
    /// DeepSeek Harness (dsh) configurator.
    ///
    /// DSH declares MCP servers as rows in a Cordis patch layer rather than in a JSON
    /// mcp.json, so this configurator implements the contract directly instead of deriving
    /// from JsonFileMcpConfigurator. The row is written to the home-level user patch layer
    /// ($DSH_HOME/cordis.patch.yml), which DSH applies over every profile's own
    /// cordis.patch.yml, so one write covers every profile on the machine.
    ///
    /// Only the mcp-unity row is ever touched: comments and every other row in the file are
    /// preserved byte for byte.
    /// </summary>
    public class DshConfigurator : McpClientConfiguratorBase
    {
        private const string EntryId = "mcp-unity";
        private const string ServerName = "unity";
        private const string BundleName = "@deepseek-ai/dsh-mcp-client";
        private const string PatchFileName = "cordis.patch.yml";
        private const string HttpTransportValue = "streamable-http";
        private const string StdioTransportValue = "stdio";
        private const string StdioPackageName = "mcp-for-unity";

        // Unity-side work (asset import, script compile, test runs) routinely outlives the
        // bridge's 60s default; the Python server allows 300s per command.
        private const int ToolCallTimeoutMs = 300000;

        private static readonly ConfiguredTransport[] DshTransports =
            { ConfiguredTransport.Http, ConfiguredTransport.Stdio };

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public DshConfigurator() : base(new McpClient
        {
            name = "DeepSeek Harness",
            SupportsHttpTransport = true,
        })
        { }

        public override IReadOnlyList<ConfiguredTransport> SupportedTransports => DshTransports;

        // DSH is a CLI/desktop app with no per-client directory of its own; the Harness home
        // is what proves it is installed.
        public override bool IsInstalled => Directory.Exists(GetDshHome());

        public override string GetConfigPath() => GetPatchPath();

        public override string GetConfigureActionLabel()
            => client.status == McpStatus.Configured ? "Unregister" : "Configure";

        public override McpStatus CheckStatus(bool attemptAutoRewrite = true)
        {
            try
            {
                string path = GetPatchPath();
                if (!File.Exists(path))
                {
                    SetUnconfigured();
                    return client.status;
                }

                string text = File.ReadAllText(path);
                string entry = FindUnityEntry(text);
                if (entry == null)
                {
                    client.SetStatus(McpStatus.MissingConfig);
                    client.configuredTransport = ConfiguredTransport.Unknown;
                    return client.status;
                }

                var expected = HttpEndpointUtility.GetCurrentServerTransport();
                client.configuredTransport = ReadConfiguredTransport(entry);

                if (EntryMatches(entry, expected, out var failureStatus, out string reason))
                {
                    client.SetStatus(McpStatus.Configured);
                    return client.status;
                }

                if (attemptAutoRewrite)
                {
                    File.WriteAllText(path, MergeEntry(text, BuildEntry()), Utf8NoBom);
                    client.SetStatus(McpStatus.Configured);
                    client.configuredTransport = expected;
                    return client.status;
                }

                client.SetStatus(failureStatus, reason);
            }
            catch (Exception ex)
            {
                client.SetStatus(McpStatus.Error, ex.Message);
                client.configuredTransport = ConfiguredTransport.Unknown;
            }

            return client.status;
        }

        public override void Configure()
        {
            try
            {
                string path = GetPatchPath();
                McpConfigurationHelper.EnsureConfigDirectoryExists(path);
                string existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
                File.WriteAllText(path, MergeEntry(existing, BuildEntry()), Utf8NoBom);

                client.SetStatus(McpStatus.Configured);
                client.configuredTransport = HttpEndpointUtility.GetCurrentServerTransport();
            }
            catch (Exception ex)
            {
                client.SetStatus(McpStatus.Error, ex.Message);
                throw new InvalidOperationException($"Failed to configure DeepSeek Harness: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Drops the mcp-unity row and leaves every other row (and every comment) intact.
        /// </summary>
        public override void Unregister()
        {
            try
            {
                string path = GetPatchPath();
                if (!File.Exists(path))
                {
                    SetUnconfigured();
                    return;
                }

                string[] lines = SplitLines(File.ReadAllText(path));
                List<int> starts = ItemStarts(lines);
                if (starts.Count == 0)
                {
                    SetUnconfigured();
                    return;
                }

                var kept = new List<string>();
                for (int i = 0; i < starts.Count; i++)
                {
                    string item = ItemText(lines, starts, i);
                    if (!IsUnityEntry(item))
                    {
                        kept.Add(TrimItemTrailingBlankLines(item));
                    }
                }

                string preamble = string.Join("\n", lines, 0, starts[0]);
                var output = new List<string>();
                if (preamble.Trim().Length > 0)
                {
                    output.Add(preamble.TrimEnd());
                }

                if (kept.Count > 0)
                {
                    output.AddRange(kept);
                }
                else
                {
                    output.Add("[]");
                }

                File.WriteAllText(path, string.Join("\n", output) + "\n", Utf8NoBom);
                SetUnconfigured();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to unregister: {ex.Message}", ex);
            }
        }

        public override string GetManualSnippet() => BuildEntry();

        public override IList<string> GetInstallationSteps() => new List<string>
        {
            $"Install DeepSeek Harness so that {GetDshHome()} exists",
            "Click Configure to add the unity MCP server to the Harness patch layer",
            "Restart dsh (dsh web / dsh cli); the Unity tools load as mcp__unity__<tool>",
            "Reload the Unity Editor window; Unregister removes only the mcp-unity row"
        };

        /// <summary>Harness home: $DSH_HOME when set, otherwise ~/.dsh.</summary>
        private static string GetDshHome()
        {
            string configured = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }

        private static string GetPatchPath() => Path.Combine(GetDshHome(), PatchFileName);

        private void SetUnconfigured()
        {
            client.SetStatus(McpStatus.NotConfigured);
            client.configuredTransport = ConfiguredTransport.Unknown;
        }

        /// <summary>
        /// Renders the patch row for the transport the user selected globally, matching the
        /// shape of the reference overlays shipped with DSH.
        /// </summary>
        private string BuildEntry()
        {
            var sb = new StringBuilder();
            sb.Append("- insert:\n");
            sb.Append("    - id: ").Append(EntryId).Append('\n');
            sb.Append("      name: ").Append(Quote(BundleName)).Append('\n');
            sb.Append("      config:\n");
            sb.Append("        serverName: ").Append(ServerName).Append('\n');

            if (EditorConfigurationCache.Instance.UseHttpTransport)
            {
                sb.Append("        transport: ").Append(HttpTransportValue).Append('\n');
                sb.Append("        url: ").Append(Quote(HttpEndpointUtility.GetMcpRpcUrl())).Append('\n');

                if (HttpEndpointUtility.IsRemoteScope())
                {
                    string apiKey = EditorPrefs.GetString(EditorPrefKeys.ApiKey, string.Empty);
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        sb.Append("        headers:\n");
                        sb.Append("          ").Append(AuthConstants.ApiKeyHeader).Append(": ")
                          .Append(Quote(apiKey)).Append('\n');
                    }
                }
            }
            else
            {
                var parts = AssetPathUtility.GetUvxCommandParts();
                string uvxPath = string.IsNullOrEmpty(parts.uvxPath) ? GetUvxPathOrError() : parts.uvxPath;
                string packageName = string.IsNullOrEmpty(parts.packageName) ? StdioPackageName : parts.packageName;

                var args = new List<string>();
                foreach (string flag in AssetPathUtility.GetUvxDevFlagsList())
                {
                    args.Add(flag);
                }

                foreach (string arg in AssetPathUtility.GetBetaServerFromArgsList())
                {
                    args.Add(arg);
                }

                args.Add(packageName);
                args.Add("--transport");
                args.Add(StdioTransportValue);

                sb.Append("        transport: ").Append(StdioTransportValue).Append('\n');
                sb.Append("        command: ").Append(Quote(uvxPath)).Append('\n');
                sb.Append("        args: [").Append(string.Join(", ", args.Select(Quote))).Append("]\n");
            }

            sb.Append("        toolCallTimeoutMs: ").Append(ToolCallTimeoutMs).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        private ConfiguredTransport ReadConfiguredTransport(string entry)
        {
            string transport = Scalar(entry, "transport");
            if (string.Equals(transport, StdioTransportValue, StringComparison.Ordinal))
            {
                return ConfiguredTransport.Stdio;
            }

            if (!string.Equals(transport, HttpTransportValue, StringComparison.Ordinal))
            {
                return ConfiguredTransport.Unknown;
            }

            string url = Scalar(entry, "url");
            string remoteUrl = HttpEndpointUtility.GetRemoteMcpRpcUrl();
            return !string.IsNullOrEmpty(remoteUrl) && UrlsEqual(url, remoteUrl)
                ? ConfiguredTransport.HttpRemote
                : ConfiguredTransport.Http;
        }

        private static string FindUnityEntry(string text)
        {
            string[] lines = SplitLines(text);
            List<int> starts = ItemStarts(lines);
            for (int i = 0; i < starts.Count; i++)
            {
                string item = ItemText(lines, starts, i);
                if (IsUnityEntry(item))
                {
                    return item;
                }
            }

            return null;
        }

        private bool EntryMatches(
            string entry, ConfiguredTransport expected, out McpStatus failureStatus, out string reason)
        {
            failureStatus = McpStatus.IncorrectPath;
            reason = null;
            string transport = Scalar(entry, "transport");

            if (expected == ConfiguredTransport.Stdio)
            {
                if (!string.Equals(transport, StdioTransportValue, StringComparison.Ordinal))
                {
                    reason = "Configured for a different transport. Re-configure to switch to stdio.";
                    return false;
                }

                string[] configuredArgs = FlowSequence(Scalar(entry, "args"));
                string configuredSource = McpConfigurationHelper.ExtractUvxUrl(configuredArgs);
                string expectedSource = GetExpectedPackageSourceForValidation();
                if (!string.IsNullOrEmpty(configuredSource)
                    && !string.IsNullOrEmpty(expectedSource)
                    && McpConfigurationHelper.PathsEqual(configuredSource, expectedSource))
                {
                    return true;
                }

                failureStatus = McpStatus.VersionMismatch;
                reason = DescribePackageSourceMismatch(configuredSource, expectedSource);
                return false;
            }

            if (!string.Equals(transport, HttpTransportValue, StringComparison.Ordinal))
            {
                reason = "Configured for a different transport. Re-configure to switch to HTTP.";
                return false;
            }

            if (!UrlsEqual(Scalar(entry, "url"), HttpEndpointUtility.GetMcpRpcUrl()))
            {
                reason = "Server URL doesn't match the endpoint configured in the Unity Editor.";
                return false;
            }

            if (HttpEndpointUtility.IsRemoteScope())
            {
                string expectedKey = EditorPrefs.GetString(EditorPrefKeys.ApiKey, string.Empty);
                string configuredKey = Scalar(entry, AuthConstants.ApiKeyHeader);
                if (!string.Equals(configuredKey ?? string.Empty, expectedKey ?? string.Empty, StringComparison.Ordinal))
                {
                    reason = "API key doesn't match the remote endpoint. Re-configure to update it.";
                    return false;
                }
            }

            return true;
        }

        private static string DescribePackageSourceMismatch(string configured, string expected)
        {
            bool configuredIsBeta = IsBetaPackageSource(configured);
            bool expectedIsBeta = IsBetaPackageSource(expected);

            if (configuredIsBeta && !expectedIsBeta)
            {
                return "Configured for prerelease server, but this package is stable. Re-configure to switch to stable.";
            }

            if (!configuredIsBeta && expectedIsBeta)
            {
                return "Configured for stable server, but this package is prerelease. Re-configure to switch to prerelease.";
            }

            return "Server version doesn't match the plugin. Re-configure to update.";
        }

        /// <summary>
        /// Writes the row into the patch list: refreshes the existing mcp-unity row when one is
        /// present, appends it otherwise, and refuses to touch a file that is not a top-level
        /// patch list.
        /// </summary>
        private static string MergeEntry(string text, string entry)
        {
            string[] lines = SplitLines(text);
            string[] entryLines = SplitLines(entry);
            List<int> starts = ItemStarts(lines);

            if (starts.Count == 0)
            {
                var preamble = new List<string>();
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                    {
                        preamble.Add(line);
                        continue;
                    }

                    if (trimmed == "[]")
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"{PatchFileName} is not a top-level patch list; refusing to rewrite it. " +
                        $"Add the mcp-unity row manually.");
                }

                while (preamble.Count > 0 && preamble[preamble.Count - 1].Trim().Length == 0)
                {
                    preamble.RemoveAt(preamble.Count - 1);
                }

                preamble.AddRange(entryLines);
                return string.Join("\n", preamble) + "\n";
            }

            var output = new List<string>();
            for (int i = 0; i < starts[0]; i++)
            {
                output.Add(lines[i]);
            }

            bool replaced = false;
            for (int i = 0; i < starts.Count; i++)
            {
                string item = ItemText(lines, starts, i);
                if (!replaced && IsUnityEntry(item))
                {
                    output.AddRange(entryLines);
                    replaced = true;
                }
                else
                {
                    output.Add(TrimItemTrailingBlankLines(item));
                }
            }

            if (!replaced)
            {
                output.AddRange(entryLines);
            }

            return string.Join("\n", output).TrimEnd('\n') + "\n";
        }

        /// <summary>
        /// Identifies the row this configurator owns. The bundle name alone is not enough:
        /// other DSH rows (a memory server, for example) use the same mcp-client bundle, so
        /// the row id — every row this configurator writes carries it — decides ownership,
        /// with the serverName as the fallback for hand-written rows that renamed the id.
        /// </summary>
        private static bool IsUnityEntry(string item)
        {
            if (string.Equals(Scalar(item, "id"), EntryId, StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(Scalar(item, "name"), BundleName, StringComparison.Ordinal)
                && string.Equals(Scalar(item, "serverName"), ServerName, StringComparison.Ordinal);
        }

        private static string[] SplitLines(string text)
            => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // A top-level patch list starts every row with "- " in column zero; continuation lines
        // of a row (including block scalars) are indented, so they never open a new row.
        private static bool IsItemStart(string line)
        {
            if (line.Length == 0 || line[0] != '-')
            {
                return false;
            }

            return line.Length == 1 || line[1] == ' ' || line[1] == '\t';
        }

        private static List<int> ItemStarts(string[] lines)
        {
            var starts = new List<int>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsItemStart(lines[i]))
                {
                    starts.Add(i);
                }
            }

            return starts;
        }

        /// <summary>
        /// Drops the blank lines an item inherits from the newline that ended the file, so
        /// reassembling the list cannot grow a blank line between rows on every write.
        /// </summary>
        private static string TrimItemTrailingBlankLines(string item) => item.TrimEnd('\n');

        private static string ItemText(string[] lines, List<int> starts, int index)
        {
            int start = starts[index];
            int end = index + 1 < starts.Count ? starts[index + 1] : lines.Length;
            return string.Join("\n", lines, start, end - start);
        }

        /// <summary>Reads a "key: value" scalar of the row, unwrapping quotes.</summary>
        private static string Scalar(string entry, string key)
        {
            var match = Regex.Match(entry, "^[ \t]*" + Regex.Escape(key) + ":[ \t]*(.*?)[ \t]*$",
                RegexOptions.Multiline);
            if (!match.Success)
            {
                return null;
            }

            return Unquote(match.Groups[1].Value.Trim());
        }

        /// <summary>Reads a flow sequence ("['a', 'b']") into its items, honoring quotes.</summary>
        private static string[] FlowSequence(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            string inner = value.Trim();
            if (inner.StartsWith("[", StringComparison.Ordinal) && inner.EndsWith("]", StringComparison.Ordinal))
            {
                inner = inner.Substring(1, inner.Length - 2);
            }

            var items = new List<string>();
            var current = new StringBuilder();
            char quote = '\0';
            foreach (char c in inner)
            {
                if (quote != '\0')
                {
                    current.Append(c);
                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c == '\'' || c == '"')
                {
                    quote = c;
                    current.Append(c);
                    continue;
                }

                if (c == ',')
                {
                    AddFlowItem(items, current);
                    continue;
                }

                current.Append(c);
            }

            AddFlowItem(items, current);
            return items.ToArray();
        }

        private static void AddFlowItem(List<string> items, StringBuilder current)
        {
            string item = Unquote(current.ToString().Trim());
            current.Clear();
            if (item.Length > 0)
            {
                items.Add(item);
            }
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
            {
                return value.Substring(1, value.Length - 2).Replace("''", "'");
            }

            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2).Replace("\\\"", "\"");
            }

            return value;
        }

        private static string Quote(string value) => "'" + (value ?? string.Empty).Replace("'", "''") + "'";
    }
}
