#!/usr/bin/env python3
"""The knowledge packs renewed: each source fetched again, each pack whose source changed built again, and the week's packs
published.

    tools/packs-weekly.py                     # every pack, this week's version (the day's date, UTC)
    tools/packs-weekly.py --packs sqlite,cpp  # only these
    tools/packs-weekly.py --dry-run           # what is new upstream, nothing built
    tools/packs-weekly.py --publish           # and a GitHub release packs-VERSION with the new packs and the index

For each pack, in turn (one at a time: embedding takes the GPU):

  1. its source fetched again into .packwork/docsrc/NAME (git: a shallow fetch of its branch; sqlite and cppreference:
     the newest archive upstream; security and grc: their scripts; advisories: code-intel's own renewal), each swapped
     in only when whole, so a failed fetch leaves the last good one;
  2. skipped when its source is the one the newest pack was built from (its commit, the archive's SHA-256, the texts'
     provenance), unless --force;
  3. built as packs/NAME-VERSION.kpack by the code-intel image (arena-code-intel), its embeddings by the stack's model
     (nomic-embed-text) on a temporary server on the GPU (--embed-url for one of your own; --cpu for no GPU). The
     embedding cache (packs/.embcache.db) holds every passage embedded before, so only what changed is embedded again;
  4. checked: the same embedding model, and not far fewer pages than the pack before (a broken upstream, a renamed
     folder), else it is kept out and named (--force takes it);
  5. listed in packs/index.json (what `code-intel pack update` and Admin -> Packs -> Update all read) and SHA256SUMS.

--publish makes the GitHub release packs-VERSION (gh, as you): the packs built this week, index.json and SHA256SUMS.
A pack unchanged this week stays where it was published, and the index points there (packs/published.json remembers).
Superseded packs stay in packs/ (an installation may have one loaded); --prune moves those older than the two newest of
each name into packs/superseded/.

Qt's pack is left out of the weekly run: its offline documentation is a download of Qt's installer, taken by hand when a
Qt release comes (--packs qt builds it again from .packwork/docsrc/qt).

--proxy http://HOST:PORT sends every download through an HTTP proxy (git, curl, the advisories' renewal).

Needs: git, curl, tar, docker (the arena-code-intel and llama.cpp server-cuda images), gh for --publish. Standard library only.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WORK = ROOT / ".packwork"
DOCSRC = WORK / "docsrc"
PACKS = ROOT / "packs"
REPO = "Binchitects/argus"
CODE_INTEL_IMAGE = os.environ.get("CODE_INTEL_IMAGE", "arena-code-intel:latest")
ENGINE_IMAGE = os.environ.get("EMBED_IMAGE", "ghcr.io/ggml-org/llama.cpp:server-cuda")
EMBED_MODEL = Path(os.environ.get("MODELS_DIR", str(Path.home() / "llm-models"))) / "embed" / "nomic-embed-text-v1.5.f16.gguf"
NETWORK = "arena-packs"
EMBEDDER = "arena-pack-embed"
# A pack with fewer pages than this share of the one before is kept out: its upstream changed shape.
LEAST_SHARE = 0.8

# name -> how its source is fetched. git: repositories (folder under the work directory, URL, branch); archive: the
# function that finds the newest archive; script: a fetch of its own; renew: code-intel's advisories renewal.
GIT = {
    "python": [(".", "https://github.com/python/cpython", "3.14")],
    "cpp": [(".", "https://github.com/MicrosoftDocs/cpp-docs", "main")],
    "debugger": [(".", "https://github.com/MicrosoftDocs/windows-driver-docs", "staging")],
    "dotnet": [(".", "https://github.com/dotnet/dotnet-api-docs", "main")],
    "react": [(".", "https://github.com/reactjs/react.dev", "main")],
    "scripting": [("PowerShell-Docs", "https://github.com/MicrosoftDocs/PowerShell-Docs.git", "main"),
                  ("windowsserverdocs", "https://github.com/MicrosoftDocs/windowsserverdocs.git", "main"),
                  ("tldr", "https://github.com/tldr-pages/tldr.git", "main")],
}
ARCHIVE = ("sqlite", "cppreference")
SCRIPTED = ("security", "grc")
WEEKLY = ["advisories", "security", "grc", "sqlite", "cppreference", "react", "python", "cpp", "debugger", "scripting", "dotnet"]
MANUAL = ["qt"]


def say(*parts: object) -> None:
    print(*parts, flush=True)


def run(cmd: list[str], **kw) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, check=True, text=True, **kw)


def out(cmd: list[str], **kw) -> str:
    return subprocess.run(cmd, check=True, text=True, capture_output=True, **kw).stdout.strip()


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def get(url: str) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": "arena-packs-weekly"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return r.read()


def download(url: str, dest: Path, tries: int = 10) -> None:
    """A file downloaded whole: a transfer that stalls (30 s under 1 KB/s) is taken up again where it stopped."""
    part = dest.with_name(dest.name + ".part")
    part.unlink(missing_ok=True)
    for attempt in range(1, tries + 1):
        got = subprocess.run(["curl", "-fsSL", "--retry", "3", "--speed-limit", "1024", "--speed-time", "30", "-C", "-",
                              "-o", str(part), url], capture_output=True, text=True)
        # 33: the server ignores ranges and the file is already whole.
        if got.returncode in (0, 33) and part.exists():
            part.rename(dest)
            return
        if got.returncode == 22 and "416" in got.stderr:
            part.rename(dest)
            return
        if attempt == tries:
            raise RuntimeError(f"could not download {url}: {got.stderr.strip() or f'curl exit {got.returncode}'}")
        time.sleep(min(30, 2 * attempt))


# ------------------------------------------------------------------ the packs there are

def meta(path: Path) -> dict[str, str]:
    con = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    try:
        return dict(con.execute("select key, value from pack_meta").fetchall())
    finally:
        con.close()


def version_key(v: str) -> tuple:
    return tuple(int(x) if x.isdigit() else 0 for x in re.split(r"[.-]", v or "0"))


def newest() -> dict[str, tuple[Path, dict[str, str]]]:
    """The newest pack of each name in packs/, by its recorded version (a date sorts after 1.0)."""
    best: dict[str, tuple[Path, dict[str, str]]] = {}
    for f in sorted(PACKS.glob("*.kpack")):
        try:
            m = meta(f)
        except sqlite3.Error as exc:
            say(f"  note: {f.name} is not a readable pack ({exc}): left out")
            continue
        name = m.get("source_name", "")
        if not name:
            continue
        if name not in best or (version_key(m.get("pack_version", "")), f.stat().st_mtime) > \
                (version_key(best[name][1].get("pack_version", "")), best[name][0].stat().st_mtime):
            best[name] = (f, m)
    return best


# ------------------------------------------------------------------ fetching

def fetch_git(name: str, dry: bool) -> str:
    """Each repository of the pack, a shallow fetch of its branch; the provenance is each one's commit."""
    root = DOCSRC / name
    commits = []
    for folder, url, branch in GIT[name]:
        d = (root / folder).resolve()
        if dry:
            head = out(["git", "ls-remote", url, f"refs/heads/{branch}"]).split()
            commits.append((folder, head[0] if head else "?"))
            continue
        if (d / ".git").exists():
            run(["git", "-C", str(d), "fetch", "--quiet", "--depth", "1", "origin", branch])
            run(["git", "-C", str(d), "checkout", "--quiet", "--force", "FETCH_HEAD"])
        else:
            tmp = d.with_name("." + d.name + ".new")
            shutil.rmtree(tmp, ignore_errors=True)
            tmp.parent.mkdir(parents=True, exist_ok=True)
            run(["git", "clone", "--quiet", "--depth", "1", "--branch", branch, url, str(tmp)])
            shutil.rmtree(d, ignore_errors=True)
            tmp.rename(d)
        commits.append((folder, out(["git", "-C", str(d), "rev-parse", "HEAD"])))
    if len(commits) == 1:
        return commits[0][1]
    return ",".join(f"{f}={c[:12]}" for f, c in commits)


def newest_archive(name: str) -> str:
    """The URL of the newest archive upstream."""
    if name == "sqlite":
        page = get("https://www.sqlite.org/download.html").decode("utf-8", "replace")
        found = re.findall(r"(\d{4}/sqlite-doc-\d+\.zip)", page)
        if not found:
            raise RuntimeError("sqlite.org's download page names no documentation archive")
        return "https://www.sqlite.org/" + found[0]
    release = json.loads(get("https://api.github.com/repos/PeterFeicht/cppreference-doc/releases/latest"))
    for asset in release.get("assets", []):
        if re.fullmatch(r"html-book-\d+\.tar\.xz", asset["name"]):
            return asset["browser_download_url"]
    raise RuntimeError(f"cppreference-doc's latest release ({release.get('tag_name')}) has no html-book archive")


def fetch_archive(name: str, dry: bool, current: str) -> str:
    url = newest_archive(name)
    stamp_file = DOCSRC / name / ".archive-url"
    if dry:
        return f"{url} (now {stamp_file.read_text().strip() if stamp_file.exists() else 'unknown'})"
    with tempfile.TemporaryDirectory(dir=WORK) as t:
        archive = Path(t) / url.rsplit("/", 1)[1]
        download(url, archive)
        digest = "sha256:" + sha256(archive)
        if digest == current and (DOCSRC / name).is_dir():
            return digest
        new = DOCSRC / f".{name}.new"
        shutil.rmtree(new, ignore_errors=True)
        new.mkdir(parents=True)
        if archive.suffix == ".zip":
            with zipfile.ZipFile(archive) as z:
                for n in z.namelist():
                    if n.startswith("/") or ".." in Path(n).parts:
                        raise RuntimeError(f"{url}: {n} would land outside its folder")
                z.extractall(new)
        else:
            run(["tar", "-xJf", str(archive), "-C", str(new), "--no-same-owner"])
        (new / ".archive-url").write_text(url + "\n")
        shutil.rmtree(DOCSRC / name, ignore_errors=True)
        new.rename(DOCSRC / name)
    return digest


def fetch_security(dry: bool) -> None:
    if dry:
        return
    run([str(ROOT / "tools" / "fetch-security-sources.sh"), str(DOCSRC / "security")])
    new = DOCSRC / ".security-texts.new"
    shutil.rmtree(new, ignore_errors=True)
    run([sys.executable, "-I", str(ROOT / "tools" / "security-texts.py"), "--from", str(DOCSRC / "security"), "--out", str(new)])
    shutil.rmtree(DOCSRC / "security-texts", ignore_errors=True)
    new.rename(DOCSRC / "security-texts")


def fetch_grc(dry: bool) -> None:
    if dry:
        return
    sys.path.insert(0, str(ROOT / "tools"))
    import importlib.util
    spec = importlib.util.spec_from_file_location("grc_catalogs", ROOT / "tools" / "grc-catalogs.py")
    grc = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(grc)
    src = WORK / "grc-sources"
    src.mkdir(parents=True, exist_ok=True)
    for file, url in grc.SOURCES.items():
        download(url, src / file)
    new = DOCSRC / ".grc.new"
    shutil.rmtree(new, ignore_errors=True)
    run([sys.executable, "-I", str(ROOT / "tools" / "grc-catalogs.py"), "--from", str(src), "--texts", str(new)])
    shutil.rmtree(DOCSRC / "grc", ignore_errors=True)
    new.rename(DOCSRC / "grc")


# ------------------------------------------------------------------ building

class Embedder:
    """The stack's embedding model on a server of the run's own, on the GPU unless --cpu; gone when the run ends."""

    def __init__(self, url: str | None, cpu: bool):
        self.given, self.cpu, self.started = url, cpu, False

    def __enter__(self) -> "Embedder":
        subprocess.run(["docker", "network", "create", NETWORK], capture_output=True)
        if self.given:
            self.url = self.given
            return self
        if not EMBED_MODEL.is_file():
            raise SystemExit(f"packs-weekly: no embedding model at {EMBED_MODEL} (MODELS_DIR)")
        subprocess.run(["docker", "rm", "-f", EMBEDDER], capture_output=True)
        gpu = [] if self.cpu else ["--gpus", "all"]
        layers = "0" if self.cpu else "99"
        run(["docker", "run", "-d", "--name", EMBEDDER, "--network", NETWORK, *gpu, "-v", f"{EMBED_MODEL.parent}:/models:ro",
             "--entrypoint", "/app/llama-server", ENGINE_IMAGE, "-m", f"/models/{EMBED_MODEL.name}", "--alias", "nomic-embed-text",
             "--embeddings", "--pooling", "mean", "-ngl", layers, "-c", "16384", "-b", "2048", "-ub", "2048", "--parallel", "8",
             "--port", "8080", "--host", "0.0.0.0"], capture_output=True)
        self.started = True
        self.url = f"http://{EMBEDDER}:8080"
        for _ in range(120):
            # Asked inside its own container (the code-intel image has no HTTP client of its own).
            ok = subprocess.run(["docker", "exec", EMBEDDER, "curl", "-fsS", "http://127.0.0.1:8080/health"],
                                capture_output=True).returncode == 0
            if ok:
                say(f"  the embedder answers ({'CPU' if self.cpu else 'GPU'})")
                return self
            if subprocess.run(["docker", "inspect", "-f", "{{.State.Running}}", EMBEDDER], capture_output=True, text=True).stdout.strip() != "true":
                log = subprocess.run(["docker", "logs", "--tail", "20", EMBEDDER], capture_output=True, text=True)
                raise SystemExit(f"packs-weekly: the embedder stopped:\n{log.stdout}{log.stderr}")
            time.sleep(2)
        raise SystemExit("packs-weekly: the embedder did not answer within 4 minutes")

    def __exit__(self, *_: object) -> None:
        if self.started:
            subprocess.run(["docker", "rm", "-f", EMBEDDER], capture_output=True)


def code_intel(embed_url: str, mounts: list[tuple[Path, str, str]], *args: str) -> None:
    vols = []
    for host, inside, mode in mounts:
        vols += ["-v", f"{host}:{inside}:{mode}"]
    proxy = [x for k in ("HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY") if os.environ.get(k) for x in ("-e", f"{k}={os.environ[k]}")]
    run(["docker", "run", "--rm", "--network", NETWORK, "--user", f"{os.getuid()}:{os.getgid()}", "-e", "HOME=/tmp", *proxy,
         "-e", f"CODE_INTEL_EMBED_URL={embed_url}", *vols, CODE_INTEL_IMAGE, *args])


def build(name: str, version: str, embed_url: str, commit: str | None) -> Path:
    """packs/NAME-VERSION.kpack from the pack's work directory (the code-intel CLI writes it whole or not at all)."""
    work = {"security": "security-texts"}.get(name, name)
    target = PACKS / f"{name}-{version}.kpack"
    args = ["pack", "build", "--source", name, "--work-dir", "/work", "--out", f"/packs/{target.name}", "--version", version]
    if commit:
        args += ["--commit", commit]
    code_intel(embed_url, [(DOCSRC / work, "/work", "ro"), (PACKS, "/packs", "rw")], *args)
    return target


def renew_advisories(version: str, embed_url: str) -> Path:
    """code-intel's own renewal (what changed in GitHub's advisory database and CISA's KEV), as advisories-VERSION.kpack."""
    stage = WORK / "advisories-stage"
    shutil.rmtree(stage, ignore_errors=True)
    stage.mkdir(parents=True)
    # The shared embedding cache, so only the advisories that changed are embedded again.
    (stage / ".embcache.db").symlink_to("/packs/.embcache.db")
    renew = WORK / "advisories-renew"
    renew.mkdir(parents=True, exist_ok=True)
    code_intel(embed_url, [(stage, "/stage", "rw"), (PACKS, "/packs", "rw"), (renew, "/renew", "rw")],
               "pack", "renew", "--packs-dir", "/stage", "--work-dir", "/renew")
    target = PACKS / f"advisories-{version}.kpack"
    (stage / "advisories.kpack").rename(target)
    shutil.rmtree(stage, ignore_errors=True)
    return target


def check(new: Path, old: tuple[Path, dict[str, str]] | None, force: bool) -> str | None:
    """Why a new pack is not to be published (None: it is)."""
    m = meta(new)
    if old is None or force:
        return None
    before = old[1]
    if m.get("embedding_model") != before.get("embedding_model") or m.get("embedding_dim") != before.get("embedding_dim"):
        return f"its embedding model is {m.get('embedding_model')} ({m.get('embedding_dim')}), the one before {before.get('embedding_model')}"
    docs, docs_before = int(m.get("doc_count", 0)), int(before.get("doc_count", 0))
    # A fixture or a pack of a handful of pages is no measure.
    if docs_before >= 50 and docs < LEAST_SHARE * docs_before:
        return f"{docs} pages against {docs_before} before: its upstream may have changed shape"
    return None


# ------------------------------------------------------------------ the index and the release

def write_index(version: str, built: list[Path], publish: bool) -> tuple[Path, Path]:
    """index.json of the newest pack of each name, each with where it is served from; SHA256SUMS beside it."""
    published_file = PACKS / "published.json"
    published = json.loads(published_file.read_text()) if published_file.exists() else {}
    base = f"https://github.com/{REPO}/releases/download/packs-{version}/"
    entries, sums = [], []
    for name, (path, m) in sorted(newest().items()):
        digest = sha256(path)
        here = path in built or path.name not in published or published[path.name]["sha256"] != digest
        url = base + path.name if here else published[path.name]["url"]
        entries.append({"name": name, "version": m.get("pack_version", ""), "url": url, "sha256": digest,
                        "size_bytes": path.stat().st_size, "license": m.get("license", ""), "attribution": m.get("attribution", ""),
                        "source_commit": m.get("source_commit", "")})
        sums.append(f"{digest}  {path.name}")
        if publish:
            published[path.name] = {"url": url, "sha256": digest, "release": f"packs-{version}" if here else published[path.name]["release"]}
    index = PACKS / "index.json"
    index.write_text(json.dumps({"schema": 1, "version": version, "packs": entries}, indent=2) + "\n")
    sums_file = PACKS / "SHA256SUMS"
    sums_file.write_text("\n".join(sums) + "\n")
    if publish:
        # Written once the release holds them (remember_published), so a failed upload leaves no claim behind.
        (PACKS / ".published.next.json").write_text(json.dumps(published, indent=2, sort_keys=True) + "\n")
    return index, sums_file


def remember_published() -> None:
    nxt = PACKS / ".published.next.json"
    if nxt.exists():
        nxt.replace(PACKS / "published.json")


def release(version: str, notes: list[str], index: Path, sums: Path) -> bool:
    """The release of the week: every pack the index serves from it (built this week, or never released). False: none."""
    entries = json.loads(index.read_text())["packs"]
    base = f"https://github.com/{REPO}/releases/download/packs-{version}/"
    assets = [str(PACKS / e["url"].rsplit("/", 1)[1]) for e in entries if e["url"].startswith(base)]
    if not assets:
        return False
    if not notes:
        notes = [f"- **{e['name']}** {e['version']}: {e['size_bytes'] / 2**20:.0f} MB, from {e['source_commit'][:60]}" for e in entries if e["url"].startswith(base)]
    body = [f"Knowledge packs of {version}: {len(assets)} new, {len(entries) - len(assets)} as before.", ""] + notes + [
        "", "Install on a stack: Admin -> Packs -> Update all (CODE_INTEL_PACK_INDEX_URL set to this release's index.json), "
        "or download a pack and check it against SHA256SUMS, then `code-intel pack install FILE --sha256 DIGEST`."]
    with tempfile.NamedTemporaryFile("w", suffix=".md", delete=False) as f:
        f.write("\n".join(body) + "\n")
    run(["gh", "release", "create", f"packs-{version}", "--repo", REPO, "--title", f"Knowledge packs {version}", "--notes-file", f.name,
         "--latest=false", *assets, str(index), str(sums)])
    os.unlink(f.name)
    return True


def prune() -> None:
    by_name: dict[str, list[tuple[tuple, Path]]] = {}
    for f in PACKS.glob("*.kpack"):
        try:
            m = meta(f)
        except sqlite3.Error:
            continue
        by_name.setdefault(m.get("source_name", ""), []).append((version_key(m.get("pack_version", "")), f))
    old = PACKS / "superseded"
    for name, files in by_name.items():
        for _, f in sorted(files, reverse=True)[2:]:
            old.mkdir(exist_ok=True)
            f.rename(old / f.name)
            say(f"  superseded: {f.name} -> superseded/")


# ------------------------------------------------------------------ the run

def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--packs", help=f"these packs, comma separated (default: {','.join(WEEKLY)}; qt only when named)")
    p.add_argument("--version", default=dt.datetime.now(dt.timezone.utc).strftime("%Y.%m.%d"), help="the version recorded (default: today, UTC)")
    p.add_argument("--force", action="store_true", help="build each named pack even if its source is as before, and publish it whatever the checks say")
    p.add_argument("--dry-run", action="store_true", help="say what is new upstream; fetch, build and publish nothing")
    p.add_argument("--embed-url", help="an embedding server of your own serving nomic-embed-text (default: a temporary one)")
    p.add_argument("--cpu", action="store_true", help="the temporary embedder on the CPU (slow)")
    p.add_argument("--publish", action="store_true", help=f"a GitHub release packs-VERSION on {REPO} (gh, as you)")
    p.add_argument("--until", help="start no pack after this time, HH:MM UTC (a run that must not overlap something else); the rest wait for the next run")
    p.add_argument("--proxy", help="an HTTP proxy for every download (http://HOST:PORT)")
    p.add_argument("--prune", action="store_true", help="move the packs older than the two newest of each name into packs/superseded/")
    a = p.parse_args()
    if a.proxy:
        for k in ("HTTPS_PROXY", "HTTP_PROXY", "https_proxy", "http_proxy"):
            os.environ[k] = a.proxy
        # The embedder is reached on the run's own network, never through the proxy.
        os.environ["NO_PROXY"] = os.environ["no_proxy"] = f"{EMBEDDER},localhost,127.0.0.1"
    names = [n.strip() for n in a.packs.split(",")] if a.packs else WEEKLY
    unknown = [n for n in names if n not in WEEKLY + MANUAL]
    if unknown:
        p.error(f"no pack {', '.join(unknown)}; known: {', '.join(WEEKLY + MANUAL)}")
    if not re.fullmatch(r"\d{4}\.\d{2}\.\d{2}", a.version) and not a.force:
        p.error("--version is a date, YYYY.MM.DD (--force for another)")
    PACKS.mkdir(exist_ok=True)
    DOCSRC.mkdir(parents=True, exist_ok=True)
    have = newest()
    say(f"Knowledge packs {a.version}{' (dry run)' if a.dry_run else ''}: {', '.join(names)}")

    built: list[Path] = []
    notes: list[str] = []
    kept_out: list[str] = []
    failed: list[str] = []
    embedder = None if a.dry_run else Embedder(a.embed_url, a.cpu)
    try:
        if embedder:
            embedder.__enter__()
        stop_at = None
        if a.until:
            hh, mm = (int(x) for x in a.until.split(":"))
            now = dt.datetime.now(dt.timezone.utc)
            stop_at = now.replace(hour=hh, minute=mm, second=0, microsecond=0)
            if stop_at <= now:
                stop_at += dt.timedelta(days=1)
        for name in names:
            if stop_at and dt.datetime.now(dt.timezone.utc) >= stop_at:
                left = names[names.index(name):]
                failed.append(f"not started by {a.until} UTC: {', '.join(left)}")
                say(f"\n{a.until} UTC: {', '.join(left)} left for the next run")
                break
            old = have.get(name)
            current = old[1].get("source_commit", "") if old else ""
            say(f"\n== {name}: now {old[0].name + ' ' + old[1].get('pack_version', '') if old else 'none'}")
            if old and old[1].get("pack_version") == a.version and not a.force:
                say(f"  already built for {a.version}")
                continue
            try:
                commit: str | None = None
                if name in GIT:
                    commit = fetch_git(name, a.dry_run)
                elif name in ARCHIVE:
                    commit = fetch_archive(name, a.dry_run, current)
                elif name == "security":
                    fetch_security(a.dry_run)
                elif name == "grc":
                    fetch_grc(a.dry_run)
                if a.dry_run:
                    say(f"  upstream: {commit or 'fetched when built'}; the pack's: {current or 'none'}")
                    continue
                if commit is not None and commit == current and not a.force:
                    say(f"  unchanged upstream ({commit[:40]}): kept")
                    continue
                say(f"  building from {commit[:40] if commit else 'the fetched texts'} ...")
                target = renew_advisories(a.version, embedder.url) if name == "advisories" else build(name, a.version, embedder.url, commit)
                m = meta(target)
                # Texts whose provenance is as before (security, grc): the pack is the one already there.
                if commit is None and name != "advisories" and m.get("source_commit") == current and not a.force:
                    target.unlink()
                    say(f"  unchanged upstream ({current[:40]}): kept")
                    continue
                if (why := check(target, old, a.force)) is not None:
                    held = target.with_name(target.name + ".held")
                    target.rename(held)
                    kept_out.append(f"{name}: {why} (kept as {held.name})")
                    say(f"  KEPT OUT: {why}")
                    continue
                built.append(target)
                line = (f"- **{name}** {a.version}: {int(m.get('doc_count', 0)):,} pages, {int(m.get('chunk_count', 0)):,} passages, "
                        f"{target.stat().st_size / 2**20:.0f} MB, from {m.get('source_commit', '')[:60]}")
                if old:
                    line += f" (was {old[1].get('pack_version')}, {int(old[1].get('doc_count', 0)):,} pages)"
                notes.append(line)
                say("  " + line[2:])
            except (subprocess.CalledProcessError, RuntimeError, OSError) as exc:
                failed.append(f"{name}: {exc}")
                say(f"  FAILED: {exc}")
    finally:
        if embedder:
            embedder.__exit__()

    if a.dry_run:
        return 0
    index, sums = write_index(a.version, built, a.publish)
    say(f"\n{len(built)} pack(s) built: {', '.join(b.name for b in built) or 'none'}; index {index}")
    for k in kept_out:
        say(f"  kept out: {k}")
    for f in failed:
        say(f"  failed: {f}")
    if a.publish:
        if release(a.version, notes + [f"- kept out: {k}" for k in kept_out], index, sums):
            remember_published()
            say(f"released packs-{a.version}: https://github.com/{REPO}/releases/tag/packs-{a.version}")
        else:
            remember_published()
            say("nothing new to release")
    if a.prune:
        prune()
    return 1 if failed or kept_out else 0


if __name__ == "__main__":
    sys.exit(main())
