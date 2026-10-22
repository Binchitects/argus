# Agent Studio

Workflows that do the work as you. A trigger starts one. Its steps ask a
model, use tools, decide, loop and wait for your yes. What they give out
goes to a room, an email, GitLab or a file. Every run is kept, with each
step as it went, to read and to replay.

## Making a workflow

**Workspace → Agent Studio.** Press **New workflow**, start from a template,
or turn one of your scheduled tasks into a workflow with **From a scheduled
task**. A new workflow starts **Off**: it runs only by hand until you switch
it **On**.

The canvas shows the workflow from the top down: the trigger, then the
steps in order. Choose the trigger or a step to change it in the panel
beside the canvas. Press **+** between two steps to add one there.
**Save** keeps your changes, and **Run now** runs the saved workflow.

### What starts it

- **By hand**: **Run now**, with words to start from, or with fields of your
  own.
- **On a schedule**: a five-field cron (`0 8 * * 1-5` is weekdays at 8), in
  your time zone.
- **On a webhook**: any system posts JSON to its address, with its secret in
  the `X-Hook-Secret` header. The secret is shown once; **A new secret**
  replaces it.
- **On GitLab events**: a merge request that opens or changes, a pipeline
  that fails, or an issue. Give GitLab the workflow's address as a webhook,
  and its secret as the secret token. The event's words (a merge request's
  changes, the end of a failed job's log) are the input.
- **On a room's message**: a message in a room that starts with its word
  (`#triage`). The rest of the message is the input, and `{{input.by}}` is
  who wrote it. What the workflow itself says in the room starts nothing.
- **From a form**: fields you choose (a line, paragraphs, or a choice;
  needed or not). Share the workflow, and others fill its form.

### Steps

| Step | What it does |
|---|---|
| **Ask a model** | One question to your model. Its answer can be JSON, read as `{{id.json.…}}`. **The model for small steps** is quicker and cheaper. |
| **Agent with tools** | The model with the tools you choose (the web, code search, your data…), as a chat of yours to open from the run. |
| **Use a tool** | One function of a tool you may use, with its arguments. A function that asks a person first (a browser's send, an API's write) is not run by a workflow. |
| **If** | A test (contains, is, is more than, is empty…), then its **Yes** steps or its **No** steps. |
| **For each** | Its steps for each item of a list: a step's JSON list, or one item a line; at most 50. `{{item}}` is the item, `{{index}}` its number from 0. |
| **Ask me first** | The run waits for your answer, and a notification tells you. **Yes, go on** carries on from there; **No, stop it** ends the run. |
| **Write text** | Words made of what came before. |
| **Say in a room** | A message in a room you write in, from you, for its people. |
| **Email me** | An email to your account's address. |
| **Comment in GitLab** | A comment on the merge request or issue that started the run (a GitLab trigger). |
| **Save a file** | A file of yours, to download from the run. |

Any text in a step may hold values. Press **{}** to put one in where the
cursor is:

- `{{input.text}}` is the run's input, and `{{input.by}}` who started it;
- `{{input.<field>}}` is a field's value;
- `{{<step id>.text}}` is what a step before it gave;
- `{{now}}` is the date and time.

## Runs

**Runs** lists every run, the newest first. Open one to see it two ways:

- **Replay** walks its steps one by one on the canvas, each with what it was
  given and what it gave.
- **Timeline** lists all its steps.

A step that failed says why, and the run stops there. **Stop** ends a run
that is going or waiting. When a run goes on after **Ask me first**, the
steps that already ran are not run again: their answers are kept.

A run takes at most 300 steps (each turn of a loop counts).

## Sharing

**Settings → Who may run it**: only you, some groups (yours), or everyone.
Those it is shared with run it by hand or fill its form. It runs as you,
with your tools and your credit. They see how it went, but not what it read
or what each step gave, except its **Write text** steps.

Workflows need the `feature.studio` permission. Everyone holds it unless an
admin denies it.
