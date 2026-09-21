namespace Service.Models;

/// <summary>One place a <c>{{config:...}}</c>/<c>{{secret:...}}</c> token is used across any flow, and whether it currently resolves.</summary>
public sealed record ConfigReferenceUsageResponse(string Reference, string FlowId, string SettingPath, bool IsResolved);
