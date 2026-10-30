using System.Xml;
using System.Xml.Linq;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private static AnalysisScope ResolveOrchestrationScope(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var owners = new List<string>();
        foreach (var projectPath in Directory.EnumerateFiles(directory, "*.btproj", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var artifacts = new List<BizTalkArtifactResponse>();
                AddArtifacts(XDocument.Load(projectPath), directory, directory, "XLang", "Orchestration", artifacts);
                if (artifacts.Any(artifact => string.Equals(artifact.FullPath, sourcePath, StringComparison.OrdinalIgnoreCase)))
                    owners.Add(projectPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
            {
                throw new ArgumentException($"Unable to inspect possible orchestration owner '{projectPath}': {exception.Message}. Select the owning .btproj explicitly.", nameof(sourcePath), exception);
            }
        }

        if (owners.Count != 1)
            throw new ArgumentException(owners.Count == 0
                ? $"No sibling .btproj explicitly includes '{sourcePath}' as an XLang item. Select its owning .btproj or solution instead; standalone orchestration import cannot resolve dependencies."
                : $"Multiple sibling .btproj files explicitly include '{sourcePath}': {string.Join(", ", owners)}. Select the intended owning .btproj explicitly.", nameof(sourcePath));

        return new AnalysisScope(sourcePath, "Orchestration", Path.GetFileNameWithoutExtension(sourcePath), directory, owners, []);
    }
}
