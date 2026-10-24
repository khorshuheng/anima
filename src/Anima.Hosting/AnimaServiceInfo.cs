namespace Anima.Hosting;

public sealed record AnimaServiceInfo(string Name, int Port, string Version)
{
    public static AnimaServiceInfo For(string name, int port) => new(
        name,
        port,
        typeof(AnimaServiceInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
}
