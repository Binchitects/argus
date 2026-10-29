namespace CodeArena.Tests;

/// <summary>Uses tied to what they name, per language, with how sure each is: scope, type, name, or a guess among a few.</summary>
public sealed class CodeResolveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("code-resolve-").FullName;
    private readonly string _data = Directory.CreateTempSubdirectory("code-resolve-data-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_data, recursive: true);
    }

    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private CodeGraph Graph()
    {
        var index = new CodeIndex(_root, CodeIndex.FileFor(_data, _root));
        index.Sync(new Workspace(_root), [_root], CancellationToken.None, threads: 2);
        return index.Graph();
    }

    /// <summary>The use of a name on a line (1-based, the first line holding the text), as what it was tied to and how.</summary>
    private static (string To, Evidence? Evidence) At(CodeGraph graph, string path, string lineText, string name)
    {
        var line = File.ReadAllLines(Path.Combine(graph.Index.Root, path)).Select((l, i) => (l, i)).First(x => x.l.Contains(lineText, StringComparison.Ordinal)).i + 1;
        var edge = graph.EdgesIn(graph.Index.FileAt(path)!).Single(e => e.Line == line && e.Name == name);
        return (string.Join(" ", edge.To.Select(t => t.Id)), edge.Evidence);
    }

    [Fact]
    public void Csharp_uses_are_tied_by_the_enclosing_type_its_bases_the_usings_and_declared_types()
    {
        Write("src/Shop/Orders.cs", """
            using Shop.Payments;

            namespace Shop.Orders;

            public sealed class OrderService : ServiceBase
            {
                private readonly AuditLog _log = new();

                public void Place()
                {
                    Validate();
                    var payment = new Payment();
                    payment.Charge(10);
                    _log.Write("placed");
                    Trace();
                    Pick().Run();
                }

                private void Validate() { }

                private static object Pick() => new object();
            }
            """);
        Write("src/Shop/ServiceBase.cs", "namespace Shop;\n\npublic abstract class ServiceBase\n{\n    protected void Trace() { }\n}\n");
        Write("src/Shop/AuditLog.cs", "namespace Shop;\n\npublic sealed class AuditLog\n{\n    public void Write(string text) { }\n}\n");
        Write("src/Shop/Payments/Payment.cs", "namespace Shop.Payments;\n\npublic sealed class Payment\n{\n    public void Charge(decimal amount) { }\n}\n");
        Write("src/Jobs/Nightly.cs", "namespace Jobs;\n\npublic sealed class Nightly\n{\n    public void Run() { }\n}\n");
        Write("src/Jobs/Weekly.cs", "namespace Jobs;\n\npublic sealed class Weekly\n{\n    public void Run() { }\n}\n");
        var graph = Graph();
        const string orders = "src/Shop/Orders.cs";

        Assert.Equal(("src/Shop/Orders.cs#Shop.Orders.OrderService.Validate", Evidence.Scope), At(graph, orders, "Validate();", "Validate"));
        Assert.Equal(("src/Shop/Payments/Payment.cs#Shop.Payments.Payment", Evidence.Scope), At(graph, orders, "new Payment()", "Payment"));
        Assert.Equal(("src/Shop/Payments/Payment.cs#Shop.Payments.Payment.Charge", Evidence.Type), At(graph, orders, "payment.Charge", "Charge"));
        Assert.Equal(("src/Shop/AuditLog.cs#Shop.AuditLog.Write", Evidence.Type), At(graph, orders, "_log.Write", "Write"));
        Assert.Equal(("src/Shop/ServiceBase.cs#Shop.ServiceBase.Trace", Evidence.Scope), At(graph, orders, "Trace();", "Trace"));
        Assert.Equal(("src/Shop/ServiceBase.cs#Shop.ServiceBase", Evidence.Scope), At(graph, orders, "class OrderService", "ServiceBase"));
        // Two Runs, nothing saying which: both, the nearer first.
        var run = At(graph, orders, "Pick().Run()", "Run");
        Assert.Equal(Evidence.Guess, run.Evidence);
        Assert.Equal("src/Jobs/Nightly.cs#Jobs.Nightly.Run src/Jobs/Weekly.cs#Jobs.Weekly.Run", run.To);

        // Its uses, across the folder: the declaration is not one.
        var charge = graph.Find("Payment.Charge").Single();
        var (uses, unresolved) = graph.UsesOf(charge);
        Assert.Equal([(orders, 13)], uses.Select(u => (u.Path, u.Line)));
        Assert.Equal("Place", uses[0].From!.Name);
        Assert.Equal(0, unresolved);
        Assert.Contains(graph.UsesFrom(graph.Find("OrderService.Place").Single()), e => e.To[0].Id == charge.Id);
    }

    [Fact]
    public void A_name_declared_in_more_than_three_places_stays_unresolved_and_is_counted()
    {
        for (var i = 0; i < 4; i++)
        {
            Write($"src/Many/Part{i}.cs", $"namespace Many;\n\npublic sealed class Part{i}\n{{\n    public void Start() {{ }}\n}}\n");
        }
        Write("src/Many/Use.cs", "namespace Many;\n\npublic static class Use\n{\n    public static void Go(dynamic part) => part.Start();\n}\n");
        var graph = Graph();
        var (uses, unresolved) = graph.UsesOf(graph.Find("Part0.Start").Single());
        Assert.Empty(uses);
        Assert.Equal(1, unresolved);
    }

    [Fact]
    public void Typescript_imports_are_followed_through_tsconfig_paths_extends_references_and_re_exports()
    {
        Write("src/web/tsconfig.json", """{ "files": [], "references": [{ "path": "./tsconfig.app.json" }] }""");
        Write("src/web/tsconfig.app.json", """
            {
              // Its paths come from the base.
              "extends": "./tsconfig.base.json",
              "include": ["src"],
            }
            """);
        Write("src/web/tsconfig.base.json", """{ "compilerOptions": { "paths": { "@/*": ["./src/*"] } } }""");
        Write("src/web/src/lib/api.ts", "export function getJson(url: string) {\n  return fetch(url)\n}\n\nexport class Client {\n  load() {\n    return this.read()\n  }\n\n  read() {\n    return 1\n  }\n}\n");
        Write("src/web/src/lib/index.ts", "export * from \"./api\"\n");
        Write("src/web/src/pages/home.tsx", """
            import { getJson } from "@/lib/api"
            import { Client as Api } from "../lib"
            import * as api from "@/lib/api"

            export function Home() {
              getJson("/x")
              const client = new Api()
              client.load()
              api.getJson("/y")
              return <p />
            }
            """);
        var graph = Graph();
        const string home = "src/web/src/pages/home.tsx";

        Assert.Equal("src/web/src/lib/api.ts", graph.ScriptTarget(graph.Index.FileAt(home)!, "@/lib/api")?.Path);
        Assert.Equal(("src/web/src/lib/api.ts#getJson", Evidence.Scope), At(graph, home, "getJson(\"/x\")", "getJson"));
        Assert.Equal(("src/web/src/lib/api.ts#Client", Evidence.Scope), At(graph, home, "new Api()", "Api"));
        Assert.Equal(("src/web/src/lib/api.ts#Client.load", Evidence.Type), At(graph, home, "client.load()", "load"));
        Assert.Equal(("src/web/src/lib/api.ts#getJson", Evidence.Scope), At(graph, home, "api.getJson", "getJson"));
        Assert.Equal(("src/web/src/lib/api.ts#Client.read", Evidence.Scope), At(graph, "src/web/src/lib/api.ts", "this.read()", "read"));
        Assert.Equal(["src/web/src/lib/api.ts", "src/web/src/lib/index.ts"], graph.Imported(graph.Index.FileAt(home)!).Select(f => f.Path).Order());
    }

    [Fact]
    public void Python_imports_relative_ones_too_self_and_assigned_types_are_followed()
    {
        Write("tools/pkg/__init__.py", "");
        Write("tools/pkg/store.py", "class Store:\n    def save(self):\n        self._write()\n\n    def _write(self):\n        pass\n");
        Write("tools/pkg/helpers.py", "def fmt(x):\n    return x\n");
        Write("tools/pkg/run.py", "import os\nfrom .store import Store\nfrom . import helpers\n\n\ndef main():\n    store = Store()\n    store.save()\n    helpers.fmt(os.getpid())\n");
        var graph = Graph();
        const string run = "tools/pkg/run.py";

        Assert.Equal(("tools/pkg/store.py#Store", Evidence.Scope), At(graph, run, "store = Store()", "Store"));
        Assert.Equal(("tools/pkg/store.py#Store.save", Evidence.Type), At(graph, run, "store.save()", "save"));
        Assert.Equal(("tools/pkg/helpers.py#fmt", Evidence.Scope), At(graph, run, "helpers.fmt", "fmt"));
        Assert.Equal(("tools/pkg/store.py#Store._write", Evidence.Scope), At(graph, "tools/pkg/store.py", "self._write()", "_write"));
        // os is not in the folder: tied to nothing, and not counted as unresolved.
        Assert.Equal("", At(graph, run, "os.getpid", "getpid").To);
    }
}
