# Company knowledge

The chat searches the company's own documents: GitLab wikis and issues,
Confluence, SharePoint and OneDrive, folders and websites. Each person finds
only what they may read, with the title and link to cite. Argus stays the
code path; this is the documents path.

The same store also feeds **retrieval**: a chat's long files and an assistant's
files go to the model as the passages that match each question, not their
first part ([below](#long-files-and-assistants)).

## What it needs

- **The embedder**: the `embed` module (llama.cpp with nomic-embed-text, the
  one Argus uses). The app finds it by its name on the stack's network, as it
  finds every module. Without it there is no company knowledge: Admin →
  Knowledge says so, the tool is not offered, and files go into answers as
  before.
- **Postgres with pgvector**: the stack's image has it, and the app turns the
  extension on by itself. On a Postgres without it, the comparison runs in
  plain SQL: the same answers, slower.
- For GitLab sources, **the GitLab bot** (Settings → Scheduled tasks: GitLab
  address and bot token). Its token needs `api` (or `read_api`) and Reporter
  in the projects. It is never Argus's token.
- For Confluence and SharePoint sources, **APP_DATA_KEY**: their token or
  client secret is stored encrypted with it, as the other secrets are.

## Sources

Admin → Knowledge → **Add a source**. Each source is read at once, then again
every **Read sources again every** (Settings → Company knowledge, an hour).
**Sync now** reads it at once. Only what changed is embedded again, and what
is gone is removed, unless part of the source could not be read (then nothing
is removed that time).

| Kind | What is read | Who may read it |
|---|---|---|
| **GitLab** | A project, or every project of a group and its subgroups (not archived ones): the wiki pages, and the issues with their comments. Confidential issues and internal notes never. | Each project's members in GitLab: active, Guest and up, inherited ones too. |
| **Folder** | Every file under a folder mounted below `/knowledge` (subfolders too): text, Markdown, HTML, PDF, Word, Excel, PowerPoint and OpenDocument, as the chat reads attachments. Hidden files and folders and links are skipped; files over 50 MB too. | The groups you choose (or everyone, or admins). |
| **Website** | Pages reached from the first page through their links, on its hosts only (or the hosts you list), up to **Most pages** (200; at most 2,000), minding the site's `robots.txt`. PDFs and documents linked from it too. | The groups you choose (or everyone, or admins). |
| **Confluence** | Cloud, or Data Center and Server: the spaces you list, or every space the account may read; their pages, and their blog posts if you choose. | Whoever may view the space and the page in Confluence, read restrictions included ([below](#confluence)). Where Confluence does not tell, the groups you choose. |
| **SharePoint** | Sites and document libraries (a OneDrive too) by their addresses: their files (Word, PowerPoint, Excel, PDF, text, Markdown and the rest the chat reads), and the sites' pages if you choose. | Each file's permissions in SharePoint ([below](#sharepoint-and-onedrive)). Where Graph cannot tell, the groups you choose. |

Each card shows the state of the last sync (synced, syncing, failed, or
synced with problems), why it failed, how many documents and passages the
source holds, when it was last read, and who may read it: for Confluence and
SharePoint, what the last sync could mirror and where the groups you chose
read instead. **Documents** lists what it holds, with links. **Settings**
changes a source (its kind stays); a change to what it reads syncs it again.

Every add, change, sync now and removal is in the audit log
(`knowledge.add`, `knowledge.change`, `knowledge.sync`, `knowledge.remove`).

### GitLab

- People are matched to GitLab accounts **by username**, as Argus does: a
  person's user name here is their GitLab username (case does not matter).
- A project's members are read at every sync and every 15 minutes besides:
  someone who leaves a project stops finding its documents within 15 minutes.
  A members list that could not be read for a day no longer counts.
- Admins get nothing from a project they are not members of: GitLab's rights
  apply to everyone.
- A project whose last activity has not moved since it was read is not read
  again; once a day it is (GitLab records activity at most hourly). An issue
  is embedded again when its `updated_at` moves, a wiki page when its text or
  title changes.

### Confluence

1. Make an account that only reads: a user with view permission on the
   spaces to read (Data Center: and "Can use"). It needs no admin rights.
2. Its secret:
   - **Cloud**: an API token of that account (id.atlassian.com → Security →
     API tokens). The source takes the site's address
     (`https://your-site.atlassian.net`), the account's email and the token
     (sent as Basic).
   - **Data Center and Server**: a personal access token of that account
     (its profile → Personal Access Tokens; Confluence 7.9 and later). The
     source takes Confluence's address (with its path, if it has one:
     `https://wiki.example.com/confluence`), no email, and the token (sent as
     Bearer).
3. **Spaces**: their keys, comma separated (`ENG, HR`). Empty: every global
   space the account may read (not personal spaces; list one by its key,
   `~jane`, to read it).
4. **Test connection** says who the account is, which spaces it may read,
   and whether Confluence shows it who may view them.

What is read: each page (and blog post, if chosen) as text with its
headings (Confluence's storage format: headings, lists, tables, code blocks;
macros' settings and pictures left out), its title, link and version. Every
sync lists the spaces' pages with their versions and restrictions (50 a
request) and reads the text only of pages whose version moved. A page that
is deleted goes. A listing that fails part way keeps every page of that
space as it was.

Who may read, mirrored from Confluence at every sync:

- **Who may view the space**: its users, and every member of its groups
  (read from Confluence), from the space's permissions. Anonymous access
  makes it everyone's.
- **Read restrictions**: a restricted page, and every page under it, is read
  only by people who pass each restriction (its own and its ancestors'),
  besides viewing the space. A restriction on an ancestor that is not a page
  (a folder) is read for itself; one that cannot be read leaves the page out.
- **People are matched** by their email (Cloud) or username (Data Center): a
  person here whose email, or user name, is the Confluence account's.
- Someone who loses access stops finding the space at the next sync. If
  Confluence cannot be read for a day past the sync's schedule, its people
  no longer count.

What is not mirrored, and goes to **Where Confluence cannot tell** (admins
only unless you choose; it applies at once, without a sync):

- A space whose permissions Confluence does not show the account (some Data
  Center versions do not, through the REST API): its pages are read by the
  groups you chose. The card says which spaces.
- People Confluence names without an email (Cloud shows it only when the
  person's profile lets anyone see it) and groups whose members cannot be
  listed: the space is read by the groups you chose too. In a page
  restriction they are left out: they do not find that page.
- Space roles and admins' rights to see everything: only view permissions and
  read restrictions count.

The REST API used is the one Cloud and Data Center share (`/rest/api`:
`space`, `space/{key}?expand=permissions`, `content/search` with CQL,
`content/{id}?expand=body.storage`, `content/{id}/restriction/byOperation/read`,
the group member listings and `user/current`).

### SharePoint and OneDrive

Through Microsoft Graph, as an app of the company's Microsoft Entra tenant:

1. Microsoft Entra ID → App registrations → **New registration** (single
   tenant, no redirect address).
2. **API permissions** → Microsoft Graph → **Application permissions**:
   - **Sites.Read.All**: every site and OneDrive. Or **Sites.Selected**, and
     grant the app read on each site to read (Graph's
     `POST /sites/{id}/permissions`, or PnP's
     `Grant-PnPAzureADAppSitePermission`).
   - **User.Read.All** (optional): lets a file shared with someone Graph
     names only by their ID be matched by their email.

   Then **Grant admin consent**.
3. **Certificates & secrets** → a client secret. Note when it expires: the
   sync fails with Entra's reason after that, until you put a new one in
   **Settings**.
4. The source takes the tenant's ID (or its domain), the app's client ID and
   secret, and the sites or libraries, one address a line:
   `https://contoso.sharepoint.com/sites/engineering` reads all its document
   libraries (and its pages, if chosen);
   `https://contoso.sharepoint.com/sites/engineering/Shared Documents` (or
   that library's page in the browser) reads that library only. A OneDrive
   is `https://contoso-my.sharepoint.com/personal/jane_contoso_com/Documents`.
5. **Test connection** says which sites and libraries the app reaches.

What is read: files the chat reads as attachments (Word, PowerPoint, Excel,
PDF, text, Markdown, CSV, HTML, OpenDocument and the like; 50 MB at most),
by their text, title, link and version; and site pages (their text web
parts). Each library is read whole once a day, and in between by Graph's
changes (delta queries, with sharing changes): only files that changed, or
whose sharing changed, are read again, and deleted files go. A delta link
Graph no longer knows is read whole. Changes that fail part way are read
again from where they were at the next sync.

Who may read, from each file's permissions in Graph:

- **People** it is shared with, by their email (or their user principal
  name): a person here whose email is that one.
- **Microsoft Entra groups**, by their object ID and their name, as people's
  directory groups: those the company sign-in's groups claim, LDAP or SCIM
  put them in, and the app's groups linked to one (or provisioned by SCIM
  under its name).
- **Everyone except external users**: everyone.
- Sharing links count only for the people they name: a link for anyone, or
  for the whole company, does not make a file everyone's.

What is not mirrored, and goes to **Where SharePoint cannot tell**:

- **SharePoint groups** (a site's Owners, Members and Visitors, and other
  site groups): Graph does not list their members. A file shared with one is
  read by the groups you chose too; most files inherit them from their site,
  so choose the groups that stand for the site's members. The card says how
  many files.
- **Site pages**: Graph does not give their permissions.
- People Graph names without an email (when the app lacks User.Read.All).

### A folder

Mount it read-only under `/knowledge` in `docker-compose.override.yml`, and
add it as `/knowledge/handbook`. A folder outside `/knowledge` is refused.

```yaml
services:
  app:
    volumes:
      - /srv/handbook:/knowledge/handbook:ro
```

The app runs as user 1000: the files must be readable by it. A file is read
again when its time or size changes.

### A website

Every request goes through the chat's web guard: public addresses only
(never the stack or the office network, whatever a name or a redirect points
to), every redirect checked, 20 seconds and 5 MB a page at most. The source's
own hosts replace the chat's allowed sites for its crawl. A page that answers
404 or 410 goes; one that fails otherwise stays as it was until it can be
read.

## In the chat

**Company knowledge** is a tool (Admin → Tools: on, for everyone, in new
chats, until you change it). It is offered once there is a source and the
embedder runs. The model calls `search_knowledge` with what to find; it gets
the best passages the person may read, at most two of one document, each
with its document's title, link, source and section, and cites them as
links. When nothing the person may read matches, it is told to say so.

What the passages say is marked as data, never instructions (Settings →
Safeguards → **Mark the web's content as data**): anyone who can write an
issue, a page or a file wrote it.

## Long files and assistants

With the embedder:

- An **attachment** longer than **Text of an attachment in the question**
  (Settings → Chat, 30,000 characters) goes in as its first 2,000 characters
  and a note; the **passages that match the question** come with each
  question, with the file's name, lines and section.
- An **assistant's files**, when together they do not fit (three times that
  setting), are named in the system prompt and go by their passages the same
  way. Files that fit still go whole.
- Up to **Passages of long files per question** (Settings → Company
  knowledge, 12,000 characters). A short question ("and the second one?")
  is searched with the question before it.
- The model can still read any part with `read_file` and `search_file`.
- Files are embedded in the background: an assistant's file when it is added, an
  attachment when a question first needs it (an answer waits up to 15 seconds
  for it; one not ready goes in as before, and is ready for the next
  question). An assistant may have 200 files.

Without the embedder, files go in as before: whole when they fit, otherwise
their start, and the rest by `read_file`.

## How it works

- A document is cut into passages of about 1,000 characters: at headings
  first (Markdown, AsciiDoc, a PDF's pages), then between paragraphs, a long
  paragraph at line ends. A passage cut for size starts with the end of the
  one before. What is embedded is the document's title and headings, then
  the passage.
- Vectors are of unit length, stored as `real[]` and compared as pgvector's
  `vector` (cosine). The search is exact, over the documents the person may
  read. Documents embedded by another model are left out of a search and
  embedded again at the next sync.
- Every document keeps its readers, any of which may read it: `everyone`,
  `admins`, `group:{id}`, `gitlab:{project}` or `confluence:{space}` (that
  project's members, that space's viewers, kept in their own table),
  `person:{email}`, `directory:{group}`, or `chosen:{source}` (the groups
  chosen for what the source cannot tell, decided at each search). A
  Confluence page's restrictions are what it **requires** besides: a reader
  must pass every one (`confluence:{space}/{page}`).
- Tables: `knowledge_sources`, `knowledge_documents`, `knowledge_chunks`,
  `knowledge_readers`. Removing a source removes its documents and passages.

## Settings and configuration

| Setting (Settings → Company knowledge) | Default | |
|---|---|---|
| Read sources again every | 1 hour | 15 minutes to a week |
| Passages of long files per question | 12,000 characters | |

In configuration (the `Knowledge` section, e.g. `Knowledge__EmbedUrl` in the
environment), for odd setups only:

| key | default | |
|---|---|---|
| `EmbedUrl` | `http://embed:8080` | an OpenAI-compatible embeddings server, without `/v1` |
| `EmbedModel` | `nomic-embed-text` | |
| `EmbedKey` | empty | a bearer key for a server outside the stack |
| `DocumentPrefix`, `QueryPrefix` | `search_document: `, `search_query: ` | what nomic-embed-text wants; empty for a model that wants none |
| `FolderRoot` | `/knowledge` | where folders must be |
| `IndexWait` | 15 seconds | how long an answer waits for its files to be embedded |
| `PgVector` | true | false compares in plain SQL |

## What comes next

Google Drive and Jira, each a connector of its own (`IKnowledgeConnector` in
`src/Llm.Api/Knowledge`: a kind, a check of its settings, and its documents
with their readers; `IConnectionTest` for Test connection). Plugins will be
able to bring a connector (`knowledge:` in the manifest).

The Confluence and SharePoint connectors are proven against fakes of their
APIs, not yet against a live Confluence or tenant.

## How it is tested

`tests/Llm.Tests/KnowledgeTests.cs`, against the fake GitLab (two projects
with members, wikis and issues), a fake embedder (texts that share words
point the same way), a folder and a fake website:

- a person without access to a project's wiki gets nothing from it (a
  blocked member and an admin who is no member neither), and a member gets the
  passage with its link and section;
- a sync reads only what changed, removes what is gone, and someone who leaves
  a project stops finding it; a failed sync keeps what it had;
- a folder and a website are read by the groups chosen, outside the root is
  refused, robots.txt and other hosts are kept out, and changing readers
  applies at once (this one without pgvector);
- without the embedder, there is no knowledge and the page says why;
- Confluence (`KnowledgeConnectorTests.cs`, against a fake Data Center and
  Cloud): someone who may not view a space gets nothing and a viewer gets
  the passage and link; a restricted page and the page under it go only to
  whom the restriction names; a new version is read and embedded again
  alone, a deleted page goes, a restriction added applies at the next sync,
  a listing failing half way keeps everything, a refused token fails and
  keeps it; hidden permissions and emails fall back to the groups chosen;
- SharePoint (against a fake Graph): files read by the people and Entra
  groups they are shared with, a SharePoint group and site pages by the
  groups chosen; delta links read only what changed (and sharing changes), a
  deleted file goes, failing half way reads the same changes again, a link
  Graph no longer knows reads the library whole; Test connection, and
  secrets stored encrypted and never sent back;
- a project of 60 files answers about any of them from its passages, with
  no file inlined; a long attachment goes as its start and the passage that
  answers.
