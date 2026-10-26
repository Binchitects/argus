"""The chat's tools as Python functions, for a program run_code runs (in Arena's sandbox, or Code Arena's).

    import arena
    arena.tools.search("find a symbol")        # which functions there are, by what they do
    help(arena.argus.find_symbol)              # its parameters and what it does
    hits = arena.argus.find_symbol(name="DecodeFrame")

Each function is one of the tools the chat has on in this answer, grouped by where it comes from (arena.web,
arena.argus, arena.knowledge, ...). A call goes to the chat, which runs it as the person: their rights, the
safeguards, the record of tool calls. What a call returns is the tool's result: a dict or a list when it is JSON,
else its text. A call the tool refuses raises arena.ToolError with its words. Only what the program prints (and
the files it writes) goes back to the chat: filter, join and sum here, and print what matters.
"""

import inspect
import json
import keyword
import os
import re
import socket
import sys
import types

__all__ = ["ToolError", "call", "tools"]

_DEFINITIONS = ".arena-tools.json"


class ToolError(Exception):
    """A call the tool refused or could not do: its words, to fix the call and try again."""


_conn = None


def _line(request):
    global _conn
    path = os.environ.get("ARENA_SOCKET")
    if not path:
        raise ToolError("This program cannot call the chat's tools: it was not run by run_code.")
    if _conn is None:
        sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        sock.connect(path)
        _conn = sock.makefile("rwb")
    _conn.write((json.dumps(request) + "\n").encode("utf-8"))
    _conn.flush()
    answer = _conn.readline()
    if not answer:
        raise ToolError("The chat stopped answering calls.")
    return json.loads(answer)


def call(tool, /, **args):
    """Calls the chat's tool `tool` (its function name, as the definitions give it) with these arguments (any names, a tool's
    own `tool` or `name` among them)."""
    answer = _line({"name": tool, "args": {k: v for k, v in args.items() if v is not None}})
    if not answer.get("ok"):
        raise ToolError(answer.get("error") or f"{tool} failed.")
    result = answer.get("result")
    if isinstance(result, str):
        text = result.strip()
        if text[:1] in ("{", "["):
            try:
                return json.loads(text)
            except ValueError:
                pass
    return result


def _identifier(name):
    """A Python name for a tool's or a group's name: letters, digits and _, not starting with a digit or a keyword."""
    clean = re.sub(r"\W", "_", name).strip("_") or "tool"
    if clean[0].isdigit():
        clean = "_" + clean
    return clean + "_" if keyword.iskeyword(clean) else clean


def _function(definition):
    """A function for one tool: keyword parameters from its JSON schema, its description as the docstring."""
    name = definition["name"]
    schema = definition.get("parameters") or {}
    properties = schema.get("properties") or {}
    required = set(schema.get("required") or [])
    params = []
    for key in sorted(properties, key=lambda k: (k not in required, k)):
        if not key.isidentifier() or keyword.iskeyword(key):
            continue
        params.append(inspect.Parameter(key, inspect.Parameter.KEYWORD_ONLY, default=inspect.Parameter.empty if key in required else None))

    def tool(**args):
        missing = [k for k in required if k not in args or args[k] is None]
        if missing:
            raise ToolError(f"{name} needs {', '.join(sorted(missing))}.")
        return call(name, **args)

    lines = [definition.get("description") or name, ""]
    for key, prop in properties.items():
        what = prop.get("description") or prop.get("type") or ""
        lines.append(f"{key}{'' if key in required else ' (optional)'}: {what}")
    tool.__name__ = tool.__qualname__ = _identifier(definition.get("function") or name)
    tool.__doc__ = "\n".join(lines).strip()
    tool.__signature__ = inspect.Signature(params)
    return tool


def _load():
    """The tools of this answer, as the chat gave them beside the program (ARENA_TOOLS names the file; none outside run_code)."""
    try:
        with open(os.environ.get("ARENA_TOOLS") or _DEFINITIONS, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return []


_definitions = _load()
_groups = {}
for _d in _definitions:
    group = _identifier(_d.get("group") or "tools")
    if group in ("tools", "call", "ToolError"):
        group += "_"
    module = _groups.setdefault(group, types.ModuleType(f"arena.{group}", f"The chat's tools from {group}."))
    fn = _function(_d)
    setattr(module, fn.__name__, fn)
    _d["path"] = f"arena.{group}.{fn.__name__}"
for _group, _module in _groups.items():
    setattr(sys.modules[__name__], _group, _module)
    sys.modules[f"arena.{_group}"] = _module
    __all__.append(_group)


class _Tools:
    """What there is: arena.tools.list() every function, arena.tools.search("words") those whose words match."""

    def list(self):
        return [f"{d['path']}: {(d.get('description') or '').splitlines()[0][:120]}" for d in _definitions]

    def search(self, words, most=10):
        asked = [w for w in re.findall(r"\w+", str(words).lower()) if len(w) > 2]
        scored = []
        for d in _definitions:
            text = f"{d['path']} {d.get('description') or ''}".lower()
            score = sum(text.count(w) for w in asked)
            if score:
                scored.append((score, f"{d['path']}: {(d.get('description') or '').splitlines()[0][:120]}"))
        return [line for _, line in sorted(scored, key=lambda s: -s[0])[:most]]

    def __repr__(self):
        return "\n".join(self.list())


tools = _Tools()
