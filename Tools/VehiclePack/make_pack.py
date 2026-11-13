"""Makes a vehicle pack: the folder the game and CDLC.BusDriver load an authored bus from.

    python make_pack.py <model> [--sync] [--zip] [--require-cok]

From the repository (run from anywhere):
    Tools/<model>/vehicle.json                 cdlb-vehicle v1 (single) or v2 (explicit two-section assembly)
    ModelSource/<model>/sunwin-mesh.bin.gz     the runtime mesh (SWB1, written by the model's build)
    ModelSource/<model>/kit-cabin.json         the palette the mesh was exported against (its row count)
    Asset/<model>.cok                          the game asset (pack_asset.sh), if packed
to Packs/CDLC.Bus.<model>/:
    <model>.cok, <model>.swb1.gz, <model>.cdlb.json (the profile plus mesh, meshSha256, paletteRows), mod.json, README.txt
--sync    first refresh the profile's mirrors, side sign and doors from the build's reports (mirror-surfaces.json, side-sign.json,
          door-linkage.json: the SWB6710EV01G build writes them), rounded as the hand-written values were (centres 4 decimals,
          directions 8); the doors get their timing and, for outward-swing leaves, their linkage
--zip     also Dist/CDLC.Bus.<model>.zip (the folder, for publishing)
v2 reads pack.sections[id].meshSource and pack.jointSource relative to ModelSource/<model>,
checks both section capacities/GUIDs and rest anchors, then writes the same complete manifest
beside each exact prefab. The joint resource is separate CJA1, never a SWB1 strap part.
v2 validates and stages the complete output before replacing the previous pack.
Fails (exit 1) on any inconsistency. --sync remains a v1-only convenience.
"""
import gzip, hashlib, json, math, re, shutil, struct, sys, tempfile, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FORMAT, VERSION = 'cdlb-vehicle', 1


def plain_name(value, label):
    if not isinstance(value, str) or not re.fullmatch(r'[A-Za-z0-9_]{1,64}', value):
        fail(f'{label} must be an explicit ASCII model/section name')
    return value


def relative_source(root, value, label):
    if not isinstance(value, str) or not value or Path(value).is_absolute() or ':' in value or '\\' in value:
        fail(f'{label} must be a relative source path')
    path = (root / value).resolve()
    if not path.is_relative_to(root.resolve()) or not path.is_file():
        fail(f'{label} escapes the model source or is missing: {value}')
    return path


def output_file(value, label):
    if not isinstance(value, str) or value in ('', '.', '..') or any(c in value for c in '/\\:') or '..' in value:
        fail(f'{label} must be a plain output filename')
    return value


def finite_vector(value, size, label):
    if not isinstance(value, list) or len(value) != size or any(type(x) not in (int, float) or not math.isfinite(x) for x in value):
        fail(f'{label} must have {size} finite components')
    return value


def transformed_anchor(section, anchor):
    t = finite_vector(section.get('restPosition'), 3, 'section.restPosition')
    q = finite_vector(section.get('restRotation'), 4, 'section.restRotation')
    if abs(sum(x*x for x in q)-1) > 1e-5:
        fail('section.restRotation must be a unit quaternion')
    x,y,z,w = q
    def cross(a,b): return [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]
    c = cross([x,y,z], anchor)
    d = cross([x,y,z], c)
    return [t[i]+anchor[i]+2*w*c[i]+2*d[i] for i in range(3)]


def make_assembly(profile, model, argv):
    """Data-only v2 assembly. Validate every input before replacing any prior pack."""
    pack = profile.get('pack')
    sections = profile.get('sections')
    articulation = profile.get('articulation')
    if not isinstance(pack, dict) or not isinstance(sections, list) or len(sections) != 2 or not isinstance(articulation, dict):
        fail('v2 requires pack, two explicit sections and articulation')
    if pack.get('template') not in ('Bus01', 'Bus02'):
        fail('pack.template must be Bus01 or Bus02')
    if '--sync' in argv:
        fail('--sync is a v1 metadata operation; v2 section coordinates must come from the assembly author')
    native = pack.get('sections')
    if not isinstance(native, dict) or len(native) != 2:
        fail('v2 requires explicit pack.sections capacity/GUID/source settings')
    src = ROOT / 'ModelSource' / model
    cabin = relative_source(src, pack.get('paletteSource', 'kit-cabin.json'), 'pack.paletteSource')
    palette_rows = len(json.loads(cabin.read_text(encoding='utf-8'))['palette'])
    ids, prefabs, files, identities, resources = set(), set(), set(), set(), []
    total_capacity = 0
    shipped = json.loads(json.dumps(profile))
    for section in shipped['sections']:
        sid = plain_name(section.get('id'), 'section.id')
        prefab = plain_name(section.get('prefab'), 'section.prefab')
        if '_' in prefab:
            fail(f'section.prefab {prefab!r} must be an ASCII alphanumeric native main name; underscores are reserved importer suffixes')
        if sid in ids or prefab in prefabs:
            fail('duplicate section id or native prefab')
        ids.add(sid); prefabs.add(prefab)
        setting = native.get(sid)
        p = section.get('profile')
        if not isinstance(setting, dict) or not isinstance(p, dict) or p.get('model') != prefab:
            fail(f'{sid} lacks its explicit native settings/profile.model')
        capacity = setting.get('capacity')
        identity = setting.get('guid')
        if type(capacity) is not int or not 0 <= capacity <= 500 or not isinstance(identity, str) or not re.fullmatch('[0-9a-fA-F]{32}', identity):
            fail(f'{sid} requires capacity 0..500 and frozen 32-hex GUID')
        if identity.lower() in identities:
            fail('native section GUIDs must differ')
        identities.add(identity.lower()); total_capacity += capacity
        aliases = setting.get('obsoleteGuids', [])
        if not isinstance(aliases, list): fail('section obsoleteGuids must be a list')
        for alias in aliases:
            if not isinstance(alias, str) or not re.fullmatch('[0-9a-fA-F]{32}', alias) or alias.lower() in identities:
                fail('section aliases must be valid, unique and distinct from native identities')
            identities.add(alias.lower())
        mesh = relative_source(src, setting.get('meshSource'), f'pack.sections.{sid}.meshSource')
        mesh_name = output_file(p.get('mesh'), f'{sid}.profile.mesh')
        if mesh_name in files:
            fail('each section must have a distinct runtime resource')
        files.add(mesh_name)
        mesh_header(mesh)
        digest = sha256(mesh)
        if p.get('meshSha256') is not None and p['meshSha256'].lower() != digest:
            fail(f'{sid} runtime differs from its source-bound profile hash; regenerate the profile')
        if p.get('paletteRows') is not None and p['paletteRows'] != palette_rows:
            fail(f'{sid} palette row count differs from pack.paletteSource; refusing to rewrite source evidence')
        p['meshSha256'] = digest
        p['paletteRows'] = palette_rows
        resources.append((mesh, mesh_name))
        if not isinstance(p.get('axles'), list) or not p['axles']:
            fail(f'{sid} must explicitly declare its real axles')
        doors = p.get('doors')
        if not isinstance(doors, list): fail(f'{sid} must declare its native door list')
        for door in doors:
            point = finite_vector(door.get('boardingPosition'), 3, 'door.boardingPosition')
            rotation = finite_vector(door.get('boardingRotation'), 4, 'door.boardingRotation')
            if abs(sum(v*v for v in rotation)-1) > 1e-5:
                fail('door.boardingRotation must be a unit quaternion')
            if type(door.get('boarding')) is not bool or type(door.get('alighting')) is not bool or not (door['boarding'] or door['alighting']):
                fail('each door requires an explicit boarding/alighting policy')
            if abs(point[2] - door.get('centreZ', float('inf'))) > door.get('width', 0) / 2:
                fail('native boarding point lies outside its declared door opening')
    if set(native) != ids or total_capacity != pack.get('capacity') or total_capacity <= 0:
        fail('section capacities must sum to positive pack.capacity, with no undeclared sections')
    if {articulation.get('frontSection'), articulation.get('rearSection')} != ids:
        fail('articulation must bind both explicit sections')
    by_id = {s['id']: s for s in sections}
    anchors = [transformed_anchor(by_id[articulation[side+'Section']],
        finite_vector(articulation.get(side+'Anchor'), 3, 'articulation.'+side+'Anchor')) for side in ('front','rear')]
    if sum((a-b)**2 for a,b in zip(*anchors)) > .001**2:
        fail('rest transforms do not join the local articulation anchors within 1 mm')
    separation = articulation.get('maxSeparation')
    if type(separation) not in (int,float) or not math.isfinite(separation) or not .001 <= separation <= .25:
        fail('articulation.maxSeparation must be .001..25 metres')
    joint = shipped['articulation'].get('joint')
    if not isinstance(joint, dict) or joint.get('format') != 'cdlb-joint-v1':
        fail('v2 requires cdlb-joint-v1 geometry')
    bend = joint.get('maxBendDegrees')
    if type(bend) not in (int,float) or not math.isfinite(bend) or not 5 <= bend <= 89:
        fail('articulation joint bend limit must be 5..89 degrees')
    joint_source = relative_source(src, pack.get('jointSource'), 'pack.jointSource')
    joint_name = output_file(joint.get('mesh'), 'articulation.joint.mesh')
    if joint_name in files:
        fail('joint resource collides with a section mesh')
    with gzip.open(joint_source, 'rb') as data:
        header = data.read(8)
    if len(header) != 8 or header[:4] != b'CJA1' or struct.unpack_from('<i', header, 4)[0] != 3:
        fail('joint must be CJA1 with three LODs')
    digest = sha256(joint_source)
    if joint.get('meshSha256') is not None and joint['meshSha256'].lower() != digest:
        fail('joint resource differs from its source-bound profile hash; regenerate the profile')
    joint['meshSha256'] = digest
    resources.append((joint_source, joint_name))
    pack_icon(pack, model)
    cok = ROOT / 'Asset' / f'{model}.cok'
    if '--require-cok' in argv and not cok.is_file():
        fail(f'no game asset at {cok}')
    packs = ROOT / 'Packs'
    packs.mkdir(exist_ok=True)
    out = packs / f'CDLC.Bus.{model}'
    temporary = Path(tempfile.mkdtemp(prefix=f'.{model}-', dir=packs))
    try:
        for source, name in resources:
            shutil.copyfile(source, temporary / name)
            if sha256(source) != sha256(temporary / name):
                fail(f'resource changed while copying: {source}')
        text = json.dumps(shipped, indent=2, ensure_ascii=False) + '\n'
        # All entrances contain the exact same manifest; selection is by explicit prefab, never suffix guessing.
        for prefab in sorted(prefabs | {model}):
            (temporary / f'{prefab}.cdlb.json').write_text(text, encoding='utf-8')
        version, title = pack.get('version', '1.0.0'), pack.get('title', model)
        staging_notice = '' if cok.is_file() else (
            'SOURCE/RESOURCE STAGING ONLY — NOT INSTALLABLE OR PLAYABLE\n'
            '缺少原生 .cok；本文件夹仅供资源/配置离线检查，不可作为完整车辆安装。\n'
            'Native asset packaging and validation must complete before installation.\n\n')
        (temporary / 'mod.json').write_text(json.dumps({
            'id': f'CDLC.Bus.{model}', 'displayName': f'CDLC Bus: {title}', 'author': pack.get('author', 'MeroT'),
            'version': version, 'description': ('RESOURCE STAGING ONLY, not installable or playable. ' if not cok.is_file() else '') +
                'Articulated bus asset. Requires CDLC Bus Driver 0.9.0 or later for its public articulated-vehicle interface.',
            'isCodeMod': False, 'dependencies': ['CDLC.BusDriver']}, indent=2, ensure_ascii=False)+'\n', encoding='utf-8')
        (temporary / 'README.txt').write_text(
            staging_notice + f'{title} ({model})\n\nTwo declared sections and independent runtime meshes.\n' +
            ('Install the whole folder; keep both .cdlb.json files, both .swb1.gz files and the joint resource beside the .cok.\n' if cok.is_file()
             else 'Keep these source resources together for offline validation; do not install this staging folder.\n') +
            'The package contains no DLL. Requires CDLC Bus Driver 0.9.0 or later and its public articulated-vehicle interface. '
            'The deforming joint for driving and AI requires this dependency; '
            'native tractor/trailer motion alone does not animate the accordion.\n'
            f'Capacity {total_capacity}: '+', '.join(f'{s["id"]}={native[s["id"]]["capacity"]}' for s in sections)+'.\n'
            'Section capacity allocation is a mod configuration, not an OEM sectional capacity statement.\n'
            'Packaging does not certify game loading, articulated motion, passenger service or glass rendering.\n', encoding='utf-8')
        if cok.is_file():
            digest = sha256(cok)
            copied = temporary / f'{model}.cok'
            shutil.copyfile(cok, copied)
            if sha256(copied) != digest or sha256(cok) != digest:
                fail('native package changed during the copy')
        previous = None
        if out.exists():
            previous = Path(tempfile.mkdtemp(prefix=f'.{model}-previous-', dir=packs))
            previous.rmdir()
            out.rename(previous)
        try:
            temporary.rename(out)
        except BaseException:
            if previous is not None: previous.rename(out)
            raise
        if previous is not None: shutil.rmtree(previous)
    finally:
        if temporary.exists(): shutil.rmtree(temporary)
    if '--zip' in argv:
        dist = ROOT / 'Dist'; dist.mkdir(exist_ok=True)
        with zipfile.ZipFile(dist / f'CDLC.Bus.{model}.zip', 'w', zipfile.ZIP_DEFLATED) as z:
            for file in sorted(out.iterdir()): z.write(file, f'CDLC.Bus.{model}/{file.name}')
    print(f'MAKE_PACK_OK {out} v2 sections {len(sections)}, independent resources {len(resources)}, capacity {total_capacity}, cok {cok.is_file()}', flush=True)
    return 0


def pack_icon(pack, model):
    """pack.icon: a PNG beside Tools/<model>/vehicle.json, the asset menu icon the in-game packer puts into the .cok
    (BusVehicleAssetSystem.IconUri, reading it beside the request's profile). Checked here; not copied into the pack: the game's
    user database would index a loose PNG in the mods folder as an image asset of its own. None without one."""
    name = pack.get('icon')
    if name is None:
        return None
    if not isinstance(name, str) or not re.fullmatch(r'[A-Za-z0-9_-][A-Za-z0-9_.-]{0,62}\.png', name, re.IGNORECASE):
        fail(f'pack.icon must be a plain .png file name beside the profile, not {name!r}')
    source = ROOT / 'Tools' / model / name
    if not source.is_file():
        fail(f'pack.icon {source} does not exist')
    if source.read_bytes()[:8] != b'\x89PNG\r\n\x1a\n':
        fail(f'pack.icon {source} is not a PNG')
    return source, name


def fail(message):
    print('MAKE_PACK_FAIL ' + message, flush=True)
    sys.exit(1)


def f32(x):
    """The float the mod reads (through double, as VehicleProfileJson does)."""
    return struct.unpack('<f', struct.pack('<f', float(x)))[0]


def same32(a, b):
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(same32(x, y) for x, y in zip(a, b))
    if isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool) and not isinstance(b, bool):
        return f32(a) == f32(b)
    return a == b


def dump(value, indent=0):
    """JSON as the profiles are laid out: an object or array on one line when it fits in 150 columns, else one entry a line."""
    flat = json.dumps(value, ensure_ascii=False, separators=(', ', ': '))
    if not isinstance(value, (dict, list)) or len(flat) + indent <= 150:
        return flat if not isinstance(value, dict) or not value else '{ ' + flat[1:-1] + ' }'
    pad = ' ' * (indent + 2)
    if isinstance(value, dict):
        items = [pad + json.dumps(k, ensure_ascii=False) + ': ' + dump(v, indent + 2) for k, v in value.items()]
        return '{\n' + ',\n'.join(items) + '\n' + ' ' * indent + '}'
    return '[\n' + ',\n'.join(pad + dump(v, indent + 2) for v in value) + '\n' + ' ' * indent + ']'


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def mesh_header(path):
    """SWB1: magic, LOD count, part count, eye (VehicleMesh.Load checks the rest)."""
    with gzip.open(path, 'rb') as f:
        head = f.read(4 + 4 + 4 + 12)
    if len(head) < 24 or head[:4] != b'SWB1':
        fail(f'{path} is not an SWB1 mesh')
    lods, parts = struct.unpack('<ii', head[4:12])
    eye = struct.unpack('<3f', head[12:24])
    if lods != 3 or not 34 <= parts <= 256:
        fail(f'{path}: {lods} LODs, {parts} parts (expected 3 and 34..256)')
    return lods, parts, eye


def sync(profile, model):
    """Mirrors and the side sign from the build's own reports, as Unity coordinates."""
    src = ROOT / 'ModelSource' / model
    changed = []
    mirrors = src / 'mirror-surfaces.json'
    if mirrors.exists():
        report = json.loads(mirrors.read_text(encoding='utf-8'))
        order = {'driver side small': 0, 'door side large': 1, 'door side auxiliary': 2}
        surfaces = sorted(report['surfaces'], key=lambda s: order.get(s['id'], 99))
        if len(surfaces) != len(profile['mirrors']):
            fail(f'{mirrors.name}: {len(surfaces)} surfaces, the profile has {len(profile["mirrors"])} mirrors')
        for entry, s in zip(profile['mirrors'], surfaces):
            new = {'centre': [round(v, 4) for v in s['centre_unity']],
                   'normal': [round(v, 8) for v in s['normal_unity']],
                   'up': [round(v, 8) for v in s['v_unity']],
                   'size': [s['width_m'], s['height_m']]}
            if s.get('convex_depth_m'):
                new['convexDepth'] = s['convex_depth_m']
            for key, value in new.items():
                value = [0.0 if v == 0 else v for v in value] if isinstance(value, list) else value
                if not same32(entry.get(key), value):
                    changed.append(f'mirror {s["id"]} {key}: {entry.get(key)} -> {value}')
                    entry[key] = value
    side = src / 'side-sign.json'
    if side.exists() and profile['signs'].get('side'):
        report = json.loads(side.read_text(encoding='utf-8'))
        sign = profile['signs']['side']
        for key, value in (('centre', [round(v, 4) for v in report['led_centre_unity']]), ('tilt', round(report['tilt_rad'], 5))):
            if not same32(sign.get(key), value):
                changed.append(f'side sign {key}: {sign.get(key)} -> {value}')
                sign[key] = value
    doors = src / 'door-linkage.json'
    if doors.exists():
        report = json.loads(doors.read_text(encoding='utf-8'))
        leaves = {}
        for leaf in report['leaves']:
            leaves.setdefault((leaf['part'] - 4) // 2, []).append(leaf)
        for i, entry in enumerate(profile['doors']):
            new = dict(report['timing'].get(str(i), {}))
            if i in leaves:
                rows = sorted(leaves[i], key=lambda l: l['part'])
                if len(rows) != entry.get('leaves'):
                    fail(f'{doors.name}: door {i} has {len(rows)} linked leaves, the profile {entry.get("leaves")}')
                new['linkage'] = [l['profile_linkage'] for l in rows]
            for key, value in new.items():
                if not same32(entry.get(key), value):
                    changed.append(f'door {i} {key}: {entry.get(key)} -> {value}')
                    entry[key] = value
    return changed


def main(argv):
    args = [a for a in argv if not a.startswith('--')]
    if len(args) != 1:
        print(__doc__)
        return 2
    model = plain_name(args[0], 'model')
    tool = ROOT / 'Tools' / model / 'vehicle.json'
    if not tool.exists():
        fail(f'no profile at {tool}')
    profile = json.loads(tool.read_text(encoding='utf-8'))
    if profile.get('format') != FORMAT or profile.get('version') not in (1, 2) or profile.get('model') != model:
        fail(f'{tool} is not a {FORMAT} v{VERSION} profile of {model}')
    if profile['version'] == 2:
        return make_assembly(profile, model, argv)
    pack = profile.get('pack') or fail(f'{tool} has no "pack" block')

    if '--sync' in argv:
        changes = sync(profile, model)
        if changes:
            tool.write_text(json.dumps(profile, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
            for c in changes:
                print('MAKE_PACK_SYNC ' + c)
        else:
            print('MAKE_PACK_SYNC nothing changed')

    src = ROOT / 'ModelSource' / model
    mesh = src / 'sunwin-mesh.bin.gz'
    if not mesh.exists():
        fail(f'no runtime mesh at {mesh} (build the model first)')
    _, parts, eye = mesh_header(mesh)
    cabin = src / 'kit-cabin.json'
    if not cabin.exists():
        fail(f'no palette at {cabin}')
    palette_rows = len(json.loads(cabin.read_text(encoding='utf-8'))['palette'])
    cok = ROOT / 'Asset' / f'{model}.cok'
    if not cok.exists() and '--require-cok' in argv:
        fail(f'no game asset at {cok} (pack_asset.sh {model})')

    out = ROOT / 'Packs' / f'CDLC.Bus.{model}'
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    mesh_name = f'{model}.swb1.gz'
    shutil.copyfile(mesh, out / mesh_name)
    digest = sha256(out / mesh_name)

    shipped = {'format': FORMAT, 'version': VERSION, 'model': model, 'mesh': mesh_name, 'meshSha256': digest,
               'paletteRows': palette_rows}
    for key, value in profile.items():
        if key not in shipped:
            shipped[key] = value
    (out / f'{model}.cdlb.json').write_text(json.dumps(shipped, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')

    version = pack.get('version', '1.0.0')
    title = pack.get('title', model)
    staging_notice = '' if cok.exists() else (
        'SOURCE/RESOURCE STAGING ONLY — NOT INSTALLABLE OR PLAYABLE\n'
        '缺少原生 .cok；仅供资源/配置离线检查。下述安装说明只适用于原生打包及验收完成后的完整车包。\n'
        'Native asset packaging and validation must complete before installation.\n\n')
    (out / 'mod.json').write_text(json.dumps({
        'id': f'CDLC.Bus.{model}', 'displayName': f'CDLC Bus: {title}', 'author': pack.get('author', 'MeroT'),
        'version': version,
        'description': ('RESOURCE STAGING ONLY, not installable or playable. ' if not cok.exists() else '') + f'{title} as a city bus (a vehicle asset; no code). With CSL Driving Life : Bus Driver installed you can '
                       'also drive it from the driver\'s seat, with its full interior, doors, mirrors and LED signs.',
        'isCodeMod': False, 'dependencies': []}, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    needs = pack.get('requiresBusDriver')
    if needs is not None and not re.fullmatch(r'\d+\.\d+\.\d+', str(needs)):
        fail(f'pack.requiresBusDriver must be a version like 0.9.9, not {needs!r}')
    needs_note = (f'需要 CSL Driving Life : Bus Driver {needs} 或更新版本才能驾驶（旧版不会使用这个车包的驾驶室模型）。\n'
                  f'Driving it needs CSL Driving Life : Bus Driver {needs} or newer (an older one does not use this pack\'s cab model).\n\n'
                  if needs else '')
    (out / 'README.txt').write_text(
        staging_notice + f'CDLC Bus: {title}  ({model}, pack {version})\n\n' + needs_note +
        f'中文：把整个 CDLC.Bus.{model} 文件夹放进\n'
        '  %USERPROFILE%\\AppData\\LocalLow\\Colossal Order\\Cities Skylines II\\Mods\\\n'
        '（不要放进 .packages）。没有任何 mod 时它是一辆普通的线路公交车；装了 CSL Driving Life : Bus Driver 后，\n'
        '可以坐进驾驶室开它（完整内饰、车门、后视镜、电子路牌）。文件夹里的 .swb1.gz 和 .cdlb.json 是 Bus Driver\n'
        '用的驾驶室模型和车型资料，游戏本身不读它们，请保留在 .cok 旁边。\n\n'
        f'English: put the whole CDLC.Bus.{model} folder into\n'
        '  %USERPROFILE%\\AppData\\LocalLow\\Colossal Order\\Cities Skylines II\\Mods\\\n'
        '(not into .packages). On its own it is an ordinary line bus; with CSL Driving Life : Bus Driver you can drive it from\n'
        'the driver\'s seat (full interior, doors, mirrors, LED signs). The .swb1.gz and .cdlb.json files are Bus Driver\'s cab\n'
        'model and profile; the game does not read them. Keep them next to the .cok.\n', encoding='utf-8')
    pack_icon(pack, model)
    if cok.exists():
        shutil.copyfile(cok, out / f'{model}.cok')
    else:
        print(f'MAKE_PACK_WARN no {cok.name} yet (pack_asset.sh {model}): the pack has the cab model only')

    if '--zip' in argv:
        dist = ROOT / 'Dist'
        dist.mkdir(exist_ok=True)
        archive = dist / f'CDLC.Bus.{model}.zip'
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
            for f in sorted(out.iterdir()):
                z.write(f, f'CDLC.Bus.{model}/{f.name}')
        print('MAKE_PACK_ZIP ' + str(archive))

    print(f'MAKE_PACK_OK {out} mesh {digest[:12]} ({parts} parts, eye {tuple(round(e, 3) for e in eye)}), '
          f'palette {palette_rows} rows, cok {"yes" if cok.exists() else "no"}', flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
