/*
 * PTG Oil System - Tables Module
 * Handles responsive tables, clickable rows, and preserves inline row actions.
 */

(function () {
    "use strict";

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init, { once: true });
    } else {
        init();
    }

    window.addEventListener("ptg:page-ready", function () {
        window.requestAnimationFrame(init);
    });

    function init() {
        initializeResponsiveTables();
        initializeListRowAvatars();
        initializeClickableTableRows();
        initializeAkRowNavigation();
        initializeTableActionMenus();
        initializeContextualRowActions();
        initializeBulkSelection();
        initializeClientTablePagination();
    }

    // Give every canonical Index list the same compact navy row marker used by
    // Customers/Users. Human and party lists already render _PersonCell, so
    // those rows are left untouched. The icon changes by route/module while
    // size, colour and spacing stay one shared visual contract.
    function initializeListRowAvatars() {
        if (!document.body.classList.contains("action-index")) return;

        var iconClass = resolveListRowIcon();
        document.querySelectorAll(".ak-table").forEach(function (table) {
            // صفحهٔ خانوادهٔ Details (مثل صورت‌حساب شراکت) فهرست نیست؛ جدولِ آن نشانگرِ ردیف نمی‌گیرد.
            if (table.closest("[data-ak-detail-v2='true']")) return;
            Array.prototype.forEach.call(table.tBodies, function (body) {
                Array.prototype.forEach.call(body.rows, function (row) {
                    if (row.dataset.akEntityAvatarReady === "true") return;
                    row.dataset.akEntityAvatarReady = "true";

                    if (row.querySelector(".ptg-person-avatar, .ptg-list-row-avatar, .ak-empty")) return;

                    var cells = Array.prototype.filter.call(row.children, function (cell) {
                        return cell.matches && cell.matches("td, th")
                            && !cell.matches(".ak-col-check, .ak-col-actions, .ak-col-spacer")
                            && !cell.querySelector(".ak-check")
                            && Number(cell.getAttribute("colspan") || "1") === 1;
                    });
                    if (!cells.length) return;

                    var cell = cells.find(function (candidate) { return candidate.querySelector(".ak-name"); })
                        || cells.find(function (candidate) {
                            return candidate.querySelector("a[href]") && !candidate.querySelector(".dropdown, form");
                        })
                        || cells[0];
                    if (!cell || cell.querySelector(".ptg-list-entity-cell")) return;

                    var wrapper = document.createElement("div");
                    wrapper.className = "ptg-list-entity-cell";

                    var avatar = document.createElement("span");
                    avatar.className = "ptg-list-row-avatar";
                    avatar.setAttribute("aria-hidden", "true");

                    var icon = document.createElement("i");
                    icon.className = "bi " + iconClass;
                    avatar.appendChild(icon);

                    var content = document.createElement("div");
                    content.className = "ptg-list-entity-content";
                    while (cell.firstChild) content.appendChild(cell.firstChild);

                    wrapper.appendChild(avatar);
                    wrapper.appendChild(content);
                    cell.appendChild(wrapper);
                });
            });
        });
    }

    function resolveListRowIcon() {
        var segment = (location.pathname.split("/").filter(Boolean)[0] || "").toLowerCase();
        var icons = {
            accountstatements: "bi-journal-text",
            auditlogs: "bi-shield-check",
            cashaccounts: "bi-wallet2",
            chartofaccounts: "bi-diagram-3",
            closingchecklist: "bi-list-check",
            companies: "bi-building",
            contractamendments: "bi-file-earmark-diff",
            contractbalancetransfers: "bi-arrow-left-right",
            contractjourney: "bi-signpost-split",
            contracts: "bi-file-earmark-text",
            currencies: "bi-currency-exchange",
            customers: "bi-person-fill",
            customsdeclarations: "bi-file-earmark-check",
            customspermitturnover: "bi-arrow-repeat",
            dailyfxrates: "bi-graph-up-arrow",
            dispatch: "bi-truck",
            drivers: "bi-person-badge",
            employees: "bi-person-vcard",
            expenserules: "bi-sliders",
            expenses: "bi-receipt",
            expensetypes: "bi-tags",
            finalclose: "bi-lock-fill",
            fiscalyears: "bi-calendar3",
            inventory: "bi-boxes",
            inventorytransportlegs: "bi-arrow-left-right",
            ledger: "bi-journal-bookmark",
            loading: "bi-box-arrow-up",
            loadingreceipts: "bi-box-arrow-in-down",
            locations: "bi-geo-alt",
            lossevents: "bi-exclamation-triangle",
            operationalassets: "bi-tools",
            partners: "bi-people-fill",
            payments: "bi-cash-coin",
            plattsrates: "bi-graph-up",
            products: "bi-droplet-fill",
            roles: "bi-person-lock",
            sales: "bi-cart-check",
            sarrafs: "bi-bank",
            sarrafsettlements: "bi-arrow-left-right",
            serviceproviders: "bi-briefcase",
            shipmentcontracts: "bi-files",
            shipmentpnl: "bi-water",
            storagetanks: "bi-database",
            suppliers: "bi-building-check",
            terminals: "bi-pin-map",
            threewaysettlement: "bi-diagram-3",
            trialclose: "bi-clipboard-check",
            trucks: "bi-truck-front",
            trucksettlements: "bi-cash-stack",
            units: "bi-rulers",
            users: "bi-person-fill",
            vessels: "bi-water",
            wagons: "bi-train-front"
        };
        return icons[segment] || "bi-list-ul";
    }

    // Contextual row actions — replaces the always-visible ⋯ menu with a small
    // hover/focus action box at the row end. The box is *derived* from the
    // existing `.ak-row-menu` dropdown items, so it inherits their real,
    // permission-filtered actions, hrefs, delete-confirmation forms and toggle
    // POSTs verbatim; each action button just re-fires its source element.
    // No per-view markup and no new endpoints. The source dropdown is hidden
    // (kept in the DOM so programmatic .click() still navigates / submits).
    function initializeContextualRowActions() {
        document.querySelectorAll(".ak-table").forEach(function (table) {
            if (table.dataset.akActionsTableReady === "true") return;
            table.dataset.akActionsTableReady = "true";

            var boxes = [];
            Array.prototype.forEach.call(table.tBodies, function (body) {
                Array.prototype.forEach.call(body.rows, function (row) {
                    var box = buildRowActionBox(row);
                    if (box) boxes.push(box);
                });
            });
            if (!boxes.length) return;

            // Reserve a fixed end column sized to the widest real action box, so
            // the absolute overlay always sits inside its own cell (never over
            // Status/badge/amount/text) and thead/tbody stay aligned. Sized to
            // the true button count — few actions reserve less, none reserve
            // nothing (var stays unset → 44px default).
            var maxWidth = 0;
            boxes.forEach(function (box) {
                var width = box.getBoundingClientRect().width;
                if (width > maxWidth) maxWidth = width;
            });
            if (maxWidth > 0) {
                table.style.setProperty("--ak-actions-w", (Math.round(maxWidth) + 16) + "px");
            }
        });
    }

    // Build one icon button for an action descriptor. Shared by the hover row
    // box and the bulk toolbar so the two are visually + behaviourally identical.
    // Click re-fires the real source control (href / form submit / data-ptg-confirm
    // / SPA nav / permission all preserved) and never triggers row navigation.
    function makeActionButton(action) {
        var button = document.createElement("button");
        button.type = "button";
        button.className = "ak-row-action";
        if (action.danger) button.classList.add("is-danger");
        if (action.label) {
            button.title = action.label;               // Persian tooltip
            button.setAttribute("aria-label", action.label);
        }
        if (action.iconClass) {
            var icon = document.createElement("i");
            icon.className = action.iconClass;
            icon.setAttribute("aria-hidden", "true");
            button.appendChild(icon);
        }
        button.addEventListener("click", function (event) {
            event.preventDefault();
            event.stopPropagation();
            action.source.click();
        });
        return button;
    }

    // Build one hover/focus action box for a row from its (hidden) ⋯ dropdown.
    // Returns the box element (for width measurement) or null when the row has
    // no actions cell / no actions — those rows reserve no extra space.
    function buildRowActionBox(row) {
        if (row.dataset.akActionsReady === "true") return null;
        row.dataset.akActionsReady = "true";

        var cell = Array.prototype.find.call(row.children, function (c) {
            return c.matches && c.matches(".ak-col-actions");
        });
        var menu = cell ? cell.querySelector(".ak-row-menu") : null;
        if (menu && menu.hasAttribute("data-ak-static-row-menu")) return null;
        var items = menu
            ? Array.prototype.slice.call(menu.querySelectorAll(".dropdown-menu .dropdown-item"))
            : [];
        if (!cell || !items.length) return null;

        // One action descriptor per real dropdown item — the single source of
        // truth reused by BOTH the hover box and the bulk toolbar (same icon,
        // order, label, re-fired source element). No second icon mapping.
        var actions = items.map(function (source) {
            var iconEl = source.querySelector(".bi");
            return {
                label: normalizeText(source.textContent),
                iconClass: iconEl ? iconEl.className : "",
                danger: source.classList.contains("ak-danger"),
                source: source
            };
        });
        row.ptgRowActions = actions;

        var box = document.createElement("div");
        box.className = "ak-row-actions";
        box.setAttribute("role", "toolbar");
        box.setAttribute("aria-label", "عملیات ردیف");
        actions.forEach(function (action) { box.appendChild(makeActionButton(action)); });

        // Keyboard: Escape dismisses the open box; focus falls back to the row
        // name link so focus is never lost. The hush is cleared when focus
        // leaves the row or the mouse re-enters, so it can reopen normally.
        box.addEventListener("keydown", function (event) {
            if (event.key !== "Escape" && event.key !== "Esc") return;
            event.stopPropagation();
            row.classList.add("ak-actions-hush");
            var fallback = row.querySelector(".ak-name") || row;
            if (fallback && fallback.focus) fallback.focus();
        });
        row.addEventListener("mouseenter", function () {
            row.classList.remove("ak-actions-hush");
        });
        row.addEventListener("focusout", function (event) {
            if (!row.contains(event.relatedTarget)) row.classList.remove("ak-actions-hush");
        });

        cell.classList.add("ak-actions-host");
        cell.appendChild(box);
        return box;
    }

    // Bulk selection. The toolbar shares the search bar's slot (.ak-filter-host):
    // checking a row fades the search out and the toolbar in, at the exact same
    // place/size — no new row, no layout shift. Exactly one row selected → that
    // row's real hover actions appear in the toolbar (same source elements);
    // many rows → only count + clear (no bulk endpoints exist, so nothing fake
    // and no sequential requests). SPA nav swaps table + host wholesale, dropping
    // stale selection.
    function initializeBulkSelection() {
        document.querySelectorAll(".ak-table").forEach(function (table) {
            if (table.dataset.bulkReady === "true") return;

            var headCheck = table.querySelector("thead .ak-check");
            if (!headCheck) return;
            table.dataset.bulkReady = "true";

            var page = table.closest(".ak-list-page") || table.closest(".ak-list") || document;
            var host = page.querySelector ? page.querySelector(".ak-filter-host") : null;

            var toolbar = null;
            if (host && host.dataset.bulkHost !== "true") {
                host.dataset.bulkHost = "true";
                toolbar = buildBulkToolbar();
                host.appendChild(toolbar.root);
            }

            function rowChecks() {
                return Array.prototype.slice.call(table.querySelectorAll("tbody .ak-check"));
            }

            function refresh() {
                var checks = rowChecks();
                var chosen = checks.filter(function (c) { return c.checked; })
                    .map(function (c) { return c.closest("tr"); })
                    .filter(Boolean);
                var n = chosen.length;

                checks.forEach(function (check) {
                    var tr = check.closest("tr");
                    if (tr) tr.classList.toggle("is-selected", check.checked);
                });

                headCheck.checked = checks.length > 0 && n === checks.length;
                headCheck.indeterminate = n > 0 && n < checks.length;

                if (!toolbar) return;
                if (n > 0) {
                    toolbar.count.textContent = selectionCountText(n);
                    renderToolbarActions(toolbar.actions, n === 1 ? chosen[0] : null);
                    host.classList.add("is-bulk-active");
                } else {
                    host.classList.remove("is-bulk-active");
                    toolbar.actions.replaceChildren();
                }
            }

            headCheck.addEventListener("change", function () {
                var on = headCheck.checked;
                rowChecks().forEach(function (check) { check.checked = on; });
                refresh();
            });

            table.addEventListener("change", function (event) {
                var target = event.target;
                if (target && target.classList.contains("ak-check") && target !== headCheck) {
                    refresh();
                }
            });

            if (toolbar) {
                toolbar.clear.addEventListener("click", function () {
                    headCheck.checked = false;
                    headCheck.indeterminate = false;
                    rowChecks().forEach(function (check) { check.checked = false; });
                    refresh();
                });
            }

            refresh();
        });
    }

    // Exactly one row selected → its real hover actions, same icons/order. Many
    // rows → none (no bulk endpoints; never fabricate or fire sequential POSTs).
    function renderToolbarActions(container, row) {
        container.replaceChildren();
        if (!row || !row.ptgRowActions) return;
        row.ptgRowActions.forEach(function (action) {
            var button = makeActionButton(action);
            button.classList.add("ak-bulk-action");
            container.appendChild(button);
        });
    }

    function buildBulkToolbar() {
        var root = document.createElement("div");
        root.className = "ak-bulk-toolbar";
        root.setAttribute("role", "toolbar");

        var count = document.createElement("span");
        count.className = "ak-selection-count";

        var actions = document.createElement("div");
        actions.className = "ak-bulk-actions";

        var clear = document.createElement("button");
        clear.type = "button";
        clear.className = "ak-bulk-clear";
        clear.textContent = "پاک‌کردن";

        root.appendChild(count);
        root.appendChild(actions);
        root.appendChild(clear);
        return { root: root, count: count, actions: actions, clear: clear };
    }

    // «N <entity> انتخاب شده» — entity noun by page (fallback «مورد»), Persian digits.
    function selectionCountText(n) {
        return toPersianDigits(n) + " " + pageEntityNoun() + " انتخاب شده";
    }

    function pageEntityNoun() {
        var segment = (location.pathname.split("/").filter(Boolean)[0] || "").toLowerCase();
        var map = {
            customers: "مشتری", users: "کاربر", suppliers: "تأمین‌کننده",
            employees: "کارمند", drivers: "راننده", sarrafs: "صراف",
            partners: "شریک", serviceproviders: "ارائه‌دهنده", companies: "شرکت",
            units: "واحد", currencies: "ارز", products: "جنس",
            trucks: "موتر", wagons: "واگن", vessels: "کشتی",
            storagetanks: "مخزن", terminals: "ترمینال", locations: "بندر",
            contracts: "قرارداد", payments: "پرداخت", expenses: "مصرف",
            roles: "نقش"
        };
        return map[segment] || "مورد";
    }

    function toPersianDigits(value) {
        return String(value).replace(/\d/g, function (d) { return "۰۱۲۳۴۵۶۷۸۹"[Number(d)]; });
    }

    // Shared Akaunting flat-list row navigation: any `.ak-table` row carrying
    // data-href becomes clickable. Replaces the per-page inline scripts that
    // used to live in every list view.
    function initializeAkRowNavigation() {
        document.querySelectorAll(".ak-table tbody tr[data-href]").forEach(function (row) {
            if (row.dataset.akRowReady === "true") return;
            row.dataset.akRowReady = "true";
            row.dataset.rowClickable = "true";

            row.addEventListener("click", function (event) {
                var href = row.getAttribute("data-href");
                if (href && !shouldIgnoreRowNavigation(event)) window.location.href = href;
            });
        });
    }

    function initializeResponsiveTables() {
        document.querySelectorAll(".table-responsive > .ak-table").forEach(function (table) {
            if (table.dataset.responsiveReady === "true") return;

            var headerCells = Array.from(table.querySelectorAll("thead th"));
            var headers = headerCells.map(function (header) {
                return normalizeText(header.textContent);
            });

            if (!headers.length) return;

            headerCells.forEach(function (header, index) {
                classifyCompactTableHeader(header, headers[index] || "");
            });

            Array.from(table.querySelectorAll("tbody tr")).forEach(function (row) {
                var cells = Array.from(row.children).filter(function (cell) {
                    return cell.matches("td, th");
                });

                if (cells.length === 1 && Number(cells[0].getAttribute("colspan") || "0") > 1) return;

                cells.forEach(function (cell, index) {
                    var label = headers[index] || "";
                    if (!label) return;

                    cell.setAttribute("data-column-label", label);
                    classifyCompactTableCell(cell, label);
                });
            });

            table.dataset.responsiveTable = "true";
            table.dataset.responsiveReady = "true";
        });
    }

    function classifyCompactTableHeader(header, label) {
        var type = resolveCompactTableColumnType(label);
        if (type) header.dataset.columnType = type;
    }

    function classifyCompactTableCell(cell, label) {
        var text;
        var type;

        if (!cell || cell.matches(".ak-col-actions") || cell.querySelector(".btn, .dropdown, form")) return;

        text = normalizeText(cell.textContent);
        if (!text || text === "-" || text === "—") return;

        type = resolveCompactTableColumnType(label);
        if (!type && isCompactDateValue(text)) type = "date";
        if (!type && (cell.classList.contains("col-text") || cell.classList.contains("col-desc") || text.length >= 34)) type = "long";
        if (!type && cell.classList.contains("text-end") && isCompactNumberValue(text)) type = "number";
        if (!type) return;

        cell.dataset.cellType = type;
        if (type === "number") cell.classList.add("ak-num");
        if (!cell.getAttribute("title") && (type === "long" || type === "ref")) cell.setAttribute("title", text);
    }

    function resolveCompactTableColumnType(label) {
        var normalized = normalizeText(label).toLowerCase();
        if (!normalized) return "";
        if (/date|تاریخ|پایان|\bend\b/.test(normalized)) return "date";
        if (/reference|\bref\b|مرجع|invoice|فاکتور|source|شماره|number|\bno\.?\b|شناسه|\bid\b/.test(normalized)) return "ref";
        if (/description|notes?|route|شرح|توضیح|یادداشت|مسیر|destination|مقصد|consignee|logistics|طرف حساب|customer|supplier|مشتری|تأمین|تامین/.test(normalized)) return "long";
        if (/amount|balance|qty|quantity|\bmt\b|\busd\b|\bfx\b|rate|price|total|premium|\bin\b|\bout\b|adjustment|transfer|مبلغ|مقدار|نرخ|قیمت|جمع|تراز|کرایه|هزینه|مصرف|مصارف|درآمد|ارزش|ورودی|خروجی/.test(normalized)) return "number";
        return "";
    }

    function isCompactDateValue(value) {
        return /^\d{4}[-/]\d{2}[-/]\d{2}$/.test(value) || /^\d{2}[-/]\d{2}[-/]\d{4}$/.test(value);
    }

    function isCompactNumberValue(value) {
        return /^[-+$€£]?\s*\d[\d,]*(?:\.\d+)?(?:\s*(?:mt|usd))?$/i.test(value);
    }

    function initializeClickableTableRows() {
        document.querySelectorAll(".ak-table tbody tr").forEach(function (row) {
            var detailsLink;

            if (row.dataset.rowNavigationReady === "true" || isEmptyStateTableRow(row)) return;

            detailsLink = resolveRowDetailsLink(row);
            if (!detailsLink) return;

            row.dataset.rowNavigationReady = "true";
            row.dataset.rowHref = detailsLink.href;
            row.dataset.rowClickable = "true";
            row.setAttribute("tabindex", "0");
            row.setAttribute("role", "link");

            row.addEventListener("click", function (event) {
                if (!shouldIgnoreRowNavigation(event)) navigateToRowDetails(detailsLink, event);
            });

            row.addEventListener("keydown", function (event) {
                if (event.key !== "Enter" && event.key !== " ") return;
                if (shouldIgnoreRowNavigation(event)) return;
                event.preventDefault();
                navigateToRowDetails(detailsLink, event);
            });
        });
    }

    function isEmptyStateTableRow(row) {
        var cells = Array.from(row.children).filter(function (cell) {
            return cell.matches("td, th");
        });
        return cells.length === 1 && Number(cells[0].getAttribute("colspan") || "0") > 1;
    }

    function resolveRowDetailsLink(row) {
        var actionLinks = Array.from(row.querySelectorAll(".ak-col-actions a[href]"));
        var otherLinks = Array.from(row.querySelectorAll("a[href]")).filter(function (link) {
            return actionLinks.indexOf(link) < 0;
        });
        return actionLinks.concat(otherLinks).find(isDetailsNavigationLink) || null;
    }

    function isDetailsNavigationLink(link) {
        var href = link.getAttribute("href") || "";
        var text = normalizeText(link.textContent).toLowerCase();
        var url;
        var path;

        if (!href || href === "#" || /^javascript:/i.test(href)) return false;
        if (link.getAttribute("data-bs-toggle") || link.getAttribute("data-bs-target")) return false;

        try { url = new URL(link.href, window.location.origin); } catch (error) { url = null; }
        path = url ? url.pathname.toLowerCase() : href.toLowerCase();

        if (/\/details(?:\/|$)/i.test(path)) return true;
        if (url && /(?:^|[?&])action=details(?:&|$)/i.test(url.search)) return true;

        return text.indexOf("details") >= 0 || text.indexOf("detail") >= 0 || text.indexOf("view") >= 0 || text.indexOf("مشاهده") >= 0 || text.indexOf("جزئیات") >= 0 || text.indexOf("جزییات") >= 0;
    }

    function shouldIgnoreRowNavigation(event) {
        var target = event.target;
        var selection = window.getSelection ? String(window.getSelection()).trim() : "";

        if (event.defaultPrevented || selection) return true;
        if (event.type === "click" && typeof event.button === "number" && event.button !== 0) return true;
        if (!target || !target.closest) return false;

        return !!target.closest("a, button, input, select, textarea, label, form, .dropdown, [data-bs-toggle], [data-bs-target], [role='button'], [contenteditable='true']");
    }

    function navigateToRowDetails(link, event) {
        var target = link.getAttribute("target");
        if ((event.ctrlKey || event.metaKey) || (target && target !== "_self")) {
            window.open(link.href, target || "_blank", "noopener");
            return;
        }
        window.location.href = link.href;
    }

    function initializeTableActionMenus() {
        document.querySelectorAll(".ak-table .ak-col-actions").forEach(function (cell) {
            cell.dataset.actionMenuReady = "true";
        });

        // `.ak-table-wrap` scrolls horizontally, which makes it a clipping
        // container on both axes. Instantiate Bootstrap with Popper's fixed
        // strategy so the real three-dot menu stays outside that clip.
        document.querySelectorAll(".ak-row-menu [data-bs-toggle='dropdown']").forEach(function (toggle) {
            if (toggle.dataset.akFixedDropdownReady === "true") return;
            if (!toggle.closest(".ak-table-wrap, .table-responsive")) return;
            if (!window.bootstrap || !window.bootstrap.Dropdown) return;

            toggle.dataset.akFixedDropdownReady = "true";
            window.bootstrap.Dropdown.getOrCreateInstance(toggle, {
                popperConfig: function (defaultConfig) {
                    return Object.assign({}, defaultConfig, { strategy: "fixed" });
                }
            });
        });
    }

    function initializeClientTablePagination() {
        document.querySelectorAll("table.ak-table").forEach(function (table) {
            setupClientTablePagination(table);
        });
    }

    function setupClientTablePagination(table) {
        var rows;
        var pageSize = 10;
        var currentPage = 1;
        var pager;
        var pagerInfo;
        var pagerPages;
        var previousButton;
        var nextButton;

        if (!table || table.dataset.clientPagerReady === "true") return;
        if (table.closest("[data-disable-client-pagination='true']")) return;
        if (hasServerPagination(table)) return;
        if (table.closest("[data-storage-contract-modal], [data-st-modal-pager]")) return;
        if (!isListTable(table)) return;

        rows = getPageableRows(table);
        if (rows.length <= pageSize) return;

        pager = document.createElement("nav");
        pager.className = "ptg-client-pager";
        pager.setAttribute("aria-label", "Table pagination");

        pagerInfo = document.createElement("span");
        pagerInfo.className = "ptg-client-pager-info";

        pagerPages = document.createElement("span");
        pagerPages.className = "ptg-client-pager-pages";

        previousButton = buildPagerButton("قبلی", "previous");
        nextButton = buildPagerButton("بعدی", "next");

        previousButton.addEventListener("click", function () {
            if (currentPage <= 1) return;
            currentPage -= 1;
            renderClientPager();
        });

        nextButton.addEventListener("click", function () {
            var pageCount = Math.ceil(rows.length / pageSize);
            if (currentPage >= pageCount) return;
            currentPage += 1;
            renderClientPager();
        });

        pager.appendChild(pagerInfo);
        pager.appendChild(previousButton);
        pager.appendChild(pagerPages);
        pager.appendChild(nextButton);

        insertPagerAfterTable(table, pager);
        table.dataset.clientPagerReady = "true";

        function renderClientPager() {
            var pageCount = Math.ceil(rows.length / pageSize);
            var start = (currentPage - 1) * pageSize;
            var end = Math.min(start + pageSize, rows.length);

            rows.forEach(function (row, index) {
                row.hidden = index < start || index >= end;
                if (row.hidden) collapseBreakdownRowOf(table, row);
            });

            pagerInfo.textContent = "نمایش " + String(start + 1) + "-" + String(end) + " از " + String(rows.length);
            previousButton.disabled = currentPage <= 1;
            nextButton.disabled = currentPage >= pageCount;
            renderPageButtons(pageCount);
        }

        function renderPageButtons(pageCount) {
            var windowSize = 5;
            var half = Math.floor(windowSize / 2);
            var first = Math.max(1, currentPage - half);
            var last = Math.min(pageCount, first + windowSize - 1);

            if (last - first + 1 < windowSize) {
                first = Math.max(1, last - windowSize + 1);
            }

            pagerPages.replaceChildren();

            for (var page = first; page <= last; page += 1) {
                pagerPages.appendChild(buildPageNumberButton(page));
            }
        }

        function buildPageNumberButton(page) {
            var button = buildPagerButton(String(page), "page");
            button.classList.toggle("is-active", page === currentPage);
            button.setAttribute("aria-current", page === currentPage ? "page" : "false");
            button.addEventListener("click", function () {
                currentPage = page;
                renderClientPager();
            });
            return button;
        }

        renderClientPager();
    }

    function hasServerPagination(table) {
        var list = table.closest(".ak-list, [data-operations-list], .ak-list-page, .ak-form-page");
        return !!(list && list.querySelector("[data-server-pagination='true']"));
    }

    function isListTable(table) {
        return !!table.closest(".ak-table-wrap, .ptg-modal-body-shell, .boltz-content");
    }

    // A row that opens an inline breakdown panel must not leave that panel
    // visible after its parent row moves to another page.
    function collapseBreakdownRowOf(table, row) {
        var key = row.dataset ? row.dataset.breakdownParent : null;
        if (!key) return;

        var detailRow = table.querySelector("[data-contract-breakdown-row='" + key + "']");
        if (detailRow) detailRow.hidden = true;

        var toggle = row.querySelector("[data-contract-breakdown-toggle]");
        if (toggle) toggle.setAttribute("aria-expanded", "false");
    }

    function getPageableRows(table) {
        var body = table.tBodies && table.tBodies.length ? table.tBodies[0] : null;
        if (!body) return [];

        return Array.from(body.rows).filter(function (row) {
            return !isEmptyStateTableRow(row);
        });
    }

    function buildPagerButton(label, type) {
        var button = document.createElement("button");
        button.type = "button";
        button.className = "ptg-client-pager-button ptg-client-pager-button-" + type;
        button.textContent = label;
        return button;
    }

    function insertPagerAfterTable(table, pager) {
        var shell = table.closest(".ak-table-wrap") || table;
        if (shell.parentElement) {
            shell.insertAdjacentElement("afterend", pager);
            return;
        }

        table.insertAdjacentElement("afterend", pager);
    }

    function normalizeText(value) {
        return String(value || "").replace(/\s+/g, " ").trim();
    }

    window.PTG = window.PTG || {};
    window.PTG.initializeResponsiveTables = initializeResponsiveTables;
    window.PTG.initializeClickableTableRows = initializeClickableTableRows;
    window.PTG.initializeTableActionMenus = initializeTableActionMenus;
    window.PTG.initializeClientTablePagination = initializeClientTablePagination;
})();
