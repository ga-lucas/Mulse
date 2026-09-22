namespace Mulse.Modules.BizTalkClassification;

/// <summary>
/// Extensibility point for BizTalk migration analysis: suggests which native Mulse module kind ("Fetch",
/// "Parse", "Render", "Deliver", or "OrchestrationAugment") a custom BizTalk assembly should be rewritten as.
/// <para>
/// The BizTalk importer only has an assembly's project file and, where available, its source file names to
/// go on — it can't run arbitrary customer code. Register additional implementations of this interface in DI
/// to teach the importer about naming conventions, namespaces, or patterns specific to your organization or a
/// third-party BizTalk component library, without modifying the importer itself. Registered classifiers run
/// before the built-in <see cref="KeywordBizTalkAssemblyKindClassifier"/> fallback, in registration order, so
/// more specific rules can override the generic defaults.
/// </para>
/// </summary>
public interface IBizTalkAssemblyKindClassifier
{
    /// <summary>
    /// Attempts to classify the assembly described by <paramref name="context"/>. Return <see langword="null"/>
    /// if this classifier has no opinion, allowing the next classifier in the pipeline to run.
    /// </summary>
    string? TryClassify(BizTalkAssemblyClassificationContext context);
}
