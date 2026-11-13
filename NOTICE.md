# Notice

CSL Driving Life : Bus Driver is an add-on for CSL Driving Life Core.

## Licence of this mod

The source code of this mod is licensed under the GNU Affero General Public License, version 3 (`LICENSE`). Code and data taken from other
projects are under their own licences, given below (MIT for the modelling toolkit and the sound-rule code, OFL 1.1 for the LED font, CC0 for the
recorded sounds); MIT permits including them in an AGPL work as long as their notices are kept, which is why those notices are reproduced in
this file.

## Credits

- Procedural modelling toolkit (MeshKit, `Interior/Kit`): written for the bridge works of the author's Realistic Road Works fork
  (Realistic Road Works by Jasperaelvoet, MIT), copied here with additions for vehicle interiors. Its MIT notice is under
  "Third-party licences" below.
- Sound model (`Systems/BusSoundModel.cs`): the rules for volume curves, conditions, pitch and triggers follow OMSI's sound.cfg as
  documented and implemented by openOMSI (https://github.com/openOMSI-org/openOMSI, formerly turbo-devv/openOMSI), with
  `docs/FORMATS.md` and `crates/omsi-audio/src/soundset.rs` read while it was written. It is a separate C# implementation, but its
  curve interpolation is the same expression as openOMSI's `curve()`, so it is credited, not described as an independent work.
  openOMSI was MIT-licensed until 14 October 2026 and is AGPL-3.0-only since. The parts followed (the curve interpolation, the
  condition relation numbering, the pitch floor at 100 Hz, the volume a triggered sound keeps, the play-once state) are present
  unchanged in the last MIT-licensed snapshot (commit `3d03018`, 13 October 2026; checked 18 October 2026), and are used under
  that MIT licence, not under the AGPL; the notice is under "Third-party licences" below. No other openOMSI code and no OMSI
  content is included.
- Door sounds: "Bus doors open-close" by Jedo (https://freesound.org/s/396811, CC0), trimmed, mixed to mono, resampled to
  44.1 kHz, high-pass filtered and normalised.
- Bus sounds (0.6.0): "Bus stop button" by Aleksanterisuuri (https://freesound.org/s/674352, CC0), the bell cut out of the
  recording and high-pass filtered (`bus_stop_request.wav`); "bus_door_alarm" (https://freesound.org/s/379375, CC0, `bus_door_warning.wav`)
  and "bus_ticket machine" (https://freesound.org/s/379380, CC0, `bus_validator.wav`) by 13FPanska_Sychra_Petr; all resampled to
  44.1 kHz mono and normalised.
- Synthesised by the author for this mod, no third-party material: the indicator relay (`bus_relay_on.wav`, `bus_relay_off.wav`),
  the card reader's beeps (`bus_card_tap.wav`, `bus_qr_scan.wav`) and the stop announcement chime (`bus_chime.wav`).
- LED screen font: GNU Unifont 18.0.01 (https://unifoundry.com/unifont/), Copyright (C) 1998-2024 Roman Czyborra, Paul Hardy,
  Qianqian Fang, Andrew Miller and others. Dual-licensed under the GNU GPL v2 or later with the GNU font embedding exception,
  and the SIL Open Font License 1.1; used here under the OFL (`Fonts/OFL-1.1.txt`). `Fonts/led-font.bin.gz` is the Basic Latin,
  Latin-1, CJK punctuation, fullwidth and CJK Unified (incl. Extension A) glyphs of `unifont_all-18.0.01.hex`, repacked as
  bitmaps; the glyphs themselves are unchanged.

## Spoken announcements (optional download, not part of this mod)

The mod contains neither the Piper engine nor any voice. The option "Install the open-source voice" downloads them when the
player clicks it (the engine from https://github.com/rhasspy/piper, MIT; the voices from the rhasspy/piper-voices release v1.0.0 on
Hugging Face or its mirror) and runs the engine as a separate process. The voices have their own terms, taken from their model
cards (read 18 October 2026). Every voice the mod can fetch is fine-tuned from the English lessac voice:

| Voice (locale) | Dataset licence | Note |
|---|---|---|
| en_US lessac | Lessac Technologies, Blizzard 2013: **research purposes only** (https://www.cstr.ed.ac.uk/projects/blizzard/2013/lessac_blizzard2013/license.html) | The voice others are fine-tuned from; used for English and as the fallback |
| zh_CN huayan | **Unknown** (https://github.com/PlayVoice/HuaYan_TTS) | Used for Chinese |
| ru_RU irina | **Unknown** (https://github.com/RHVoice/RHVoice) | |
| fr_FR siwis | CC BY 4.0 | |
| de_DE thorsten, es_ES davefx, pl_PL darkman, pt_BR faber | CC0 | |

The player downloads these voices for their own use and is responsible for their terms; the option's description in the game says
so for lessac and huayan. The other announcement voices (Azure, Windows, chime only) need no download. Nothing here grants any
right to the voices.

## Vehicles, liveries and marks

The buses are modelled after real vehicles (Sunwin SWB6710EV01G and SWB6109EV12G, CRRC K18), using the makers' published photos and
drawings and operators' liveries as modelling reference only; no reference photo is shipped. They are approximations, not
the makers' models. The logos on them (Sunwin, CRRC) were traced from the makers' published artwork and the names, marks and
liveries belong to their owners. This mod is not made, approved or endorsed by Sunwin, CRRC or any operator, and no
permission from them is claimed. If an owner objects to a mark or livery, the affected vehicle pack will be changed or removed.

## Third-party licences

MIT licence, for the Realistic Road Works toolkit (MeshKit) and for the openOMSI snapshot named above:

```
Copyright (c) 2026 Jasper Aelvoet (Realistic Road Works)
Copyright (c) 2026 usonskyyyy (openOMSI, snapshot 3d03018 of 13 October 2026)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The LED font is under the SIL Open Font License 1.1 (`Fonts/OFL-1.1.txt`).
