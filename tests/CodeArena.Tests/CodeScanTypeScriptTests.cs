namespace CodeArena.Tests;

/// <summary>The TypeScript and JavaScript scanner: what a file declares, where each starts and ends, and what it imports.</summary>
public sealed class CodeScanTypeScriptTests
{
    private const string Fixture = """
        import { api } from "@/lib/api"
        import type { Model } from './models'
        export { helper } from "./helpers"
        const fs = require("node:fs")

        /** The longest a request waits. */
        export const TIMEOUT = 30_000

        export interface Source {
          id: string
          name?: string
          read(at: number): Promise<string>
        }

        export type Kind = 'a' | 'b'

        export enum State { New, Done }

        /** Reads a source, whole. */
        export async function readSource(id: string): Promise<Source> {
          const url = `/api/${id}/{nothing}`
          return api(url)
        }

        function* ids() {
          yield 1
        }

        export class Store extends Base implements Reader {
          #cache = new Map<string, string>()
          static instances = 0
          constructor(private readonly root: string) {
            super()
          }
          get size() {
            return this.#cache.size
          }
          async load(key: string) {
            return fs.readFileSync(key)
          }
          onChange = (e: Event) => {
            this.#cache.clear()
          }
        }

        export const sourcesQuery = {
          queryKey: ['sources'],
          queryFn: () => api('/api/sources'),
        }

        export const useThing = (id: string) => {
          return api(id)
        }

        export function SourceList({ sources }: { sources: Source[] }) {
          return <ul>{sources.map((s) => <SourceRow key={s.id} source={s} />)}</ul>
        }

        const Memoised = memo((props: { x: number }) => <p>{props.x}</p>)

        export default function Page() {
          return <SourceList sources={[]} />
        }

        const lazy = () => import("./later")
        """;

    [Fact]
    public void A_file_declares_its_functions_classes_types_and_components_each_with_its_range()
    {
        var f = new TypeScriptScanner().Scan(Fixture);
        var shapes = f.Symbols.Select(s => (s.Kind, s.Qualified, s.Start, s.End)).ToList();
        Assert.Contains(("const", "TIMEOUT", 7, 7), shapes);
        Assert.Contains(("interface", "Source", 9, 13), shapes);
        Assert.Contains(("property", "Source.id", 10, 10), shapes);
        Assert.Contains(("method", "Source.read", 12, 12), shapes);
        Assert.Contains(("type", "Kind", 15, 15), shapes);
        Assert.Contains(("enum", "State", 17, 17), shapes);
        Assert.Contains(("function", "readSource", 20, 23), shapes);
        Assert.Contains(("function", "ids", 25, 27), shapes);
        Assert.Contains(("class", "Store", 29, 44), shapes);
        Assert.Contains(("constructor", "Store.constructor", 32, 34), shapes);
        Assert.Contains(("accessor", "Store.size", 35, 37), shapes);
        Assert.Contains(("method", "Store.load", 38, 40), shapes);
        Assert.Contains(("method", "Store.onChange", 41, 43), shapes);
        Assert.Contains(("const", "sourcesQuery", 46, 49), shapes);
        Assert.Contains(("function", "useThing", 51, 53), shapes);
        Assert.Contains(("component", "SourceList", 55, 57), shapes);
        Assert.Contains(("component", "Memoised", 59, 59), shapes);
        Assert.Contains(("component", "Page", 61, 63), shapes);
        Assert.Contains(("function", "lazy", 65, 65), shapes);
        // A member names what holds it, as the Outline nests it.
        Assert.Equal("Store", f.Symbols.Single(s => s.Qualified == "Store.load").Parent);
        Assert.Equal("Source", f.Symbols.Single(s => s.Qualified == "Source.id").Parent);
        Assert.Null(f.Symbols.Single(s => s.Qualified == "readSource").Parent);
        // Object literal members are not symbols of their own.
        Assert.DoesNotContain(shapes, s => s.Qualified.EndsWith("queryFn", StringComparison.Ordinal));
        Assert.Equal(("Reads a source, whole.", "The longest a request waits."),
            (f.Symbols.Single(s => s.Name == "readSource").Doc, f.Symbols.Single(s => s.Name == "TIMEOUT").Doc));

        Assert.Equal(["@/lib/api", "./models", "./helpers", "node:fs", "./later"], f.Imports);
        // A JSX element is used as a call; the template's text is no use.
        Assert.Contains(f.Uses, u => u.Name == "SourceRow" && u.Call && u.Line == 56);
        Assert.Contains(f.Uses, u => u.Name == "readFileSync" && u.Call && u.Receiver == "fs");
        Assert.DoesNotContain(f.Uses, u => u.Name == "nothing");
        Assert.False(f.Approximate);
    }

    [Fact]
    public void Code_Arenas_IDE_source_reads_with_every_symbol_where_its_name_is()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "web", "src", "code-arena");
        var files = Directory.GetFiles(root, "*.ts*", SearchOption.AllDirectories).Where(f => f.EndsWith(".ts", StringComparison.Ordinal) || f.EndsWith(".tsx", StringComparison.Ordinal)).ToList();
        Assert.True(files.Count > 30);
        var count = 0;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var lines = text.Split('\n');
            foreach (var s in new TypeScriptScanner().Scan(text).Symbols)
            {
                count++;
                Assert.True(s.Start <= s.End && s.End <= lines.Length, $"{Path.GetFileName(file)}: {s.Qualified} {s.Start}-{s.End}");
                var name = s.Name.TrimStart('#');
                Assert.True(string.Join('\n', lines[(s.Start - 1)..Math.Min(lines.Length, s.Start + 3)]).Contains(name, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)}:{s.Start}: {s.Kind} {s.Qualified} is not named near its start");
            }
        }
        Assert.True(count > 1000, $"only {count} symbols in the IDE's source");
    }
}
