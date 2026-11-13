namespace Mulse.Ui.Models;

public sealed record ProcessingBacklogViewModel(bool DurableInboxSupported, int? UnfinishedMessages, int? PendingMessages,
    int? ClaimedMessages, int? HeldMessages, int? HeldExecutions, int? UnknownDeliveries, int? InFlightDeliveries, int? MaxPendingMessages);
