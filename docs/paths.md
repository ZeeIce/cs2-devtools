# Path syntax

A **path** names one value inside the running game. The Explorer shows the path of everything you open; you can also type or paste one.

## Roots

| Starts with | Root | Example |
|---|---|---|
| a name | a **system** (running game or mod logic), by short name or full type name | `CitySystem`, `Game.Simulation.CitySystem` |
| `@` | a **type's static (global) fields** | `@Game.Simulation.TimeSystem` |
| `#` | an **entity**, as `index:version` | `#1234:1` |

If two systems share a short name, use the full name (the error message tells you when).

## Steps

| Step | Meaning | Example |
|---|---|---|
| `.name` | a field or property | `CitySystem.m_City` |
| `[n]` | item *n* of an array, list, or native container (0-based) | `SomeSystem.m_Items[0]` |
| `[#n]` | the *n*-th entry of a dictionary or set, in enumeration order | `SomeSystem.m_Map[#2]` |
| `.Component` (after `#…`) | a component of the entity | `#1234:1.Building` |

Steps chain: `#1234:1.Renter[0].m_Renter`.

## Editing

Only simple values are editable:

| Type | Write it as |
|---|---|
| Number | `12`, `-3`, `0.25` (whole numbers only for integer fields) |
| On/off | `true` / `false` (or `1` / `0`) |
| Text | the text itself; `null` for empty |
| Option (enum) | its name, e.g. `Full`; flags as `A|B`; or its number |
| Entity reference | `1234:1` |

Edits inside structs and components are written back automatically, so `#1234:1.Building.m_Flags` really changes the building.

## Pins

A pin re-applies a value every other frame. Pins on system and static paths are saved per city (`ModsData/CS2DevTools/pins.json` in the game's data folder) and restored when that city loads. Pins on entity paths (`#…`) are for the current session only, because entity numbers change when a save loads.

## API operations

`Api.Handle(json)` accepts `{ "op": …, … }` and returns `{ "ok": true, "result": … }` or `{ "ok": false, "error": … }`.

| op | Arguments | Result |
|---|---|---|
| `status` | — | ready flag, assemblies, whether Presidente is loaded |
| `roots` | `filter`, `assembly`, `statics`, `offset`, `limit` | systems (and static types) |
| `inspect` | `path`, `offset`, `limit`, `properties` | the value and its children |
| `entities` | `all` (component names), `none`, `filter`, `offset`, `limit` | matching entities |
| `selected` | — | the entity selected in the game |
| `set` | `path`, `value` | old and new value *(write)* |
| `pin` / `unpin` / `unpinAll` | `path`, `value` | *(write)* |
| `methods` | `path`, `filter` | callable methods with ids |
| `invoke` | `path`, `id`, `args` | return value and `out` values *(write)* |
| `search` | `query`, `mode` (`name`/`value`), `scope`, `assembly`, `depth`, `limit` | matches |
| `history` / `undo` | — | change history / revert last edit *(undo is a write)* |
| `quick`, `mood`, `clearMoods`, `laws`, `forceLaw`, `money` | see `src/DevToolsUISystem.cs` | Presidente plugin *(writes where they change state)* |
