"""
deploy/features and the compose file in step: each optional service runs under its feature's profile, each feature's
services are there, and .env.example turns on the features that are on by default.
"""
from __future__ import annotations

import re
import unittest
from pathlib import Path

import yaml

DEPLOY = Path(__file__).resolve().parents[2] / "deploy"
# What always runs: no profile, no feature.
CORE = {"traefik", "web", "app", "postgres", "litellm"}


def features() -> list[dict]:
    out = []
    for line in (DEPLOY / "features").read_text().splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        fid, default, services, needs, requires, title = (f.strip() for f in line.split("|"))
        out.append({"id": fid, "on": default == "on", "services": [] if services == "-" else services.split(),
                    "needs": needs, "requires": [] if requires == "-" else requires.split(), "title": title})
    return out


class Features(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.features = features()
        cls.compose = yaml.safe_load((DEPLOY / "docker-compose.yml").read_text())["services"]

    def test_Each_optional_service_runs_under_its_features_profile_and_the_core_under_none(self):
        owner = {s: f["id"] for f in self.features for s in f["services"]}
        for name, service in self.compose.items():
            with self.subTest(name):
                if name in CORE:
                    self.assertNotIn("profiles", service)
                else:
                    self.assertIn(name, owner, f"{name} is in no feature of deploy/features")
                    self.assertEqual([owner[name]], service.get("profiles"))
        for f in self.features:
            for s in f["services"]:
                self.assertIn(s, self.compose, f"{f['id']} runs {s}, which docker-compose.yml does not have")

    def test_The_ids_are_profile_names_and_what_a_feature_requires_is_listed(self):
        ids = [f["id"] for f in self.features]
        self.assertEqual(len(ids), len(set(ids)))
        for f in self.features:
            self.assertRegex(f["id"], r"^[a-z0-9][a-z0-9-]*$")
            self.assertIn(f["needs"], {"-", "gpu", "gitlab"})
            for r in f["requires"]:
                self.assertIn(r, ids, f"{f['id']} requires {r}")

    def test_The_examples_COMPOSE_PROFILES_is_the_features_on_by_default(self):
        m = re.search(r"^COMPOSE_PROFILES=(.*)$", (DEPLOY / ".env.example").read_text(), re.M)
        self.assertIsNotNone(m)
        self.assertEqual([f["id"] for f in self.features if f["on"]], m.group(1).split(","))

    def test_The_deployment_guide_lists_every_feature(self):
        doc = (DEPLOY.parent / "docs" / "deployment.md").read_text()
        section = doc.split("\n## Features\n", 1)[1].split("\n## ", 1)[0]
        for f in self.features:
            self.assertIn(f"`{f['id']}`", section, f["id"])


if __name__ == "__main__":
    unittest.main()
