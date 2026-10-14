# Afallon Item Tool — v0.3.0-alpha

**ALPHA · for testing only · by soybean_alien · vibe-coded with AI (Anthropic Claude + OpenAI Codex)**

An in-game window (press **F8**) that lists every item in Afallon (all 1,234 of them,
including quest items and hidden developer items) with real icons, search, filters and a
detailed preview. One click puts an item in your bag.

> **What it's for:** testing. Build out a character and try gear, builds and items
> *before* you commit to them on a real playthrough. It is not meant for normal play.
>
> **⚠ Use with caution. Your saves are not guaranteed.** This is alpha software. Back up
> your saves first (Step 1), and preferably test on a throwaway character.

---

## ✅ REQUIRED before you start

| | What | Why |
|---|---|---|
| **REQUIRED** | **Afallon** on Steam (Windows) | Tested on Early Access **0.16.3**. Other versions may not work. |
| **REQUIRED** | **[BepInEx 6.0.0-be.788 — Unity IL2CPP, Windows x64](https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip)** | The mod loader. The mod **will not run** without it. |
| | This mod: `AfallonItemTool-0.3.0-alpha.zip` | |

**About BepInEx (read this):**
- You need exactly **BepInEx 6, build #788, "Unity.IL2CPP-win-x64"**. The link above
  downloads the right file: `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip` (about 34 MB).
- **BepInEx 5 will NOT work**, and neither will the "Mono" build. Afallon is an IL2CPP game.
- All builds are listed at <https://builds.bepinex.dev/projects/bepinex_be> (look for **#788**).
- Optional check: in PowerShell run `Get-FileHash <the zip>`. It should print
  `F4CC496BD098A0DF4164B81E3737297707F13A47C2478DBA2F60EEFAB784817A`.

---

## Step 1: Back up your saves (2 minutes)

1. Close Afallon.
2. Press **Win + R**, paste the line below and press Enter:
   ```
   %USERPROFILE%\AppData\LocalLow\Stargazing interactive\Afallon
   ```
3. Copy that whole folder to your Desktop (or anywhere safe).

To restore later, close the game and copy the files back.

## Step 2: Open your game folder

1. In **Steam**, right-click **Afallon**, then choose **Manage**, then **Browse local files**.
2. A folder opens that contains `Afallon.exe`. Keep it open. Below, this is **the game folder**.

## Step 3: Install BepInEx (skip this step if it's already installed)

1. Download BepInEx using the **REQUIRED** link above.
2. Right-click the BepInEx zip, choose **Extract All…**, and extract it **into the game folder**.
3. Check it: the game folder should now contain these items **next to `Afallon.exe`**
   (not inside an extra sub-folder):
   ```
   BepInEx\    dotnet\    winhttp.dll    doorstop_config.ini    .doorstop_version
   ```
4. Start Afallon **once** and wait for the main menu.
   - The **first start takes 1–5 minutes** while BepInEx sets itself up. The game may look
     frozen; leave it alone.
   - A black console window opens beside the game. That's normal.
5. Quit the game.

## Step 4: Install the Item Tool

1. Extract `AfallonItemTool-0.3.0-alpha.zip` somewhere (your Desktop is fine).
2. Inside it there's a folder named **`BepInEx`**. Drag that `BepInEx` folder **into the
   game folder**. If Windows asks, choose **Replace / Merge**.
3. Check it: this file must exist:
   ```
   <game folder>\BepInEx\plugins\AfallonItemTool\AfallonItemSpawner.dll
   ```
   Keep everything in that folder together (both DLLs and the `fonts` folder).

## Step 5: Play

1. Start Afallon. The spawner window appears at the main menu (if it doesn't, press **F8**).
2. Load a character. Spawning only works once you're in the world.
3. Click an item, set the quantity, and press **Add**.

That's it. 🎉

---

## How to use it

| Action | How |
|---|---|
| Show / hide | **F8**, or the red **X** |
| Move | Drag the title bar (the position is remembered) |
| Search | Type a name, ID, type, rarity, class or source ("world", "vendor", "craft"…) |
| Filter | Category tabs, **Hidden / Dev**, and **Class** |
| Sort | **Sort**: Best, Level, Item power, Name, ID, or any stat |
| Best gear for a class | Pick a class, then Sort: Best |
| Preview | Click a row to see stats, level, what it does on use, and where it drops |
| Spawn | Select an item, set the quantity (−/+; Shift = ±10; Max = one stack), then **Add** |
| Currency | Gold and Corrupted emeralds go straight into your wallet (up to 999,999 per Add; **the game caps Gold at 900,000**) |
| Corruption | Gear: set +0…+30 before you press Add. Corruption Token: sets the stone's level |
| Item stats | Pick a stat and value, then **+ Add** (repeat), or **Godly**, then **Apply**. **Clear** undoes it. This changes every copy of that item while the mod is installed |
| Quest items | Allowed by default; toggle them at the bottom right. Spawning quest items can break quests |

Items are given through the game's own reward function (the same one quests use).

**Important:** never click inside the black BepInEx console. Selecting text there
**freezes the game** until you press Esc in the console.

---

## Settings (optional)

File: `<game folder>\BepInEx\config\soybean_alien.afallon.itemspawner.cfg`. It's created on
the first launch; edit it while the game is closed.

| Setting | Default | Meaning |
|---|---|---|
| `Controls.ToggleKey` | `F8` | Show/hide key |
| `Display.UiScale` | `1` | Window size multiplier (0.5–3) |
| `Display.FontName` | *(empty)* | Empty = the game's own font. `Geist` = the bundled Geist font. Or the name of another game font |
| `Safety.AllowQuestItems` | `true` | Allow quest items |
| `Safety.MaximumQuantity` | `999` | Largest amount per Add for normal items (currency allows 999,999) |
| `Inventory.InventorySlots` | `0` | Bag size (`0` = the game default of 105). **Read the warning below** |
| `Inventory.AllowShrink` | `false` | See the warning below |

### ⚠ Bag size warning
If you raise `InventorySlots` and the bag later gets smaller again (because you lowered the
setting **or uninstalled the mod**), the game **permanently deletes items in the extra
slots**. Before you lower it or uninstall, move everything into the first 105 slots.

---

## Uninstall

1. If you made the bag bigger, empty the extra slots first (see the warning above).
2. Close Afallon.
3. Delete `<game folder>\BepInEx\plugins\AfallonItemTool\`.
4. Optional: delete `<game folder>\BepInEx\config\soybean_alien.afallon.itemspawner*`.
5. Optional, to remove BepInEx completely: delete `BepInEx\`, `dotnet\`, `winhttp.dll`,
   `doorstop_config.ini`, `.doorstop_version` and `changelog.txt` from the game folder.

Items you spawned stay in your inventory. Stat changes vanish with the mod.

---

## Troubleshooting

Log file: `<game folder>\BepInEx\LogOutput.log`

| Problem | Fix |
|---|---|
| No window and no log file | BepInEx isn't installed correctly. It must be **6.0.0-be.788 IL2CPP**, with its files directly next to `Afallon.exe` |
| The first launch seems stuck | BepInEx is setting up. Wait up to 5 minutes, once |
| The game froze | You clicked in the black console. Click it and press **Esc** |
| "In-game only" | Load a character first |
| "Inventory is full" | Free some bag space |
| `UI build failed` in the log | Afallon probably updated. Wait for a new mod version |
| After an Afallon update | Delete `BepInEx\interop\` and `BepInEx\cache\`, then launch once |
| Window too small or too big | Change `Display.UiScale` (for example `1.3`) |

## Found a bug?

This is an alpha, so please report bugs on the GitHub **Issues** page. Include:
what you did, what happened, your Afallon version, and your `BepInEx\LogOutput.log`.

---

## Legal

- Afallon is developed and published by **Stargazing Interactive**. This mod is shared
  with the **explicit permission of Afallon's solo developer** (Reddit
  u/Alfred_money_pants). Thank you! That permission is not an endorsement: the mod is
  **not affiliated with, endorsed by, or supported by Stargazing Interactive** or
  Valve/Steam, so report bugs to this project, not to the developer. "Afallon" and its
  names and marks belong to their owners.
- This package contains **no Afallon game files**. Icons, names and stats are read from
  your own installed copy while the game runs.
- For **single-player, personal testing** only. Don't use it to gain an advantage over
  other players, and don't sell it.
- **Use at your own risk. No warranty. Saves are not guaranteed.** See `DISCLAIMER.md`.
- Mod code: GNU GPL v3.0 (`LICENSE`) with additional terms in `NOTICE.md`: forks and
  modified versions must credit "Afallon Item Tool by soybean_alien" and be marked as
  modified. Bundled font Geist: SIL Open Font License 1.1.
  BepInEx: LGPL-2.1, not included. See `THIRD-PARTY-NOTICES.md`.
- **AI disclosure:** this mod was vibe-coded with AI. soybean_alien directed and tested it;
  the code was written with Anthropic Claude and OpenAI Codex.
