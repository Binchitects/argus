namespace CodeArena.Tests;

/// <summary>Hot exit's backups: kept per folder outside it, listed, deleted, and never for a path outside the folder.</summary>
public sealed class BackupTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("backups-data-").FullName;
    private readonly string _shop = Directory.CreateTempSubdirectory("backups-shop-").FullName;
    private readonly string _blog = Directory.CreateTempSubdirectory("backups-blog-").FullName;

    public void Dispose()
    {
        foreach (var dir in new[] { _data, _shop, _blog })
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private Backups For(string root) => new(_data, root, new IdeFiles(new Workspace(root)).Resolve);

    [Fact]
    public void A_backup_is_kept_listed_and_deleted_and_each_folder_has_its_own()
    {
        var shop = For(_shop);
        shop.Keep("src/app.ts", "const total = 2\n", "v1");
        shop.Keep("user:settings.json", "{}", "u1");
        var listed = shop.List();
        Assert.Equal(["src/app.ts", "user:settings.json"], listed.Select(b => b!["path"]!.GetValue<string>()).Order());
        Assert.Equal("const total = 2\n", listed.Single(b => b!["path"]!.GetValue<string>() == "src/app.ts")!["text"]!.GetValue<string>());
        Assert.Equal("v1", listed.Single(b => b!["path"]!.GetValue<string>() == "src/app.ts")!["version"]!.GetValue<string>());
        // Kept outside the folder, readable by its owner alone.
        Assert.Empty(Directory.GetFiles(_shop, "*", SearchOption.AllDirectories));
        if (!OperatingSystem.IsWindows())
        {
            foreach (var f in Directory.GetFiles(Path.Combine(_data, "backups"), "*.json", SearchOption.AllDirectories))
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f));
            }
        }
        Assert.Empty(For(_blog).List());

        shop.Keep("src/app.ts", null, null);
        Assert.Equal(["user:settings.json"], shop.List().Select(b => b!["path"]!.GetValue<string>()));
    }

    [Fact]
    public void A_path_outside_the_folder_or_a_text_past_5_MB_is_refused()
    {
        var shop = For(_shop);
        Assert.Equal(403, Assert.Throws<IdeError>(() => shop.Keep("../elsewhere.txt", "x", null)).Status);
        Assert.Equal(413, Assert.Throws<IdeError>(() => shop.Keep("big.txt", new string('x', Backups.MostBytes + 1), null)).Status);
        Assert.Empty(shop.List());
    }
}
