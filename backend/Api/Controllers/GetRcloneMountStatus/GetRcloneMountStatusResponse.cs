namespace NzbWebDAV.Api.Controllers.GetRcloneMountStatus;

public class GetRcloneMountStatusResponse : BaseApiResponse
{
    public bool Enabled { get; set; }
    public bool Running { get; set; }
    public int? Pid { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public string? LastError { get; set; }
    public string? RcloneVersion { get; set; }
    public long? CacheBytesUsed { get; set; }
    public string? CacheMaxSize { get; set; }
}
