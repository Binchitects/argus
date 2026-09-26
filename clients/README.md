# Connecting an editor or agent

Argus speaks standard MCP at **`http://<host>:8080/mcp`** (`https://` when the
server has `ARGUS_TLS_CERT`). Every client needs the same two facts:

- **the URL** — use the host name listed in `ARGUS_HOSTNAME` (or `localhost`);
  MCP's DNS-rebinding guard refuses any other `Host` with `421`;
- **a credential** — a **code index key** from the app's *Settings & keys* page
  (`ak_…`). It sees exactly the repositories your GitLab account can read. A
  GitLab personal access token works too, for people without an account here.

For the model itself, the gateway is OpenAI-compatible at
`http://<host>:4000/v1` with a **model API key** (`sk-…`) from the same page.

| client | file |
|---|---|
| Claude Code | `claude-code/add.sh`, and `claude-code/verify-after.sh` as a Stop hook |
| Qwen Code | `qwen-code/settings.example.json` |
| DeepSeek Harness | `deepseek-harness/argus-mcp.patch.yml` |
| Continue | `continue/config.example.yaml` |
| anything else | `generic-mcp/http.json`, `generic-mcp/stdio.json` |

The HTTP configs are exercised by the browser suite (`frontend/e2e`), which
connects to `/mcp` with a code index key; each client's own syntax around them
is taken from that client's documentation.

## Claude Code

```bash
export ARGUS_URL=http://llm.example.lan:8080/mcp ARGUS_KEY=ak_...
clients/claude-code/add.sh
```

### Forcing verify-after

A model can answer an API question from memory with no tool call at all.
`argus verify --claude-hook` is a Stop hook: when the model tries to finish,
it checks the last answer against the installed documentation packs and, only
when the documentation **contradicts** it, blocks the stop with the correction
(for example *MessageBoxW dll: you said 'shell32.dll'; the documentation says
'User32.dll'*). "Could not check" never blocks. It needs a local `argus` with
the packs; see `claude-code/verify-after.sh` for the settings snippet.

## Qwen Code

```bash
qwen mcp add argus http://llm.example.lan:8080/mcp -t http -H "Authorization: Bearer ak_..."
```

or merge `qwen-code/settings.example.json` into `~/.qwen/settings.json`. To use
the local model as well: `--openai-base-url http://llm.example.lan:4000/v1
--openai-api-key sk-...`.

## DeepSeek Harness

```bash
ARGUS_KEY=ak_... dsh --patch clients/deepseek-harness/argus-mcp.patch.yml "Use mcp__argus__find_symbol with name=DecodeFrame."
```

`insert:` is the append form, and `!!js` is required around the header
template — without it the backticks arrive literally and Argus answers 401.

## Anything else

`generic-mcp/http.json` is the shape most URL-based clients take.
`generic-mcp/stdio.json` runs a local `argus` as a child process, with a GitLab
token in `ARGUS_TOKEN`, for one person working against their own index.

## Checking the connection without an agent

```bash
curl -s http://llm.example.lan:8080/mcp -H "Authorization: Bearer ak_..." \
  -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"1"}}}'
```

A `200` with `"serverInfo":{"name":"argus"...}` means the URL, the host name
and the key are all right; `401` names what is wrong with the key, `421` the
host name.
