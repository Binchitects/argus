# Contributing

## Getting set up

You need Docker; the .NET SDK and Node 22 are optional (`tools/dn` runs the SDK
in a container). [docs/development.md](docs/development.md) has the layout and
every build and test command. The short version:

```bash
tools/dn build LlmService.slnx -c Release
tools/dn test tests/Llm.Tests -c Release
(cd src/web && npm ci && npm run typecheck && npm run lint && npm test)
```

## Making a change

1. **Branch from `main`**, and keep one concern per branch.
2. **Test at the layer the change lives in**, and above it when it crosses one:
   a unit or integration test for the logic, a browser test for what a person
   sees, the deployment's suites (`deploy/scripts/`) for what a deployment does.
   A bug fix comes with the test that would have caught it.
3. **Update the docs in the same change.** A setting, a route, a page or a
   service that exists only in code is not finished.
4. **Commit messages** say what changed and why, in plain words: a subject line,
   a blank line, then the reasoning. Measured numbers beat adjectives.
5. **Open a pull request.** CI must pass: the API and Argus tests, both web apps
   in a browser, the deployment tooling, and every env sample resolving.

## Rules that are not negotiable

- **No secrets in the repository**, ever: not in code, samples, tests,
  fixtures or commit messages. `.env` holds them; samples say how to make each.
- **No Docker socket in any web-facing container.** What recreates containers
  stays a host command.
- **Access is enforced on the server.** A page may hide what a person cannot
  use, but the API refuses it.
- **Warnings are errors.** Fix the cause; suppress only with a comment saying why.

## Reporting a problem

Open an issue with what you did, what you expected, what happened, and the
relevant logs (the Logs page, or `docker compose logs <service>`), with every
secret removed. For a vulnerability, see [SECURITY.md](SECURITY.md) instead.
