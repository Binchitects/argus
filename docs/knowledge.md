# Company knowledge

The chat searches the company's own documents: GitLab wikis and issues,
folders and websites. Each person finds only what they may read, with the
title and link to cite. Argus stays the code path; this is the documents path.

The same store also feeds **retrieval**: a chat's long files and a project's
files go to the model as the passages that match each question, not their
first part ([below](#long-files-and-projects)).

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

Each card shows the state of the last sync (synced, syncing, failed, or
synced with problems), why it failed, how many documents and passages the
source holds, when it was last read, and who may read it. **Documents** lists
what it holds, with links.

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
issue or a page wrote it.

## Long files and projects

With the embedder:

- An **attachment** longer than **Text of an attachment in the question**
  (Settings → Chat, 30,000 characters) goes in as its first 2,000 characters
  and a note; the **passages that match the question** come with each
  question, with the file's name, lines and section.
- A **project's files**, when together they do not fit (three times that
  setting), are named in the system prompt and go by their passages the same
  way. Files that fit still go whole.
- Up to **Passages of long files per question** (Settings → Company
  knowledge, 12,000 characters). A short question ("and the second one?")
  is searched with the question before it.
- The model can still read any part with `read_file` and `search_file`.
- Files are embedded in the background: a project's file when it is added, an
  attachment when a question first needs it (an answer waits up to 15 seconds
  for it; one not ready goes in as before, and is ready for the next
  question). A project may have 200 files.

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
- Every document keeps its readers: `everyone`, `admins`, `group:{id}`, or
  `gitlab:{project}` (that project's members, kept in their own table).
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

Confluence, SharePoint and OneDrive, Google Drive and Jira, each a connector
of its own (`IKnowledgeConnector` in `src/Llm.Api/Knowledge`: a kind, a check
of its settings, and its documents with their readers). They need an
instance or a vendor's sandbox to prove against. Plugins will be able to
bring a connector (`knowledge:` in the manifest).

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
- a project of 60 files answers about any of them from its passages, with
  no file inlined; a long attachment goes as its start and the passage that
  answers.
