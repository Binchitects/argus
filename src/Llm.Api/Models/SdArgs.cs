using System.Text;

namespace Llm.Api.Models;

/// <summary>A file a picture or video model reads, by its role (the server's flag for it) and its path in the library.</summary>
public sealed record MediaFile(string Role, string Path);

/// <summary>
/// What a picture or video server runs: its model, files, arguments and, for decoding on the GPU when it has room as the
/// server loads, the free memory that needs and the arguments then (and on the CPU otherwise).
/// </summary>
public sealed record SdModel(string Name, string Server, IReadOnlyList<MediaFile> Files, IReadOnlyList<string> Args, int? VaeGpuMb = null,
    IReadOnlyList<string>? VaeGpuArgs = null, IReadOnlyList<string>? VaeCpuArgs = null);

/// <summary>The control file services/sd-serve.sh reads (one setting a line, never run as a command).</summary>
public static class SdArgs
{
    /// <summary>stable-diffusion.cpp's flag for each role a file has.</summary>
    public static readonly IReadOnlyDictionary<string, string> RoleFlags = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["diffusion"] = "--diffusion-model", ["model"] = "--model", ["llm"] = "--llm", ["llm_vision"] = "--llm_vision", ["t5xxl"] = "--t5xxl",
        ["clip_l"] = "--clip_l", ["clip_g"] = "--clip_g", ["clip_vision"] = "--clip_vision", ["vae"] = "--vae", ["taesd"] = "--taesd",
        ["high_noise"] = "--high-noise-diffusion-model",
    };

    /// <summary>The control file: running <paramref name="model"/> (generation <paramref name="gen"/>) when <paramref name="want"/>, else stopped.</summary>
    public static string Conf(SdModel model, long gen, bool want, int? gpu = null)
    {
        var b = new StringBuilder();
        b.Append("gen=").Append(gen).Append('\n');
        b.Append("want=").Append(want ? "on" : "off").Append('\n');
        b.Append("model=").Append(model.Name).Append('\n');
        if (gpu is { } g)
        {
            b.Append("gpu=").Append(g).Append('\n');
        }
        foreach (var f in model.Files)
        {
            b.Append("file=").Append(f.Path).Append('\n');
        }
        foreach (var f in model.Files)
        {
            b.Append("arg=").Append(RoleFlags[f.Role]).Append('\n').Append("arg=").Append(f.Path).Append('\n');
        }
        foreach (var a in model.Args)
        {
            b.Append("arg=").Append(a).Append('\n');
        }
        if (model.VaeGpuMb is { } need)
        {
            b.Append("vae_gpu_mb=").Append(need).Append('\n');
            foreach (var a in model.VaeGpuArgs ?? [])
            {
                b.Append("vae_gpu_arg=").Append(a).Append('\n');
            }
            foreach (var a in model.VaeCpuArgs ?? [])
            {
                b.Append("vae_cpu_arg=").Append(a).Append('\n');
            }
        }
        return b.ToString();
    }
}
