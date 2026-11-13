"""Arrange unchanged final-source renders under identical camera and lighting conditions."""
import argparse,json
from pathlib import Path
from PIL import Image,ImageDraw,ImageFont

p=argparse.ArgumentParser();p.add_argument('--views',type=Path,required=True);p.add_argument('--out',type=Path,required=True);p.add_argument('--lod',type=int,default=0)
a=p.parse_args();report=json.loads(a.views.read_text());rows={v['image']:v for v in report['views']}
width,height=960,720;label=34
sheet=Image.new('RGB',(width*2,(height+label)*2),(24,26,28));draw=ImageDraw.Draw(sheet);font=ImageFont.truetype('C:/Windows/Fonts/arial.ttf',23)
for row,name in enumerate(('wheel-close','trim-close')):
    on=f'LOD{a.lod}-{name}.png';off=f'LOD{a.lod}-{name}-normal-off.png'
    for key in ('lod','frame','camera','target','lens_mm','resolution'):assert rows[on][key]==rows[off][key],(name,key)
    assert rows[off]['normal_maps'] is False
    for column,filename in enumerate((on,off)):
        im=Image.open(a.views.parent/filename).convert('RGB');im.thumbnail((width,height),Image.Resampling.LANCZOS)
        x=column*width;y=row*(height+label)
        sheet.paste(im,(x+(width-im.width)//2,y+label+(height-im.height)//2))
        draw.text((x+12,y+5),name+' | Baked normal '+('ON' if column==0 else 'OFF'),font=font,fill='white')
sheet.save(a.out)
a.out.with_suffix('.json').write_text(json.dumps({'source_sha256':report['source_sha256'],'views':str(a.views.resolve()),'lod':a.lod,'operation':'Full images resized and arranged; no crop, retouch or exposure change','native_or_game_validated':False},indent=2))
