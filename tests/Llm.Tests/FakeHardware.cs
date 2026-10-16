using Llm.Api.Models;

namespace Llm.Tests;

/// <summary>
/// Machines the app must place models on sanely, as their probes would report them: no GPU, today's one 24 GB card, two
/// NVIDIA cards of different sizes, four 96 GB cards, eight 80 GB cards, two 192 GB AMD cards. Each has the picture
/// server's reserve on the GPU it runs on (the first, unless a test moves it).
/// </summary>
public static class FakeHardware
{
    public const long GiB = 1L << 30;

    /// <summary>The picture server's reserve (Engine:ImageReserveBytes' default).</summary>
    public const long Picture = 2 * GiB;

    private static Hardware Of(long ram, int? pictureGpu, params (string Name, long Gib, string Vendor, string Backend)[] gpus)
    {
        var devices = gpus.Select((g, i) => new GpuDevice(i, g.Name, g.Gib * GiB, 0, g.Vendor, g.Backend)).ToList();
        var hw = new Hardware(devices.FirstOrDefault()?.Name, devices.Count, devices.Sum(d => d.Total), 0, ram, 8 * GiB, Devices: devices);
        return HardwareProbe.WithReserves(hw, new EngineOptions(), pictureGpu)!;
    }

    public static Hardware CpuOnly => Hardware.CpuOnly(64 * GiB, 8 * GiB);

    /// <summary>Today's live machine: one RTX 3090, 61 GB of RAM.</summary>
    public static Hardware One24 => Of(61 * GiB, null, ("NVIDIA GeForce RTX 3090", 24, "nvidia", "cuda"));

    public static Hardware MixedNvidia => Of(128 * GiB, null, ("NVIDIA GeForce RTX 3090", 24, "nvidia", "cuda"), ("NVIDIA GeForce RTX 3060", 12, "nvidia", "cuda"));

    /// <summary>The design's server: four RTX PRO 6000 Blackwell, 1 TB of RAM; the picture server on <paramref name="pictureGpu"/>.</summary>
    public static Hardware Four96(int? pictureGpu = 3) => Of(1024 * GiB, pictureGpu, [.. Enumerable.Repeat(("NVIDIA RTX PRO 6000 Blackwell Server Edition", 96L, "nvidia", "cuda"), 4)]);

    public static Hardware Eight80 => Of(2048 * GiB, 7, [.. Enumerable.Repeat(("NVIDIA H100 80GB HBM3", 80L, "nvidia", "cuda"), 8)]);

    public static Hardware Rocm2x192 => Of(1536 * GiB, 1, ("AMD Instinct MI300X", 192, "amd", "rocm"), ("AMD Instinct MI300X", 192, "amd", "rocm"));

    public static IEnumerable<object[]> All =>
    [
        ["cpu only", CpuOnly], ["1 x 24 GB", One24], ["2 mixed NVIDIA", MixedNvidia], ["4 x 96 GB", Four96()], ["8 x 80 GB", Eight80], ["AMD ROCm 2 x 192 GB", Rocm2x192],
    ];
}
