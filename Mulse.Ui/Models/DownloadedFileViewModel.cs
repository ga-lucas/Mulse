namespace Mulse.Ui.Models;

/// <summary>A binary file returned from the API (for example a zip of scaffolded BizTalk migration modules)
/// along with the file name and content type the server suggested for saving it.</summary>
public sealed record DownloadedFileViewModel(string FileName, string ContentType, byte[] Content);
