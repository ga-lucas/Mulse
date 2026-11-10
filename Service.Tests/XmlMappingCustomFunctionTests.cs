using System.Xml.Linq;
using Mulse.CompatibilityPack;

namespace Service.Tests;

public sealed class XmlMappingCustomFunctionTests
{
    private static XmlValueExpression Src(string path) => new() { Kind = XmlValueExpressionKind.Source, SourcePath = path };
    private static XmlValueExpression Const(string text) => new() { Kind = XmlValueExpressionKind.Constant, Constant = text };
    private static XmlValueExpression Param(string name) => new() { Kind = XmlValueExpressionKind.Parameter, Name = name };
    private static XmlValueExpression Call(string name, params XmlValueExpression[] arguments)
        => new() { Kind = XmlValueExpressionKind.Custom, Name = name, Arguments = arguments };
    private static XmlValueExpression Fn(XmlMappingFunction function, params XmlValueExpression[] arguments)
        => new() { Kind = XmlValueExpressionKind.Function, Function = function, Arguments = arguments };

    private static XmlCustomFunctionDefinition Function(string name, string[] parameters, XmlValueExpression? body) => new()
    {
        Name = name, Description = "test", Body = body,
        Parameters = parameters.Select(parameter => new XmlCustomFunctionParameter { Name = parameter }).ToList()
    };

    private static XmlMappingDefinition Map(XmlValueExpression expression, params XmlCustomFunctionDefinition[] functions) => new()
    {
        Version = 2, TargetRoot = "Output", CustomFunctions = functions,
        Fields = [new() { TargetPath = "Result", Expression = expression }]
    };

    private static string Run(XmlMappingDefinition map, string input = "<Input/>", TimeProvider? time = null)
        => XDocument.Parse(new XmlMappingEngine(XmlMappingTests.Json(map), time).Transform(input)).Root!.Element("Result")!.Value;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static string RunExpression(XmlValueExpression expression, string input = "<Input/>", TimeProvider? time = null)
        => Run(Map(expression), input, time);

    [Fact]
    public void NewGuid_ProducesDifferentValidGuids()
    {
        var first = RunExpression(Fn(XmlMappingFunction.NewGuid));
        Assert.True(Guid.TryParse(first, out _));
        Assert.NotEqual(first, RunExpression(Fn(XmlMappingFunction.NewGuid)));
    }

    [Fact]
    public void DateNow_UsesTheClockAndFormat()
        => Assert.Equal("202603041530", RunExpression(Fn(XmlMappingFunction.DateNow, Const("yyyyMMddHHmm")), time: new FixedClock(new(2026, 3, 4, 15, 30, 59, TimeSpan.Zero))));

    [Theory]
    [InlineData("20240131123456", "yyyyMMddHHmmss|yyyyMMddHHmm|yyyyMMdd", "yyyy-MM-ddTHH:mm:ss", "2024-01-31T12:34:56")]
    [InlineData("202401311234", "yyyyMMddHHmmss|yyyyMMddHHmm|yyyyMMdd", "yyyy-MM-ddTHH:mm:ss", "2024-01-31T12:34:00")]
    [InlineData("20240131", "yyyyMMddHHmmss|yyyyMMddHHmm|yyyyMMdd", "yyyy-MM-dd", "2024-01-31")]
    [InlineData("garbage", "yyyyMMdd", "yyyy-MM-dd", "")]
    [InlineData("2024-01-31", "", "yyyyMMdd", "20240131")]
    public void DateReformat_ParsesWithTheAllowedFormats(string text, string inputFormats, string outputFormat, string expected)
        => Assert.Equal(expected, RunExpression(Fn(XmlMappingFunction.DateReformat, Const(text), Const(outputFormat), Const(inputFormats))));

    [Theory]
    [InlineData("20240131", "true")]
    [InlineData("nope", "false")]
    public void DateValid_ReportsParseability(string text, string expected)
        => Assert.Equal(expected, RunExpression(Fn(XmlMappingFunction.DateValid, Const(text), Const("yyyyMMdd"))));

    [Theory]
    [InlineData("true", "a")]
    [InlineData("false", "b")]
    [InlineData("", "b")]
    public void Conditional_PicksABranch(string condition, string expected)
        => Assert.Equal(expected, RunExpression(Fn(XmlMappingFunction.Conditional, Const(condition), Const("a"), Const("b"))));

    [Fact]
    public void LogicalOrAndNot_Combine()
    {
        Assert.Equal("true", RunExpression(Fn(XmlMappingFunction.LogicalOr, Const("false"), Const("true"))));
        Assert.Equal("false", RunExpression(Fn(XmlMappingFunction.LogicalOr, Const("false"), Const("false"), Const("false"))));
        Assert.Equal("true", RunExpression(Fn(XmlMappingFunction.LogicalNot, Const("false"))));
        Assert.Equal("false", RunExpression(Fn(XmlMappingFunction.LogicalNot, Const("true"))));
    }

    [Fact]
    public void StringHelpers_Work()
    {
        Assert.Equal("abc", RunExpression(Fn(XmlMappingFunction.StringLowerCase, Const("AbC"))));
        Assert.Equal("a b", RunExpression(Fn(XmlMappingFunction.StringTrim, Const("  a b  "))));
        Assert.Equal("a-c", RunExpression(Fn(XmlMappingFunction.StringReplace, Const("a.c"), Const("."), Const("-"))));
        Assert.Equal("a.c", RunExpression(Fn(XmlMappingFunction.StringReplace, Const("a.c"), Const(""), Const("-"))));
        Assert.Equal("bc", RunExpression(Fn(XmlMappingFunction.StringSubstring, Const("abcd"), Const("1"), Const("2"))));
        Assert.Equal("", RunExpression(Fn(XmlMappingFunction.StringSubstring, Const("abcd"), Const("9"), Const("2"))));
        Assert.Equal("true", RunExpression(Fn(XmlMappingFunction.StringContains, Const("abcd"), Const("bc"))));
        Assert.Equal("false", RunExpression(Fn(XmlMappingFunction.StringContains, Const("abcd"), Const("x"))));
    }

    [Fact]
    public void CustomFunction_IsInlinedWithItsParameters()
    {
        var map = Map(Call("Shout", Src("/Input/Name")),
            Function("Shout", ["text"], Fn(XmlMappingFunction.StringConcat, Fn(XmlMappingFunction.StringUpperCase, Param("text")), Const("!"))));
        Assert.Equal("JOE!", Run(map, "<Input><Name>joe</Name></Input>"));
        Assert.DoesNotContain("Unimplemented", new XmlMappingEngine(XmlMappingTests.Json(map)).Xslt);
    }

    [Fact]
    public void CustomFunctions_CanCallEachOtherAndBeNested()
    {
        var map = Map(Call("Twice", Call("Twice", Const("ab"))),
            Function("Twice", ["x"], Fn(XmlMappingFunction.StringConcat, Param("x"), Param("x"))));
        Assert.Equal("abababab", Run(map));
        var chained = Map(Call("Outer", Const("a")),
            Function("Outer", ["x"], Call("Inner", Param("x"))),
            Function("Inner", ["y"], Fn(XmlMappingFunction.StringUpperCase, Param("y"))));
        Assert.Equal("A", Run(chained));
    }

    [Fact]
    public void Stub_FailsClosedByDefault_AndCompilesWhenStubsAreAllowed()
    {
        var map = Map(Call("Pending", Const("x")), Function("Pending", ["a"], null));
        var json = XmlMappingTests.Json(map);
        var error = Assert.Throws<ArgumentException>(() => new XmlMappingEngine(json));
        Assert.Contains("Pending", error.Message);
        Assert.Contains("stub", error.Message);
        var preview = new XmlMappingEngine(json, allowStubs: true);
        Assert.Contains("Unimplemented('Pending')", preview.Xslt);
        Assert.ThrowsAny<Exception>(() => preview.Transform("<Input/>"));
    }

    [Fact]
    public void UnusedStub_DoesNotBlockTheMap()
        => Assert.Equal("x", Run(Map(Const("x"), Function("Pending", ["a"], null))));

    [Fact]
    public void Cycles_AreRejected()
    {
        var self = Map(Const("x"), Function("Loop", ["a"], Call("Loop", Param("a"))));
        Assert.Contains("cycle", Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(self))).Message);
        var pair = Map(Const("x"),
            Function("A", [], Call("B")),
            Function("B", [], Call("A")));
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(pair)));
    }

    [Fact]
    public void Growth_IsLimited()
    {
        var functions = new List<XmlCustomFunctionDefinition> { Function("F0", ["x"], Fn(XmlMappingFunction.StringConcat, Param("x"), Param("x"), Param("x"), Param("x"))) };
        for (var i = 1; i < 8; i++)
            functions.Add(Function($"F{i}", ["x"], Fn(XmlMappingFunction.StringConcat, Call($"F{i - 1}", Param("x")), Call($"F{i - 1}", Param("x")), Call($"F{i - 1}", Param("x")), Call($"F{i - 1}", Param("x")))));
        var map = Map(Call("F7", Const("a")), functions.ToArray());
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(map)));
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("1abc")]
    [InlineData("StringLeft")]
    [InlineData("")]
    public void FunctionNames_MustBeSafeAndNotBuiltIn(string name)
        => Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(Map(Const("x"), Function(name, [], null)))));

    [Fact]
    public void DuplicateFunctionsAndParameters_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(Map(Const("x"), Function("A", [], null), Function("A", [], null)))));
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(Map(Const("x"), Function("A", ["p", "p"], null)))));
    }

    [Fact]
    public void Calls_MustMatchTheDeclaredArity_AndExist()
    {
        var wrong = Map(Call("One", Const("a"), Const("b")), Function("One", ["a"], Param("a")));
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(wrong)));
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(Map(Call("Missing")))));
    }

    [Fact]
    public void Bodies_CannotReadSourcesOrUndeclaredParameters()
    {
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(Map(Const("x"), Function("A", [], Src("/Input/A"))))));
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(Map(Const("x"), Function("A", ["a"], Param("b"))))));
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(Map(Param("a")))));
    }

    [Fact]
    public void CustomFunctions_RequireVersionTwo()
    {
        var map = Map(Const("x"), Function("A", [], null)) with { Version = 1, Fields = [], };
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(map)));
    }

    [Fact]
    public void Documentation_IsSizeLimited()
    {
        var map = Map(Const("x"), Function("A", [], null) with { OriginalSource = new string('x', XmlMappingFunctionCatalog.MaxOriginalSourceLength + 1) });
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(map)));
    }

    [Fact]
    public void CatalogDocumentsEveryFunction()
    {
        foreach (var function in Enum.GetValues<XmlMappingFunction>())
            Assert.False(string.IsNullOrWhiteSpace(XmlMappingFunctionCatalog.Summary(function)), function.ToString());
    }
}
