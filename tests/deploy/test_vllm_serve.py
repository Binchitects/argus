"""
The launchers the supervisor starts a model with, for a GGUF file from the library: deploy/services/vllm/serve.py (vLLM)
adds the folder of the file's config and tokenizer with --tokenizer, which vLLM's GGUF plugin needs; sglang/serve.py adds
it with --tokenizer-path, refuses a part of a split file, which SGLang cannot read, and tells SGLang a llama file's rotary
layout. gguf_config.py writes the folder once from the file's own header (transformers, gguf: stand-ins here), with the
tokens that end an answer and the sampling its makers recommend, and says why when it cannot. A snapshot, or arguments
that name a tokenizer, are left as they are. vllm-gguf-rope.py, the plugin the vLLM images install, gives a llama model read
from a GGUF file llama.cpp's rotary layout and changes nothing else.
"""
from __future__ import annotations

import importlib.util
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
VLLM = REPO / "deploy" / "services" / "vllm"
sys.path.insert(0, str(VLLM))
import gguf_config  # noqa: E402


def load(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


vllm_serve = load(VLLM / "serve.py", "vllm_serve")
sglang_serve = load(REPO / "deploy" / "services" / "sglang" / "serve.py", "sglang_serve")


def header(path: Path, arch: str | None, before: int = 2) -> None:
    """A GGUF file's header as llama.cpp writes one: a few values first (a string and an array of strings), then its architecture."""
    def s(text: str) -> bytes:
        b = text.encode()
        return struct.pack("<Q", len(b)) + b

    kvs = [s("general.name") + struct.pack("<I", 8) + s("Tiny"),
           s("tokenizer.ggml.tokens") + struct.pack("<IIQ", 9, 8, 3) + s("a") + s("b") + s("c"),
           s("llama.context_length") + struct.pack("<II", 4, 8192)][:before]
    if arch is not None:
        kvs.append(s("general.architecture") + struct.pack("<I", 8) + s(arch))
    path.write_bytes(b"GGUF" + struct.pack("<IQQ", 3, 0, len(kvs)) + b"".join(kvs))


class Saved:
    """A config or tokenizer that writes its files as transformers' do."""

    def __init__(self, files: dict[str, str]):
        self.files = files

    def save_pretrained(self, folder: str):
        for name, text in self.files.items():
            Path(folder, name).write_text(text)


def fake_transformers(config=None) -> dict:
    """transformers and gguf as sys.modules entries: AutoConfig and AutoTokenizer from a GGUF file, GGUFReader's fields."""
    calls = []

    def config_from(folder, gguf_file):
        calls.append(("config", folder, gguf_file))
        if isinstance(config, Exception):
            raise config
        return Saved({"config.json": json.dumps({"model_type": "qwen3"})})

    def tokenizer_from(folder, gguf_file):
        calls.append(("tokenizer", folder, gguf_file))
        return Saved({"tokenizer.json": "{}", "tokenizer_config.json": "{}", "chat_template.jinja": "{{ messages }}"})

    field = lambda v: types.SimpleNamespace(contents=lambda: v)  # noqa: E731
    reader = lambda path: types.SimpleNamespace(fields={  # noqa: E731
        "tokenizer.ggml.eos_token_id": field(151645), "tokenizer.ggml.eot_token_id": field(151643), "tokenizer.ggml.padding_token_id": field(151654),
        "tokenizer.ggml.tokens": field(["a", "b"]), "general.sampling.temp": field(0.6000000238418579), "general.sampling.top_k": field(20),
        "general.architecture": field("qwen3")})
    modules = {"transformers": types.SimpleNamespace(AutoConfig=types.SimpleNamespace(from_pretrained=config_from),
                                                     AutoTokenizer=types.SimpleNamespace(from_pretrained=tokenizer_from)),
               "gguf": types.SimpleNamespace(GGUFReader=reader)}
    return modules, calls


class LauncherTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="gguf-launch-"))
        self.addCleanup(shutil.rmtree, self.dir, ignore_errors=True)
        self.library = self.dir / "library"
        (self.library / "org" / "Model-GGUF").mkdir(parents=True)
        self.gguf = self.library / "org" / "Model-GGUF" / "Model-Q4_K_M.gguf"
        self.gguf.write_bytes(b"GGUF" + b"\0" * 60)
        env = mock.patch.dict(os.environ, {"VLLM_GGUF_DIR": str(self.dir / "hf")})
        env.start()
        self.addCleanup(env.stop)

    def test_vllm_gets_the_files_config_and_tokenizer_written_once_from_its_header(self):
        modules, calls = fake_transformers()
        with mock.patch.dict(sys.modules, modules):
            run = vllm_serve.args(["serve", str(self.gguf), "--max-model-len", "8192"])
            again = vllm_serve.args(["serve", str(self.gguf), "--max-model-len", "4096"])
        folder = gguf_config.folder_for(str(self.gguf), str(self.dir / "hf"))
        self.assertEqual(["serve", str(self.gguf), "--max-model-len", "8192", "--tokenizer", folder], run)
        self.assertEqual(folder, again[-1])
        # Read from the file itself (its folder and its name, as transformers takes a GGUF), once.
        self.assertEqual([("config", str(self.gguf.parent), self.gguf.name), ("tokenizer", str(self.gguf.parent), self.gguf.name)], calls)
        self.assertEqual(["chat_template.jinja", "config.json", "generation_config.json", "tokenizer.json", "tokenizer_config.json"], sorted(os.listdir(folder)))
        self.assertEqual({"eos_token_id": [151645, 151643], "pad_token_id": 151654, "temperature": 0.6, "top_k": 20},
                         json.loads(Path(folder, "generation_config.json").read_text()))
        # Nothing half-written left beside it.
        self.assertEqual([Path(folder).name], os.listdir(self.dir / "hf"))

    def test_a_file_changed_in_place_gets_a_new_folder(self):
        before = gguf_config.folder_for(str(self.gguf), str(self.dir / "hf"))
        self.gguf.write_bytes(b"GGUF" + b"\1" * 80)
        self.assertNotEqual(before, gguf_config.folder_for(str(self.gguf), str(self.dir / "hf")))

    def test_a_snapshot_or_a_tokenizer_named_already_is_left_as_it_is(self):
        modules, calls = fake_transformers()
        with mock.patch.dict(sys.modules, modules):
            for argv in (["serve", str(self.library / "hf" / "org" / "Model@abc"), "--max-model-len", "8192"],
                         ["serve", str(self.gguf), "--tokenizer", "/library/hf/org/Model@abc"],
                         ["serve", str(self.gguf), "--hf-config-path=/library/hf/org/Model@abc"]):
                self.assertEqual(argv, vllm_serve.args(argv))
            for argv in (["--model-path", "/library/hf/org/Model@abc", "--tp-size", "1"],
                         ["--model-path", str(self.gguf), "--tokenizer-path", "/library/hf/org/Model@abc"]):
                self.assertEqual(argv, sglang_serve.args(argv))
        self.assertEqual([], calls)

    def test_an_architecture_transformers_cannot_read_says_why_and_how_else_to_run_it(self):
        modules, _ = fake_transformers(config=ValueError("GGUF model with architecture qwen4exp is not supported yet."))
        with mock.patch.dict(sys.modules, modules), self.assertRaises(ValueError) as e:
            vllm_serve.args(["serve", str(self.gguf)])
        self.assertIn(f"vLLM cannot run {self.gguf}", str(e.exception))
        self.assertIn("architecture qwen4exp is not supported yet", str(e.exception))
        self.assertIn("run it on llama.cpp, or give it the model's Hugging Face snapshot", str(e.exception))
        self.assertFalse((self.dir / "hf").exists() and any(p.name.endswith(".json") for p in (self.dir / "hf").rglob("*")))

    def test_without_transformers_nothing_is_written_and_the_server_reads_the_file(self):
        # The fake servers' image: no transformers, no gguf.
        with mock.patch.dict(sys.modules, {"transformers": None, "gguf": None}):
            self.assertEqual(["serve", str(self.gguf)], vllm_serve.args(["serve", str(self.gguf)]))
            self.assertEqual(["--model-path", str(self.gguf)], sglang_serve.args(["--model-path", str(self.gguf)]))

    def test_sglang_gets_its_tokenizer_folder_and_refuses_a_part_of_a_split_file_with_why(self):
        modules, _ = fake_transformers()
        with mock.patch.dict(sys.modules, modules):
            run = sglang_serve.args(["--model-path", str(self.gguf), "--tp-size", "1"])
            self.assertEqual(["--model-path", str(self.gguf), "--tp-size", "1", "--tokenizer-path", gguf_config.folder_for(str(self.gguf), str(self.dir / "hf"))], run)
            with self.assertRaises(ValueError) as e:
                sglang_serve.args(["--model-path", "/library/org/Big-GGUF/Big-Q4_K_M-00001-of-00003.gguf", "--tp-size", "2"])
        self.assertIn("it reads one GGUF file, and this is a part of one split in several", str(e.exception))
        self.assertIn("llama-gguf-split --merge", str(e.exception))
        self.assertTrue(gguf_config.split_part("/l/Big-Q4_K_M-00001-of-00003.gguf"))
        self.assertFalse(gguf_config.split_part("/l/Model-Q4_K_M.gguf"))
        self.assertFalse(gguf_config.split_part("/l/Model-of-the-year-Q4.gguf"))

    def test_sglang_is_told_a_llama_files_rotary_layout_and_another_architectures_are_left_as_they_are(self):
        header(self.gguf, "llama")
        qwen = self.gguf.with_name("Qwen-Q4_K_M.gguf")
        header(qwen, "qwen3")
        modules, _ = fake_transformers()
        with mock.patch.dict(sys.modules, modules):
            run = sglang_serve.args(["--model-path", str(self.gguf), "--tp-size", "1"])
            given = sglang_serve.args(["--model-path", str(self.gguf), "--json-model-override-args", '{"max_position_embeddings": 4096}'])
            other = sglang_serve.args(["--model-path", str(qwen), "--tp-size", "1"])
        flag = run.index("--json-model-override-args")
        self.assertEqual({"rope_is_neox_style": False}, json.loads(run[flag + 1]))
        self.assertEqual({"max_position_embeddings": 4096, "rope_is_neox_style": False}, json.loads(given[given.index("--json-model-override-args") + 1]))
        self.assertEqual(1, given.count("--json-model-override-args"))
        self.assertNotIn("--json-model-override-args", other)

    def test_a_files_architecture_is_read_from_its_header_alone(self):
        for arch, before in (("llama", 0), ("qwen3", 2), ("nomic-bert", 3)):
            header(self.gguf, arch, before)
            self.assertEqual(arch, gguf_config.architecture(str(self.gguf)))
        header(self.gguf, None, 3)
        self.assertIsNone(gguf_config.architecture(str(self.gguf)))
        self.gguf.write_bytes(b"GGUF" + struct.pack("<IQQ", 3, 0, 5) + b"\1\0")
        self.assertIsNone(gguf_config.architecture(str(self.gguf)))
        self.gguf.write_text("not a GGUF file")
        self.assertIsNone(gguf_config.architecture(str(self.gguf)))
        self.assertIsNone(gguf_config.architecture(str(self.dir / "gone.gguf")))

    def test_the_generation_config_takes_one_end_token_as_a_number_and_no_sampling_it_does_not_say(self):
        self.assertEqual({"eos_token_id": 2, "bos_token_id": 1, "pad_token_id": 0},
                         gguf_config.generation({"tokenizer.ggml.eos_token_id": 2, "tokenizer.ggml.bos_token_id": 1, "tokenizer.ggml.padding_token_id": 0}))
        self.assertEqual({"eos_token_id": 7, "top_p": 0.95, "min_p": 0.0},
                         gguf_config.generation({"tokenizer.ggml.eos_token_id": 7, "tokenizer.ggml.eot_token_id": 7, "general.sampling.top_p": 0.949999988079071,
                                                 "general.sampling.min_p": 0.0}))

    def test_vllm_is_run_with_the_folder_from_a_launch_before(self):
        """serve.py as the supervisor runs it, `vllm` on the PATH: the folder written by an earlier start is named, without transformers."""
        folder = Path(gguf_config.folder_for(str(self.gguf), str(self.dir / "hf")))
        folder.mkdir(parents=True)
        (folder / "config.json").write_text("{}")
        bin_dir = self.dir / "bin"
        bin_dir.mkdir()
        (bin_dir / "vllm").write_text('#!/bin/sh\nfor a in "$@"; do echo "$a"; done\n')
        (bin_dir / "vllm").chmod(0o755)
        out = subprocess.run([sys.executable, str(VLLM / "serve.py"), "serve", str(self.gguf), "--port", "8001"], capture_output=True, text=True, timeout=30,
                             env={"PATH": f"{bin_dir}:/usr/bin:/bin", "VLLM_GGUF_DIR": str(self.dir / "hf")})
        self.assertEqual(0, out.returncode, out.stderr)
        self.assertEqual(["serve", str(self.gguf), "--port", "8001", "--tokenizer", str(folder)], out.stdout.split())

    def test_vllm_does_not_start_when_the_folder_cannot_be_made(self):
        bin_dir = self.dir / "bin"
        bin_dir.mkdir()
        (bin_dir / "vllm").write_text("#!/bin/sh\necho started\n")
        (bin_dir / "vllm").chmod(0o755)
        stubs = self.dir / "stubs"
        (stubs / "transformers").mkdir(parents=True)
        (stubs / "gguf").mkdir()
        (stubs / "gguf" / "__init__.py").write_text("class GGUFReader:\n    pass\n")
        (stubs / "transformers" / "__init__.py").write_text(
            "class AutoConfig:\n    @staticmethod\n    def from_pretrained(folder, gguf_file):\n"
            "        raise ValueError('GGUF model with architecture qwen3vlmoe is not supported yet.')\n"
            "AutoTokenizer = AutoConfig\n")
        out = subprocess.run([sys.executable, str(VLLM / "serve.py"), "serve", str(self.gguf)], capture_output=True, text=True, timeout=30,
                             env={"PATH": f"{bin_dir}:/usr/bin:/bin", "VLLM_GGUF_DIR": str(self.dir / "hf"), "PYTHONPATH": str(stubs)})
        self.assertEqual(1, out.returncode)
        self.assertNotIn("started", out.stdout)
        self.assertIn("vLLM cannot run", out.stderr)
        self.assertIn("architecture qwen3vlmoe is not supported yet", out.stderr)


class RopePluginTests(unittest.TestCase):
    """gguf_rope.register over stand-ins for vLLM's llama model and get_rope, as vLLM calls it in each of its processes."""

    def setUp(self):
        calls = self.calls = []

        def get_rope(head, max_position, rope_parameters, is_neox_style):
            calls.append(is_neox_style)
            return ("rope", head, max_position, rope_parameters, is_neox_style)

        class LlamaAttention:
            head_dim, max_position_embeddings = 64, 8192

            def _init_rotary_emb(self, config, quant_config=None):
                self.rotary_emb = get_rope(self.head_dim, max_position=self.max_position_embeddings,
                                           rope_parameters=getattr(config, "rope_parameters", None), is_neox_style=True)

        self.attention = LlamaAttention
        llama = types.SimpleNamespace(LlamaAttention=LlamaAttention)
        rotary = types.SimpleNamespace(get_rope=get_rope)
        names = ("vllm", "vllm.model_executor", "vllm.model_executor.layers", "vllm.model_executor.models")
        stubs = {n: types.ModuleType(n) for n in names} | {"vllm.model_executor.layers.rotary_embedding": rotary, "vllm.model_executor.models.llama": llama}
        stubs["vllm.model_executor.models"].llama = llama
        stubs["vllm.model_executor.layers"].rotary_embedding = rotary
        patch = mock.patch.dict(sys.modules, stubs)
        patch.start()
        self.addCleanup(patch.stop)
        self.rope = load(VLLM.parent / "vllm-gguf-rope.py", "arena_gguf_rope")

    def rotary(self, model_type: str, quant: str | None):
        layer = self.attention()
        config = types.SimpleNamespace(model_type=model_type, rope_parameters={"rope_theta": 100000.0})
        layer._init_rotary_emb(config, quant_config=None if quant is None else types.SimpleNamespace(get_name=lambda: quant))
        return layer.rotary_emb

    def test_a_llama_model_from_a_gguf_file_takes_llama_cpps_rotary_layout_and_nothing_else_changes(self):
        self.rope.register()
        self.rope.register()
        self.assertEqual(("rope", 64, 8192, {"rope_theta": 100000.0}, False), self.rotary("llama", "gguf"))
        for model_type, quant in (("llama", None), ("llama", "fp8"), ("qwen3", "gguf"), ("mistral", None)):
            self.assertTrue(self.rotary(model_type, quant)[-1], (model_type, quant))
        # Registered twice (a process loads its plugins once, but each process does): wrapped once.
        self.calls.clear()
        self.rotary("llama", "gguf")
        self.assertEqual([True, False], self.calls)


if __name__ == "__main__":
    unittest.main()
