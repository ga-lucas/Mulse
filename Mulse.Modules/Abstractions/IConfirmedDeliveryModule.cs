namespace Mulse.Modules.Abstractions;

/// <summary>
/// A destination that atomically deduplicates the stable delivery ID together with its effect.
/// Confirmation is scoped to this delivery, not arbitrary downstream side effects.
/// </summary>
public interface IConfirmedDeliveryModule : IDeliverModule
{
    /// <summary>Whether the required durable destination is currently configured.</summary>
    bool IsConfirmationAvailable { get; }

    Task<ConfirmedDeliveryResult> ConfirmAsync(Guid stableDeliveryId, FlowExecutionContext context,
        IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken);

    Task<ConfirmedDeliveryResult> DeliverOnceAsync(Guid stableDeliveryId, FlowExecutionContext context,
        IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken);
}

public enum DeliveryConfirmationStatus { Confirmed, NotDelivered, Unknown }

/// <summary>Captured responses must be recoverable when confirmation reports a prior delivery.</summary>
public sealed record ConfirmedDeliveryResult(DeliveryConfirmationStatus Status, IntegrationBatch? Responses = null);
