/*
 * PTG — پیش‌نویس خودکار فرم‌های ثبت.
 *
 * فقط فرمی پیش‌نویس می‌شود که فیلد [data-ptg-draft] (Html.FormDraft) دارد. فیلدها دقیقاً مثل بدنهٔ POST
 * (new FormData) خوانده و پس از مکث تایپ در دیتابیس سیستم ذخیره می‌شوند؛ هر کلید نیست که درخواست بسازد.
 * - در هر لحظه فقط یک درخواست ذخیره برای هر فرم در جریان است؛ تغییر وسط ارسال، ذخیرهٔ بعدی را می‌سازد.
 * - Revision پایه جلوی بازنویسی نسخهٔ تازه‌تر را می‌گیرد؛ تعارض = ذخیرهٔ جداگانه با کلید تازه (هیچ چیز گم نمی‌شود).
 * - قطع اینترنت/خطای سرور: نسخهٔ محلی همین کاربر در localStorage، تلاش دوباره و همگام‌سازی پس از اتصال.
 * - پیش‌نویس فقط پس از نخستین تعامل واقعی کاربر ساخته می‌شود؛ بازکردن فرم به‌تنهایی پیش‌نویس نمی‌سازد.
 * هیچ منطق تجاری اینجا نیست؛ ثبت نهایی همان submit عادی فرم است.
 */
(function () {
    "use strict";

    var STORAGE_PREFIX = "ptg:form-draft:v1:";
    var DEBOUNCE_MS = 1500;
    var MAX_WAIT_MS = 10000;
    var RETRY_DELAYS_MS = [5000, 15000, 30000, 60000];
    var KEEPALIVE_LIMIT = 60000;
    var EXCLUDED = { "__RequestVerificationToken": 1, "__FormToken": 1, "__DraftKey": 1, "__DraftForm": 1 };

    var controllers = [];
    var syncRunning = false;

    function onReady(fn) {
        if (document.readyState === "loading") {
            document.addEventListener("DOMContentLoaded", fn, { once: true });
        } else {
            fn();
        }
    }

    function newKey() {
        if (window.crypto && typeof window.crypto.randomUUID === "function") {
            try { return window.crypto.randomUUID(); } catch (_) { /* http غیرامن: پایین */ }
        }
        var bytes = new Uint8Array(16);
        (window.crypto || window.msCrypto).getRandomValues(bytes);
        bytes[6] = (bytes[6] & 0x0f) | 0x40;
        bytes[8] = (bytes[8] & 0x3f) | 0x80;
        var hex = Array.prototype.map.call(bytes, function (b) { return (b + 0x100).toString(16).slice(1); }).join("");
        return hex.slice(0, 8) + "-" + hex.slice(8, 12) + "-" + hex.slice(12, 16) + "-" + hex.slice(16, 20) + "-" + hex.slice(20);
    }

    function hashText(text) {
        var h = 0x811c9dc5;
        for (var i = 0; i < text.length; i++) {
            h ^= text.charCodeAt(i);
            h = Math.imul(h, 0x01000193);
        }
        return text.length + ":" + (h >>> 0).toString(16);
    }

    function formatTime(date) {
        try {
            return date.toLocaleTimeString("fa-AF", { hour: "2-digit", minute: "2-digit" });
        } catch (_) {
            return date.getHours() + ":" + String(date.getMinutes()).padStart(2, "0");
        }
    }

    // ---- نسخهٔ محلی (فقط وقتی سرور در دسترس نیست یا صفحه بسته می‌شود) -------------------
    function storageKey(userId, key) {
        return STORAGE_PREFIX + userId + ":" + key;
    }

    function writeLocal(c, snap) {
        if (!c.key) return false;
        try {
            localStorage.setItem(storageKey(c.userId, c.key), JSON.stringify({
                key: c.key,
                formKey: c.formKey,
                baseRevision: c.revision,
                resumePath: resumePath(),
                summary: snap.summary,
                rowCount: snap.rowCount,
                fields: snap.fields,
                hash: snap.hash,
                savedAt: Date.now()
            }));
            return true;
        } catch (_) {
            return false;
        }
    }

    function removeLocal(userId, key, onlyHash) {
        try {
            var name = storageKey(userId, key);
            if (onlyHash) {
                var raw = localStorage.getItem(name);
                if (raw && JSON.parse(raw).hash !== onlyHash) return;
            }
            localStorage.removeItem(name);
        } catch (_) { /* دسترسی نیست */ }
    }

    function readLocalEntries(userId) {
        var result = [];
        try {
            var prefix = STORAGE_PREFIX + userId + ":";
            for (var i = 0; i < localStorage.length; i++) {
                var name = localStorage.key(i);
                if (!name || name.indexOf(prefix) !== 0) continue;
                try {
                    var entry = JSON.parse(localStorage.getItem(name));
                    if (entry && entry.key && entry.formKey && Array.isArray(entry.fields)) {
                        entry.storageName = name;
                        result.push(entry);
                    }
                } catch (_) { /* ورودی خراب نادیده گرفته می‌شود و پاک هم نمی‌شود */ }
            }
        } catch (_) { /* دسترسی نیست */ }
        return result;
    }

    function resumePath() {
        return location.pathname + location.search;
    }

    // ---- ارتباط با سرور -----------------------------------------------------------------
    function antiforgeryToken(root) {
        var input = (root && root.querySelector("input[name='__RequestVerificationToken']"))
            || document.querySelector("input[name='__RequestVerificationToken']");
        return input ? input.value : "";
    }

    function postSave(baseUrl, token, body, keepalive) {
        var json = JSON.stringify(body);
        return fetch(baseUrl + "/Save", {
            method: "POST",
            credentials: "same-origin",
            keepalive: !!keepalive && json.length < KEEPALIVE_LIMIT,
            headers: {
                "Content-Type": "application/json",
                "Accept": "application/json",
                "RequestVerificationToken": token,
                "X-Requested-With": "XMLHttpRequest"
            },
            body: json
        }).then(function (res) {
            var type = (res.headers.get("content-type") || "").toLowerCase();
            if (type.indexOf("application/json") < 0) {
                // ورود منقضی (صفحهٔ HTML ورود) یا خطای پروکسی: مثل قطع ارتباط رفتار می‌شود.
                return { httpStatus: res.status, data: { status: "network" } };
            }
            return res.json().then(function (data) {
                return { httpStatus: res.status, data: data || {} };
            });
        });
    }

    // ---- نشانگر وضعیت -------------------------------------------------------------------
    function ensureStatusElement(c) {
        var el = c.form.querySelector("[data-ptg-draft-status]");
        if (!el) {
            el = document.createElement("span");
            el.className = "ptg-draft-status";
            el.setAttribute("data-ptg-draft-status", "");
            el.setAttribute("role", "status");
            el.setAttribute("aria-live", "polite");
            el.hidden = true;
            var icon = document.createElement("i");
            icon.className = "bi";
            icon.setAttribute("aria-hidden", "true");
            var text = document.createElement("span");
            text.setAttribute("data-ptg-draft-status-text", "");
            el.appendChild(icon);
            el.appendChild(text);
            var link = document.createElement("a");
            link.className = "ptg-draft-status-link";
            link.href = c.baseUrl;
            link.textContent = "پیش‌نویس‌ها";
            el.appendChild(link);

            var footer = c.form.querySelector(".ak-footer-actions");
            var submit = c.form.querySelector("button[type='submit'], input[type='submit']");
            if (footer && submit && footer.contains(submit)) {
                // دکمه ممکن است داخل گروهِ دکمه‌های footer باشد، نه فرزند مستقیم آن.
                submit.parentNode.insertBefore(el, submit);
            } else if (footer) {
                footer.appendChild(el);
            } else if (submit && submit.parentNode) {
                submit.parentNode.insertBefore(el, submit);
            } else {
                c.form.appendChild(el);
            }
        }
        c.statusEl = el;
    }

    var STATUS = {
        resumed: { icon: "bi-journal-check", text: "ادامهٔ پیش‌نویس ذخیره‌شده" },
        saving: { icon: "bi-arrow-repeat", text: "در حال ذخیره…" },
        saved: { icon: "bi-cloud-check", text: "پیش‌نویس ذخیره شد" },
        error: { icon: "bi-exclamation-triangle", text: "ذخیره انجام نشد" },
        submitted: { icon: "bi-check2-circle", text: "این پیش‌نویس قبلاً ثبت نهایی شده است" }
    };

    function setStatus(c, state, title) {
        var el = c.statusEl;
        if (!el) return;
        var def = STATUS[state];
        el.hidden = false;
        el.className = "ptg-draft-status is-" + state;
        el.querySelector("i").className = "bi " + def.icon;
        el.querySelector("[data-ptg-draft-status-text]").textContent = def.text;
        if (title) {
            el.title = title;
        } else {
            el.removeAttribute("title");
        }
        c.status = state;
    }

    // ---- کنترل‌گر هر فرم ----------------------------------------------------------------
    function bind(marker) {
        var form = marker.form;
        if (!form || form.__ptgDraft) return;

        var c = {
            form: form,
            marker: marker,
            formKey: marker.getAttribute("data-ptg-draft"),
            userId: marker.getAttribute("data-ptg-draft-user") || "",
            baseUrl: marker.getAttribute("data-ptg-draft-url") || "/FormDrafts",
            key: marker.value || "",
            revision: parseInt(marker.getAttribute("data-ptg-draft-revision") || "0", 10) || 0,
            touched: false,
            lastHash: null,
            timer: null,
            firstPendingAt: 0,
            inFlight: null,
            again: false,
            retryTimer: null,
            retryIndex: 0,
            stopped: false,
            submitting: false
        };
        form.__ptgDraft = c;
        controllers.push(c);
        ensureStatusElement(c);

        if (c.key) setStatus(c, "resumed");
        // خط پایه: محتوای فعلی فرم (خالی یا همان نسخهٔ سرور) تا تغییر واقعی نشود فرستاده نمی‌شود؛
        // دوباره پس از اسکریپت‌های آغازین صفحه (قیمت، پیش‌فرض‌ها) گرفته می‌شود.
        function takeBaseline() {
            if (!c.touched && c.form.isConnected) c.lastHash = snapshot(c).hash;
        }
        window.setTimeout(takeBaseline, 0);
        window.setTimeout(takeBaseline, 1500);

        function markTouched(event) {
            if (!event.isTrusted || c.stopped) return;
            if (c.statusEl && c.statusEl.contains(event.target)) return;
            if (c.submitting && event.type !== "click") {
                // ارسال را اعتبارسنجی متوقف کرد و کاربر در حال اصلاح فرم است: ذخیرهٔ خودکار ادامه دارد.
                c.submitting = false;
            }
            c.touched = true;
            ensureKey(c);
            schedule(c);
        }

        form.addEventListener("input", markTouched, true);
        form.addEventListener("change", markTouched, true);
        // افزودن/حذف سطر و دکمه‌های داخل فرم (تغییرشان با MutationObserver هم دیده می‌شود).
        form.addEventListener("click", function (event) {
            if (event.target && event.target.closest && event.target.closest("button, [role='button'], a, label")) {
                markTouched(event);
            }
        }, true);

        if (window.MutationObserver) {
            c.observer = new MutationObserver(function (mutations) {
                if (!c.touched) return;
                for (var i = 0; i < mutations.length; i++) {
                    if (!c.statusEl || !c.statusEl.contains(mutations[i].target)) {
                        schedule(c);
                        return;
                    }
                }
            });
            c.observer.observe(form, { childList: true, subtree: true });
        }

        var syncing = syncLocal(c.userId, c.baseUrl, antiforgeryToken(form));
        if (!c.key) {
            showExistingDrafts(c, syncing);
        }
    }

    function ensureKey(c) {
        if (!c.key) {
            c.key = newKey();
            c.revision = 0;
            c.marker.value = c.key;
        }
    }

    function clearTimers(c) {
        if (c.timer) { window.clearTimeout(c.timer); c.timer = null; }
        if (c.retryTimer) { window.clearTimeout(c.retryTimer); c.retryTimer = null; }
        c.firstPendingAt = 0;
    }

    function schedule(c, delay) {
        if (c.stopped || c.submitting || !c.touched) return;
        if (c.timer) window.clearTimeout(c.timer);
        var now = Date.now();
        if (!c.firstPendingAt) c.firstPendingAt = now;
        var wait = typeof delay === "number"
            ? delay
            : Math.min(DEBOUNCE_MS, Math.max(0, c.firstPendingAt + MAX_WAIT_MS - now));
        c.timer = window.setTimeout(function () {
            c.timer = null;
            save(c);
        }, wait);
    }

    function snapshot(c) {
        var detail = { summary: null, rowCount: null };
        try {
            c.form.dispatchEvent(new CustomEvent("ptg:draft-serialize", { detail: detail }));
        } catch (_) { /* اسکریپت صفحه نباید ذخیره را بشکند */ }

        var fields = [];
        new FormData(c.form).forEach(function (value, name) {
            if (typeof value !== "string" || EXCLUDED[name]) return;
            fields.push([name, value]);
        });
        var json = JSON.stringify(fields);
        return {
            fields: fields,
            hash: hashText(json),
            summary: typeof detail.summary === "string" ? detail.summary : null,
            rowCount: typeof detail.rowCount === "number" && isFinite(detail.rowCount) ? detail.rowCount : null
        };
    }

    function save(c, snap, keepalive) {
        if (c.stopped || (c.submitting && !snap)) return;
        if (!snap && !c.form.isConnected) return;
        if (c.inFlight) {
            c.again = true;
            return;
        }

        c.firstPendingAt = 0;
        snap = snap || snapshot(c);
        if (snap.hash === c.lastHash) {
            if (c.status === "saving") setStatus(c, "saved");
            return;
        }

        ensureKey(c);
        setStatus(c, "saving");
        var body = {
            key: c.key,
            formKey: c.formKey,
            resumePath: resumePath(),
            summary: snap.summary,
            rowCount: snap.rowCount,
            baseRevision: c.revision,
            fields: snap.fields
        };

        c.inFlight = postSave(c.baseUrl, antiforgeryToken(c.form), body, keepalive)
            .then(function (result) { handleResult(c, snap, result); },
                function () { handleFailure(c, snap, "اتصال به سرور برقرار نشد."); })
            .then(function () {
                c.inFlight = null;
                if (c.again) {
                    c.again = false;
                    schedule(c, 0);
                }
            });
    }

    function handleResult(c, snap, result) {
        var data = result.data || {};
        var status = data.status;
        if (status === "saved") {
            if (typeof data.revision === "number" && data.revision > 0) c.revision = data.revision;
            c.lastHash = snap.hash;
            c.retryIndex = 0;
            removeLocal(c.userId, c.key, snap.hash);
            var at = data.savedAtUtc ? new Date(data.savedAtUtc) : new Date();
            if (c.form.isConnected) setStatus(c, "saved", "آخرین ذخیره: " + formatTime(at));
            return;
        }

        if (status === "submitted") {
            c.stopped = true;
            clearTimers(c);
            removeLocal(c.userId, c.key);
            if (c.form.isConnected) setStatus(c, "submitted");
            return;
        }

        if (status === "conflict") {
            if (c.submitting || !c.form.isConnected) {
                // صفحه ارسال شده یا رفته است؛ ثبت ناموفق محتوای ارسال‌شده را خودش در همین پیش‌نویس نگه داشته است.
                removeLocal(c.userId, c.key, snap.hash);
                return;
            }
            // نسخهٔ سرور در پنجرهٔ دیگری تغییر کرده یا حذف شده است: همین صفحه جداگانه ذخیره می‌شود.
            removeLocal(c.userId, c.key, snap.hash);
            c.key = "";
            c.revision = 0;
            c.lastHash = null;
            ensureKey(c);
            if (typeof window.ptgToast === "function") {
                window.ptgToast("info",
                    "این پیش‌نویس در پنجرهٔ دیگری تغییر کرده یا حذف شده بود؛ اطلاعات این صفحه به‌عنوان پیش‌نویس جداگانه ذخیره شد.",
                    "پیش‌نویس");
            }
            c.again = true;
            return;
        }

        if (status === "busy") {
            // همین پیش‌نویس در حال ثبت نهایی است؛ نتیجهٔ آن ثبت (موفق یا ناموفق) محتوا را نگه می‌دارد.
            if (c.submitting || !c.form.isConnected) return;
            writeLocal(c, snap);
            scheduleRetry(c);
            return;
        }

        if (status === "invalid" || status === "forbidden") {
            writeLocal(c, snap);
            if (c.form.isConnected) setStatus(c, "error", data.message || "این پیش‌نویس روی سرور ذخیره نشد؛ نسخهٔ آن در همین مرورگر نگه داشته شد.");
            return;
        }

        handleFailure(c, snap, data.message);
    }

    function handleFailure(c, snap, message) {
        var kept = writeLocal(c, snap);
        if (c.form.isConnected) {
            setStatus(c, "error",
                (message ? message + " " : "")
                + (kept ? "اطلاعات در همین مرورگر نگه داشته شد و پس از اتصال خودکار ذخیره می‌شود." : "دوباره تلاش می‌شود."));
        }
        scheduleRetry(c);
    }

    function scheduleRetry(c) {
        if (c.stopped || !c.form.isConnected) return;
        if (c.retryTimer) window.clearTimeout(c.retryTimer);
        var delay = RETRY_DELAYS_MS[Math.min(c.retryIndex, RETRY_DELAYS_MS.length - 1)];
        c.retryIndex++;
        c.retryTimer = window.setTimeout(function () {
            c.retryTimer = null;
            c.lastHash = null;
            save(c);
        }, delay);
    }

    // صفحه عوض می‌شود (SPA)، بسته یا Refresh می‌شود: آخرین تغییرها فوراً فرستاده و محلی هم نگه داشته می‌شوند.
    function flushAll(leaving) {
        controllers.forEach(function (c) {
            if (c.stopped || c.submitting || !c.touched || !c.form.isConnected) return;
            if (c.timer) { window.clearTimeout(c.timer); c.timer = null; }
            var snap = snapshot(c);
            if (snap.hash === c.lastHash) return;
            if (leaving) writeLocal(c, snap);
            if (c.inFlight) {
                c.again = true;
                return;
            }
            save(c, snap, leaving);
        });
    }

    // ---- همگام‌سازی نسخه‌های محلی ------------------------------------------------------
    function syncLocal(userId, baseUrl, token) {
        if (syncRunning || !userId || navigator.onLine === false) return Promise.resolve();
        var entries = readLocalEntries(userId).filter(function (entry) {
            // پیش‌نویسی که همین حالا در یک فرم باز در حال ویرایش است، به‌دست خودِ همان فرم ذخیره می‌شود.
            return !controllers.some(function (c) { return c.key === entry.key && c.form.isConnected && c.touched; });
        });
        if (!entries.length) return Promise.resolve();

        syncRunning = true;
        var reloadCurrent = false;
        var chain = Promise.resolve();
        entries.forEach(function (entry) {
            chain = chain.then(function () {
                return syncEntry(entry, baseUrl, token).then(function (outcome) {
                    var open = controllers.find(function (c) { return c.key === entry.key && c.form.isConnected; });
                    if (outcome === "saved" && open && !open.touched) reloadCurrent = true;
                });
            });
        });

        return chain.then(function () {
            syncRunning = false;
            if (reloadCurrent) {
                // نسخهٔ محلیِ تازه‌تر همین پیش‌نویس به سرور رسید؛ صفحه با همان نسخه دوباره ساخته می‌شود.
                location.reload();
            }
        }, function () { syncRunning = false; });
    }

    function syncEntry(entry, baseUrl, token) {
        var body = {
            key: entry.key,
            formKey: entry.formKey,
            resumePath: entry.resumePath,
            summary: entry.summary,
            rowCount: entry.rowCount,
            baseRevision: entry.baseRevision || 0,
            fields: entry.fields
        };
        return postSave(baseUrl, token, body, false).then(function (result) {
            var status = (result.data || {}).status;
            if (status === "saved" || status === "submitted") {
                localStorage.removeItem(entry.storageName);
                return status;
            }

            if (status === "conflict") {
                // نسخهٔ سرور تغییر کرده است؛ نسخهٔ محلی بی‌آنکه چیزی را بازنویسی کند پیش‌نویس جداگانه می‌شود.
                body.key = newKey();
                body.baseRevision = 0;
                return postSave(baseUrl, token, body, false).then(function (forked) {
                    if ((forked.data || {}).status === "saved") localStorage.removeItem(entry.storageName);
                    return "forked";
                });
            }

            return "kept";
        }, function () { return "kept"; });
    }

    // ---- پیش‌نویس‌های موجود همین فرم -----------------------------------------------------
    function showExistingDrafts(c, syncing) {
        syncing.then(function () {
            return fetch(c.baseUrl + "/List?formKey=" + encodeURIComponent(c.formKey), {
                credentials: "same-origin",
                headers: { "Accept": "application/json", "X-Requested-With": "XMLHttpRequest" }
            });
        }).then(function (res) {
            var type = res && (res.headers.get("content-type") || "").toLowerCase();
            return res && res.ok && type.indexOf("application/json") >= 0 ? res.json() : null;
        }).then(function (data) {
            if (!data || !Array.isArray(data.drafts) || !c.form.isConnected) return;
            var drafts = data.drafts.filter(function (d) { return d.key !== c.key; });
            if (!drafts.length) return;
            renderNotice(c, drafts, data.manageUrl);
        }).catch(function () { /* بدون اتصال: فقط ذخیرهٔ محلی */ });
    }

    function navigate(url) {
        if (window.PTG && typeof window.PTG.spaNavigate === "function") {
            window.PTG.spaNavigate(url);
        } else {
            location.href = url;
        }
    }

    function renderNotice(c, drafts, manageUrl) {
        if (c.form.parentNode.querySelector("[data-ptg-draft-notice]")) return;
        var latest = drafts[0];
        var notice = document.createElement("div");
        notice.className = "alert alert-info ptg-draft-notice";
        notice.setAttribute("data-ptg-draft-notice", "");
        notice.setAttribute("role", "status");

        var text = document.createElement("div");
        text.className = "ptg-draft-notice-text";
        var title = document.createElement("strong");
        title.textContent = drafts.length === 1
            ? "یک پیش‌نویس ذخیره‌شده برای این فرم دارید."
            : drafts.length + " پیش‌نویس ذخیره‌شده برای این فرم دارید.";
        var meta = document.createElement("span");
        var parts = [];
        if (latest.summary) parts.push(latest.summary);
        if (latest.rowCount > 0) parts.push(latest.rowCount + " سطر");
        if (latest.lastSavedText) parts.push("آخرین ذخیره: " + latest.lastSavedText);
        meta.textContent = parts.join(" · ");
        text.appendChild(title);
        text.appendChild(meta);

        var actions = document.createElement("div");
        actions.className = "ptg-draft-notice-actions";
        var resume = document.createElement("a");
        resume.className = "ak-btn";
        resume.href = latest.resumeUrl;
        resume.textContent = drafts.length === 1 ? "ادامهٔ پیش‌نویس" : "ادامهٔ آخرین پیش‌نویس";
        resume.addEventListener("click", function (event) {
            if (c.touched) {
                // کار این صفحه هم پیش‌نویس جداگانه است؛ پیش از رفتن ذخیره می‌شود.
                flushAll(true);
            }
            event.preventDefault();
            navigate(latest.resumeUrl);
        });
        actions.appendChild(resume);

        if (manageUrl) {
            var manage = document.createElement("a");
            manage.className = "ak-btn ak-btn--ghost";
            manage.href = manageUrl;
            manage.textContent = "همهٔ پیش‌نویس‌ها";
            actions.appendChild(manage);
        }

        var fresh = document.createElement("button");
        fresh.type = "button";
        fresh.className = "ak-btn ak-btn--ghost";
        fresh.textContent = "فرم جدید";
        fresh.title = "پیش‌نویس‌های قبلی حذف نمی‌شوند و در فهرست پیش‌نویس‌ها می‌مانند.";
        fresh.addEventListener("click", function () { notice.remove(); });
        actions.appendChild(fresh);

        notice.appendChild(text);
        notice.appendChild(actions);
        c.form.parentNode.insertBefore(notice, c.form);
    }

    // ---- راه‌اندازی -------------------------------------------------------------------
    function init() {
        controllers = controllers.filter(function (c) { return c.form.isConnected || c.inFlight; });
        document.querySelectorAll("input[data-ptg-draft]").forEach(bind);

        // صفحهٔ فهرست پیش‌نویس‌ها هم نسخه‌های محلیِ همگام‌نشده را می‌فرستد.
        var syncMarker = document.querySelector("[data-ptg-draft-sync]");
        if (syncMarker && !syncMarker.__ptgDraftSynced) {
            syncMarker.__ptgDraftSynced = true;
            var userId = syncMarker.getAttribute("data-ptg-draft-user");
            var before = readLocalEntries(userId).length;
            if (before > 0) {
                syncLocal(userId, syncMarker.getAttribute("data-ptg-draft-url"), antiforgeryToken(document)).then(function () {
                    // پیش‌نویس محلیِ تازه به سرور رسید: فهرست با آن دوباره ساخته می‌شود.
                    if (syncMarker.isConnected && readLocalEntries(userId).length < before) location.reload();
                });
            }
        }
    }

    // ثبت نهایی: در فاز capture پنجره (پیش از ناوبری SPA که رویداد را متوقف می‌کند) دیده می‌شود.
    window.addEventListener("submit", function (event) {
        var c = event.target && event.target.__ptgDraft;
        if (!c || c.stopped) return;
        if (c.touched) ensureKey(c);
        c.submitting = true;
        clearTimers(c);
    }, true);

    onReady(init);
    window.addEventListener("ptg:page-ready", init);
    window.addEventListener("ptg:before-swap", function () { flushAll(true); });
    window.addEventListener("pagehide", function () { flushAll(true); });
    document.addEventListener("visibilitychange", function () {
        if (document.visibilityState === "hidden") flushAll(false);
    });
    window.addEventListener("online", function () {
        controllers.forEach(function (c) {
            if (c.status === "error" && !c.stopped) {
                c.retryIndex = 0;
                c.lastHash = null;
                schedule(c, 0);
            }
        });
        var first = controllers[0];
        if (first) syncLocal(first.userId, first.baseUrl, antiforgeryToken(first.form));
    });

    window.PTG = window.PTG || {};
    window.PTG.formDrafts = {
        flush: function () { flushAll(false); },
        pendingLocalCount: function (userId) { return readLocalEntries(userId).length; }
    };
})();
