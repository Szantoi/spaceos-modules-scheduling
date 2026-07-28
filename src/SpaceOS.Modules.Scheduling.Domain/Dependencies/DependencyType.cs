namespace SpaceOS.Modules.Scheduling.Domain.Dependencies;

/// <summary>Precedence relation between a predecessor and a successor operation.</summary>
public enum DependencyType
{
    /// <summary>Finish-to-Start: the successor may start after the predecessor finishes.</summary>
    FinishToStart,

    /// <summary>Start-to-Start: the successor may start after the predecessor starts.</summary>
    StartToStart,

    /// <summary>Finish-to-Finish: the successor may finish after the predecessor finishes.</summary>
    FinishToFinish,

    /// <summary>Start-to-Finish: the successor may finish after the predecessor starts.</summary>
    StartToFinish,
}

/// <summary>Which rule produced a resolved bound — carried into the API response.</summary>
public enum BoundSource
{
    /// <summary>An explicit fixed date on the operation; overrides every derived bound.</summary>
    FixedOverride,

    /// <summary>A partial release of the predecessor's output.</summary>
    PartialRelease,

    /// <summary>The precedence relation itself (type + lag).</summary>
    Dependency,
}
