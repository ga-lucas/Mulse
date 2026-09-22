namespace Mulse.Modules;

/// <summary>
/// Infers a MIME content-type from a file name's extension. Centralizes the extension-to-content-type
/// mapping used by every file-based fetch module (local file system, SFTP, file-system-watcher,
/// document repository) so they all agree on the same content-type inference instead of each
/// re-implementing their own (potentially inconsistent) switch statement.
/// </summary>
public static class ContentTypeResolver
{
    /// <summary>Returns the inferred MIME content-type for <paramref name="fileName"/>, based on its extension.</summary>
    public static string FromFileName(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".hl7" or ".adt" => "text/hl7-v2",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }
}
