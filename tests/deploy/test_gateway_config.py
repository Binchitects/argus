"""The gateway's own settings (deploy/config/litellm.yaml), in each copy of them: the HA one and the chart's."""
from __future__ import annotations

import asyncio
import importlib.util
import sys
import types
import unittest

from support import REPO

DEPLOY = REPO / "deploy"
COPIES = (DEPLOY / "config" / "litellm.yaml", DEPLOY / "config" / "litellm-ha.yaml",
          DEPLOY / "helm" / "argus-arena" / "files" / "litellm.yaml", DEPLOY / "helm" / "argus-arena" / "files" / "litellm-ha.yaml")
LIMITS = DEPLOY / "config" / "arena_limits.py"


def settings(path):
    return path.read_text().split("\nlitellm_settings:\n", 1)[1].split("\n\n", 1)[0].splitlines()


class FakeRouter:
    """LiteLLM's router as the hook reads it: each model's copies with what the app registered."""

    def __init__(self, models):
        self.models = models

    def get_model_list(self, model_name=None):
        return [{"model_name": model_name, "model_info": info} for info in self.models.get(model_name, [])]


def limits(router):
    """arena_limits.py as LiteLLM loads it, against a LiteLLM of stand-ins: its hook's base and the proxy's router."""
    logger = types.ModuleType("litellm.integrations.custom_logger")
    logger.CustomLogger = type("CustomLogger", (), {})
    proxy = types.ModuleType("litellm.proxy.proxy_server")
    proxy.llm_router = router
    stand_ins = {"litellm": types.ModuleType("litellm"), "litellm.integrations": types.ModuleType("litellm.integrations"),
                 "litellm.integrations.custom_logger": logger, "litellm.proxy": types.ModuleType("litellm.proxy"),
                 "litellm.proxy.proxy_server": proxy}
    saved = {name: sys.modules.get(name) for name in stand_ins}
    sys.modules.update(stand_ins)
    spec = importlib.util.spec_from_file_location("arena_limits", LIMITS)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module, saved


class GatewayConfigTests(unittest.TestCase):
    def test_a_max_tokens_above_a_models_longest_answer_is_lowered_to_it(self):
        # llama.cpp writes as long as a request asks: LiteLLM lowers max_tokens to the model's max_output_tokens.
        for path in COPIES:
            self.assertIn("  modify_params: true", settings(path), path)

    def test_every_copy_holds_each_way_of_asking_and_answers_claude_code_through_chat_completions(self):
        for path in COPIES:
            lines = settings(path)
            self.assertIn("  callbacks: [arena_limits.longest_answer]", lines, path)
            self.assertIn("  use_chat_completions_url_for_anthropic_messages: true", lines, path)
        self.assertEqual(LIMITS.read_text(), (DEPLOY / "helm" / "argus-arena" / "files" / "arena_limits.py").read_text())

    def test_the_hold_is_mounted_beside_the_config_that_names_it(self):
        # LiteLLM loads a callback's module from the config's own folder (/app).
        self.assertIn("./config/arena_limits.py:/app/arena_limits.py:ro", (DEPLOY / "docker-compose.yml").read_text())
        self.assertIn("./config/arena_limits.py:/app/arena_limits.py:ro", (DEPLOY / "ha.yml").read_text())
        chart = DEPLOY / "helm" / "argus-arena" / "templates"
        self.assertIn("mountPath: /app/arena_limits.py, subPath: arena_limits.py", (chart / "gateway.yaml").read_text())
        self.assertIn('{{ .Files.Get "files/arena_limits.py" | indent 4 }}', (chart / "configmaps.yaml").read_text())

    def test_each_way_of_asking_for_a_longer_answer_is_lowered_to_the_models_longest(self):
        # SmolLM2 on llama.cpp: answers up to 1,024. A pool says the least of its copies.
        router = FakeRouter({"Small": [{"max_output_tokens": 1024}], "Pool": [{"max_output_tokens": 4096}, {"max_output_tokens": 2048}],
                             "Unsaid": [{"mode": "chat"}]})
        module, saved = limits(router)
        try:
            def hold(data):
                return asyncio.run(module.longest_answer.async_pre_call_hook(None, None, data, "acompletion"))

            # Chat completions (max_tokens, max_completion_tokens), text completions and Anthropic's messages
            # (max_tokens), the Responses API (max_output_tokens).
            for key in ("max_tokens", "max_completion_tokens", "max_output_tokens"):
                self.assertEqual(1024, hold({"model": "Small", key: 5000})[key], key)
            self.assertEqual(2048, hold({"model": "Pool", "max_completion_tokens": 3000})["max_completion_tokens"])
            # Within it, none asked, a model that says none, or one the gateway does not know: as asked.
            self.assertEqual({"model": "Small", "max_tokens": 300}, hold({"model": "Small", "max_tokens": 300}))
            self.assertEqual({"model": "Small", "messages": []}, hold({"model": "Small", "messages": []}))
            self.assertEqual(5000, hold({"model": "Unsaid", "max_tokens": 5000})["max_tokens"])
            self.assertEqual(5000, hold({"model": "Other", "max_output_tokens": 5000})["max_output_tokens"])
        finally:
            for name, was in saved.items():
                if was is None:
                    sys.modules.pop(name, None)
                else:
                    sys.modules[name] = was


if __name__ == "__main__":
    unittest.main()
