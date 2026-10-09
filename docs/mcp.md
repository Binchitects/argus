# Arena MCP

Every tool a person has in the chat, for their own agent: one MCP server at
`https://DOMAIN/mcp`, signed in with **their API key**. Code Arena, Claude
Code, Qwen Code, Continue or any MCP client connects once and gets Argus, the
web, Python, pictures and the servers, APIs and plugins the admins added, with
the same names and schemas as in the chat, run as that person.

## Connecting

**Connect your tools** (`/setup`) has the address, the tools it serves you,
and setups to paste. Keep the key in `LLM_SERVICE_API_KEY`, not in a file you
share.

```bash
# Claude Code
claude mcp add --transport http arena https://DOMAIN/mcp \
  --header "Authorization: Bearer $LLM_SERVICE_API_KEY"

# Qwen Code (no --trust: a tool that asks first should ask you)
qwen mcp add arena https://DOMAIN/mcp -t http \
  -H "Authorization: Bearer $LLM_SERVICE_API_KEY"
```

Other clients (streamable HTTP):

```json
{ "mcpServers": { "arena": { "type": "http", "url": "https://DOMAIN/mcp",
  "headers": { "Authorization": "Bearer <your API key>" } } } }
```

- The key is the one **Your account → API key** makes (`sk-…`), the same that
  calls the gateway. A new key replaces the old one here too.
- With the stack's own CA, Node-based clients need
  `NODE_EXTRA_CA_CERTS=deploy/config/traefik/certs/ca.crt` (see
  [clients/README.md](../clients/README.md)).
- `GET https://DOMAIN/.well-known/mcp` says where the server is and how to sign
  in, for clients that look.

## What it serves

- **Exactly the person's chat tools**: those on, allowed to them (Admin →
  Tools: everyone, admins, or groups) and available now. A tool off in new
  chats is still theirs, so it is served. There is no separate switch per tool:
  taking a tool away in Admin → Tools takes it away here at once.
- **The same functions**: Argus's (`find_symbol`, …), `run_python`, `web_search`,
  `fetch_page`, `generate_image`, `generate_video`, `speak`, `calculate`,
  `current_time`, `days_between`, `decide` (Laya, while its module runs:
  [chat.md](chat.md#decide-laya)), `deep_research` (below), and
  `server__function` for each MCP server, API and plugin (`pets__list_pets`).
- **Deep research** (`deep_research`, a `question`), for whoever may use it
  (Admin → Tools → Deep research). It runs as in the chat, in a new chat of
  the person's own titled "Deep research: …": in turn, with their default
  model, tools and credit, its parts by sub-agents. It is checked as a message
  sent with **Deep research** on (the safeguards, the deep research a day).
  The agent hears the step as progress (waiting in line, planning the
  research, its parts, a call waiting for the person, writing the report),
  then gets the report with the chat's address, where the person reads its
  parts. Cancelling the call stops it; the chat keeps what was done. It takes
  minutes: give the client a long tool timeout. While **Web** asks before each
  call, it is not served (nobody is in its chat to allow the web's calls), and
  the notes for the agent say so, as does **Connect your tools** (it is not
  listed among the tools served); the person starts it in the chat instead.
- **Not served**: the chat's own tools. Reading files (a chat's files),
  questions to the person (`ask_user`) and sub-agents (`delegate`) have no chat
  here.
- **A tool that asks first** (Admin → Tools, or a plugin's writes) cannot show
  the app's **Allow** card here. It is served with `destructiveHint: true`, and
  its description tells the client to ask before each call. Let the client ask:
  do not trust every tool (`--trust`, yolo modes).
- **Notes for the agent** come with `initialize`: who the tools run as, each
  tool's own instructions (Argus's among them), and the tools that are not
  available now with the reason (a plugin whose account the person has not
  connected yet, a server that is down or too slow to answer).
- **Files a tool makes** (a picture, a sound, Python's output) come back as a
  link to `https://DOMAIN/api/chat/attachments/{id}/content`
  (`resource_link`), and pictures and sound up to 4 MB inline as well.
  `resources/read` reads a link with the key; the person opens it in the
  browser, signed in. A client of 2025-03-26 or older gets the links in the
  text. The files are kept with the person's files, outside any chat.
- **Prompts**: the prompt library as the person sees it in the chat's `/`
  menu: their own, their groups', the company's and their plugins'. Each
  `{{variable}}` is an argument; `prompts/get` returns the text filled in, as
  one user message. A name in two places is theirs first, as in the menu.

## Who it runs as

- The key is checked at the gateway by its SHA-256: the key itself never leaves
  the app. The answer is kept for 30 seconds, so a replaced, deleted or blocked
  key stops working within that. A disabled person is refused on their next
  request.
- Argus answers as the person (the chat's token and their email): no GitLab
  token is needed here. A plugin calls its service with the person's own
  account. Pictures and speech go through the gateway on the person's credit;
  pictures and video count against their pictures a day (Settings →
  Safeguards). Pictures, speech, video and each deep research started count
  against their key's requests a minute too, when an admin sets one, as one
  request in the minute the call started however long it runs: past it the
  call is an error saying so, audited as `mcp.rate_limited`. The requests a
  deep research run makes are its chat's, held by the deep research a day and
  the fair line, not by the key's limits
  ([admin.md](admin.md#rate-limits-for-api-keys)).
- **Every call is audited**: `mcp.call`, with the tool (a built-in's id, or the
  plugin's or server's name), the function, and whether it worked, entered when
  it ends at the time it started. A call the client stops is audited too.
- **Refused**: 401 with no key, a key the gateway does not know, a blocked key,
  or a disabled person (the body says which); 503 when the gateway cannot be
  asked; 404 when Arena MCP is off.

## The protocol

- Streamable HTTP, protocol 2025-06-18; 2025-03-26 and 2024-11-05 are
  answered too. Served: `initialize`, `ping`, `tools/list`, `tools/call`,
  `prompts/list`, `prompts/get`, `resources/list`, `resources/templates/list`,
  `resources/read`.
- Requests are `POST /mcp`. `GET` and `DELETE` answer 405: the server sends
  nothing unasked, and there are no sessions to end. It is stateless: each
  request carries the key, a person's tool list is kept a minute, and the
  `Mcp-Session-Id` it gives is only a label.
- **Connecting never waits long for a tool server.** `initialize` and
  `tools/list` start the person's tools; those that are a network round trip
  (Argus, an admin's MCP server, a plugin) start together, beside the others,
  and are waited for 10 seconds at most (`Mcp:ListWaitSeconds`), all together.
  A plugin with a sign-in of each person's own has it read first (and renewed
  if it lapsed) within the same wait, then connects beside the rest. One slower is listed as not available now, with
  that reason, and the list is then kept 10 seconds instead of a minute, so
  the next list asks it again. Before 5.3 they started one after another and
  each could hold the connection for a minute: a slow Argus made Claude Code
  (30 s), Qwen Code and Code Arena give up on Arena MCP.
- `tools/call` answers as server-sent events when the client accepts them: the
  tool's progress as it comes (when the client sent a `progressToken`; Argus
  and MCP servers report it), a comment line every 15 seconds while it runs
  (so a proxy does not drop it), then the answer. `notifications/cancelled`
  stops a call; so does the client hanging up.
- A tool's failure is a result with `isError`, as in the chat. An unknown
  function, or one the person may not use, is a JSON-RPC error (-32602).

## Settings

**Settings → Arena MCP → Arena MCP** (`Mcp:Enabled`, on by default, applies
at once). Off, `/mcp` and `/.well-known/mcp` answer 404 and Connect your tools
leaves it out. **Wait for a slow tool server** (`Mcp:ListWaitSeconds`, 10
seconds, applies at once) is how long connecting waits for Argus, the MCP
servers and the plugins (above).

Traefik sends `/mcp` on `DOMAIN` to the app, beside `/api`, `/connect` and
`/.well-known` (`deploy/config/traefik/routes.yml`).

## How it is tested

`tests/Llm.Tests/ArenaMcpTests.cs` connects with the app's own MCP client and
with plain requests: initialize, list and call with a person's key, each call
audited; no key, a wrong key, a blocked key and a disabled person refused, the
gateway asked once for a while; a group's tool served only to its members, and
a tool that asks first marked; an Argus call made as the person (their email at
Argus); an admin's MCP server proxied with its progress; a call cancelled; a
picture inline and as a link that `resources/read` reads and the person opens;
a server that never answers listed as not available within the wait, and
asked again on the next list, a plugin with a person's own sign-in too.
`tests/Llm.Tests/DeepResearchTests.cs` runs `deep_research` for an agent: a
chat of the person's own does it, its steps come as progress and the report as
the result; not served, saying why (in the agent's notes and on Connect your
tools), while the web asks first, when an unknown function name is still "no
tool named"; gone when the person may not use it.
`src/web/src/pages/setup.test.tsx` checks the setups on Connect your tools.
