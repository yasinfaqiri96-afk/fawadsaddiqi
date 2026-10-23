"""Extract annotated visual evidence and write reusable, finance-free HTML specimens.

Usage: python tools/build_catalog.py --references /path/to/ui-references
No screenshot pixels are used as a replacement for rendered controls.
"""
import argparse
import hashlib
import html
import json
from pathlib import Path
import shutil
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
REPO = ROOT.parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--references', type=Path, default=ROOT / 'references')
args = parser.parse_args()
(ROOT / 'assets').mkdir(exist_ok=True)
(ROOT / 'references').mkdir(exist_ok=True)
(ROOT / 'reference-crops').mkdir(exist_ok=True)
(ROOT / 'templates').mkdir(exist_ok=True)
WEB = REPO / 'src/PTGOilSystem.Web/wwwroot'
for weight in (400, 700):
    shutil.copy2(WEB / f'vendor/fonts/iransans/files/iransans-{weight}.woff2', ROOT / f'assets/iransans-{weight}.woff2')
shutil.copy2(WEB / 'vendor/fonts/iransans/LICENSE-IRANSans-5.5.pdf', ROOT / 'assets/LICENSE-IRANSans-5.5.pdf')
icons = WEB / 'vendor/bootstrap-icons/font'
(ROOT / 'assets/fonts').mkdir(exist_ok=True)
shutil.copy2(icons / 'bootstrap-icons.min.css', ROOT / 'assets/bootstrap-icons.min.css')
shutil.copy2(icons / 'fonts/bootstrap-icons.woff2', ROOT / 'assets/fonts/bootstrap-icons.woff2')
shutil.copy2(icons / 'fonts/bootstrap-icons.woff', ROOT / 'assets/fonts/bootstrap-icons.woff')
shutil.copy2(WEB / 'images/لوکو شمعل فارسی رنک ابی.webp', ROOT / 'assets/mashal-logo.webp')
refs = {}
for source in sorted(args.references.glob('R0*')):
    if not source.is_file():
        continue
    target = ROOT / 'references' / source.name
    if target.resolve() != source.resolve():
        shutil.copy2(source, target)
    refs[source.name[:3]] = source
assert len(refs) == 7, 'All seven supplied reference images are required.'

Image.open(refs['R07']).crop((882, 253, 962, 370)).save(ROOT / 'assets/receipt-avatar.png')
Image.open(refs['R01']).crop((513, 94, 595, 168)).save(ROOT / 'assets/profile-avatar.png')
for name, box in {
    'dash-square-fill': (936, 201, 970, 236),
    'bank2': (728, 201, 762, 236),
    'graph-up-arrow': (522, 201, 556, 236),
    'arrow-up-square-fill': (728, 295, 762, 330),
    'arrow-down-square-fill': (522, 295, 556, 330),
}.items():
    Image.open(refs['R06']).crop(box).save(ROOT / f'assets/widget-{name}.png')

def icon(name):
    return f'<i class="mr-icon bi bi-{name}" aria-hidden="true"></i>'

def button(label, kind='', action='demo', ico=None):
    return f'<button type="button" class="mr-button {kind}" data-mr-action="{action}" data-mr-demo>{icon(ico) if ico else ""}{label}</button>'

def field(label, value='', floating=False, select=False, placeholder='', extra=''):
    label_markup = f'<span class="mr-label">{label}</span>'
    if select:
        control = f'<span class="mr-select-wrap"><select class="mr-input mr-select" {extra}><option>{value}</option><option>گزینهٔ دیگر</option></select>{icon("chevron-up" if floating else "chevron-down")}</span>'
    else:
        control = f'<input class="mr-input" value="{value}" placeholder="{placeholder}" {extra}>'
    return f'<label class="mr-field {"mr-floating" if floating else ""}">{label_markup}{control}</label>'

def stepper(value='۰', quantity=False):
    numeric = '1' if quantity else '0'
    return f'<div class="mr-stepper"><button type="button" data-mr-action="step-down" aria-label="کاهش تعداد">{icon("dash")}</button><input class="mr-input" type="number" value="{numeric}" min="0" step="1" aria-label="تعداد"><button type="button" data-mr-action="step-up" aria-label="افزایش تعداد">{icon("plus")}</button></div>'

def search(label='جستجو', sidebar=False):
    return f'<label class="mr-search">{icon("search")}<input type="search" aria-label="{label}" placeholder="{label if not sidebar else ""}"></label>'

nav_items = [('داشبورد','grid'),('اشخاص','people'),('کالاها و خدمات','box-seam'),('دریافت و پرداخت','arrow-repeat'),('خرید و هزینه','handbag'),('فروش و درآمد','bar-chart'),('مالی و بانکداری','bank'),('چک','cash-stack'),('انبارداری','boxes'),('حسابداری','calculator')]

def nav(active='داشبورد'):
    return '<nav class="mr-nav" aria-label="منوی اصلی">' + ''.join(f'<a class="mr-nav-item" href="#" data-mr-action="demo" data-mr-demo {"aria-current=\"page\"" if label == active else ""}>{icon(ico)}<span>{label}</span></a>' for label, ico in nav_items) + '</nav>'

context = '<div class="mr-context"><span class="mr-row">'+icon('bag-fill')+'شرکت نفت و گاز مشعل</span><select class="mr-input" aria-label="سال مالی"><option>سال مالی ۱۴۰۰/۰۱/۰۱</option><option>سال مالی ۱۴۰۱/۰۱/۰۱</option></select>'+button('ثبت فاکتور',ico='plus-circle')+'</div>'

def sidebar(blue=True):
    return '<aside class="mr-sidebar"><div class="mr-brand"><img src="assets/mashal-logo.webp" alt="مشعل"></div>'+('' if blue else context)+'<div class="mr-sidebar-search">'+search('جستجو در منو',True)+(f'<button type="button" class="mr-sidebar-back" aria-label="جمع‌کردن منو" data-mr-action="demo" data-mr-demo>{icon("box-arrow-right")}</button>' if blue else '')+'</div>'+nav()+'<footer class="mr-sidebar-footer"><div class="mr-sidebar-download">نسخهٔ ویندوز مشعل　'+icon('windows')+'</div><div class="mr-sidebar-links">پشتیبانی　│　تماس با ما</div></footer></aside>'

switch = '<label class="mr-switch"><input type="checkbox" role="switch" checked><span>پرداخت می‌کنم</span></label>'
security_switch = '<label class="mr-switch mr-switch--security"><input type="checkbox" role="switch"><span>حالت امنیتی</span></label>'

def topbar():
    return '<header class="mr-topbar"><span>چهارشنبه ۱۴۰۰/۹/۱۷</span><div class="mr-row">'+security_switch+'<span class="mr-topbar-divider"></span><button type="button" class="mr-icon-button" aria-label="تازه‌سازی" data-mr-demo data-mr-action="demo">'+icon('arrow-repeat')+'</button><button type="button" class="mr-icon-button" aria-label="حساب کاربری" data-mr-demo data-mr-action="demo">'+icon('person-fill')+'</button></div></header>'

settings_groups = [
    ('عمومی',[('حساب کاربری','person'),('کسب و کار','briefcase'),('مدیریت کاربران','people')]),
    ('مالی',[('حسابداری','calculator'),('سال مالی','calendar3'),('فاکتور','file-earmark-text'),('چاپ','printer'),('به‌روزرسانی اطلاعات','arrow-repeat'),('لیست قیمت‌ها','chat-square-text')]),
    ('سیستم',[('نسخه آنلاین','display'),('رابط برنامه‌نویسی (API)','file-earmark-code'),('پنل پیامک','envelope'),('اعلانات','bell')])]
settings = '<nav class="mr-settings-nav" aria-label="تنظیمات">'+search('جستجو در تنظیمات...')+''.join(f'<h2>{title}</h2><div class="mr-nav">'+''.join(f'<a href="#" class="mr-nav-item" data-mr-action="demo" data-mr-demo {"aria-current=\"page\"" if label == "کسب و کار" else ""}>{icon(ico)}{label}</a>' for label,ico in items)+'</div>' for title,items in settings_groups)+'</nav>'

def party():
    return '<section class="mr-card mr-party-card"><img class="mr-party-avatar" src="assets/receipt-avatar.png" alt="تصویر شخص در مرجع"><div class="mr-party-fields">'+field('شخص','حسین یوسفی',True,True)+field('مبلغ','۶۸۳۰۰۰۰',True)+'<div class="mr-party-bottom"><input class="mr-input" aria-label="شرح" placeholder="شرح">'+f'<button type="button" class="mr-icon-button mr-icon-button--delete" data-mr-action="delete-card" data-mr-demo aria-label="حذف شخص">{icon("trash-fill")}</button></div></div></section>'

summary = '<div class="mr-summary"><span class="mr-summary-positive">مجموع: ۶۸۳۰۰۰۰</span><span class="mr-summary-negative">باقیمانده: ۰</span></div>'
disclosure = '<details class="mr-disclosure"><summary>افزودن دریافت'+icon('chevron-up')+'</summary><div>'+button('افزودن روش دریافت',ico='plus')+'</div></details>'
bank = '<section class="mr-card mr-bank-card"><header><span>بانک</span><button type="button" class="mr-icon-button mr-icon-button--delete" aria-label="حذف بانک" data-mr-action="demo" data-mr-demo>'+icon('trash-fill')+'</button></header><div class="mr-grid">'+field('بانک','ملت',True,True)+field('مبلغ','۶۸۳۰۰۰۰',True)+field('کارمزد خدمات بانکی','۰',True)+field('شرح','',True)+'</div></section>'

def ledger(number='۳۳', amount='۷۶,۵۰۰,۰۰۰', desc='واریز بخشی از وجه فروش به تنخواه گردان'):
    return '<article class="mr-ledger-row"><span class="mr-badge">'+number+'</span><span>'+amount+' ریال</span><span class="mr-badge mr-badge--auto">اتوماتیک</span><span class="mr-ledger-meta"><span class="mr-ledger-copy">('+desc+')</span><small>۱۴۰۰/۰۹/۱۲</small></span></article>'

count = '<span class="mr-count">تعداد: <strong>۳۳</strong></span>'
widget = '<section class="mr-widget"><header class="mr-widget-header"><h2>آخرین اسناد</h2>'+count+'</header><div class="mr-widget-list">'+ledger()+ledger('۳۲','۸۲,۰۰۰,۰۰۰','واریز بخشی از وجه فروش صندوق به تنخواه گردان')+ledger('۳۱','۱۴۸,۰۰۰,۰۰۰','انتقال وجه پایا به حساب بانک ملی')+'</div></section>'
widget_types = [('آخرین اسناد','dash-square-fill'),('بانکداری','bank2'),('بدهکاران','graph-up-arrow'),('بستانکاران','graph-up-arrow'),('چک‌های پرداختی','arrow-up-square-fill'),('چک‌های دریافتی','arrow-down-square-fill')]

def tile(label, ico):
    return f'<label class="mr-widget-option"><input type="checkbox" data-mr-option checked><img class="mr-widget-art" src="assets/widget-{ico}.png" alt=""><span>{label}</span></label>'

modal = '<section class="mr-modal-panel"><header><span>افزودن ابزارک</span><label class="mr-row"><input class="mr-checkbox" type="checkbox" data-mr-select-all checked>انتخاب همه</label></header><div class="mr-widget-options">'+''.join(tile(*item) for item in widget_types)+'</div><footer class="mr-modal-actions">'+button('تأیید',action='confirm-widgets')+button('لغو','mr-button--cancel',action='close-modal')+'</footer></section>'

business = '<div class="mr-business-form"><div class="mr-business-top"><div class="mr-business-logo"><img src="assets/mashal-logo.webp" alt="مشعل"></div><div class="mr-grid"><div class="mr-full">'+field('نام کسب و کار *','شرکت نفت و گاز مشعل')+'</div>'+field('نوع کسب و کار *','فروشگاه',select=True)+field('زمینه فعالیت *','نفت و گاز')+'</div></div><div>'+field('نام قانونی کسب و کار','شرکت نفت و گاز مشعل')+'</div><div class="mr-grid mr-grid--three">'+field('شماره ثبت','',placeholder='شماره ثبت')+field('شناسه ملی','',placeholder='شناسه ملی')+field('کد اقتصادی','',placeholder='کد اقتصادی')+'</div><div class="mr-grid mr-business-address">'+field('استان *','قزوین',select=True)+field('شهر *','شریفیه',select=True)+'</div></div>'

modal = modal.replace('data-mr-action="confirm-widgets"', 'data-mr-action="confirm-widgets" data-mr-confirm disabled')

data = []

def add(cid, title, ref, box, markup, theme='light', scale=1, category='فرم', note='', status='measured', metrics=None):
    image = Image.open(refs[ref])
    crop = image.crop(box)
    native = [round(crop.width*scale), round(crop.height*scale)]
    if scale != 1:
        crop = crop.resize(native, Image.Resampling.LANCZOS)
    crop.save(ROOT / f'reference-crops/{cid}.png')
    (ROOT / f'templates/{cid}.html').write_text(markup+'\n', encoding='utf-8')
    record = dict(id=cid,title=title,category=category,reference=refs[ref].name,crop=list(box),scale=scale,size=native,theme=theme,template=f'templates/{cid}.html',image=f'reference-crops/{cid}.png',selector=next((s for s in ['mr-modal-panel','mr-party-card','mr-repeat-group','mr-sidebar','mr-topbar','mr-widget'] if s in markup),'mr-component'),status=status,note=note,metrics=metrics or {})
    data.append(record)

add('C01','سایدبار آبی','R05',(1126,0,1366,667),sidebar(),'blue',category='پوسته',note='هویت مرجع چرتکه است؛ لوگوی پیش‌فرض کتابخانه عمداً مشعل است. تعداد منو از میزبان می‌آید.',metrics={'width':240,'itemHeight':47})
add('C02','سایدبار سفید','R02',(827,114,993,635),sidebar(False),scale=240/166,category='پوسته',note='JPEG مقیاس‌شده؛ ابعاد تقریبی بر مبنای عرض ۲۴۰ پیکسل. لوگوی مشعل حفظ شده است.')
add('C03','نوار بالای آبی','R05',(0,0,1126,48),topbar(),'blue',category='پوسته',metrics={'height':48})
add('C04','نوار بالای سفید','R02',(18,114,827,151),'<header class="mr-topbar"><span></span><div class="mr-row">'+icon('person')+icon('bell')+icon('incognito')+'</div></header>',scale=48/37,category='پوسته',note='برش JPEG؛ ارتفاع با نوار ۴۸ پیکسلی کالیبره شده است.')
add('C05','نوار عنوان و عملیات','R05',(8,48,1126,104),'<header class="mr-toolbar"><h1>داشبورد</h1><div class="mr-row"><button type="button" class="mr-icon-button" aria-label="افزودن ابزارک" data-mr-action="open-modal">'+icon('plus')+'</button>'+button('ذخیره','mr-button--save',ico='bookmark-fill')+'</div></header>','blue',category='پوسته',metrics={'height':56},note='دکمهٔ افزودن در گالری مودال انتخاب ابزارک را باز می‌کند.')
add('C06','منوی داخلی تنظیمات','R02',(614,175,805,604),settings,scale=1.4457831325,category='پوسته',note='کادرهای فیروزه‌ای روی عکس، annotation آموزشی هستند و جزو طراحی نیستند.')
add('C07','دکمهٔ اصلی','R02',(58,613,123,634),button('ذخیره تغییرات'),scale=1.4457831325,category='دکمه',note='ارتفاع اندازه‌گیری‌شدهٔ JPEG حدود ۳۰ پیکسل؛ توکن عمومی دکمه ۳۲ پیکسل است.')
add('C08','دکمهٔ ذخیرهٔ خطی','R05',(40,58,122,94),button('ذخیره','mr-button--save',ico='bookmark-fill'),'blue',category='دکمه',metrics={'height':36,'radius':8})
add('C09','دکمهٔ آیکنی افزودن','R05',(133,58,169,94),'<button type="button" class="mr-icon-button" aria-label="افزودن" data-mr-action="open-modal">'+icon('plus')+'</button>','blue',category='دکمه',note='آیکن از Bootstrap Icons موجود در پروژه است؛ تطابق منبع آیکن مرجع اثبات نشده است.')
add('C10','فیلد متن ساده','R02',(77,281,317,303),'<input class="mr-input" aria-label="نام کسب و کار" value="شرکت نفت و گاز مشعل">',scale=1.4457831325,metrics={'height':32})
add('C11','فهرست انتخاب ساده','R03',(368,267,630,291),'<label class="mr-select-wrap"><span class="mr-sr">واحد پول اصلی</span><select class="mr-input mr-select"><option>ریال</option><option>افغانی</option><option>دالر</option></select>'+icon('chevron-down')+'</label>',scale=1.3114754098)
add('C12','فیلد با عنوان روی کادر','R07',(430,249,803,291),field('شخص','حسین یوسفی',True,True),'blue',metrics={'inputHeight':36,'radius':8})
add('C13','فیلد تاریخ','R07',(357,116,826,158),'<label class="mr-field mr-floating"><span class="mr-label mr-sr">تاریخ</span><span class="mr-date"><input class="mr-input" aria-label="تاریخ" value="۱۴۰۰　 /　 ۷　 /　 ۲۰">'+icon('calendar3')+'</span></label>','blue',note='قالب ظاهری تاریخ است؛ تقویم و تبدیل تاریخ باید از کامپوننت فعلی سیستم بیایند.')
add('C14','جستجوی منوی آبی','R05',(1181,56,1354,90),'<div class="mr-sidebar" style="min-height:0;width:173px">'+search('جستجو',True)+'</div>','blue',metrics={'height':34,'width':173})
add('C15','آواتار و افزودن تصویر','R01',(510,94,596,182),'<div class="mr-avatar-edit"><img src="assets/profile-avatar.png" alt="آواتار استخراج‌شده از مرجع"><button type="button" class="mr-icon-button" aria-label="تغییر تصویر" data-mr-action="demo" data-mr-demo>'+icon('plus')+'</button></div>',scale=1.4371257485,category='هویت',note='خود تصویر از screenshot بریده شده؛ فایل اصلی و وضوح بالاتر موجود نیست.',status='raster-limited')
add('C16','کلید روشن و خاموش','R03',(75,327,189,349),switch,scale=1.3114754098,metrics={'trackWidth':36,'trackHeight':18},note='حالت روشن از مرجع؛ حالت focus و disabled تکمیل دسترس‌پذیری است.')
add('C17','کلید حالت امنیتی','R05',(169,12,288,36),'<div style="background:var(--mr-header-bg);color:var(--mr-header-ink);height:24px">'+security_switch+'</div>','blue',metrics={'trackWidth':42,'trackHeight':20})
add('C18','کنترل افزایش و کاهش','R03',(368,384,630,408),stepper(),scale=1.3114754098,note='فقط تعداد را با stepUp/stepDown تغییر می‌دهد؛ به نرخ مالیات یا محاسبهٔ واقعی متصل نیست.')
add('C19','چک‌باکس و تعداد روش','R04',(682,213,764,236),'<div class="mr-quantity"><input class="mr-checkbox" type="checkbox" checked aria-label="فعال‌بودن روش">'+stepper(quantity=True)+'</div>',scale=1.3186813187)
add('C20','سرستون جدول متراکم','R04',(167,95,777,117),'<div class="mr-table-wrap"><table class="mr-table"><thead><tr>'+''.join(f'<th scope="col">{s}</th>' for s in ['ردیف','شماره فاکتور','عنوان فاکتور','تاریخ','مبلغ فاکتور','مبلغ','مبلغ باقی‌مانده','حذف'])+'</tr></thead></table></div>',scale=1.3186813187,category='جدول',note='در مرجع فقط سرستون دیده می‌شود؛ بدنهٔ جدول از این تصویر قابل استخراج نیست.')
add('C21','افزودن ردیف خط‌چین','R04',(167,133,777,158),'<button type="button" class="mr-add-row" data-mr-action="demo" data-mr-demo>'+icon('plus')+'افزودن ردیف</button>',scale=1.3186813187,category='دکمه')
methods='<section class="mr-panel"><h2 class="mr-panel-title">انتخاب نوع دریافت</h2><div class="mr-methods">'+''.join('<div class="mr-method"><span>'+s+'</span><div class="mr-quantity"><input type="checkbox" class="mr-checkbox" checked aria-label="'+s+'">'+stepper(quantity=True)+'</div></div>' for s in ['بانک','شخص','چک','تنخواه','صندوق','کارت‌خوان'])+'</div></section>'
add('C22','گروه انتخاب روش','R04',(167,168,777,240),methods,scale=1.3186813187,category='گروه',note='نمایش همهٔ روش‌ها صرفاً استخراج رابط است؛ تنها روش‌های موجود در سیستم میزبان باید متصل شوند.')
add('C23','کارت شخص و مبلغ','R07',(92,239,1034,384),party(),'blue',category='کارت',metrics={'width':942,'height':145,'radius':10,'avatarColumn':128,'columnGap':79},note='مبلغ متن نمونه است؛ کتابخانه هیچ جمع یا ثبت مالی انجام نمی‌دهد.')
add('C24','گروه کارت‌های تکرارشونده','R07',(59,206,1067,417),'<section class="mr-repeat-group">'+party()+'<button type="button" class="mr-group-add" aria-label="افزودن شخص" data-mr-action="add-card" data-mr-demo>'+icon('plus')+'</button></section>','blue',category='گروه',metrics={'width':1008,'height':211,'padding':32,'radius':8})
add('C25','نوار مجموع و باقی‌مانده','R07',(64,491,540,531),summary,'blue',category='خلاصه',metrics={'width':476,'height':40},note='ارقام از مدل میزبان می‌آیند؛ جمع یا تبدیل پول در این کامپوننت وجود ندارد.')
add('C26','پنل باز و بسته‌شونده','R07',(590,491,1062,532),disclosure,'blue',category='گروه',metrics={'height':40})
add('C27','کارت اطلاعات بانک','R07',(92,628,1034,768),bank,'blue',category='کارت',note='پایین کارت در تصویر بریده است؛ ارتفاع کامل و ردیف‌های پایین از عکس قابل تأیید نیستند.',status='partially-visible')
add('C28','کارت ابزارک داشبورد','R05',(619,143,1076,511),widget,'blue',category='کارت',metrics={'width':457,'height':368,'padding':20,'radius':8})
add('C29','ردیف سند در ابزارک','R05',(640,220,1055,300),ledger(),'blue',category='فهرست',metrics={'height':80,'radius':8})
add('C30','برچسب شماره و وضعیت','R05',(978,232,1042,257),'<span class="mr-badge" style="width:64px">۳۳</span>','blue',category='برچسب',metrics={'width':64,'height':25})
add('C31','نشان تعداد','R05',(311,158,380,199),'<span class="mr-count" style="width:69px">تعداد: <strong>۱۰</strong></span>','blue',category='برچسب',metrics={'width':69,'height':41})
add('C32','پنجرهٔ انتخاب ابزارک','R06',(357,94,1009,563),modal,'blue',category='مودال',metrics={'width':652,'height':469,'topBorder':6,'padding':24},note='آیکن‌های ابزارک از خود screenshot استخراج شده‌اند؛ نسخهٔ اصلی با وضوح بالاتر موجود نیست.')
add('C33','گزینهٔ انتخاب ابزارک','R06',(794,182,985,254),tile(*widget_types[0]),'blue',category='مودال',metrics={'width':191,'height':72,'radius':9})
add('C34','نوار عملیات پایین فرم','R02',(44,604,806,640),'<footer class="mr-footer-actions">'+button('ذخیره تغییرات')+'</footer>',scale=1.4457831325,category='پوسته',metrics={'height':52})
security = '<section class="mr-security">'+icon('pc-display')+'<div><h2>رمز عبور اصلی</h2><p>برای بالا بردن امنیت، رمز عبور خود را تغییر دهید.</p></div>'+button('تغییر رمز عبور',action='toggle-password',ico='pencil')+'</section><section data-mr-password hidden class="mr-stack" style="padding:16px">'+field('رمز عبور جدید',extra='type="password" autocomplete="new-password"')+'</section>'
add('C35','ردیف امنیت و تغییر رمز','R01',(103,257,587,294),security,scale=1.4371257485,category='هویت')
add('C36','شبکهٔ فرم کسب و کار','R02',(77,265,578,601),business,scale=1.4457831325,category='چیدمان',note='چیدمان فرم یک الگوست؛ نام فیلدها و binding از سیستم میزبان حفظ می‌شوند.')
add('C37','انتخاب شرکت و سال مالی','R02',(835,162,985,241),context,scale=1.4457831325,category='پوسته')
def dense_input(label):
    return '<input class="mr-input" aria-label="'+label+'" placeholder="'+label+'">'
transaction_bank = '<section class="mr-card mr-dense-destination"><header class="mr-row mr-between"><span>بانک</span>'+button('جستجوی لیست','mr-button--soft',ico='search')+button('حذف','mr-button--danger',ico='trash')+'</header><div class="mr-dense-fields"><div class="mr-dense-code">'+dense_input('کد')+dense_input('نام بانک')+'</div><div class="mr-grid">'+dense_input('شعبه بانک')+dense_input('شماره حساب')+'</div><div class="mr-grid mr-grid--three">'+field('مبلغ','۰',True)+field('کارمزد','۰',True)+field('نوع ارز','ریال',True)+'</div>'+dense_input('شرح')+'</div></section>'
add('C38','کارت مقصد دریافت متراکم','R04',(485,297,757,463),transaction_bank,scale=1.3186813187,category='کارت',note='نام بانک/شعبه/حساب نمونهٔ مرجع است؛ حذف یا افزودن هیچ فیلد backend مجاز نیست.')

manifest = {
    'name':'Mashal Reference Component Kit','version':'1.0.0','components':data,
    'referenceFiles':[{'file':p.name,'size':list(Image.open(p).size),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in refs.values()],
    'calibration':'R01–R04 are JPEGs with inferred CSS scale; R05–R07 are direct pixel crops. Annotation strokes are evidence only, never component chrome.',
    'fidelity':'Measured extraction; no claim of 100% pixel identity. Font identity, icon provenance, original avatar assets and unseen states remain unverified.',
    'financialScope':'No backend, no monetary arithmetic, no migrations, no network requests from behaviors.js.'
}
(ROOT / 'catalog.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(f'Built {len(data)} component templates and reference crops from {len(refs)} source images.')
