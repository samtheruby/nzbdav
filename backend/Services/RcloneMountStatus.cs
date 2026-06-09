namespace NzbWebDAV.Services;

/// <summary>
/// An immutable snapshot of the embedded rclone mount's state, published by
/// <see cref="RcloneMountService"/> and read by the status API.
/// </summary>
public record RcloneMountStatusSnapshot
{
    public bool Enabled { get; init; }
    public bool Running { get; init; }
    public int? Pid { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public string? LastError { get; init; }
}

/// <summary>
/// Thread-safe holder for the latest embedded-mount status. Registered as a singleton
/// and shared between RcloneMountService (the writer) and the status API (the reader).
/// Reference assignment of the snapshot is atomic, so no locking is needed.
/// </summary>
public class RcloneMountStatus
{
    private volatile RcloneMountStatusSnapshot _snapshot = new();

    public RcloneMountStatusSnapshot Current => _snapshot;

    public void Set(RcloneMountStatusSnapshot snapshot) => _snapshot = snapshot;
}
