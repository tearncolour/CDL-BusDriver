"""Create labelled review sheets without cropping individual saved camera images."""
import argparse,json,math
from pathlib import Path
from PIL import Image,ImageDraw,ImageFont

ap=argparse.ArgumentParser();ap.add_argument('--views',type=Path,required=True);ap.add_argument('--out',type=Path,required=True);ap.add_argument('--lod',type=int,required=True);ap.add_argument('--doors',action='store_true')
a=ap.parse_args();report=json.loads(a.views.read_text());rows=[v for v in report['views'] if v['lod']==a.lod and v.get('normal_maps') is not False]
if a.doors:rows.sort(key=lambda v:(Path(v['image']).name.split('-')[1],Path(v['image']).name.split('-')[2],v['frame']))
w,h=480,360;columns=3;label=24;sheet=Image.new('RGB',(columns*w,math.ceil(len(rows)/columns)*(h+label)),(24,26,28));draw=ImageDraw.Draw(sheet)
font=ImageFont.truetype('C:/Windows/Fonts/arial.ttf',16)
for i,row in enumerate(rows):
    im=Image.open(a.views.parent/row['image']).convert('RGB');im.thumbnail((w,h),Image.Resampling.LANCZOS);x=i%columns*w;y=i//columns*(h+label)
    sheet.paste(im,(x+(w-im.width)//2,y+label+(h-im.height)//2));draw.text((x+8,y+3),Path(row['image']).name,font=font,fill='white')
sheet.save(a.out)
