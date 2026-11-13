SWB6109EV12G independent export tools
====================================

These tools reuse the CDLC format and algorithms, not another vehicle's shell.
The main build_model.py supplies the independently modelled geometry.

Coordinate and part contract
  Blender: metres, +X door side, -Y front, +Z up.
  Unity:   metres, +X door side, +Y up, +Z front.
  CDLC has 34 parts, 0..33. Door leaves occupy 4..9; wheels 19..22.
  Keep actual hinges named by door_leaves in model-spec.json. They are never
  relocated by the rig. Four wheel bases are named WheelFL/FR/RL/RR pivot.
  Imported animated instruments use MeshKit SteeringWheel/SpeedNeedle/RpmNeedle.

Required model-spec.json fields
  length_m, width_m, body_roof_m, front_overhang_m, rear_overhang_m,
  front_track_m, rear_track_m, wheel_radius_m, tyre_half_width_m,
  tyre_tread_depth_m, driver_eye_unity (three values), and door_leaves.
  Each door leaf: {"part": 4, "pivot": "Door1A hinge", "open_degrees": 86}.
  Optional: floor_height_m, door_count, kit_offset_blender (default [0,0,0]),
  runtime_lod_ratios (default [1,.32,.085]), runtime_dynamic_signs (true).
  Dimensions and door locations must come from this vehicle's research.

Functions executed from the main build's namespace
  prepare_animation_rig(MASTER, scene, SPEC)
  collect_cdlc_parts(MASTER)
  prepare_livery_uv(MASTER, L, W, SPEC['body_roof_m'])
  check_wheel_clearance(source_objects, flatten_object, AXLES, R,
      SPEC['front_track_m'], SPEC['rear_track_m'],
      half_width=SPEC['tyre_half_width_m'],
      tread_depth=SPEC['tyre_tread_depth_m'],
      rear_dual_offset=SPEC.get('rear_dual_offset_m',0))
  write_cdlc_runtime(source_objects, collect_cdlc_parts(MASTER),
      flatten_object, ROOT, SPEC, normal_callback=your_front_normal_function)

flatten_object returns a new disposable Blender mesh in world coordinates.
The writer transforms it to the part's canonical rest coordinates.
Objects with front_common_surface require the provided analytic normal callback.
Objects with preserve_lod, and structural part 0, keep their source topology.

Material/object metadata
  material: livery_control, glass, light_id (0..9).
  object: cdlc_part, bone (Root/WheelFL/WheelFR/WheelRL/WheelRR), preserve_lod.
  cdlc_last_lod explicitly omits a whole object after that LOD. Wheel solids
  retain closed topology; tread rings use 0 and wheel nuts use 1, so lower
  detail removes whole decorative pieces before preserve_lod is considered.
  cdlc_part_by_material is a JSON map from material slot to target part,
  for example {"0":18,"1":0} for painted outside and fixed-colour inner skin.
  Static lamp material routing takes precedence over that generic mapping.
  Optional shared tail/brake lens: cdlc_tail_brake_shared=true; an explicit
  cdlc_brake_overlay_offset_blender can specify its outward direction.
  Author interior structural liners explicitly as part 0. Fixed white markings
  remain Exterior 16; only all-controlled-paint objects become Livery 18.

Build and verification
  build.ps1 compiles/runs KitExport, invokes the main Blender build, then checks
  saved-source geometry, plate/curved-line details and wheel poses, asset maps
  and all three runtime LODs. It does not
  install game files. verify_source.py accepts an explicit --blend and --report
  for independent intermediate snapshots. It preserves raw material-interface
  edge counts and verifies mirror assemblies together before classifying gaps.
  KitExport reads this model's specification; by default it exports only generic
  cabin fittings. --all-parts is a diagnostic option, not a verified body layout.
  The source and binary tyre tests use the full specified tread envelope and
  0, +/-15, +/-25, +/-35 degree front-wheel poses with zero radial tolerance.
  Rear dual tyres are separate collision cylinders sharing their axle pivots.
  verify_saved_wheels.py repeats this against the final saved master and binds
  wheel-clearance.json to its exact source hash without saving the .blend.
  After export, verify_native.py runs all six FBX files through the installed
  game PipelinePlugin reader, verifies the five bone positions against this
  model specification, and records the unchanged input hashes. --probe and
  --plugin-dir configure another workstation's external reader installation.
  After all five vehicle reports pass, validation_manifest.py checks their
  source/runtime/asset hashes agree and writes validation-manifest.json.
  --extra-report can bind additional completed visibility or livery reports.
  reexport_saved.py can regenerate runtime/FBX from an existing saved master
  after protecting interior LOD topology; it records unchanged MASTER geometry
  fingerprints. Use the full build for edits to the model's construction code.
  build.ps1 runs that native check and manifest automatically when its probe
  and PipelinePlugin.dll are available. Override -NativeProbe and
  -NativePluginDirectory for other installations; -SkipNative omits it.
  If the native reader is unavailable or skipped, native-validation.json says
  NOT_RUN and validation-manifest.json says INCOMPLETE, never PASS.

Custom colour variant (run from the repository root)
  & 'D:\SteamLibrary\steamapps\common\Blender\blender.exe' --background --python-exit-code 1 --python Tools/SWB6109EV12G/apply_livery.py -- --color 245DB7 --output out/SWB6109EV12G-blue.blend
  HEX input is sRGB and is converted to Blender linear colour. The master is
  protected from overwrite; use a separate out/ path for variant renders too.

Isolated tool-contract fixtures are recorded in out/swb6109-tool-smoke; those
fixture reports do not substitute for a vehicle audit. Vehicle reports carry
the exact tested source/binary hashes. Intermediate snapshots may also be kept
under out/swb6109-tool-smoke while the geometry is being reviewed.
