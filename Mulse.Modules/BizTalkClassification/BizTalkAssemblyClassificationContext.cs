namespace Mulse.Modules.BizTalkClassification;

/// <summary>
/// Contextual information about a custom BizTalk assembly (a plain .csproj referenced by one or more BizTalk
/// projects, typically a custom pipeline component, orchestration helper, or property schema handler) that
/// <see cref="IBizTalkAssemblyKindClassifier"/> implementations use to suggest which native Mulse module kind
/// it should be rewritten as during BizTalk migration.
/// </summary>
/// <param name="AssemblyName">The assembly/project name, for example "Contoso.Interfaces.Core.CPC.RetrieveDocument".</param>
/// <param name="ProjectPath">The full path to the assembly's .csproj file.</param>
/// <param name="SourceFileNames">
/// File names (without directory) of the assembly's source files, when discoverable. Useful for classifiers
/// that look for naming conventions in individual class files rather than just the assembly name.
/// </param>
public sealed record BizTalkAssemblyClassificationContext(
    string AssemblyName,
    string ProjectPath,
    IReadOnlyList<string> SourceFileNames);
