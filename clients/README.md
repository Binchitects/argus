# Connecting an agent to Argus

One directory per client, each holding the smallest file that makes that client
work. Argus speaks standard MCP, so most of this is your client's own syntax
around the same two facts: **the URL** and **your GitLab token**.

| client | file | status |
|---|---|---|
| [DeepSeek Harness](#deepseek-harness) | `deepseek-harness/argus-mcp.patch.yml` | **executed**, v2.1.2 |
| [Qwen Code](#qwen-code) | `qwen-code/settings.example.json` | **executed**, qwen 0.23.3 on v2.1.2 |
| [Claude Code](#claude-code) | `claude-code/add.sh` | transcribed, **not executed here** |
| [Continue](#continue) | `continue/config.example.yaml` | transcribed, **not executed here** |
| [anything else](#anything-else) | `generic-mcp/http.json`, `generic-mcp/stdio.json` | the HTTP shape is verified by the two above; stdio is exercised by the test suite |

Say what you ran. The two that say "executed" were driven end to end against a
live stack: the harness called `find_symbol`, got `root/eal-core` back, and
Argus logged the call against the right person (`user=dev_alpha, outcome=ok`).
The other three are written from each client's own documentation and are
correct as far as that goes, but nobody has run them from this repository.

---

## Before anything: two settings that are not the client's fault

**TLS.** The stack serves a self-signed certificate. Node-based clients
(DeepSeek Harness, Qwen Code) have **no per-server TLS option**, so trust has to
be established before the process starts:

```bash
export NODE_EXTRA_CA_CERTS="$PWD/stack/config/traefik/certs/tls.crt"
```

`stack/scripts/with-ca.sh` prints the equivalents for curl, python and git.
Clients that offer an "insecure" switch (Qwen Code's `--insecure`) work too, but
that turns off verification for **every** connection the process makes, not just
this one.

**The hostname.** Use the name the reverse proxy forwards, `argus.<domain>` by
default. Argus's DNS-rebinding guard compares the inbound `Host` header against
a fixed allowlist, and a mismatch is `421 Invalid Host Header` on every `/mcp`
call while `/healthz` still answers 200 -- so the service looks healthy and only
the tool calls fail.

---

## DeepSeek Harness

```bash
ARGUS_TOKEN=<gitlab-pat> \
NODE_EXTRA_CA_CERTS="$PWD/stack/config/traefik/certs/tls.crt" \
  dsh --profile headless --patch clients/deepseek-harness/argus-mcp.patch.yml \
  "Use the mcp__argus__find_symbol tool, with name=DecodeFrame."
```

Two details in that file are load-bearing and neither is guessable:

- **`insert:` is required.** A bare `- name: ...` entry is read as an *override*
  of a server that does not exist yet, and dsh rejects the patch with
  `id is required for non-insert patches`.
- **`!!js` is required** around the token template. Without it the backticks
  arrive as literal text and Argus answers 401. Taking the token from
  `process.env` keeps a PAT out of the file, so it can live in this repository.

`--profile headless "task"` is how to exercise an MCP server from a script: no
browser, no server left running, the tool calls visible in the transcript.

## Qwen Code

```bash
# let the CLI write it (this is how the sample was produced)
qwen mcp add argus https://argus.llm.localhost/mcp -t http \
  -H 'Authorization: Bearer <gitlab-pat>' --trust \
  --description 'Organisation code index'

NODE_EXTRA_CA_CERTS="$PWD/stack/config/traefik/certs/tls.crt" \
  qwen --approval-mode yolo \
  "Use the argus MCP tool find_symbol to look up the symbol DecodeFrame."
```

Or merge `qwen-code/settings.example.json` into `~/.qwen/settings.json`
(user scope) or `<project>/.qwen/settings.json` (project scope). The keys it
uses -- `httpUrl`, `headers`, `trust`, `description` -- are exactly what
`qwen mcp add` writes.

**Scope changes one thing.** A user-scope server connects as soon as it is
added. A **project**-scope one starts as `Pending approval` and is not
connected until:

```bash
qwen mcp approve argus
```

That is deliberate on Qwen Code's part -- a server declared in a repository
would otherwise be added silently by cloning it -- but it reads as a broken
server if you are expecting it to just connect. Both scopes were exercised
end to end here, and both called `find_symbol` correctly.

**`trust: true` is not optional in a headless run.** Without it Qwen Code asks
for confirmation before every Argus call, and nothing ever proceeds.

**Pointing Qwen Code at the stack's own model**, which is worth doing because it
needs no cloud key and exercises both halves at once:

```bash
qwen --auth-type openai \
     --openai-base-url https://gateway.<domain>/v1 \
     --openai-api-key "$LITELLM_MASTER_KEY" \
     -m "$MODEL_NAME" --approval-mode yolo "<task>"
```

The gateway host is `gateway.<domain>` -- **not** `api.<domain>`, which routes
to Authelia and answers with a login redirect that reads as a 401.

## Claude Code

`claude-code/add.sh <http|stdio>`. Not executed here; `claude` is not installed
on the machine this was written on. Check it with `claude mcp list`.

### Forcing verify-after

`claude-code/verify-after.sh` is a `Stop` hook that checks the model's answer
against the documentation packs **before the turn is allowed to end**, and hands
back anything the documentation contradicts. Install it with:

```bash
cp clients/claude-code/verify-after.sh ~/.claude/hooks/
chmod +x ~/.claude/hooks/verify-after.sh
```

then add to `~/.claude/settings.json`, merging with whatever is there:

```json
{
  "hooks": {
    "Stop": [
      { "hooks": [ { "type": "command",
                     "command": "~/.claude/hooks/verify-after.sh" } ] }
    ]
  }
}
```

This exists because of one measured failure. A model reviewing kernel code
answered in 2.2 seconds with **zero tool calls**, naming `wcscpy_s` (user-mode),
`<string.h>` (user-mode) and `ucrt.lib` (user-mode) — a real function, a real
header, a real library, and the wrong answer to a question about kernel code.
Telling the model to verify does not work; `SERVER_INSTRUCTIONS` already says to,
and this is the model that ignored it. The hook puts the check outside the
model's judgement.

It calls `argus verify`, which is `docs_verify` with an exit code — hooks are
shell commands and cannot call an MCP tool, so a command is the only interface
that works. **Exit 2 is the only blocking outcome**, and only a genuine
contradiction produces it. Exit 6 means "could not check" — no packs installed,
or unreadable ones — and does **not** block, because a deployment without
documentation packs must not become an agent that can never finish a sentence.
Anything unexpected also fails open, for the same reason.

Only the *last* assistant message is checked, so a claim the model already
corrected in a later turn never blocks. A turn that produced no prose — a
tool-only turn, an interrupted one — is nothing to check and never blocks.

**Not executed against a real Claude Code**, like `add.sh` above: the hook
protocol (stdin JSON, exit 2 blocks, stderr is the reason) is from
[Anthropic's hooks reference](https://code.claude.com/docs/en/hooks), and the
transcript shape is the part to verify against your own version. The script
fails open when the transcript does not look as expected, so a version that
renames a field stops checking rather than blocking every answer.

## Continue

Merge `continue/config.example.yaml` into `~/.continue/config.yaml`. The HTTP
form takes the same `url`/`headers` shape as everything else.

## Anything else

`generic-mcp/http.json` and `generic-mcp/stdio.json` are the two transports in
their lowest common denominator form. Rename the outer key to whatever your
client documents -- `mcp`, `mcpServers`, `context_servers` -- and leave the
object inside alone.

| | HTTP | stdio |
|---|---|---|
| serves | many developers, one server | one client, one process |
| credential | `Authorization: Bearer <pat>` per request | `ARGUS_TOKEN` in the environment |
| identity | resolved per request | resolved once at startup |
| needs | a running server, a port, TLS in production | nothing but the command |

---

## What a client has to get right to be worth connecting

**Pass the server's `instructions` through to the model.** Argus returns 1,803
characters at connect time saying that recollection of headers, libraries and
IRQLs is unreliable. Measured: passing it through took tool use from 3 of 20
questions to 8, and accuracy from 12/20 to 14/20. A client that drops it leaves
that on the table -- and dropping it is silent, because everything else works.

**Use native function calling**, not a text protocol the model imitates. A text
protocol scored 10/20 and *collapsed to 4/20* when told to check facts first,
because the added prose broke the output format.

Most clients do both by default. Hermes needed patches for the first; see
`scripts/hermes-patch/`.

## Checking the connection without an agent

```bash
python scripts/smoke_test.py --url https://argus.<domain>/mcp --token <pat>
```

Seven checks: health, a bad token refused, the MCP handshake, the server
instructions, tool registration, a pack fact, and the private index answering
with the right repository count. If that passes and your agent still fails, the
problem is in the client's configuration, not in Argus.
