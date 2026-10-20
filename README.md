# Presidente Dev Tools

In-game developer panel for the Presidente (HomelandSecurity) mod. Toggle it with the **PDT** button at the top left.

- **Quick:** president and public approval (set or pin), party liking, city money, and 29 political actors' moods. Moods are changed through Presidente's own event list; events can fade over 12 months or be permanent.
- **Laws:** every law, forced on or off through Presidente's WriteLaw, with no Assembly vote.
- **Explorer:** browse every Presidente system and static type, field by field. Edit any number, bool, enum, string or entity, pin values so they hold every frame, and call methods (including `out` parameters).
- **Search:** find a field by name across all systems.
- **Pins & history:** session history with undo.

Works by runtime reflection. Presidente's files are never modified. Edits change live state and are saved with the city, so keep a backup save.

Build: `./build.ps1`. Offline tests: `dotnet run --project tests/runner/Runner.csproj` (after building).
