using Microsoft.AspNetCore.Http.HttpResults;
using Mulse.Modules;
using Service.Models;

namespace Service.Endpoints;

public static class DesignerEndpoints
{
    public static WebApplication MapDesignerEndpoints(this WebApplication app)
    {
        app.MapPost("/api/flow-designer/xml-mapping/schemas", (XmlSchemaAnalysisRequest request, CancellationToken cancellationToken) =>
                TypedResults.Ok(Service.Mapping.XmlSchemaInspector.Analyze(request.Schemas, cancellationToken)))
            .WithName("AnalyzeXmlMappingSchemas")
            .WithSummary("Inspect supplied XSDs for namespace-correct mapping paths and cardinality")
            .WithDescription("Compiles only supplied schema text; no external file/network resolution. Unsupported schema constructs are shown explicitly. This is not a BizTalk execution approval.")
            .WithTags("Designer")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost("/api/flow-designer/xml-mapping/import", (XmlMappingImportRequest request, CancellationToken cancellationToken) =>
                TypedResults.Ok(Service.Mapping.BizTalkNativeMapConverter.Convert(request.MapXml, request.SourceSchemas, request.TargetSchemas, cancellationToken)))
            .WithName("ImportNativeXmlMapping")
            .WithSummary("Convert a supported scalar BizTalk map to an editable native mapping proposal")
            .WithDescription("Requires explicit source/target schemas. Rejects unsupported constructs rather than dropping links. No script execution, deployment-policy association, flow modification, or execution approval.")
            .WithTags("Designer")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost("/api/flow-designer/xml-mapping/preview", (XmlMappingPreviewRequest request, CancellationToken cancellationToken) =>
            {
                var engine = new Mulse.CompatibilityPack.XmlMappingEngine(request.MappingJson);
                return TypedResults.Ok(new XmlMappingPreviewResponse(engine.Transform(request.SampleXml, cancellationToken), engine.Xslt));
            })
            .WithName("PreviewXmlMapping")
            .WithSummary("Preview a saved XML mapping against sample XML")
            .WithDescription("Uses the execution engine with no flow execution, file access, scripts, external document resolution, or delivery. Preview success does not approve an imported flow.")
            .WithTags("Designer")
            .Produces<XmlMappingPreviewResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost("/api/flow-designer/analyze", async Task<Ok<FlowDesignResponse>> (AnalyzeFlowDesignRequest request, IFlowDesignService flowDesignService, CancellationToken cancellationToken) =>
            {
                var response = await flowDesignService.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(response);
            })
            .WithName("AnalyzeFlowDesignSample")
            .WithSummary("Analyze uploaded or pasted sample data")
            .WithDescription("Detects text payload formats and proposes module/field mappings. XLS/XLSX samples use binaryContent (base64, up to 10 MiB) with empty textContent; workbook inference checks container structure without executing plugin code or extracting cells.")
            .WithTags("Designer")
            .Produces<FlowDesignResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapPost("/api/flow-designer/flows", async Task<Created<FlowResponse>> (CreateDesignedFlowRequest request, IFlowDesignService flowDesignService, CancellationToken cancellationToken) =>
            {
                var created = await flowDesignService.CreateFlowAsync(request, cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"/api/flows/{created.Id}", FlowMappings.MapFlow(created));
            })
            .WithName("CreateFlowFromDesigner")
            .WithSummary("Create a flow from the WYSIWYG designer")
            .WithDescription("Creates a runtime-configurable flow from a designer-authored module selection, settings, and visual field mapping document.")
            .WithTags("Designer")
            .Produces<FlowResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }
}
