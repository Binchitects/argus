# Where people already are

Argus Arena outside its own page: a bot in Slack, Mattermost and Teams, email
in, the app installed on a phone or a desktop with push notifications, and a
browser extension. Each person is always answered as themselves: their model
access, tools, credit and rights, as in the chat.

| | address the platform calls | checked by |
|---|---|---|
| Slack | `https://DOMAIN/api/bots/slack` | Slack's request signature (the app's signing secret), at most five minutes old |
| Mattermost | `https://DOMAIN/api/bots/mattermost` | the outgoing webhook's or slash command's token |
| Microsoft Teams | `https://DOMAIN/api/bots/teams` | the Bot Connector's signed token (JWT), against the Bot Framework's published keys |
| Email in | `https://DOMAIN/api/mail/inbound` | a shared secret |

Admin → Settings → **Chat bots** holds every setting below, and lists these
addresses with whether each platform is set up.

## Chat bots

A question to the bot (a mention in a channel, a direct message, a Mattermost
trigger word or slash command) is answered like this:

1. **Who asked**: the platform is asked for the person's email address (Slack's
   profile, Mattermost's account, Teams' member), and the account here with the
   same address answers. Someone without one is told so, politely, in the
   thread; so is someone whose account is disabled. Each refusal is in the
   audit log (`bot.refused`).
2. **Their chat for the thread**: the first question of a thread starts a chat
   of theirs, titled with the platform (`Slack · What does the parser do?`);
   the next questions in the thread carry it on. It is in their chat list like
   any other, and they can go on there. A second question while the first is
   answered waits for it.
3. **The answer**: with the bot's model and tools (or the ones a new chat of
   theirs would use), of those they may use, in turn with everyone else's
   answers, against their credit; the safeguards check the question as in the
   chat. **What the bots are told** (an instruction for every bot chat) asks
   for short answers in plain Markdown. Nobody in the thread can press
   **Allow**, so the model is not offered **Deep research** while it asks
   first (Admin → Tools); with asking turned off it may start one, and the
   thread gets the report.
4. **Back in the thread**: the answer, cut to what a post holds (Slack 4,000
   characters, Mattermost 16,000, Teams 12,000) with a link to the whole of it
   in the chat. Slack's Markdown is converted (bold, links, headings).

Per platform an admin sets the credentials (secrets: write-only, stored
encrypted), the **channels** it answers in (empty: every channel it is in;
direct messages always) and its **model** and **tools** (tool ids from Admin →
Tools, comma separated; `none` for no tools). In a channel not on the list the
bot says it does not answer there.

**The answer is posted where the question was**: everyone in the channel reads
it, though it was written with the asker's rights (a private repository Argus
reads for them, a plugin acting as them). Keep the channel list to channels
whose members may see what their askers see, or leave the bots to direct
messages. The person's email on the platform must be the one here, and
verified there: an admin who lets anyone set an unverified email on the
platform lets them be answered as someone else.

### Slack

1. Create a Slack app (api.slack.com/apps). Under **OAuth & Permissions** add
   the bot scopes `app_mentions:read`, `chat:write`, `im:history`, `users:read`
   and `users:read.email`, and install it in the workspace.
2. Settings → Chat bots: **Slack: signing secret** (Basic Information) and
   **Slack: bot token** (`xoxb-…`).
3. Under **Event Subscriptions** turn events on, with the Request URL
   `https://DOMAIN/api/bots/slack` (Slack checks it at once), and subscribe to
   the bot events `app_mention` and `message.im`. For direct messages, turn on
   **App Home → Messages tab**.
4. Invite the bot to the channels (`/invite @argus`).

Slack must reach the address from the internet. An event Slack sends again
(it waits three seconds) is answered once.

### Mattermost

1. A bot account (System Console → Integrations → Bot accounts; then
   Integrations → Bot accounts → Add) and its access token. The server must
   let it see email addresses (System Console → Site configuration → Users and
   teams → **Show email address**, or a bot with the system role that sees
   them). Add the bot to the channels.
2. An **outgoing webhook** (trigger word such as `@argus`, callback URL
   `https://DOMAIN/api/bots/mattermost`), or a **slash command** (`/ask`,
   POST to the same URL), or both. Their tokens go into **Mattermost:
   webhook and command tokens**, comma separated.
3. Settings → Chat bots: **Mattermost: server** (`https://chat.example.com`)
   and **Mattermost: bot token**.

A post that starts with the trigger word is answered in its thread (a reply in
a thread, in that thread). A slash command tells the asker "Asking as you…"
and posts the answer in the channel, quoting the question, as a thread of its
own (and a chat of its own). Mattermost must allow the callback's address:
System Console → Developer → **Allow untrusted internal connections** if the
app is on your network.

### Microsoft Teams

1. An **Azure Bot** (Azure portal), with a Microsoft App ID and a client secret;
   multi-tenant, or single-tenant with its tenant id. Its **messaging
   endpoint** is `https://DOMAIN/api/bots/teams`; turn on the **Microsoft
   Teams** channel.
2. Settings → Chat bots: **Teams: app ID**, **Teams: app password**, and for a
   single-tenant bot **Teams: tenant**.
3. A Teams app package (manifest) for the bot, uploaded by your Teams admin, so
   people can add it to teams and chats.

Every activity the Bot Connector posts carries a token it signed. The app
checks it against the keys the Bot Framework publishes
(`login.botframework.com`, read once a day), for this bot's App ID, the
issuer `https://api.botframework.com`, its expiry, the key's endorsement for
the activity's channel (`msteams`), and the service address the activity names.
Anything else is refused (401). The app signs in to Microsoft with the client
secret to read the member's email and post the reply. Microsoft must reach the
address from the internet, and the app must reach `login.botframework.com`,
`login.microsoftonline.com` and the Bot Connector (`smba.trafficmanager.net`).

## Email in

A mail gateway (Postmark, Mailgun, SendGrid's Inbound Parse, or your own relay
with a webhook) posts each email it receives to
`https://DOMAIN/api/mail/inbound`, as a form or JSON with `from`, `to`,
`subject` and `text`, and the secret in `X-Mail-Secret`, as a bearer token, or
as the password of the address (`https://any:SECRET@DOMAIN/api/mail/inbound`,
for gateways that only take a URL).

- The sender, found by their address, gets the answer by email (Settings →
  Email must be set), with a link to the chat. The chat is theirs, titled
  `Email · <subject>`; a reply with the same subject (`Re: …`) carries it on.
- A sender without an account gets **nothing back**: a sender's address is
  easily forged, and answering strangers would make the app a mail cannon.
  It is audited (`bot.refused`). So is an email from the app's own address (a
  loop), and one the gateway marks as forged (`spf` or `dkim` given, and not
  `pass`).
- **Email in: tools** is `none` by default, for the same reason: a forged
  sender cannot read the answer (it goes to the real person), but tools would
  act as them. Add tools only when your gateway checks senders (SPF, DKIM,
  DMARC) and drops what fails.

## The installable app and push notifications

The web app installs as an app on a phone or a desktop: Chrome and Edge offer
**Install** (also in the account menu and under Your account when the browser
offers it); on Android, the menu's **Add to home screen**; on an iPhone or iPad,
Safari's **Share → Add to Home Screen**. It opens in its own window, with the
product's name (Settings → Branding) and icon.

Its service worker keeps the app's shell (the page and its files), so the app
starts without a network, but never keeps an answer of the API: what it shows
is always current. Push notifications send the bell's news to each device that turned
them on, with the app closed (see [chat.md](chat.md#notifications)):

- The app signs pushes with its own VAPID key, made on first use and kept
  encrypted under `APP_KEY` (the settings table, row `webpush:vapid`); each
  push is encrypted for the device (RFC 8291) so the push service carries it
  unread.
- The app posts only to the browsers' push services: `Push:Hosts` in the app's
  configuration, by default `fcm.googleapis.com, *.push.services.mozilla.com,
  *.push.apple.com, *.notify.windows.com`. The app needs to reach them.
- A device the push service no longer knows is removed. `Push:PerPerson`
  devices per person (20).
- Browsers allow service workers and pushes only on a trusted certificate:
  with the stack's own CA, each device must trust it first.

## The browser extension

`clients/browser-extension/` (Chrome, Edge, Firefox): right-click a selection or
a page, or use the toolbar button, to ask Argus Arena about it. It opens the
app's `/ask` page with the page's title, address and text in the address's
fragment (never sent to a server); the person signs in as usual, asks, and a
chat starts with the page attached as a file. With their API key in the
options, the popup also asks the gateway directly. Loading it, and installing
it for a whole company, are in [its README](../clients/browser-extension/README.md).

## How it is tested

- Each platform against a fake of its API (`FakeChatPlatforms`): Slack's
  signature (a wrong secret, a stale timestamp), a mention answered in its
  thread as the person matched by email, a reply carrying the chat on, a
  stranger refused politely and audited, channels not on the list declined,
  direct messages answered; a Slack answer not offered deep research while it
  asks first, and offered once it does not; Mattermost's tokens, a reply
  answered in its thread's first post, a slash command; Teams' token checks
  (another bot's audience, another key, another issuer, another service
  address, a key not endorsed for the channel) and the reply; email in with a
  fake SMTP server.
- Web Push: the encryption against RFC 8291's own test vector; a subscription,
  a notification pushed and decrypted as the browser does, its VAPID signature
  checked; a device the push service forgot removed.
- The extension's pure functions with `node --test`.
- **Not run against the real platforms**: no Slack workspace, Teams tenant or
  Mattermost server was used (a Mattermost image needs a download, which was
  not made), and the extension and the installed app were not driven in a real
  browser.
