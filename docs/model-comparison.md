# Three models, measured

`qwen3.6:27b`, `qwen3.6:35b` and `qwen3.8:27b` on the same ten task families —
test development, code review, performance, coding style, SDK, WDK, win32,
scripting, security review, code safety. One question each, graded by
substring match on 17 fact tokens **verified against the packs before any
model ran**.

Reproduce with [`evals/run_model_bench.py`](../evals/run_model_bench.py).

## The numbers

| model | params | closed book | with Argus | delta | tool calls |
|---|---|---|---|---|---|
| `qwen3.6:27b` (dense) | 27.8B | 5 / 10 | **10 / 10** | +5 | 26 |
| `qwen3.6:35b` (MoE) | 36.0B | 5 / 10 | 9 / 10 | +4 | 19 |
| `qwen3.8:27b` (dense) | 27.3B | 5 / 9 | 9 / 10 | +4 | **33** |

`5/9` is not a typo: one `qwen3.8` closed-book row returned
`HTTP 500` from Ollama and is graded `ERROR`, which is excluded from the
denominator rather than counted as a wrong answer.

## Every model scores 5 closed book, and fails the same five tasks

| | closed book | with Argus |
|---|---|---|
| coding-style, performance, scripting, sdk-support, code-safety | all three PASS | all three PASS |
| test-development, code-review, wdk-support, win32-support | all three FAIL | all three PASS |
| security-review | all three FAIL | 3.6:27b only |

This now holds across **two model generations and an 8-billion-parameter
gap**. Not similar scores — the same five tasks, task for task, three times.

The five they all pass are widely-documented facts: amortized complexity, MSVC
flag syntax, robocopy switches. The five they all fail are driver IRQLs, the
documented header for `CreateFileW` (`fileapi.h`, not the `windows.h` memory
reaches for), and kernel safe-string routines. Those are **recall** failures on
facts too specialised to sit in any of these models' weights, and neither a
newer generation nor more parameters moved them.

> Scale does not fix this. A newer generation does not fix this. Retrieval does.

## More tool calls is not more correct

`qwen3.8:27b` made **33 tool calls, the most of any model, and did not score
highest.** `qwen3.6:27b` reached 10/10 with 26. Willingness to check is
necessary and not sufficient.

The `security-review` failures show why, because the three models failed the
same task for three different reasons:

| model | calls | answered | |
|---|---|---|---|
| `qwen3.6:27b` | 5 | `RtlStringCchCopyW` / `ntstrsafe.h` | correct |
| `qwen3.6:35b` | **0** | `wcscpy_s` / `<wchar.h>` | from memory, user-mode answer |
| `qwen3.8:27b` | **4** | `RtlCopyUnicodeString` / `wdm.h` | retrieved, real, wrong API |

`qwen3.8` is the interesting one. It checked, it retrieved a genuine kernel
routine, and it reported that routine's documented header and library
accurately. `RtlCopyUnicodeString` simply is not the replacement for `wcscpy`
into a fixed buffer — the safe-string routines in `ntstrsafe.h` are.

So its answer is **correct about the wrong API**. That is the same failure
class as 35b's `wcscpy_s`, reached by the opposite route: one arrived by not
looking, the other by looking and choosing badly. Retrieval fixes recall. It
does not fix API selection, and nothing in this project currently does —
`docs_verify` cannot either, because every fact stated about the wrong API
checks out.

## Which model to run

`qwen3.6:27b`, on this evidence: the only one to reach 10/10, and it does so
while being the smallest of the three. It is also the only one that got
`security-review` right, by checking rather than recalling.

The margin is one task in ten, so the honest claim is "checks more reliably",
not "better at everything". `qwen3.8:27b` is a newer generation and calls tools
more eagerly, which may matter more on tasks unlike these; on this bench it
converts that eagerness into a wrong API rather than a right one.

## Caveats

Ten questions, one run each, no repeats — this measures a direction, not a
ranking with confidence intervals. The grading is deliberately mechanical
(substring match on documented tokens) so nothing depends on judging prose,
but that also means a right answer phrased unusually would score wrong.

The answer key is checked against the packs before every run, so a key written
from memory cannot silently mark a correct model wrong.
