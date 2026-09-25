"""A minimal MCP streamable-HTTP client: initialize, then JSON-RPC over POST.

Deliberately independent of both SDKs under test, so it cannot share a bug with
either of them.
"""
import json, sys, urllib.request


class Session:
    def __init__(self, base, token, headers=None):
        self.base = base.rstrip('/') + '/mcp'
        self.token = token
        self.headers = headers or {}
        self.sid = None
        self.n = 0
        r = self.rpc('initialize', {"protocolVersion": "2025-06-18", "capabilities": {},
                                    "clientInfo": {"name": "probe", "version": "1"}})
        self.init = r
        self.notify('notifications/initialized')

    def _post(self, payload):
        data = json.dumps(payload).encode()
        req = urllib.request.Request(self.base, data=data, method='POST')
        req.add_header('Content-Type', 'application/json')
        req.add_header('Accept', 'application/json, text/event-stream')
        req.add_header('Authorization', 'Bearer ' + self.token)
        req.add_header('MCP-Protocol-Version', '2025-06-18')
        if self.sid:
            req.add_header('mcp-session-id', self.sid)
        for k, v in self.headers.items():
            req.add_header(k, v)
        try:
            resp = urllib.request.urlopen(req, timeout=60)
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode(), dict(e.headers)
        return resp.status, resp.read().decode(), dict(resp.headers)

    def notify(self, method, params=None):
        payload = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            payload["params"] = params
        return self._post(payload)

    def rpc(self, method, params=None):
        self.n += 1
        payload = {"jsonrpc": "2.0", "id": self.n, "method": method}
        if params is not None:
            payload["params"] = params
        status, body, headers = self._post(payload)
        for k, v in headers.items():
            if k.lower() == 'mcp-session-id':
                self.sid = v
        if status != 200:
            raise RuntimeError(f"{method}: HTTP {status}: {body[:300]}")
        if 'event-stream' in headers.get('Content-Type', headers.get('content-type', '')):
            for line in body.splitlines():
                if line.startswith('data: '):
                    msg = json.loads(line[6:])
                    if msg.get('id') == self.n:
                        return msg
            raise RuntimeError('no response in stream: ' + body[:300])
        return json.loads(body)

    def call(self, name, args):
        return self.rpc('tools/call', {"name": name, "arguments": args})
