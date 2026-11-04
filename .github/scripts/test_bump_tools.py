import json
import re
import unittest

import bump_tools as b


def tool(arg):
    return next(t for t in b.TOOLS if t.arg == arg)


class BumpToolsTests(unittest.TestCase):
    def test_versions_compare_as_numbers(self):
        self.assertEqual(b.newest(["2.1.0", "2.1.10", "2.1.9"]), "2.1.10")
        self.assertEqual(b.newest(["10.2+2.0.1", "10.2+2.0.2"]), "10.2+2.0.2")
        self.assertIsNone(b.newest([]))

    def test_reads_and_sets_args(self):
        text = "ARG LIBCDIO_VERSION=2.1.0\nARG WHIPPER_COMMIT=abc\nFROM x\nARG LIBCDIO_VERSION\n"
        self.assertEqual(b.read_args(text), {"LIBCDIO_VERSION": "2.1.0", "WHIPPER_COMMIT": "abc"})
        changed = b.set_arg(text, "LIBCDIO_VERSION", "2.2.0")
        self.assertIn("ARG LIBCDIO_VERSION=2.2.0\n", changed)
        self.assertIn("ARG LIBCDIO_VERSION\n", changed)

    def test_gnu_listing_patterns(self):
        page = ('<a href="libcdio-2.1.0.tar.bz2">x</a> <a href="libcdio-2.2.0.tar.bz2.sig">x</a> '
                '<a href="libcdio-paranoia-10.2+2.0.1.tar.bz2">x</a> <a href="libcdio-paranoia-10.2+2.0.2.tar.bz2">x</a>')
        self.assertEqual(b.newest(re.findall(tool("LIBCDIO_VERSION").pattern, page)), "2.2.0")
        self.assertEqual(b.newest(re.findall(tool("LIBCDIO_PARANOIA_VERSION").pattern, page)), "10.2+2.0.2")

    def test_whipper_follows_the_forks_develop_branch(self):
        whipper = tool("WHIPPER_COMMIT")
        self.assertEqual(b.commit_url(whipper.source), "https://api.github.com/repos/yannb2/whipper/commits/develop")
        sha = "fdb2f1ef5a6df0f812ab1b94beb66d33aaf4987c"
        self.assertEqual(b.head_commit(json.dumps({"sha": sha, "commit": {}})), sha)
        with self.assertRaises(ValueError):
            b.head_commit(json.dumps({"sha": "not-a-sha"}))
        self.assertTrue(b.is_newer(whipper, "1" * 40, sha))
        self.assertFalse(b.is_newer(whipper, sha, sha))
        self.assertEqual(b.show(whipper, sha), "fdb2f1e")
        self.assertEqual(b.notes(whipper, sha, "1" * 40),
                         "https://github.com/yannb2/whipper/compare/fdb2f1e...1111111")

    def test_every_tool_has_an_arg_in_the_dockerfile(self):
        pins = b.read_args(b.DOCKERFILE.read_text())
        for t in b.TOOLS:
            self.assertIn(t.arg, pins, t.name)


if __name__ == "__main__":
    unittest.main()
