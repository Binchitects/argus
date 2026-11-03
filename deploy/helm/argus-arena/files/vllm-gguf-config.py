"""
What vLLM and SGLang read beside a model that a GGUF file has in its own header: the model's Hugging Face config and
tokenizer (transformers reads them from the file) and its generation config (the tokens that end an answer, and the
sampling its makers recommend). The launchers (vllm/serve.py, sglang/serve.py) write them into a folder of the file's own
under VLLM_GGUF_DIR (/tmp/vllm-gguf), once, and name it to the server.

vLLM's GGUF plugin needs them: it takes the config and tokenizer from the folder beside the file, which in the library has
none. SGLang reads a GGUF's config itself, but its tokenizer only for some architectures (a llama one fails as it looks
for tokenizer_config.json on the Hub).
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
import struct
import tempfile

ROOT = "/tmp/vllm-gguf"

# GGUF's value types: their struct formats (8 is a string, 9 an array).
_SCALARS = {0: "<B", 1: "<b", 2: "<H", 3: "<h", 4: "<I", 5: "<i", 6: "<f", 7: "<?", 10: "<Q", 11: "<q", 12: "<d"}


def named(argv: list[str], flags: tuple[str, ...]) -> bool:
    """Whether the arguments set one of these flags already (--flag VALUE or --flag=VALUE)."""
    return any(a == f or a.startswith(f + "=") for a in argv for f in flags)


def split_part(path: str) -> bool:
    """Whether a GGUF file is one part of a model split in several (NAME-00001-of-00003.gguf)."""
    stem = os.path.basename(path)[: -len(".gguf")]
    parts = stem.rsplit("-", 3)
    return len(parts) == 4 and parts[2] == "of" and parts[1].isdigit() and parts[3].isdigit()


def architecture(path: str) -> str | None:
    """A GGUF file's general.architecture, read from its header alone (no package needed); None when it has none or is no GGUF."""

    def text(f) -> str:
        (n,) = struct.unpack("<Q", f.read(8))
        return f.read(n).decode("utf-8", "replace")

    def skip(f, kind: int) -> None:
        if kind == 8:
            text(f)
        elif kind == 9:
            (inner,) = struct.unpack("<I", f.read(4))
            (n,) = struct.unpack("<Q", f.read(8))
            if inner in _SCALARS:
                f.seek(n * struct.calcsize(_SCALARS[inner]), 1)
            else:
                for _ in range(n):
                    skip(f, inner)
        else:
            f.seek(struct.calcsize(_SCALARS[kind]), 1)

    try:
        with open(path, "rb") as f:
            if f.read(4) != b"GGUF":
                return None
            _, _, count = struct.unpack("<IQQ", f.read(20))
            for _ in range(count):
                key = text(f)
                (kind,) = struct.unpack("<I", f.read(4))
                if key == "general.architecture" and kind == 8:
                    return text(f)
                skip(f, kind)
    except (OSError, KeyError, struct.error):
        return None
    return None


def folder_for(path: str, root: str) -> str:
    """The folder of a file's config and tokenizer: by its path, size and time, so a file changed in place gets a new one."""
    st = os.stat(path)
    key = hashlib.sha256(f"{os.path.realpath(path)}|{st.st_size}|{st.st_mtime_ns}".encode()).hexdigest()[:16]
    return os.path.join(root, key)


def generation(fields: dict) -> dict:
    """The generation config from a GGUF's metadata: every token that ends an answer (llama.cpp's end of text, of turn and of
    message), and the sampling its makers recommend (general.sampling.*)."""
    out: dict = {}
    stops: list[int] = []
    for key in ("tokenizer.ggml.eos_token_id", "tokenizer.ggml.eot_token_id", "tokenizer.ggml.eom_token_id"):
        if fields.get(key) is not None and int(fields[key]) not in stops:
            stops.append(int(fields[key]))
    if stops:
        out["eos_token_id"] = stops if len(stops) > 1 else stops[0]
    for key, name in (("tokenizer.ggml.bos_token_id", "bos_token_id"), ("tokenizer.ggml.padding_token_id", "pad_token_id")):
        if fields.get(key) is not None:
            out[name] = int(fields[key])
    for key, name in (("general.sampling.temp", "temperature"), ("general.sampling.top_p", "top_p"), ("general.sampling.min_p", "min_p")):
        if fields.get(key) is not None:
            out[name] = round(float(fields[key]), 6)
    if fields.get("general.sampling.top_k") is not None:
        out["top_k"] = int(fields["general.sampling.top_k"])
    return out


def write(path: str, out: str) -> None:
    """The config, tokenizer and generation config of a GGUF file, from its header, into the folder <out> (renamed into place)."""
    from gguf import GGUFReader
    from transformers import AutoConfig, AutoTokenizer

    folder, name = os.path.split(path)
    config = AutoConfig.from_pretrained(folder, gguf_file=name)
    tokenizer = AutoTokenizer.from_pretrained(folder, gguf_file=name)
    fields = {k: f.contents() for k, f in GGUFReader(path).fields.items()
              if k.startswith(("tokenizer.ggml.", "general.sampling.")) and k.endswith(("_id", "temp", "top_p", "top_k", "min_p"))}
    os.makedirs(os.path.dirname(out), exist_ok=True)
    work = tempfile.mkdtemp(dir=os.path.dirname(out), prefix=".work-")
    try:
        config.save_pretrained(work)
        tokenizer.save_pretrained(work)
        with open(os.path.join(work, "generation_config.json"), "w") as f:
            json.dump(generation(fields), f, indent=2)
        try:
            os.rename(work, out)
        except OSError:
            # Another start made it meanwhile.
            if not os.path.isfile(os.path.join(out, "config.json")):
                raise
    finally:
        shutil.rmtree(work, ignore_errors=True)


def prepare(path: str, server: str) -> str | None:
    """
    The folder of a GGUF file's config and tokenizer, written first when it is not there; a ValueError says why it cannot be.
    None where it is not there and transformers or gguf is not installed (the fake servers, which read nothing): the server
    is left to the file.
    """
    out = folder_for(path, os.environ.get("VLLM_GGUF_DIR", ROOT))
    if os.path.isfile(os.path.join(out, "config.json")):
        return out
    try:
        import gguf  # noqa: F401
        import transformers  # noqa: F401
    except ImportError:
        return None
    print(f"{server}: reading {path}'s config and tokenizer from its header into {out}", flush=True)
    try:
        write(path, out)
    except (ValueError, KeyError, OSError) as e:
        raise ValueError(f"{server} cannot run {path}: its config and tokenizer cannot be read from it ({e}). {server} reads a GGUF file's "
                         "settings through transformers, which knows only some architectures: run it on llama.cpp, or give it the model's "
                         "Hugging Face snapshot.") from e
    return out
