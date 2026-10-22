"""Build calibrated side-by-side, 50% overlay, raw diff and annotation stroke mask.
This is image analysis for QA; it never alters the supplied source images.
"""
import argparse
import json
from pathlib import Path
from PIL import Image, ImageChops, ImageDraw

parser = argparse.ArgumentParser()
parser.add_argument('--references', required=True)
parser.add_argument('--captures', default='artifacts/ui-reference/final')
parser.add_argument('--output', default='artifacts/ui-reference/comparison')
args = parser.parse_args()
out = Path(args.output)
out.mkdir(parents=True, exist_ok=True)
calibration = json.loads(Path('docs/ui-reference/calibration.json').read_text())
for ref in calibration['references']:
    capture = Path(args.captures) / ('calibrated-' + ref['capture'] + '.png')
    if not capture.exists():
        continue
    source = Image.open(Path(args.references) / ref['file']).convert('RGB')
    x, y, w, h = ref['crop']
    scale = ref['scale']
    source = source.crop((x, y, x+w, y+h))
    normalized = source.resize((round(w*scale), round(h*scale)), Image.Resampling.LANCZOS)
    target = Image.open(capture).convert('RGB')
    # At most 1 px rounding is cropped; never stretch aspect ratios independently.
    size = (min(normalized.width, target.width), min(normalized.height, target.height))
    normalized = normalized.crop((0, 0, *size))
    target = target.crop((0, 0, *size))
    normalized.save(out / (ref['id'] + '-source-normalized.png'))
    board = Image.new('RGB', (size[0]*2, size[1]), '#ffffff')
    board.paste(normalized, (0,0));board.paste(target, (size[0],0))
    board.save(out / (ref['id'] + '-side-by-side.png'))
    Image.blend(normalized, target, .5).save(out / (ref['id'] + '-overlay.png'))
    ImageChops.difference(normalized, target).save(out / (ref['id'] + '-difference-raw.png'))
    mask = Image.new('L', size, 255)
    draw = ImageDraw.Draw(mask)
    for ax, ay, aw, ah in ref['annotations']:
        draw.rectangle((round((ax-x)*scale),round((ay-y)*scale),round((ax+aw-x)*scale),round((ay+ah-y)*scale)),outline=0,width=round(5*scale))
    mask.save(out / (ref['id'] + '-annotation-mask.png'))
    diff = ImageChops.difference(normalized, target)
    Image.composite(diff, Image.new('RGB',size,'black'),mask).save(out/(ref['id']+'-difference-masked.png'))
    # Source and implementation modal details are directly comparable at PNG 1:1.
    if ref['id'] == 'R06':
        crops=[normalized.crop((357,94,1009,563)),target.crop((357,94,1009,563))]
        detail=Image.new('RGB',(1304,469),'white')
        detail.paste(crops[0],(0,0));detail.paste(crops[1],(652,0))
        detail.save(out/'R06-modal-detail.png')
print('Comparison boards, overlays, differences and annotation-only masks saved to',out)
