"""A plugin's own MCP server, run in the sandbox: no network, a read-only root,
its own user, killed at its time limit (runner.py).

    from sandbox_tools.mcp_server import Server

    server = Server("units", instructions="Lengths between metres and feet.")

    @server.tool("Converts a length.", {"value": {"type": "number"}, "unit": {"type": "string", "enum": ["m", "ft"]}})
    def convert(value, unit):
        return f"{value * 3.28084:.2f} ft" if unit == "m" else f"{value / 3.28084:.2f} m"

    server.run()

The app runs the plugin's server.py once for each call: it writes initialize,
then tools/list or tools/call, one JSON-RPC message a line, and reads the
answers until the server ends at the end of its input. What a tool returns is
the call's text (anything else is sent as JSON); an exception is the call's
error, said to the model. The plugin's settings, as the admin filled them in,
are server.settings (settings.json beside server.py).
"""

import json
import os
import sys

PROTOCOL = "2025-06-18"


class Server:
    def __init__(self, name, version="1.0.0", instructions=None):
        self.name = name
        self.version = version
        self.instructions = instructions
        self._tools = {}

    @property
    def settings(self):
        """The plugin's settings (settings.json), or {} when there are none."""
        try:
            with open(os.path.join(os.getcwd(), "settings.json"), encoding="utf-8") as f:
                return json.load(f)
        except (OSError, ValueError):
            return {}

    def tool(self, description, properties=None, required=None, name=None):
        """A function the model may call: its description, its arguments as JSON Schema properties (each one required unless
        required names some), and its name (the function's own by default)."""
        properties = properties or {}

        def wrap(fn):
            self._tools[name or fn.__name__] = (fn, {
                "name": name or fn.__name__,
                "description": description,
                "inputSchema": {"type": "object", "properties": properties,
                                "required": list(properties) if required is None else list(required)},
            })
            return fn
        return wrap

    def handle(self, message):
        """The answer to one message; None for a notification."""
        method = message.get("method")
        if "id" not in message:
            return None
        ident = message["id"]
        if method == "initialize":
            result = {"protocolVersion": PROTOCOL, "capabilities": {"tools": {}},
                      "serverInfo": {"name": self.name, "version": self.version}}
            if self.instructions:
                result["instructions"] = self.instructions
            return {"jsonrpc": "2.0", "id": ident, "result": result}
        if method == "ping":
            return {"jsonrpc": "2.0", "id": ident, "result": {}}
        if method == "tools/list":
            return {"jsonrpc": "2.0", "id": ident, "result": {"tools": [spec for _, spec in self._tools.values()]}}
        if method == "tools/call":
            params = message.get("params") or {}
            tool = self._tools.get(params.get("name"))
            if tool is None:
                return {"jsonrpc": "2.0", "id": ident, "error": {"code": -32602, "message": f"No tool {params.get('name')!r}."}}
            try:
                value = tool[0](**(params.get("arguments") or {}))
                text = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False, default=str)
                return {"jsonrpc": "2.0", "id": ident, "result": {"content": [{"type": "text", "text": text}], "isError": False}}
            except Exception as error:  # the call's error, for the model to read
                return {"jsonrpc": "2.0", "id": ident,
                        "result": {"content": [{"type": "text", "text": f"{type(error).__name__}: {error}"}], "isError": True}}
        return {"jsonrpc": "2.0", "id": ident, "error": {"code": -32601, "message": f"No method {method!r}."}}

    def run(self, stdin=None, stdout=None):
        """Answers each message on stdin, one a line, until its end."""
        stdin = stdin or sys.stdin
        stdout = stdout or sys.stdout
        for line in stdin:
            line = line.strip()
            if not line:
                continue
            try:
                message = json.loads(line)
            except ValueError:
                answer = {"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "Not JSON."}}
            else:
                answer = self.handle(message)
            if answer is not None:
                stdout.write(json.dumps(answer, ensure_ascii=False) + "\n")
                stdout.flush()
