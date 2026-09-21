using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Payload for creating a managed runtime module package.</summary>
public sealed record CreateModulePackageRequest
{
    /// <summary>The unique package id.</summary>
    [Required]
    public required string Id { get; init; }

    /// <summary>The assembly path of the plugin package.</summary>
    [Required]
    public required string AssemblyPath { get; init; }

    /// <summary>Whether the package should be enabled immediately.</summary>
    public bool Enabled { get; init; } = true;
}
