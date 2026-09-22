namespace Mulse.Modules.BuiltIn.Fetch;

/// <summary>
/// What a file-oriented fetch module should do with a source file after it has been read
/// successfully. Mirrors the receive-location semantics BizTalk always applied (delete or move
/// after pickup) specifically to prevent the same file being re-processed on every poll/run.
/// </summary>
public enum FetchPostProcessingAction
{
    /// <summary>Leave the file in place. The next run may read it again.</summary>
    None,

    /// <summary>Delete the file after it has been fetched successfully.</summary>
    Delete,

    /// <summary>Move the file into an archive directory after it has been fetched successfully.</summary>
    MoveToArchive,

    /// <summary>Rename the file in place (so wildcard re-scans no longer match it) after it has been fetched successfully.</summary>
    Rename
}
