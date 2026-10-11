// امانی — نمایش فقط فیلدهای واحدِ انتخاب‌شده (MT یا لیتر). فیلدهای غیرفعال ارسال نمی‌شوند.
(() => {
    const bind = (scope) => {
        const unit = scope.querySelector('[data-custody-unit]');
        if (!unit) return;
        const update = () => scope.querySelectorAll('[data-custody-measurement]').forEach(group => {
            const active = group.dataset.custodyMeasurement === unit.value;
            group.hidden = !active;
            group.querySelectorAll('input').forEach(input => { input.disabled = !active; });
        });
        unit.addEventListener('change', update);
        update();
    };
    document.querySelectorAll('[data-custody-measure-scope]').forEach(bind);
})();
