"""deploy/services/studio/server.py: how a text is cut for the voice model, which models are there, and one at a time,
with torch, FastAPI and the audio libraries stubbed (no GPU, no models)."""
from __future__ import annotations

import importlib.util
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]


def _stubs() -> dict[str, types.ModuleType]:
    class App:
        def __init__(self, **_):
            pass

        def _keep(self, *_, **__):
            return lambda f: f

        get = post = exception_handler = _keep

    torch = types.ModuleType("torch")
    torch.cuda = types.SimpleNamespace(is_available=lambda: False, empty_cache=lambda: None, mem_get_info=lambda: (0, 0))
    torch.OutOfMemoryError = type("OutOfMemoryError", (Exception,), {})
    fastapi = types.ModuleType("fastapi")
    fastapi.FastAPI, fastapi.Request = App, object
    responses = types.ModuleType("fastapi.responses")
    responses.JSONResponse = responses.Response = object
    pil = types.ModuleType("PIL")
    pil.Image = types.ModuleType("PIL.Image")
    return {
        "torch": torch, "fastapi": fastapi, "fastapi.responses": responses, "numpy": types.ModuleType("numpy"),
        "soundfile": types.ModuleType("soundfile"), "PIL": pil, "PIL.Image": pil.Image,
    }


with mock.patch.dict(sys.modules, _stubs()):
    spec = importlib.util.spec_from_file_location("studio_server", REPO / "deploy/services/studio/server.py")
    server = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(server)


class Pieces(unittest.TestCase):
    def test_sentences_are_put_together_up_to_the_most_and_long_ones_cut_at_a_space(self):
        self.assertEqual(server._pieces("One. Two! Three?", most=10), ["One. Two!", "Three?"])
        self.assertEqual(server._pieces("Hello there.  ", most=280), ["Hello there."])
        long = " ".join(["word"] * 40)
        pieces = server._pieces(long, most=50)
        self.assertTrue(all(len(p) <= 50 for p in pieces))
        self.assertEqual(" ".join(pieces), long)
        self.assertEqual(server._pieces("   "), [])


class Models(unittest.TestCase):
    def test_a_model_is_there_only_with_all_its_files_and_one_missing_is_refused_with_how_to_get_it(self):
        with tempfile.TemporaryDirectory() as d, mock.patch.object(server, "STUDIO_DIR", d):
            self.assertFalse(server._present("cutout"))
            folder = Path(d) / "birefnet-lite"
            folder.mkdir()
            for f in server.MODELS["cutout"][1][:-1]:
                (folder / f).write_text("")
            self.assertFalse(server._present("cutout"))
            (folder / server.MODELS["cutout"][1][-1]).write_text("")
            self.assertTrue(server._present("cutout"))
            with self.assertRaises(server.Refused) as refused:
                server._load("voice")
            self.assertEqual(refused.exception.status, 503)
            self.assertIn("Admin → Models", str(refused.exception))

    def test_loading_one_unloads_the_other_first(self):
        with mock.patch.dict(server._loaded, {"music": object()}, clear=True), mock.patch.object(server, "_present", return_value=True), \
                mock.patch.object(server, "_free_mb", return_value=1000):
            # Not enough GPU memory for the voice model: refused, and music was unloaded to make room first.
            with self.assertRaises(server.Refused) as refused:
                server._load("voice")
            self.assertIn("1,000 MB free", str(refused.exception))
            self.assertEqual(server._loaded, {})


if __name__ == "__main__":
    unittest.main()
