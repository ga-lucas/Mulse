using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Payload for updating a managed runtime module package.</summary>
public sealed record UpdateModulePackageRequest
{
    /// <summary>The unique package id.</summary>
    [Required]
    public required string Id { get; init; }

    /// <summary>The assembly path of the plugin package.</summary>
    [Required]
    public required string AssemblyPath { get; init; }

    /// <summary>Whether the package should remain enabled.</summary>
    public bool Enabled { get; init; } = true;
}
