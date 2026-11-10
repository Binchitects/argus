# Code Arena's tools, measured

Code Arena's own tools (grep, read_file, run_shell, run_code) and Farzan Graph
Arena's long attachments, on the tasks a model gives them, before and after
v7.8's changes (2026-11-06). Asked for because grep and the shell were slow
and failed, the Python tool did nothing useful, and large files filled the
window.

Produced by the tests that hold the numbers, each printing its table:

- `tests/CodeArena.Tests/ToolMeasureTests.cs`: grep, read_file and
  run_shell on a tree of 3,000 source files of 200 lines, a 4 MB minified
  bundle, 200 binary files and 2,000 files `.gitignore` leaves out;
- `tests/CodeArena.Tests/CodePythonTests.cs`: run_code through the whole
  agent (a fake gateway gives the model's calls), with Python 3 (skipped where there is none, as in the .NET SDK image: here the host's Python was mounted in);
- `tests/Llm.Tests/FileOutlineTests.cs`: Farzan Arena's attachments.

The build host is shared with a live stack and power capped: times are a
guide, within some tens of milliseconds.

## grep

| task | before | after |
|---|---|---|
| a literal in the tree (git) | 139 ms, 7 matches | 127 ms, 7 matches |
| a regex, files only | 145 ms | 84 ms (stops at a file's first match) |
| a pattern of nested repeats (`(a+)+$`), 300 files | **300,005 ms**, "No matches" | **9 ms**, no matches |
| a 21 MB log given as `path` | **"No matches"** (skipped as too big) | the line found, 93 ms |
| outside git, with a `.gitignore` | **2,007 files**, the 2,000 ignored among them | 7 files |

Causes: each line was matched with .NET's backtracking engine and a time
limit of 1 s, and a pattern that timed out left only that file: 300 files,
300 seconds. A file over 5 MB was skipped without a word, even when it was
the one named. Outside a git repository the walk skipped only the build
folders, not what `.gitignore` says.

Now the pattern runs without backtracking (linear in the line; one with a
lookaround or a backreference keeps the time limit, and a pattern that runs
out of it stops the search with an error saying why), files are read a line
at a time and several at once, a file given as `path` is read whatever its
size, the big files skipped in a folder are named, and a search past 30 s
says how many files it left. ripgrep was not used: it is not on the build
host nor, as a rule, on the people's machines, and its regex syntax is not
.NET's, which the tool's description promises; the managed path is as quick
on these trees.

## read_file and @files

| task | before | after |
|---|---|---|
| a 6,604-line C# file, no range | 92,180 characters (its first 2,000 lines) | 7,485 (its outline: 60 classes, their 300 methods by name and line; its first 30 lines) |
| one method of it | lines by offset only | `symbol: "Part42.Method3"`: 1,094 characters |
| `@shop.go` of 844 lines in a message | 32,425 characters (the whole file, numbered) | under 8,000 (its outline) |

Before, the model's window took 92,180 characters for a file it then had to
page through, kept aside past 16,000 with only its start shown. Now a file
over 500 lines or 24,000 characters read without a range gives its outline
(the index's scanners for C#, TypeScript, JavaScript, Python and Markdown;
other languages by the lines that look like declarations), and the model
reads the part it needs by lines or by a symbol's name.

## run_shell

| task | before | after |
|---|---|---|
| 2.6 MB of output, the error in its middle | 30,045 characters; **the error gone** | 29,270 characters, the error line given with its number, and the whole output kept for `read_result` |
| a command killed by a signal | `[exit code 137]` | `[exit code 137 (killed by signal 9, SIGKILL: often out of memory)]` |
| a command that leaves a child running | 2,002 ms | 2,003 ms (it does not wait for the child) |

## run_code (Python)

| task | before | after |
|---|---|---|
| four calls of one data task, each using the last one's variables | **1 of 4 ran** (NameError in the others) | 4 of 4 |
| `import` a package of the project's `.venv` | **ModuleNotFoundError** | imported, `sys.prefix` the venv |
| a last line `total + 0.5` | `(no output)` | `30.5` |
| a matplotlib plot | nothing came back | saved in `.code-arena/plots/`, shown to the model |
| a call past its time | stopped | stopped, and the next call is told its variables are gone |
| Windows (not measured here) | Python there has no `socket.AF_UNIX`: a program's first tool call failed | TCP on 127.0.0.1 with a token |

Each call ran a new `python3` with nothing of the last: a model that loaded
data in one call and used it in the next failed every time, and the
project's virtual environment was never the Python that ran. Now each chat
has one interpreter, kept between calls. run_shell with `python -c` was the
other choice; it has the same fault (nothing kept), so the tool was kept and
made a notebook's kernel.

## Farzan Arena's long attachments

| task | before | after |
|---|---|---|
| a 118,681-character manual (40 chapters, 120 steps) | its first 30,000 characters | 8,207: its start and its outline; `read_file` with `section: "Chapter 40"` reads one |

## A real run

Code Arena built from this change, with Qwen3-4B (Q4_K_M) on llama.cpp on
the CPU (the GPU was the live stack's), each task in a new session in
`--mode yolo`, on a made project: 2,000 Python files, a 3,160-line module, a
CSV of 3,000 sales and a check script of 120,000 lines with one failure in
its middle. The model is slow there (5 to 8 tokens a second): the seconds
are the model's, every tool answered in under 0.1 s.

| task | calls | tool errors | right | seconds |
|---|---|---:|---|---:|
| where `parse_order` is defined and who calls it (grep) | grep once (7 matches in 4 files) | 0 | yes | 183 |
| what `compute_discount` gives a gold customer, in the large module | read_file (its outline, 6,878 characters), then read_file of lines 1914-1933 | 0 | yes (34.0) | 337 |
| two run_code calls, the second using the first's variables | 5 run_code | 2: a NameError that was Code Arena's (below), and the model's own | yes (east) | 955 |
| the same, with calls in their order | 4 run_code | 2, both the model's own (a name it never set, a wrong zip) | yes (east, its last call's result; the server was stopped as it wrote its answer) | 1,264 (the machine busier) |
| which check failed in `./check.sh` | run_shell (15,188 characters shown) | 0 | **no**: "all 120,000 checks passed" | 541 |
| the same, with the error lines named | run_shell, then grep | 0 | yes (check 60001, stock negative for sku 4471) | 649 |

The run found two faults the fakes had not, fixed since and in the table's
second rows: the model gave both run_code calls in one answer and they ran
at once, the second first (its NameError was Code Arena's, not the
model's); and the one failure in the cut middle of a long output was gone
for the model, which passed over the note that the whole output was kept.
Now calls that change something run in their order, and the lines of the
cut part that look like errors come with the result.

Before this change the large module would have come as its first 2,000
lines (about 100,000 characters, kept aside past 16,000 with only its start
shown); grep and run_shell answered these tasks the same.

## What is next

Tool results past 16,000 characters are kept aside with their start and end
shown (since v7.0); results before the last two questions are cleared before
the session compacts. Next: a summary of a long result by the small model in
place of its start, measured on the tool-modes tasks.
