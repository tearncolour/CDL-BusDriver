# SWB6115 (third-party asset): sign overlay

`SWB6115.cdlb-signs.json` gives a player's SWB6115 bus asset our LED signs without changing it (BUS_ASSET_SPEC.md section 9).
It goes beside the asset's `.cok`. The asset is not ours: its `.cok` is not in this repository and is never re-exported.

## The asset

- Source: `SWB6115.zip` from the owner, 2026-10-24. It contains `SWB6115_1d87f4e65028fdae0f08c59b834b0261.cok` (5217272 bytes) and its `.cid`.
- Prefab `SWB6115` (CarPrefab, bus, 80 passengers), one mesh, one LOD. Bounds x ±1.319, y 0.003..2.993, z −5.760..5.847.
- Doors on +X: activity locations at z 4.55, −0.76 and −4.5. The body is a textured box. The windows and signs are only in the
  texture (a 4096² virtual texture).

## How the signs were placed

- The `.Geometry` was decoded offline (zstd + meshopt): 2048 vertices, 2273 triangles.
- The base colour came from the cache's 512 px mid mip. Its 256 px sheet covers the whole atlas, about 4.5 cm a texel on the body.
- Rays were cast along each axis onto the real triangles. Each hit gives the surface position and the texel at its UV.

| Sign | Painted area found | Surface | Overlay |
|---|---|---|---|
| Front | grey-blue screen RGB (106,117,118) in a black frame at the top of the windscreen: x −0.66..0.68, y 2.48..2.685 | z 5.316 → 5.294 upwards (rake 0.107 rad) | 1.30 × 0.20 at (0.01, 2.582, 5.311), tilt 0.107, board 1.40 × 0.24 |
| Rear | same screen at the top of the rear window: x −0.58..0.60, y 2.395..2.605 | z −5.690 → −5.678 upwards (0.057 rad) | 1.14 × 0.19 at (0.01, 2.50, −5.69), tilt 0.057, board 1.24 × 0.24 |
| Side (+X) | lower pane of the first window behind the front door, split off by its own frame: z 2.69..3.63, y 1.40..1.64 | x 1.253 → 1.248 upwards (0.028 rad) | 0.85 × 0.20 at (1.256, 1.52, 3.16), tilt 0.028, board 0.92 × 0.24 |

- The dots sit 6 mm outside the surface; the boards, RGB (14,14,14), lie 4 mm behind them.
- The classic matrices have square dots: 104, 96 and 68 columns × 16.

## Limits

- The edges are only as exact as the 256 px texture sheet (about ±2 cm). The full-resolution tiles were not decoded.
- No photo of this asset in game was used, and no photo of a real SWB6115 either. The side sign's place follows the asset's own
  painted pane; Shanghai buses commonly carry their side sign there.
- Not yet seen in game.

## Installed

- 2026-10-24 16:53: `Mods/CDLC.Bus.SWB6115` = the original `.cok` and `.cid` (byte-identical to `SWB6115.zip`), this overlay and a
  `mod.json` (`isCodeMod: false`, 1.0.0). It needs BusDriver 0.12.15 or later.
- Cold start at 16:55 in a visible window: BusDriver 0.12.15 loaded, Player.log had no exception, error or FATAL lines.
- Player.log warns `Duplicate prefab ID: CarPrefab:SWB6115` and `RenderPrefab:SWB6115 Mesh`. This is the asset's own
  `ObsoleteIdentifiers`, which list each prefab's current ID (PrefabSystem warns when it registers them). The prefab loads once.
  No other copy exists on this PC; subscribed mods 101948 and 131252 show the same warning.
- The overlay is read only in a city (an AI bus seen or the bus driven): look for `Sign overlay SWB6115` in `Logs/CDLC.BusDriver.Mod.log`.
