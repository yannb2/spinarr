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
        text = "ARG LIBCDIO_VERSION=2.1.0\nARG LIBCDIO_PARANOIA_VERSION=10.2+2.0.2\nFROM x\nARG LIBCDIO_VERSION\n"
        self.assertEqual(b.read_args(text), {"LIBCDIO_VERSION": "2.1.0", "LIBCDIO_PARANOIA_VERSION": "10.2+2.0.2"})
        changed = b.set_arg(text, "LIBCDIO_VERSION", "2.2.0")
        self.assertIn("ARG LIBCDIO_VERSION=2.2.0\n", changed)
        self.assertIn("ARG LIBCDIO_VERSION\n", changed)

    def test_gnu_listing_patterns(self):
        page = ('<a href="libcdio-2.1.0.tar.bz2">x</a> <a href="libcdio-2.2.0.tar.bz2.sig">x</a> '
                '<a href="libcdio-paranoia-10.2+2.0.1.tar.bz2">x</a> <a href="libcdio-paranoia-10.2+2.0.2.tar.bz2">x</a>')
        self.assertEqual(b.newest(re.findall(tool("LIBCDIO_VERSION").pattern, page)), "2.2.0")
        self.assertEqual(b.newest(re.findall(tool("LIBCDIO_PARANOIA_VERSION").pattern, page)), "10.2+2.0.2")

    def test_only_a_higher_version_is_newer(self):
        self.assertTrue(b.is_newer("10.2+2.0.3", "10.2+2.0.2"))
        self.assertFalse(b.is_newer("10.2+2.0.2", "10.2+2.0.2"))
        self.assertFalse(b.is_newer(None, "2.1.0"))

    def test_whipper_is_gone(self):
        self.assertNotIn("WHIPPER_COMMIT", b.read_args(b.DOCKERFILE.read_text()))

    def test_every_tool_has_an_arg_in_the_dockerfile(self):
        pins = b.read_args(b.DOCKERFILE.read_text())
        for t in b.TOOLS:
            self.assertIn(t.arg, pins, t.name)


if __name__ == "__main__":
    unittest.main()
