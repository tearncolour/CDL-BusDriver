# MeshKit cabin export

This standalone .NET 10 utility builds the existing `BusInterior` CPU meshes without loading the game or UnityEngine. It exports all three MeshKit LODs for selective reuse by the Blender model builder. The source files are linked from `Interior/Kit` and `Interior/Models`, so the exporter uses the actual mod geometry and `KitPoseBuffer` transforms.

Run from the repository root:

```powershell
dotnet run --project Tools/SWB6710EV01G/KitExport/KitExport.csproj -c Release -- --output ModelSource/SWB6710EV01G/kit-cabin.json
```

`Unity.Mathematics.dll` is resolved from `CSII_MANAGEDPATH`, or from the existing offline test build in `Tests/CDLC.BusDriver.Tests/bin/Release/net10.0`. An explicit DLL path is also supported:

```powershell
dotnet run --project Tools/SWB6710EV01G/KitExport/KitExport.csproj -c Release -p:UnityMathematicsPath="C:/path/to/Unity.Mathematics.dll" -- --output ModelSource/SWB6710EV01G/kit-cabin.json
```

The default requested geometry is 7.145 m long, 2.35 m wide, with axles at Z = +2.3725 and -2.7275 m, a provisional 2.9 m roof, 0.36 m saloon floor, two doors, and a left-side driver. `BusInterior` quantizes these parameters for its runtime mesh cache: both the requested and actual built specifications are included in the JSON. The actual cabin length is 7.2 m and width is 2.36 m.

Options are `--length`, `--width`, `--roof`, `--floor`, `--front-axle`, `--rear-axle`, `--doors`, `--pose` (0 closed/rest, 1 open/steered), and `--spec` (a JSON object using `BusInteriorSpec` field names). Options are applied from left to right. A spec file supplies the complete struct; missing values are not merged with the defaults.

## JSON contract

- `lods[].parts[]` contains `index`, `name`, `parent`, `meshSource`, `visible`, `flags`, `restPivot`, `partToModel`, `positions`, `normals`, `uv`, `colors`, `indices`, and `submeshes`.
- `positions` and `normals` are array-of-arrays in posed **model space**, in metres, with +X right, +Y up, and +Z forward. `partToModel` is already applied; do not apply it a second time. It remains in the export as column-major transform metadata.
- `uv` preserves the original MeshKit palette or metric UV. `colors` contains the resolved palette/flat surface color as linear RGBA per vertex. The ambient-occlusion ramp is included; light/shadow shading is not baked.
- `indices` is a flat triangle index array. `submeshes` provides `finish`, `start`, and `count` into this array.
- `palette[]` contains `index`, `name`, sRGB `rgb`, `finish`, `metallic`, and `roughness` for all MeshKit swatches.
- Shared strap meshes are expanded at every strap's posed position. Empty part/LOD meshes are omitted. Visibility is recorded separately so hidden lamps and alternate geometry can be filtered.

For the SWB6710EV01G, reuse `Dash`, `Cab`, `SteeringWheel`, `SpeedNeedle`, and `RpmNeedle` as generic detail assets. The existing bus uses a conventional door layout and rounded dimensions. Its complete shell, glass, doors, seats, poles, wheel positions, and roof equipment are **not** a verified SWB6710EV01G reconstruction. Rebuild those from the new vehicle's references. The existing VDV-style dashboard is also a detailed placeholder pending vehicle-specific interior references.
