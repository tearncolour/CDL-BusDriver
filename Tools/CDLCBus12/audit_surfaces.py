"""Validate baked PNG channel packing and record periodic edges and mip attenuation."""
import argparse,hashlib,json
from pathlib import Path
import numpy as np
from PIL import Image

ap=argparse.ArgumentParser();ap.add_argument('--bakes',type=Path,required=True);ap.add_argument('--out',type=Path,required=True)
a=ap.parse_args();rows=[]
for tile in ('leather','polymer','woven','brushed'):
    paths=[a.bakes/(tile+suffix+'.png') for suffix in ('-Normal','-Detail')]
    normal,detail=[np.asarray(Image.open(p).convert('RGBA'),dtype=np.int16) for p in paths]
    if normal.shape!=(512,512,4) or detail.shape!=normal.shape:raise ValueError('Unexpected texture dimensions')
    error=max(int(np.abs(detail[:,:,1]-normal[:,:,1]).max()),int(np.abs(detail[:,:,3]-normal[:,:,0]).max()))
    neutral=max(int(np.abs(detail[:,:,0]-128).max()),int(np.abs(detail[:,:,2]-128).max()))
    if error>1 or neutral>1:raise ValueError('Invalid HDRP linear channel packing: '+tile)
    n=normal[:,:,:3].astype(float)/127.5-1
    lengths=np.linalg.norm(n,axis=2)
    if np.max(np.abs(lengths-1))>.015:raise ValueError('Invalid baked normal length: '+tile)
    adj=np.concatenate((np.linalg.norm(np.diff(n,axis=0),axis=2).ravel(),np.linalg.norm(np.diff(n,axis=1),axis=2).ravel()))
    seam=np.concatenate((np.linalg.norm(n[0]-n[-1],axis=1),np.linalg.norm(n[:,0]-n[:,-1],axis=1)))
    if np.mean(seam*seam)>4*np.mean(adj*adj):raise ValueError('Periodic normal edge exceeds twice the interior RMS step: '+tile)
    mips=[];xy=n[:,:,:2]
    while True:
        mips.append({'resolution':len(xy),'xy_rms':float(np.sqrt(np.mean(xy*xy)))})
        if len(xy)==1:break
        xy=xy.reshape(len(xy)//2,2,len(xy)//2,2,2).mean(axis=(1,3))
    rows.append({'tile':tile,'files':[{'path':str(p),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in paths],
        'max_channel_error_8bit':error,'neutral_channel_error_8bit':neutral,'normal_length_max_error':float(np.max(np.abs(lengths-1))),
        'adjacent_normal_step_rms':float(np.sqrt(np.mean(adj*adj))),'repeat_boundary_step_rms':float(np.sqrt(np.mean(seam*seam))),
        'ideal_box_mips':mips})
a.out.write_text(json.dumps({'packing_pass':True,'periodic_edges_pass':True,'tiles':rows,'scope':'PNG linear channel packing, periodic edge RMS (at most twice the interior step), and ideal box-filter estimates. Actual GPU mip filtering and visible seams still require game review.'},indent=2))
print('Four normal/detail tile channel packs passed')
