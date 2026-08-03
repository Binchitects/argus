"""Install, list and remove knowledge packs.

There is no registry file. A pack is already self-describing, so the installed
files *are* the registry: "registered" and "present and readable" are the same
statement, and there is no index that can drift out of step with the directory
it describes.

Installation is verify-then-place. A pack is streamed to a temporary file in
the destination directory, checked, and only then renamed into position, so a
failed install leaves nothing behind -- a truncated download that quietly
became a half-empty knowledge base is the exact "looks like it works" failure
this format is built to avoid.
"""

from __future__ import annotations

import hashlib
import json
import re
import uuid
from dataclasses import dataclass
from pathlib import Path

import httpx

from .. import embed as embed_module
from . import format as pack_format

PACK_SUFFIX = ".arguspack"

#: Pack names become filenames, and a pack's name comes from pack_meta inside
#: a file that may have been downloaded from anyone. Without this an installed
#: pack could name itself "../../.ssh/authorized_keys" and be written straight
#: out of the destination directory.
_NAME_RE = re.compile(r"^[a-z0-9][a-z0-9._-]{0,63}$")

_DOWNLOAD_CHUNK = 1 << 20
_INDEX_TIMEOUT = 30.0
_DOWNLOAD_TIMEOUT = 300.0


class RegistryError(Exception):
    """A pack could not be installed, listed or removed."""


@dataclass(frozen=True)
class InstalledPack:
    name: str
    version: str
    path: Path
    embedding_model: str
    embedding_dim: str
    size_bytes: int
    license: str
    attribution: str
    source_commit: str
    #: False when the pack's embedding space differs from this instance's.
    #: Lexical and symbol search still work; only semantic search is affected.
    compatible: bool
    incompatible_reason: str = ""


@dataclass(frozen=True)
class IndexEntry:
    name: str
    version: str
    url: str
    sha256: str
    size_bytes: int
    license: str


def install(
    url_or_path: str | Path, *, dest_dir: Path, expected_sha256: str | None = None,
    client: httpx.Client | None = None,
) -> InstalledPack:
    """Fetch or copy a pack into ``dest_dir`` after verifying it."""
    dest_dir = Path(dest_dir)
    dest_dir.mkdir(parents=True, exist_ok=True)

    # Staged inside dest_dir so the final move is a rename, not a copy across
    # filesystems -- a copy could be observed half-done.
    staging = dest_dir / f".incoming-{uuid.uuid4().hex}.tmp"

    try:
        digest = _stage(url_or_path, staging, client=client)

        if expected_sha256 and digest.lower() != expected_sha256.strip().lower():
            raise RegistryError(
                f"checksum mismatch for {url_or_path}: expected "
                f"{expected_sha256.strip().lower()}, got {digest}"
            )

        meta = _read_pack_meta(staging)
        name = _require_name(meta.get("source_name", ""))
        compatible, reason = _compatibility(meta)

        final = dest_dir / f"{name}{PACK_SUFFIX}"
        staging.replace(final)
    except BaseException:
        staging.unlink(missing_ok=True)
        raise

    return InstalledPack(
        name=name,
        version=meta.get("pack_version", ""),
        path=final,
        embedding_model=meta.get("embedding_model", ""),
        embedding_dim=meta.get("embedding_dim", ""),
        size_bytes=final.stat().st_size,
        license=meta.get("license", ""),
        attribution=meta.get("attribution", ""),
        source_commit=meta.get("source_commit", ""),
        compatible=compatible,
        incompatible_reason=reason,
    )


def list_installed(dest_dir: Path) -> list[InstalledPack]:
    """Every pack in ``dest_dir``, including any that cannot be read.

    An unreadable file is reported as incompatible rather than skipped. Silently
    omitting it would present a knowledge base that is quietly missing a source,
    which is the failure this module exists to prevent.
    """
    dest_dir = Path(dest_dir)
    if not dest_dir.is_dir():
        return []

    packs = []
    for path in sorted(dest_dir.glob(f"*{PACK_SUFFIX}")):
        try:
            meta = _read_pack_meta(path)
        except RegistryError as exc:
            packs.append(InstalledPack(
                name=path.stem, version="", path=path,
                embedding_model="", embedding_dim="",
                size_bytes=path.stat().st_size, license="", attribution="",
                source_commit="", compatible=False,
                incompatible_reason=f"unreadable: {exc}",
            ))
            continue
        compatible, reason = _compatibility(meta)
        packs.append(InstalledPack(
            name=meta.get("source_name", path.stem),
            version=meta.get("pack_version", ""),
            path=path,
            embedding_model=meta.get("embedding_model", ""),
            embedding_dim=meta.get("embedding_dim", ""),
            size_bytes=path.stat().st_size,
            license=meta.get("license", ""),
            attribution=meta.get("attribution", ""),
            source_commit=meta.get("source_commit", ""),
            compatible=compatible,
            incompatible_reason=reason,
        ))
    return packs


def remove(name: str, dest_dir: Path) -> bool:
    """Delete an installed pack. True if one was removed, False if absent."""
    path = Path(dest_dir) / f"{_require_name(name)}{PACK_SUFFIX}"
    if not path.is_file():
        return False
    path.unlink()
    return True


def fetch_index(url: str, *, client: httpx.Client | None = None) -> list[IndexEntry]:
    """Read a published index of available packs."""
    owns_client = client is None
    client = client or httpx.Client(timeout=_INDEX_TIMEOUT)
    try:
        response = client.get(url)
    except httpx.HTTPError as exc:
        raise RegistryError(f"GET {url} failed: {exc}") from exc
    finally:
        if owns_client:
            client.close()

    if response.status_code != 200:
        raise RegistryError(
            f"GET {url} returned {response.status_code}: {response.text[:200]}"
        )
    try:
        body = response.json()
    except json.JSONDecodeError as exc:
        raise RegistryError(
            f"GET {url}: index is not JSON: {response.text[:200]}"
        ) from exc

    raw = body.get("packs") if isinstance(body, dict) else None
    if not isinstance(raw, list):
        raise RegistryError(f"GET {url}: index has no 'packs' list")

    entries = []
    for item in raw:
        if not isinstance(item, dict):
            raise RegistryError(f"GET {url}: index entry is not an object: {item!r}")
        missing = [k for k in ("name", "version", "url", "sha256") if not item.get(k)]
        if missing:
            # A published entry without a checksum cannot be verified on
            # download, so it is a malformed index rather than an optional
            # field.
            raise RegistryError(
                f"GET {url}: index entry {item.get('name', '?')!r} is missing "
                f"{', '.join(missing)}"
            )
        entries.append(IndexEntry(
            name=str(item["name"]),
            version=str(item["version"]),
            url=str(item["url"]),
            sha256=str(item["sha256"]),
            size_bytes=int(item.get("size_bytes") or 0),
            license=str(item.get("license", "")),
        ))
    return entries


def _stage(
    url_or_path: str | Path, staging: Path, *, client: httpx.Client | None,
) -> str:
    """Write the pack to ``staging``; return its hex SHA-256."""
    text = str(url_or_path)
    if text.startswith(("http://", "https://")):
        return _download(text, staging, client=client)

    source = Path(url_or_path)
    if not source.is_file():
        raise RegistryError(f"no such pack file: {source}")
    with source.open("rb") as src, staging.open("wb") as dst:
        return _copy_hashing(src, dst)


def _download(url: str, staging: Path, *, client: httpx.Client | None) -> str:
    owns_client = client is None
    client = client or httpx.Client(timeout=_DOWNLOAD_TIMEOUT, follow_redirects=True)
    try:
        with client.stream("GET", url) as response:
            if response.status_code != 200:
                raise RegistryError(f"GET {url} returned {response.status_code}")
            digest = hashlib.sha256()
            with staging.open("wb") as handle:
                for chunk in response.iter_bytes(_DOWNLOAD_CHUNK):
                    digest.update(chunk)
                    handle.write(chunk)
            return digest.hexdigest()
    except httpx.HTTPError as exc:
        raise RegistryError(f"GET {url} failed: {exc}") from exc
    finally:
        if owns_client:
            client.close()


def _copy_hashing(src, dst) -> str:
    digest = hashlib.sha256()
    while True:
        chunk = src.read(_DOWNLOAD_CHUNK)
        if not chunk:
            return digest.hexdigest()
        digest.update(chunk)
        dst.write(chunk)


def _read_pack_meta(path: Path) -> dict[str, str]:
    try:
        conn = pack_format.open_pack(path)
    except Exception as exc:
        raise RegistryError(f"{path.name} is not a readable pack: {exc}") from exc
    try:
        meta = pack_format.read_meta(conn)
    except Exception as exc:
        raise RegistryError(f"{path.name} has no readable pack_meta: {exc}") from exc
    finally:
        conn.close()
    if not meta:
        raise RegistryError(f"{path.name} has an empty pack_meta")
    return meta


def _require_name(name: str) -> str:
    if not _NAME_RE.match(name or ""):
        raise RegistryError(
            f"invalid pack name {name!r}: names must match {_NAME_RE.pattern} "
            f"(a pack names its own file, so this is what keeps a downloaded "
            f"pack inside the directory it was installed into)"
        )
    return name


def _compatibility(meta: dict[str, str]) -> tuple[bool, str]:
    try:
        pack_format.require_compatible(
            meta, model=embed_module.EMBED_MODEL, dim=embed_module.EMBED_DIM
        )
    except pack_format.PackMismatch as exc:
        return False, str(exc)
    return True, ""
