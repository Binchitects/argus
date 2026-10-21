"""The browser agent's browser: Chromium headless with its DevTools on 127.0.0.1, and a forwarder that lets the app
reach them from its network.

Chromium answers DevTools only to a Host of localhost or an address, so the forwarder puts "localhost" in each
connection's first request (the HTTP JSON endpoints, or a WebSocket's upgrade) and then passes bytes both ways. Pages
reach the web only through the app's proxy (BROWSER_PROXY), which checks every site.
"""

import asyncio
import os
import signal
import subprocess
import sys

CHROMIUM_PORT = 9223
LISTEN_PORT = int(os.environ.get("BROWSER_PORT", "9222"))
PROXY = os.environ.get("BROWSER_PROXY", "http://app:3128")


def start_chromium() -> subprocess.Popen:
    return subprocess.Popen([
        "chromium", "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
        "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync",
        "--disable-background-networking", "--disable-component-update", "--metrics-recording-only",
        "--password-store=basic", "--mute-audio", "--hide-scrollbars",
        f"--proxy-server={PROXY}", "--proxy-bypass-list=<-loopback>",
        f"--remote-debugging-port={CHROMIUM_PORT}", "--remote-debugging-address=127.0.0.1", "--remote-allow-origins=*",
        "--user-data-dir=/tmp/profile", "--window-size=1280,800", "about:blank",
    ])


async def pipe(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
    try:
        while data := await reader.read(65536):
            writer.write(data)
            await writer.drain()
    except (ConnectionError, OSError):
        pass
    finally:
        try:
            writer.close()
        except OSError:
            pass


async def serve(client_reader: asyncio.StreamReader, client_writer: asyncio.StreamWriter) -> None:
    try:
        head = await asyncio.wait_for(client_reader.readuntil(b"\r\n\r\n"), timeout=10)
    except (asyncio.IncompleteReadError, asyncio.LimitOverrunError, asyncio.TimeoutError, ConnectionError):
        client_writer.close()
        return
    lines = head.split(b"\r\n")
    rewritten = [b"Host: localhost:%d" % CHROMIUM_PORT if l.lower().startswith(b"host:") else l for l in lines]
    try:
        up_reader, up_writer = await asyncio.open_connection("127.0.0.1", CHROMIUM_PORT)
    except OSError:
        client_writer.write(b"HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n")
        client_writer.close()
        return
    up_writer.write(b"\r\n".join(rewritten))
    await up_writer.drain()
    await asyncio.gather(pipe(client_reader, up_writer), pipe(up_reader, client_writer))


async def main() -> None:
    chromium = start_chromium()
    loop = asyncio.get_running_loop()
    stop = loop.create_future()
    for sig in (signal.SIGTERM, signal.SIGINT):
        loop.add_signal_handler(sig, lambda: stop.done() or stop.set_result(None))
    server = await asyncio.start_server(serve, "0.0.0.0", LISTEN_PORT)
    async with server:
        while not stop.done():
            if chromium.poll() is not None:
                print(f"chromium ended ({chromium.returncode}): starting it again", flush=True)
                chromium = start_chromium()
            await asyncio.wait([stop], timeout=2)
    chromium.terminate()
    try:
        chromium.wait(timeout=10)
    except subprocess.TimeoutExpired:
        chromium.kill()
    sys.exit(0)


if __name__ == "__main__":
    asyncio.run(main())
