using Mulse.Hl7;
using Mulse.Modules;

namespace Service.Tests;

public sealed class Hl7XmlRenderTests
{
    private const string Xml = "<ns0:ADT_A01 xmlns:ns0=\"urn:x\"><MSH><MSH.1>|</MSH.1><MSH.2>^~\\&amp;</MSH.2><MSH.3><MSH.3.1>APP</MSH.3.1></MSH.3><MSH.9><MSH.9.1>ADT</MSH.9.1><MSH.9.2>A01</MSH.9.2></MSH.9><MSH.12><MSH.12.1>2.3.1</MSH.12.1></MSH.12></MSH>"
        + "<PID_Group><PID><PID.1>1</PID.1><PID.3><PID.3.1>123</PID.3.1><PID.3.4><PID.3.4.1>A</PID.3.4.1><PID.3.4.2>B</PID.3.4.2></PID.3.4></PID.3></PID></PID_Group></ns0:ADT_A01>";

    private static async Task<string> RunAsync(string xml, Dictionary<string, string>? settings = null)
    {
        var module = new Hl7XmlRenderModule();
        var batch = new IntegrationBatch([new("a.xml", BinaryData.FromString(xml), "application/xml", new Dictionary<string, string>())]);
        var result = await module.RenderAsync(new("f", "e", DateTimeOffset.UnixEpoch), batch, new ModuleStepDefinition { Module = "hl7-xml-render", Settings = settings ?? [] }, default);
        return result.Payloads[0].Content.ToString();
    }

    [Fact]
    public async Task RendersSegmentsFieldsComponentsAndSubcomponents()
    {
        var text = await RunAsync(Xml);
        Assert.Equal("MSH|^~\\&|APP||||||ADT^A01|||2.3.1\rPID|1||123^^^A&B\r", text);
    }

    [Fact]
    public async Task TrimsTrailingEmptyFieldsWhenConfigured()
    {
        var text = await RunAsync("<R><MSH><MSH.2>^~\\&amp;</MSH.2><MSH.3><MSH.3.1>X</MSH.3.1></MSH.3></MSH><PID><PID.1>1</PID.1><PID.4/></PID></R>", new() { ["trimTrailingDelimiters"] = "true" });
        Assert.Equal("MSH|^~\\&|X\rPID|1\r", text);
    }

    [Fact]
    public async Task RejectsNonXmlAndMissingMsh()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("MSH|^~"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("<R><PID><PID.1>1</PID.1></PID></R>"));
    }
}
