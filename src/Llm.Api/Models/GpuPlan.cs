using System.Globalization;

namespace Llm.Api.Models;

/// <summary>
/// Something that keeps GPU memory: a llama.cpp model (spread over its GPUs), a vLLM group (its share of each of its GPUs),
/// the picture server or the embedder (a reserve on one GPU). <see cref="Strict"/>: it cannot move to RAM when its GPU is
/// short, so it would not start (vLLM); a llama.cpp model only gets slower (layers to RAM).
/// </summary>
public sealed record Resident(string Name, string Kind, IReadOnlyDictionary<int, long> PerGpu, bool Strict = false);

/// <summary>
/// The one owner of placement: every resident thing on every GPU, whatever the machine has (no GPU, one, many, of any vendor
/// and size), and whether each GPU's sum fits. Admin → Models shows a bar per GPU from it, and a load or a save that would
/// not fit is refused by it. Nothing here knows a layout: the residents say where they are.
/// </summary>
public sealed record GpuPlan(IReadOnlyList<GpuUse> Gpus, IReadOnlyList<ModelProblem> Problems)
{
    public ModelProblem? FirstError => Problems.FirstOrDefault(p => p.Error);

    private static string G(long b) => (b / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";

    /// <summary>The machine's GPUs: those it lists, else one standing for all of its GPU memory (a source that does not list them), else none.</summary>
    public static IReadOnlyList<GpuDevice> DevicesOf(Hardware hw) =>
        hw.Devices is { Count: > 0 } d ? d : hw.Gpus > 0 ? [new GpuDevice(0, hw.GpuName ?? "GPU", hw.GpuTotal, hw.GpuReserved)] : [];

    /// <summary>A GPU's room for everything: its memory less the driver's share and llama.cpp's margin.</summary>
    public static long BudgetOf(GpuDevice d) => Math.Max(0, d.Total - d.Reserved - Hardware.FitMargin);

    /// <summary>
    /// A model of <paramref name="bytes"/> on these GPUs (null: all), spread over them by what each has for models (as
    /// llama.cpp's fit splits its layers): each GPU's room less the reserves on it.
    /// </summary>
    public static Resident Spread(string name, string kind, Hardware hw, IReadOnlyCollection<int>? gpus, long bytes, bool strict = false)
    {
        var mine = DevicesOf(hw).Where(x => gpus is not { Count: > 0 } || gpus.Contains(x.Index)).ToList();
        var room = mine.ToDictionary(x => x.Index, x => Math.Max(0, BudgetOf(x) - (hw.Reserves ?? []).Where(r => r.Gpu == x.Index).Sum(r => r.Bytes)));
        var total = room.Values.Sum();
        return new Resident(name, kind, mine.ToDictionary(x => x.Index,
            x => total > 0 ? (long)(bytes * ((double)room[x.Index] / total)) : bytes / Math.Max(1, mine.Count)), strict);
    }

    /// <summary>A vLLM group: on each of its GPUs, that share of the GPU's whole memory (--gpu-memory-utilization), as vLLM takes it.</summary>
    public static Resident Share(string name, Hardware hw, IReadOnlyCollection<int> gpus, double utilization) =>
        new(name, "vllm", DevicesOf(hw).Where(x => gpus.Contains(x.Index)).ToDictionary(x => x.Index, x => (long)(x.Total * utilization)), true);

    /// <summary>
    /// The servers' reserves the machine says (the picture server, the embedder), one resident each. Not strict: llama.cpp's
    /// fit leaves them their room, and a picture job short of it is lent the GPU (<see cref="GpuShare"/>).
    /// </summary>
    public static IEnumerable<Resident> Reserved(Hardware hw) =>
        (hw.Reserves ?? []).Where(r => r.Bytes > 0).Select(r => new Resident(r.Name, "reserve", new Dictionary<int, long> { [r.Gpu] = r.Bytes }));

    /// <summary>
    /// Every GPU with what its residents need of it, and what does not fit: a GPU asked for more than it has is an error
    /// when a resident there cannot spill to RAM (it would not start), else a warning (the last llama.cpp model to load puts
    /// layers in RAM and answers slower). A resident on a GPU the machine does not have is an error too.
    /// </summary>
    public static GpuPlan Of(Hardware hw, IEnumerable<Resident> residents)
    {
        var devices = DevicesOf(hw);
        var all = residents.ToList();
        var problems = new List<ModelProblem>();
        foreach (var r in all)
        {
            foreach (var missing in r.PerGpu.Keys.Where(i => devices.All(d => d.Index != i)).Take(1))
            {
                problems.Add(new("gpus", devices.Count == 0
                    ? $"{r.Name} is placed on GPU {missing}, and this machine has no GPU: choose the CPU, or an engine on a machine with GPUs."
                    : $"{r.Name} is placed on GPU {missing}, and this machine has GPUs {string.Join(", ", devices.Select(d => d.Index))}.", true));
            }
        }
        var gpus = devices.Select(d =>
        {
            var on = all.Where(r => r.PerGpu.ContainsKey(d.Index)).ToList();
            return new GpuUse(d.Index, d.Name, BudgetOf(d), on.Sum(r => r.PerGpu[d.Index]), [.. on.Select(r => r.Name)], d.Vendor, d.Total);
        }).ToList();
        foreach (var g in gpus.Where(g => g.Need > g.Budget))
        {
            var strict = all.Where(r => r.Strict && r.PerGpu.ContainsKey(g.Index)).Select(r => r.Name).ToList();
            if (strict.Count > 0)
            {
                problems.Add(new("memory", $"GPU {g.Index} ({g.Name}) would be asked for about {G(g.Need)} of its {G(g.Budget)} by {string.Join(", ", g.Models)}: "
                    + $"{string.Join(", ", strict)} cannot move to RAM, so one would not start. Place them on different GPUs, give a vLLM model a smaller share "
                    + "(GPU memory use), or keep fewer loaded.", true));
            }
            else
            {
                problems.Add(new("memory", $"On GPU {g.Index} ({g.Name}) they would use about {G(g.Need)} of its {G(g.Budget)}: the last to load puts layers in RAM and answers slower. Place them on different GPUs, or keep fewer loaded.", false));
            }
        }
        return new GpuPlan(gpus, problems);
    }

    /// <summary>What is left on a GPU once its residents have theirs (0 for a GPU it does not know).</summary>
    public long FreeOn(int gpu) => Gpus.FirstOrDefault(g => g.Index == gpu) is { } g ? Math.Max(0, g.Budget - g.Need) : 0;
}
