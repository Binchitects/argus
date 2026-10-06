/**
 * The help for each page: what it is for, what each part does, and the common tasks step by
 * step. The ? at the top of every page shows its topic (help/route-help.ts says which); the
 * manual shows them all as one page (help/pages-doc.ts). **Words** in bold are the page's own
 * labels. Keep it plain and short, as the docs are.
 */

export interface HelpPart {
  name: string
  text: string
}

export interface HelpTask {
  /** What it does, as "Add a person". */
  title: string
  steps: string[]
}

/** A link into the manual: a page (its id under /help) and one of its headings. */
export interface ManualLink {
  doc: string
  section?: string
}

export interface HelpTopic {
  /** The page's name, as the sidebar calls it. */
  title: string
  /** What the page is for. */
  about: string
  parts: HelpPart[]
  tasks: HelpTask[]
  /** Where the manual tells the whole story. */
  manual?: ManualLink
  /** A page for admins: its help is for them only. */
  admin?: boolean
  /**
   * Help for each part a page shows one at a time, by the address's anchor or last segment:
   * Settings' groups (#company-directory-ldap), each dashboard (/admin/dashboards/gpu-hardware).
   */
  sections?: Record<string, HelpPart>
  /** What those parts are called: "This group", "Its groups". */
  sectionNames?: { one: string; all: string }
}

export const topics = {
  app: {
    title: 'Around every page',
    about: 'What stays the same on every page: the sidebar, the bar at the top, and your account menu.',
    parts: [
      { name: 'Sidebar', text: 'Every page you may open, in groups: **Workspace** for everyone, and **Administration**, **Argus** and **Observe** for admins. **Collapse** at its foot keeps only the icons. On a phone it opens from the menu button.' },
      { name: 'Manual', text: 'At the foot of the sidebar and in your account menu: this manual, with search.' },
      { name: 'Version and Source', text: 'Under the sidebar: the version you use, and a link to its source.' },
      { name: 'Search (Ctrl K)', text: 'Goes to any page or runs a command (a theme, sign out). Type part of a name.' },
      { name: '?', text: 'Help for the page you are on: this panel. It stays open while you work on the page.' },
      { name: 'The bell', text: 'Your news: answers that finished while you were away, scheduled tasks, your credit, and for admins the alerts. **Desktop notifications** at its foot say the same on your desktop.' },
      { name: 'Your account menu', text: 'Your picture at the top right: **Your account**, **Theme**, **Width**, **Manual**, **Get help** when your admins set a contact, **Install the app** when the browser offers it, and **Sign out**.' },
    ],
    tasks: [
      { title: 'Find a page fast', steps: ['Press Ctrl K (⌘ K on a Mac).', 'Type part of its name, or what it is about ("api key", "credit").', 'Press Enter.'] },
      { title: 'Change the theme or the width', steps: ['Open your account menu (your picture, top right).', 'Choose **Theme** (light, dark or the system\'s) or **Width** (Comfortable, Wide or Full width).'] },
    ],
  },

  home: {
    title: 'Home',
    about: 'Where you land after signing in: the quickest ways in, and your credit.',
    parts: [
      { name: 'Start a chat', text: 'Opens the chat, with Argus searching the code you can read in GitLab.' },
      { name: 'Your usage', text: 'Your tokens, cache hits and cost, over time and by model.' },
      { name: 'Connect your tools', text: 'Your API key, and the steps for Claude Code, Qwen Code, your editor or a script.' },
      { name: 'Your credit', text: 'What you spent this month, through the chat and your API key together, of your credit.' },
      { name: 'System', text: 'For admins: the services, people, total spend and who is over credit, with a link to **Overview**.' },
    ],
    tasks: [{ title: 'Ask your first question', steps: ['Press **Start a chat**.', 'Type your question in the box at the bottom.', 'Press Enter.'] }],
    manual: { doc: 'chat', section: 'what-it-does' },
  },

  chat: {
    title: 'Chat',
    about: 'Ask the models questions, with files, tools and the code you may read. Each chat is yours alone.',
    parts: [
      { name: 'The chat list', text: 'On the left: **New chat**, search, your assistants, your chats by date, and **Archived chats** at the bottom. Each chat\'s menu renames, forks, archives or deletes it. On a narrow screen it opens from the chats button.' },
      { name: 'Model and Thinking', text: 'At the top: the model this chat uses, and how hard it thinks. **Auto** lets a small model pick, when your admins set one up. A chat keeps its model.' },
      { name: 'Memory', text: 'The brain at the top: what the chat remembers about you, for every chat. Only you see it.' },
      { name: 'Chat settings', text: 'The sliders at the top: your own instructions for this chat, and its temperature, top-p and longest answer.' },
      { name: 'Canvas and Files', text: '**Canvas** opens documents and code that you and the model both edit. **Files** lists every file of the chat: what you attached, what Argus read, what the model wrote.' },
      { name: '⋯ (chat actions)', text: 'Fork, compact, share, move to an assistant, export, archive or delete this chat.' },
      { name: 'The answers', text: 'Under each answer: the model, time, tokens and cost; thumbs up or down; **Answer again** (also with another model, shorter or longer); **Fork from here**; read aloud. Arrows like 2 / 3 switch between versions of a question or an answer.' },
      { name: 'The message box', text: 'The paperclip attaches files (or paste or drop them). **Tools** chooses what the model may use in this chat. **Deep research** and **Compare** apply to the next message. The microphone records a voice message; **Talk** is a voice conversation.' },
      { name: 'Context', text: 'The gauge beside Send: how full the model\'s window is, and what fills it. **Compact now** summarizes older messages so the chat can go on lighter.' },
      { name: '/ in the box', text: 'At the start of a message, / finds a saved prompt (Workspace → Prompts) and fills in its blanks.' },
    ],
    tasks: [
      { title: 'Ask about a file', steps: ['Press the paperclip, or drop the file on the page.', 'Wait until it shows as attached.', 'Type your question and press Enter.'] },
      { title: 'Let the model use a tool', steps: ['Press **Tools** in the message box.', 'Turn on the tool (Argus, Python, Web, image generation…).', 'Ask your question: the model calls the tool when it needs it. A tool marked as asking first waits for your **Allow**.'] },
      { title: 'Try another model on the same question', steps: ['Open **Answer again** under the answer.', 'Choose the model (or a thinking level).', 'Both answers stay: the arrows switch between them.'] },
      { title: 'Compare two models blind', steps: ['Press **Compare** beside Deep research.', 'Choose two models, or leave it to chance.', 'Send your question, read both answers, and vote. The names show after your vote, and the vote counts on the Leaderboard.'] },
      { title: 'Share a chat', steps: ['Open **⋯** at the top right of the chat.', 'Choose **Share**.', 'Choose who may open it (everyone in the company, or chosen groups) and what it shows. The link is copied. **Revoke link** stops it.'] },
      { title: 'Use a saved prompt', steps: ['Type / at the start of the message.', 'Choose the prompt with the arrows and Enter.', 'Fill in each blank (Enter moves to the next) and send.'] },
    ],
    manual: { doc: 'chat', section: 'what-it-does' },
  },

  assistant: {
    title: 'An assistant',
    about: 'One assistant\'s page: what every chat with it starts from, and your chats with it.',
    parts: [
      { name: 'Start a chat', text: 'A new chat with the assistant: its instructions, files, model, thinking and tools. A **conversation starter** asks its first question in one click.' },
      { name: 'Instructions', text: 'What every answer with it reads: who it is for and how to answer. Those who may edit it change them here.' },
      { name: 'Files', text: 'Files every answer can read. **Add files** to add more (up to 200).' },
      { name: 'Your chats with it', text: 'Only yours: other people\'s chats with it stay theirs.' },
      { name: 'Settings', text: 'Its name, what it is for, icon and colour, model, thinking, tools and conversation starters.' },
      { name: 'Sharing', text: 'Who may use it (only you, chosen groups, or everyone, which admins choose) and who may edit it. Only its owner shares or removes it.' },
    ],
    tasks: [
      { title: 'Share it with your team', steps: ['Open **Sharing**.', 'Choose **Chosen groups** and tick your team\'s group.', 'Add the people or groups who may edit it, if any, and press **Save sharing**.'] },
      { title: 'Move an existing chat to it', steps: ['Open the chat.', 'Open **⋯** at the top right, then **Move to assistant**.', 'Choose this assistant.'] },
    ],
    manual: { doc: 'chat', section: 'assistants' },
  },

  shared: {
    title: 'A shared chat',
    about: 'A chat someone shared with you, read only. Fork it to go on in a chat of your own.',
    parts: [
      { name: 'The messages', text: 'The chat as its owner shared it: the whole chat, or one branch. Arrows switch between versions where there are several.' },
      { name: 'Files', text: 'The chat\'s files, to open or download.' },
      { name: 'Fork into my chats', text: 'Copies it, with its files, into a chat of your own that you can write in. **Fork from here** under an answer copies it up to that answer.' },
      { name: 'Revoke link', text: 'For its owner only: the link stops working at once, for everyone.' },
    ],
    tasks: [{ title: 'Carry on from a shared chat', steps: ['Press **Fork into my chats**.', 'The copy opens as your own chat: write in it as in any other.'] }],
    manual: { doc: 'chat', section: 'shared-chats' },
  },

  assistants: {
    title: 'Assistants',
    about: 'Instructions, files and settings that a chat starts from, made once and shared with a team (like custom GPTs or Claude\'s projects).',
    parts: [
      { name: 'The gallery', text: 'Every assistant you may use: yours, those shared with your groups, and the company\'s. Each shows who made it, how far it is shared, and how much it is used.' },
      { name: 'Search and Show', text: 'Find one by name, or show only some of them.' },
      { name: 'New assistant', text: 'Makes one: a name and what it is for, then its instructions, files and settings on its page. It is yours alone until you share it.' },
      { name: 'Start a chat and Open', text: '**Start a chat** opens a new chat with it; **Open** shows its page.' },
    ],
    tasks: [
      { title: 'Make an assistant for a task you repeat', steps: ['Press **New assistant**.', 'Give it a name and say what it is for, then press **Create**.', 'On its page, write its **Instructions** and add **Files**.', 'Under **Settings**, choose a model, tools and a few conversation starters, and save.'] },
    ],
    manual: { doc: 'chat', section: 'assistants' },
  },

  prompts: {
    title: 'Prompts',
    about: 'Prompts you use often, with blanks you fill in each time. Type / in the chat to use one.',
    parts: [
      { name: 'Yours', text: 'Prompts you made. Only you change them; you may share them with your groups.' },
      { name: 'Shared with you', text: 'From people in your groups.' },
      { name: 'For everyone', text: 'The company\'s, kept by admins.' },
      { name: 'From plugins', text: 'They come and go with their plugin.' },
      { name: 'New prompt', text: 'A slash name (lowercase letters, digits, - and _), a title, and the text. Write a blank as {{name}}.' },
    ],
    tasks: [
      { title: 'Make a prompt with blanks', steps: ['Press **New prompt**.', 'Give it a slash name, like review, and a title.', 'Write the text with blanks: Review {{file}} for {{focus}}.', 'Choose who uses it, and save.', 'In the chat, type /review: a field opens for each blank.'] },
    ],
    manual: { doc: 'chat', section: 'prompts-and-slash-commands' },
  },

  tasks: {
    title: 'Scheduled tasks',
    about: 'Questions asked for you on a schedule or on an event, with your model and tools: a morning digest, a weekly report, a review of each merge request.',
    parts: [
      { name: 'The tasks', text: 'Each card shows when it runs (in words, with the next runs), its last run, and a link to that run\'s chat.' },
      { name: 'Run now', text: 'Runs it at once, to see what it brings.' },
      { name: 'New task', text: 'A name, what to ask, and when it runs: **On a schedule**, **On GitLab events** or **On a webhook**. Also its model and tools, and where the answer goes.' },
      { name: 'Delivery', text: 'Every run is a chat and a note under the bell. It can also email you the answer, post it to a channel\'s webhook, or answer as a comment in GitLab.' },
      { name: 'New secret', text: 'For tasks run by events: a new secret for its address. The old one stops working at once.' },
    ],
    tasks: [
      { title: 'Get a morning digest', steps: ['Press **New task**.', 'Name it and write what to ask, as you would in the chat.', 'Under **Runs**, choose **On a schedule**, then **Weekdays** at 08:00 in your time zone.', 'Choose the tools it needs (Argus, Web) and save.', 'Press **Run now** once to check the answer.'] },
      { title: 'Review each merge request in GitLab', steps: ['Press **New task** and choose **On GitLab events**.', 'Tick **A merge request opened or updated**, and **Answer as a comment in GitLab**.', 'Save: the task shows its address and a secret, once.', 'In GitLab, add a webhook to the project or group with that address and secret.'] },
    ],
    manual: { doc: 'chat', section: 'scheduled-tasks' },
  },

  usage: {
    title: 'Usage & cost',
    about: 'Tokens and cost over time and by model. Input is split into cache miss and cache hit, which are priced differently.',
    parts: [
      { name: 'Time range', text: 'The period the page shows.' },
      { name: 'Everyone and Mine', text: 'Admins see everyone\'s usage, and their own under **Mine**.' },
      { name: 'The totals', text: 'Requests, input (cache miss and cache hit), output and cost.' },
      { name: 'Tokens by kind and Cost', text: 'The same, over time.' },
      { name: 'By model', text: 'What each model was used for, and what it cost.' },
    ],
    tasks: [{ title: 'See what you spent this month', steps: ['Choose the time range.', 'Read **Cost**; **By model** says where it went. Chats and API calls show here within a minute.'] }],
    manual: { doc: 'chat', section: 'cost-and-credit' },
  },

  leaderboard: {
    title: 'Leaderboard',
    about: 'The company\'s models on its own questions: people compare two models blind in the chat and vote. Each vote moves an Elo rating.',
    parts: [
      { name: 'Models by rating', text: 'Each model\'s rating, wins, losses, ties and win rate.' },
      { name: 'Compare', text: 'How votes are cast: **Compare** in the chat\'s message box sends a question to two models, their names hidden until you vote.' },
    ],
    tasks: [{ title: 'Cast a vote', steps: ['Open the chat and press **Compare**.', 'Send a question and read both answers.', 'Vote for the better one, a tie, or both bad. The names show after your vote.'] }],
    manual: { doc: 'chat', section: 'what-it-does' },
  },

  setup: {
    title: 'Connect your tools',
    about: 'Use the models from your own tools: a coding agent, your editor, a script. They sign in with your API key and spend from your credit.',
    parts: [
      { name: 'Trust this site\'s certificate', text: 'Shown when the site uses a private certificate: the file to download, its fingerprint, and how to install it so your tools trust the site.' },
      { name: 'API key', text: 'Your key for every tool. **New key** makes one and shows it once; the old one stops working.' },
      { name: 'The address, and your models', text: 'The gateway\'s address (OpenAI-compatible, and Anthropic\'s), and the models you may use with what each can do.' },
      { name: 'Set up your tool', text: 'Choose your system and your tool (Code Arena, Claude Code, Qwen Code, Continue, Python, curl, GitLab CI…), then follow its steps. Each ends with a check that it works.' },
      { name: 'Arena MCP', text: 'Your chat tools for your own agent, at one address, with your API key.' },
      { name: 'Argus, the code index', text: 'Argus\'s address for coding agents over MCP. It answers as your GitLab account: only from code you may read.' },
    ],
    tasks: [
      { title: 'Set up Claude Code (or another tool)', steps: ['Under **API key**, press **New key** and copy it.', 'Run the line that keeps the key for every terminal, then open a new terminal.', 'Choose your system and your tool under **Set up your tool**.', 'Run its steps in order; the last one checks that it works.'] },
    ],
    manual: { doc: 'mcp', section: 'connecting' },
  },

  account: {
    title: 'Your account',
    about: 'Your profile, API key, answers, memory, sign-in security and appearance.',
    parts: [
      { name: 'API key', text: 'Your key for your tools. **New key** replaces it; the old one stops working at once.' },
      { name: 'Answers', text: 'How long answers are, in every chat: Short, Normal or Thorough.' },
      { name: 'Memory', text: 'What the chat remembers about you. Add, edit or delete one, **Forget everything**, or turn memory off. Nobody else sees it, admins included.' },
      { name: 'Appearance', text: 'Theme and width, remembered on this device.' },
      { name: 'Your data', text: 'A zip of your chats, files, assistants, tasks and settings, and how long chats are kept.' },
      { name: 'Connections', text: 'Your own account at services the chat\'s tools work with (plugins). Only you can use what you connect.' },
      { name: 'Push notifications', text: 'The bell\'s news on this device, even with the app closed. **Turn on for this device**, send a test, or remove a device.' },
      { name: 'Two-factor sign-in and Password', text: 'A code from your phone at every sign-in, and your password. People who sign in with the company directory or company sign-in change these there.' },
    ],
    tasks: [
      { title: 'Turn on two-factor sign-in', steps: ['Under **Two-factor sign-in**, press **Set up**.', 'Scan the picture with an authenticator app on your phone (or type its key).', 'Enter the code it shows and press **Turn on**.', 'Keep the recovery codes somewhere safe: they are shown only once.'] },
      { title: 'Get your chats out', steps: ['Under **Your data**, press **Download your data**.', 'You get a zip: each chat as JSON and Markdown, with your files.'] },
    ],
    manual: { doc: 'chat', section: 'memory' },
  },

  ask: {
    title: 'Ask about a page',
    about: 'The browser extension opens this page with the page you are on, or what you selected on it. Your question starts a new chat with it attached.',
    parts: [
      { name: 'What you are asking about', text: 'The page or the selection the extension sent: the model reads it as a file.' },
      { name: 'Your question', text: 'What you want to know about it. **Ask** starts the chat.' },
    ],
    tasks: [{ title: 'Ask about a web page', steps: ['On the page, use the extension (or select text first).', 'Type your question here.', 'Press **Ask**.'] }],
    manual: { doc: 'integrations', section: 'the-browser-extension' },
  },

  design: {
    title: 'Design system',
    about: 'The components this web is built from, in their states. For the people who build the app.',
    parts: [{ name: 'Sections', text: 'Buttons, badges, forms, dialogs, panels, menus, toasts and tabs, each as the app uses it.' }],
    tasks: [],
  },

  manual: {
    title: 'Manual',
    about: 'How to use the app, and how to run it. The ? at the top of every page opens the help for that page.',
    parts: [
      { name: 'Search the manual', text: 'Finds every section that has all your words, best first. A result opens the page at that section.' },
      { name: 'Contents', text: 'The pages of the manual, in groups. The page you read lists its sections below its name.' },
      { name: 'Every page, explained', text: 'The help of every page of the app in one place: what it is for, its parts, and the common tasks.' },
      { name: 'Links', text: 'Links between pages stay in the app. A file of the repository that is not in the manual shows as its name.' },
    ],
    tasks: [{ title: 'Find how to do something', steps: ['Type a few words in **Search the manual** (such as "api key" or "credit").', 'Open the best result.', 'Copy the address to send someone straight to that section.'] }],
  },

  login: {
    title: 'Signing in',
    about: 'Sign in with your account, your company directory login, or your company account.',
    parts: [
      { name: 'Username and password', text: 'Your username or email and your password. With the company directory, it is your directory login.' },
      { name: 'Sign in with your company', text: 'When your company set it up: signs you in at the company\'s identity provider.' },
      { name: 'Keep me signed in', text: 'This device stays signed in for longer. Leave it off on a shared computer.' },
      { name: 'Two-factor code', text: 'When you turned it on: the code from your authenticator app, or one of your recovery codes.' },
    ],
    tasks: [
      { title: 'When you cannot sign in', steps: ['Check your username and password; after several wrong tries the account waits a while before it can try again.', 'Lost your phone? Use a recovery code instead of the two-factor code.', 'Still locked out? Ask an admin: they can reset your password or two-factor sign-in.'] },
    ],
  },

  overview: {
    title: 'Overview',
    about: 'The stack at a glance, refreshed every 30 seconds.',
    admin: true,
    parts: [
      { name: 'The numbers', text: 'Services up, people and admins, total spend, who is at or past their credit, app replicas, the code index and the certificate.' },
      { name: 'Services', text: 'Each service as probed from inside the stack, with why it is down.' },
      { name: 'Code index', text: 'Whether Argus\'s index is current; when it is not, which repositories and why.' },
      { name: 'Certificate', text: 'Days until the site\'s certificate expires and who issued it, and what to do near the end.' },
    ],
    tasks: [{ title: 'Find out why the chat does not answer', steps: ['Look at **Services**: a service down says why.', 'For the model, open Models to see whether it is loaded.', 'For more, open Logs and choose its container.'] }],
    manual: { doc: 'admin', section: 'admin' },
  },

  people: {
    title: 'People',
    about: 'Everyone who can sign in: their role, credit and sign-in security.',
    admin: true,
    parts: [
      { name: 'The table', text: 'Each person with their spend and credit and last sign-in. **Show** filters to admins, the disabled or those over credit. A row opens the person.' },
      { name: 'Bulk actions', text: 'Tick people, then **Set credit**, **Disable**, **Enable** or **Sign out everywhere**.' },
      { name: 'Add person', text: 'A local account with a password and an API key, shown once. People from the company directory or company sign-in appear by themselves at their first sign-in.' },
      { name: 'Export CSV', text: 'Everyone, with spend and credit left.' },
    ],
    tasks: [
      { title: 'Add a person', steps: ['Press **Add person**.', 'Use their GitLab username, so Argus knows what they may read; add their email and name.', 'Press **Add person**, and give them the password and API key now: they are shown only once.'] },
      { title: 'Raise someone\'s credit', steps: ['Tick them in the table.', 'Press **Set credit** and type the new amount (empty: no limit).', 'API keys get it at once, the chat within about a minute.'] },
    ],
    manual: { doc: 'authentication', section: 'managing-people' },
  },

  person: {
    title: 'A person',
    about: 'One person: their profile, access, credit, API key, groups and data.',
    admin: true,
    parts: [
      { name: 'Profile', text: 'Name, username and email; **Change name**.' },
      { name: 'Access', text: '**Reset password**, **Reset 2FA**, **Sign out everywhere**, **Make admin** and **Disable** (signed out, their API keys stop until enabled again).' },
      { name: 'Credit and API key', text: 'Their spend against their credit, **Set credit**, and **New API key** (the old one stops at once).' },
      { name: 'Groups', text: 'The groups they are in: these decide which tools and models they may use.' },
      { name: 'Legal hold', text: 'While on hold nothing of theirs is deleted. **Export their data** for eDiscovery. Both are audited.' },
      { name: 'Delete', text: 'Removes them, their chats and their API keys. Their usage stays in the reports.' },
    ],
    tasks: [{ title: 'Help someone who lost their phone', steps: ['Press **Reset 2FA**.', 'They sign in with their password and can turn it on again.'] }],
    manual: { doc: 'authentication', section: 'managing-people' },
  },

  groups: {
    title: 'Groups',
    about: 'Who may use which tools and models. An app group has the people you add; a directory group follows the company directory or the identity provider.',
    admin: true,
    parts: [
      { name: 'The list', text: 'Each group, its kind (App, Directory or SCIM), its members and its rules. A row opens the group.' },
      { name: 'New group', text: 'An app group (you add the people) or a directory group (by its name or DN in the directory).' },
      { name: 'Chargeback', text: 'Below the list: each month\'s spend per group or cost centre, as CSV.' },
    ],
    tasks: [{ title: 'Give a tool to one team only', steps: ['Press **New group**, make it, and add the team\'s people (or name their directory group).', 'Open Tools, edit the tool, and choose **Chosen groups** with this group.'] }],
    manual: { doc: 'admin', section: 'admin' },
  },

  group: {
    title: 'A group',
    about: 'One group: its members, its priority, and its policies.',
    admin: true,
    parts: [
      { name: 'Members', text: 'Who is in it. In an app group, **Add people** and **Remove**; a directory or SCIM group is filled from outside.' },
      { name: 'Edit', text: 'Its name, description, and **Priority in the answers** (-10 to 10): when the model is busy, higher goes first.' },
      { name: 'Policies', text: 'How long members\' chats are kept, a credit a month (shared or each member\'s), a cost centre, and which safeguards apply to them.' },
      { name: 'Delete', text: 'Tools and models given to it stop being available to its members.' },
    ],
    tasks: [{ title: 'Give a team a monthly credit', steps: ['Under **Policies**, type **Credit a month ($)**.', 'Choose whether it is shared by the members or each member\'s.', 'Press **Save policies**: the gateway has it within a minute.'] }],
    manual: { doc: 'admin', section: 'credit-for-groups' },
  },

  tools: {
    title: 'Tools',
    about: 'What the chat\'s model may call, and for whom: built-in tools, MCP servers and APIs.',
    admin: true,
    parts: [
      { name: 'Each tool', text: 'On or off, who may use it (everyone, admins, or chosen groups), **On in new chats**, and **Ask before each call**.' },
      { name: 'Add a server or API', text: 'An MCP server by its address, or an API by its OpenAPI document. It is tested before it is added; its key is stored encrypted and never shown.' },
      { name: 'Its certificate', text: 'For https: check it (the default), trust a CA you paste, or do not check. A test that meets an untrusted certificate says why.' },
      { name: 'Longest call', text: 'For a server whose tools run long: up to 24 hours.' },
    ],
    tasks: [
      { title: 'Add an MCP server', steps: ['Press **Add a server or API** and choose **MCP server**.', 'Give it a name, its address, and its key as a header if it needs one.', 'Test it, then save. Choose who may use it.'] },
      { title: 'Turn on the web for the chat', steps: ['Turn on **Web** here.', 'Allow sites under Settings → Python and web → **Sites the chat may open** (or * for any public site).'] },
    ],
    manual: { doc: 'admin', section: 'admin' },
  },

  plugins: {
    title: 'Plugins',
    about: 'Ready-made tools for the chat: an API or an MCP server with its sign-in. Installed, each is a tool under Tools, where you choose who may use it.',
    admin: true,
    parts: [
      { name: 'Installed', text: 'Your plugins, with **Settings**, **Who may use it** and **Remove**.' },
      { name: 'Catalog', text: 'Plugins that come with the app, or from a catalog set under Settings → Plugins. **Install** adds one.' },
      { name: 'Upload a zip and From an address', text: 'Install one that is not in the catalog. With its SHA-256, only that exact file is installed.' },
    ],
    tasks: [{ title: 'Install a plugin', steps: ['Press **Install** on it in the catalog.', 'Fill in its settings (its address, a key), and save.', 'Choose who may use it under **Who may use it** (or in Tools).'] }],
    manual: { doc: 'plugins', section: 'installing' },
  },

  knowledge: {
    title: 'Knowledge',
    about: 'The company\'s documents the chat searches, each person only what they may read: GitLab wikis and issues, Confluence, SharePoint and OneDrive, folders and websites.',
    admin: true,
    parts: [
      { name: 'Sources', text: 'Each source with its last sync, its documents and passages, and who may read it. **Sync now**, **Documents**, **Settings** and **Remove**.' },
      { name: 'Add a source', text: 'A GitLab project or group, Confluence, SharePoint or OneDrive, a folder mounted under /knowledge, or a website. Confluence and SharePoint have **Test connection**.' },
      { name: 'Who may read it', text: 'GitLab, Confluence and SharePoint keep their own rights; for a folder or a website you choose the groups.' },
    ],
    tasks: [{ title: 'Add a Confluence space', steps: ['Press **Add a source** and choose **Confluence**.', 'Give the site\'s address, a reading account and its API token, and the space keys.', 'Press **Test connection**, then add it. It syncs, and the chat finds it with the **Company knowledge** tool.'] }],
    manual: { doc: 'knowledge', section: 'sources' },
  },

  'sign-in': {
    title: 'Sign-in',
    about: 'Who can sign in, and how: local accounts, the company directory, and company sign-in. The rules and limits are under Settings.',
    admin: true,
    parts: [
      { name: 'Local accounts', text: 'Always on. Admins add people under People. **Sessions and limits** opens their settings.' },
      { name: 'Company directory (LDAP)', text: 'When set up: the server, the admin and required groups, and how often people are checked. **Check the directory now** disables people who left.' },
      { name: 'Company sign-in', text: 'OIDC or SAML at your identity provider: its admin and required groups, and whether SCIM is on. **Configure** opens its settings.' },
    ],
    tasks: [{ title: 'Turn on the company directory', steps: ['Press **Configure** on the directory card (Settings → Company directory).', 'Fill in the server, the service account and where people are.', 'Press **Test connection**, then save.'] }],
    manual: { doc: 'authentication', section: 'the-company-directory-ldap--active-directory' },
  },

  models: {
    title: 'Models',
    about: 'Every model at the gateway, and who may use each. The engine holds several at once: the ones kept loaded stay, the others load when asked for.',
    admin: true,
    parts: [
      { name: 'The engine', text: 'How many models it holds, which are kept loaded, and how full each GPU would be.' },
      { name: 'Each model', text: 'Its state (loaded, loading, not loaded, could not load), **Keep loaded**, **Load**, **Unload**, **Edit**, and who may use it.' },
      { name: 'Working hours', text: 'Other models kept loaded at some hours: a small fast one by day, the big one at night.' },
      { name: 'Other GPU servers', text: 'Another machine\'s OpenAI-compatible engine, whose models the gateway serves beside these.' },
      { name: 'Find on Hugging Face', text: 'Search GGUF models, see whether they fit this machine, and download them into the library. **Downloads** shows the progress.' },
      { name: 'Add a model', text: 'A GGUF file from the model library. The form shows what fits this machine.' },
    ],
    tasks: [
      { title: 'Switch the chat to another model', steps: ['Press **Load** on the model (or **Keep loaded** to keep it).', 'Wait while it loads: seconds from memory, minutes from disk.', 'Set **Model new chats use** under Settings → Model to make it the default.'] },
      { title: 'Download a model', steps: ['Press **Find on Hugging Face** and search.', 'Open a result, tick a file that fits, and press **Download**.', 'When it is in, press **Add as a model** under **Downloads**.'] },
    ],
    manual: { doc: 'admin', section: 'models' },
  },

  settings: {
    title: 'Settings',
    about: 'Everything about this deployment, in groups. Each setting says what it does, and when it applies: at once, or after the app restarts.',
    admin: true,
    parts: [
      { name: 'Groups', text: 'On the left (a list on a phone): one group at a time. A number shows how many of its settings were changed.' },
      { name: 'Search all settings', text: 'Finds settings by name, key or explanation, across every group.' },
      { name: 'Each setting', text: 'Its name, what it does, when it applies (**Applies at once** or **After a restart**), its key, and where its value comes from. **Back to the default** removes a saved value.' },
      { name: 'Unsaved changes', text: 'The bar at the bottom: **Save changes** or **Discard**. A change that can stop a service asks first.' },
      { name: 'Restart the app now', text: 'Shown when a saved change waits for a restart. It takes a few seconds.' },
    ],
    tasks: [
      { title: 'Change a setting', steps: ['Choose its group, or search for it.', 'Change the value.', 'Press **Save changes**. If it applies after a restart, press **Restart the app now**.'] },
    ],
    manual: { doc: 'settings', section: 'when-a-change-applies' },
    sectionNames: { one: 'This group', all: 'Its groups' },
    sections: {
      branding: { name: 'Branding', text: 'The product\'s name, the sign-in page\'s headline, where people get help, and where the source is.' },
      'sign-in-and-sessions': { name: 'Sign-in and sessions', text: 'How long sessions last, and how many wrong passwords lock an account or an address, and for how long.' },
      'company-directory-ldap': { name: 'Company directory (LDAP)', text: 'Sign-in with the company directory (LDAP or Active Directory): the server, a read-only service account, where people and groups are, and the admin and required groups. **Test connection** tries the values before you save them.' },
      'company-sign-in': { name: 'Company sign-in', text: 'Sign-in at the company\'s identity provider, by OIDC or SAML: the provider, its admin and required groups, and the button\'s label. Below the settings: what to register at the provider, a test, and SCIM.' },
      chat: { name: 'Chat', text: 'The chat\'s limits: tool calls, attachments, when a chat compacts, answers at once and the wait in line, sub-agents, the longest answer and tool call, memory and the leaderboard.' },
      model: { name: 'Model', text: 'The model new chats use, the model for sub-agents and small steps, the thinking levels, how many models the engine holds at once, and the time zone of working hours.' },
      'argus-gitlab': { name: 'Argus (GitLab)', text: 'When Argus reindexes by itself and in which time zone, and the GitLab address people\'s browsers open from Argus\'s answers.' },
      'python-and-web': { name: 'Python and web', text: 'The sites the chat\'s Web tool may open, the search engine, and the longest Python run.' },
      plugins: { name: 'Plugins', text: 'Where the plugin catalog comes from, and the key its index is signed with.' },
      'arena-mcp': { name: 'Arena MCP', text: 'Whether each person\'s chat tools are served to their own agent at /mcp.' },
      'scheduled-tasks': { name: 'Scheduled tasks', text: 'Whether tasks are on, tasks per person, how often one may run, the hosts webhooks may post to, and GitLab\'s address and bot token for answers as comments.' },
      email: { name: 'Email', text: 'The mail server the app sends through: tasks\' answers, credit and alert news, and answers to email in.' },
      notifications: { name: 'Notifications', text: 'Whether credit and alert news also goes by email, and the webhook the admins\' news is posted to.' },
      safeguards: { name: 'Safeguards', text: 'What keeps the chat from misuse: limits per person, secrets in messages, blocked words, the model\'s check of each message, personal data, and what happens after refusals. A group can set its own under Groups.' },
      'company-knowledge': { name: 'Company knowledge', text: 'How often sources are read again, and how much of long files comes with each question.' },
      'data-retention': { name: 'Data retention', text: 'How long chats are kept before they are deleted with their files. A group can keep its own.' },
      'api-keys': { name: 'API keys', text: 'The answer cache: repeated API requests answered from it, at no cost, and for how long.' },
      'chat-bots': { name: 'Chat bots', text: 'The Slack, Mattermost and Teams bots and email in: their secrets, channels, model and tools. The addresses to give each platform are below the settings.' },
    },
  },

  audit: {
    title: 'Audit log',
    about: 'Every sign-in and every change to people, settings, tools and the code index: who, to whom, from where.',
    admin: true,
    parts: [
      { name: 'Show', text: 'All events, or only failed ones, sign-ins, people, settings or Argus.' },
      { name: 'The events', text: 'When, who, what and from which address. **Load older events** goes further back.' },
      { name: 'Export CSV', text: 'The events shown, as a file.' },
    ],
    tasks: [{ title: 'Find who changed a setting', steps: ['Choose **Settings** under **Show**.', 'Each change names the setting, who changed it and when; a secret shows only as "a new secret value".'] }],
    manual: { doc: 'authentication', section: 'auditing-it' },
  },

  quality: {
    title: 'Quality',
    about: 'How people rate the answers, per model and per assistant, and how the models fare when people compare them blind.',
    admin: true,
    parts: [
      { name: 'Time range', text: 'A day, a week, a month or three.' },
      { name: 'By model and By assistant', text: 'Answers written, the share rated, the share rated up, and why answers were rated down.' },
      { name: 'Latest down-rated answers', text: 'Title, model, reason and the person\'s words, never the chat. A chat opens only when its owner shared it with the down vote; each opening is audited.' },
      { name: 'Arena leaderboard', text: 'The models by the votes cast with Compare in this time range.' },
    ],
    tasks: [{ title: 'Find out why a model disappoints', steps: ['Choose a longer time range.', 'Read its reasons under **By model**.', 'Open the shared chats under **Latest down-rated answers**, where people allowed it.'] }],
    manual: { doc: 'admin', section: 'quality' },
  },

  indexing: {
    title: 'Indexing',
    about: 'Argus\'s index of your GitLab: what it holds, how current it is, and runs on demand.',
    admin: true,
    parts: [
      { name: 'The numbers', text: 'Repositories, failing ones, files and symbols.' },
      { name: 'Index now', text: 'Starts a run: its progress, overall and per repository, and its log. Extra branches can be indexed for this run only.' },
      { name: 'Repositories', text: 'What GitLab lists, and which are indexed: each at its default branch and the branches you add. **Update** brings one up to date now; **Refresh from GitLab** lists new ones.' },
      { name: 'Schedule', text: 'When Argus reindexes by itself, in words or cron, in a time zone.' },
      { name: 'Push and merge webhook', text: 'GitLab tells Argus when a branch changes, so it updates at once. The secret is shown once; the steps for GitLab are on the card.' },
    ],
    tasks: [
      { title: 'Index another branch of a repository', steps: ['Open **Branches** on the repository.', 'Tick the branch, or add a pattern such as release/*.', 'Save, then press **Update**.'] },
      { title: 'Update the index on every push', steps: ['Turn on the webhook and copy its address and secret.', 'In GitLab: the group or project → Settings → Webhooks, with Push and Merge request events.', 'Test it there: the delivery shows under **Last deliveries**.'] },
    ],
    manual: { doc: 'argus', section: 'choosing-what-is-indexed' },
  },

  packs: {
    title: 'Knowledge packs',
    about: 'Prebuilt indexes of documentation (SDKs, standards) that Argus searches next to your code. A pack added here is searched from the next question.',
    admin: true,
    parts: [
      { name: 'The packs', text: 'Each pack, whether it is searched, its version and licence. **Update** gets a newer one; **Update all** does every pack.' },
      { name: 'Add a pack', text: '**From the library** (built packs on the host) or **From a URL** with its SHA-256. Only a pack built with Argus\'s embedding model can be searched.' },
      { name: 'Last pack operation', text: 'What the last install or update did, with its log.' },
    ],
    tasks: [{ title: 'Add a pack', steps: ['Press **Add a pack**.', 'Choose one from the library (or give its URL and SHA-256).', 'Press **Install**: the chat\'s next question can use it.'] }],
    manual: { doc: 'argus/knowledge-packs', section: 'using-packs' },
  },

  explore: {
    title: 'Explore',
    about: 'Search what Argus holds: symbols and paths, where a name is used, words in the code, and the documentation packs.',
    admin: true,
    parts: [
      { name: 'Symbols and paths', text: 'Symbols and files whose names contain your words, in one repository or all.' },
      { name: 'References', text: 'Every line where a name is used, with its definitions marked.' },
      { name: 'Code', text: 'Words in the files: "a phrase" or prefix*.' },
      { name: 'Documentation', text: 'The packs, by words, an API\'s name, or meaning.' },
      { name: 'A result', text: 'Opens a file at its line, or a page of a pack.' },
    ],
    tasks: [{ title: 'Check why a tool found nothing', steps: ['Search the name under **Symbols and paths**.', 'Nothing? Try **Code** for the words: it may be named differently.', 'Still nothing? Check under Indexing that its repository is indexed.'] }],
    manual: { doc: 'admin', section: 'admin' },
  },

  monitoring: {
    title: 'Monitoring',
    about: 'Live probes of every service, refreshed every 15 seconds.',
    admin: true,
    parts: [
      { name: 'Services', text: 'Each service with its state, and why it is down.' },
      { name: 'Elsewhere', text: 'Links to Prometheus\'s and Alertmanager\'s own pages, with the same sign-in.' },
    ],
    tasks: [],
    manual: { doc: 'admin', section: 'admin' },
  },

  dashboards: {
    title: 'Dashboards',
    about: 'The stack at work: the model, the machine, the services and their logs. Every panel runs its query here, against Prometheus, Loki and the gateway\'s database.',
    admin: true,
    parts: [
      { name: 'LLM Overview and Stack Performance', text: 'Requests, tokens, cost and latency at the gateway; how fast the model answers, its queues and its cache.' },
      { name: 'Resources, GPU Hardware, Host & Containers', text: 'CPU, memory, GPU, disk and network, of the machine and of each container.' },
      { name: 'Stack Health & Alerts and Logs', text: 'Which services answer, the alerts firing now, and every service\'s logs.' },
      { name: 'Argus and its index', text: 'Who asked Argus what, and its index runs.' },
    ],
    tasks: [],
    manual: { doc: 'admin', section: 'dashboards' },
  },

  dashboard: {
    title: 'A dashboard',
    about: 'One dashboard: its panels, drawn here from their queries.',
    admin: true,
    parts: [
      { name: 'Time range', text: 'The period every panel shows.' },
      { name: 'Variables', text: 'Narrow the panels, for example to one model or one container.' },
      { name: 'Panels', text: 'Each chart, number or table, with its own query. **All dashboards** goes back to the list.' },
    ],
    tasks: [],
    manual: { doc: 'admin', section: 'dashboards' },
    sectionNames: { one: 'This dashboard', all: 'The dashboards' },
    sections: {
      'llm-overview': { name: 'LLM Overview', text: 'Requests, tokens, cost and latency at the gateway, and the engine behind it, with the share of each prompt read from the cache.' },
      'stack-performance': { name: 'Stack Performance', text: 'Throughput, time to first token, queues and the cache: how fast the model answers.' },
      'stack-health': { name: 'Stack Health & Alerts', text: 'Which services answer, the alerts firing now, and Prometheus itself.' },
      resources: { name: 'Resources (CPU, Memory, GPU)', text: 'CPU, memory and GPU of the machine and of each service.' },
      'gpu-hardware': { name: 'GPU Hardware', text: 'The GPU: utilisation, memory, temperature, power and clocks.' },
      'host-containers': { name: 'Host & Containers', text: 'The host and each container: CPU, memory, disk and network.' },
      'stack-logs': { name: 'Logs (Loki)', text: 'Every service\'s logs, by container, searchable, and their volume.' },
      argus: { name: 'Argus: who asked what', text: 'Who asked Argus what: tool calls, people, refusals and timings.' },
      'stack-indexing': { name: 'Indexing: Argus code index', text: 'Argus\'s index runs: repositories, files, symbols and failures.' },
      'usage-by-user': { name: 'Usage by person', text: 'Each person\'s tokens and cost. The Usage & cost page shows it too.' },
    },
  },

  logs: {
    title: 'Logs',
    about: 'Every service\'s logs from Loki, newest first.',
    admin: true,
    parts: [
      { name: 'Filters', text: 'Time range, containers, level (all, warnings and errors, errors) and text. They are in the address, so a view can be linked.' },
      { name: 'Volume', text: 'Log lines per interval, by level.' },
      { name: 'Live', text: 'Adds lines as they are written.' },
      { name: 'The query sent to Loki', text: 'The LogQL behind the view.' },
    ],
    tasks: [{ title: 'Find the errors of one service', steps: ['Choose its container.', 'Set **Level** to **Errors**.', 'Widen the time range if nothing shows.'] }],
    manual: { doc: 'admin', section: 'admin' },
  },

  alerts: {
    title: 'Alerts',
    about: 'What fires now, what fired before, and every rule that watches the stack. Checked every 30 seconds.',
    admin: true,
    parts: [
      { name: 'Firing now', text: 'From Alertmanager, with silenced ones marked.' },
      { name: 'History', text: 'Every time an alert fired, over a day, a week or a month.' },
      { name: 'Rules', text: 'Each rule with its state, severity, how long its condition must hold, and its query.' },
    ],
    tasks: [{ title: 'Get alerts outside the app', steps: ['Set up email under Settings → Email, or an **Alerts webhook** under Settings → Notifications.', 'Every admin\'s bell gets them too, once per firing.'] }],
    manual: { doc: 'admin', section: 'admin' },
  },

  traces: {
    title: 'Traces',
    about: 'The slowest answers, across people, and where their time went. Times, tokens and tool names only: never what was said.',
    admin: true,
    parts: [
      { name: 'Time range', text: 'An hour to a month.' },
      { name: 'The slowest answers', text: 'When, who, the model, the whole time, the slowest step, tokens and tools.' },
      { name: 'A trace', text: 'The wait in line, getting ready, each round of the model with its speeds, each tool call, and each sub-agent. The slowest step is named, with why.' },
    ],
    tasks: [],
    manual: { doc: 'admin', section: 'admin' },
  },
} satisfies Record<string, HelpTopic>

export type TopicId = keyof typeof topics

export const topicList: [TopicId, HelpTopic][] = Object.entries(topics) as [TopicId, HelpTopic][]
