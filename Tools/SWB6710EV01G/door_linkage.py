"""The iEV7's passenger doors: leaf layout, linkage design and kinematics.

Pure Python (math only): build_model.py, animation_rig.py and the checks execute it in their namespace, and it runs on its own
(``python door_linkage.py``) to print the motion. BusDriver solves the same linkage at run time (Interior/Models/DoorLinkage.cs:
a shaft's arm carries a joint on the leaf, a roller on the leaf runs in a straight rail along the body) from the profile's
doors[].linkage, which make_pack.py --sync copies from door-linkage.json.

Evidence (user review 2026-10-16): Bilibili BV1U5RNYgEyJ, Nanjing SWB6710EV03G (same iEV7 body, recorded 2025-03-01), the
closing at the stop in its last 30 s (about 380.4-382.9 s), the open doors at 0:16-0:19 and 0:56-1:02, the saloon views at
4:13-4:22; the user's corrections and sketch (two guide points on each leaf, the meeting edge sweeping outside on an arc);
the official right profile (references/right-profile-official.jpg) for the leaves' grab rails. Read from them, with their
limits (720p, about 20 distinct frames a second, a hand-held panning camera, no drawing of the mechanism):
- No hinge: each leaf runs on a track. Open, it stands about square to the body at the jamb it parks at, MOSTLY INSIDE the
  bus; only its meeting edge stands a little outside (the user: "只出来一点点"). Its inner face, with the diagonal grab rail,
  then faces the doorway for the passengers getting on and off. The front pair's A parks at the front jamb, B at the rear
  jamb, the rear single leaf at the REAR jamb.
- Closing, the meeting edge sweeps outside on an arc to the middle of the opening while the jamb end comes out to the post.
- At the stop the rear door closed first; the front pair began about 1.2 s later and took about 1.0 s, slow at both ends.
  How long a door takes to open could not be measured (the camera was panning); 1.0 s is an estimate.
The mechanism is designed here (no drawing of the real one; the video hides it in the header): a Scott Russell straight-line
linkage per leaf. A short door shaft in the header, above the leaf's jamb end when shut, turns a crank of half the guide span
to a pin on the leaf's top; a roller one guide span from the jamb end runs in a straight header rail along the body (the
sketch's circle by the body line). The jamb end then runs exactly straight into the bus (the sketch's inner circle), where a
pin under the leaf runs in a slot in the floor, keeping the leaf plumb. The leaf turns 80 / 82 degrees, not 90: at 90 the
rail's roller is at its dead point (the video cannot tell 82 from 90).

Plan coordinates are Blender's after the tumblehome (taper.py) (x across, out on the door side; y towards the rear); the
guide points are at the leaf's top, 47 mm inside its bottom (the tumblehome). Runtime (Unity) plan: (x, z) = (x, -y)
(animation_rig._RIG_COORD). An angle counter-clockwise about Blender +Z is quaternion.RotateY of the same angle.
"""
import math

DOOR_LEAF_Z = (.395, 2.300)     # bottom clear of the step tread (0.386), top under the header
DOOR_LEAF_GAP = .0115           # each leaf is 23 mm narrower than its share of the opening (seals close the gap)
DOOR_TOP_INSET = .047           # the leaf's top is this much inside its bottom (the tumblehome, taper.py)
# The header hardware stays under the black frit over the door (from 2.34 m, leaning in with the tumblehome); the cover over
# it sits inside the frit.
DOOR_CRANK_Z = (2.305, 2.313)   # the crank, just above the leaf's top, under the rail channel
DOOR_SHAFT_Z = (2.304, 2.338)   # the short door shaft, up into its bearing in the header cover
DOOR_RAIL_Z = (2.318, 2.338)    # the roller's rail channel along the header, open at the bottom for the roller's stem
DOOR_ROLLER_Z = (2.321, 2.336)
DOOR_COVER_Z = (2.338, 2.40)
DOOR_SLOT_Z = (.380, .3866)     # the floor slot of the jamb end's pin, in the step tread (its top 0.6 mm proud)
DOOR_GRAB_RAIL_Z = {0: (1.70, 1.04), 1: (.97, 1.71)}   # (jamb stile, meeting stile): photo estimates, see build_model.py
DOOR_DRIVE_FIRST = 34           # parts after the 34 contract parts: one door drive per leaf, then the wrap surfaces, then the straps
WRAP_SURFACE_OUT = .009         # the body wrap's skin over windows and door glass stands this far out from the glass
WRAP_LAYOUT = 1                 # the version of the wrap surfaces' geometry (vehicle.json wrap.layout; a wrap file is made for one)
# per door: seconds to open, to close, and the close delay after the command (vehicle.json doors[]; the video: the rear door
# first, the front pair 1.2 s later, each about 1.0 s; opening not measurable, estimated as 1.0 s)
DOOR_TIMING = {0: (1.0, 1.0, 1.2), 1: (1.0, 1.0, 0.0)}
DOOR_FPS = 24
DOOR_OPEN_FRAMES = (0, 24)      # the preview timeline: both doors open together, stay open, the close command at 48
DOOR_CLOSE_FRAME = 48
DOOR_TIMELINE_END = 104
# Each door's design: the jamb-end guide point along the leaf from its jamb stile and its depth inside the leaf's top inner
# face, the guide span (jamb end to roller), the open turn (degrees). Scanned against the post, the card reader, the poles
# and seats, the meeting stiles and the roller's dead point (2026-10-16): the front pair opens 0.14 m outside and 0.40 m
# inside, the rear leaf 0.12 m outside and 0.73 m inside.
DOOR_DESIGN = {0: (.10, .015, .26, 80.), 1: (.05, .02, .64, 82.)}
DOOR_MIN_SLACK = .02            # the roller never closer than this to its dead point (BusDriver rejects below .005)


def _rot(v, a):
    c, s = math.cos(a), math.sin(a)
    return (v[0] * c - v[1] * s, v[0] * s + v[1] * c)


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1])


def _add(a, b):
    return (a[0] + b[0], a[1] + b[1])


def _ang(v):
    return math.atan2(v[1], v[0])


def _wrap(a):
    return (a + math.pi) % (2 * math.pi) - math.pi


def door_ease(p):
    """BusInterior.DoorEase: every door's motion from its opening 0..1."""
    p = min(1., max(0., p))
    return p * p * (3 - 2 * p)


def door_opening(door, frame):
    """The preview timeline's opening of a door at a frame (linear in time, as BusDriver's DoorMotion; eased in door_solve)."""
    opens, closes, delay = DOOR_TIMING[door]
    if frame <= DOOR_CLOSE_FRAME:
        return min(1., max(0., (frame - DOOR_OPEN_FRAMES[0]) / (opens * DOOR_FPS)))
    return min(1., max(0., 1. - (frame - DOOR_CLOSE_FRAME - delay * DOOR_FPS) / (closes * DOOR_FPS)))


def door_point(L, s, x_offset=0., top=True):
    """A closed-position plan point of the leaf: s along it from its jamb, x_offset out from its frame plane (top or bottom)."""
    return (L['side'] - (DOOR_TOP_INSET if top else 0.) + x_offset, L['jamb_y'] + L['d'] * s)


def door_leaves(doors, side):
    """The leaves in contract order (Door0A, Door0B, Door1A) with their linkage and drive part."""
    leaves = []
    for di, (cy, dw) in enumerate(doors):
        count = 2 if di == 0 else 1
        for leaf in range(count):
            # the jamb it parks at, and the way from that jamb across the opening (+1: rearward)
            jamb, d = (cy - dw / 2, 1) if di == 0 and leaf == 0 else (cy + dw / 2, -1)
            cg, kg, span, degrees = DOOR_DESIGN[di]
            L = {'name': f'Door{di + 1}{chr(65 + leaf)}', 'door': di, 'leaf': leaf, 'part': 4 + 2 * di + leaf,
                 'drive_part': DOOR_DRIVE_FIRST + len(leaves), 'jamb_y': jamb, 'd': d, 'side': side,
                 'w': dw / count - 2 * DOOR_LEAF_GAP, 'centre_y': cy - dw / 2 + dw / count * (leaf + .5),
                 'design': DOOR_DESIGN[di], 'span': span, 'open_degrees': degrees}
            # the Scott Russell linkage: the shaft above the jamb-end guide point, the crank pin halfway, the roller at the span
            L['shaft'] = door_point(L, cg, -kg)
            L['joint'] = door_point(L, cg + span / 2, -kg)
            L['guide'] = door_point(L, cg + span, -kg)
            L['link'] = span / 2
            L['branch'] = 1. if L['guide'][1] >= L['joint'][1] else -1.
            phi = math.radians(degrees)
            # the crank turns as far as the leaf, the other way (the leaf's meeting stile out: clockwise for d = +1)
            L['theta'] = phi * d
            L['open_yaw'] = -phi * d
            end = door_solve(L, 1.)
            L['open_joint'], L['open_guide'] = end['joint'], end['guide']
            # how far the open leaf reaches into the bus (its jamb stile's inside corner, at the top)
            L['open_jamb_inside'] = L['side'] - door_move(L, end, door_point(L, DOOR_LEAF_GAP, -.006))[0]
            leaves.append(L)
    for index, L in enumerate(leaves):
        L['surface_part'] = DOOR_DRIVE_FIRST + len(leaves) + 1 + index      # the wrap surface of this leaf (a child of its leaf part)
    return leaves


def wrap_window_part(leaves):
    """The wrap surface part of the windows (the shell's child), right after the door drives."""
    return DOOR_DRIVE_FIRST + len(leaves)


def door_solve(L, p):
    """The linkage at opening p, as DoorLinkage.Solve: shaft turn, joint, roller, leaf turn about the joint, slack."""
    theta = L['theta'] * door_ease(p)
    B = _add(L['shaft'], _rot(_sub(L['joint'], L['shaft']), theta))
    dx = L['guide'][0] - B[0]
    d2 = L['link'] ** 2 - dx * dx
    if d2 <= 0:
        raise ValueError(f"{L['name']}: the roller has no place on its rail at {p:.3f}")
    slack = math.sqrt(d2)
    G = (L['guide'][0], B[1] + L['branch'] * slack)
    yaw = _wrap(_ang(_sub(G, B)) - _ang(_sub(L['guide'], L['joint'])))
    return {'p': p, 'theta': theta, 'joint': B, 'guide': G, 'yaw': yaw, 'slack': slack}


def door_move(L, sol, xy):
    """A closed-position plan point of the leaf, at the solved pose."""
    return _add(sol['joint'], _rot(_sub(xy, L['joint']), sol['yaw']))


def door_check(L, samples=200):
    """The motion's own checks (the 3D clearance is door_clearance.py's): raises on a failure, returns the figures."""
    g, w = DOOR_LEAF_GAP, L['w']
    # the leaf's real corners: the frame's outer sheet (+8 mm) and inside copy (-6 mm) at the stile edges, the glass and the
    # lower panel (+27 mm) 38.5 mm in from them, at the bottom and the top
    corners = [(s0, xo, top) for top in (False, True) for s0, xo in
               ((g, .008), (g, -.006), (g + .0385, .027), (g + w, .008), (g + w, -.006), (g + w - .0385, .027))]
    sv = lambda xy: (xy[1] - L['jamb_y']) * L['d']
    straight = door_point(L, L['design'][0], -L['design'][1])
    figures = {'min_slack_m': 9., 'post_margin_m': 9., 'meeting_advance_m': -9., 'jamb_end_off_line_m': 0.}
    last = 0.
    for i in range(samples + 1):
        sol = door_solve(L, i / samples)
        figures['min_slack_m'] = min(figures['min_slack_m'], sol['slack'])
        if (sol['yaw'] - last) * L['d'] > 1e-9:
            raise ValueError(f"{L['name']}: the leaf turns back at {i / samples:.3f}")
        last = sol['yaw']
        for s0, xo, top in corners:
            q = door_move(L, sol, door_point(L, s0, xo, top))
            wall = L['side'] - (DOOR_TOP_INSET if top else 0.)
            # within the post's 43 mm wall (and its skin), no part of the leaf within 1 mm of the post's side of the opening
            if wall - .046 < q[0] < wall + .005:
                figures['post_margin_m'] = min(figures['post_margin_m'], sv(q) - .001)
        for xo in (.008, .027):
            figures['meeting_advance_m'] = max(figures['meeting_advance_m'], sv(door_move(L, sol, door_point(L, g + w - (.0385 if xo > .01 else 0.), xo, False))) - (g + w))
        # the Scott Russell point moves straight into the bus
        figures['jamb_end_off_line_m'] = max(figures['jamb_end_off_line_m'], abs(sv(door_move(L, sol, straight)) - sv(straight)))
    end = door_solve(L, 1.)
    if abs(end['yaw'] - L['open_yaw']) > 1e-6:
        raise ValueError(f"{L['name']}: the linkage does not reach its open turn")
    if figures['min_slack_m'] < DOOR_MIN_SLACK:
        raise ValueError(f"{L['name']}: the roller comes within {figures['min_slack_m']:.4f} m of its dead point")
    if figures['post_margin_m'] < 0.:
        raise ValueError(f"{L['name']}: the leaf moves into its post ({figures['post_margin_m']:.4f} m)")
    if figures['meeting_advance_m'] > 1e-4:
        raise ValueError(f"{L['name']}: the meeting stile moves towards the middle of the opening")
    if figures['jamb_end_off_line_m'] > 1e-6:
        raise ValueError(f"{L['name']}: the jamb end leaves its straight line")
    meet_out = door_move(L, end, door_point(L, g + w, .008, False))
    jamb_in = door_move(L, end, door_point(L, g, -.006, False))
    figures.update(open_degrees=L['open_degrees'], shaft_degrees=math.degrees(L['theta']), crank_m=L['link'],
                   open_meeting_outside_m=meet_out[0] - L['side'], open_jamb_inside_m=L['side'] - jamb_in[0],
                   jamb_end_travel_m=L['span'] * math.sin(math.radians(L['open_degrees'])),
                   rail_travel_m=math.dist(L['guide'], L['open_guide']))
    return figures


def unity_xz(xy):
    return [round(xy[0], 6), round(-xy[1], 6)]


def door_profile_linkage(L):
    """The leaf's doors[].linkage entry (runtime plan coordinates)."""
    return {'shaft': unity_xz(L['shaft']), 'joint': unity_xz(L['joint']), 'guide': unity_xz(L['guide']),
            'shaftDegrees': round(math.degrees(L['theta']), 6), 'drivePart': L['drive_part']}


if __name__ == '__main__' and globals().get('__file__', '').endswith('door_linkage.py'):   # not when exec'd by the build
    for L in door_leaves([(-1.24, 1.12), (1.42, .88)], 1.175):
        print(L['name'], {k: round(v, 4) for k, v in door_check(L).items()}, door_profile_linkage(L))
        for i in range(0, 11):
            sol = door_solve(L, i / 10)
            print('   p=%.1f theta=%7.2f yaw=%7.2f joint=(%.4f, %.4f) roller y=%.4f slack=%.4f' % (
                sol['p'], math.degrees(sol['theta']), math.degrees(sol['yaw']), *sol['joint'], sol['guide'][1], sol['slack']))
