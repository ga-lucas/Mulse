using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mulse.Modules.Orchestration;

/// <summary>Compiles a bounded, typed expression subset, never arbitrary CLR calls or source assemblies.</summary>
public static class OdxExpressionCompiler
{
    public static OdxCondition CompileCondition(string source)
    {
        var parser = new Parser(source);
        var expression = parser.Expression();
        parser.End();
        return (_, frame, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(Boolean(expression(frame))); };
    }

    public static OdxActivity CompileStatements(string source)
    {
        var parser = new Parser(source);
        var assignments = parser.Statements();
        return (_, frame, ct) =>
        {
            foreach (var assignment in assignments) { ct.ThrowIfCancellationRequested(); assignment(frame); }
            return Task.CompletedTask;
        };
    }

    private delegate object? Value(OdxExecutionFrame frame);
    private static bool Boolean(object? value) => value is bool boolean ? boolean : throw new InvalidOperationException("ODX condition requires a Boolean value; no implicit coercion is performed.");
    private static int Integer(object? value) => value is int number ? number : throw new InvalidOperationException("ODX arithmetic requires Int32 operands.");
    private static string Text(object? value) => value as string ?? throw new InvalidOperationException("ODX string operation requires a non-null string.");
    private static object? Scalar(JsonNode? value) => value switch
    {
        null => null,
        JsonValue scalar when scalar.TryGetValue<bool>(out var boolean) => boolean,
        JsonValue scalar when scalar.TryGetValue<int>(out var number) => number,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
        _ => throw new InvalidOperationException("ODX expressions require a scalar string, Boolean, Int32 or null fact.")
    };

    private static object? Read(OdxExecutionFrame frame, string path)
    {
        var parts = path.Split('.');
        if (!frame.Variables.TryGetValue(parts[0], out var value))
            throw new InvalidOperationException($"ODX variable/message fact '{parts[0]}' has not been initialized. XML/schema adapters must explicitly supply typed facts.");
        foreach (var part in parts.Skip(1))
        {
            if (value is not JsonObject record) throw new InvalidOperationException($"ODX member access '{path}' requires a non-null object.");
            value = record[part];
        }
        return Scalar(value);
    }

    private static void Write(OdxExecutionFrame frame, string path, object? value)
    {
        var parts = path.Split('.');
        var json = value switch { null => null, string text => JsonValue.Create(text), bool boolean => JsonValue.Create(boolean), int number => JsonValue.Create(number), _ => throw new InvalidOperationException("Unsupported ODX assignment value.") };
        if (parts.Length == 1)
        {
            if (!frame.Variables.ContainsKey(path)) throw new InvalidOperationException($"ODX assignment targets undeclared variable '{path}'.");
            frame.Variables[path] = json;
            return;
        }
        if (!frame.Variables.TryGetValue(parts[0], out var parent)) throw new InvalidOperationException($"ODX assignment targets unknown message fact '{parts[0]}'.");
        foreach (var part in parts.Skip(1).SkipLast(1))
            parent = parent is JsonObject record ? record[part] : throw new InvalidOperationException($"ODX assignment '{path}' requires an initialized object.");
        if (parent is not JsonObject target) throw new InvalidOperationException($"ODX assignment '{path}' requires an initialized object.");
        target[parts[^1]] = json;
    }

    private static object? Binary(string op, object? left, object? right) => op switch
    {
        "==" => Equal(left, right), "!=" => !Equal(left, right),
        "+" when left is string || right is string => Format(left) + Format(right),
        "+" => checked(Integer(left) + Integer(right)), "-" => checked(Integer(left) - Integer(right)),
        "*" => checked(Integer(left) * Integer(right)), "/" => Integer(left) / Integer(right), "%" => Integer(left) % Integer(right),
        "<" => Integer(left) < Integer(right), "<=" => Integer(left) <= Integer(right), ">" => Integer(left) > Integer(right), ">=" => Integer(left) >= Integer(right),
        _ => throw new InvalidOperationException("Unsupported ODX binary operation.")
    };
    private static bool Equal(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.GetType() != right.GetType()) throw new InvalidOperationException("ODX equality requires operands of matching scalar types.");
        return left.Equals(right);
    }
    private static string Format(object? value) => value switch
    {
        null => "", string text => text, bool boolean => boolean ? "True" : "False", int number => number.ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("Unsupported ODX string conversion.")
    };

    private sealed class Parser
    {
        private readonly string _source;
        private int _offset;
        private int _tokens;
        private int _depth;
        private int _operations;
        private string _token = "";
        private object? _literal;
        private bool _isString;

        public Parser(string source)
        {
            if (string.IsNullOrWhiteSpace(source) || source.Length > 65536) throw new NotSupportedException("ODX expressions require 1-65536 characters.");
            _source = source;
            Next();
        }
        public void End() { if (_token != "") Unsupported(); }
        private void Unsupported() => throw new NotSupportedException($"ODX expression syntax is unsupported near character {_offset}. No source statement was executed.");
        private void Expect(string token) { if (_isString || _token != token) Unsupported(); Next(); }
        private bool Take(string token) { if (_isString || _token != token) return false; Next(); return true; }
        private static bool Identifier(string text) => text.Split('.').All(part => part.Length > 0 && (char.IsAsciiLetter(part[0]) || part[0] == '_') && part.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'));
        public IReadOnlyList<Action<OdxExecutionFrame>> Statements()
        {
            var statements = new List<Action<OdxExecutionFrame>>();
            while (_token != "")
            {
                if (_isString || !Identifier(_token)) Unsupported();
                var target = _token; Next();
                if (Take("++")) { Expect(";"); statements.Add(frame => Write(frame, target, checked(Integer(Read(frame, target)) + 1))); continue; }
                if (Take("--")) { Expect(";"); statements.Add(frame => Write(frame, target, checked(Integer(Read(frame, target)) - 1))); continue; }
                var add = Take("+=");
                if (!add) Expect("=");
                var value = Expression(); Expect(";");
                statements.Add(frame => Write(frame, target, add ? Binary("+", Read(frame, target), value(frame)) : value(frame)));
            }
            if (statements.Count == 0) Unsupported();
            return statements;
        }
        public Value Expression(int precedence = 0)
        {
            if (++_depth > 64) throw new NotSupportedException("ODX expression nesting exceeds 64 levels.");
            try
            {
                Value left = Primary();
                while (!_isString && Priority(_token) is var priority && priority > precedence)
                {
                    if (++_operations > 256) throw new NotSupportedException("ODX expression exceeds 256 binary operations.");
                    var op = _token; Next(); var right = Expression(priority); var previous = left;
                    left = op switch
                    {
                        "&&" => frame => Boolean(previous(frame)) && Boolean(right(frame)),
                        "||" => frame => Boolean(previous(frame)) || Boolean(right(frame)),
                        _ => frame => Binary(op, previous(frame), right(frame))
                    };
                }
                return left;
            }
            finally { _depth--; }
        }
        private static int Priority(string op) => op switch { "||" => 1, "&&" => 2, "==" or "!=" => 3, "<" or "<=" or ">" or ">=" => 4, "+" or "-" => 5, "*" or "/" or "%" => 6, _ => 0 };
        private Value Primary()
        {
            if (Take("!")) { var value = Expression(6); return frame => !Boolean(value(frame)); }
            if (Take("-")) { var value = Expression(6); return frame => checked(-Integer(value(frame))); }
            if (Take("(")) { var value = Expression(); Expect(")"); return value; }
            if (_isString || _literal is int || _token is "null" or "true" or "false")
            {
                var value = _isString || _literal is int ? _literal : _token switch { "true" => true, "false" => false, _ => (object?)null };
                Next(); return _ => value;
            }
            var name = _token;
            if (!Identifier(name)) Unsupported();
            Next();
            if (!Take("(")) return frame => Read(frame, name);
            if (name is "System.Text.RegularExpressions.Regex.IsMatch" or "Regex.IsMatch")
            {
                var input = Expression(); Expect(","); var pattern = Expression(); Expect(")");
                return frame => Regex.IsMatch(Text(input(frame)), Text(pattern(frame)), RegexOptions.None, TimeSpan.FromMilliseconds(100));
            }
            // A promoted-property reference is the sole supported message invocation, not a CLR call.
            if (!name.Contains('.') && !_isString && Identifier(_token) && _token.Contains('.'))
            {
                var property = _token; Next(); Expect(")");
                return frame =>
                {
                    if (!frame.Messages.TryGetValue(name, out var message) || message.Count != 1)
                        throw new InvalidOperationException($"Promoted-property access requires one initialized message '{name}'.");
                    if (!message.Payloads[0].Metadata.TryGetValue(property, out var value))
                        throw new InvalidOperationException($"Message '{name}' has no promoted property '{property}'.");
                    return value;
                };
            }
            Unsupported(); return null!;
        }
        private void Next()
        {
            if (++_tokens > 8192) throw new NotSupportedException("ODX expression exceeds 8192 tokens.");
            _isString = false; _literal = null;
            while (_offset < _source.Length)
            {
                if (char.IsWhiteSpace(_source[_offset])) { _offset++; continue; }
                if (_source.AsSpan(_offset).StartsWith("//")) { while (_offset < _source.Length && _source[_offset] != '\n') _offset++; continue; }
                if (_source.AsSpan(_offset).StartsWith("/*"))
                {
                    var end = _source.IndexOf("*/", _offset + 2, StringComparison.Ordinal); if (end < 0) Unsupported(); _offset = end + 2; continue;
                }
                break;
            }
            if (_offset == _source.Length) { _token = ""; return; }
            var start = _offset; var ch = _source[_offset++];
            if (ch == '"')
            {
                var text = new StringBuilder(); var closed = false;
                while (_offset < _source.Length)
                {
                    var c = _source[_offset++];
                    if (c == '"') { closed = true; break; }
                    if (c is '\r' or '\n') Unsupported();
                    if (c == '\\')
                    {
                        if (_offset == _source.Length) Unsupported();
                        c = _source[_offset++];
                        c = c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', '"' => '"', '0' => '\0', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', '\'' => '\'', _ => throw new NotSupportedException("Unsupported ODX string escape.") };
                    }
                    text.Append(c);
                }
                if (!closed) Unsupported();
                _token = "string"; _literal = text.ToString(); _isString = true; return;
            }
            if (char.IsAsciiLetter(ch) || ch == '_')
            {
                while (_offset < _source.Length && (char.IsAsciiLetterOrDigit(_source[_offset]) || _source[_offset] is '_' or '.')) _offset++;
                _token = _source[start.._offset]; return;
            }
            if (char.IsAsciiDigit(ch))
            {
                while (_offset < _source.Length && char.IsAsciiDigit(_source[_offset])) _offset++;
                _token = _source[start.._offset];
                if (!int.TryParse(_token, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) Unsupported();
                _literal = number; return;
            }
            _token = ch.ToString();
            if (_offset < _source.Length && _source.Substring(start, 2) is "==" or "!=" or "<=" or ">=" or "&&" or "||" or "++" or "--" or "+=")
            { _token = _source.Substring(start, 2); _offset++; }
        }
    }
}
