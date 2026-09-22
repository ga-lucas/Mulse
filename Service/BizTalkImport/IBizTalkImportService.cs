using Service.Models;

namespace Service.BizTalkImport;

public interface IBizTalkImportService
{
    Task<BizTalkSolutionAnalysisResponse> AnalyzeAsync(AnalyzeBizTalkSolutionRequest request, CancellationToken cancellationToken);
}
