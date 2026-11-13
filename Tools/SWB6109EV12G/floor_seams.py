"""Keep contiguous floor tiles flat up to their shared construction edges."""
import bpy


def patch_floor_seams(master):
    changed=[]
    for obj in master.objects:
        if obj.name=='Floor continuous central low aisle' or obj.name.startswith('Floor outboard wing '):
            removed=[]
            for modifier in list(obj.modifiers):
                if modifier.type=='BEVEL':
                    removed.append(modifier.name)
                    obj.modifiers.remove(modifier)
            obj['floor_shared_edge_revision']=1
            changed.append({'object':obj.name,'removed_bevels':removed})
    if len(changed)!=7:raise RuntimeError('Expected central aisle and six adjoining outboard floor tiles')
    bpy.context.view_layer.update()
    return {'objects':changed,'scope':'Remove V-shaped bevel grooves at shared floor edges; no positions, outer body or wheel clearance changed'}
