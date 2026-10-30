using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    internal sealed record ProjectReference(string Name, string Include, string? HintPath, bool IsProjectReference);
    private sealed record ProjectIdentity(string Name, string ProjectPath);

    private static IReadOnlyList<ProjectReference> ReadProjectReferences(XDocument document)
        => document.Descendants()
            .Where(static element => element.Name.LocalName is "Reference" or "ProjectReference")
            .Select(static element =>
            {
                var include = element.Attribute("Include")?.Value ?? string.Empty;
                var isProject = element.Name.LocalName == "ProjectReference";
                var name = NormalizeReferenceName(include,
                    element.Elements().FirstOrDefault(static child => child.Name.LocalName == "Name")?.Value);
                if (isProject && string.Equals(name, include.Trim(), StringComparison.Ordinal))
                {
                    name = Path.GetFileNameWithoutExtension(include.Replace('\\', Path.DirectorySeparatorChar));
                }

                return new ProjectReference(name, include,
                    element.Elements().FirstOrDefault(static child => child.Name.LocalName == "HintPath")?.Value,
                    isProject);
            })
            .Where(static reference => !string.IsNullOrWhiteSpace(reference.Name))
            .Distinct()
            .ToArray();

    private static BizTalkProjectReferenceResponse ResolveProjectReference(
        string ownerProjectPath,
        ProjectReference reference,
        IReadOnlyList<ProjectIdentity> projects)
    {
        var namedProjects = projects.Where(project =>
                string.Equals(project.Name, reference.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(project.ProjectPath), reference.Name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var sourcePath = reference.IsProjectReference ? reference.Include : reference.HintPath;
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            if (sourcePath.Contains("$(", StringComparison.Ordinal) || sourcePath.Contains("%(", StringComparison.Ordinal))
            {
                return Result(BizTalkReferenceResolution.Unresolved, null, namedProjects,
                    "The reference path contains unevaluated MSBuild properties or item metadata. Supply a concrete path or review it manually.");
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ownerProjectPath)!,
                    sourcePath.Replace('\\', Path.DirectorySeparatorChar)));
            }
            catch (ArgumentException exception)
            {
                return Result(BizTalkReferenceResolution.Unresolved, null, namedProjects, $"Invalid reference path: {exception.Message}");
            }

            if (reference.IsProjectReference)
            {
                var exact = projects.FirstOrDefault(project => string.Equals(project.ProjectPath, fullPath, StringComparison.OrdinalIgnoreCase));
                return exact is not null
                    ? Result(BizTalkReferenceResolution.ProjectPath, exact.ProjectPath, [exact], "Resolved by the explicit ProjectReference path.")
                    : Result(BizTalkReferenceResolution.Unresolved, null, namedProjects,
                        $"ProjectReference target '{fullPath}' is not in the analyzed workspace. Add its project, solution, or folder to the source paths; no name-based substitute was used.");
            }

            // HintPath points at a build output, not a project file. Only accept the closest owning
            // project directory with a matching assembly/file name; never pick an unrelated namesake.
            var outputName = Path.GetFileNameWithoutExtension(fullPath);
            var owners = namedProjects.Where(project => IsWithinDirectory(fullPath, Path.GetDirectoryName(project.ProjectPath)!)
                    && (string.Equals(project.Name, outputName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileNameWithoutExtension(project.ProjectPath), outputName, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(static project => Path.GetDirectoryName(project.ProjectPath)!.Length)
                .ToArray();
            if (owners.Length > 0)
            {
                var closest = owners.Where(project => Path.GetDirectoryName(project.ProjectPath)!.Length
                    == Path.GetDirectoryName(owners[0].ProjectPath)!.Length).ToArray();
                if (closest.Length == 1)
                {
                    return Result(BizTalkReferenceResolution.HintPath, closest[0].ProjectPath, closest,
                        "Inferred from the HintPath's closest project directory and matching assembly/file name; build output was not loaded or executed.");
                }

                return Result(BizTalkReferenceResolution.Ambiguous, null, closest,
                    "Multiple matching projects own the HintPath directory. No dependency was selected.");
            }

            return namedProjects.Length == 0
                ? Result(BizTalkReferenceResolution.External, null, [], "No matching source project was found in the analyzed workspace; this is an external assembly reference.")
                : Result(BizTalkReferenceResolution.Unresolved, null, namedProjects,
                    $"HintPath '{fullPath}' does not identify a matching source project directory. No name-based substitute was used.");
        }

        return namedProjects.Length switch
        {
            0 => Result(BizTalkReferenceResolution.External, null, [], "No matching source project was found in the analyzed workspace; this is an external assembly reference."),
            1 => Result(BizTalkReferenceResolution.UniqueName, namedProjects[0].ProjectPath, namedProjects,
                "Resolved by a unique assembly/project file name within the analyzed workspace; no path evidence was supplied."),
            _ => Result(BizTalkReferenceResolution.Ambiguous, null, namedProjects,
                "Multiple source projects match this name. Supply a ProjectReference or concrete HintPath; no dependency was selected.")
        };

        BizTalkProjectReferenceResponse Result(BizTalkReferenceResolution resolution, string? resolvedPath,
            IReadOnlyList<ProjectIdentity> candidates, string description)
            => new(reference.Name, reference.Include, reference.HintPath, resolution, resolvedPath,
                candidates.Select(static project => project.ProjectPath).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(), description);
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static IEnumerable<string> DescribeReferenceIssues(string projectPath, IReadOnlyList<BizTalkProjectReferenceResponse> references)
        => references.Where(static reference => reference.Resolution is BizTalkReferenceResolution.Ambiguous or BizTalkReferenceResolution.Unresolved)
            .Select(reference => $"Project '{projectPath}', reference '{reference.Name}': {reference.Description} Candidates: {string.Join(", ", reference.CandidateProjectPaths)}");

    private static IReadOnlyList<string> ResolveDependencyPaths(
        string projectPath,
        IReadOnlyDictionary<string, IReadOnlyList<BizTalkProjectReferenceResponse>> referencesByPath)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { projectPath };
        var dependencies = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(projectPath);
        while (pending.TryDequeue(out var current))
        {
            if (!referencesByPath.TryGetValue(current, out var references))
            {
                continue;
            }

            foreach (var reference in references)
            {
                if (reference.ResolvedProjectPath is { } resolved && visited.Add(resolved))
                {
                    dependencies.Add(resolved);
                    pending.Enqueue(resolved);
                }
            }
        }

        return dependencies;
    }

    private static string ReserveFlowId(string suggestedId, string sourceIdentity, ISet<string> reservedIds)
    {
        if (reservedIds.Add(suggestedId))
        {
            return suggestedId;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceIdentity.ToUpperInvariant())))[..12].ToLowerInvariant();
        var uniqueId = $"{suggestedId}-{hash}";
        var suffix = 2;
        while (!reservedIds.Add(uniqueId))
        {
            uniqueId = $"{suggestedId}-{hash}-{suffix++}";
        }

        return uniqueId;
    }
}
