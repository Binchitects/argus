# How Argus links repositories

Argus links two repositories when one uses what the other provides. It reads
this from the files it already indexes, with no build run (see
[overview.md](overview.md#across-repositories) for what each ecosystem
declares). This page says how sure each link is and what a query walks.

## Kinds and layers

A link's kind comes from what the file declared and how. Each kind sits in a
layer:

| Layer | Kinds | What it means |
|---|---|---|
| build | `package:nuget`, `package:npm`, `package:pypi`, `package:cargo`, `package:maven`, `package:git`, `repository` (a submodule), `import:csharp`, `import:java`, `import:python`, `import:go`, `import:proto`, `include`, `image:base` (a `FROM`) | what it is built from |
| ci | `ci:include` (a CI include, trigger, need or component), `image:ci` (a job's `image:`) | what its pipeline runs |
| deploy | `image:deploy` (an image in a chart, a manifest, a compose file) | what runs it |
| runtime | `runtime:*` | what it talks to while it runs |
| declared | `declared` | what its owners say it depends on |
| history | `cochange` | what changes with it; never walked by default |

## How sure a link is

Each kind has a prior, by how its name matched:

| Kind | Prior |
|---|---|
| a package reference, a CI include | 0.97 |
| a `git+…` requirement, a submodule, a `.proto` imported by its path | 0.95 |
| a Go import (by its module) | 0.90 |
| an image by its project's path | 0.90 (a sub-image 0.85, a private registry's bare name 0.60) |
| a C#, Java or Python import of a name provided exactly; an `#include` | 0.85 |
| a C# type's namespace, a Kotlin function's package, a Python module in a package | 0.75 |
| a `.proto` found by the end of its path | 0.70 |
| a bare top-level Python module | 0.60 |
| a declared dependency | 0.99 |
| a co-change | 0.45 |

A code link that rests on one file is less sure: its prior × 0.85. The priors
are starting values, to be calibrated against links that owners confirm.

A link is **strong** at 0.85 or more, **likely** at 0.5 or more, and **weak**
below. A pair of repositories is linked per layer by the noisy-OR of its
kinds, 1 − Π(1 − c), taking the best link of each kind (names of one kind are
not independent votes). A package reference (0.97) and the imports it brings
(0.85) make 0.9955.

## A name several repositories provide

Two repositories can both declare `namespace Acme.Shared`. A use of it is
settled by what else the user has, in this order:

1. A package reference or submodule of one of them (the prior × 0.95).
2. The same file's other uses, each provided by one repository (× 0.80).
3. The user's other links, likely or strong (× 0.70).
4. The same name, settled in another of the user's files.

Otherwise it is a **candidate** of each provider, at the prior divided by
how many provide it. A candidate is kept with its evidence and listed, never
walked.

## Scope and origin

A use in a test (a file under `test/`, `tests/`, `spec/`, `e2e/` or a
`*.Tests` project, a `*Tests.cs`, `*Test.java`, `*.test.ts`, `*_test.go` or
`test_*.py`, a dev or test dependency) makes a link of scope `test`. Queries walk the product's own links (`main`) unless
asked for tests too. What a test, a fixture, a sample, a template, generated
code or a vendored copy declares provides nothing.

## Evidence

Each link keeps:

- `why`: one line, such as "Acme.Shared is provided by 2 repositories; a
  package or submodule of this one settles it".
- `uses`: up to three places that use it (path and line).
- `provider`: where the other repository declares the name.
- `candidates`: the repositories that provide the name, when it is unsure.

## Tables

| Table | Holds |
|---|---|
| `repo_links` | each way one repository uses another: kind, name, layer, scope, how its name matched, how many provide it, confidence, tier, files, evidence |
| `repo_edges` | each pair per layer: scope, confidence (the noisy-OR), tier, files, kinds |
| `file_links` | which file makes each code or package link, by the name it uses, so a symbol is searched only where it can be used |

A query walks `repo_edges` of scope `main`, every layer but history, at 0.5
or more.
