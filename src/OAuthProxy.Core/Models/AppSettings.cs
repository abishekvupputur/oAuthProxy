namespace OAuthProxy.Core.Models;

public sealed class AppSettings
{
    public int ListenPort { get; set; } = 5559;
    public bool StartWithWindows { get; set; }
}
