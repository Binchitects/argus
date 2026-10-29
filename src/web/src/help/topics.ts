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
      { name: '?', text: 'Help for the page you are on: this panel. On a wide screen it sits beside the page, which makes room for it, and stays open while you work and move between pages. On a smaller screen it opens over the page.' },
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
      { name: 'Your credit this month', text: 'Kind by kind (the chat, API keys, pictures, video, speech): what you spent this month, of your credit, and what is left (your group\'s when it binds first).' },
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
      { name: 'The message box', text: 'The paperclip attaches files (or paste or drop them). **Tools** chooses what the model may use in this chat, **Deep research** among them when an admin gave it to you: the model starts it when you ask, after your **Allow**. **Compare** applies to the next message. The microphone records a voice message; **Talk** is a voice conversation; the screen button shares your screen, and each question then takes a picture of it; **Sketch** (the pen) is a whiteboard whose drawing the model builds into a page, a component, a diagram, a 3D scene or a table. With **Browser** on in Tools, the model uses a real browser while you watch it above the box, and **Take over** lets you do a part yourself.' },
      { name: 'Context', text: 'The gauge beside Send: how full the model\'s window is, what fills it, and where the chat compacts itself (Your account → Answers → **Compact long chats**; it always compacts before a message would not fit). **Compact now** summarizes older messages so the chat can go on lighter. Before compacting, older tools\' results are cleared in what the model reads (the chat keeps them), and often that is enough. Under **Biggest**: the messages that take most of it, each pinned or left out from there. A chat that compacted keeps every message: the model searches and reads them again by their numbers when the summary is not enough.' },
      { name: 'A long chat', text: 'A long chat opens at its newest messages; **Show earlier messages** at the top reads more of it.' },
      { name: 'Pin and leave out', text: 'Beside each question: the pin keeps it word for word after every summary (a code, an exact instruction); **Leave this question out** hides it, and its answer, from the model without deleting it. Press again to undo.' },
      { name: '/ in the box', text: 'At the start of a message, / finds a skill (Workspace → Skills): it sits above the box with a field for each parameter, and goes with your message.' },
      { name: '↑ in the box', text: 'With the box empty (or the caret on its first line), ↑ brings back what you sent before: this chat\'s messages, then your other chats\'. ↓ goes back to what you were typing, and Esc goes straight there.' },
      { name: 'No connection', text: 'What you send with no connection waits above the box, and goes by itself when the connection is back (a new chat is made then). One the server refuses stays with **Send again**.' },
      { name: 'From your phone', text: 'Installed on a phone, Arena is in the Share sheet: what you share opens a new chat with it attached and its words in the box. A long press on the app\'s icon offers **New chat**, **Talk** and **Record a meeting**.' },
      { name: 'A room\'s call', text: 'In a room, **Call** at the top starts its call; the others see who is in it and **Join**. What each says shows as live captions above the box, in your language when you read the room in one (**Show the original** beside each). **Mute** stops your sound and your captions; **Leave** ends it for you.' },
    ],
    tasks: [
      { title: 'Ask about a file', steps: ['Press the paperclip, or drop the file on the page.', 'Wait until it shows as attached.', 'Type your question and press Enter.'] },
      { title: 'Let the model use a tool', steps: ['Press **Tools** in the message box.', 'Turn on the tool (Argus, Python, Web, image generation…).', 'Ask your question: the model calls the tool when it needs it. A tool marked as asking first waits for your **Allow**.'] },
      { title: 'Try another model on the same question', steps: ['Open **Answer again** under the answer.', 'Choose the model (or a thinking level).', 'Both answers stay: the arrows switch between them.'] },
      { title: 'Compare two models blind', steps: ['Press **Compare** in the message box.', 'Choose two models, or leave it to chance.', 'Send your question, read both answers, and vote. The names show after your vote, and the vote counts on the Leaderboard.'] },
      { title: 'Share a chat', steps: ['Open **⋯** at the top right of the chat.', 'Choose **Share**.', 'Choose who may open it (everyone in the company, or chosen groups) and what it shows. The link is copied. **Revoke link** stops it.'] },
      { title: 'Use a skill', steps: ['Type / at the start of the message.', 'Choose the skill with the arrows and Enter.', 'Fill in its parameters (Enter moves to the next, then to the box), add anything in the box, and send.'] },
      { title: 'Send a message again', steps: ['Press ↑ in the empty box until the message shows (your other chats\' come after this chat\'s).', 'Change it if you like: the one you sent stays as it was.', 'Press Enter: it goes as a new message.'] },
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

  skills: {
    title: 'Skills',
    about: 'Instructions for work you ask often, with parameters filled in each time, as in Claude Code. Type / in the chat to use one; with a description, the model uses it on its own when a question fits.',
    parts: [
      { name: 'Learned from your work', text: 'Skills worked out in your chats and Code Arena sessions, and better versions of skills that went wrong there: **Keep** (or **Apply**) or **Not now**. Each skill shows its version; **How it got better** has **Undo**.' },
      { name: 'Yours', text: 'Skills you made. Only you change them; you may share them with your groups.' },
      { name: 'Shared with you', text: 'From people in your groups.' },
      { name: 'For everyone', text: 'The company\'s, kept by admins.' },
      { name: 'Arena\'s own', text: 'The skills people use most, built in for the chat and Code Arena: /code-review, /write-tests, /debug, /plan-change, /summarize and more. Yours, a group\'s or the company\'s of the same name wins; an admin turns one off for everyone.' },
      { name: 'From plugins', text: 'They come and go with their plugin.' },
      { name: 'New skill', text: 'A slash name, a title, when to use it, the instructions (a parameter as {{name}}, what you type after the fields as $ARGUMENTS) and its parameters.' },
      { name: 'Used on its own', text: 'A skill that says when to use it is listed to the model (the Skills tool): it loads the skill when a question fits. Off: only with /.' },
    ],
    tasks: [
      {
        title: 'Make a skill with parameters',
        steps: [
          'Press **New skill**.',
          'Give it a slash name, like review, a title, and when to use it.',
          'Write the instructions with parameters: Review {{file}} for {{focus}}. Each one gets a row: say what it is, whether it is required, its default.',
          'Choose who uses it, and save.',
          'In the chat, type /review: the skill sits above the box with a field for each parameter; what you write in the box goes with it.',
        ],
      },
    ],
    manual: { doc: 'chat', section: 'skills-and-slash-commands' },
  },

  apps: {
    title: 'Apps',
    about: 'What the chat built, published to use: a page or a React component at its own link. It asks the model and keeps its data as the person using it.',
    parts: [
      { name: 'The apps', text: 'Those you may open: yours, your groups\', and everyone\'s, the most used first. **Mine** shows only yours.' },
      { name: 'Publishing one', text: 'In a chat, open a page or a component in the Files panel\'s preview and press **Publish as app**: a name, a line about it, and who may open it (only you, some groups, everyone).' },
    ],
    tasks: [
      { title: 'Make an app for your team', steps: ['Ask the chat to build it: "a page where the team votes on the lunch place, the results kept for everyone".', 'Open its **Preview** in the Files panel and try it: it already asks the model and keeps its data while the page is open.', 'Press **Publish as app**, choose **Some groups** and your team, and **Publish**.', 'Copy its link and share it.'] },
    ],
    manual: { doc: 'chat', section: 'apps' },
  },

  'app-page': {
    title: 'An app',
    about: 'An app running, as its person uses it: it asks the model with your credit and your safeguards, and keeps its data (everyone\'s, or yours).',
    parts: [
      { name: 'Remix', text: 'Makes it yours: a copy only you open, to change in a chat.' },
      { name: 'Change and Versions', text: 'For its owner: its name, words and who may open it; every version of its code, and putting an old one back.' },
      { name: 'Copy link', text: 'Its address, to share with whoever may open it.' },
    ],
    tasks: [{ title: 'Make it your own', steps: ['Press **Remix**: a copy that is yours, only you open it.', 'Open the chat it came from, or a new one, and ask for the change with its code.', 'Publish the new version from its preview.'] }],
    manual: { doc: 'chat', section: 'apps' },
  },

  wiki: {
    title: 'Wiki',
    about: 'The company\'s documents, code and data, searched and read directly, with no model: only what you may read.',
    parts: [
      { name: 'Search', text: 'Words, a name in the code, a table: **Everything**, or only **Documents**, **Code** or **Data**. Documents are found by their words, then by meaning.' },
      { name: 'Sources', text: 'The knowledge sources you may read, with how many of their documents you may read; open one to browse its documents by title.' },
      { name: 'A document', text: 'Read whole, as it was last read from its source, with **Open the original** where it lives.' },
      { name: 'Code', text: 'Files and symbols in the repositories you may read in GitLab, as Argus indexed them; **Repositories** lists them.' },
    ],
    tasks: [{ title: 'Find the leave policy', steps: ['Type "paid leave" and press **Search**.', 'Open the document under **Documents**.', 'Press **Open the original** to see it where it lives.'] }],
  },
  data: {
    title: 'Data',
    about: 'The company\'s databases asked in words: each answer is the SQL it ran (read only), what it means, a chart and the rows.',
    parts: [
      { name: 'Database', text: 'The databases an admin opened to you. **Tables** lists what can be answered: the tables, their columns and what they hold.' },
      { name: 'An answer', text: 'What it counts in plain words, the chart (one figure, lines over time, bars) or the **Table**, and the **SQL** it ran: change it and **Run** your version.' },
      { name: 'Ask a follow-up', text: 'The next question builds on this answer ("and by country?").' },
      { name: 'Pin to a board', text: 'Keeps the chart on a board, run again every hour, every day, or when you refresh it.' },
    ],
    tasks: [
      { title: 'Make a weekly sales chart', steps: ['Choose the database and ask: "Orders per week this quarter".', 'Check what it means and the SQL; switch to **Table** to see the rows.', 'Press **Pin to a board**, choose **A new board…**, name it, and pin it to run **Every day**.'] },
    ],
    manual: { doc: 'data', section: 'asking' },
  },

  boards: {
    title: 'Boards',
    about: 'Charts pinned from answers, each run again on its schedule as the board\'s owner. Yours, your groups\' and everyone\'s.',
    parts: [
      { name: 'The boards', text: 'Those you may see, the latest changed first, with how many charts each has.' },
      { name: 'New board', text: 'An empty board: charts come onto it from answers, with **Pin to a board**.' },
    ],
    tasks: [{ title: 'Share a board with your team', steps: ['Open the board and press **Change**.', 'Choose **Some groups** and tick your team.', 'Save: your team finds it under Boards.'] }],
    manual: { doc: 'data', section: 'boards' },
  },

  board: {
    title: 'A board',
    about: 'Its charts as they last ran. They run as its owner: those it is shared with see what the owner may ask.',
    parts: [
      { name: 'A chart', text: 'Its title, its database, how often it runs again, and when it last ran. Its menu: **Post to a room**, and for the owner **Run it now**, how often, and **Take it off the board**.' },
      { name: 'Change', text: 'For its owner: its name, words, and who may see it (only you, some groups, everyone).' },
    ],
    tasks: [{ title: 'Post a chart to a room', steps: ['Open the chart\'s menu and choose **Post to a room**.', 'Choose the room and press **Post**: its last result goes there as a table, for the room\'s people.'] }],
    manual: { doc: 'data', section: 'boards' },
  },

  decks: {
    title: 'Decks',
    about: 'Slides from a few words, with charts from the company\'s data and pictures from the picture model, in the company\'s look.',
    parts: [
      { name: 'Make a deck about…', text: 'What it is about, in a sentence or a page; under **Look**, the company template to make it in (or the built-in look).' },
      { name: 'Your decks', text: 'Each with its first slide, how many slides it has and when it last changed.' },
      { name: 'Templates', text: 'For those who may change the branding: the company\'s PowerPoint templates, one the default for new decks.' },
    ],
    tasks: [
      { title: 'Make a deck for a meeting', steps: ['Say what it is about in **Make a deck about…**: the subject, who it is for, the points that matter.', 'Press **Make it**: the slides open, their pictures coming one by one.', 'Change what you want by asking, then **Present**, or download it as **PowerPoint** or **PDF**.'] },
    ],
    manual: { doc: 'decks', section: 'making-a-deck' },
  },

  deck: {
    title: 'A deck',
    about: 'Its slides, the one chosen large; changed by asking or by hand; presented, printed or downloaded.',
    parts: [
      { name: 'Slides', text: 'Every slide, small: press one to see it large. Below it, **Change by hand**, move it up or down, copy it, add one after it, or take it out.' },
      { name: 'Change it by asking', text: 'In words: "a slide on costs after the third", "make this slide shorter". **This slide** is the one you see.' },
      { name: 'Present, PowerPoint, PDF', text: '**Present** fills the screen (→ and ← to move, Escape to end). **PowerPoint** is a .pptx in its template; **PDF** prints the slides, one a page (Save as PDF).' },
      { name: 'More', text: '**Rename**, **Versions** (put an old one back), **Ask the numbers again** for charts from a database, its **Look**, and **Delete**.' },
    ],
    tasks: [{ title: 'Put a chart of real numbers on a slide', steps: ['Press **Change by hand** on the slide.', 'Choose the **Chart** layout, the database and what it counts ("orders per month this year").', 'Save: its numbers are asked of the database now, and again with **Ask the numbers again**.'] }],
    manual: { doc: 'decks', section: 'changing-it' },
  },

  media: {
    title: 'Media studio',
    about: 'Pictures from a few words; one changed by saying how (all of it, or only the part painted over), made a little different, enlarged, cut out or set moving; speech in a voice from a sample, and music.',
    parts: [
      { name: 'Describe a picture', text: 'What to draw, its **Shape** and **How many** (one, two or four). **Upload a picture** brings one of yours in to change it.' },
      { name: 'Video', text: 'Say what a video is about: the model writes a storyboard (shots, each what is seen and what the narrator says) to change before **Make the video** draws, moves, narrates, scores and cuts it into one MP4. **Your videos** opens each storyboard again.' },
      { name: 'Speech and Music', text: 'With the studio server: **Speech** speaks your words in its own voice or **a voice from a sample** (recorded or a sound file, only when you tick that you may use it); **Music** makes a piece from a description and lyrics, 15 seconds to 2 minutes.' },
      { name: 'Your pictures and clips', text: 'Newest first; work going on shows where it will be, with what it is doing (waiting for its turn on the GPU, drawing). **Show** picks pictures or clips.' },
      { name: 'A picture, open', text: '**Change** it by saying how, all of it or only the part you paint over (**Paint the part**: brush, erase, undo); **Variations**; **Enlarge** (four times as large); **Cut out** (the background taken away); **Move** (a short clip that starts from it); **Download** or **Delete**. **Made from** opens the one it came from.' },
    ],
    tasks: [
      { title: 'Change only part of a picture', steps: ['Open the picture and choose **Change**.', 'Press **Paint the part** and paint over what should change.', 'Say what it becomes ("a blue knitted hat") and press **Change the painted part**: the rest stays as it was.'] },
      { title: 'Make a picture move', steps: ['Open the picture and choose **Move**.', 'Say what happens and how long; press **Make the clip**.', 'It takes a few minutes: the bell says when it is ready.'] },
    ],
    manual: { doc: 'media', section: 'media-studio' },
  },

  studio: {
    title: 'Agent Studio',
    about: 'Workflows that do the work as you: a trigger, steps that think, decide and wait for your yes, and what they give out. Every run kept, to replay.',
    parts: [
      { name: 'Your workflows', text: 'Each with its trigger, whether it is on, and when it last ran.' },
      { name: 'Shared with you', text: 'Workflows others shared: fill their form or run them; they run as their owner.' },
      { name: 'Templates', text: 'A morning digest, a review of each merge request, a room\'s triage, a request form, one by one: a workflow to start from.' },
      { name: 'From a scheduled task', text: 'One of your scheduled tasks as a workflow: its trigger, its question and tools, its email and GitLab answer.' },
    ],
    tasks: [
      { title: 'Review each merge request', steps: ['Choose the template **Review each merge request**.', 'In its trigger, copy its address; press **Save** and copy the secret shown.', 'In GitLab, add a webhook with that address and secret token, for merge request events.', 'Switch it **On**.'] },
    ],
    manual: { doc: 'studio', section: 'making-a-workflow' },
  },

  workflow: {
    title: 'A workflow',
    about: 'Its trigger and steps on the canvas, changed in the panel beside it; its runs, each to replay step by step.',
    parts: [
      { name: 'The canvas', text: 'The trigger on top, the steps in order: an **If** with its Yes and No steps side by side, a **For each** with its steps framed. **+** adds a step there.' },
      { name: 'The panel', text: 'What is chosen: its settings. **{}** puts a value in a text: the input, a field, what a step before gave.' },
      { name: 'On, Save, Run now', text: '**On** lets its trigger run it; **Save** keeps the changes; **Run now** runs it, with words or its fields.' },
      { name: 'Runs', text: 'Every run; open one to **Replay** it on the canvas or read its **Timeline**. A run that asks you waits for **Yes, go on** or **No, stop it**.' },
    ],
    tasks: [{ title: 'Ask before it posts', steps: ['Press **+** before the step that posts.', 'Choose **Ask me first** and write the question, with the draft in it ({{draft.text}}).', 'Save. Each run now waits for your yes, and a notification tells you.'] }],
    manual: { doc: 'studio', section: 'steps' },
  },

  fixes: {
    title: 'Fix it',
    about: 'An agent makes the change for a finding, an issue, a failed pipeline or your words, runs the tests it can, and opens the merge request as you when you say so.',
    parts: [
      { name: 'Your GitLab token', text: 'Your own personal access token (scope api): it reads the branch and opens the merge request as you. Kept encrypted, never shown again.' },
      { name: 'A new fix', text: 'The project, and what to fix: in words, an issue\'s number, or a failed pipeline\'s. The branch is the default one unless you name it.' },
      { name: 'Your fixes', text: 'Each with how it went: at work, ready, failed, or its merge request open.' },
    ],
    tasks: [{ title: 'Fix a failed pipeline', steps: ['Link your GitLab token (scope api) once.', 'Choose **A failed pipeline**, give the project and the pipeline\'s number, and press **Fix it**.', 'Read the diff and the tests, then **Open the merge request as you**.'] }],
    manual: { doc: 'chat', section: 'fix-it' },
  },

  fix: {
    title: 'A fix',
    about: 'What the agent did, step by step; the change as a diff; what the tests said; the merge request.',
    parts: [
      { name: 'Step by step', text: 'Each file it looked at, read, searched for, changed, and each test it ran.' },
      { name: 'The change', text: 'The diff: what goes in red, what comes in green.' },
      { name: 'Open the merge request as you', text: 'A new branch with the change, and a merge request into the branch it started from, with your token.' },
      { name: 'Another round', text: 'More to do on the same change: the agent starts from what it changed.' },
    ],
    tasks: [{ title: 'Ask for a test too', steps: ['Under **More to do on it**, write: "add a test for the empty case".', 'Press **Another round**, and read the new diff.'] }],
    manual: { doc: 'chat', section: 'fix-it' },
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
      { name: 'API key', text: 'Your key for every tool. **New key** makes one and shows it once (five an hour at most); the old one stops working. **Rate limits** say what it may send a minute, what it used in the last one, and what was refused.' },
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
      { name: 'API key', text: 'Your key for your tools. **New key** replaces it (five an hour at most); the old one stops working at once. **Rate limits**: the requests and tokens it may use a minute, what it used in the last one, and what the gateway refused in the last day (HTTP 429: wait as long as its Retry-After says).' },
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
      { name: 'Contents', text: 'The pages of the manual, in groups. The page you read lists its sections below its name. On a smaller screen, pick the page, then the section, from the two lists at the top.' },
      { name: 'Every page, explained', text: 'The help of every page of the app in one place: what it is for, its parts, and the common tasks.' },
      { name: 'Links', text: 'Links between pages stay in the app. A file of the repository that is not in the manual shows as its name.' },
    ],
    tasks: [{ title: 'Find how to do something', steps: ['Type a few words in **Search the manual** (such as "api key" or "credit").', 'Open the best result.', 'Copy the address to send someone straight to that section. A section picked from the contents has its own address too.'] }],
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
      { name: 'The table', text: 'Each person with their spend this month, the credits they have used up, and last sign-in. **Show** filters to admins, the disabled, those over a credit, or (with the company directory on) local accounts. A row opens the person.' },
      { name: 'Bulk actions', text: 'Tick people, then **Set credit**, **Disable**, **Enable**, **Sign out everywhere** or, with the company directory on, **Move to the directory**.' },
      { name: 'Add person', text: 'A local account with a password and an API key, shown once. People from the company directory or company sign-in appear by themselves at their first sign-in.' },
      { name: 'Export CSV', text: 'Everyone, with this month\'s spend and credit of each kind, and their API access.' },
    ],
    tasks: [
      { title: 'Add a person', steps: ['Press **Add person**.', 'Use their GitLab username, so Argus knows what they may read; add their email and name.', 'Press **Add person**, and give them the password and API key now: they are shown only once.'] },
      { title: 'Move local accounts to the directory', steps: ['Press **Show → Local accounts** and tick the people.', 'Press **Move to the directory**: each shows the directory entry they will sign in as and what changes, or why they stay.', 'Press **Move**: they sign in with their directory password from now on, and keep their chats, keys, spend and email.'] },
      { title: 'Raise someone\'s credit', steps: ['Tick them in the table.', 'Press **Set credit**, choose the kind, and type the new amount (empty: no limit).', 'It holds at once; their other credits stay as they were.'] },
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
      { name: 'Credits this month', text: 'One per kind: their spend this month against their credit, the tightest group credit left, and **Set credits**.' },
      { name: 'API key', text: 'The **API access** switch (on for everyone; off blocks their keys at once), their keys, and **New API key** (the old one stops at once).' },
      { name: 'Rate limits', text: 'The requests and tokens each of their API keys may use a minute, where each comes from (theirs, a group\'s or the company\'s), what they used in the last minute and what was refused in the last day. **Set limits** gives them their own.' },
      { name: 'Groups', text: 'The groups they are in: these decide which tools and models they may use.' },
      { name: 'Legal hold', text: 'While on hold nothing of theirs is deleted. **Export their data** for eDiscovery. Both are audited.' },
      { name: 'Delete', text: 'Removes them, their chats and their API keys. Their usage stays in the reports.' },
    ],
    tasks: [
      { title: 'Help someone who lost their phone', steps: ['Press **Reset 2FA**.', 'They sign in with their password and can turn it on again.'] },
      { title: 'Hold back a script that floods the gateway', steps: ['Under **Rate limits**, type **Requests a minute** (and **Tokens a minute** if it sends long prompts).', 'Press **Set limits**: their keys have them at once.', 'Empty puts back their groups\' or the company\'s; 0 is no limit.'] },
    ],
    manual: { doc: 'authentication', section: 'managing-people' },
  },

  access: {
    title: 'Access',
    about: 'Roles say what people may run here; rules give or take a model, a tool, a knowledge source, a repository or a feature. The most specific wins: a person\'s own over their groups\', a group\'s over everyone\'s; at one level a deny wins.',
    admin: true,
    parts: [
      { name: 'Roles', text: 'The built-in roles (Owner, People admin, Model admin, Billing, Auditor and the rest) and your own. Each shows who holds it: **Give it** to a person, a group (a directory group linked on the Groups page too) or everyone, maybe until a date. **New role** picks permissions from the list; a built-in role cannot be changed, only copied.' },
      { name: 'Rules', text: 'Allows and denies of a model, a tool, a knowledge source, a repository or a feature, for a person, a group, a role or everyone. A name may be a pattern with *.' },
      { name: 'Checker', text: 'May this person do this? Yes or no, with the rules and roles that decide it, in order.' },
      { name: 'Many people', text: 'People and groups, and what changes for each (roles, groups, features, accounts): **Dry run** says person by person what would change; **Make the changes** does it, as one change.' },
      { name: 'History', text: 'Every change to roles, who holds them and rules: when, by whom, and what.' },
    ],
    tasks: [
      { title: 'Let a team lead watch the dashboards', steps: ['Press **New role**, name it (Supervisor), and tick `monitoring.read` and `logs.read`.', 'Press **Give it** on the new role and choose the person or their group.'] },
      { title: 'Give a directory group a role', steps: ['On Groups, make a directory group with its name or DN.', 'Here, **Give it** on the role to that group: whoever the directory puts in it holds the role.'] },
    ],
    manual: { doc: 'admin', section: 'access-roles-and-rules' },
  },

  safeguards: {
    title: 'Safeguards',
    about: 'Rules that look at questions, attached files, answers, what goes to tools and comes back, and API keys\' requests, and block, mask, warn, ask for a reason, log or send to another model. They run top to bottom.',
    admin: true,
    parts: [
      { name: 'Rules', text: 'Each rule looks for one thing (secrets, personal data, words, a topic the model judges, prompt injection, links, files), where, for whom (everyone, or groups and people) and with which models (`@external`: every model on another provider). **Allow** above another rule of the same detector exempts what it covers; **Monitor only** records what it would do. Start **from a template**.' },
      { name: 'Test bench', text: 'Paste a text and see which rules fire and what they would do; nothing is sent or recorded.' },
      { name: 'What they did', text: 'Every time a rule acted, day by day and one by one: who, where, the rule, what it found (kinds, never a secret or personal data) and a little of the text for words, topics, links and injection.' },
      { name: 'Reviews', text: 'People ask for a refusal to be reviewed, with why. **Allow it**: the same message passes that rule for a day; **Keep it refused**. They hear either way.' },
    ],
    tasks: [
      { title: 'Keep personal data from cloud models', steps: ['Choose the template **Personal data to cloud models**.', 'Keep **Models** as `@external` and save: the company\'s own models still see it.'] },
      { title: 'Try a rule before it bites', steps: ['Make it with **Monitor only**.', 'Watch **What they did** for a week, then switch it to **Enforce**.'] },
    ],
    manual: { doc: 'admin', section: 'safeguards' },
  },

  memory: {
    title: 'Memory',
    about: 'Everything the chat and Code Arena remember for you: about you (every chat and project), about your projects (from Code Arena), and the company\'s (kept by those who keep its knowledge). Each answer reads the pinned ones, and those that matter to its question, found by meaning.',
    parts: [
      { name: 'Remember something', text: 'One or two short sentences, with its kind: a preference, a fact, a decision, how to do something, or a lesson (what went wrong once). **Pinned**: every answer reads it.' },
      { name: 'Find a memory', text: 'By its words; **Whose** narrows to yours, a project\'s or the company\'s, and the kind to one kind.' },
      { name: 'A memory', text: 'Its kind (change it), whose, where it came from (a chat links to it), and how often answers read it. Pin, edit or forget it. The company\'s are changed only by those who keep its knowledge.' },
      { name: 'Remember these?', text: 'What memory proposes from your chats that went quiet: **Keep** it (answers read it from then on; one that says otherwise than a memory you kept replaces it) or **Not now**. **Remember on its own** keeps them at once.' },
      { name: 'Under an answer', text: '**Read N memories**: the ones found for its question; a click shows them.' },
      { name: 'History', text: 'Once a day your memories are tidied: those that say the same are merged, one replaced long ago or unused for months is let go (never a pinned one), a long one is tightened. Each change is listed with what it was; **Undo** puts it back. **Tidy now** tidies at once.' },
      { name: 'Past chats', text: 'Each chat and Code Arena session that went quiet gets a short digest of what it was about. When you refer to earlier work ("as we decided last month"), the model finds that chat by its digest and reads what it needs in it.' },
    ],
    tasks: [
      { title: 'Make the chat always know something', steps: ['Type it under **Remember something**, with its kind.', 'Tick **Pinned**.', 'Press **Remember**: every answer reads it from now on.'] },
      { title: 'See what an answer remembered', steps: ['Under the answer, press **Read N memories**.', 'Each memory it read is listed; **All memories** opens this page.'] },
    ],
    manual: { doc: 'chat', section: 'memory' },
  },

  meetings: {
    title: 'Meetings',
    about: 'Meetings recorded in the browser or uploaded, made into who said what, the minutes (a summary, the decisions, the action items) and a reminder for each action item on its day. Each meeting is its owner\'s.',
    parts: [
      { name: 'Record a meeting', text: 'The microphone records the room (or a call played aloud) until **Stop and make the minutes**; the loudness bar shows it hears.' },
      { name: 'Upload a recording', text: 'A sound or a video, up to 60 MB (three hours as the browser records).' },
      { name: 'Your meetings', text: 'Each one with its length and how many of its action items are open; while one is made, its step.' },
    ],
    tasks: [
      { title: 'Make the minutes of a meeting', steps: ['Type its title, or leave it to the minutes.', 'Choose **Record a meeting** and allow the microphone, or **Upload a recording**.', 'Wait for its steps: listening, telling the voices apart, writing the minutes.', 'Name the speakers, check the action items, then **Make a plan** or **Ask about it**.'] },
    ],
    manual: { doc: 'chat', section: 'meetings' },
  },

  meeting: {
    title: 'A meeting',
    about: 'One meeting: its recording, the transcript (who spoke when; a line plays from there), the summary, the decisions, the action items and the speakers.',
    parts: [
      { name: 'Transcript', text: 'Each part of what was said with its speaker and time. A line plays the recording from there; the line playing is marked. **Find in what was said** keeps the lines that hold your words.' },
      { name: 'Action items', text: 'Whose each is (a speaker, someone else by name, or nobody yet) and its due date. Done ones are struck through. A person\'s items are reminded to them once, on the day due (an item dated in the past, at once).' },
      { name: 'Speakers', text: 'Told apart by their voices and named as the meeting named them. Rename one, or tell which person in Arena they are (**It is me**, or a name): their action items go to that person.' },
      { name: 'Make a plan', text: 'A plan with a step per action item, its owner and due date, opened on the board.' },
      { name: 'Ask about it', text: 'A chat with the minutes and the whole transcript as a file: ask what was said, by whom, or what to tell someone who missed it.' },
      { name: 'Make again', text: 'Listens and writes the minutes again (after a better model is set, or when it failed). Names you gave go.' },
    ],
    tasks: [
      { title: 'Give an action item to someone', steps: ['Under **Speakers**, tell which person a speaker is.', 'Their action items show **→** that person.', 'Set a due date: they are reminded on that day.'] },
    ],
    manual: { doc: 'chat', section: 'meetings' },
  },

  checklists: {
    title: 'Checklists',
    about: 'Your own checklists: what is done before work counts as done. Turned on in a chat or a Code Arena session, the model goes through one and ticks each item, saying how.',
    parts: [
      { name: 'Your checklists', text: 'Each with its items in order. **New checklist** writes one, an item a line; the pencil changes it, the bin deletes it (chats it is on in keep their copy).' },
      { name: 'In a chat', text: 'The checklist button in a chat\'s header turns one on, shows how many items are done, who ticked each and how, and lets you tick them too.' },
    ],
    tasks: [{ title: 'Have every change checked the same way', steps: ['Write a checklist: the tests pass, the docs say what changed.', 'In a chat (or Code Arena, with **/checklist**), turn it on.', 'Ask for the work: before it says it is done, it ticks each item with how.'] }],
    manual: { doc: 'chat', section: 'checklists' },
  },

  plans: {
    title: 'Plans',
    about: "Plans the chat made with its Plans tool, and Code Arena's working plans: steps with what done means, owners, dates and what each waits for. Yours, and those of rooms you are in.",
    parts: [{ name: 'The list', text: "Each plan, newest change first, with how many of its steps are done; Code Arena's show a terminal and their project. A row opens it." }],
    tasks: [{ title: 'Make a plan', steps: ['In a chat, turn on **Plans** in the Tools menu.', 'Ask it to plan something; tell it who does what and by when.', 'Open the plan from the answer.'] }],
    manual: { doc: 'chat', section: 'plans' },
  },

  plan: {
    title: 'A plan',
    about: 'One plan as a board (a column per status) and a timeline (each step from its start to its due date). The chat keeps it up to date; you move steps along here.',
    parts: [
      { name: 'Board', text: 'To do, Doing, Blocked and Done. A step\'s buttons move it to another column; **A new step** adds one.' },
      { name: 'Timeline', text: 'Each dated step as a bar from its start to its due date, coloured by its status.' },
      { name: 'Warnings', text: 'What does not fit: a step that starts before what it waits for is due, or one overdue and not done.' },
      { name: 'Checks and files', text: "A step's **Done when** says how it is checked, and, once it ran, whether the check passed (its output on hover); Code Arena's steps list the files they change." },
      { name: "Code Arena's plans", text: 'Its agent keeps them as it works: they show here as it goes, and nothing on the board moves them.' },
    ],
    tasks: [{ title: 'Change the plan by talking', steps: ['Open **Its chat**.', 'Say what changed ("Ben is away next week, move his steps").', 'The chat changes the plan; the board shows it.'] }],
    manual: { doc: 'chat', section: 'plans' },
  },

  lab: {
    title: 'Science lab',
    about: 'Problems of a science (mathematics, physics, chemistry, statistics, biology, quantum computing, or any) worked by the model with the lab\'s tools: each computation done and each step checked by SymPy, Pint, RDKit, Biopython, Lean, the quantum simulators (Qiskit, Stim, QuTiP, PennyLane, PySCF) or Python, not taken on trust. Each problem is kept as a notebook.',
    parts: [
      { name: 'A new problem', text: 'Choose its science (each has its own instructions and tools), write the problem and press **Work it**: a notebook opens and the lab starts on it.' },
      { name: 'Notebooks', text: 'Yours, newest first, with how many of their checks were proved, refuted or held only numerically. A row opens it. They are not among your chats.' },
    ],
    tasks: [{ title: 'Check an answer you have', steps: ['Choose **Mathematics**.', 'Write the problem and your answer, and ask the lab to check it.', 'The notebook says whether SymPy proved it, and where it fails if not.'] }],
    manual: { doc: 'chat', section: 'science-lab' },
  },

  'lab-notebook': {
    title: 'A lab notebook',
    about: 'One problem worked step by step: the model\'s words, and a cell for each computation, check, solution and conversion with what the tool gave. On top, what was proved and what not.',
    parts: [
      { name: 'Checks', text: '**Proved** when SymPy shows it exactly; **holds numerically, not proved** when it is only true at the points tried; **refuted** with a point where it fails. A solution is checked by putting it back into every equation (✓ or ✗); a reaction is **balanced, checked** when each element and the charge count the same on both sides; a proof is **checked by Lean** only when Lean accepts it with no sorry.' },
      { name: 'Molecules and sequences', text: 'A molecule from its SMILES is drawn beside its formula, mass and properties; a sequence shows what it was turned into (its reverse complement, RNA or protein) and its figures.' },
      { name: 'The lab\'s check', text: 'An answer that no tool checked is not left at that: the lab asks the model to check it with its tools and correct it, and says so above the answer that follows. An answer that gives code is run instead: when no run had that code as written, the lab asks the model to run it and correct it.' },
      { name: 'Ask more', text: 'A next step, another case, or why a step holds: the lab goes on in the same notebook.' },
      { name: 'Open as a chat', text: 'The notebook is a chat underneath: open it there to branch, edit or share it.' },
    ],
    tasks: [{ title: 'Find a mistake', steps: ['Look for a **Refuted** cell.', 'Its detail says where it fails.', 'Ask the lab to correct that step.'] }],
    manual: { doc: 'chat', section: 'science-lab' },
  },

  'room-join': {
    title: 'Join a room',
    about: 'An invite link to a room: the room, its owner, how many are in it and the role you join with (member: read and write; viewer: read).',
    parts: [{ name: 'Join the room', text: 'You join, and the room opens; its owner hears. A link that expired, was used up or stopped says so: ask for a new one.' }],
    tasks: [{ title: 'Make a link for others', steps: ['In the room, open **People**.', 'Under **Invite links**, choose the role, how long it works and for how many, and press **Make a link**.', 'Copy it then: it is shown once.'] }],
    manual: { doc: 'chat', section: 'rooms' },
  },

  code: {
    title: 'Code Studio',
    about: 'Your workspaces (an IDE in the browser on your projects), reviews of merge requests by the model, and the code assistant. Reviews read the code with your own GitLab access, through Argus, and write nothing to GitLab.',
    parts: [
      { name: 'Business map', text: 'What each system does for the business, read from its code, documents, issues and wiki: Argus explains the business from it.' },
      { name: 'Workspaces', text: 'A GitLab project and branch the server keeps for you, with Code Arena\'s IDE: **Open** starts it, unused for 30 minutes it sleeps (its files stay), **Delete** takes it away. It clones and pushes with your GitLab token.' },
      { name: 'Merge requests', text: 'A project by its path (group/project): its open, merged or all merge requests. **Review** starts a review; a branch without a merge request can be reviewed against the one it goes into. GitHub and Gitea projects are github:owner/repo and gitea:owner/repo, once your token is linked in **Account → Code hosts**.' },
      { name: 'Ask about code', text: 'A question about a project, a branch or a merge request: answered in a chat with Argus, which reads the code as you may.' },
      { name: 'Automatic reviews', text: 'A project whose merge requests are reviewed as they open and get new commits (GitLab\'s webhook, and a look every few minutes), and a branch reviewed on a schedule. Read with your access, at your cost; each review can go to a room.' },
      { name: 'Reviews', text: 'Yours, and those of rooms you are in, with their verdict and how many findings are still open.' },
    ],
    tasks: [
      { title: 'Review a merge request', steps: ['Type the project\'s path and press **Show**.', 'Press **Review** beside the merge request.', 'Read the findings when it is ready; the bell says so.'] },
      { title: 'Review a project automatically', steps: ['Under **Automatic reviews**, press **A project** and type its path.', 'Choose the branches, how often to look, and a room if the team should see each review.', 'Press **Review automatically**, then put the URL and the secret token in the project\'s GitLab webhook (Merge request events).'] },
    ],
    manual: { doc: 'chat', section: 'code-review' },
  },

  business: {
    title: 'A business map',
    about: 'What one system does for the business: its domain and summary, what it lets the business do, the processes step by step, the rules it enforces and why, what it deals in, whom it talks to, and the words people use. Every item says where it was read.',
    parts: [
      { name: 'Sources', text: 'The files and lines, documents, issues, merge requests and wiki pages each item was read from; each opens in GitLab.' },
      { name: 'Find in the map', text: 'Narrows every part to the items that hold your words.' },
      { name: 'Ask Argus', text: 'A chat with Argus about this system: it explains the business from the map, then shows it in the code.' },
      { name: 'Map again', text: 'Reads the project again, with your access and at your cost: after the code, the issues or the wiki changed.' },
    ],
    tasks: [{ title: 'Map a system', steps: ['In **Code Studio → Business map**, type the project (group/project) and press **Map it**.', 'The map fills in as the model reads the code, the documents, the issues and the wiki.', 'Ask Argus about the business in any chat with Argus on.'] }],
    manual: { doc: 'chat', section: 'the-business-map' },
  },

  workspace: {
    title: 'A workspace',
    about: 'Code Arena\'s IDE on one of your workspaces, the whole window: its editor, terminals, search, Git, language servers, debugger and agent, all on the server.',
    parts: [
      { name: 'Its engine', text: 'Runs as a user of its own with limits, no Docker socket, and reaches the app, the gateway, Argus (with your key) and the internet only. Its agent has a key of its own at the gateway, against your credit.' },
      { name: 'Asleep', text: 'Unused for 30 minutes, it stops; its files stay. Opening it again wakes it.' },
    ],
    tasks: [{ title: 'Work on a project in the browser', steps: ['Under **Code Studio → New workspace**, name the project (and a branch) and press **Open it**.', 'The IDE fills the window once the workspace is up; **Open** on its card brings it back later.', 'Edit, run and commit as in Code Arena; its agent works on the same files.'] }],
    manual: { doc: 'chat', section: 'code-studio-workspaces' },
  },

  review: {
    title: 'A review',
    about: 'One review: what the change does, its risk and verdict, the CI, and the findings on its lines, each checked by a second pass.',
    parts: [
      { name: 'Findings', text: 'Each on a file and line, with its severity, why it matters and a fix. **Resolved** or **Dismiss** keeps track; ones a second check doubts are listed apart, with why.' },
      { name: 'Discuss in a room', text: 'A room with the review in it, for the people you add; the model answers there when someone writes @Arena.' },
      { name: 'Review again', text: 'After new commits: reads only what came after the last review.' },
    ],
    tasks: [{ title: 'Talk a review over with the team', steps: ['Press **Discuss in a room**.', 'Add the people in the room\'s **People** panel.', 'Write @Arena in the room to ask the model about it.'] }],
    manual: { doc: 'chat', section: 'code-review' },
  },

  'security-review': {
    title: 'A security review',
    about: 'One repository read for weaknesses: where data from outside reaches a query, a command, a path, a fetch, a deserialiser or HTML, each traced to its callers. A finding is kept only when it quotes the sink\'s line and names a CWE the security pack knows.',
    parts: [
      { name: 'Weaknesses', text: 'Each with its CWE and severity, the sink\'s line, where the data comes in and each step to the sink, how it could be exploited, the fix, and the security pack\'s pages it rests on. Ones a second check doubts are listed apart, with why.' },
      { name: 'Found by', text: 'The model\'s trace, the scanners (opengrep, Bandit, gitleaks, Trivy) where the scanners feature is installed, or both: filter by it. A scanner\'s match the model cleared is kept apart with what to check.' },
      { name: 'Packages, secrets, configuration', text: 'Vulnerable packages with the smallest upgrade (checked again when the advisories are renewed, new ones marked), secrets in the code or its history, and misconfigurations, each in its own section.' },
      { name: 'Triage', text: '**Fix it** (an agent\'s change, opened as a merge request as you), **Fixed**, **Accept the risk** (why, and by when to look again) or **Not a weakness** (why); each is audited.' },
      { name: 'Downloads', text: 'The bill of materials (CycloneDX) and the findings as SARIF, for a code host\'s or an IDE\'s security view.' },
    ],
    tasks: [{ title: 'Review a repository\'s security', steps: ['In Code Studio, under **Security**, name the repository and maybe a branch.', 'Press **Review security**.', 'Read each weakness and triage it, or press **Fix it**.'] }],
    manual: { doc: 'chat', section: 'security-reviews' },
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
      { name: 'Policies', text: 'How long members\' chats are kept, credits a month, one per kind (the chat, API keys, pictures, video, speech; shared or each member\'s), a cost centre, which safeguards apply to them, and the requests and tokens a minute each member\'s API key may use.' },
      { name: 'Delete', text: 'Tools and models given to it stop being available to its members.' },
    ],
    tasks: [{ title: 'Give a team a monthly credit', steps: ['Under **Policies**, type a credit for each kind to limit (**Chat**, **API keys**, **Pictures**, **Video**, **Speech**); empty is no limit.', 'Choose whether they are shared by the members or each member\'s.', 'Press **Save policies**: they hold at once.'] }],
    manual: { doc: 'admin', section: 'credits' },
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
      { name: 'Customize', text: 'A name and words of your own, each function on or off and asking first, fixed and default arguments, calls a day per person, and settings for some groups; **Try a function** calls it as you.' },
      { name: 'Toolsets', text: 'Named sets of tools people switch a chat to; a group\'s new chats can start with one.' },
      { name: 'New tool', text: 'A tool without code: an HTTP request to an address you fix, or a prompt on a model you choose. **Try it** before saving.' },
    ],
    tasks: [
      { title: 'Add an MCP server', steps: ['Press **Add a server or API** and choose **MCP server**.', 'Give it a name, its address, and its key as a header if it needs one.', 'Test it, then save. Choose who may use it.'] },
      { title: 'Turn on the web for the chat', steps: ['Turn on **Web** here.', 'Allow sites under Settings → Python and web → **Sites the chat may open** (or * for any public site).'] },
      { title: 'Choose who may use deep research', steps: ['Find **Deep research** here.', 'Choose who may use it: everyone, admins, or chosen groups. Only they see **Deep research** in the chat\'s **Tools**.', '**Ask before each run**: the model asks the person before it starts one itself.'] },
    ],
    manual: { doc: 'admin', section: 'admin' },
  },

  plugins: {
    title: 'Plugins',
    about: 'Ready-made tools for the chat: an API or an MCP server with its sign-in. Installed, each is a tool under Tools, where you choose who may use it.',
    admin: true,
    parts: [
      { name: 'Installed', text: 'Your plugins, with **Settings**, **Who may use it**, **Health** (calls, failures and time this week), **Check now**, **Update to …**, **Back to …** the version before, **Pin this version** and **Remove**.' },
      { name: 'Make a plugin and Check a plugin', text: 'A starter plugin with every part, and a check of a plugin\'s zip that says what would stop it installing and what it would ask for.' },
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

  'data-sources': {
    title: 'Data sources',
    about: 'The databases people may ask in words (PostgreSQL), which of their tables, and who; and what was asked of them. Every query runs read only.',
    admin: true,
    parts: [
      { name: 'A source', text: 'Who its connection signs in as and where, the tables open to questions, and who may ask it. **Change**, **What was asked** (the latest queries: who, the question, the SQL), **Read its tables again**, and **Remove**.' },
      { name: 'Connect a database', text: 'A name, the connection (kept encrypted, never shown again), **Try it** to list the tables its role may read, the tables to open, what the model should know, who may ask it, and the limits on rows and seconds.' },
      { name: 'Read only', text: 'Each query is one statement in a read-only transaction, never committed, after its plan shows it reads only the open tables. A superuser, or a role that may read the server\'s files, is refused.' },
    ],
    tasks: [{ title: 'Open the shop\'s orders to the sales team', steps: ['Make a role that may only read: grant select on the tables to it.', 'Press **Connect a database**, give the connection and press **Try it**.', 'Tick the orders and customers tables, note that totals are in euros, choose **Some groups** and the sales team.', 'Press **Connect**: they find it under Workspace → Data.'] }],
    manual: { doc: 'data', section: 'connecting-a-database-admins' },
  },

  storage: {
    title: 'Storage',
    about: 'What takes room and where, how long it stays, and ways to free it: the chat\'s files to browse and delete, clean-ups, and each person\'s room. Nothing of a person on legal hold is ever deleted here.',
    admin: true,
    parts: [
      { name: 'Where the room goes', text: 'The disks (the host\'s, and the folders the app mounts), how full each is and how fast it fills; then what takes room: each database and its largest tables, the chat\'s files, the model library, Argus, backups, logs and metrics, with their growth over 30 days. **How long things stay** lists the rules in force, and **What the app cannot see** what to look at on the host.' },
      { name: 'Files', text: 'The chat\'s files and what the tools made: whose, which chat, from where, how big and when. Filter by person, kind, where from, whether a chat still has it, size and age. Each downloads (audited); ticked ones are deleted after you confirm.' },
      { name: 'Clean-ups', text: 'Files in no chat, old pictures, videos and speech, files of deleted chats, leftovers on disk, old backups, and models nothing uses. **Preview** says what would go and the room it frees; **Clean up** asks first, and each run is audited. Old backups are only shown, with the command that removes them on the host: the app sees the backups read only.' },
      { name: 'People', text: 'Each person\'s files against their room. **Room** gives someone their own (0: no limit), or the company\'s again. **Give someone a room** finds anyone by name, before they have files.' },
      { name: 'Measure again', text: 'Measures everything now. Otherwise folders are measured at most every two minutes, and the trends written down every six hours.' },
    ],
    tasks: [
      { title: 'Free room on a full disk', steps: ['Look at **Where the room goes**: the disk says what of the stack is on it.', 'Open **Clean-ups** and press **Preview** on the ones that take most there.', 'Press **Clean up**, read what goes, and confirm.'] },
      { title: 'Limit what each person keeps', steps: ['Open Settings → Storage and set **Room for each person\'s files**.', 'For someone who needs more, open **People** here and press **Room**.'] },
    ],
    manual: { doc: 'admin', section: 'storage' },
  },

  'sign-in': {
    title: 'Sign-in',
    about: 'Who can sign in, and how: local accounts, the company directory, and company sign-in. The rules and limits are under Settings.',
    admin: true,
    parts: [
      { name: 'Local accounts', text: 'Always on. Admins add people under People. **Sessions and limits** opens their settings.' },
      { name: 'Company directory (LDAP)', text: 'When set up: the server, the admin and required groups, and how often people are checked. **Check the directory now** disables people who left the directory or the required group; if the directory cannot be used, or the required group is not found or nobody can be found in it, it changes nobody and says why.' },
      { name: 'Company sign-in', text: 'OIDC or SAML at your identity provider: its admin and required groups, and whether SCIM is on. **Configure** opens its settings.' },
    ],
    tasks: [
      { title: 'Turn on the company directory', steps: ['Press **Configure** on the directory card (Settings → Company directory).', 'Follow the guide at the top: the server, the service account and where people are, then the groups. Each field\'s help has an OpenLDAP and an Active Directory example.', 'Press **Test the settings**: each step says what works and, when something does not, exactly what to fix.', 'Under **Try a person\'s sign-in**, type someone\'s username and password and press **Try it**: it shows who they would be here. Then save.'] },
      { title: 'Require two-factor sign-in', steps: ['Open Settings → Sign-in and set Require two-factor sign-in to admins or everyone (or, for one group, Admin → Groups → the group → Policies → Require two-factor sign-in).', 'Each person it covers who has none sets it up at their next sign-in, before anything else; a session already open goes on until it ends.', 'Admin → Compliance shows the admins who still have none.'] },
      { title: 'Find out why someone cannot sign in', steps: ['Open **Audit log** and look for their sign-in: it has the directory\'s reason.', 'In Settings → Company directory, under **Try a person\'s sign-in**, type their username and password and press **Try it**: it shows each step, and who they would be here or why not. A wrong password counts as a wrong sign-in does.'] },
    ],
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
      { name: 'Other servers and providers', text: 'Another machine\'s OpenAI-compatible engine, or a provider\'s API (OpenAI, Anthropic, Gemini, OpenRouter...) with the company\'s key: their models the gateway serves beside these, to the chat, API keys and Code Arena.' },
      { name: 'Find on Hugging Face', text: 'Search GGUF models, see whether they fit this machine, and download them into the library. **Downloads** shows the progress.' },
      { name: 'Add a model', text: 'One dialog for every kind: choose a file from the model library or a model the speech server has, and it is taken as what it is (a chat, picture, video or speech model), which you can change. A chat model\'s form shows what fits this machine. **Edit** on a card opens the same dialog.' },
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
      'company-directory-ldap': { name: 'Company directory (LDAP)', text: 'Sign-in with the company directory (LDAP or Active Directory): the server, a read-only service account, where people and groups are, and the admin and required groups. A guide opens the group, with OpenLDAP and Active Directory examples. **Test the settings** checks the values in the form before you save them, step by step, and says exactly what is wrong; **Try a person\'s sign-in** checks someone\'s username and password as signing in would. Neither saves anything.' },
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
      'sign-in-notice': { name: 'Sign-in notice', text: 'What everyone accepts before using the service (acceptable use, the privacy notice): shown on the sign-in page, and accepted once a version by each person before anything else; each acceptance is kept, with its version, and is in the audit log and the evidence. A new version is accepted again.' },
      compliance: { name: 'Compliance', text: 'How often access is reviewed (a review starts by itself and the admins are told), after how many days an account counts as unused, and whether unused accounts are disabled every day.' },
      'audit-log': { name: 'Audit log', text: 'Sends every entry of the audit log on to the company\'s SIEM as it is written, in order and each once: by syslog (udp, tcp or tls) and to a webhook, signed with its key. A collector that does not answer is tried again; Admin → Compliance shows how far it got.' },
      speech: { name: 'Speech', text: 'Reading aloud and speech to text for everyone, unless a person chooses their own under Your account → Voice: the language people speak (or found by itself), the voice for each language, the reading speed, and whether Talk reads answers aloud. **Try it** reads a sample with the voice chosen.' },
      storage: { name: 'Storage', text: 'When a disk is too full (the alert), how much each person\'s files may take, and what the clean-up under Admin → Storage takes as old media.' },
      prices: { name: 'Prices', text: 'What a chat model costs when it has no price of its own under Admin → Models (input, cached input and output, per million tokens), and what pictures, video and speech cost. Every request gets its cost from these. **Recalculate past costs** prices requests recorded at no cost, for the dates you choose, after showing how many and the total.' },
      'api-keys': { name: 'API keys', text: 'The requests and tokens a minute each key may use (a group\'s or a person\'s own replaces them; 0: no limit), and the answer cache: repeated API requests answered from it, at no cost, and for how long.' },
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

  compliance: {
    title: 'Compliance',
    about: 'ISO 27001, SOC 2, HIPAA and the GDPR: what Arena does for each control, whether it holds now, the company\'s part attested, and the evidence an auditor asks for.',
    admin: true,
    parts: [
      { name: 'Frameworks', text: 'For each one, the controls that serve it and its clauses, how many are met, in part or not met now, and how many the company has attested. Choose one to see only its controls.' },
      { name: 'Checks now', text: 'What the app checks on itself: passwords and lockout, sessions, admins\' second factor, the audit log, how long chats and logs are kept, the newest backup, the certificate and secrets encrypted at rest. Amber says what to change.' },
      { name: 'Controls', text: 'Each control: what meets it in Arena, the clauses it serves, its checks, where it is documented, the tests that prove it in every release, and the company\'s part.' },
      { name: 'Attest', text: 'Says the company\'s part is done, with what and where its proof is. Kept with your name and the date, and in the audit log; it counts for a year. Needs the Compliance officer role (or Owner).' },
      { name: 'Access reviews', text: 'Who holds privileged access and the accounts unused for long, each kept or revoked; a new one starts by itself every quarter. **Start a review** starts one now.' },
      { name: 'Data requests', text: 'People\'s requests about their data (a copy, a correction, restriction, objection, erasure), each due within a month: answer it, restrict their processing, erase everything (with a certificate), or refuse with the reason.' },
      { name: 'Security alerts and incidents', text: 'What the app noticed (many failed sign-ins, an admin role given, many exports, a failed or late backup), each raised once to every admin; open an incident from one, or by hand.' },
      { name: 'Download evidence', text: 'One zip: the controls, every check\'s result, the attestations, the settings (no secret), who holds an administrative permission, the access reviews, the audit log\'s chain and its last 90 days by action. Each download is audited.' },
    ],
    tasks: [
      { title: 'Prepare for an audit', steps: ['Fix what is red or amber under **Checks now**: each says what to change.', 'For each control, do the company\'s part and **Attest** it with where its proof is.', 'Run `python3 deploy/scripts/compliance-check.py` on the host for the checks from outside.', '**Download evidence** and give it to the auditor with your policies.'] },
    ],
    manual: { doc: 'compliance', section: 'controls-and-how-they-are-kept' },
  },

  'access-review': {
    title: 'Access review',
    about: 'Who holds privileged access, and the accounts nobody used for long: each kept or revoked by a reviewer, and the review kept for good once closed.',
    admin: true,
    parts: [
      { name: 'Privileged roles', text: 'Each administrative role given to a person, a group or everyone. **Revoke** takes it away at once (audited). The first admin is always an Owner and stays one.' },
      { name: 'Members of privileged groups', text: 'Each member of a group that holds such a role. **Revoke** removes them from it; a directory\'s or SCIM\'s group says to change it there.' },
      { name: 'Unused accounts', text: 'Nobody signed in to them for long (Settings → Compliance). **Revoke** disables the account.' },
      { name: 'Keep the rest', text: 'Keeps every item not decided yet, under your name.' },
      { name: 'Close the review', text: 'Once every item is decided. A closed review cannot change; its decisions are in the evidence download.' },
    ],
    tasks: [{ title: 'Review access for the quarter', steps: ['Open the review from Admin → Compliance (or the bell, when the schedule started it).', 'For each item: **Keep** it, or **Revoke** it when the person no longer needs it.', 'Press **Close the review** when nothing is left to decide.'] }],
    manual: { doc: 'compliance', section: 'access-reviews' },
  },

  incident: {
    title: 'Incident',
    about: 'A security incident from finding to closure: the clocks the law sets, each step on its timeline, and what was learned.',
    admin: true,
    parts: [
      { name: 'What the law asks', text: 'With personal data: 72 hours from finding it to tell the supervisory authority, or to record why it need not be told (GDPR 33). With health data: 60 days to tell the people (HIPAA 164.404), and HHS. Each clock says how long is left, or how late.' },
      { name: 'Steps', text: 'A note, contained, resolved, authority told (or need not be, with why), people told, and closed with what was learned. Each is kept with your name and the time, and in the audit log.' },
      { name: 'Timeline', text: 'Every step, oldest first: the record an auditor and the authority ask for.' },
    ],
    tasks: [{ title: 'Handle a personal data breach', steps: ['Open an incident (from its alert, or by hand) with personal data ticked.', 'Contain it, then tell the supervisory authority within 72 hours, or record why it need not be.', 'Tell the people when the risk to them is high.', 'Close it with what was learned.'] }],
    manual: { doc: 'compliance', section: 'security-alerts-and-incidents' },
  },

  'admin-security': {
    title: 'Security',
    about: 'Every repository\'s latest security review at a glance: what is open, what cannot wait, what changed this week, and the risks accepted.',
    admin: true,
    parts: [
      { name: 'The numbers', text: 'Open findings by severity, the known-exploited, those past their time to fix (7, 30, 90 and 180 days by severity, from when each was found), new and fixed this week.' },
      { name: 'Repositories', text: 'Each repository and branch\'s latest review, the known-exploited and most severe first, with its bill of materials. A row opens the review.' },
      { name: 'Cannot wait', text: 'Open findings known to be exploited, and those past their time to fix.' },
      { name: 'This week', text: 'Found in the last seven days, by reviews and by the packages checked again when the advisories are renewed; and what was fixed.' },
      { name: 'Risks accepted', text: 'Who accepted each, why, and until when; lapsed ones are marked.' },
    ],
    tasks: [{ title: 'See what needs doing first', steps: ['Read **Cannot wait**.', 'Open a finding\'s review and press **Fix it**, or triage it.'] }],
    manual: { doc: 'admin', section: 'security' },
  },

  'usage-groups': {
    title: 'Usage by group',
    about: "Each group's requests, tokens and spend over a day, a week, 30 or 90 days, chat and API keys together, and the people in no group.",
    admin: true,
    parts: [
      { name: 'The groups', text: 'Members, how many used it, requests, tokens, the share of input read from the cache, and spend, the most spent first; a person in two groups counts in both.' },
      { name: 'Members', text: 'Open a group for its members who used it, each with their own requests, tokens and spend.' },
    ],
    tasks: [{ title: 'See which team uses the models most', steps: ['Choose **30 days**.', 'Read the spend column.', 'Open the group for who in it.'] }],
    manual: { doc: 'admin', section: 'usage-by-group' },
  },

  line: {
    title: 'Line',
    about: 'Chat answers and API requests running now, and who waits in the order they will be served, every two seconds.',
    admin: true,
    parts: [
      { name: 'Waiting', text: 'Each request waiting its turn: its place, whose it is, the model, chat or API, how long it has waited and its priority.' },
      { name: 'Running', text: 'What runs now, the longest first.' },
      { name: 'The limits', text: 'How many run at once for everyone, per person, the longest line and how long a wait lasts (Settings → Chat), and each model\'s places.' },
    ],
    tasks: [{ title: 'Find out why someone waits', steps: ['Find them under **Waiting**.', 'See who runs before them, and the limits above.', 'Raise a limit in Settings → Chat, or theirs in Admin → People.'] }],
    manual: { doc: 'admin', section: 'line' },
  },

  feedback: {
    title: 'Feedback',
    about: 'What people send about Arena with Send feedback: ideas, problems, praise; each marked read or done, or answered.',
    admin: true,
    parts: [
      { name: 'The list', text: 'Each one with its kind, words, who sent it and the page it came from, newest first.' },
      { name: 'Handling one', text: 'Mark it read or done, or answer it: the answer goes to the person\'s bell.' },
    ],
    tasks: [{ title: 'Answer a problem report', steps: ['Open it from the list.', 'Write the answer.', 'Send it: the person is told, and it shows under Your feedback.'] }],
    manual: { doc: 'admin', section: 'feedback' },
  },

  prompts: {
    title: 'Prompt log',
    about: "Everyone's questions and the answers they got, in any state, in the chat and with API keys, for those with prompts.review. Every search is in the audit log.",
    admin: true,
    parts: [
      { name: 'Search', text: 'Words in the question (and in an API answer), a person by user name or email, a state, and where: all, chat or API.' },
      { name: 'The log', text: 'Each question with its state (waiting, running, done, failed and why, stopped, refused), answer, person, model, chat, tokens and time taken, newest first; **Show all** opens a long one, **Older** goes on.' },
    ],
    tasks: [{ title: 'Look into a complaint about an answer', steps: ['Search by the person and a few words of the question.', 'Read the answer they got.', 'The search itself is audited as prompts.viewed.'] }],
    manual: { doc: 'admin', section: 'prompt-log' },
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
      { name: 'Time range', text: 'The period every panel shows: the last hour unless you choose another, from five minutes to 90 days. It goes in the address, so a link opens the same view.' },
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
      'llm-users': { name: 'LLM and users', text: 'Requests, tokens, latency and time to first token; the line (running, waiting, the average wait, refusals); the engine\'s speed; and who uses what. With usage.read.' },
      system: { name: 'System', text: 'The services: which answer, their alerts, errors, restarts, the database and the logs\' volume. With monitoring.read.' },
      hardware: { name: 'Hardware', text: 'GPU, CPU, memory, disks, network and temperatures of the machine. With hardware.read.' },
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
