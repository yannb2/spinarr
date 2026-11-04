#!/usr/bin/env python3
"""bump_tools.py: look for newer versions of the tools pinned in the Dockerfile and update the ARG lines.

Run by the weekly "Tool updates" workflow, which opens a pull request with the result; the
image build on that pull request is the test. Nothing is installed or merged here.

    python3 .github/scripts/bump_tools.py            # update the Dockerfile, print a Markdown summary
    python3 .github/scripts/bump_tools.py --dry-run  # only print what would change

whipper is the homelab fork, pinned to a commit: a newer commit on its develop branch is the update.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

DOCKERFILE = Path(__file__).resolve().parents[2] / "Dockerfile"
USER_AGENT = "Spinarr tool updates (+https://github.com/yannb2/spinarr)"


@dataclass
class Tool:
    arg: str              # the ARG in the Dockerfile
    name: str
    source: str           # page URL, or owner/repo@branch for a commit
    pattern: str = ""     # for a page: regex whose group 1 is a version
    commit: bool = False
    notes: str = ""       # release notes link for the pull request


TOOLS = [
    Tool("WHIPPER_COMMIT", "whipper (fork)", "yannb2/whipper@develop", commit=True,
         notes="https://github.com/yannb2/whipper/commits/develop"),
    Tool("LIBCDIO_VERSION", "libcdio", "https://ftp.gnu.org/gnu/libcdio/",
         r"libcdio-(\d+\.\d+\.\d+)\.tar\.bz2", notes="https://github.com/libcdio/libcdio/blob/master/NEWS.md"),
    Tool("LIBCDIO_PARANOIA_VERSION", "cd-paranoia", "https://ftp.gnu.org/gnu/libcdio/",
         r"libcdio-paranoia-(\d+\.\d+\+\d+\.\d+\.\d+)\.tar\.bz2",
         notes="https://github.com/libcdio/libcdio-paranoia/blob/master/NEWS.md"),
]


def version_key(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in re.findall(r"\d+", version))


def newest(versions: list[str]) -> str | None:
    return max(versions, key=version_key, default=None)


def read_args(text: str) -> dict[str, str]:
    """ARG NAME=value lines with a value: the pins."""
    return dict(re.findall(r"^ARG ([A-Z0-9_]+)=(\S+)$", text, re.MULTILINE))


def set_arg(text: str, arg: str, value: str) -> str:
    return re.sub(rf"^ARG {arg}=\S+$", f"ARG {arg}={value}", text, count=1, flags=re.MULTILINE)


def fetch(url: str) -> str:
    headers = {"User-Agent": USER_AGENT}
    if url.startswith("https://api.github.com/") and os.environ.get("GITHUB_TOKEN"):
        headers["Authorization"] = f"Bearer {os.environ['GITHUB_TOKEN']}"
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as response:
        return response.read().decode("utf-8", "replace")


def commit_url(source: str) -> str:
    repository, _, branch = source.partition("@")
    return f"https://api.github.com/repos/{repository}/commits/{branch or 'main'}"


def head_commit(answer: str) -> str:
    """The full commit id in GitHub's answer for a branch."""
    sha = json.loads(answer)["sha"]
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError(f"not a commit id: {sha!r}")
    return sha


def is_newer(tool: Tool, latest: str | None, current: str) -> bool:
    if not latest:
        return False
    return latest != current if tool.commit else version_key(latest) > version_key(current)


def show(tool: Tool, value: str) -> str:
    return value[:7] if tool.commit else value


def notes(tool: Tool, current: str, latest: str) -> str:
    if tool.commit:
        repository = tool.source.partition("@")[0]
        return f"https://github.com/{repository}/compare/{current[:7]}...{latest[:7]}"
    return tool.notes


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--dockerfile", type=Path, default=DOCKERFILE)
    args = parser.parse_args()

    text = args.dockerfile.read_text()
    pins = read_args(text)
    rows, failed = [], []
    for tool in TOOLS:
        current = pins.get(tool.arg)
        if current is None:
            failed.append(f"{tool.name}: no ARG {tool.arg} in the Dockerfile")
            continue
        try:
            if tool.commit:
                latest = head_commit(fetch(commit_url(tool.source)))
            else:
                latest = newest(re.findall(tool.pattern, fetch(tool.source)))
        except (urllib.error.URLError, TimeoutError, KeyError, ValueError) as error:
            failed.append(f"{tool.name}: could not check ({error})")
            continue
        if is_newer(tool, latest, current):
            rows.append((tool, current, latest))
            text = set_arg(text, tool.arg, latest)

    if rows:
        print("| Tool | Now | New | Notes |\n|---|---|---|---|")
        for tool, current, latest in rows:
            print(f"| {tool.name} | {show(tool, current)} | **{show(tool, latest)}** | [what changed]({notes(tool, current, latest)}) |")
    else:
        print("No newer versions found.")
    for line in failed:
        print(f"\n- {line}")

    if rows and not args.dry_run:
        args.dockerfile.write_text(text)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as out:
            out.write(f"changed={'true' if rows else 'false'}\n")
            out.write(f"tools={', '.join(f'{t.name} {show(t, v)}' for t, _, v in rows)}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
