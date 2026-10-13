"""Dependency-free tests for verify_release_contents.py."""

from verify_release_contents import violation

CASES = {
    "AfallonItemSpawner.dll": None,
    "README.md": None,
    "config/default.cfg": None,
    "GameAssembly.dll": "known game/generated binary",
    "data/global-metadata.dat": "known game/generated binary",
    "BepInEx/interop/Assembly-CSharp.dll": "known game/generated binary",
    "decompiled/InventoryManager.cs": "forbidden generated/decompiler directory",
    "icons/sword.assets": "game asset/container extension",
    "src/OriginalPlugin.cs": "source file is not permitted in the binary release archive",
}

failures = []
for name, expected in CASES.items():
    actual = violation(name)
    if actual != expected:
        failures.append(f"{name}: expected {expected!r}, got {actual!r}")
    else:
        print(f"PASS {name}")
for failure in failures:
    print(f"FAIL {failure}")
print(f"RESULT {len(CASES) - len(failures)}/{len(CASES)} passed")
raise SystemExit(1 if failures else 0)
