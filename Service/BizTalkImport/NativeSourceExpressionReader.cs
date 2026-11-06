using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mulse.Modules.ControlFlow;

namespace Service.BizTalkImport;

/// <summary>Maps one exact source helper method (matched by its dotted suffix) onto a pure hot-loadable transform module with explicit settings.</summary>
internal sealed record NativeSourceUdf(string Module, IReadOnlyDictionary<string, string> Settings, int Arguments);

internal sealed class NativeSourceExpressionReader
{
    private static readonly Regex Token = new("""\G\s*(?:(?<comment>//[^\r\n]*|/\*[\s\S]*?\*/)|(?<string>"(?:[^"\\\r\n]|\\["\\nrt])*")|(?<number>[0-9]+)|(?<name>[A-Za-z_][A-Za-z_0-9]*(?:\.[A-Za-z_][A-Za-z_0-9]*)*)|(?<symbol>==|!=|<=|>=|&&|\|\||\+\+|--|\+=|[-+*/%<>()!;,={}]))""", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly string _source;
    private readonly IReadOnlyDictionary<string, NativeScalarType> _variables;
    private readonly IReadOnlySet<string> _messages;
    private readonly NativeSourceMessageCatalog? _messageCatalog;
    private readonly IReadOnlySet<string> _documents;
    private readonly IReadOnlySet<string> _elements;
    private readonly NativeSourceContextCatalog? _contextCatalog;
    private readonly NativeSourceConfigurationCatalog? _configuration;
    private readonly IReadOnlySet<string> _transformTypes;
    private readonly string? _mapChoices;
    private readonly IReadOnlyDictionary<string, NativeSourceUdf> _udfs;
    private readonly IReadOnlySet<string> _dynamicPorts;
    private string _token = "", _kind = "";
    private int _offset, _depth, _values, _statements;

    public NativeSourceExpressionReader(string source, IReadOnlyDictionary<string, NativeScalarType> variables, IReadOnlySet<string>? messages = null, NativeSourceMessageCatalog? messageCatalog = null, IReadOnlySet<string>? documents = null, IReadOnlySet<string>? elements = null, NativeSourceContextCatalog? contextCatalog = null, NativeSourceConfigurationCatalog? configuration = null,
        IReadOnlySet<string>? transformTypes = null, string? mapChoices = null, IReadOnlyDictionary<string, NativeSourceUdf>? udfs = null, IReadOnlySet<string>? dynamicPorts = null)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 4096) throw new NotSupportedException("Native scalar source requires 1-4096 characters.");
        _source = source; _variables = variables; _messages = messages ?? new HashSet<string>(StringComparer.Ordinal); _messageCatalog = messageCatalog;
        _documents = documents ?? new HashSet<string>(StringComparer.Ordinal);
        _elements = elements ?? new HashSet<string>(StringComparer.Ordinal);
        _contextCatalog = contextCatalog; _configuration = configuration;
        _transformTypes = transformTypes ?? new HashSet<string>(StringComparer.Ordinal); _mapChoices = mapChoices;
        _udfs = udfs ?? new Dictionary<string, NativeSourceUdf>(StringComparer.Ordinal);
        _dynamicPorts = dynamicPorts ?? new HashSet<string>(StringComparer.Ordinal); Next();
    }
    /// <summary>Hidden String variable that holds one runtime property of a dynamic send port, so the value keeps assignment-time semantics.</summary>
    internal static string DynamicPortVariable(string port, string property) => port + "_" + property;
    // Dotted keys (e.g. "ex.Message") can only be importer-registered read-only exception aliases; the native variable uses underscores.
    private static string NativeVariableName(string token) => token.Replace('.', '_');
    private NativeMessageFact ReadContext(string message)
    {
        Expect("(");
        if (_kind != "name" || _contextCatalog is null) throw new NotSupportedException("Message context access requires an exact declared property-schema identity.");
        var fact = _contextCatalog.Resolve(message, _token);
        Next(); Expect(")"); return fact;
    }
    private void Next()
    {
        while (true)
        {
            if (string.IsNullOrWhiteSpace(_source[_offset..])) { _token = _kind = ""; return; }
            Match match;
            try { match = Token.Match(_source, _offset); }
            catch (RegexMatchTimeoutException exception) { throw new NotSupportedException("Source scalar tokenization exceeded its bounded time limit.", exception); }
            if (!match.Success) throw new NotSupportedException("Source scalar expression contains unsupported syntax; schema members and CLR calls require explicit adapters.");
            _offset += match.Length;
            if (match.Groups["comment"].Success) continue;
            _kind = new[] { "string", "number", "name", "symbol" }.Single(name => match.Groups[name].Success);
            _token = match.Groups[_kind].Value;
            return;
        }
    }
    private bool Take(string token) { if (_kind != "symbol" || _token != token) return false; Next(); return true; }
    private void Expect(string token) { if (!Take(token)) throw new NotSupportedException("Incomplete native source scalar expression."); }
    private void ExpectName(string name)
    {
        if (_kind != "name" || _token != name) throw new NotSupportedException("XML composition requires exact declared document/element identities and complete supported operations.");
        Next();
    }
    private void End() { if (_token.Length != 0) throw new NotSupportedException("Native source scalar expression has unconsumed behavior."); }
    public NativeExpression ReadCondition() { var expression = Read(); End(); return expression; }
    public IReadOnlyList<NativeFlowNode> ReadAssignments(string id) => ReadBlock(id, 0);
    private NativeFlowNode ReadIf(string id, int depth)
    {
        Expect("("); var condition = Read(); Expect(")");
        RequireType(condition, "if", NativeScalarType.Boolean); NativeExpression.Validate(condition);
        var node = new NativeFlowNode { Id = id, Name = "If", Kind = NativeFlowNodeKind.If, Expression = condition, Children = ReadBranch(id + "-then", depth) };
        if (_kind == "name" && _token == "else")
        {
            Next();
            if (_kind == "name" && _token == "if") { Next(); node.Otherwise = [ReadIf(id + "-elseif", depth + 1)]; }
            else node.Otherwise = ReadBranch(id + "-else", depth);
        }
        return node;
    }
    private List<NativeFlowNode> ReadBranch(string id, int depth)
    {
        if (depth >= 8) throw new NotSupportedException("Source conditional nesting exceeds 8 levels.");
        Expect("{"); var body = ReadBlock(id, depth + 1); Expect("}");
        return [.. body];
    }
    private List<NativeFlowNode> ReadBlock(string id, int depth)
    {
        var nodes = new List<NativeFlowNode>();
        string? pendingEmptyDocument = null;
        while (_token.Length != 0 && !(depth > 0 && _kind == "symbol" && _token == "}"))
        {
            if (++_statements > 64) throw new NotSupportedException("Source block exceeds 64 statements.");
            var name = _token;
            if (_kind != "name") throw new NotSupportedException("Source statement requires a declared scalar/message or exact schema member.");
            Next();
            if (name == "if")
            {
                if (pendingEmptyDocument is not null) throw new NotSupportedException("A conditional cannot interrupt an incomplete XML document construction.");
                nodes.Add(ReadIf(id + "-if-" + nodes.Count, depth)); continue;
            }
            if (_dynamicPorts.Contains(name) && _token == "(")
            {
                if (pendingEmptyDocument is not null) throw new NotSupportedException("A port assignment cannot interrupt an incomplete XML document construction.");
                Next();
                var property = _kind == "name" && _token is "Microsoft.XLANGs.BaseTypes.Address" or "Microsoft.XLANGs.BaseTypes.TransportType" ? _token[(_token.LastIndexOf('.') + 1)..]
                    : throw new NotSupportedException("Dynamic port property assignment supports only the exact Address and TransportType properties; other adapter properties are not inferred.");
                Next(); Expect(")"); Expect("=");
                var variable = DynamicPortVariable(name, property);
                if (!_variables.TryGetValue(variable, out var variableType) || variableType != NativeScalarType.String) throw new NotSupportedException("Dynamic port '" + name + "' has no hidden String state variable.");
                if (ReadUdfCall(variable, id + "-udf-" + nodes.Count) is { } portCall) { nodes.Add(portCall); continue; }
                var assigned = Read(); Expect(";"); NativeExpression.Validate(assigned);
                RequireType(assigned, name + " " + property, NativeScalarType.String);
                nodes.Add(new() { Id = id + "-set-" + nodes.Count, Name = name + " " + property, Kind = NativeFlowNodeKind.SetVariable, Variable = variable, Expression = assigned });
                continue;
            }
            var document = name.EndsWith(".LoadXml", StringComparison.Ordinal) ? name[..^8] : null;
            if (_transformTypes.Contains(name))
            {
                if (pendingEmptyDocument is not null) throw new NotSupportedException("A transform cannot consume an incomplete XML document construction.");
                if (_mapChoices is null || Mulse.Modules.Mapping.RenderDispatchDefinition.Read(_mapChoices, allowEmpty: true).Count == 0)
                    throw new NotSupportedException("Dynamic transforms require explicit exact source map identity to native renderer choices; no legacy CLR map is loaded.");
                Expect("="); ExpectName("System.Type.GetType"); Expect("("); var selector = Read(); Expect(")"); Expect(";");
                if (selector.Kind switch
                    {
                        NativeExpressionKind.Literal => selector.Literal?.Type != NativeScalarType.String,
                        NativeExpressionKind.Variable => _variables[selector.Variable!] != NativeScalarType.String,
                        NativeExpressionKind.MessageValue => selector.Fact!.Kind is not (NativeMessageFactKind.XmlString or NativeMessageFactKind.Metadata),
                        _ => true
                    }) throw new NotSupportedException("Dynamic map selection requires an exact proven String literal, scalar or schema/context field.");
                ExpectName("transform"); Expect("(");
                if (_kind != "name" || !_messages.Contains(_token)) throw new NotSupportedException("Dynamic transform output requires an exact unshadowed service message.");
                var output = _token; Next(); Expect(")"); Expect("="); ExpectName(name); Expect("(");
                var inputs = new List<string>();
                do
                {
                    if (inputs.Count == 32 || _kind != "name" || !_messages.Contains(_token))
                        throw new NotSupportedException("Dynamic transforms require 1-32 ordered exact unshadowed service message arguments.");
                    inputs.Add(_token); Next();
                } while (Take(","));
                Expect(")"); Expect(";");
                if (++_statements > 64) throw new NotSupportedException("Source block exceeds 64 statements.");
                NativeExpression.Validate(selector);
                nodes.Add(new() { Id = id + "-map-" + nodes.Count, Kind = NativeFlowNodeKind.Render,
                    Module = Mulse.Modules.Mapping.RenderDispatchDefinition.ModuleId, InputMessages = inputs, MessageName = output,
                    SelectionExpression = selector, Settings = new() { ["casesJson"] = _mapChoices } });
                continue;
            }
            if (_elements.Contains(name))
            {
                if (pendingEmptyDocument is null) throw new NotSupportedException("A temporary XML root element must immediately initialize a newly constructed document in the same complete block.");
                var owner = pendingEmptyDocument;
                Expect("="); ExpectName(owner + ".CreateElement"); Expect("(");
                if (_kind != "string" || JsonSerializer.Deserialize<string>(_token) != "")
                    throw new NotSupportedException("Native root creation requires an explicitly empty prefix; prefixed DOM construction is not inferred.");
                Next(); Expect(","); var local = Read(); Expect(","); var ns = Read(); Expect(")"); Expect(";");
                if (++_statements > 64) throw new NotSupportedException("Source block exceeds 64 statements.");
                ExpectName(owner + ".AppendChild"); Expect("("); ExpectName(name); Expect(")"); Expect(";");
                if (ReferencesMessage(local, owner) || ReferencesMessage(ns, owner))
                    throw new NotSupportedException("Root creation cannot read the empty document being replaced.");
                NativeExpression.Validate(local); NativeExpression.Validate(ns);
                nodes.Add(new() { Id = id + "-root-" + nodes.Count, Kind = NativeFlowNodeKind.CreateXmlRoot, MessageName = owner, Expression = local, NamespaceExpression = ns });
                pendingEmptyDocument = null; continue;
            }
            if (pendingEmptyDocument is not null && name != pendingEmptyDocument && document != pendingEmptyDocument)
                throw new NotSupportedException("An empty XmlDocument constructor must be immediately replaced by an exact copy, LoadXml or complete root construction; empty DOM behavior is unsupported.");
            const string append = ".DocumentElement.AppendChild";
            if (name.EndsWith(append, StringComparison.Ordinal) && _documents.Contains(name[..^append.Length]))
            {
                var owner = name[..^append.Length];
                Expect("("); ExpectName(owner + ".ImportNode"); Expect("(");
                if (_kind != "name" || !_token.EndsWith(".FirstChild", StringComparison.Ordinal) || !_messages.Contains(_token[..^11]))
                    throw new NotSupportedException("Native append requires FirstChild of an exact declared message.");
                var source = _token[..^11]; Next(); Expect(","); ExpectName("true"); Expect(")"); Expect(")"); Expect(";");
                nodes.Add(new() { Id = id + "-append-" + nodes.Count, Kind = NativeFlowNodeKind.AppendXmlMessage, MessageName = owner, SourceMessage = source });
                continue;
            }
            if (document is not null && _documents.Contains(document))
            {
                Expect("("); var xml = Read(); Expect(")"); Expect(";"); NativeExpression.Validate(xml);
                if (pendingEmptyDocument is not null && ReferencesMessage(xml, pendingEmptyDocument))
                    throw new NotSupportedException("LoadXml cannot read the empty XmlDocument being replaced; no previous native snapshot is substituted.");
                nodes.Add(new() { Id = id + "-create-" + nodes.Count, Kind = NativeFlowNodeKind.CreateXmlMessage, MessageName = document, Expression = xml });
                pendingEmptyDocument = null; continue;
            }
            if (_messages.Contains(name))
            {
                if (_token == "(")
                {
                    if (pendingEmptyDocument is not null) throw new NotSupportedException("Context assignment cannot reuse a previous snapshot of the empty document being replaced.");
                    var context = ReadContext(name);
                    Expect("="); var expression = Read(); Expect(";"); NativeExpression.Validate(expression);
                    nodes.Add(new() { Id = id + "-context-" + nodes.Count, Kind = NativeFlowNodeKind.SetMessageValue, Target = context, Expression = expression });
                    continue;
                }
                Expect("=");
                if (_kind == "name" && _token == "new" && _documents.Contains(name))
                {
                    Next();
                    if (_kind != "name" || _token != "System.Xml.XmlDocument") throw new NotSupportedException("Only the exact parameterless XmlDocument constructor is recognized.");
                    Next(); Expect("("); Expect(")"); Expect(";"); pendingEmptyDocument = name; continue;
                }
                if (_kind != "name" || !_messages.Contains(_token) || _token == pendingEmptyDocument)
                    throw new NotSupportedException("Message copy requires a declared initialized source; transforms/context/empty DOM behavior require explicit native operations.");
                var source = _token; Next(); Expect(";");
                nodes.Add(new() { Id = id + "-read-" + nodes.Count, Kind = NativeFlowNodeKind.UseMessage, MessageName = source });
                nodes.Add(new() { Id = id + "-save-" + nodes.Count, Kind = NativeFlowNodeKind.SaveMessage, MessageName = name });
                pendingEmptyDocument = null; continue;
            }
            if (name.Contains('.') && _variables.ContainsKey(name)) throw new NotSupportedException("Exception details are read-only in native flows; assignment to '" + name + "' is not translated.");
            if (_variables.ContainsKey(name))
            {
                NativeExpression expression;
                if (_token is "++" or "--")
                {
                    var operation = _token == "++" ? NativeExpressionKind.Add : NativeExpressionKind.Subtract; Next();
                    expression = new() { Kind = operation, Operands = [new() { Kind = NativeExpressionKind.Variable, Variable = name }, new() { Literal = new(NativeScalarType.Int32, "1") }] };
                }
                else
                {
                    var add = Take("+="); if (!add) Expect("=");
                    if (!add && ReadUdfCall(name, id + "-udf-" + nodes.Count) is { } call) { nodes.Add(call); continue; }
                    expression = Read();
                    if (add) expression = new() { Kind = NativeExpressionKind.Add, Operands = [new() { Kind = NativeExpressionKind.Variable, Variable = name }, expression] };
                }
                Expect(";"); NativeExpression.Validate(expression);
                nodes.Add(new() { Id = id + "-set-" + nodes.Count, Kind = NativeFlowNodeKind.SetVariable, Variable = name, Expression = expression });
                continue;
            }
            var target = _messageCatalog?.Resolve(name) ?? throw new NotSupportedException("Source assignment has no declared scalar or exact schema-field target.");
            Expect("="); var value = Read(); Expect(";"); NativeExpression.Validate(value);
            nodes.Add(new() { Id = id + "-write-" + nodes.Count, Kind = NativeFlowNodeKind.SetMessageValue, Target = target, Expression = value });
        }
        if (pendingEmptyDocument is not null) throw new NotSupportedException("Empty XmlDocument behavior has no native message equivalent; supply XML or copy a message explicitly.");
        return nodes;
    }
    private NativeFlowNode? ReadUdfCall(string target, string id)
    {
        if (_kind != "name") return null;
        var match = _udfs.FirstOrDefault(pair => _token == pair.Key || _token.EndsWith("." + pair.Key, StringComparison.Ordinal));
        if (match.Value is not { } udf) return null;
        var method = _token;
        if (_variables[target] != NativeScalarType.String) throw new NotSupportedException(method + " returns a String; assignment target '" + target + "' is not a declared String variable.");
        Next(); Expect("(");
        var arguments = new List<NativeExpression>();
        do
        {
            if (arguments.Count == udf.Arguments) throw new NotSupportedException(method + " takes " + udf.Arguments + " argument(s).");
            var argument = Read(); RequireType(argument, method + " argument", NativeScalarType.String); NativeExpression.Validate(argument); arguments.Add(argument);
        } while (Take(","));
        Expect(")"); Expect(";");
        if (arguments.Count != udf.Arguments) throw new NotSupportedException(method + " takes " + udf.Arguments + " argument(s).");
        return new() { Id = id, Name = method, Kind = NativeFlowNodeKind.InvokeTransform, Module = udf.Module, Settings = new Dictionary<string, string>(udf.Settings), Variable = target, TransformInputs = arguments };
    }
    private NativeExpression Read(int precedence = 0)
    {
        if (++_depth > 32 || ++_values > 128) throw new NotSupportedException("Source scalar expression exceeds its 32-level/128-value limit.");
        try
        {
            NativeExpression left;
            if (Take("!")) left = new() { Kind = NativeExpressionKind.Not, Operands = [Read(7)] };
            else if (Take("-")) left = new() { Kind = NativeExpressionKind.Negate, Operands = [Read(7)] };
            else if (Take("(")) { left = Read(); Expect(")"); }
            else if (_kind == "name" && _configuration is not null && _configuration.TryResolve(_token, out var configuredValue))
            {
                Next(); Expect("("); Expect(")"); left = new() { Literal = NativeScalar.From(configuredValue) };
            }
            else if (_kind == "name" && _token == "System.Convert.ToString")
            {
                Next(); Expect("(");
                if (_kind == "name" && _token == "Microsoft.XLANGs.Core.Service.RootService.InstanceId") { Next(); left = new() { Kind = NativeExpressionKind.InstanceId }; }
                else
                {
                    var operand = Read(); RequireType(operand, "Convert.ToString", NativeScalarType.String, NativeScalarType.Int32, NativeScalarType.Boolean);
                    left = new() { Kind = NativeExpressionKind.ToText, Operands = [operand] };
                }
                Expect(")");
            }
            else if (_kind == "name" && _token == "System.Convert.ToInt32")
            {
                Next(); Expect("(");
                var direct = _kind == "name" && _token == "xpath";
                left = Read(); Expect(")");
                if (direct)
                {
                    if (left is not { Kind: NativeExpressionKind.MessageValue, Fact.Kind: NativeMessageFactKind.XmlCount })
                        throw new NotSupportedException("Only explicit ToInt32 of a bounded XPath count is translated; other XPath conversions require reviewed native semantics.");
                }
                else
                {
                    RequireType(left, "Convert.ToInt32", NativeScalarType.String, NativeScalarType.Int32, NativeScalarType.Boolean);
                    left = new() { Kind = NativeExpressionKind.ToInt32, Operands = [left] };
                }
            }
            else if (_kind == "name" && ReadStringCall() is { } call) left = call;
            else if (_kind == "name" && _token == "xpath")
            {
                Next(); Expect("(");
                if (_kind != "name" || !_messages.Contains(_token)) throw new NotSupportedException("XPath requires an explicitly declared named message.");
                var message = _token; Next(); Expect(",");
                if (_kind != "string") throw new NotSupportedException("XPath requires a constant supported path, not runtime source evaluation.");
                var fact = NativeSourceXmlFactReader.Read(message, JsonSerializer.Deserialize<string>(_token)!);
                Next(); Expect(")"); left = new() { Kind = NativeExpressionKind.MessageValue, Fact = fact };
            }
            else if (_kind == "string") { left = new() { Literal = new(NativeScalarType.String, JsonSerializer.Deserialize<string>(_token)!) }; Next(); }
            else if (_kind == "number")
            {
                if (!int.TryParse(_token, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw new NotSupportedException("Source integer exceeds Int32 bounds.");
                left = new() { Literal = NativeScalar.From(number) }; Next();
            }
            else if (_kind == "name" && _token is "true" or "false" or "null")
            { left = new() { Literal = _token == "null" ? new(NativeScalarType.Null, "") : new(NativeScalarType.Boolean, _token) }; Next(); }
            else if (_kind == "name" && _variables.ContainsKey(_token))
            { left = new() { Kind = NativeExpressionKind.Variable, Variable = NativeVariableName(_token) }; Next(); }
            else if (_kind == "name" && _messages.Contains(_token))
            {
                var message = _token; Next();
                left = new() { Kind = NativeExpressionKind.MessageValue, Fact = ReadContext(message) };
            }
            else if (_kind == "name" && _token.Contains('.') && _messageCatalog is not null)
            { left = new() { Kind = NativeExpressionKind.MessageValue, Fact = _messageCatalog.Resolve(_token) }; Next(); }
            else throw new NotSupportedException("Source scalar operand is not a literal, declared native variable or proven schema member.");
            while (_kind == "symbol" && Priority(_token) > precedence)
            {
                var operation = _token; var priority = Priority(operation); Next();
                left = new() { Kind = Operation(operation), Operands = [left, Read(priority)] };
            }
            return left;
        }
        finally { _depth--; }
    }
    private NativeScalarType? TypeOf(NativeExpression expression) => expression.Kind switch
    {
        NativeExpressionKind.Literal => expression.Literal!.Type,
        NativeExpressionKind.Variable => _variables.TryGetValue(expression.Variable!, out var type) ? type : null,
        NativeExpressionKind.MessageValue => expression.Fact!.Kind switch
        {
            NativeMessageFactKind.Metadata or NativeMessageFactKind.XmlText or NativeMessageFactKind.XmlString => NativeScalarType.String,
            NativeMessageFactKind.XmlInt32 or NativeMessageFactKind.MetadataInt32 or NativeMessageFactKind.XmlCount => NativeScalarType.Int32,
            _ => NativeScalarType.Boolean
        },
        NativeExpressionKind.InstanceId or NativeExpressionKind.Substring or NativeExpressionKind.SubstringLength or NativeExpressionKind.ToText => NativeScalarType.String,
        NativeExpressionKind.Not or NativeExpressionKind.And or NativeExpressionKind.Or or NativeExpressionKind.Equal or NativeExpressionKind.NotEqual
            or NativeExpressionKind.Less or NativeExpressionKind.LessOrEqual or NativeExpressionKind.Greater or NativeExpressionKind.GreaterOrEqual
            or NativeExpressionKind.RegexIsMatch => NativeScalarType.Boolean,
        NativeExpressionKind.Add => TypeOf(expression.Operands[0]) is { } first && TypeOf(expression.Operands[1]) is { } second
            ? first == NativeScalarType.String || second == NativeScalarType.String ? NativeScalarType.String : first == NativeScalarType.Int32 && second == NativeScalarType.Int32 ? NativeScalarType.Int32 : null : null,
        _ => NativeScalarType.Int32
    };
    private void RequireType(NativeExpression expression, string operation, params NativeScalarType[] allowed)
    {
        if (TypeOf(expression) is not { } type || !allowed.Contains(type))
            throw new NotSupportedException(operation + " requires an operand whose scalar type is statically proven (" + string.Join("/", allowed) + "); no implicit CLR coercion is inferred.");
    }
    private NativeExpression? ReadStringCall()
    {
        var token = _token;
        if (token is "System.Text.RegularExpressions.Regex.IsMatch" or "Regex.IsMatch")
        {
            Next(); Expect("("); var input = Read(); Expect(","); var pattern = Read();
            if (_kind == "symbol" && _token == ",") throw new NotSupportedException("Regex.IsMatch option/timeout overloads are not translated; native matching uses no options.");
            Expect(")");
            RequireType(input, "Regex.IsMatch", NativeScalarType.String); RequireType(pattern, "Regex.IsMatch", NativeScalarType.String);
            return new() { Kind = NativeExpressionKind.RegexIsMatch, Operands = [input, pattern] };
        }
        var dot = token.LastIndexOf('.');
        if (dot <= 0 || token[(dot + 1)..] is not ("Length" or "IndexOf" or "LastIndexOf" or "Substring")) return null;
        var member = token[(dot + 1)..];
        NativeExpression receiver;
        var prefix = token[..dot];
        if (_variables.TryGetValue(prefix, out var variableType))
            receiver = new() { Kind = NativeExpressionKind.Variable, Variable = NativeVariableName(prefix) };
        else if (_messageCatalog is not null && prefix.Contains('.'))
        {
            try { receiver = new() { Kind = NativeExpressionKind.MessageValue, Fact = _messageCatalog.Resolve(prefix) }; }
            catch (NotSupportedException) { return null; }
        }
        else return null;
        RequireType(receiver, "String " + member, NativeScalarType.String);
        Next();
        if (member == "Length") return new() { Kind = NativeExpressionKind.Length, Operands = [receiver] };
        Expect("(");
        var first = Read();
        if (member is "IndexOf" or "LastIndexOf")
        {
            if (first.Kind != NativeExpressionKind.Literal || first.Literal!.Type != NativeScalarType.String || first.Literal.Value.Length == 0 || first.Literal.Value.Any(ch => ch < ' ' || ch > '~'))
                throw new NotSupportedException("IndexOf/LastIndexOf import only a non-empty printable-ASCII constant string; the legacy default is culture-sensitive and native search is ordinal.");
            if (_kind == "symbol" && _token == ",") throw new NotSupportedException("IndexOf/LastIndexOf start-index and comparison overloads are not translated.");
            Expect(")");
            return new() { Kind = member == "IndexOf" ? NativeExpressionKind.IndexOf : NativeExpressionKind.LastIndexOf, Operands = [receiver, first] };
        }
        RequireType(first, "String Substring", NativeScalarType.Int32);
        if (Take(","))
        {
            var length = Read(); Expect(")"); RequireType(length, "String Substring", NativeScalarType.Int32);
            return new() { Kind = NativeExpressionKind.SubstringLength, Operands = [receiver, first, length] };
        }
        Expect(")");
        return new() { Kind = NativeExpressionKind.Substring, Operands = [receiver, first] };
    }
    private static bool ReferencesMessage(NativeExpression expression, string name)
        => expression.Fact?.MessageName == name || expression.Operands.Any(operand => ReferencesMessage(operand, name));
    private static int Priority(string value) => value switch { "||" => 1, "&&" => 2, "==" or "!=" => 3, "<" or "<=" or ">" or ">=" => 4, "+" or "-" => 5, "*" or "/" or "%" => 6, _ => 0 };
    private static NativeExpressionKind Operation(string value) => value switch
    {
        "||" => NativeExpressionKind.Or, "&&" => NativeExpressionKind.And, "==" => NativeExpressionKind.Equal, "!=" => NativeExpressionKind.NotEqual,
        "<" => NativeExpressionKind.Less, "<=" => NativeExpressionKind.LessOrEqual, ">" => NativeExpressionKind.Greater, ">=" => NativeExpressionKind.GreaterOrEqual,
        "+" => NativeExpressionKind.Add, "-" => NativeExpressionKind.Subtract, "*" => NativeExpressionKind.Multiply, "/" => NativeExpressionKind.Divide, "%" => NativeExpressionKind.Remainder,
        _ => throw new NotSupportedException("Unsupported source scalar operator.")
    };
}
