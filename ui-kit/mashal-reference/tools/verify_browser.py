from pathlib import Path
import json
from PIL import Image,ImageOps,ImageDraw
from playwright.sync_api import sync_playwright
import argparse
parser=argparse.ArgumentParser()
parser.add_argument('--base-url',default='http://127.0.0.1:4173/')
args=parser.parse_args()
root=Path(__file__).resolve().parents[1]
out=root/'qa'
out.mkdir(exist_ok=True)
catalog=json.loads((root/'catalog.json').read_text())
errors=[]; checks={}; geometry={}
base=args.base_url.rstrip('/')+'/'
with sync_playwright() as p:
    browser=p.chromium.launch(args=['--no-sandbox'])
    context=browser.new_context(device_scale_factor=1)
    page=context.new_page()
    page.on('pageerror',lambda e: errors.append(str(e)))
    page.on('console',lambda m: errors.append(m.text) if m.type=='error' else None)
    for c in catalog['components']:
        w,h=c['size'];page.set_viewport_size({'width':w,'height':h})
        page.goto(base+'specimen.html?id='+c['id'])
        page.wait_for_function('window.kitReady === true')
        page.screenshot(path=str(out/(c['id']+'-render.png')))
        geometry[c['id']]=page.locator('#mount > :not(dialog)').first.bounding_box()
        ref=Image.open(root/c['image']).convert('RGB')
        actual=Image.open(out/(c['id']+'-render.png')).convert('RGB')
        pair=Image.new('RGB',(w*2,h+24),'#eef1f6')
        pair.paste(ref,(0,24));pair.paste(actual,(w,24))
        ImageDraw.Draw(pair).text((8,5),c['id']+' SOURCE',fill='black')
        ImageDraw.Draw(pair).text((w+8,5),'RENDER',fill='black')
        pair.save(out/(c['id']+'-compare.png'))
    page.set_viewport_size({'width':1366,'height':900})
    page.goto(base+'index.html#C32');page.wait_for_function('window.galleryReady === true')
    page.frame_locator('#live').locator('.mr-modal-panel').wait_for()
    page.screenshot(path=str(out/'gallery-desktop.png'),full_page=True)
    checks['catalog38']=page.locator('#catalog-nav a').count()==38
    page.locator('#filter').fill('مودال')
    checks['search_filters']=page.locator('#catalog-nav a:visible').count()<38
    page.locator('#filter').fill('')
    page.locator('#theme').select_option('light')
    page.frame_locator('#live').locator('[data-mr-theme="light"]').wait_for()
    checks['theme_switch']=True
    page.locator('#state').select_option('disabled')
    checks['disabled_state']=page.frame_locator('#live').locator('input:enabled').count()==0
    page.locator('#state').select_option('default')
    page.frame_locator('#live').locator('input:enabled').first.wait_for()
    page.set_viewport_size({'width':390,'height':844})
    page.screenshot(path=str(out/'gallery-mobile.png'),full_page=True)
    checks['mobile_gallery_no_overflow']=page.evaluate('document.documentElement.scrollWidth <= innerWidth')
    page.set_viewport_size({'width':652,'height':469})
    page.goto(base+'specimen.html?id=C32');page.wait_for_function('window.kitReady === true')
    page.locator('[data-mr-select-all]').uncheck()
    checks['select_all_off']=page.locator('[data-mr-option]:checked').count()==0
    page.locator('[data-mr-option]').first.check()
    checks['select_all_indeterminate']=page.locator('[data-mr-select-all]').evaluate('(e)=>e.indeterminate')
    checks['confirm_enables_on_change']=page.locator('[data-mr-confirm]').is_enabled()
    page.locator('[data-mr-select-all]').check()
    checks['select_all_on']=page.locator('[data-mr-option]:checked').count()==6
    checks['confirm_unchanged_disabled']=page.locator('[data-mr-confirm]').is_disabled()
    page.set_viewport_size({'width':1100,'height':667})
    page.goto(base+'specimen.html?id=C05');page.wait_for_function('window.kitReady === true')
    page.get_by_role('button',name='افزودن ابزارک').click()
    checks['modal_opens']=page.locator('dialog').evaluate('(e)=>e.open')
    page.keyboard.press('Escape')
    checks['modal_escape']=not page.locator('dialog').evaluate('(e)=>e.open')
    checks['modal_focus_returns']=page.get_by_role('button',name='افزودن ابزارک').evaluate('(e)=>e===document.activeElement')
    page.goto(base+'specimen.html?id=C18');page.wait_for_function('window.kitReady === true')
    page.get_by_role('button',name='افزایش تعداد').click()
    checks['step_up']=page.get_by_role('spinbutton').input_value()=='1'
    page.get_by_role('button',name='کاهش تعداد').click()
    page.get_by_role('button',name='کاهش تعداد').click()
    checks['step_lower_bound']=page.get_by_role('spinbutton').input_value()=='0'
    page.goto(base+'specimen.html?id=C24');page.wait_for_function('window.kitReady === true')
    page.get_by_role('button',name='افزودن شخص').click()
    checks['add_card']=page.locator('.mr-party-card').count()==2
    page.get_by_role('button',name='حذف شخص').last.click()
    checks['remove_card']=page.locator('.mr-party-card').count()==1
    page.set_viewport_size({'width':390,'height':844})
    page.goto(base+'specimen.html?id=C24&responsive=1');page.wait_for_function('window.kitReady === true')
    checks['mobile_card_stacks']=page.locator('.mr-party-card').evaluate('(e)=>getComputedStyle(e).gridTemplateColumns.split(" ").length === 1')
    page.screenshot(path=str(out/'receipt-mobile.png'),full_page=True)
    checks['mobile_card_no_overflow']=page.evaluate('document.documentElement.scrollWidth <= innerWidth')
    page.goto(base+'specimen.html?id=C12&state=error');page.wait_for_function('window.kitReady === true')
    checks['error_described']=page.locator('select[aria-invalid="true"][aria-describedby]').count()==1
    page.goto(base+'specimen.html?id=C26');page.wait_for_function('window.kitReady === true')
    page.locator('summary').click()
    checks['disclosure_opens']=page.locator('details').evaluate('(e)=>e.open')
    page.emulate_media(reduced_motion='reduce')
    checks['reduced_motion']=page.evaluate('matchMedia("(prefers-reduced-motion: reduce)").matches')
    browser.close()
checks['no_browser_errors']=not errors
(out/'browser-checks.json').write_text(json.dumps({'checks':checks,'errors':errors,'geometry':geometry},indent=2))
for i in range(0,len(catalog['components']),8):
    group=catalog['components'][i:i+8]
    sheet=Image.new('RGB',(1000,280*len(group)),'#e8edf4')
    draw=ImageDraw.Draw(sheet)
    for j,c in enumerate(group):
        draw.text((12,j*280+4),c['id']+' SOURCE                                               RENDER',fill='#263449')
        for col,path in enumerate([root/c['image'],out/(c['id']+'-render.png')]):
            im=Image.open(path).convert('RGB');im.thumbnail((480,248))
            sheet.paste(im,(col*500+(500-im.width)//2,j*280+25))
    sheet.save(out/f'contact-{i//8+1}.png')
print(json.dumps({'checks':checks,'errors':errors,'failed':[k for k,v in checks.items() if not v]},indent=2))
