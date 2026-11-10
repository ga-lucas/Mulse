using System.Text;
using Service.Mapping;

namespace Service.Tests;

public sealed class XmlSchemaInspectorRootFilterTests
{
    private static string Schema()
    {
        var big = new StringBuilder();
        for (var index = 0; index < 2100; index++) big.Append($"<xs:element name=\"F{index}\" type=\"xs:string\" />");
        return $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:r" elementFormDefault="qualified">
              <xs:element name="Small"><xs:complexType><xs:sequence><xs:element name="A" type="xs:string" /></xs:sequence></xs:complexType></xs:element>
              <xs:element name="Big"><xs:complexType><xs:sequence>{big}</xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
    }

    [Fact]
    public void RootFilter_CountsOnlyTheRequestedGlobalElement()
    {
        var schema = Schema();
        Assert.Contains("2,048-node", Assert.Throws<ArgumentException>(() => XmlSchemaInspector.Analyze([schema])).Message, StringComparison.Ordinal);
        var small = XmlSchemaInspector.Analyze([schema], "Small");
        Assert.Equal("Small", Assert.Single(small.Roots).Name.Split(':')[^1]);
        Assert.Contains("2,048-node", Assert.Throws<ArgumentException>(() => XmlSchemaInspector.Analyze([schema], "Big")).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => XmlSchemaInspector.Analyze([schema], "Missing"));
    }
}
