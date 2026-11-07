using System.Text;

// What the code index and graph linked from Code Arena (Argus.csproj) read through Code Arena's own helpers, as Argus
// has them: a checkout's files are plain files, read as UTF-8, and nothing of the index is kept on disk here.
namespace CodeArena;

internal static class Files
{
    public static (string Text, bool Bom) ReadText(string file)
    {
        var bytes = File.ReadAllBytes(file);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (new UTF8Encoding(false, false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }
}

internal static class PrivateFiles
{
    public static void EnsureDirectory(string dir) => Directory.CreateDirectory(dir);
}
