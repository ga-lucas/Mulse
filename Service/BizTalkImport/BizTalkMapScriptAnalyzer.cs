using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Mulse.Modules.Pipeline;

namespace Service.BizTalkImport;

internal static class BizTalkMapScriptAnalyzer
{
    private static readonly string[] BizTalkReferences = ["Microsoft.BizTalk", "Microsoft.XLANGs", "IBaseMessage", "XLANGMessage"];

    public static MapScriptInventory Analyze(XDocument document)
    {
        var scripts = document.Descendants().Where(static element => element.Name.LocalName == "Script").ToArray();
        var evidence = scripts.Take(128).Select(script =>
        {
            var code = script.Value;
            var functoid = script.Ancestors().FirstOrDefault(element => element.Name.LocalName == "Functoid");
            return new MapScriptEvidence(
                ((IXmlLineInfo)script).HasLineInfo() ? ((IXmlLineInfo)script).LineNumber : 0,
                Limit(functoid?.Attribute("FunctoidID")?.Value ?? functoid?.Attribute("ID")?.Value ?? "(not declared)"),
                Limit(script.Attribute("Language")?.Value ?? "(not declared)"),
                code.Length,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant(),
                BizTalkReferences.Where(reference => code.Contains(reference, StringComparison.Ordinal)).ToArray());
        }).ToArray();
        return new(scripts.Length, Math.Max(0, scripts.Length - evidence.Length), evidence);
    }

    private static string Limit(string text) => text.Length <= 128 ? text : text[..128];
}
