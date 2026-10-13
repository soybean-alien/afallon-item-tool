"""Fail-closed scanner for an Afallon Item Tool release directory or ZIP."""

from __future__ import annotations

import argparse
from pathlib import Path, PurePosixPath
import sys
import zipfile

FORBIDDEN_NAMES = {
    "assembly-csharp.dll", "gameassembly.dll", "global-metadata.dat", "unityplayer.dll",
}
FORBIDDEN_PARTS = {"cpp2il", "decompiled", "interop", "game_data", "afallon_data"}
FORBIDDEN_SUFFIXES = {
    ".assets", ".bank", ".bnk", ".bundle", ".pak", ".ress", ".resource",
    ".unity3d",
}
FORBIDDEN_SOURCE_SUFFIXES = {".cs", ".cpp", ".h", ".hpp", ".il"}


def normalized_parts(name: str) -> tuple[str, ...]:
    return tuple(part.casefold() for part in PurePosixPath(name.replace("\\", "/")).parts)


def violation(name: str) -> str | None:
    path = PurePosixPath(name.replace("\\", "/"))
    parts = normalized_parts(name)
    leaf = path.name.casefold()
    suffix = path.suffix.casefold()
    if leaf in FORBIDDEN_NAMES:
        return "known game/generated binary"
    if any(part in FORBIDDEN_PARTS for part in parts):
        return "forbidden generated/decompiler directory"
    if suffix in FORBIDDEN_SUFFIXES:
        return "game asset/container extension"
    if suffix in FORBIDDEN_SOURCE_SUFFIXES:
        return "source file is not permitted in the binary release archive"
    return None


def entries(target: Path) -> list[str]:
    if target.is_dir():
        return [str(path.relative_to(target)).replace("\\", "/")
                for path in target.rglob("*") if path.is_file()]
    if target.is_file() and target.suffix.casefold() == ".zip":
        with zipfile.ZipFile(target) as archive:
            return [info.filename for info in archive.infolist() if not info.is_dir()]
    raise ValueError("Target must be a directory or .zip archive.")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("target", type=Path)
    args = parser.parse_args()
    try:
        names = entries(args.target.resolve())
    except (OSError, ValueError, zipfile.BadZipFile) as error:
        print(f"ERROR {error}", file=sys.stderr)
        return 2
    failures = [(name, reason) for name in names if (reason := violation(name))]
    for name, reason in failures:
        print(f"BLOCK {name}: {reason}")
    if failures:
        print(f"RESULT blocked ({len(failures)} violation(s), {len(names)} file(s) scanned)")
        return 1
    print(f"RESULT pass ({len(names)} file(s) scanned)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
