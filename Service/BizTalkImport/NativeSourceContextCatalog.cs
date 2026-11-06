using System.Xml;
using System.Xml.Linq;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Mapping;

namespace Service.BizTalkImport;

internal sealed class NativeSourceContextCatalog
{
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";
    private static readonly XNamespace BizTalk = "http://schemas.microsoft.com/BizTalk/2003";
    private readonly Dictionary<string, List<NativeMessageFact>> _properties = new(StringComparer.Ordinal);
    private readonly HashSet<string> _uncertainNamespaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _keyOwners = new(StringComparer.Ordinal);
    private readonly IReadOnlySet<string> _messages;
    private string? _catalogFailure;

    /// <summary>Importer-defined metadata key prefix for adapter/assembler properties that no project schema declares; the key is not a BizTalk property identity.</summary>
    internal const string PlatformKeyNamespace = "urn:mulse:biztalk-platform-property:";
    // Exact symbols only (observed in the supplied Billing/Requisition sources); a namespace is never trusted as a whole.
    private static readonly IReadOnlySet<string> PlatformProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "BTAHL7Schemas.ToBeBatched", "BTAHL7Schemas.MSH5_1", "BTAHL7Schemas.MSH3_1", "BTAHL7Schemas.MSH2", "BTAHL7Schemas.MSH1", "BTAHL7Schemas.MessageClass",
        "BTAHL7Schemas.MessageEncoding", "BTAHL7Schemas.ParseError", "BTAHL7Schemas.ZPartPresent", "BTAHL7Schemas.SegmentDelimiter2Char",
        "FTP.UserName", "FTP.Password", "FTP.PassiveMode", "FTP.FtpsConnectionMode", "FTP.UseSsl", "FTP.UseDataProtection",
        "FILE.Username", "FILE.Password", "MIME.FileName", "BTS.ReceivePortName",
        "SMTP.Subject", "SMTP.From", "SMTP.SMTPHost", "SMTP.SMTPAuthenticate", "SMTP.EmailBodyText", "SMTP.EmailBodyTextCharset",
        "nsoftware.BizTalk.FTP.User", "nsoftware.BizTalk.FTP.Password", "nsoftware.BizTalk.FTP.FTPServer", "nsoftware.BizTalk.FTP.RemoteFile",
        "nsoftware.BizTalk.FTP.RemotePath", "nsoftware.BizTalk.FTP.FTPPort", "nsoftware.BizTalk.FTP.SSLCertSubject", "nsoftware.BizTalk.FTP.SSLAcceptServerCertAcceptAny",
        "nsoftware.BizTalk.SFTP.SSHHost", "nsoftware.BizTalk.SFTP.SSHPort", "nsoftware.BizTalk.SFTP.RemoteFile", "nsoftware.BizTalk.SFTP.RemotePath",
        "nsoftware.BizTalk.SFTP.SSHCertSubject", "nsoftware.BizTalk.SFTP.SSHUser", "nsoftware.BizTalk.SFTP.SSHPassword", "nsoftware.BizTalk.SFTP.Append",
        "nsoftware.BizTalk.SFTP.Overwrite", "nsoftware.BizTalk.SFTP.SSHAcceptServerHostKeyAcceptAny"
    };
    private static readonly IReadOnlySet<string> PlatformSecrets = new HashSet<string>(StringComparer.Ordinal)
    { "FTP.Password", "FILE.Password", "nsoftware.BizTalk.FTP.Password", "nsoftware.BizTalk.SFTP.SSHPassword" };

    public static bool IsPlatformProperty(string symbol) => PlatformProperties.Contains(symbol);
    public static bool IsPlatformSecret(string symbol) => PlatformSecrets.Contains(symbol);
    public static bool IsPlatformKey(string key) => key.StartsWith("{" + PlatformKeyNamespace, StringComparison.Ordinal);
    private static string PlatformKey(string symbol)
    {
        var separator = symbol.LastIndexOf('.');
        return XName.Get(symbol[(separator + 1)..], PlatformKeyNamespace + symbol[..separator]).ToString();
    }

    private NativeSourceContextCatalog(IReadOnlySet<string> messages) => _messages = messages;

    public static NativeSourceContextCatalog Read(BizTalkSchemaSourceCatalog schemas, IReadOnlySet<string> messages)
    {
        var result = new NativeSourceContextCatalog(messages);
        var count = 0;
        foreach (var evidence in schemas.DeclaredSchemas)
        {
            var separator = evidence.Reference.LastIndexOf('.');
            var clrNamespace = separator < 0 ? "" : evidence.Reference[..separator];
            if (evidence.Status != "SourceCandidate" || evidence.Candidates.Count != 1)
            {
                result._uncertainNamespaces.Add(clrNamespace);
                continue;
            }
            try
            {
                var schema = XmlMappingSchemas.ReadXmlFile(evidence.Candidates[0].SourcePath).Root;
                if (schema?.Name != Xs + "schema") throw new NotSupportedException("Declared property source must be an XSD schema.");
                var info = schema.Elements(Xs + "annotation").Elements(Xs + "appinfo").Elements(BizTalk + "schemaInfo").ToArray();
                if (!info.Any(item => (string?)item.Attribute("schema_type") == "property")) continue;
                if (info.Length != 1 || schema.Elements().Any(item => item.Name == Xs + "include" || item.Name == Xs + "redefine" || item.Name == Xs + "import"))
                    throw new NotSupportedException("Property identity requires one explicit property schema without imported or redefined declarations.");
                var ns = (string?)schema.Attribute("targetNamespace");
                if (string.IsNullOrWhiteSpace(ns) || ns.Length > 2048) throw new NotSupportedException("Property schema requires an explicit bounded namespace.");
                _ = XmlMappingSchemas.Compile([schema.ToString(SaveOptions.DisableFormatting)]);
                foreach (var element in schema.Elements(Xs + "element"))
                {
                    if (++count > 4096)
                    {
                        result._catalogFailure = "Context property catalog exceeds 4096 declarations; no partial ownership catalog is used.";
                        return result;
                    }
                    var name = (string?)element.Attribute("name");
                    if (name is null || !NativeExpression.ValidName(name))
                        throw new NotSupportedException("Property declarations require bounded CLR identifiers.");
                    var symbol = (clrNamespace.Length == 0 ? "" : clrNamespace + ".") + name;
                    var key = XName.Get(name, ns).ToString();
                    if (!result._properties.TryGetValue(symbol, out var matches)) result._properties[symbol] = matches = [];
                    if (!result._keyOwners.TryGetValue(key, out var owners)) result._keyOwners[key] = owners = new(StringComparer.Ordinal);
                    owners.Add(evidence.Reference);
                    try
                    {
                        var fields = element.Elements(Xs + "annotation").Elements(Xs + "appinfo").Elements(BizTalk + "fieldInfo").ToArray();
                        if (fields.Length != 1 || !Guid.TryParse((string?)fields[0].Attribute("propertyGuid"), out var guid) || guid == Guid.Empty
                            || (string?)fields[0].Attribute("propSchFieldBase") is not ("MessageDataPropertyBase" or "MessageContextPropertyBase")
                            || element.Attribute("ref") is not null || element.Elements().Any(item => item.Name != Xs + "annotation"))
                            throw new NotSupportedException("Property requires one explicit data/context field identity and a built-in scalar type.");
                        var type = (string?)element.Attribute("type") ?? "";
                        var parts = type.Split(':');
                        var typeNamespace = parts.Length == 2 ? element.GetNamespaceOfPrefix(parts[0]) : parts.Length == 1 ? element.GetDefaultNamespace() : null;
                        if (typeNamespace != Xs) throw new NotSupportedException("Property type must be an explicit XML Schema built-in.");
                        var kind = parts[^1] switch
                        {
                            "string" => NativeMessageFactKind.Metadata,
                            "int" => NativeMessageFactKind.MetadataInt32,
                            "boolean" => NativeMessageFactKind.MetadataBoolean,
                            _ => throw new NotSupportedException("Property type supports only String, Int32 or Boolean; no implicit datatype conversion.")
                        };
                        var fact = new NativeMessageFact { Kind = kind, MetadataKey = key };
                        fact.Validate();
                        matches.Add(fact);
                    }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                    { result._failures[symbol] = "Context property translation failed: " + exception.Message; }
                }
            }
            catch (Exception exception) when (exception is ArgumentException or XmlException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                result._uncertainNamespaces.Add(clrNamespace);
                result._failures[clrNamespace] = "Property schema ownership is incomplete: " + exception.Message;
            }
        }
        return result;
    }

    public NativeMessageFact Resolve(string message, string symbol)
    {
        if (_catalogFailure is not null) throw new NotSupportedException(_catalogFailure);
        if (!_messages.Contains(message)) throw new NotSupportedException("Context access requires an unshadowed, uniquely declared service-level message; scope-local/undeclared message identities are not inferred.");
        if (IsPlatformProperty(symbol) && !_properties.ContainsKey(symbol)) return new() { Kind = NativeMessageFactKind.Metadata, MetadataKey = PlatformKey(symbol), MessageName = message };
        var separator = symbol.LastIndexOf('.');
        var clrNamespace = separator < 0 ? "" : symbol[..separator];
        if (_uncertainNamespaces.Contains(clrNamespace))
            throw new NotSupportedException(_failures.GetValueOrDefault(clrNamespace, "Context property namespace has incomplete or ambiguous declared source ownership."));
        if (_failures.TryGetValue(symbol, out var failure)) throw new NotSupportedException(failure);
        if (!_properties.TryGetValue(symbol, out var matches) || matches.Count != 1 || _keyOwners[matches[0].MetadataKey].Count != 1)
            throw new NotSupportedException("Context property requires one exact declared property-schema identity and unambiguous expanded name; built-in, multipart and undeclared properties are not inferred.");
        return new() { Kind = matches[0].Kind, MetadataKey = matches[0].MetadataKey, MessageName = message };
    }
}
