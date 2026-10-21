# CS2 Dev Tools

**An in-game inspector and editor for Cities: Skylines II.** Look inside the running game — the base game, every mod you have installed, and every building, citizen, vehicle and road in your city — and change values while you play.

Think of it as a "developer console with a UI": you click through the game's internals like folders, see live values, and edit them.

> ⚠️ **Dev Tools changes your live game.** Changes are saved with your city when you save. Keep a backup save before experimenting, and only change things you understand.

---

## What can I do with it?

| You want to… | How |
|---|---|
| See everything about the building you just clicked | Click it in the game, then **Inspect in Dev Tools** in its info window |
| Find every building of some kind | **Entities** tab → `Building, ResidentialProperty` |
| Find where the game stores a number you see on screen (say, 97) | **Search** tab → **By value** → `97` |
| Find a setting by name | **Search** tab → **By name** → e.g. `Isolation` |
| Change a value | Type the new value next to it → **Set** |
| Keep a value fixed, even if the game recalculates it | **Pin** it — pins are re-applied every frame and restored when you reload the city |
| Undo a change | **Pins & history** tab → **Undo last edit** |
| Run one of the game's or a mod's internal functions | Open a system → **Methods** → fill in the arguments → **Call** |
| Control the Presidente mod (approval, moods, laws, money) | **Presidente** and **Laws** tabs (shown only when Presidente is installed) |

---

## Installing

**Requirements:** Cities: Skylines II 1.6 or newer. Works with any other mods; none are required.

1. Download `CS2DevTools.dll` and `CS2DevTools.mjs` (from the Releases page, or [build them yourself](#building-from-source)).
2. Create this folder and put both files in it:
   ```
   %USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\CS2DevTools\
   ```
3. Start the game and load a city. A **DEV** button appears at the top-left of the screen.

To uninstall, delete the `CS2DevTools` folder. The mod never changes game files or other mods.

---

## Using the panel

Click **DEV** to open or close the panel.

### Explorer
Browse the game like a file tree.

- **Roots** lists every *system* (the game's and each mod's running logic). Click a mod name to show only that mod, or turn on **Statics** to also see global settings.
- Click any blue name to open it. Use the breadcrumb at the top, or **Up**, to go back.
- **Inspect selected** opens whatever you last clicked in the game world. Every object's info window also gets a small **Dev Tools** row with an **Inspect in Dev Tools** button that does the same.
- Values that point to another object (shown as **→ name**) can be clicked to jump there.
- Editable values have a box, **Set** and **Pin**. On/off values also have **Toggle**.
  - Numbers: `12`, `0.5`
  - Options (enums): type the option name, e.g. `Full`. For multi-choice flags use `A|B`.
  - Objects: `1234:1`
- **Props** also shows computed properties (this runs the game's code for each one, so leave it off unless you need it).
- **Methods** lists the object's functions; you can call those whose arguments are simple values.

### Entities
Everything in your city — each building, citizen, car, road, company — is an **entity** made of **components** (pieces of data such as `Building`, `Renter`, `Citizen`).

- Type component names separated by commas, e.g. `Building, ResidentialProperty`, and press **Find**.
- Optionally exclude components (`without`) or filter by asset name.
- Click a result to open it in the Explorer and see/edit all of its components.

### Search
- **By name** finds fields whose name contains your text.
- **By value** finds fields whose current value matches — numbers (`97`, `0.25`) or text (`Tariff`).
- **Scope** limits where to look: blank = every system, `statics` = global settings, or a path (like `#1234:1` for one building).

### Pins & history
- Pins are saved per city and come back when you reload it. Pins on individual entities last only for the current session, because the game renumbers entities when a save loads.
- History lists every change made this session; **Undo last edit** reverts the most recent one.

### Presidente tabs
Shown only if the [Presidente](https://mods.paradoxplaza.com/) mod (HomelandSecurity) is installed:
- **Presidente:** set or pin approval, add temporary or permanent mood changes to any of the 29 political groups, set city money.
- **Laws:** turn any law on or off directly, without an Assembly vote.

---

## Paths (for power users)

Every value has a **path** you can paste into the Explorer box. See [docs/paths.md](docs/paths.md) for the full syntax. Quick examples:

| Path | Meaning |
|---|---|
| `CitySystem.m_City` | A field on the game's `CitySystem` |
| `@Game.Simulation.TimeSystem.kTicksPerDay` | A static (global) field — note the leading `@` |
| `SomeSystem.m_List[3]` | Item 3 of a list |
| `SomeSystem.m_Map[#2]` | The third entry of a dictionary |
| `#1234:1.Building.m_Flags` | Entity 1234 (version 1), its `Building` component, field `m_Flags` |
| `#1234:1.Renter[0].m_Renter` | The first element of that entity's `Renter` list |

---

## Is it safe?

- **Nothing on disk is modified** — no game files, no mod files. Dev Tools only changes values in memory while the game runs.
- **Saving stores your changes** in that city's save, like any other in-game change.
- **Some edits can break a city** (for example, pointing a building at an entity that doesn't exist). Use backups, pin carefully, and prefer **Undo** over guessing.
- **Other mods are never required.** Dev Tools discovers what's loaded at runtime and does not depend on any mod being present.

---

## For mod authors and tool builders

Other mods can drive Dev Tools without referencing it at compile time:

```csharp
var api = AppDomain.CurrentDomain.GetAssemblies()
    .FirstOrDefault(a => a.GetName().Name == "CS2DevTools")?.GetType("CS2DevTools.Api");
string json = (string)api.GetMethod("Handle").Invoke(null, new object[] {
    "{\"op\":\"inspect\",\"path\":\"CitySystem\"}" });
```

- `Api.Handle(json)` — takes the same requests the panel sends and returns `{ok, result|error}`. Call it on the main thread.
- `Api.IsWrite(op)` — `true` for operations that change state (`set`, `pin`, `invoke`, …), so hosts can gate them.
- Operations: `status`, `roots`, `inspect`, `entities`, `selected`, `set`, `pin`, `unpin`, `methods`, `invoke`, `search`, `history`, `undo`, plus the Presidente ones. See [docs/paths.md](docs/paths.md#api-operations).

The [Cities II Agent Bridge](https://github.com/ZeeIce/cities2-agent-bridge-ndc) exposes this as its `devtools` command.

---

## Building from source

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (6 or newer) and an installed copy of Cities: Skylines II.

```powershell
./build.ps1                                   # finds the game automatically
./build.ps1 -GamePath "D:\Games\Cities Skylines II"   # or point it at the game
dotnet run --project tests/runner/Runner.csproj       # offline tests (after building)
```

The build compiles against the game's own assemblies (no Unity project, no NuGet packages) and writes `artifacts/CS2DevTools.dll` and `artifacts/CS2DevTools.mjs`. The panel is plain JavaScript using the game's built-in React — no bundler needed.

### How it works
- **Engine** (`src/Engine.cs`): path-based reflection over every loaded non-framework assembly; ECS entities through `EntityManager` (components via `GetComponentData`/`SetComponentData`, buffers via `AsNativeArray`); value types are written back up the path so edits inside structs stick.
- **UI system** (`src/DevToolsUISystem.cs`): one request trigger and one response binding for the panel, pins re-applied every other frame, pins saved to `ModsData/CS2DevTools/pins.json`.
- **Presidente plugin** (`src/Curated.cs`): convenience controls built on Presidente's own methods.
- **Panel** (`ui/CS2DevTools.mjs`).

---

## FAQ

**The DEV button doesn't appear.** Check both files are in `Mods\CS2DevTools\` and that the game's `Logs\CS2DevTools.log` says "Dev Tools loaded".

**A value I set keeps changing back.** The game recalculates it every update. Use **Pin** instead of **Set**.

**"entity_not_found_or_stale".** The object was removed or the city was reloaded (entity numbers change on load). Find it again via **Entities** or **Inspect selected**.

**Why can't I edit some values?** Only simple values (numbers, text, on/off, options, entity references) are editable. Open containers to edit what's inside them.

---

See [CHANGELOG.md](CHANGELOG.md) for version history.
