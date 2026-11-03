"""The gateway's own settings (deploy/config/litellm.yaml), in each copy of them: the HA one and the chart's."""
from __future__ import annotations

import unittest

from support import REPO

DEPLOY = REPO / "deploy"
COPIES = (DEPLOY / "config" / "litellm.yaml", DEPLOY / "config" / "litellm-ha.yaml",
          DEPLOY / "helm" / "argus-arena" / "files" / "litellm.yaml", DEPLOY / "helm" / "argus-arena" / "files" / "litellm-ha.yaml")


class GatewayConfigTests(unittest.TestCase):
    def test_a_max_tokens_above_a_models_longest_answer_is_lowered_to_it(self):
        # llama.cpp writes as long as a request asks: LiteLLM lowers max_tokens to the model's max_output_tokens.
        for path in COPIES:
            settings = path.read_text().split("\nlitellm_settings:\n", 1)[1].split("\n\n", 1)[0]
            self.assertIn("  modify_params: true", settings.splitlines(), path)


if __name__ == "__main__":
    unittest.main()
