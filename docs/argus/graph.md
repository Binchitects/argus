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

## Asking the graph

`repo_map`, `system_map`, `dependency_path` and `change_impact` take three
optional arguments:

- `min_confidence`: the least confidence a pair must have to be walked; 0.5
  by default (likely and strong), 0.85 for strong links only, 0 for all.
- `layers`: which layers to walk, as a list of `build`, `ci`, `deploy`,
  `runtime`, `declared`, `history`, `test` (the same as `include_tests`) or
  `all`. Every layer but history by default.
- `include_tests`: walk links that only tests make, too.

What each answers:

- `repo_map` gives each linked repository its confidence (the product of the
  links on its way), its tier and layers, and each kind with its names,
  files, how its name matched and up to three uses. `possible` lists what is
  not walked: candidates (with the readable repositories that provide the
  name) and weak links.
- `system_map` finds hubs, layers and cycles from build links only, unless
  `layers` is given: a CI template that every pipeline includes is not a
  foundation of the code. Hubs are ranked by how many repositories build on
  them, then by the sum of those links' confidence. `hidden_links` counts the
  pairs that the filter leaves out.
- `dependency_path` returns the surest chains, not the shortest: it ranks a
  chain by the sum of −ln(confidence) of its steps, plus 0.05 a step, and
  returns up to three (Yen's algorithm). Each chain has its confidence (the
  product of its steps) and its weakest step. When nothing at the asked
  confidence joins the two, weaker chains are returned with `weak: true`.
- `change_impact` lists dependents by depth, each reached through its surest
  parent, surest first, and `possible_dependents` from candidates.

Links are made between default branches. A branch's row (`path@branch`) is
answered by its default branch's, and the result's `note` says so.
For a plain-language description, `which_repo` leans away from a repository
that many readable repositories build on (counted from likely and strong
build links of their own code): a new feature rarely belongs in a shared
library.
