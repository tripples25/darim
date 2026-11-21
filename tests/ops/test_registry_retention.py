import importlib.machinery
import importlib.util
from pathlib import Path
import unittest

path = Path(__file__).parents[2] / "ops/monitoring/registry-cleanup"
loader = importlib.machinery.SourceFileLoader("registry_cleanup", str(path))
module = importlib.util.module_from_spec(importlib.util.spec_from_loader(loader.name, loader))
loader.exec_module(module)


class RetentionTests(unittest.TestCase):
    def test_protects_aliases_recency_and_successful_releases(self):
        tags = {
            "production": ("live", 0),
            "1" * 40: ("live", 0),
            "2" * 40: ("rollback", 0),
            "3" * 40: ("old", 0),
            "4" * 40: ("recent", 100),
            "5" * 40: ("recent", 0),
            "6" * 40: ("manual", 0),
            "special": ("manual", 0),
        }
        self.assertEqual(module.candidates(tags, {"live", "rollback"}, 50), {"old"})

    def test_protects_children_of_kept_index(self):
        root = "sha256:" + "1" * 64
        child = "sha256:" + "2" * 64
        self.assertEqual(
            module.retained_children({root}, {root: {"manifests": [{"digest": child}]}, child: {}}),
            {root, child},
        )
