using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mulse.Modules.Orchestration;
using Service.Models;

namespace Service.BizTalkImport;

internal static class BizTalkOdxTranslationScaffolder
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IReadOnlyList<BizTalkScaffoldedModuleResponse> Create(string path)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..16].ToLowerInvariant();
        var moduleId = "odx-translated-" + hash;
        var className = "TranslatedOdx_" + hash;
        var name = Path.GetFileNameWithoutExtension(path);
        OdxProgram program;
        try { program = OdxProgramReader.ReadFile(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException)
        {
            return [new(className + ".translation.json", moduleId + "-report", name,
                JsonSerializer.Serialize(new { version = 1, status = "Blocked", detail = "ODX translation could not read a valid bounded source model: " + exception.Message }, Options))];
        }
        var services = Flatten(program.Roots).Where(node => node.ShapeType == "ServiceDeclaration").ToArray();
        if (services.Length != 1)
            return [new(className + ".translation.json", moduleId + "-report", name,
                JsonSerializer.Serialize(new { version = 1, status = "Blocked", detail = "Automatic translation requires exactly one ServiceDeclaration in this orchestration." }, Options))];
        var compilation = OdxAutomaticBindings.Compile(program);
        var model = JsonSerializer.Serialize(program, Options);
        var literal = JsonSerializer.Serialize(model);
        var source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using System.Text.Json;
            using Mulse.Modules.Abstractions;
            using Mulse.Modules.Execution;
            using Mulse.Modules.Orchestration;
            using Mulse.Modules.Payloads;
            using Mulse.Modules.Pipeline;

            public sealed class {{className}} : IOrchestrationAugmentModule
            {
                private static readonly OdxProgram Program = JsonSerializer.Deserialize<OdxProgram>({{literal}}, JsonSerializerOptions.Web)
                    ?? throw new InvalidOperationException("Missing ODX source model.");
                public ModuleDescriptor Descriptor { get; } = new("{{moduleId}}", "Automatically bound ODX with explicit untranslated stubs", ModuleKind.OrchestrationAugment,
                    "Supported typed statements/conditions are automatically bound; unresolved activities remain explicit blockers. Not an imported execution approval.");

                public async Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var compilation = OdxAutomaticBindings.Compile(Program);
                    compilation.EnsureComplete();
                    var frame = new OdxExecutionFrame(Program, context, batch, step);
                    await new OdxProgramExecutor().ExecuteAsync(Program, FindService(Program.Roots), compilation.Bindings, frame, cancellationToken);
                    return frame.Batch;
                }

                private static OdxNode FindService(System.Collections.Generic.IReadOnlyList<OdxNode> nodes)
                {
                    foreach (var node in nodes)
                    {
                        if (node.ShapeType == "ServiceDeclaration") return node;
                        if (ContainsService(node.Children)) return FindService(node.Children);
                    }
                    throw new InvalidOperationException("ODX has no service.");
                }
                private static bool ContainsService(System.Collections.Generic.IReadOnlyList<OdxNode> nodes)
                {
                    foreach (var node in nodes) if (node.ShapeType == "ServiceDeclaration" || ContainsService(node.Children)) return true;
                    return false;
                }
            }
            """;
        return [new(className + ".cs", moduleId, name, source),
            new(className + ".translation.json", moduleId + "-report", name,
                JsonSerializer.Serialize(new { version = 1, entryNodeId = services[0].Id, diagnostics = compilation.Diagnostics }, Options))];
    }

    private static IEnumerable<OdxNode> Flatten(IEnumerable<OdxNode> nodes)
        => nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));
}
