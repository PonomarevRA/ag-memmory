#!/usr/bin/env python3
"""Install the current agent-memory skill into portable project skill paths."""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path


SKILL_NAME = "agent-memory"
TARGET_ROOTS = (
    Path(".agents") / "skills",
    Path(".claude") / "skills",
    Path(".cursor") / "skills",
)


def repository_root() -> Path | None:
    result = subprocess.run(
        ["git", "rev-parse", "--show-toplevel"],
        check=False,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        return None
    return Path(result.stdout.strip()).resolve()


def target_paths(root: Path) -> list[Path]:
    return [root / relative / SKILL_NAME for relative in TARGET_ROOTS]


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Install agent-memory into project skill paths without overwriting existing copies."
    )
    parser.add_argument("--check", action="store_true", help="Report paths without creating files.")
    args = parser.parse_args()

    root = repository_root()
    if root is None:
        print("Not inside a Git repository; no project skill was created.", file=sys.stderr)
        return 2

    source = Path(__file__).resolve().parent.parent
    created: list[Path] = []
    existing: list[Path] = []

    for target in target_paths(root):
        if target.exists():
            existing.append(target)
            continue
        if args.check:
            print(f"would create {target.relative_to(root)}")
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(source, target)
        created.append(target)

    if args.check:
        for target in existing:
            print(f"exists {target.relative_to(root)}")
        return 0

    for target in created:
        print(f"created {target.relative_to(root)}")
    for target in existing:
        print(f"kept existing {target.relative_to(root)}")

    if not created:
        print("All project skill paths already exist; nothing was overwritten.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
