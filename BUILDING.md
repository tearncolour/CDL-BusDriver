# Building

CSL Driving Life : Bus Driver is built the way the game's official modding toolchain builds a mod. `CDLC.BusDriver.csproj` imports the toolchain's `Mod.props` and `Mod.targets` (from `CSII_TOOLPATH`) and
overrides none of their targets. The project file follows the official mod template (ColossalOrder.ModTemplate 1.0.0): `Configurations`, `PublishConfigurationPath`, the two imports
after the first `PropertyGroup`, references by name (the toolchain's assembly search path holds the game's Managed folder), and the three publish profiles in
`Properties/PublishProfiles`.

## Prerequisites

- The modding toolchain, installed from the game (Options, Modding). It sets the user-level environment variables `CSII_TOOLPATH`, `CSII_MANAGEDPATH`, `CSII_MSCORLIBPATH`,
  `CSII_USERDATAPATH`, `CSII_LOCALMODSPATH`, `CSII_UNITYMODPROJECTPATH`, `CSII_ENTITIESVERSION`, `CSII_MODPOSTPROCESSORPATH` and `CSII_MODPUBLISHERPATH`. `Mod.props` reads them at
  user level; a value in a shell is not used (and can be stale), so the build needs no variable set by hand.
- The .NET SDK.

## Build

```powershell
dotnet build CDLC.BusDriver.csproj -c Release -p:DrivingModPath=<Core's bin\Release\net48> -p:AssetModPath=<Assets' bin\Release\net48>
```

What the toolchain does with it: checks its paths; compiles with the Entities source generators (a system class has to be `partial`, as the template's example system is); runs the mod
post-processor on the dll (IL post-processing, and the Burst libraries for Windows, macOS and Linux: `CDLC.BusDriver_win_x86_64.dll`, `CDLC.BusDriver_linux_x86_64.so`,
`CDLC.BusDriver_mac_x86_64.bundle`, which are part of the output); then **deploys the output folder (`bin\Release\net48`) to `Mods\CDLC.BusDriver`, deleting that folder first**.

## Building without touching an installed mod

`-p:LocalModsPath=<folder>` makes the same build deploy to `<folder>\CDLC.BusDriver` instead. Use it whenever the game is running, an installed version has to stay, or a build is only to be
checked. A normal build also removes everything in `Mods\CDLC.BusDriver` that the build does not produce: the review and probe output of the authoring tools (`review-results\`, `vehicle-probe-*\`, `review-session.json`) and the request and result files (`pack-request.txt`, `pack-result.txt`) are written into the mod's folder and are deleted with it. Move what is to be kept out of it first.

## What a build contains

The output folder is the mod: the dll, the pdb, the native libraries, `mod.json`, `LICENSE`, `NOTICE.md`, and what the project copies (`lang`, `Audio`, `Fonts`, `CDLC.BusDriver.mjs`, `CDLC.BusDriver.css`, and the built-in bus as a vehicle package, `Asset\CDLCBus12.cok` with its `.cdlg.json`). A file the code reads
from the mod's own folder has to be one of those, or tracked in git so that a build of a commit has it. `Fonts/card-font.bin.gz` (the baked card-board face the card signs read at run time) is tracked for that reason (it was untracked until this branch, so a build of a commit lacked it and the card boards fell back to LED signs). It is made offline from Noto fonts (SIL OFL 1.1) by `Tools/CardFont/make_card_font.py`, a script that is not in the repository yet, only in the author's working tree.

The mod is compiled against the Core and the Assets it runs with, which are separate mods and never bundled: `DrivingModPath` and `AssetModPath` name the folders holding `CDLC.dll` and
`CDLC.Asset.dll` (defaults: the standard build output of the sibling projects). `-p:Authoring=false` builds the player dll without the packer and the in-game probes (a different binary: it needs its
own cold-start check). The built-in bus's package is made by the game's packer, see `BUS_ASSET_SPEC.md` and `Tools/VehiclePack`.


## Texts

Every text a player sees or hears is in `lang/<locale>.json` (en-US and zh-HANS, embedded in the dll and also shipped in `lang\` for translators and language packs). The code holds ids:

- `CDLC.BusDriver.mjs`: `t("UI.Id", args...)`; the helper shows the id when no language has a text for it. A text call with a text of its own (`t("UI.Id", "English")`) fails the tests.
- C#: `BusLocale.Text(key)` (the active language, else the English file, else the key); `BusLocale.English(key)` is the English line under a Chinese text on a two-language sign or board; `BusLocale.In(locale, key)` is a text in one language (the wrap manual and the template's guide hold both).
- The tests read the language files of their own tree (`Tests/CDLC.BusDriver.Tests/LangPack.cs`).
- A name the game or a model supplies (a surface, a vehicle kind, a camera) is data: it is the argument that is shown when no language has a text for the id.
- A text given to the game's UI is `LocalizedString.Id(id)`; `IdWithFallback` fails the tests.
- Log lines, exceptions and the authoring tools' output are for whoever reads the log and stay English in the code.

## Publishing

`dotnet publish CDLC.BusDriver.csproj -c Release -p:PublishProfile=PublishNewVersion` (or `PublishNewMod`, `UpdatePublishedConfiguration`) runs the toolchain's ModPublisher on
`Mods\CDLC.BusDriver`, the deployed folder, with `Properties/PublishConfiguration.xml`. `ModVersion` there is the version of the last upload; set it, the change log and the thumbnail before
a new version. Run it from the project folder (the thumbnail path is relative). It uploads: only on the owner's word.

## Tests

```powershell
dotnet run --project Tests/CDLC.BusDriver.Tests/CDLC.BusDriver.Tests.csproj -c Release -p:CDLDevKitRoot=<CDLDevKit> -p:AssetModRoot=<CDLAssetMod>
```

The test program is a plain console program, not a mod: it takes the game's Managed folder from `CSII_MANAGEDPATH` and compiles the pure parts of the mod. It finds the repository above its own
folder, or `CDLC_BUSDRIVER_REPO`. The vehicle-pack checks read `Packs/CDLC.Bus.*`, which a checkout of the repository does not contain.
