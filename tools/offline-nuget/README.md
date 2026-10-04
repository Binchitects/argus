# tools/offline-nuget: .NET runtime packs for Arena Code

Arena Code (src/ArenaCode) is published as one self-contained file per system,
so the machines that run it need no .NET. Building those files needs .NET's
runtime and host packs for each system. They are not part of the SDK, and a
host with no internet cannot fetch them, so they are kept here, once, as
`.nupkg` files. Git ignores them (`tools/offline-nuget/*.nupkg`).

With them here:

- `tools/publish-arena-code.sh --offline` builds every system into
  `dist/arena-code/<rid>/` from this folder only (nothing is downloaded);
- the app's image (`src/Llm.Api/Dockerfile`, `ARENA_CODE=auto`) builds them the
  same way and serves them on Connect your tools.

Without them, the image skips Arena Code and the page says how to add it.

## What goes here

Ten files: the runtime pack and the host pack for each of the five systems, at
the runtime version the SDK publishes with (the SDK image's .NET runtime,
10.0.12 for SDK 10.0.1xx at the time of writing):

```
microsoft.netcore.app.runtime.<rid>.<version>.nupkg
microsoft.netcore.app.host.<rid>.<version>.nupkg
```

for `<rid>` in `linux-x64 linux-arm64 win-x64 osx-x64 osx-arm64`.

If the publish says `Unable to find package Microsoft.NETCore.App.Runtime.<rid>`
with a version, that version is the one to fetch: the SDK was updated.

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
