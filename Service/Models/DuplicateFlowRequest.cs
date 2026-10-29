using System.ComponentModel.DataAnnotations;

namespace Service.Models;

public sealed record DuplicateFlowRequest([property: Required] string Id);
