# Third-party notices

## Bundled in the release package

### Geist font (Vercel)
- Files: `BepInEx/plugins/AfallonItemSpawner/fonts/Geist-*.ttf`
- License: **SIL Open Font License 1.1**. The full text ships next to the fonts as
  `Geist-OFL-LICENSE.txt`.
- Source: <https://github.com/vercel/geist-font>
- The font is used only if you set `Display.FontName = Geist`. It is unmodified and is
  not sold on its own.

## Required, but NOT included

### BepInEx 6.0.0-be.788 (Unity IL2CPP, win-x64)
- License: **LGPL-2.1**. Includes components under their own licenses (HarmonyX, MIT;
  Il2CppInterop, LGPL-3.0; Doorstop, LGPL-2.1; Cpp2IL, MIT; the .NET runtime, MIT).
- Download it yourself from <https://builds.bepinex.dev/projects/bepinex_be> (build #788).
- The mod is compiled against BepInEx's public API and links to it at runtime.

## Referenced at build time only (never redistributed)

- The Unity engine modules and the Afallon `Assembly-CSharp` interop assemblies that
  BepInEx generates on your own machine. The project's build reads them from your
  local game install. They are excluded from this repository and from the release
  package (`.gitignore` and `tools/verify_release_contents.py` enforce this).

## Afallon

Afallon and all of its code, data, names, icons and other assets are the property of
Stargazing Interactive and/or their licensors. None of them are included here. The mod
reads item names, stats and icons from the player's own installed copy while the game
runs.
