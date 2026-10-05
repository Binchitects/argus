# tools/offline-nuget: .NET runtime packs for Code Arena

Code Arena (src/CodeArena) is published as one self-contained file per system,
so the machines that run it need no .NET. Building those files needs .NET's
runtime and host packs for each system. They are not part of the SDK, and a
host with no internet cannot fetch them, so they are kept here, once, as
`.nupkg` files. Git ignores them (`tools/offline-nuget/*.nupkg`).

With them here:

- `tools/publish-code-arena.sh --offline` builds every system into
  `dist/code-arena/<rid>/` from this folder only (nothing is downloaded), and
  `tools/package-code-arena.sh` packs them for a release;
- the app's image (`src/Llm.Api/Dockerfile`, `CODE_ARENA=auto`) builds them the
  same way and serves them on Connect your tools.

Without them, or with packs at another version than the SDK publishes with
(below), the image skips Code Arena and the page says how to add it.

## What goes here

Ten files: the runtime pack and the host pack for each of the five systems, at
the runtime version the SDK publishes with (the SDK image's own .NET runtime:
10.0.12 at the time of writing). The SDK prints it with
`dotnet msbuild src/CodeArena/CodeArena.csproj -getProperty:BundledNETCoreAppPackageVersion`
(`tools/dn` in place of `dotnet` without one installed):

```
microsoft.netcore.app.runtime.<rid>.<version>.nupkg
microsoft.netcore.app.host.<rid>.<version>.nupkg
```

for `<rid>` in `linux-x64 linux-arm64 win-x64 osx-x64 osx-arm64`.

The SDK's runtime moves with each monthly .NET patch: a newer
`mcr.microsoft.com/dotnet/sdk:10.0` pulled (or a newer SDK installed) asks for
newer packs. `tools/publish-code-arena.sh --offline` checks this first, asking
the SDK as above (other runtimes installed beside it, such as a newer .NET's
preview, do not count): when a pack is missing it lists the files at the
version to fetch, builds nothing and exits with 3, and the app's image then
skips Code Arena instead of failing (its build log shows the list).

## Filling it, once, on a machine with internet

```bash
version=10.0.12
for rid in linux-x64 linux-arm64 win-x64 osx-x64 osx-arm64; do
  for pack in runtime host; do
    id="microsoft.netcore.app.$pack.$rid"
    curl -fLo "tools/offline-nuget/$id.$version.nupkg" \
      "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg"
  done
done
```

Then carry the folder to the host with the repository (about 220 MB). A
`.nupkg` is a signed zip: NuGet checks the signature when it restores from it.
