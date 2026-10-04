# Plugins

Each folder is a plugin that comes with the app (the image has them in
`/plugins`): Admin → Plugins installs one. A plugin is a `plugin.yaml` and the
files it names; no plugin code runs in the app. See
[docs/plugins.md](../docs/plugins.md) for the manifest.

| plugin | what it does | sign-in |
|---|---|---|
| `gitlab-issues` | finds projects, reads, creates and comments on GitLab issues | each person's own GitLab account (OAuth) |
