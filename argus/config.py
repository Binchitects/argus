from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

import yaml

DEFAULT_EXCLUDE_DIRS = (
    "third_party", "vendor", "node_modules",
    "build", "out", "x64", "Debug", "Release",
)


class ConfigError(Exception):
    """Raised when configuration is missing or malformed."""


#: Spellings accepted for a boolean option. YAML produces real booleans, an
#: environment variable is always a string, and shell users write all of these.
_TRUE = ("1", "true", "yes", "on")
_FALSE = ("0", "false", "no", "off")


def _as_bool(name: str, value: object) -> bool:
    """Parse a YAML or environment boolean, refusing anything ambiguous.

    Raises rather than defaulting, because the option this parses
    (`gitlab.verify`) turns certificate verification off when it is false.
    A typo or an unexpected spelling silently meaning "verify" is a security
    bug; silently meaning "do not verify" is a worse one.
    """
    if isinstance(value, bool):
        return value
    text = str(value).strip().lower()
    if text in _TRUE:
        return True
    if text in _FALSE:
        return False
    raise ConfigError(f"{name} must be a boolean (true or false), not {value!r}")


@dataclass(frozen=True)
class GitLabConfig:
    """How Argus authenticates to GitLab.

    Two modes, because the two credentials are not interchangeable at the
    protocol level:

    * ``token`` -- a personal, project or group access token, sent as
      ``PRIVATE-TOKEN``. The original mode, and still the default.
    * ``password`` -- a username and password exchanged for an OAuth token at
      ``/oauth/token``, then sent as ``Authorization: Bearer``.

    The header differs per mode, so a caller cannot simply swap the value in:
    GitLab rejects an OAuth token presented as ``PRIVATE-TOKEN``. `credential`
    returns both the value and the header that carries it, which is why
    nothing outside this module builds those headers by hand any more.

    Git cloning needs no such distinction. `mirror` answers git's askpass
    prompt with the username ``oauth2`` and the credential as the password,
    which GitLab accepts for access tokens and OAuth tokens alike.
    """

    url: str
    token: str = ""
    #: "token" or "password". Inferred when unset: a username makes it
    #: password mode, so an operator who configures a username and no `auth`
    #: gets what they plainly meant rather than an error.
    auth: str = ""
    username: str = ""
    #: Never read from the config file -- see `Config.load`. Held in memory
    #: for the life of the process because the OAuth token it buys expires,
    #: and a re-exchange is the only way to recover without a restart.
    password: str = ""
    #: Path to a PEM bundle holding the CA that signed GitLab's certificate,
    #: for an instance on a private or internal CA. Public roots stay loaded
    #: alongside it -- see `tls.verify_for`.
    ca_cert: str = ""
    #: Whether to verify GitLab's certificate at all. Leave it alone unless
    #: GitLab serves a self-signed certificate and there is NO CA file to
    #: point at, which is a real situation rather than a misconfiguration.
    #:
    #: Setting it false disables verification for every API call and every
    #: clone to this GitLab, so anything able to answer on that hostname can
    #: read the access token. It exists because the alternative -- the operator
    #: patching Argus -- is worse, not because it is good.
    verify: bool = True

    def __post_init__(self) -> None:
        mode = (self.auth or ("password" if self.username else "token")).lower()
        if mode not in ("token", "password"):
            raise ConfigError(
                f"gitlab.auth must be 'token' or 'password', not {self.auth!r}")
        object.__setattr__(self, "auth", mode)
        if mode == "token" and not self.token:
            raise ConfigError("gitlab.token is required when gitlab.auth is 'token'")
        if mode == "password" and not (self.username and self.password):
            raise ConfigError(
                "gitlab.auth is 'password', so gitlab.username and a password "
                "are both required (set the password in ARGUS_GITLAB_PASSWORD)")

    def redacted(self) -> str:
        """A description safe to log or put in an error.

        Exists so that no caller has to decide, each time, which fields of
        this object are safe to print. Neither the token nor the password
        appears, in any mode.
        """
        if self.auth == "password":
            described = f"{self.url} as {self.username} (password)"
        else:
            described = f"{self.url} (access token)"
        if not self.verify:
            # Named in the description because every credential and
            # transport error carries it. An operator reading "could not
            # reach GitLab ... certificate verification DISABLED" has the
            # cause of a whole class of confusing failures in front of them,
            # rather than having to remember a setting from weeks ago.
            described += ", certificate verification DISABLED"
        return described


@dataclass(frozen=True)
class IndexConfig:
    data_dir: Path
    db_path: Path
    max_file_bytes: int = 1048576
    exclude_dirs: tuple[str, ...] = DEFAULT_EXCLUDE_DIRS
    repo_time_budget_seconds: int = 600
    #: Glob patterns naming the branches to index, e.g. ("main", "v*").
    #:
    #: Empty -- the default -- means each project's default branch and nothing
    #: else, which is what Argus did before it could index more than one. The
    #: project's default branch is ALWAYS indexed whether or not it matches a
    #: pattern: it is what every answer falls back to, and a config whose
    #: patterns happen to miss it would leave the default silently empty.
    #:
    #: Cost is roughly linear in branches matched. A release-per-major-version
    #: layout with v1/v2/v3 alongside main is four times the index.
    branches: tuple[str, ...] = ()

    @property
    def mirrors_dir(self) -> Path:
        return self.data_dir / "mirrors"

    @property
    def trees_dir(self) -> Path:
        return self.data_dir / "trees"


@dataclass(frozen=True)
class PacksConfig:
    """Where installed knowledge packs live.

    Separate from IndexConfig on purpose: packs are the public corpus and
    share nothing with the private index but a disk.
    """

    dir: Path


@dataclass(frozen=True)
class Config:
    gitlab: GitLabConfig
    index: IndexConfig
    #: Optional so a Config built in code (tests, tooling) stays valid without
    #: it; read through `packs_dir`, which supplies the default.
    packs: PacksConfig | None = None

    @property
    def packs_dir(self) -> Path:
        return self.packs.dir if self.packs else self.index.data_dir / "packs"

    @staticmethod
    def load(path: Path) -> "Config":
        raw = yaml.safe_load(Path(path).read_text(encoding="utf-8")) or {}
        gl = raw.get("gitlab") or {}
        ix = raw.get("index") or {}
        pk = raw.get("packs") or {}

        # The environment wins over the file, exactly as it does for the token
        # and username below. This matters more than it looks: the shipped
        # container config names a throwaway test GitLab, so without an
        # override the one setting an operator is told to change lives in a
        # file that `docker compose` cannot rewrite, and ARGUS_GITLAB_URL in
        # .env is silently ignored. That was the actual behaviour.
        url = os.environ.get("ARGUS_GITLAB_URL") or gl.get("url")
        if not url:
            raise ConfigError(
                "gitlab.url is required: set ARGUS_GITLAB_URL in the "
                "environment or gitlab.url in the config file")

        token = os.environ.get("ARGUS_GITLAB_TOKEN") or gl.get("token") or ""
        username = os.environ.get("ARGUS_GITLAB_USERNAME") or gl.get("username") or ""
        auth = (os.environ.get("ARGUS_GITLAB_AUTH") or gl.get("auth") or "").lower()

        # The password is read from the environment ONLY, never from the
        # config file, and there is no `gl.get("password")` fallback on
        # purpose. A token in a config file is bad; a password is worse --
        # it is reusable across every system the person signs in to, and
        # config files get committed, copied into images, and pasted into
        # issues. A key named in the file is refused outright rather than
        # ignored, because silently disregarding a password someone believed
        # they had configured would leave them authenticating as nobody and
        # wondering why.
        if "password" in gl:
            raise ConfigError(
                "gitlab.password must not appear in the config file. Set "
                "ARGUS_GITLAB_PASSWORD in the environment instead."
            )
        password = os.environ.get("ARGUS_GITLAB_PASSWORD") or ""

        # TLS, for a GitLab that is not on a public CA. `ca_cert` wins over
        # `verify` and the two together are refused rather than silently
        # resolved: whichever one an operator meant, guessing wrong either
        # breaks the connection or turns verification off when they had
        # supplied everything needed to keep it on.
        ca_cert = str(
            os.environ.get("ARGUS_GITLAB_CA_CERT") or gl.get("ca_cert") or ""
        ).strip()
        # An EMPTY environment variable means "not set", not "the empty
        # string". Every shipped .env carries `ARGUS_GITLAB_VERIFY=` on purpose
        # -- that is how a sample documents a setting it leaves at its default
        # -- and reading it as a value crash-looped Argus on the very first
        # start with "gitlab.verify must be a boolean, not ''".
        #
        # Blankness is tested explicitly rather than with `or`, because `or`
        # would also discard a deliberate "0" and turn verification back ON
        # under an operator who had turned it off.
        verify_env = (os.environ.get("ARGUS_GITLAB_VERIFY") or "").strip()
        verify = _as_bool("gitlab.verify",
                          verify_env or gl.get("verify", True))
        if ca_cert and not verify:
            raise ConfigError(
                "gitlab.ca_cert and gitlab.verify=false are both set, and they "
                "contradict each other: a CA bundle is exactly what makes "
                "verification possible. Keep gitlab.ca_cert, or drop it and "
                "leave gitlab.verify=false."
            )
        if ca_cert and not Path(ca_cert).is_file():
            # Caught here, at load, because the alternative is a
            # CERTIFICATE_VERIFY_FAILED at the first API call that reads
            # exactly like a wrong token or a wrong URL.
            raise ConfigError(
                f"gitlab.ca_cert is {ca_cert!r}, which is not a file. Point it "
                f"at a PEM bundle containing the CA that signed GitLab's "
                f"certificate, or set gitlab.verify=false if no such file "
                f"exists."
            )

        if not (token or username or password):
            raise ConfigError(
                "no GitLab credential: set gitlab.token (or ARGUS_GITLAB_TOKEN), "
                "or gitlab.username with ARGUS_GITLAB_PASSWORD"
            )

        for key in ("data_dir", "db_path"):
            if not ix.get(key):
                raise ConfigError(f"index.{key} is required")

        try:
            max_file_bytes = int(ix.get("max_file_bytes", 1048576))
            repo_time_budget_seconds = int(ix.get("repo_time_budget_seconds", 600))
        except (TypeError, ValueError) as exc:
            raise ConfigError(f"index config value is not an integer: {exc}") from exc

        return Config(
            gitlab=GitLabConfig(url=url.rstrip("/"), token=token, auth=auth,
                                username=username, password=password,
                                ca_cert=ca_cert, verify=verify),
            index=IndexConfig(
                data_dir=Path(ix["data_dir"]),
                db_path=Path(ix["db_path"]),
                max_file_bytes=max_file_bytes,
                exclude_dirs=tuple(ix.get("exclude_dirs", DEFAULT_EXCLUDE_DIRS)),
                repo_time_budget_seconds=repo_time_budget_seconds,
                branches=tuple(ix.get("branches", ()) or ()),
            ),
            packs=PacksConfig(
                dir=Path(pk["dir"]) if pk.get("dir")
                else Path(ix["data_dir"]) / "packs"
            ),
        )
