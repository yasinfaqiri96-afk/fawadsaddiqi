"""Capture real authenticated Razor pages and exercise presentation controls.
Requires playwright (Python), Chromium and an already running app.
The optional --storage-state file is a local secret and must never be committed.
"""
import argparse
import json
from pathlib import Path
from playwright.sync_api import sync_playwright

parser = argparse.ArgumentParser()
parser.add_argument('--base-url', default='https://127.0.0.1:5001')
parser.add_argument('--storage-state')
parser.add_argument('--output', default='artifacts/ui-reference/final')
parser.add_argument('--company-id', type=int, default=1)
parser.add_argument('--sale-id', type=int)
args = parser.parse_args()
out = Path(args.output)
out.mkdir(parents=True, exist_ok=True)
calibration = json.loads(Path('docs/ui-reference/calibration.json').read_text())
routes = {
    'profile': '/Auth/ChangePassword',
    'business': f'/Companies/Edit/{args.company_id}',
    'accounting': '/accounting/chart-of-accounts/mapping',
    'settings': '/SystemSettings',
    'transaction': '/Payments/Create',
    'dashboard': '/',
    'inventory': '/Inventory',
    'sales': '/Sales',
}
if args.sale_id:
    routes['receipt'] = f'/Payments/Create?salesTransactionId={args.sale_id}'

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, args=['--no-sandbox', '--ignore-certificate-errors'])
    context = browser.new_context(ignore_https_errors=True, device_scale_factor=1,
                                  storage_state=args.storage_state)
    page = context.new_page()
    errors, checks, metrics = [], {}, {}
    page.on('pageerror', lambda error: errors.append(str(error)))
    page.on('console', lambda message: errors.append('console: ' + message.text)
            if message.type == 'error' else None)

    def visit(route):
        response = page.goto(args.base_url + route)
        page.evaluate('document.fonts.ready')
        page.wait_for_timeout(500)
        later = page.locator('[data-offline-later]')
        if later.count():
            later.click()
        fresh = page.get_by_role('button', name='فرم جدید', exact=True)
        if fresh.count():
            fresh.click()
        assert response.status == 200, (route, response.status)
        assert page.locator('#ptg-main-content').count(), 'Authenticated app required'

    def open_picker():
        page.evaluate("""() => { window.qaModalReady = new Promise(resolve => document.getElementById('mashalWidgetPicker').addEventListener('shown.bs.modal', () => resolve(true), {once:true})); }""")
        page.locator('.mashal-widget-add').click()
        page.evaluate('window.qaModalReady')

    # Development's existing auto sign-in needs a second navigation to use its cookie.
    page.goto(args.base_url + '/')
    visit('/')
    for width, height, group in [(1366, 768, 'desktop'), (1440, 900, 'wide'), (390, 844, 'mobile')]:
        page.set_viewport_size({'width': width, 'height': height})
        for name, route in routes.items():
            visit(route)
            page.screenshot(path=str(out / f'{group}-{name}.png'))
            metrics[f'{group}-{name}'] = page.evaluate('''() => ({
                viewport: [innerWidth, innerHeight], dpr: devicePixelRatio,
                overflow: document.documentElement.scrollWidth > innerWidth,
                shellOverflow: document.querySelector('.ptg-app').scrollWidth > innerWidth,
                sidebar: document.querySelector('.ptg-sidebar').getBoundingClientRect().toJSON(),
                topbar: document.querySelector('.ptg-topbar').getBoundingClientRect().toJSON()
            })''')

    for reference in calibration['references']:
        name = reference['capture']
        if name == 'receipt' and not args.sale_id:
            continue
        page.set_viewport_size(dict(zip(['width', 'height'], reference['viewport'])))
        visit(routes.get(name, '/'))
        if name == 'widgets':
            open_picker()
            page.locator('#mashalWidgetPicker').wait_for(state='visible')
            page.wait_for_timeout(250)
        page.screenshot(path=str(out / f'calibrated-{name}.png'))
        if name in ['dashboard', 'widgets']:
            metrics['calibrated-' + name] = page.evaluate('''() => {
                const box = s => document.querySelector(s)?.getBoundingClientRect().toJSON();
                return {toolbar:box('.mashal-dashboard-toolbar'), cards:[...document.querySelectorAll('.mashal-widget')].map(e=>e.getBoundingClientRect().toJSON()), modal:box('#mashalWidgetPicker .modal-content')};
            }''')
        if name == 'widgets':
            page.keyboard.press('Escape')
            page.locator('#mashalWidgetPicker').wait_for(state='hidden')
            page.wait_for_function("document.querySelector('.mashal-widget-add') === document.activeElement", timeout=1500)
            checks['modal_escape_and_return_focus'] = page.locator('.mashal-widget-add').evaluate('(el) => el === document.activeElement')

    # Widget selection, persistence, select all, cancel and keyboard focus trap.
    page.set_viewport_size({'width': 1366, 'height': 768})
    visit('/')
    open_picker()
    page.locator('[data-mashal-widget-choice][value="tanks"]').uncheck()
    page.locator('[data-mashal-widgets-apply]').click()
    page.locator('#mashalWidgetPicker').wait_for(state='hidden')
    page.reload()
    checks['widget_persistence'] = page.locator('[data-mashal-widget="tanks"]').is_hidden()
    open_picker()
    page.locator('[data-mashal-widgets-all]').check()
    page.locator('[data-mashal-widgets-apply]').click()
    page.locator('#mashalWidgetPicker').wait_for(state='hidden')
    checks['widget_select_all'] = page.locator('[data-mashal-widget="tanks"]').is_visible()
    open_picker()
    page.locator('[data-mashal-widgets-all]').uncheck()
    page.keyboard.press('Escape')
    page.locator('#mashalWidgetPicker').wait_for(state='hidden')
    checks['widget_cancel_preserves_selection'] = page.locator('[data-mashal-widget="tanks"]').is_visible()
    open_picker()
    page.wait_for_timeout(300)
    for _ in range(12):
        page.keyboard.press('Tab')
        page.wait_for_timeout(30)
        assert page.evaluate("document.activeElement.closest('#mashalWidgetPicker') !== null")
    checks['modal_focus_trap'] = True
    page.keyboard.press('Escape')
    page.locator('#mashalWidgetPicker').wait_for(state='hidden')

    visit('/Auth/ChangePassword')
    page.locator('.mashal-password-details summary').click()
    checks['password_disclosure'] = page.locator('[name="CurrentPassword"]').is_visible()
    page.screenshot(path=str(out / 'desktop-profile-security.png'))

    visit('/Payments/Create')
    page.locator('[data-direction-choice][data-direction-value="2"]').click()
    checks['payment_direction'] = page.locator('select[name="Direction"]').input_value() == '2'
    page.locator('[data-payment-method-choice]').last.click()
    checks['sarraf_method_enables_real_fields'] = page.locator('[data-payment-sarraf-section]').is_visible()
    page.locator('[data-payment-method-choice]').first.click()
    checks['cashbank_method_restored'] = page.locator('#cashAccountSelect').is_visible()

    if args.sale_id:
        visit(routes['receipt'])
        page.locator('.mashal-receipt-options > summary').click()
        page.locator('[data-payment-method-choice]').last.click()
        checks['invoice_sarraf_preserves_disabled_post_fields'] = page.evaluate("!new FormData(document.getElementById('roznamchaForm')).has('CounterpartyType') && !new FormData(document.getElementById('roznamchaForm')).has('Amount')")
        page.locator('[data-payment-method-choice]').first.click()
        checks['invoice_cashbank_preserves_post_fields'] = page.evaluate("new FormData(document.getElementById('roznamchaForm')).has('CounterpartyType') && new FormData(document.getElementById('roznamchaForm')).has('Amount') && new FormData(document.getElementById('roznamchaForm')).has('Description')")

    # Existing shell SPA transition must also switch the route-specific variant.
    visit('/')
    page.locator('.boltz-sidebar-foot a[href="/Auth/ChangePassword"]').click()
    page.wait_for_url('**/Auth/ChangePassword')
    page.wait_for_timeout(500)
    checks['spa_light_variant'] = page.locator('body.mashal-light').count() == 1
    page.locator('.boltz-sidebar-nav a[href="/"]').first.click()
    page.wait_for_url(args.base_url + '/')
    page.wait_for_timeout(500)
    checks['spa_legacy_variant'] = page.locator('body.mashal-legacy').count() == 1
    open_picker()
    page.locator('#mashalWidgetPicker').wait_for(state='visible', timeout=3000)
    checks['modal_after_spa'] = page.locator('#mashalWidgetPicker').is_visible()
    page.keyboard.press('Escape')

    (out / 'browser-checks.json').write_text(json.dumps({
        'browser': browser.version, 'checks': checks, 'metrics': metrics,
        'console_errors': errors, 'density': 1, 'zoom': 1,
    }, ensure_ascii=False, indent=2))
    print(json.dumps({'checks': checks, 'console_errors': errors}, ensure_ascii=False))
    assert all(checks.values()), checks
    assert not any(value['overflow'] or value['shellOverflow'] for key, value in metrics.items() if 'overflow' in value), 'Viewport overflow'
    browser.close()
