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

    [Theory]
    [InlineData("MathAbs", "-3.5", "", "3.5")]
    [InlineData("MathAbs", "abc", "", "")]
    [InlineData("MathAdd", "1.5", "2", "3.5")]
    [InlineData("MathAdd", "1", "x", "")]
    [InlineData("MathSubtract", "10", "4", "6")]
    [InlineData("MathMultiply", "1.5", "4", "6")]
    [InlineData("LogicalGte", "2", "2", "true")]
    [InlineData("LogicalGte", "1", "2", "false")]
    [InlineData("LogicalLt", "9", "10", "true")]
    [InlineData("LogicalLt", "b", "a", "false")]
    [InlineData("LogicalLte", "2", "2", "true")]
    [InlineData("LogicalLte", "3", "2", "false")]
    [InlineData("StringTrimLeft", "  a ", "", "a ")]
    [InlineData("StringTrimRight", "  a ", "", "  a")]
    public void BizTalkFunctoidFunctions_Work(string name, string first, string second, string expected)
    {
        var function = Enum.Parse<XmlMappingFunction>(name);
        var arguments = XmlMappingFunctionCatalog.Arity(function).Min == 1 ? new[] { Const(first) } : [Const(first), Const(second)];
        Assert.Equal(expected, RunExpression(Fn(function, arguments)));
    }

    [Fact]
    public void VariadicMath_FoldsAllArguments()
    {
        Assert.Equal("6", RunExpression(Fn(XmlMappingFunction.MathAdd, Const("1"), Const("2"), Const("3"))));
        Assert.Equal("5", RunExpression(Fn(XmlMappingFunction.MathSubtract, Const("10"), Const("3"), Const("2"))));
        Assert.Equal("24", RunExpression(Fn(XmlMappingFunction.MathMultiply, Const("2"), Const("3"), Const("4"))));
        Assert.Equal("", RunExpression(Fn(XmlMappingFunction.MathAdd, Const("1"), Const("x"), Const("3"))));
    }

    [Fact]
    public void OnlyCompiledHelperFunctoids_AreMarkedVerified()
    {
        Assert.True(XmlMappingFunctionCatalog.HelperVerified(XmlMappingFunction.MathAbs));
        Assert.True(XmlMappingFunctionCatalog.HelperVerified(XmlMappingFunction.StringConcat));
        Assert.False(XmlMappingFunctionCatalog.HelperVerified(XmlMappingFunction.MathAdd));
        Assert.False(XmlMappingFunctionCatalog.HelperVerified(XmlMappingFunction.LogicalOr));
        Assert.False(XmlMappingFunctionCatalog.HelperVerified(XmlMappingFunction.Conditional));
    }

    [Fact]
    public void FieldCondition_WritesTheTargetOnlyWhenExactlyTrue()
    {
        var map = new XmlMappingDefinition
        {
            Version = 2, TargetRoot = "Output",
            Fields = [new() { TargetPath = "Result", Expression = Src("/Input/Value"), Condition = Fn(XmlMappingFunction.LogicalEq, Src("/Input/Kind"), Const("A")) }]
        };
        var engine = new XmlMappingEngine(XmlMappingTests.Json(map));
        Assert.Equal("1", XDocument.Parse(engine.Transform("<Input><Kind>A</Kind><Value>1</Value></Input>")).Root!.Element("Result")!.Value);
        Assert.Null(XDocument.Parse(engine.Transform("<Input><Kind>B</Kind><Value>1</Value></Input>")).Root!.Element("Result"));
        Assert.Contains("= 'true'", engine.Xslt);
    }

    [Fact]
    public void FieldCondition_ExpandsCustomFunctionsAndNeedsAnExpression()
    {
        var map = new XmlMappingDefinition
        {
            Version = 2, TargetRoot = "Output",
            CustomFunctions = [Function("IsA", ["x"], Fn(XmlMappingFunction.LogicalEq, Param("x"), Const("A")))],
            Fields = [new() { TargetPath = "Result", Expression = Const("ok"), Condition = Call("IsA", Src("/Input/Kind")) }]
        };
        var engine = new XmlMappingEngine(XmlMappingTests.Json(map));
        Assert.DoesNotContain("Unimplemented", engine.Xslt);
        Assert.Equal("ok", XDocument.Parse(engine.Transform("<Input><Kind>A</Kind></Input>")).Root!.Element("Result")!.Value);
        Assert.Null(XDocument.Parse(engine.Transform("<Input><Kind>Z</Kind></Input>")).Root!.Element("Result"));

        var noExpression = new XmlMappingDefinition
        {
            Version = 2, TargetRoot = "Output",
            Fields = [new() { TargetPath = "Result", Constant = "x", Condition = Const("true") }]
        };
        Assert.Contains("condition", Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(noExpression))).Message);
        var version1 = noExpression with { Version = 1 };
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(version1)));
    }

    [Fact]
    public void FieldCondition_ReadingARepeatFieldMustSitInsideTheRepeat()
    {
        var map = new XmlMappingDefinition
        {
            Version = 2, TargetRoot = "Output",
            Repeats = [new() { SourcePath = "/Input/Row", TargetPath = "Row" }],
            Fields = [new() { TargetPath = "Row/Value", Expression = Src("/Input/Row/V"), Condition = Fn(XmlMappingFunction.LogicalGt, Src("/Input/Row/V"), Const("1")) }]
        };
        var output = XDocument.Parse(new XmlMappingEngine(XmlMappingTests.Json(map)).Transform("<Input><Row><V>1</V></Row><Row><V>2</V></Row></Input>")).Root!;
        Assert.Equal(2, output.Elements("Row").Count());
        Assert.Equal(["2"], output.Descendants("Value").Select(value => value.Value));
        var outside = map with { Fields = [new() { TargetPath = "Result", Expression = Const("x"), Condition = Fn(XmlMappingFunction.LogicalGt, Src("/Input/Row/V"), Const("1")) }] };
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(outside)));
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
    public void UnusedStub_StillFailsClosedUnlessStubsAreAllowed()
    {
        var json = XmlMappingTests.Json(Map(Const("x"), Function("Pending", ["a"], null)));
        Assert.Contains("unimplemented stub", Assert.Throws<ArgumentException>(() => new XmlMappingEngine(json)).Message, StringComparison.Ordinal);
        Assert.NotNull(new XmlMappingEngine(json, allowStubs: true).Xslt);
    }

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
