# Connecting any MCP client

Argus speaks standard MCP. Nothing in the protocol path is specific to one
agent -- `deploy/smoke_test.py` and `deploy/agent_client_example.py` both use
the vanilla `mcp` SDK, and both work unmodified.

Two transports, and the choice is about deployment shape rather than features.
The tools, the ACL and the audit log are identical on both.

| | HTTP | stdio |
|---|---|---|
| serves | many developers, one server | one client, one process |
| credential | `Authorization: Bearer <PAT>` per request | `ARGUS_TOKEN` in the environment |
| identity | resolved per request | resolved once at startup |
| needs | a running server, a port, TLS in production | nothing but the command |

Use **HTTP** for a team: one indexed host, each developer's own GitLab token,
per-request ACL. Use **stdio** for a single developer on one machine, or for a
client that speaks nothing else.

## stdio

```bash
ARGUS_TOKEN=<your-gitlab-pat> argus serve --config /etc/argus/config.yaml --stdio
```

The credential is resolved *before* the server starts, so a bad token fails on
stderr where the person configuring the client sees it -- rather than as a tool
error inside an agent transcript, which is where a missing credential is
hardest to recognise for what it is.

`stdout` carries the JSON-RPC stream. Everything Argus prints goes to stderr,
so nothing it says can corrupt the protocol.

### Claude Code

```bash
claude mcp add argus --env ARGUS_TOKEN=<pat> -- argus serve --config /etc/argus/config.yaml --stdio
```

### Continue

`~/.continue/config.yaml`:

```yaml
mcpServers:
  - name: argus
    command: argus
    args: ["serve", "--config", "/etc/argus/config.yaml", "--stdio"]
    env:
      ARGUS_TOKEN: <pat>
```

### OpenCode, Cursor, Windsurf, Zed

All take the same three fields under their own key -- `mcp`, `mcpServers` or
`context_servers`:

```json
{
  "command": "argus",
  "args": ["serve", "--config", "/etc/argus/config.yaml", "--stdio"],
  "env": { "ARGUS_TOKEN": "<pat>" }
}
```

## HTTP

```bash
argus serve --config /etc/argus/config.yaml --host 0.0.0.0 --port 7700
```

### Claude Code

```bash
claude mcp add --transport http argus https://argus.internal/mcp --header "Authorization: Bearer <pat>"
```

### Continue, Cursor, and anything else taking a URL

```json
{
  "url": "https://argus.internal/mcp",
  "headers": { "Authorization": "Bearer <pat>" }
}
```

Behind a reverse proxy, pass the public hostname or every request is rejected
with 421 by the DNS-rebinding guard:

```bash
argus serve --config /etc/argus/config.yaml --host 0.0.0.0 --allowed-host argus.internal
```

## Verifying, whichever client

```bash
python deploy/smoke_test.py --url https://argus.internal/mcp --token <pat>
```

Seven checks: health, that a bad token is refused, the MCP handshake, the
server instructions, tool registration, a documented fact retrieved from a
pack, and the private index answering with the right repository count.

## What a client has to do to get the benefit

**Pass the server's `instructions` through to the model.** Argus returns 1,803
characters at connect time saying that recollection of headers, libraries and
IRQLs is unreliable. Measured: passing it through took tool use from 3 of 20
questions to 8, and accuracy from 12/20 to 14/20. A client that drops it leaves
that on the table -- and dropping it is silent, because everything else still
works.

**Use native function calling**, not a text protocol the model imitates. A text
protocol scored 10/20 and *collapsed to 4/20* when told to check facts first,
because the added prose broke the output format.

Most clients do both by default. Hermes needed patches for the first --
see `deploy/hermes-patch/`.
