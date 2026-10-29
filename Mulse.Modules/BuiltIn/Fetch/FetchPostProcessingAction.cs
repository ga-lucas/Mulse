namespace Mulse.Modules.BuiltIn.Fetch;

/// <summary>
/// What a file-oriented fetch module should do after a terminal processing/delivery outcome.
/// Success and permanent-failure actions are configured independently; reading a file does not acknowledge it.
/// </summary>
public enum FetchPostProcessingAction
{
    /// <summary>Leave the file in place. The next run may read it again.</summary>
    None,

    /// <summary>Delete the file after the configured terminal outcome.</summary>
    Delete,

    /// <summary>Move the file into an archive directory after the configured terminal outcome.</summary>
    MoveToArchive,

    /// <summary>Rename the file in place after the configured terminal outcome. Search patterns must exclude renamed files.</summary>
    Rename
}
