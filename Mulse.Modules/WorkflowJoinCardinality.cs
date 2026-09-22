namespace Mulse.Modules;

/// <summary>How matched rows are shaped into output records by <see cref="MultiSourceJoinMapAugmentModule"/>.</summary>
public enum WorkflowJoinCardinality
{
    /// <summary>One output record per matched left x right row pair (classic relational fan-out).</summary>
    FanOut,

    /// <summary>One output record per left row, with all matched right rows attached as a nested JSON array.</summary>
    Nested
}
