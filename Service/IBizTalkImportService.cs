using Service.Models;

namespace Service;

public interface IBizTalkImportService
{
    Task<BizTalkSolutionAnalysisResponse> AnalyzeAsync(AnalyzeBizTalkSolutionRequest request, CancellationToken cancellationToken);
}
