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

## With a model

Not run yet: the grep and index arms with Qwen3-4B-Q4_K_M (pass rate, tokens, rounds, tool calls) come with the tools
(design commit 14), and decide whether `codeIndex` is on by default (commit 15, after the person sees the numbers).
