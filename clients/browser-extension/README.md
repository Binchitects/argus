# Argus Arena for the browser

A browser extension (Manifest V3, plain JavaScript, Chrome, Edge and Firefox) that
asks Argus Arena about the page you are on, or what you selected on it.

- **Right-click** a selection: *Ask Argus Arena about "…"*; or anywhere on a
  page: *Ask Argus Arena about this page*.
- **The toolbar button**: *Ask about the selection* or *Ask about this page*.

Both open the app's `/ask` page in a new tab, with the page's title, address and
text (or the selection). You sign in to the app as usual; there you ask your
question, and a new chat starts with the page attached as a file. The text
travels in the address's fragment (`#…`), which the browser never sends to a
server; the app keeps it in that tab while you sign in, then takes it out of
the address. Up to 100,000 characters of a page are handed over.

**Ask here** (optional): with your API key in the options, the popup also asks
the gateway directly (`https://gateway.DOMAIN/v1/chat/completions`) and shows
the answer, without a chat in the app. The key stays in this browser
(`storage.local`); the questions use your credit. The browser asks once to allow
the extension to reach the gateway's address.

## Load it unpacked

**Chrome or Edge**: `chrome://extensions` (or `edge://extensions`) → turn on
**Developer mode** → **Load unpacked** → choose this folder. Then the
extension's **Details** → **Extension options**, and set the app's address
(`https://llm.example.com`).

**Firefox** (128 or later): `about:debugging#/runtime/this-firefox` → **Load
Temporary Add-on…** → choose `manifest.json` in this folder. A temporary add-on
lasts until Firefox restarts; for good, sign it with `web-ext sign` (your AMO
key) or install it through your company's Firefox policies.

For everyone in a company: Chrome and Edge install it by policy
(`ExtensionInstallForcelist` with a packed `.crx` on your own server); Firefox
through `policies.json` (`ExtensionSettings`).

## What it may do

| permission | why |
|---|---|
| `contextMenus` | the right-click entries |
| `activeTab`, `scripting` | read the page's title, text and selection, only in the tab you clicked in, when you click |
| `storage` | the app's address, and the API key if you set one |
| optional: the gateway's address | asking in the popup, once you allow it |

It reads no page by itself and sends nothing anywhere until you click.

## Test

The pure functions (the app's address, the hand-over address, the gateway
request and its answer) with node's own test runner, no packages:

```bash
node --test clients/browser-extension/lib.test.js
```
