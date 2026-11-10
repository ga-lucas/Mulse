using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;

namespace Service.Tests;

public sealed class XmlMappingExpressionTests
{
    private static XmlValueExpression Src(string path) => new() { Kind = XmlValueExpressionKind.Source, SourcePath = path };
    private static XmlValueExpression Const(string text) => new() { Kind = XmlValueExpressionKind.Constant, Constant = text };
    private static XmlValueExpression Fn(XmlMappingFunction function, params XmlValueExpression[] arguments)
        => new() { Kind = XmlValueExpressionKind.Function, Function = function, Arguments = arguments };

    private static XmlMappingDefinition Map(XmlValueExpression expression, IReadOnlyList<XmlStringOperation>? operations = null) => new()
    {
        Version = 2, TargetRoot = "Output",
        Fields = [new() { TargetPath = "Result", Expression = expression, Operations = operations ?? [] }]
    };

    private static string Run(XmlValueExpression expression, string input = "<Input/>", TimeProvider? time = null)
        => XDocument.Parse(new XmlMappingEngine(XmlMappingTests.Json(Map(expression)), time).Transform(input)).Root!.Element("Result")!.Value;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Theory]
    [InlineData("abcdef", "3", "abc")]
    [InlineData("ab", "5", "ab")]
    [InlineData("abc", "2.9", "ab")]
    [InlineData("abc", "0", "")]
    [InlineData("abc", "-1", "")]
    [InlineData("abc", "x", "")]
    public void StringLeft_MatchesBizTalkHelper(string text, string count, string expected)
        => Assert.Equal(expected, Run(Fn(XmlMappingFunction.StringLeft, Const(text), Const(count))));

    [Theory]
    [InlineData("abcdef", "2", "ef")]
    [InlineData("ab", "5", "ab")]
    [InlineData("abc", "x", "")]
    public void StringRight_MatchesBizTalkHelper(string text, string count, string expected)
        => Assert.Equal(expected, Run(Fn(XmlMappingFunction.StringRight, Const(text), Const(count))));

    [Fact]
    public void StringSizeConcatAndUpperCase_ReadSourcesAndTreatMissingAsEmpty()
    {
        Assert.Equal("5", Run(Fn(XmlMappingFunction.StringSize, Src("/Input/A")), "<Input><A>hello</A></Input>"));
        Assert.Equal("0", Run(Fn(XmlMappingFunction.StringSize, Src("/Input/A"))));
        Assert.Equal("JOESmith", Run(Fn(XmlMappingFunction.StringConcat, Fn(XmlMappingFunction.StringUpperCase, Src("/Input/F")), Src("/Input/L"), Const("")),
            "<Input><F>joe</F><L>Smith</L></Input>"));
        Assert.Equal("AB ÉX", Run(Fn(XmlMappingFunction.StringConcat, Fn(XmlMappingFunction.StringUpperCase, Const("ab é")), Const("X"))));
    }

    [Theory]
    [InlineData("3.7", "3")]
    [InlineData("-3.2", "-4")]
    [InlineData("2.5", "2")]
    [InlineData("3.5", "3")]
    [InlineData("1,000.5", "1000")]
    [InlineData("abc", "")]
    [InlineData("99999999999", "")]
    public void MathInt_RoundsDown_AndYieldsEmptyWhenInvalid(string input, string expected)
        => Assert.Equal(expected, Run(Fn(XmlMappingFunction.MathInt, Const(input))));

    [Theory]
    [InlineData("6", "3", "2")]
    [InlineData("1", "3", "0.333333333333333")]
    [InlineData("1", "0", "")]
    [InlineData("x", "1", "")]
    public void MathDivide_UsesFifteenDigitsAndEmptyForInvalid(string left, string right, string expected)
        => Assert.Equal(expected, Run(Fn(XmlMappingFunction.MathDivide, Const(left), Const(right))));

    [Theory]
    [InlineData(XmlMappingFunction.LogicalEq, "1.0", "1", "true")]
    [InlineData(XmlMappingFunction.LogicalEq, "a", "A", "false")]
    [InlineData(XmlMappingFunction.LogicalEq, "a", "a", "true")]
    [InlineData(XmlMappingFunction.LogicalNe, "NB", "NB", "false")]
    [InlineData(XmlMappingFunction.LogicalNe, "1", "2", "true")]
    [InlineData(XmlMappingFunction.LogicalGt, "10", "9", "true")]
    [InlineData(XmlMappingFunction.LogicalGt, "b", "a", "true")]
    [InlineData(XmlMappingFunction.LogicalGt, "10", "9x", "false")]
    [InlineData(XmlMappingFunction.LogicalGt, "0", "0", "false")]
    public void Comparisons_AreNumericWhenBothNumericOtherwiseOrdinal(XmlMappingFunction function, string left, string right, string expected)
        => Assert.Equal(expected, Run(Fn(function, Const(left), Const(right))));

    [Theory]
    [InlineData("true", "1", "TRUE", "true")]
    [InlineData(" true ", "2", "true", "true")]
    [InlineData("true", "0", "true", "false")]
    [InlineData("true", "abc", "true", "false")]
    [InlineData("false", "true", "true", "false")]
    public void LogicalAnd_FoldsAllArguments(string a, string b, string c, string expected)
        => Assert.Equal(expected, Run(Fn(XmlMappingFunction.LogicalAnd, Const(a), Const(b), Const(c))));

    [Fact]
    public void LogicalExistence_ChecksTheNodeNotItsText()
    {
        var expression = Fn(XmlMappingFunction.LogicalExistence, Src("/Input/@Key"));
        Assert.Equal("true", Run(expression, "<Input Key=''/>"));
        Assert.Equal("false", Run(expression));
    }

    [Fact]
    public void Existence_Size_Gt_And_Ne_ComposeLikeTheBillingGuard()
    {
        var key = Src("/Input/@Key");
        var guard = Fn(XmlMappingFunction.LogicalAnd,
            Fn(XmlMappingFunction.LogicalExistence, key),
            Fn(XmlMappingFunction.LogicalNe, Fn(XmlMappingFunction.StringRight, key, Const("2")), Const("NB")),
            Fn(XmlMappingFunction.LogicalGt, Fn(XmlMappingFunction.StringSize, key), Const("0")));
        Assert.Equal("true", Run(guard, "<Input Key='ABC'/>"));
        Assert.Equal("false", Run(guard, "<Input Key='ABNB'/>"));
        Assert.Equal("false", Run(guard, "<Input Key=''/>"));
        Assert.Equal("false", Run(guard));
    }

    [Fact]
    public void CurrentDateFunctions_UseTheInjectedLocalClock()
    {
        var clock = new FixedClock(new DateTimeOffset(2024, 3, 5, 7, 8, 9, TimeSpan.Zero));
        Assert.Equal("2024-03-05", Run(Fn(XmlMappingFunction.DateCurrentDate), time: clock));
        Assert.Equal("2024-03-05T07:08:09", Run(Fn(XmlMappingFunction.DateCurrentDateTime), time: clock));
    }

    [Theory]
    [InlineData("it's")]
    [InlineData("say \"hi\"")]
    [InlineData("a'b\"c")]
    [InlineData("<&>")]
    public void Constants_SurviveQuotingAndMarkup(string text)
        => Assert.Equal(text + "!", Run(Fn(XmlMappingFunction.StringConcat, Const(text), Const("!"))));

    [Fact]
    public void ExpressionFields_AlwaysEmitAndAcceptStringOperations()
    {
        var map = Map(Fn(XmlMappingFunction.StringUpperCase, Src("/Input/A")), [new() { Kind = XmlStringOperationKind.Prefix, Argument = ">" }]);
        var engine = new XmlMappingEngine(XmlMappingTests.Json(map));
        Assert.Equal(">", XDocument.Parse(engine.Transform("<Input/>")).Root!.Element("Result")!.Value);
        Assert.Equal(">X", XDocument.Parse(engine.Transform("<Input><A>x</A></Input>")).Root!.Element("Result")!.Value);
    }

    [Fact]
    public void ExpressionsInsideRepeats_ReadTheirOwnRecordAndOuterValues()
    {
        var map = new XmlMappingDefinition
        {
            Version = 2, TargetRoot = "Output",
            Repeats = [new() { SourcePath = "/Input/Item", TargetPath = "Row" }],
            Fields =
            [
                new() { TargetPath = "Row/Label", Expression = Fn(XmlMappingFunction.StringConcat, Src("/Input/Prefix"), Fn(XmlMappingFunction.StringUpperCase, Src("/Input/Item/Name"))) },
                new() { SourcePath = "/Input/Item/Id", TargetPath = "Row/Id" }
            ]
        };
        var output = XDocument.Parse(new XmlMappingEngine(XmlMappingTests.Json(map))
            .Transform("<Input><Prefix>#</Prefix><Item><Id>1</Id><Name>a</Name></Item><Item><Id>2</Id><Name>b</Name></Item></Input>")).Root!;
        Assert.Equal(["#A", "#B"], output.Elements("Row").Select(row => row.Element("Label")!.Value));
    }

    [Fact]
    public void ExpressionReadingARepeatedSourceOutsideItsTarget_IsRejected()
    {
        var map = new XmlMappingDefinition
        {
            Version = 2, TargetRoot = "Output",
            Repeats = [new() { SourcePath = "/Input/Item", TargetPath = "Row" }],
            Fields =
            [
                new() { TargetPath = "Row/Id", SourcePath = "/Input/Item/Id" },
                new() { TargetPath = "Flat", Expression = Fn(XmlMappingFunction.StringUpperCase, Src("/Input/Item/Name")) }
            ]
        };
        Assert.Contains("not below the repeat's target", Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(map))).Message);
    }

    private static XmlMappingDefinition PositionMap(params XmlValueExpression[] extra) => new()
    {
        Version = 2, TargetRoot = "Output",
        Repeats = [new() { SourcePath = "/Input/Group", TargetPath = "G" }, new() { SourcePath = "/Input/Group/Item", TargetPath = "G/Row" }],
        Fields =
        [
            new() { TargetPath = "G/Num", Expression = Fn(XmlMappingFunction.RecordPosition, Src("/Input/Group")) },
            new() { TargetPath = "G/Row/Num", Expression = Fn(XmlMappingFunction.StringConcat, Fn(XmlMappingFunction.RecordPosition, Src("/Input/Group")), Const("."), Fn(XmlMappingFunction.RecordPosition, Src("/Input/Group/Item"))) },
            .. extra.Select((expression, index) => new XmlFieldMappingDefinition { TargetPath = $"G/Row/Extra{index}", Expression = expression })
        ]
    };

    [Fact]
    public void RecordPosition_IsOneBasedPerParentAndRestartsInNestedRepeats()
    {
        var output = XDocument.Parse(new XmlMappingEngine(XmlMappingTests.Json(PositionMap(Fn(XmlMappingFunction.MathAdd, Fn(XmlMappingFunction.RecordPosition, Src("/Input/Group/Item")), Const("10")))))
            .Transform("<Input><Group><Item/><Item/></Group><Group><Item/></Group></Input>")).Root!;
        Assert.Equal(["1", "2"], output.Elements("G").Select(group => group.Element("Num")!.Value));
        Assert.Equal(["1.1", "1.2", "2.1"], output.Descendants("Row").Select(row => row.Element("Num")!.Value));
        Assert.Equal(["11", "12", "11"], output.Descendants("Row").Select(row => row.Element("Extra0")!.Value));
    }

    [Fact]
    public void RecordPosition_NeedsARepeatingSourcePathThatEnclosesItsTarget()
    {
        Assert.Contains("repeating record", Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(Map(Fn(XmlMappingFunction.RecordPosition, Src("/Input/Other")))))).Message);
        Assert.Contains("repeating record", Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(Map(Fn(XmlMappingFunction.RecordPosition, Const("1")))))).Message);
        var outside = Map(Fn(XmlMappingFunction.RecordPosition, Src("/Input/Item"))) with { Repeats = [new() { SourcePath = "/Input/Item", TargetPath = "Row" }] };
        outside = outside with { Fields = [.. outside.Fields, new XmlFieldMappingDefinition { TargetPath = "Row/Id", Constant = "x" }] };
        Assert.Contains("not below the repeat's target", Assert.Throws<ArgumentException>(() => new XmlMappingEngine(XmlMappingTests.Json(outside))).Message);
    }

    [Fact]
    public void GeneratedStylesheet_DeclaresTheFunctionNamespaceOnlyWhenUsed()
    {
        var used = new XmlMappingEngine(XmlMappingTests.Json(Map(Fn(XmlMappingFunction.StringSize, Const("a"))))).Xslt;
        Assert.Contains(XmlMappingFunctions.Namespace, used);
        var plain = new XmlMappingEngine(XmlMappingTests.Json(new XmlMappingDefinition { TargetRoot = "O", Fields = [new() { Constant = "x", TargetPath = "A" }] })).Xslt;
        Assert.DoesNotContain(XmlMappingFunctions.Namespace, plain);
    }

    [Fact]
    public void Definitions_RoundTripThroughJsonWithNamedFunctions()
    {
        var json = XmlMappingTests.Json(Map(Fn(XmlMappingFunction.StringLeft, Src("/Input/A"), Const("2"))));
        Assert.Contains("\"StringLeft\"", json);
        Assert.Equal("ab", XDocument.Parse(new XmlMappingEngine(json).Transform("<Input><A>abc</A></Input>")).Root!.Element("Result")!.Value);
    }

    public static IEnumerable<object[]> InvalidDefinitions()
    {
        yield return [Map(Fn(XmlMappingFunction.StringLeft, Const("a"))), "takes 2"];
        yield return [Map(Fn(XmlMappingFunction.StringConcat, Const("a"))), "takes 2 to 32"];
        yield return [Map(Fn(XmlMappingFunction.DateCurrentDate, Const("a"))), "takes 0"];
        yield return [Map(Fn((XmlMappingFunction)999)), "supported function"];
        yield return [Map(new() { Kind = XmlValueExpressionKind.Function }), "supported function"];
        yield return [Map(Src("Input/A")), "Source paths must be absolute"];
        yield return [Map(new() { Kind = XmlValueExpressionKind.Source }), "source node"];
        yield return [Map(new() { Kind = XmlValueExpressionKind.Constant }), "invalid constant"];
        yield return [Map(Const(new string('x', 4097))), "invalid constant"];
        yield return [Map(new() { Kind = XmlValueExpressionKind.Constant, Constant = "a", SourcePath = "/Input/A" }), "invalid constant"];
        yield return [Map(Fn(XmlMappingFunction.StringSize, Src("/Input/A")) with { Constant = "x" }), "supported function"];
        var deep = Const("x");
        for (var index = 0; index < 16; index++) deep = Fn(XmlMappingFunction.StringUpperCase, deep);
        yield return [Map(deep), "exceeds"];
        yield return [Map(Fn(XmlMappingFunction.StringConcat, [.. Enumerable.Range(0, 32).Select(_ => Fn(XmlMappingFunction.StringUpperCase, Fn(XmlMappingFunction.StringUpperCase, Fn(XmlMappingFunction.StringUpperCase, Const("x")))))])), "exceeds"];
        yield return [Map(Const("x")) with { Version = 1 }, "requires version 2"];
        yield return [Map(Const("x")) with { Fields = [new() { TargetPath = "R", Expression = Const("x"), SourcePath = "/Input/A" }] }, "expression"];
        yield return [Map(Const("x")) with { Fields = [new() { TargetPath = "R", Expression = Const("x"), Constant = "y" }] }, "either a source path or a constant|expression"];
        yield return [Map(Const("x")) with { Fields = [new() { TargetPath = "R", Expression = Const("x"), MissingValueBehavior = MissingXmlValueBehavior.Omit }] }, "expression"];
        yield return [Map(Const("x")) with { Namespaces = new() { ["mulse-fn"] = "urn:other" } }, "reserved"];
        yield return [Map(Const("x")) with { Namespaces = new() { ["f"] = XmlMappingFunctions.Namespace } }, "reserved"];
    }

    [Theory]
    [MemberData(nameof(InvalidDefinitions))]
    public void InvalidExpressions_FailExplicitly(XmlMappingDefinition map, string expectedMessage)
    {
        var exception = Assert.ThrowsAny<Exception>(() => new XmlMappingEngine(XmlMappingTests.Json(map)));
        Assert.Matches(expectedMessage, exception.Message);
    }

    [Fact]
    public void Catalog_HasAnArityForEveryFunction()
    {
        foreach (var function in Enum.GetValues<XmlMappingFunction>())
        {
            var (min, max) = XmlMappingFunctionCatalog.Arity(function);
            Assert.InRange(min, 0, max);
        }
    }
}
