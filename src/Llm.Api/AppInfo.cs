using System.Reflection;

namespace Llm.Api;

public sealed record AppInfo(string Name, string Version)
{
    public static AppInfo Current { get; } = new(
        "LLM Service",
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? "0.0.0");
}
