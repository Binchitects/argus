using System.Text;

namespace Llm.Api.Models;

/// <summary>
/// Conversations' caches on disk (Engine:KvCacheGb, on KV_CACHE_DIR: an NVMe), a tier below the engine's RAM
/// (cache-ram): when a conversation's slot goes to another, what the slot holds (its prompt read, its state) is
/// written there first; when the conversation comes back to a slot that does not hold it, that file is read back
/// into the slot, so the engine reads only what is new instead of the whole history again (seconds, not minutes, on
/// a long one). It survives the engine's restarts. The engine's script (router.sh) keeps the folder within its size
/// and age (Engine:KvCacheDays; 0: for ever), the oldest used first out.
/// </summary>
public static class KvCache
{
    /// <summary>Where the engine has the folder (its slot-save-path is a folder in it a model).</summary>
    public const string EngineDir = "/kvcache";

    /// <summary>The file in the engine's presets folder that says how much the folder may hold, and how long (router.sh reads it).</summary>
    public const string ControlFile = "kvcache";

    /// <summary>A model's folder: its name, with what a path cannot hold made plain.</summary>
    public static string FolderOf(string model)
    {
        var sb = new StringBuilder(model.Length);
        foreach (var ch in model)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        }
        return sb.ToString().TrimStart('.');
    }

    /// <summary>A conversation's file in its model's folder.</summary>
    public static string FileOf(Guid conversation) => conversation.ToString("N") + ".bin";
}
