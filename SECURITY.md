# Security

## Reporting a vulnerability

Please report it privately through GitHub's vulnerability reporting (the
repository's **Security** tab, **Report a vulnerability**), not in a public
issue. Include what is affected, how to reproduce it, and what an attacker
gains. You will get an answer, and credit if you want it, once a fix is out.

## How the platform is built to be safe

- **One way in.** Traefik is the only service with published ports; everything
  else is reachable only on the Docker network. TLS is not optional.
- **Every request is someone's.** People sign in to the app (passwords, LDAP or
  Active Directory, two-factor); services with their own login use OIDC through
  it, and the rest are behind forward authentication. Machine clients use
  short-lived tokens or a person's own API key.
- **Access is decided on the server.** Groups decide who may use each model and
  tool; the API refuses what a person may not use. Argus answers each developer
  with exactly the repositories their GitLab account can read, enforced in SQL,
  with a read-only service token.
- **Code the model writes runs in a sandbox** with no network, a separate user
  per job, and limits on time, memory, files and processes.
- **Secrets live in `.env` only.** The app never shows a secret back (a secret
  can be replaced, not read), and signing keys are stored encrypted.
- **No web-facing container has the Docker socket.** Changes that recreate
  containers are a host command.
- **Everything that changes something is audited**: sign-ins, people, groups,
  models, settings, the index.

What each of these is and how it is tested is in
[docs/architecture.md](docs/architecture.md),
[docs/authentication.md](docs/authentication.md) and
[docs/testing.md](docs/testing.md).
