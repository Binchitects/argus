# Code Arena's code index: measured

What the code index (docs/design/code-index.md) is worth, measured on tasks made from this repository's own history and
text before any model ran (evals/run_code_index.py, evals/code-index-tasks.json: 60 *locate* tasks from real commits, 20
*string* tasks from messages that appear once). Half the tasks (by hash) tune the index's weights, the other half judge it.

## Retrieval alone, no model (2026-10-30)

`python3 evals/run_code_index.py retrieve --program code-arena`: for each task, the repository checked out where the task
stands, `code-arena search "<question>" --json --budget 2000`, and the files it cites in order. The question is the commit's
subject (locate) or the message (string). Results: evals/code-index-retrieval.json.

**hit@k** is the share of tasks with a right file among the first k files cited (for a message: its line inside a cited
place, give or take two). **recall@5** is the share of a commit's changed files among the first five.

| Family | Arm | Tasks | hit@1 | hit@3 | hit@5 | hit@10 | recall@5 |
|---|---|---|---|---|---|---|---|
| locate | index | 60 | 53% | 73% | 78% | 78% | 51% |
| locate | grep-rank | 60 | 13% | 40% | 62% | 77% | 35% |
| string | index | 20 | 90% | 90% | 90% | 90% | – |
| string | grep -F | 20 | 100% | 100% | 100% | 100% | – |

- **The baselines.** *grep-rank* ranks the source files by how many of the question's words they hold (`git grep -i -w -c`,
  each word), then by how often: what an agent gets from grepping the words one by one. For a message, *grep -F* finds its
  line exactly, as it should: the tool's description sends exact text, regexes and every-occurrence questions to grep.
- **Locate:** the index puts a changed file first in 53% of the tasks against 13%, and among the first five in 78% against
  62%. The tuning half (35 tasks) and the judging half (25) agree: hit@1 51% and 56%, hit@5 80% and 76%. The weights are
  still the design's starting ones: nothing has been tuned yet.
- **String:** a question that a file holds word for word lifts the symbol holding that line (the phrase signal), which
  took the index from 65% to 90%. The two misses are short messages in long methods whose words many files share.
- **Cost:** a median answer of 6,751 characters (≈1,930 tokens at 3.5 characters a token) for a budget of 2,000; a median of
  1.4 s a search, the process's start, loading the 15 MB index and syncing it after each checkout included.

## With a model (2026-10-30)

`python3 evals/run_code_index.py run --arms grep,index --models Qwen3-4B-Q4_K_M --runs 1 --split test`: the judging half
(25 locate and 10 string tasks), one run each, Code Arena in ask mode (`-p` asks no one, so nothing is changed), on the
live stack's model. Results: evals/code-index-results.json.

| Arm | Family | Runs | Pass | Files named (recall) | Prompt tokens (median) | Rounds (median) | Runs with no tool | Seconds (median) |
|---|---|---|---|---|---|---|---|---|
| grep | locate | 25 | 0 | 0% | 10,879 | 1 | 15 | 42 |
| index | locate | 25 | 0 | 1% | 23,655 | 2 | 11 | 72 |
| grep | string | 10 | 1 | 90% | 22,192 | 2 | 0 | 59 |
| index | string | 10 | 0 | 90% | 25,778 | 2 | 4 | 46 |

- **Qwen3-4B cannot do these tasks in either arm.** For a commit's subject it mostly answers at once from nothing (15 of
  25 runs with no tool in the grep arm, 11 in the index arm), or plans and runs steps it does not need (`plan` 8 times,
  `run_steps` 5, a `screenshot`, a `search_sessions` in the grep arm). For a message, both arms name the right file nine
  times in ten but give no line within two of it (one run did, with grep).
- **The index's tools are taken up more readily**: 13 `code_search` and 2 `code_symbol` calls over the 25 locate runs,
  against 3 `grep` calls in the grep arm. The prompt tokens are higher in the index arm because more of its runs go a
  second round, not because its answers are bigger.
- **The gate is not met**, so `codeIndex` stays off by default. A fair verdict needs a model that can work with tools
  over several rounds (14B and up, or a remote one): the live stack runs only Qwen3-4B, and loading another model there
  is the person's call. The retrieval numbers above are what the index itself does.
- Runs since keep the start of each answer in the results (`answer`), to read why a run failed.
