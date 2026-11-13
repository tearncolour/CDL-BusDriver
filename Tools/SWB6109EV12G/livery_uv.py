"""Add a stable five-zone image-wrap layout to editable body components."""
def prepare_livery_uv(collection, length, width, height):
    import bpy
    if min(length, width, height)<=0:
        raise ValueError('Livery projection requires positive measured vehicle dimensions')
    bpy.context.view_layer.update()
    for ob in collection.objects:
        if ob.type!='MESH' or not any(m and m.get('livery_control') for m in ob.data.materials):
            continue
        wrap=ob.data.uv_layers.get('CDLCWrapUV') or ob.data.uv_layers.new(name='CDLCWrapUV')
        for poly in ob.data.polygons:
            normal=(ob.matrix_world.to_3x3()@poly.normal).normalized()
            for li in poly.loop_indices:
                co=ob.matrix_world@ob.data.vertices[ob.data.loops[li].vertex_index].co
                if abs(normal.z)>.7:
                    u=.5+.5*(co.x+width/2)/width;v=.5*(co.y+length/2)/length
                elif abs(normal.x)>abs(normal.y):
                    u=(.5 if normal.x>0 else 0)+.5*((co.y+length/2)/length if normal.x>0 else (length/2-co.y)/length)
                    v=.5+.5*co.z/height
                else:
                    u=(0 if normal.y<0 else .25)+.25*(co.x+width/2)/width;v=.5*co.z/height
                wrap.data[li].uv=(max(0,min(1,u)),max(0,min(1,v)))
        ob.data.uv_layers.active_index=0
