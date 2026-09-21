namespace Service.Models;

/// <summary>
/// Payload for toggling whether a flow is enabled, without requiring the caller to resend the
/// entire flow definition (fetch/parse/augments/deliveries). Most callers - especially the UI -
/// only ever need to flip this one flag, for example right after reviewing an imported BizTalk
/// draft flow's settings.
/// </summary>
public sealed record SetFlowEnabledRequest(bool Enabled);
