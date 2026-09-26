"""Tools the chat calls by name, run in the sandbox like any other job.

The app sends a one-line program:

    from sandbox_tools import run; run("calc.calculate", "{\"expression\": \"2^10\"}")

The arguments arrive as a JSON string (data, never code) and only functions
named tool_* in these modules can be called. run() prints one JSON line:
{"ok": true, "result": ...} or {"ok": false, "error": "..."}. Files a tool
writes in the working directory go back to the person, as with any job.
"""

import importlib
import json
import sys

MODULES = {"calc", "dates", "data", "convert", "diagram"}


class ToolError(Exception):
    """A problem with what was asked: said to the model as it is, to fix and retry."""


def run(name, args_json):
    module, _, function = name.partition(".")
    try:
        if module not in MODULES or not function.isidentifier():
            raise ToolError(f"There is no tool {name}.")
        args = json.loads(args_json)
        if not isinstance(args, dict):
            raise ToolError("The arguments must be an object.")
        fn = getattr(importlib.import_module(f"sandbox_tools.{module}"), "tool_" + function, None)
        if fn is None:
            raise ToolError(f"There is no tool {name}.")
        # Arguments the model sent as null are the same as not sent.
        result = fn(**{k: v for k, v in args.items() if v is not None})
        out = {"ok": True, "result": result}
    except ToolError as e:
        out = {"ok": False, "error": str(e)}
    except TypeError as e:
        out = {"ok": False, "error": f"Wrong arguments: {e}"}
    except Exception as e:  # anything else, told plainly: the model fixes its input
        out = {"ok": False, "error": f"{type(e).__name__}: {e}"}
    sys.stdout.write(json.dumps(out, ensure_ascii=False, default=str) + "\n")


def cap(text, limit=6000):
    """Long results are cut, and say so: they cost the model's context."""
    text = str(text)
    return text if len(text) <= limit else text[:limit] + f" …[{len(text) - limit} more characters]"
