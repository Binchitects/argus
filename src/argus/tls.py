"""TLS policy for reaching GitLab.

Argus talks to GitLab over two transports that share no trust configuration at
all: `httpx` for the API, and the `git` binary for clones and fetches. A GitLab
behind a private CA therefore fails in two unrelated-looking places, at two
different times, with two different errors:

  * `httpx` raises CERTIFICATE_VERIFY_FAILED from the FIRST API call, so
    `argus index` dies before it enumerates a single project;
  * `git` prints "server certificate verification failed" only later, when the
    first clone runs, so the API path can look perfectly healthy.

Both read `GitLabConfig.verify` and `GitLabConfig.ca_cert` through this module,
so one line of configuration fixes both. That is the entire reason this module
exists rather than each call site deciding for itself -- a call site that
forgets is a call site that fails in the field.
"""

from __future__ import annotations

import ssl

import httpx

from .config import GitLabConfig


def verify_for(cfg: GitLabConfig) -> bool | ssl.SSLContext:
    """The ``verify=`` argument for an httpx client aimed at ``cfg``.

    Three cases, in the order an operator is likely to need them:

    * nothing configured -- hand httpx ``True`` and let it use certifi, the
      behaviour that was hard-coded before this module existed;
    * a CA bundle -- return a context holding the PUBLIC roots *plus* that
      bundle. Passing the path straight to ``verify=`` would REPLACE the
      default trust store, so a GitLab that redirects to a public host, or any
      other HTTPS call sharing this client, would suddenly fail to verify a
      certificate it used to accept. Loading the private CA on top of the
      defaults cannot lose a root that already worked;
    * verification off -- hand httpx ``False``. This is the case for a GitLab
      with a self-signed certificate and no CA file to point at, which is a
      real deployment and not a mistake to be argued with. It is loud in the
      config (see `Config.load`) and named in every error the credential
      module builds, so it cannot be enabled by accident and then forgotten.
    """
    if not cfg.verify:
        return False
    if not cfg.ca_cert:
        return True
    context = ssl.create_default_context()
    context.load_verify_locations(cafile=cfg.ca_cert)
    return context


def client_for(cfg: GitLabConfig, *, timeout: float) -> httpx.Client:
    """An httpx client that applies ``cfg``'s TLS policy. Caller closes it."""
    return httpx.Client(timeout=timeout, verify=verify_for(cfg))


def git_env(cfg: GitLabConfig, env: dict[str, str]) -> dict[str, str]:
    """Apply the same policy to the environment of a ``git`` subprocess.

    git reads neither Python's configuration nor httpx's, so the clone path has
    to be told separately: ``GIT_SSL_CAINFO`` is git's CA bundle, and
    ``GIT_SSL_NO_VERIFY`` is its escape hatch. Mutates and returns ``env`` so a
    caller can chain it onto the credential variables built elsewhere.
    """
    if not cfg.verify:
        env["GIT_SSL_NO_VERIFY"] = "true"
    elif cfg.ca_cert:
        env["GIT_SSL_CAINFO"] = cfg.ca_cert
    return env
