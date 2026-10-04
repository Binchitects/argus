# The API in CI

A review of each merge request, and an explanation of each failed pipeline,
from jobs in your own GitLab pipeline. For teams that prefer the job in their
pipeline to a task the app runs on GitLab's events
([Scheduled tasks](chat.md#scheduled-tasks)). Both call the gateway with an API
key, like any other tool.

Two files in the repository:

- `clients/arena/arena`: a small client of the gateway. One Python 3 file
  (3.8 or later) with nothing to install: `arena ask`, `arena review` and
  `arena explain-failure`.
- `clients/gitlab-ci/arena-review.yml`: a template to include. Its
  `arena-review` job reviews merge requests; its `arena-explain` job runs when
  a job of the pipeline failed.

**Connect your tools → GitLab CI** in the app has the same setup, with this
deployment's address and model filled in.

## Setup

1. **An API key.** A person's own, from **Connect your tools**, works; it
   spends from their credit, and stops working when they make a new one. For a
   team's pipeline, an account made for it (**Admin → People → Add**, then
   its new API key) keeps the spend on its own line, with its own credit and
   the models it may use.
2. **A GitLab token** that reads the merge request and the jobs' logs and
   posts the comment: a project access token (the project's **Settings →
   Access tokens**) with the Reporter role and the `api` scope. The comment
   comes from the token's bot account. This is the project's own token, never
   Argus's read-only one.
3. **The CI/CD variables**, in the project's (or its group's) **Settings →
   CI/CD → Variables**. Mark the key and the token **Masked**.

   | variable | what |
   |---|---|
   | `ARENA_URL` | the gateway, `https://gateway.DOMAIN` |
   | `ARENA_KEY` | the API key |
   | `ARENA_GITLAB_TOKEN` | the GitLab token |
   | `ARENA_MODEL` | the model; without it, the first chat model the key may use |
   | `ARENA_CA_CERT` | the deployment's CA, when its certificate is its own (`certs/ca.crt` from `scripts/make-cert.sh`): a variable of type **File** |
   | `ARENA_REVIEW_PROMPT` or `ARENA_REVIEW_PROMPT_FILE` | the review's instructions: the text, or a file in the repository |
   | `ARENA_EXPLAIN_PROMPT` or `ARENA_EXPLAIN_PROMPT_FILE` | the same for the explanation |
   | `ARENA_EXPLAIN_POST` | `true`: the explanation is posted on the merge request too |
   | `ARENA_MAX_CHARS` | how much of a diff or of the logs is sent: 40000 characters |
   | `ARENA_TIMEOUT` | seconds to wait for the next part of an answer: 600 (a model that is not loaded loads first) |

4. **The CLI where the jobs find it**, one of:
   - a copy of `clients/arena/arena` in your repository, named by `ARENA_CLI`
     (`ci/arena` below). Nothing is fetched; update the copy now and then.
   - the Argus Arena repository in your GitLab: `ARENA_PROJECT` (its path)
     and `ARENA_REF` (a branch or tag, `main` by default). The jobs read the
     file through GitLab's API with `ARENA_GITLAB_TOKEN`, so that token must
     be able to read that repository: a project access token is a member of
     its own project only, a group access token of every project in its group.
   - an image with `arena` on its `PATH`, or `ARENA_CLI` as an `https` address
     that serves the file. The GitLab token is sent only to this GitLab.
5. **The include**, in `.gitlab-ci.yml`:

   ```yaml
   include:
     - project: platform/argus-arena     # the Argus Arena repository in your GitLab
       ref: main                         # or the release tag you run
       file: clients/gitlab-ci/arena-review.yml

   variables:
     ARENA_CLI: ci/arena
   ```

   Or by its address, when GitLab can reach it:
   `include: remote: https://.../clients/gitlab-ci/arena-review.yml`.

The next merge request gets a comment with the review, ending with the model
and the commit it saw.

## The jobs

- **`arena-review`** runs in merge request pipelines, in the `test` stage,
  without waiting for other jobs (`needs: []`). It reads the merge request's
  title, description and changes, asks for a review, and posts it as one
  comment. A newer push cancels a review still running (`interruptible`, with
  GitLab's auto-cancel of redundant pipelines, on by default). It may
  fail without failing the pipeline (`allow_failure`): a gateway that is down
  never blocks a merge.
- **`arena-explain`** runs in the `.post` stage when a job failed
  (`when: on_failure`). It reads the end of each failed job's log (up to five
  jobs), and writes what failed, why, and the fix into its own log; with
  `ARENA_EXPLAIN_POST: "true"`, also on the merge request (in a branch
  pipeline, the one open from the branch).

A project whose jobs run in branch pipelines gets the review in a merge request
pipeline of its own, beside them. The image is `python:3.13-slim`, but any
image with `python3` and a POSIX shell works. To use a mirror or your own,
override it once; to put the review in another stage (a pipeline without
`test`), override the job:

```yaml
.arena:
  image: registry.example.com/python:3.13-slim
arena-review:
  stage: check
```

## Examples

Your team's review rules, kept in the repository:

```yaml
variables:
  ARENA_CLI: ci/arena
  ARENA_REVIEW_PROMPT_FILE: .gitlab/review.md
```

A job of your own, with the same CLI (the variables above set):

```yaml
readme-check:
  image: python:3.13-slim
  script:
    - python3 ci/arena ask "Which steps of this README would a newcomer get stuck on?" < README.md
```

From a terminal:

```bash
export ARENA_URL=https://gateway.DOMAIN ARENA_KEY="$LLM_SERVICE_API_KEY"
export ARENA_CA_CERT=/path/to/ca.crt            # with a certificate of the deployment's own
git diff main | python3 clients/arena/arena ask "Review this change"
python3 clients/arena/arena explain-failure build.log
CI_API_V4_URL=https://gitlab.example.com/api/v4 ARENA_GITLAB_TOKEN=... \
  python3 clients/arena/arena review --project group/app --mr 42 --dry-run
```

## The CLI

| command | what it does |
|---|---|
| `arena ask "question"` | answers as it is written; text piped in is added to the question |
| `arena review` | reviews the merge request and posts one comment; `--dry-run` prints it only; `--project` and `--mr` outside a pipeline |
| `arena explain-failure [LOG ...]` | explains a failure from log files (`-` is stdin), or without them from the pipeline's failed jobs; `--post` comments on the merge request |

Each takes `--model` and `--max-chars`; `review` and `explain-failure` take
`--prompt` and `--prompt-file`. `arena --help` lists every variable.

What is sent is cut to `ARENA_MAX_CHARS`. A diff keeps whole files while they
fit, generated files (such as lock files) last, then part of the next file;
the model is told which files it did not see, and the comment says so. A log
keeps its end, without colours and GitLab's section markers.

Exit codes:

| code | meaning |
|---|---|
| 0 | done; also nothing to review, or no failed job |
| 1 | an unexpected error |
| 2 | wrong arguments, or nothing to ask |
| 3 | configuration missing or unreadable (a variable, a CA file, a prompt file) |
| 4 | the gateway failed or refused: its address, its certificate, the key, the model, the credit |
| 5 | GitLab failed or refused: reading the merge request or the jobs, posting the comment |
| 130 | interrupted |

The answer streams to stdout as it is written; what the CLI does and why it
stopped go to stderr. Neither the key nor the token is ever printed.

`arena ask` reads its stdin when it is not a terminal. In a CI job, where the
runner may hand the job's script to the shell on stdin, give it the text
through a pipe, as above, or `< /dev/null`.

## When something is wrong

| the job says | why |
|---|---|
| `ARENA_URL answered with a web page, not the API` | `ARENA_URL` is the app's address: use `https://gateway.DOMAIN` |
| `The gateway's certificate is not trusted` | the deployment's certificate is its own: set `ARENA_CA_CERT` |
| `The gateway refused the key (401)` | a wrong or replaced key, or a model the key may not use |
| `Budget has been exceeded` | the key's credit is used up (**Admin → People**) |
| `GitLab says ARENA_GITLAB_TOKEN may not do this (403)` | the token needs the `api` scope and Reporter |
| `set ARENA_PROJECT ... or ARENA_CLI` | the jobs do not know where the CLI is (step 4) |

## How it is tested

`python3 -m unittest discover clients/arena` (in CI on every push): the CLI
against a fake gateway and a fake GitLab on a local socket, a gateway behind a
certificate from a CA of its own, and the template's jobs run by `sh` as a
runner runs them (with PyYAML; skipped without it). The template has not yet
run in a real GitLab's CI.
