using System.Text.Json.Nodes;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Execution;
using Mulse.Modules.Pipeline;

namespace Mulse.Billing;

public sealed class BillingFilenameTransformModule : ITransformModule
{
    public ModuleDescriptor Descriptor { get; } = new("billing-filename-transform", "Billing filename macros", ModuleKind.Transform,
        "Expands one text template using DATE/DATETIME/TIME/UTC/DAY/GUID/TICKS/SECONDS macros. Uses execution start time and explicit zone/culture. Does not read or write files.", BillingFilename.Settings);

    public Task<JsonNode?> InvokeAsync(FlowExecutionContext context, IReadOnlyList<JsonNode?> inputs, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count != 1 || inputs[0] is not JsonValue scalar || !scalar.TryGetValue<string>(out var template))
            throw new ArgumentException("Billing filename transform requires exactly one non-null text template.");
        var result = BillingFilename.Expand(template, context, step.Settings, Descriptor.Id);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<JsonNode?>(JsonValue.Create(result));
    }
}
