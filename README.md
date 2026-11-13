# CSL Driving Life : Bus Driver

`0.9.25`

Drive your city's bus lines yourself. Requires **CSL Driving Life Core** (formerly Beta Test Driving Mod, 0.6.0 or later), which does the driving.

## How it works

1. Open the bus panel (toolbar bus button or **Alt+B**) and pick a line.
2. **From depot**: a new bus leaves the line's depot for you, and you take the wheel as soon as it is out on the road.
   **Take over**: drive the bus of that line nearest to the camera.
3. Drive the route. During a shift (only then) the line is highlighted, the boarding zone of every stop of your line is painted on the road (the next stop bright yellow) and the line's path to the next stop is drawn as a dashed line. The bar at the top shows the next stop, its distance and direction, and your passengers.
4. Stop inside the zone (anywhere along it, either way round: the bus's middle and front door have to be in it) and press **F**: the doors open and the game's own residents get off and on. The bar at the top counts the riders still to board. Press **F** again to close once everyone walking up is aboard, or hold **F** to close at once. Stopping at another stop of the line skips the stops in between.
5. Every boarding passenger pays the line's fare, which the game books into the city budget (public transport fees). The panel adds up the fares of your shift.
6. **End shift** in the panel (or leave the bus with the driving mod's key) and the bus carries on as usual.

Buses in the game have no door animation; the door state is shown in the bar at the top and on the boarding zone.

## Keys

| Key | Pad | |
|---|---|---|
| **Alt+B** | – | Bus panel (anywhere in game) |
| **F** | X / Square | Doors: tap to open or close, hold to close at once (on a shift) |
| **Q** / **E** | LB / RB | Left / right indicator, the same key again switches it off; cancels itself when the wheel comes back after a turn (any bus you drive) |
| **Z**, or **Q** + **E** together | LB + RB together | Hazard warning lamps (any bus you drive); a pad button of their own can be bound in the options |
| **L** | D-pad left | Headlights: auto, low beam, high beam, off (our own buses) |
| **K** (hold) | D-pad right | Flash the high beam, whatever the switch says |
| **I** | D-pad up | Immersive mode (on a shift) |

If you changed any of the earlier default keys (doors E, right indicator R), your keys stay as they are: press **Reset keys** in Options -> Bus Driver -> Keys for this layout.

## Settings

Options -> Bus Driver: boarding zones, zones at every stop, route line, shift bar, headlights, keys.

All texts are in `lang/*.json` (`CDLC.BusDriver.*` keys) and go through the game's localization, so language packs can translate them.

## Build

`dotnet build CDLC.BusDriver.csproj -c Release` is the official toolchain's build and deploys to the local `Mods\CDLC.BusDriver` (deleting it first); `-p:LocalModsPath=<folder>` deploys to
`<folder>\CDLC.BusDriver` instead. It needs the built Core and Assets (`-p:DrivingModPath`, `-p:AssetModPath`). See `BUILDING.md`. Tests: `node Tests/ui-smoke.mjs .`

## Licence

The source code of this mod is licensed under the **GNU Affero General Public License, version 3** (see [LICENSE](LICENSE)); decided by the
author on 18 October 2026, for the version that first ships the LICENSE file and later ones. Third-party parts keep their own licences, listed in
[NOTICE.md](NOTICE.md): the modelling toolkit and the sound-rule code taken from openOMSI are MIT, the LED font is under the SIL Open Font
License 1.1, the recorded sounds are CC0 and the rest were synthesised by the author. The licence of the vehicle packs (`CDLC.Bus.*`) is set in
those packs.

## Credits, licences and marks

Third-party code, sounds, fonts and the text-to-speech voices (the voices are an optional download with their own licences, one of
them research-only) are listed with their licences in [NOTICE.md](NOTICE.md). The buses are approximations modelled after real
vehicles; the names, logos and liveries on them belong to their owners, and this mod is not made, approved or endorsed by them.
