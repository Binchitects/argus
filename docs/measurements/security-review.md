# The security review, measured

The security review (Code Studio → Security, `SecurityReview.cs`) and the
scanners image's rules, measured on three public test suites (2026-10-28,
after v7.4.0):

- **OWASP Benchmark** for Java (GPL-2.0, read here and never shipped; clone
  at f40c618c, 240 MB): 2,740 servlets in 11 categories, each a real weakness
  or a look-alike that is safe.
- **Juliet for Java 1.3** (NIST SARD, public domain, 77 MB): 6,241 cases in
  the 14 categories the review and the rules cover.
- **Juliet for C# 1.3** (NIST SARD, public domain, 69 MB): 4,986 cases in 15
  categories.

Produced by `evals/run_security_review.py` (results in
`evals/security-review-{owasp,juliet-java,juliet-csharp}-results.json`). The
suites name their answers (Juliet's comments say FLAW and FIX, its classes are
named for the CWE and its methods `bad` and `good`; the Benchmark's servlet
paths name the category), so each case is read with its comments removed and
those names replaced, line numbers kept: neither the model nor the scanners can
read the answer. Four arms:

- **scanners**: the scanners image's opengrep with Arena's rules, on every
  case;
- **model**: the review's own sinks, tracing question, grounding on the cited
  line and skeptic, told only the CWE's id;
- **pack**: the same with the security pack: a finding is kept only for a CWE
  the pack knows, and the skeptic is given the pack's page;
- **both**: the review as it ships: the scanners' matches traced too and named
  in the question, the pack, and the scanners' findings.

The model arms read 12 cases a category (seed 7; the Benchmark's half real,
half safe), each as a repository of its own, the way the review reads one, as
a throwaway person (removed after) with the default chat model, Qwen3-4B
(Q4_K_M). Scored per case and flow: a finding of the case's weakness (or one of
its kin, CWE-23 for CWE-22) in Juliet's bad flow is a true positive, in one of
its good flows a false positive; a Benchmark case is positive when a finding of
its weakness is in its file. The score is Youden's J (true positive rate less
false positive rate), the Benchmark's own: 0 is guessing, 1 is every weakness
found and no safe code flagged.

## The scanners, on every case

| suite | rules | precision | recall | score |
|---|---|---:|---:|---:|
| Benchmark | v7.4.0 | 0.664 | 0.117 | 0.054 |
| Benchmark | now (923a1cf1) | 0.720 | 0.852 | 0.498 |
| Juliet Java | v7.4.0 | 0.463 | 0.484 | 0.202 |
| Juliet Java | now | 0.471 | 0.512 | 0.223 |
| Juliet C# | v7.4.0 | 0.435 | 0.215 | 0.067 |
| Juliet C# | now | 0.570 | 0.485 | 0.291 |

## The review, on the cases the model read

| suite | cases | arm | precision | recall | score |
|---|---:|---|---:|---:|---:|
| Benchmark | 132 | scanners | 0.738 | 0.894 | 0.576 |
| | | model | 0.571 | 0.182 | 0.045 |
| | | pack | 0.556 | 0.152 | 0.030 |
| | | both, as v7.4.0 shipped | 0.723 | 0.909 | 0.561 |
| | | **both, now** | 0.716 | 0.955 | **0.576** |
| Juliet Java | 168 | scanners | 0.578 | 0.440 | 0.244 |
| | | model | 0.587 | 0.321 | 0.183 |
| | | pack | 0.523 | 0.333 | 0.148 |
| | | both, as v7.4.0 shipped | 0.560 | 0.583 | 0.303 |
| | | **both, now** | 0.549 | 0.631 | **0.315** |
| Juliet C# | 180 | scanners | 0.760 | 0.544 | 0.443 |
| | | model | 0.500 | 0.178 | 0.073 |
| | | pack | 0.477 | 0.172 | 0.061 |
| | | both, as v7.4.0 shipped | 0.710 | 0.489 | 0.371 |
| | | **both, now** | 0.718 | 0.594 | **0.457** |

"As v7.4.0 shipped" is with the prompt's example fixed (b5936fc9, below);
"now" is d28ceac7. The model was asked 1,831 times in 2 hours 7 minutes
(2.2 million prompt tokens, 253,000 written); no request was refused.

Per category, the score of each arm (the scanners on every case, then on the
cases the model read):

| Benchmark | cases | scanners, all | scanners | model | pack | both |
|---|---:|---:|---:|---:|---:|---:|
| cmdi | 251 | 0.03 | 0.00 | 0.00 | 0.00 | 0.00 |
| crypto | 246 | 0.75 | 1.00 | 0.00 | 0.00 | 1.00 |
| hash | 236 | 0.69 | 0.83 | 0.00 | 0.00 | 0.83 |
| ldapi | 59 | 0.36 | 0.67 | 0.00 | 0.00 | 0.67 |
| pathtraver | 268 | 0.22 | 0.50 | -0.17 | -0.17 | 0.50 |
| securecookie | 67 | 1.00 | 1.00 | 0.00 | 0.00 | 1.00 |
| sqli | 504 | 0.35 | 0.00 | -0.17 | -0.17 | 0.00 |
| trustbound | 126 | 0.33 | 0.33 | 0.00 | 0.00 | 0.33 |
| weakrand | 493 | 0.92 | 0.83 | 0.50 | 0.50 | 1.00 |
| xpathi | 35 | 0.43 | 0.67 | 0.00 | 0.00 | 0.67 |
| xss | 455 | 0.34 | 0.50 | 0.33 | 0.17 | 0.33 |

| Juliet | Java cases | Java scanners, all | Java model | Java both | C# cases | C# scanners, all | C# model | C# both |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| CWE-89 SQL | 2,220 | 0.53 | 0.48 | 0.53 | 999 | 0.34 | 0.24 | 0.17 |
| CWE-78 command | 444 | 0.06 | -0.02 | 0.00 | 370 | 0.06 | 0.19 | 0.00 |
| CWE-23 relative path | 444 | 0.12 | 0.37 | 0.03 | 370 | 0.39 | 0.00 | 0.33 |
| CWE-36 absolute path | 444 | 0.12 | 0.50 | 0.10 | 370 | 0.39 | 0.00 | 0.67 |
| CWE-80 XSS | 666 | 0.16 | 0.35 | 0.37 | 666 | 0.38 | 0.00 | 0.67 |
| CWE-81 XSS in an error | 333 | 0.00 | 0.00 | 0.00 | 333 | 0.38 | 0.00 | 0.50 |
| CWE-83 XSS in an attribute | 333 | 0.16 | 0.00 | 0.25 | 333 | 0.38 | 0.00 | 0.33 |
| CWE-90 LDAP | 444 | 0.12 | 0.00 | 0.25 | 370 | 0.34 | 0.00 | 0.50 |
| CWE-643 XPath | 444 | 0.05 | 0.00 | 0.06 | 370 | 0.13 | 0.00 | 0.02 |
| CWE-94 code | | | | | 370 | 0.13 | 0.00 | 0.10 |
| CWE-601 redirect | 333 | 0.16 | 0.52 | 0.68 | 333 | 0.38 | 0.56 | 0.54 |
| CWE-327 broken crypto | 34 | 0.00 | 0.00 | 0.00 | 34 | 1.00 | 0.00 | 1.00 |
| CWE-328 weak hash | 51 | 1.00 | 0.00 | 1.00 | 34 | 1.00 | 0.00 | 1.00 |
| CWE-338 weak random | 34 | 0.00 | 0.00 | 0.00 | 17 | 0.00 | 0.00 | 0.00 |
| CWE-614 cookie not secure | 17 | 1.00 | 0.00 | 1.00 | 17 | 1.00 | 0.00 | 1.00 |

Twelve cases a category: a cell is a handful of flows, and one case moves it
by a tenth or more.

## What it says

- **The model cleared real matches more often than false ones.** As v7.4.0
  shipped, a scanner's match the model read and found no outside data
  reaching was kept apart, and a match next to a model finding that the
  skeptic refuted, or that named another weakness, went with that finding. On
  two of the three suites that put the review below the scanners alone
  (Benchmark 0.561 against 0.576, Juliet C# 0.371 against 0.443). Now such a
  match stays open with the model's doubt and what to check (d28ceac7): the
  review is at or above the scanners on all three, and recall rises most
  (C# 0.489 to 0.594).
- **The rules were too narrow; broadening them was the largest gain.** The
  v7.4.0 rules knew few of the ways Java and C# take outside data (only a
  request, by one spelling of its type) and had no rules for LDAP,
  XPath, session data, cookies, weak hashes or C#'s code from text. With
  sources beyond requests (sockets, URL connections, the console, `main`'s
  arguments, environment), both spellings of a type (imported and fully
  qualified) and the new rules (923a1cf1, 106 rules), the Benchmark's score
  went from 0.054 to 0.498 and Juliet C#'s from 0.067 to 0.291.
- **The prompt's example was copied.** The tracing prompt's answer example
  said `"cwe": "CWE-89"`, and Qwen3-4B answered CWE-89 for paths and XSS
  too, so its findings were scored (and shown) as the wrong weakness. With a
  placeholder, and a traced finding of another kind taking its sink's CWE
  (b5936fc9), the model alone on the Benchmark went from -0.03 to 0.045, and
  both from 0.530 to 0.561.
- **Where the rules are thin, the model finds more.** In Juliet Java the
  model alone beat the scanners on relative and absolute paths (0.37 and 0.50
  against 0.12), redirects (0.52 against 0.16) and XSS (0.35 against 0.16):
  many of Juliet's flows pass the data through other classes, fields and
  `finally` blocks, which opengrep's taint does not follow, and the model
  reads the callers. In C# it did better on commands (0.19 against 0.06).
- **The model reads only where its sinks or the scanners point.** It found
  nothing alone in C#'s paths, XSS, LDAP, XPath and code: its sink patterns
  do not match `new StreamReader(path)` or `resp.Write(...)`, and it has no
  LDAP or XPath sink. On the Benchmark they miss `new java.io.File(`, a
  `Runtime` kept in a variable before `exec`, and `prepareStatement` built
  from strings, so the model alone scored below zero on paths and SQL there.
  With the scanners' matches named in the question (both), it traces those
  too.
- **Commands are matched, not told apart.** Besides the taint rules, a
  command rule flags every command run with a value that is not a literal in
  the call: recall 1.0, but 94% of Juliet's safe flows flagged and 79% of the
  Benchmark's, whose safe flows pass a constant through a variable or a
  branch that cannot be taken. The model did not separate them either.
- **The pack's page made the skeptic believe more.** With the pack, the
  model alone scored a little lower on all three suites (0.045 to 0.030,
  0.183 to 0.148, 0.073 to 0.061). On Juliet Java's paths, the skeptic given
  the CWE-22 page refuted 6 findings in safe flows, against 16 when told only
  the CWE's id, and kept 25 false ones against 14. Not changed: the
  differences are a few cases each, with one 4-billion-parameter model, and
  the pages are still what a finding links to.
- **Found and fixed on the way.** The safeguards' secrets detector took SQL
  built around a password column (`"... PASSWORD='" + pw + "'"`) for a
  password, and the gateway refused the Benchmark's SQL cases (b36a03a8). The
  scanners service closed the connection after a scan sent as a tar, as the
  tar's padding was left unread (76762698). opengrep loses taint across a
  `finally` block: Juliet's flows that read the data inside `try` and use it
  after are missed (an engine limit, not fixed here).

## Caveats

One run of each arm on one machine, one model at 4-bit, 12 cases a category
(132 to 180 cases a suite): a difference of a few cases is within what
another sample would change, and the per-category cells more so. Juliet is
synthetic, each case a few dozen lines with one weakness; the Benchmark is
Java servlets only. Both are public and were likely in the model's training
data; the answers' names and comments are hidden, but the code's shape is
not. The scanners were scored on every case, the model arms on the sample;
the "scanners" rows beside the model arms are the scanners on the same
sample. The review's limits on a repository (three sinks a file, thirty a
review) apply to each case as to a repository; on a real repository the
thirty are spread over many files. Not measured: Fix it on real CVE fixes
(CVEfixes, which needs a yes to download).

## Reproduce

```bash
python3 evals/run_security_review.py --suite owasp --root DIR/BenchmarkJava
python3 evals/run_security_review.py --suite juliet-java --root DIR/Java/src/testcases
python3 evals/run_security_review.py --suite juliet-csharp --root DIR/src/testcases
```

The scanners image (`arena-scanners`) and a running Arena are needed; the
model's answers are kept in `evals/.cache` by question, so a run stopped is
resumed, and a change to the merge alone reruns in minutes.
