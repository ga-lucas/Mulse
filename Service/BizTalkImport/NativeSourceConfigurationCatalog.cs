using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mulse.Modules.Mapping;

namespace Service.BizTalkImport;

internal sealed class NativeSourceConfigurationCatalog
{
    private const string Identifier = "[A-Za-z_][A-Za-z_0-9]*";
    private const string Dotted = Identifier + "(?:\\." + Identifier + ")*";
    private const string String = "\"(?:[^\"\\\\\r\n]|\\\\[\"\\\\nrt])*\"";
    private static Regex Pattern(string text) => new(text, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
    private static readonly Regex Source = Pattern(@"\A\s*(?:using\s+" + Dotted + @"\s*;\s*)*namespace\s+(?<namespace>" + Dotted + @")\s*\{\s*public\s+static\s+class\s+(?<class>" + Identifier + @")\s*\{(?<body>[\s\S]*)\}\s*\}\s*\z");
    private static readonly Regex Constant = Pattern(@"\G\s*private\s+const\s+string\s+(?<name>" + Identifier + @")\s*=\s*(?<value>" + String + @")\s*;");
    private static readonly Regex Method = Pattern(@"\G\s*public\s+static\s+string\s+(?<name>" + Identifier + @")\s*\(\s*\)\s*\{\s*if\s*\(\s*System\.Configuration\.ConfigurationManager\.AppSettings\s*\[\s*(?<key>" + Identifier + @")\s*\]\s*!=\s*null\s*\)\s*return\s+System\.Configuration\.ConfigurationManager\.AppSettings\s*\[\s*\k<key>\s*\]\s*;\s*return\s+string\.Empty\s*;\s*\}");
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static NativeSourceConfigurationCatalog Read(string configurationPath, IReadOnlyList<string> helperPaths)
    {
        var configuration = XmlMappingSchemas.ReadXmlFile(configurationPath);
        var root = configuration.Root!;
        if (root.Name != "configuration" || root.Descendants().Any(element => element.Name.LocalName is "ConfigurationFile" or "location"
                || element.Name.LocalName == "appSettings" && element.Name.NamespaceName.Length != 0
                || element.Name == "section" && (string?)element.Attribute("name") == "appSettings")
            || root.Descendants("appSettings").Count() != root.Elements("appSettings").Count())
            throw new ArgumentException("Native configuration snapshot requires one host configuration without location/AppDomain overrides.");
        var sections = root.Elements("appSettings").ToArray();
        if (sections.Length > 1 || sections.Any(section => section.HasAttributes || !string.IsNullOrWhiteSpace(string.Concat(section.Nodes().OfType<XText>().Select(text => text.Value)))))
            throw new ArgumentException("Native configuration snapshot requires at most one inline appSettings section; file/configSource overrides are unsupported.");
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in sections.SelectMany(section => section.Elements()))
        {
            if (settings.Count >= 4096 || entry.Name != "add" || entry.HasElements || !string.IsNullOrWhiteSpace(entry.Value)
                || entry.Attributes().Count() != 2 || entry.Attribute("key") is not { Value.Length: > 0 and <= 256 } key
                || entry.Attribute("value") is not { Value.Length: <= 4096 } value || !settings.TryAdd(key.Value, value.Value))
                throw new ArgumentException("Native configuration snapshot requires at most 4096 unique bounded literal add key/value entries; duplicate keys, clear/remove and unsupported attributes are rejected.");
        }
        var catalog = new NativeSourceConfigurationCatalog();
        foreach (var path in helperPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string text;
            using (var reader = File.OpenText(path))
            {
                var buffer = new char[65_537];
                var length = reader.ReadBlock(buffer, 0, buffer.Length);
                if (length > 65_536) throw new ArgumentException("Configuration helper source exceeds 65536 characters.");
                text = new string(buffer, 0, length);
            }
            try
            {
                var source = Source.Match(text);
                if (!source.Success) throw new ArgumentException("Configuration helper must be a complete simple public static class of literal keys and parameterless appSettings getters; no source code is executed.");
                var constants = new Dictionary<string, string>(StringComparer.Ordinal);
                var body = source.Groups["body"].Value;
                var offset = 0;
                while (Constant.Match(body, offset) is { Success: true } constant)
                {
                    var value = JsonSerializer.Deserialize<string>(constant.Groups["value"].Value)!;
                    if (constants.Count >= 64 || value.Length is 0 or > 256 || !constants.TryAdd(constant.Groups["name"].Value, value))
                        throw new ArgumentException("Configuration helper requires at most 64 unique bounded literal setting keys.");
                    offset += constant.Length;
                }
                var methods = 0;
                while (Method.Match(body, offset) is { Success: true } method)
                {
                    if (++methods > 64 || !constants.TryGetValue(method.Groups["key"].Value, out var key))
                        throw new ArgumentException("Configuration getter requires a declared literal key and at most 64 methods.");
                    var symbol = source.Groups["namespace"].Value + "." + source.Groups["class"].Value + "." + method.Groups["name"].Value;
                    if (!catalog._values.TryAdd(symbol, settings.GetValueOrDefault(key, "")))
                        throw new ArgumentException("Configuration helper method identity is duplicated across explicitly selected source files.");
                    offset += method.Length;
                }
                if (methods == 0 || !string.IsNullOrWhiteSpace(body[offset..]))
                    throw new ArgumentException("Configuration helper contains unsupported behavior; no partial helper snapshot is accepted.");
            }
            catch (RegexMatchTimeoutException exception) { throw new ArgumentException("Configuration helper source inspection exceeded its bounded time limit.", exception); }
            catch (JsonException exception) { throw new ArgumentException("Configuration helper key requires a supported escaped literal string.", exception); }
        }
        return catalog;
    }

    public bool TryResolve(string symbol, out string value) => _values.TryGetValue(symbol, out value!);
}
