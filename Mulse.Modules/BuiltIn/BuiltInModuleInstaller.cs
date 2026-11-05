namespace Mulse.Modules.BuiltIn;

public sealed class BuiltInModuleInstaller : IModuleInstaller
{
    public void Install(IModuleRegistryBuilder builder)
    {
        builder.AddFetch<FileSystemFetchModule>();
        builder.AddFetch<SftpFetchModule>();
        builder.AddFetch<FlowLinkFetchModule>();
        builder.AddParse<JsonParseModule>();
        builder.AddOrchestrationAugment<JsonEnvelopeAugmentTransformModule>();
        builder.AddOrchestrationAugment<DecisionAugmentModule>();
        builder.AddOrchestrationAugment<NativeControlFlowAugmentModule>();
        builder.AddOrchestrationAugment<XmlRulesAugmentModule>();
        builder.AddOrchestrationAugment<MultiSourceJoinMapAugmentModule>();
        builder.AddOrchestrationAugment<ParsedDataMapAugmentModule>();
        builder.AddOrchestrationAugment<StatefulOrchestrationAugmentModule>();
        builder.AddOrchestrationAugment<DurableBatchAugmentModule>();
        builder.AddOrchestrationAugment<OdxProgramAugmentModule>();
        builder.AddOrchestrationAugment<XmlBatchEnvelopeAugmentModule>();
        builder.AddOrchestrationAugment<DebatchAugmentModule>();
        builder.AddOrchestrationAugment<MapDispatchAugmentModule>();
        builder.AddRender<JsonRenderModule>();
        builder.AddRender<XmlRenderModule>();
        builder.AddRender<RenderDispatchModule>();
        builder.AddRender<RenderPipelineModule>();
        builder.AddDeliver<LoggingDeliverModule>();
        builder.AddDeliver<FileSystemDeliverModule>();
        builder.AddDeliver<HttpDeliverModule>();
        builder.AddDeliver<FlowLinkDeliverModule>();
        builder.AddTransform<ConcatTransformModule>();
        builder.AddTransform<AddTransformModule>();
        builder.AddTransform<SubtractTransformModule>();
        builder.AddTransform<MultiplyTransformModule>();
        builder.AddTransform<DivideTransformModule>();
        builder.AddTransform<CoalesceTransformModule>();
        builder.AddTransform<SubstringTransformModule>();
        builder.AddTransform<UpperCaseTransformModule>();
        builder.AddTransform<LowerCaseTransformModule>();
        builder.AddTransform<TrimTransformModule>();
        builder.AddTransform<RoundTransformModule>();
        builder.AddTransform<FormatDateTransformModule>();
    }
}
