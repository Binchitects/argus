using Llm.Api.Gateway;

namespace Llm.Tests;

/// <summary>
/// The models a test lists, as routes beside the app's own (<see cref="IRoutesSource"/>), answered by the fake model at
/// engine.test: a test adds one to try a second model, or one that can see.
/// </summary>
public sealed class FakeGateway : IRoutesSource
{
    /// <summary>The models served; tests change it.</summary>
    public List<GatewayModel> Models { get; } =
        [new("Qwen3.8-Flash-Next", 32768, 8192, Vision: false, Tools: true, Thinking: true, 0.20m, 0.02m, 0.80m)];

    public IEnumerable<Deployment> Routes()
    {
        List<GatewayModel> given;
        lock (Models)
        {
            given = [.. Models];
        }
        foreach (var m in given)
        {
            var info = new System.Text.Json.Nodes.JsonObject
            {
                ["mode"] = m.Mode, ["supports_vision"] = m.Vision, ["supports_function_calling"] = m.Tools, ["supports_reasoning"] = m.Thinking, ["supports_audio_input"] = m.Audio,
            };
            if (m.Context is { } c)
            {
                info["max_input_tokens"] = c;
            }
            if (m.MaxOutput is { } o)
            {
                info["max_output_tokens"] = o;
            }
            var litellm = new System.Text.Json.Nodes.JsonObject { ["model"] = "openai/" + m.Name, ["api_base"] = "http://engine.test/v1", ["api_key"] = "none" };
            if (m.InputPerMtok is { } i)
            {
                litellm["input_cost_per_token"] = i / 1_000_000m;
            }
            if (m.CachedInputPerMtok is { } ci)
            {
                litellm["cache_read_input_token_cost"] = ci / 1_000_000m;
            }
            if (m.OutputPerMtok is { } op)
            {
                litellm["output_cost_per_token"] = op / 1_000_000m;
            }
            yield return new Deployment
            {
                Name = m.Name, Kind = Deployment.Remote, BaseUrl = "http://engine.test/v1", Model = m.Name, Mode = m.Mode, Info = info, Params = litellm, Fingerprint = "test:" + m.Name,
                Price = m.InputPerMtok is { } p ? new TokenPrice(p, m.CachedInputPerMtok ?? 0, m.OutputPerMtok ?? 0) : null,
            };
        }
    }

}

/// <summary>Holds the first call a fake gateway's hook sees for one person or key until released, and says when it holds it.</summary>
public sealed class Hold
{
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _once;

    public Task Held => _held.Task.WaitAsync(TimeSpan.FromSeconds(15));

    public Func<string, Task> For(string target) => async seen =>
    {
        if (seen == target && Interlocked.Exchange(ref _once, 1) == 0)
        {
            _held.SetResult();
            await _go.Task;
        }
    };

    public void Release() => _go.TrySetResult();
}
