namespace Service.Models;

/// <summary>A single stored config/secret value that resolves matching <c>{{config:...}}</c>/<c>{{secret:...}}</c> tokens.</summary>
public sealed record ConfigValueResponse(string Reference, string Value);

/// <summary>Payload for creating or updating a config/secret value.</summary>
public sealed record SetConfigValueRequest(string Value);
