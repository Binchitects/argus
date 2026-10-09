import { matchPath } from "react-router";

// The help for each page of Argus's own app: what it is for, what each part does, and the
// common tasks step by step. "Help" in the sidebar shows the page's; /help shows them all.

export interface Topic {
  title: string;
  about: string;
  parts: [name: string, text: string][];
  tasks: { title: string; steps: string[] }[];
  admin?: boolean;
}

/** Every route of App.tsx. Each needs its help below: the record's type says so. */
export type ArgusRoute = "/login" | "/" | "/chat/:id" | "/settings" | "/help" | "/manage" | "/manage/people" | "/manage/indexing" | "/manage/repositories" | "/manage/explore" | "/manage/packs";

export const help: Record<ArgusRoute, Topic> = {
  "/login": {
    title: "Signing in",
    about: "Sign in with the account an admin made for you.",
    parts: [["Username or email", "Either works. A new account's password was given to you once, by an admin."]],
    tasks: [{ title: "When you cannot sign in", steps: ["Check your username and password.", "Still refused? Ask an admin to reset your password (Administration → People)."] }],
  },
  "/": {
    title: "Chat",
    about: "Ask about your organisation's code and the API documentation: where something is implemented, what breaks if a header changes, which repository a change belongs in.",
    parts: [
      ["Conversations", "Your chats, newest first. New chat starts another."],
      ["Model", "The model that answers. Each answer can call the code index's tools; their calls and results show under it."],
      ["Message", "Enter sends, Shift+Enter starts a new line. Stop ends an answer and keeps what it wrote."],
    ],
    tasks: [{ title: "Ask about the code", steps: ["Press New chat.", "Ask, naming the repository or the symbol if you know it.", "Open a tool call under the answer to see what the index returned."] }],
  },
  "/chat/:id": {
    title: "A chat",
    about: "One conversation: carry it on, or start a new one from the list.",
    parts: [
      ["The answers", "Each with the tool calls it made, their arguments and results."],
      ["Message", "Enter sends, Shift+Enter starts a new line."],
    ],
    tasks: [],
  },
  "/settings": {
    title: "Settings & keys",
    about: "Your password and your keys: code index keys for MCP clients, and model API keys for the gateway.",
    parts: [
      ["Password", "Change it with your current one."],
      ["Code index keys", "For editors and agents that speak MCP (Claude Code, Continue, Qwen Code). A key sees exactly the repositories your GitLab account can read."],
      ["Model API keys", "For tools that call the models through the gateway, with its base URL. They spend from your budget."],
    ],
    tasks: [
      {
        title: "Connect Claude Code to the code index",
        steps: ["Under Code index keys, name a key (laptop) and create it.", "Copy it now: it is shown once.", "Point Claude Code at the MCP address shown above, with the key as a bearer token."],
      },
    ],
  },
  "/help": {
    title: "Help",
    about: "The help of every page in one place.",
    parts: [["Each page", "What it is for, what its parts do, and the common tasks. Help in the sidebar shows the page you are on."]],
    tasks: [],
  },
  "/manage": {
    title: "Overview",
    about: "Everything the service is made of, at a glance.",
    admin: true,
    parts: [
      ["Tiles", "Services up, people, spend, the code index and the knowledge packs."],
      ["Services", "Each service with its state and how long it took to answer."],
    ],
    tasks: [{ title: "When the index is empty or stale", steps: ["Open Indexing.", "Start a pass with Index all repositories."] }],
  },
  "/manage/people": {
    title: "People",
    about: "Who can sign in, what they may do, and how much of the model they have used.",
    admin: true,
    parts: [
      ["Everyone", "Each person with their role, status, spend against budget and last sign-in. Edit, Reset password and Delete per person."],
      ["GitLab", "How the code index knows what a person may read: by their email, or a GitLab username when the email does not match."],
      ["Add a person", "A password is made and shown once."],
    ],
    tasks: [{ title: "Add a person", steps: ["Fill in Username, Email and Display name (and a budget, if any).", "Press Add person.", "Give them the password now: it is shown once."] }],
  },
  "/manage/indexing": {
    title: "Indexing",
    about: "The code index: what it covers and what it is doing.",
    admin: true,
    parts: [
      ["Schedule", "How often it reindexes itself (ARGUS_INDEX_INTERVAL), and pushes queued from GitLab."],
      ["Index all repositories", "Starts a pass now. Branches adds branches or globs (develop release/*) besides each default branch."],
      ["Repositories", "Each repository and branch, when it was last indexed, and how it went."],
    ],
    tasks: [{ title: "Bring the index up to date", steps: ["Press Index all repositories.", "Follow the run here; a repository that failed says why."] }],
  },
  "/manage/repositories": {
    title: "Repositories",
    about: "Every repository the index holds, one row each whatever token listed it: find them, follow their indexing, and change many at once.",
    admin: true,
    parts: [
      ["Search and filters", "Find repositories by name, path or group, and filter by state (indexed, indexing, failed, never, stale), group, language and whether they are on."],
      ["Progress", "Each repository's own progress while it is indexed: queued, fetching, reading files, symbols, embedding, done or failed with why."],
      ["Log", "A repository's log in plain sentences: what was done, how long it took, and what to do about a warning or an error."],
      ["Schedule", "When each repository is indexed again by itself: off, every few hours, daily or weekly; the default for all is on the Indexing page."],
      ["Change several", "Select repositories (or every one the filters show), then turn them on or off, index them now, change their schedule, or remove them. Each one's result is shown."],
    ],
    tasks: [{ title: "Reindex the failed ones", steps: ["Filter by Failed.", "Select all.", "Press Index now, and follow each one's progress."] }],
  },
  "/manage/explore": {
    title: "Explore",
    about: "What the index actually holds, so \"the tool found nothing\" can be told apart from \"it was never indexed\".",
    admin: true,
    parts: [
      ["Search the index", "Symbols and files whose names contain your words, in one repository or every one."],
      ["Repositories, Symbols, Files", "What was found. A file with 0 symbols is one the extractor did not recognise."],
    ],
    tasks: [],
  },
  "/manage/packs": {
    title: "Knowledge packs",
    about: "Public documentation in one file each: prose, API symbols and embeddings. The docs tools answer from them.",
    admin: true,
    parts: [
      ["Packs installed", "Each pack's version and licence. Remove takes one out; Update all packs gets newer ones."],
      ["Pack library", "Built packs on the server. Load adds one at once, Unload takes it out."],
      ["Install pack", "From a URL or a path on the server, with its SHA-256."],
    ],
    tasks: [{ title: "Add a pack", steps: ["Press Load on it in the Pack library.", "Or paste its URL and SHA-256 and press Install pack."] }],
  },
};

/** For an address no route knows, or an admins' page someone else is on: no page's help, but where to find it. */
export const noHelp: Topic = {
  title: "This page",
  about: "This page has no help of its own.",
  parts: [["Every page's help", "What each page is for, what its parts do, and the common tasks: the link below."]],
  tasks: [],
};

const routes = Object.keys(help) as ArgusRoute[];

/** The help for an address (the most specific route that matches it); admins' pages for admins only. */
export function helpFor(pathname: string, isAdmin: boolean): Topic {
  const route = routes.filter((r) => matchPath(r, pathname)).sort((a, b) => b.length - a.length)[0];
  const topic = route ? help[route] : noHelp;
  return topic.admin && !isAdmin ? noHelp : topic;
}
