"""Photograph-checked CRRC front emblem, executed in build_model's globals.

Entry point: build_k18_badges().  There is no font/text geometry.  Six filled
contours follow the manufacturer's published symbol, checked against the actual
chrome emblem in official-c18-en-6.jpg.  The dark photographic reflections are
not traced as holes or new strokes.  Sizes/depth are photographic estimates.
"""

K18_BADGE_REFERENCES = {
    'official-crrc-brand-symbol.png': '5b84f64207cce8e5a757476df3447fca429a946d22e99acb4bd584f047a6f2e5',
    'official-c18-en-6.jpg': 'e915ee57b06cd3e887ca8780d695a783ff8bfe003bf005b3ab0876d9b95cca4d',
}


def k18_badge_contours(arc_steps=8):
    """Return six independent closed-fill boundaries in original 291px axes.

    The 236px square mark occupies x=28..264, y=30..266.  Original rounded
    C-shaped brackets remain open towards the middle.  In particular there is
    no invented continuous centre spine or rectangular backing plate.
    """
    def arc(cx, cy, radius, start, end, steps=None):
        steps = arc_steps if steps is None else steps
        return [(cx + radius * math.cos(math.radians(start + (end-start)*i/steps)),
                 cy + radius * math.sin(math.radians(start + (end-start)*i/steps)))
                for i in range(1, steps+1)]

    top = [(28,46),(129,46),(129,30),(162,30),(162,46),(264,46),(264,72),(28,72)]
    bottom = [(28,223),(264,223),(264,249),(162,249),(162,266),(129,266),(129,249),(28,249)]
    left = [(129,86),(65,86)] + arc(65,121,35,-90,-180)
    left += [(30,175)] + arc(65,175,35,180,90)
    left += [(129,210),(129,183),(73,183)] + arc(73,171,12,90,180)
    left += [(61,125)] + arc(73,125,12,180,270) + [(129,113)]
    centre_steps = min(arc_steps, 4)
    finger = [(129,134),(91,134)] + arc(91,138,4,-90,-180,centre_steps)
    finger += [(87,158)] + arc(91,158,4,180,90,centre_steps) + [(129,162)]
    reflect = lambda points: [(291-x,y) for x,y in reversed(points)]
    return {'top_bar': top, 'bottom_bar': bottom,
            'left_bracket': left, 'right_bracket': reflect(left),
            'left_centre': finger, 'right_centre': reflect(finger)}


def _k18_badge_triangles(points):
    """Ear-clip a simple concave contour without collinear zero-area ears."""
    def cross(a, b, c):
        return (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0])
    area = sum(a[0]*b[1]-b[0]*a[1]
               for a,b in zip(points, points[1:]+points[:1]))
    direction = 1 if area > 0 else -1
    remaining = list(range(len(points)))
    triangles = []
    while len(remaining) > 3:
        for j,b in enumerate(remaining):
            a,c = remaining[j-1],remaining[(j+1)%len(remaining)]
            if cross(points[a],points[b],points[c])*direction <= 1e-8:
                continue
            if any(all(cross(points[u],points[v],points[k])*direction >= -1e-8
                       for u,v in ((a,b),(b,c),(c,a)))
                   for k in remaining if k not in (a,b,c)):
                continue
            triangles.append((a,b,c));remaining.pop(j)
            break
        else:
            raise ValueError('K18 badge contour cannot be triangulated safely')
    if abs(cross(*(points[i] for i in remaining))) <= 1e-8:
        raise ValueError('Degenerate final K18 badge ear')
    triangles.append(tuple(remaining))
    return triangles


def _k18_badge_inset(points, distance):
    """Exact parallel-edge inset of the traced polygon; no bevel of a triangulated cap.

    The narrow concave arcs are already sampled by the contour. A four-ring
    chamfer avoids Blender Bevel creating folded n-gons where cap diagonals
    meet that concavity on the curved bus nose.
    """
    area=sum(a.x*b.y-b.x*a.y for a,b in zip(points,points[1:]+points[:1]))
    direction=1 if area>0 else -1
    result=[]
    for i,p in enumerate(points):
        before=(p-points[i-1]).normalized()
        after=(points[(i+1)%len(points)]-p).normalized()
        n1=Vector((-before.y*direction,before.x*direction,0))
        n2=Vector((-after.y*direction,after.x*direction,0))
        bisector=(n1+n2).normalized()
        denominator=bisector.dot(n2)
        if denominator<.1:
            raise ValueError('K18 badge contour has an unsafe chamfer corner')
        result.append(p+bisector*(distance/denominator))
    return result


def build_k18_badges(width=.170, height=.170, centre_y=.815,
                     thickness=.0030, bevel_width=.00045):
    """Create only the front factory emblem, part 16, explicit LOD0/1/2.

    Dependencies in the builder namespace: mesh, select_section, front_z,
    front_normal, CHROME, ROOT, math, Vector. References are SHA checked;
    geometry is embedded as editable traced contours, not raster textures.
    All six parts have conforming backs on front_z and front displacement
    along the sampled body normal. Chamfers are explicit closed mesh rings.
    """
    import hashlib as _badge_hashlib

    if width <= 0 or height <= 0 or not 0 < bevel_width < thickness/2:
        raise ValueError('Invalid K18 badge dimensions/bevel')
    if int(CHROME.get('light_id', 0)) != 0 or bool(CHROME.get('glass', False)):
        raise ValueError('K18 badge must use non-emissive opaque chrome')
    for filename, expected in K18_BADGE_REFERENCES.items():
        path_ = ROOT/'ModelSource/CRRC_K18/reference'/filename
        actual = _badge_hashlib.sha256(path_.read_bytes()).hexdigest()
        if actual != expected:
            raise ValueError('K18 badge reference changed: '+str(path_))

    select_section('front')
    result = []
    for level, steps in ((0,10),(1,6),(2,3)):
        for label, contour in k18_badge_contours(steps).items():
            # Screen-left is vehicle-right; hence x reverses into Unity.
            flat = [Vector(((146-x)*width/236, centre_y+(148-y)*height/236, 0))
                    for x,y in contour]
            inner=_k18_badge_inset(flat,bevel_width)
            inner_pixels=[(146-p.x*236/width,148-(p.y-centre_y)*236/height) for p in inner]
            triangles = _k18_badge_triangles(inner_pixels)
            if len(triangles) != len(flat)-2:
                raise ValueError('Incomplete K18 badge contour triangulation: '+label)
            verts = []
            for profile,depth in ((inner,0.0),(flat,bevel_width),
                                  (flat,thickness-bevel_width),(inner,thickness)):
                for v in profile:
                    p = Vector((v.x,v.y,front_z(v.x,v.y)))
                    verts.append(tuple(p + front_normal(v.x,v.y)*depth))
            n = len(flat)
            faces = [tuple(reversed(t)) for t in triangles]
            faces += [tuple(i+3*n for i in t) for t in triangles]
            for ring in range(3):
                for i in range(n):
                    a=ring*n+i;b=ring*n+(i+1)%n;c=b+n;d=a+n
                    faces += [(a,b,c),(a,c,d)]
            ob = mesh('K18 factory front badge '+label+' LOD'+str(level),
                      verts, faces, CHROME, 16, lod=level)
            ob['first_lod'] = level
            ob['last_lod'] = level
            ob['k18_front_role'] = 'factory_badge'
            ob['badge_contour'] = label
            ob['badge_reference_sha256'] = K18_BADGE_REFERENCES['official-c18-en-6.jpg']
            ob['badge_symbol_sha256'] = K18_BADGE_REFERENCES['official-crrc-brand-symbol.png']
            ob['badge_geometry_method'] = 'Six manually traced manufacturer symbol contours; no font'
            ob['badge_dimensions_status'] = 'Photo-estimated width/height; thickness engineering estimate'
            ob['badge_thickness_m'] = thickness
            ob['badge_chamfer_method']='Explicit four closed rings with triangulated concave caps; no Bevel/WeightedNormal modifier'
            ob['badge_chamfer_width_m']=bevel_width
            if level:
                ob.hide_render = True
                ob.hide_set(True)
            result.append(ob)
    return result
