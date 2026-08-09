"""A working agent loop against a live Argus MCP server.

This is the client half of the measurements in docs/pack-measurements.md, in a
form Hermes can copy. It connects over MCP, reads the tool schemas and the
server's instructions from the server itself, and drives an Ollama model with
NATIVE function calling.

Three things here are load-bearing, each because the alternative was measured
and was worse:

**Native function calling, not a text protocol.** A loop that asked the model
to emit "CALL <tool> <arg>" scored 10/20 and called a tool on 6 of 20
questions; telling it to check first made that *worse*, 4/20, because the
extra prose crowded out the output format. The same instruction under
/api/chat with a `tools` schema improved things instead. There is no format to
break when the model emits a structured tool_calls entry.

**The server's own instructions as the system message.** Argus sends an
`instructions` block at connect time (server.SERVER_INSTRUCTIONS). Passing it
through took tool use from 3 of 20 questions to 8, and accuracy from 12/20 to
14/20. An agent that ignores it leaves that on the table.

**Tool schemas read from the server, not hard-coded here.** The descriptions
carry measured guidance -- when docs_search is the wrong tool, why docs_get
exists -- and a client that paraphrases them loses it.

    python deploy/agent_client_example.py http://localhost:8080/mcp <token> \\
        "Which import library must I link to call CryptAcquireContextW?"
"""
from __future__ import annotations

import asyncio
import json
import sys
import urllib.request

from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client

OLLAMA_CHAT = "http://localhost:11434/api/chat"
MODEL = "qwen3.6:35b"
MAX_TURNS = 8


def to_openai_schema(tool) -> dict:
    """One MCP tool as the function schema Ollama expects."""
    return {
        "type": "function",
        "function": {
            "name": tool.name,
            "description": tool.description or "",
            "parameters": tool.inputSchema or {"type": "object", "properties": {}},
        },
    }


def chat(messages: list[dict], schemas: list[dict] | None) -> dict:
    body = {
        "model": MODEL, "messages": messages, "stream": False,
        # Thinking is off for latency; it changes nothing about tool choice.
        "think": False,
        "options": {"temperature": 0, "num_predict": 400},
    }
    if schemas:
        body["tools"] = schemas
    req = urllib.request.Request(
        OLLAMA_CHAT, data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=900) as resp:
        return json.load(resp).get("message", {}) or {}


def render(result) -> str:
    """MCP content blocks as text the model can read."""
    parts = []
    for block in getattr(result, "content", []) or []:
        text = getattr(block, "text", None)
        if text:
            parts.append(text)
    return "\n".join(parts)[:4000] or "(no result)"


async def main() -> None:
    url, token, question = sys.argv[1], sys.argv[2], " ".join(sys.argv[3:])

    async with streamablehttp_client(
        url, headers={"Authorization": f"Bearer {token}"}
    ) as (read, write, _):
        async with ClientSession(read, write) as session:
            init = await session.initialize()

            listed = await session.list_tools()
            schemas = [to_openai_schema(t) for t in listed.tools]

            messages: list[dict] = []
            # The server's instructions ARE the system prompt. Measured worth
            # +5 questions of tool use and +2 answers; do not paraphrase them.
            instructions = getattr(init, "instructions", None)
            if instructions:
                messages.append({"role": "system", "content": instructions})
            messages.append({"role": "user", "content": question})

            for turn in range(MAX_TURNS):
                msg = chat(messages, schemas)
                calls = msg.get("tool_calls") or []
                if not calls:
                    print(msg.get("content", "").strip())
                    return

                messages.append({
                    "role": "assistant",
                    "content": msg.get("content", ""),
                    "tool_calls": calls,
                })
                for call in calls:
                    fn = call.get("function", {}) or {}
                    name = fn.get("name", "")
                    args = fn.get("arguments") or {}
                    if isinstance(args, str):
                        try:
                            args = json.loads(args)
                        except json.JSONDecodeError:
                            args = {}
                    print(f"  -> {name}({json.dumps(args)[:80]})", file=sys.stderr)
                    try:
                        result = await session.call_tool(name, args)
                        text = render(result)
                    except Exception as exc:
                        # Hand the failure back rather than aborting: a model
                        # that sees "no such tool" picks another, while a
                        # traceback ends the task.
                        text = f"(tool error: {type(exc).__name__}: {exc})"
                    messages.append({"role": "tool", "content": text})

            # Out of turns. Ask once with no tools so it has to commit rather
            # than loop until the caller gives up.
            messages.append({"role": "user",
                             "content": "Give your final answer now."})
            print(chat(messages, None).get("content", "").strip())


if __name__ == "__main__":
    if len(sys.argv) < 4:
        print(__doc__)
        raise SystemExit(2)
    asyncio.run(main())
