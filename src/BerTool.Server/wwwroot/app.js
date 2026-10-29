'use strict';

const state = {
    session: null,
    currentView: 'dashboard',
    dashboard: null,
    products: [],
    settings: null,
    filters: {
        status: 'all',
        search: '',
        product: '',
        productState: 'all',
        page: 1,
        pageSize: 50
    },
    autoRefresh: localStorage.getItem('bertool.autoRefresh') !== 'false',
    refreshTimer: null,
    searchTimer: null,
    loadingDashboard: false,
    dashboardReloadPending: false,
    productFilterSignature: '',
    authOptions: {
        adEnabled: false,
        adConfigured: false,
        localLoginEnabled: true
    }
};

const dateTimeFormatter = new Intl.DateTimeFormat('tr-TR', {
    dateStyle: 'medium',
    timeStyle: 'short'
});

const relativeTimeFormatter = new Intl.RelativeTimeFormat('tr', { numeric: 'auto' });

const statusLabels = {
    all: 'Tümü',
    healthy: 'Sağlıklı',
    risky: 'Riskli',
    offline: 'Çevrimdışı'
};

function byId(id) {
    return document.getElementById(id);
}

function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, character => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#39;'
    })[character]);
}

function textOr(value, fallback = 'Bilinmiyor') {
    const normalized = String(value ?? '').trim();
    return normalized.length > 0 ? normalized : fallback;
}

function formatDate(value) {
    if (!value) {
        return '-';
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime()) || date.getFullYear() <= 1970) {
        return '-';
    }

    return dateTimeFormatter.format(date);
}

function timeAgo(value) {
    if (!value) {
        return '-';
    }

    const target = new Date(value).getTime();
    if (!Number.isFinite(target)) {
        return '-';
    }

    const seconds = Math.round((target - Date.now()) / 1000);
    const absolute = Math.abs(seconds);

    if (absolute < 15) {
        return 'az önce';
    }

    if (absolute < 60) {
        return relativeTimeFormatter.format(seconds, 'second');
    }

    if (absolute < 3600) {
        return relativeTimeFormatter.format(Math.round(seconds / 60), 'minute');
    }

    if (absolute < 86400) {
        return relativeTimeFormatter.format(Math.round(seconds / 3600), 'hour');
    }

    if (absolute < 2592000) {
        return relativeTimeFormatter.format(Math.round(seconds / 86400), 'day');
    }

    return relativeTimeFormatter.format(Math.round(seconds / 2592000), 'month');
}

function formatSpeed(value) {
    if (value === null || value === undefined || Number.isNaN(Number(value))) {
        return '-';
    }

    const numeric = Number(value);
    if (numeric >= 1000) {
        return `${(numeric / 1000).toLocaleString('tr-TR', { maximumFractionDigits: 2 })} Gbps`;
    }

    return `${numeric.toLocaleString('tr-TR', { maximumFractionDigits: 1 })} Mbps`;
}

function formatNumber(value, suffix = '') {
    if (value === null || value === undefined || value === '') {
        return '-';
    }

    const numeric = Number(value);
    if (Number.isNaN(numeric)) {
        return '-';
    }

    return `${numeric.toLocaleString('tr-TR', { maximumFractionDigits: 2 })}${suffix}`;
}

async function api(path, options = {}) {
    const requestOptions = {
        method: options.method || 'GET',
        credentials: 'same-origin',
        headers: {
            Accept: 'application/json',
            ...(options.headers || {})
        }
    };

    if (options.body !== undefined) {
        requestOptions.headers['Content-Type'] = 'application/json';
        requestOptions.body = JSON.stringify(options.body);
    }

    const response = await fetch(path, requestOptions);
    const contentType = response.headers.get('content-type') || '';
    let payload = null;

    if (contentType.includes('application/json')) {
        try {
            payload = await response.json();
        } catch {
            payload = null;
        }
    }

    if (!response.ok) {
        if (response.status === 401 && state.session && state.session.authenticated) {
            toast('Yönetici oturumunuz sona erdi. Lütfen tekrar giriş yapın.', 'error');
            showLogin();
        }

        const error = new Error(payload?.message || payload?.title || `İstek başarısız oldu (${response.status}).`);
        error.status = response.status;
        error.payload = payload;
        throw error;
    }

    return payload;
}

function toast(message, type = 'info') {
    const root = byId('toast-root');
    const element = document.createElement('div');
    element.className = `toast toast-${type}`;
    element.setAttribute('role', 'status');
    element.textContent = message;
    root.appendChild(element);

    window.setTimeout(() => {
        element.classList.add('toast-hiding');
        window.setTimeout(() => element.remove(), 220);
    }, type === 'error' ? 6500 : 4000);
}

function describeError(error) {
    return error?.message || 'Beklenmeyen bir hata oluştu.';
}

function setButtonBusy(button, busy, busyText) {
    if (!button) {
        return;
    }

    if (busy) {
        button.dataset.originalText = button.textContent;
        button.textContent = busyText || 'İşleniyor...';
        button.disabled = true;
        button.classList.add('loading');
    } else {
        button.textContent = button.dataset.originalText || button.textContent;
        button.disabled = false;
        button.classList.remove('loading');
    }
}

function showLogin() {
    state.session = null;
    closeModal();
    stopAutoRefresh();
    byId('app-view').classList.add('hidden');
    byId('login-view').classList.remove('hidden');
    byId('login-error').classList.add('hidden');
    byId('login-password').value = '';
    updateLoginHint();

    window.setTimeout(() => byId('login-username').focus(), 30);
}

function applyAuthOptions(session) {
    state.authOptions = {
        adEnabled: Boolean(session?.adEnabled),
        adConfigured: session?.adConfigured !== false,
        localLoginEnabled: session?.localLoginEnabled !== false
    };
    updateLoginHint();
}

function updateLoginHint() {
    const hint = byId('login-hint');
    if (!hint) {
        return;
    }

    const { adEnabled, adConfigured, localLoginEnabled } = state.authOptions;

    if (adEnabled && !adConfigured && localLoginEnabled) {
        hint.textContent = 'Active Directory yapılandırması geçersiz. Yerel yönetici hesabıyla giriş yapabilirsiniz.';
    } else if (adEnabled && adConfigured && localLoginEnabled) {
        hint.textContent = 'Active Directory hesabınızla veya yerel yönetici hesabıyla giriş yapabilirsiniz.';
    } else if (adEnabled && adConfigured) {
        hint.textContent = 'Active Directory yönetici hesabınızla giriş yapın.';
    } else if (adEnabled) {
        hint.textContent = 'Active Directory yapılandırması geçersiz ve yerel yedek giriş kapalı. Sunucu yöneticisine başvurun.';
    } else {
        hint.textContent = 'Yerel yönetici hesabınızla giriş yapın.';
    }
}

async function handleLogin(event) {
    event.preventDefault();

    const submitButton = byId('login-submit');
    const errorElement = byId('login-error');
    errorElement.classList.add('hidden');
    setButtonBusy(submitButton, true, 'Giriş yapılıyor...');

    try {
        const session = await api('/api/admin/login', {
            method: 'POST',
            body: {
                username: byId('login-username').value.trim(),
                password: byId('login-password').value
            }
        });

        state.session = session;
        applyAuthOptions(session);
        byId('login-view').classList.add('hidden');
        byId('app-view').classList.remove('hidden');
        updateUserUi();
        configureAutoRefresh();
        await switchView('dashboard');
    } catch (error) {
        errorElement.textContent = describeError(error);
        errorElement.classList.remove('hidden');
    } finally {
        setButtonBusy(submitButton, false);
    }
}

async function handleLogout() {
    try {
        await api('/api/admin/logout', { method: 'POST' });
    } catch (error) {
        if (error.status !== 401) {
            toast(describeError(error), 'error');
        }
    }

    showLogin();
}

function updateUserUi() {
    const username = state.session?.username || '';
    const displayName = state.session?.displayName || username;
    const isAd = state.session?.authenticationMethod === 'ad';
    const userElement = byId('current-user');
    userElement.textContent = displayName;
    userElement.title = isAd ? `${username} (Active Directory)` : username;

    const methodBadge = byId('current-user-method');
    methodBadge.textContent = isAd ? 'AD' : 'Yerel';
    methodBadge.className = `badge ${isAd ? 'badge-success-soft' : 'badge-neutral'}`;
    methodBadge.title = isAd ? 'Active Directory kimlik doğrulaması' : 'Yerel yönetici hesabı';
    methodBadge.classList.remove('hidden');

    // AD kullanıcısının parolası dizin tarafından yönetilir; yerel yönetici
    // şifresi formu bu oturumda gösterilmez.
    byId('password-panel').classList.toggle('hidden', isAd);

    updateConfigVersionUi(state.session?.configVersion);
}

function updateConfigVersionUi(version) {
    if (version === null || version === undefined) {
        return;
    }

    byId('sidebar-config-version').textContent = `Yapılandırma sürümü: ${version}`;
    byId('settings-config-version').textContent = `Sürüm ${version}`;
}

async function refreshSession() {
    try {
        const session = await api('/api/admin/session');
        if (!session.authenticated) {
            applyAuthOptions(session);
            showLogin();
            return;
        }

        state.session = session;
        applyAuthOptions(session);
        updateUserUi();
    } catch (error) {
        if (error.status !== 401) {
            toast(describeError(error), 'error');
        }
    }
}

async function switchView(viewName) {
    state.currentView = viewName;

    document.querySelectorAll('.view').forEach(view => {
        view.classList.toggle('hidden', view.id !== `view-${viewName}`);
    });

    document.querySelectorAll('.nav-item').forEach(item => {
        item.classList.toggle('active', item.dataset.viewLink === viewName);
    });

    if (viewName === 'dashboard') {
        await loadDashboard();
    } else if (viewName === 'products') {
        await loadProducts();
    } else if (viewName === 'settings') {
        await loadSettings();
    }
}

function dashboardQuery(includePaging = true) {
    const params = new URLSearchParams();

    if (state.filters.status !== 'all') {
        params.set('status', state.filters.status);
    }

    if (state.filters.search.trim()) {
        params.set('search', state.filters.search.trim());
    }

    if (state.filters.product) {
        params.set('product', state.filters.product);

        if (state.filters.productState !== 'all') {
            params.set('productState', state.filters.productState);
        }
    }

    if (includePaging) {
        params.set('page', String(state.filters.page));
        params.set('pageSize', String(state.filters.pageSize));
    }

    return params;
}

async function loadDashboard(options = {}) {
    if (state.loadingDashboard) {
        // Filtreler hızlı değiştirildiğinde istek hâlâ sürüyor olabilir.
        // Bu durumda yeni isteği sessizce düşürmek yerine, mevcut istek
        // tamamlandıktan sonra bir kez daha yükleme yapılmasını işaretle.
        state.dashboardReloadPending = true;
        return;
    }

    const silent = options.silent === true;
    const refreshButton = byId('refresh-button');
    let reloadForPage = false;

    state.loadingDashboard = true;
    refreshButton.disabled = true;
    refreshButton.classList.add('loading');

    try {
        const params = dashboardQuery();
        const dashboard = await api(`/api/admin/dashboard?${params.toString()}`);
        state.dashboard = dashboard;

        const totalPages = Math.max(1, Math.ceil(dashboard.filteredCount / state.filters.pageSize));
        if (state.filters.page > totalPages) {
            state.filters.page = totalPages;
            reloadForPage = true;
        }

        renderSummary();
        renderProductFilter();
        renderMachineTable();
        renderPagination();
        byId('last-updated').textContent = `Son yenileme: ${new Date().toLocaleTimeString('tr-TR')}`;
    } catch (error) {
        if (error.status !== 401) {
            toast(`Makine listesi alınamadı: ${describeError(error)}`, 'error');
            if (!silent && !state.dashboard) {
                byId('machine-table-body').innerHTML =
                    '<tr><td colspan="7" class="empty-state">Makine listesi yüklenemedi. Yenilemeyi deneyin.</td></tr>';
            }
        }
    } finally {
        state.loadingDashboard = false;
        refreshButton.disabled = false;
        refreshButton.classList.remove('loading');
    }

    if (reloadForPage) {
        await loadDashboard({ silent: true });
    } else if (state.dashboardReloadPending) {
        state.dashboardReloadPending = false;
        await loadDashboard({ silent: true });
    }
}

function renderSummary() {
    const summary = state.dashboard?.summary || { total: 0, healthy: 0, risky: 0, offline: 0 };
    byId('summary-total').textContent = summary.total;
    byId('summary-healthy').textContent = summary.healthy;
    byId('summary-risky').textContent = summary.risky;
    byId('summary-offline').textContent = summary.offline;

    document.querySelectorAll('[data-status-filter]').forEach(element => {
        element.classList.toggle('active', element.dataset.statusFilter === state.filters.status);
    });
}

function renderProductFilter() {
    const products = state.dashboard?.products || [];
    const signature = JSON.stringify(products.map(product => [
        product.id,
        product.displayName,
        product.enabled,
        product.unhealthyMachineCount
    ]));

    if (signature !== state.productFilterSignature) {
        const select = byId('product-filter');
        const currentValue = state.filters.product;
        select.innerHTML = '<option value="">Tüm ürünler</option>' +
            products.map(product => {
                const suffix = product.enabled ? '' : ' (pasif)';
                const unhealthy = product.unhealthyMachineCount > 0
                    ? ` · ${product.unhealthyMachineCount} sorunlu`
                    : '';
                return `<option value="${escapeHtml(product.id)}">${escapeHtml(product.displayName)}${escapeHtml(suffix + unhealthy)}</option>`;
            }).join('');

        state.productFilterSignature = signature;
        select.value = products.some(product => product.id === currentValue) ? currentValue : '';
        if (select.value === '') {
            state.filters.product = '';
        }
    }

    byId('product-filter').value = state.filters.product;
    byId('product-state-filter').value = state.filters.productState;
    byId('product-state-filter').disabled = !state.filters.product;
    byId('search-input').value = state.filters.search;

    document.querySelectorAll('[data-status-filter]').forEach(element => {
        element.classList.toggle('active', element.dataset.statusFilter === state.filters.status);
    });
}

function renderMachineTable() {
    const machines = state.dashboard?.machines || [];
    const body = byId('machine-table-body');

    if (machines.length === 0) {
        const hasFilters = state.filters.status !== 'all' ||
            state.filters.search.trim() ||
            state.filters.product ||
            state.filters.productState !== 'all';

        body.innerHTML = `<tr><td colspan="7" class="empty-state">${
            hasFilters
                ? 'Filtrelerle eşleşen makine bulunamadı.'
                : 'Henüz rapor gönderen makine yok. Ajan ayarlarını ve sunucu bağlantısını kontrol edin.'
        }</td></tr>`;
        return;
    }

    body.innerHTML = machines.map(machine => {
        const failingProducts = (machine.productStatuses || []).filter(product => !product.isHealthy);
        const issues = failingProducts.length > 0
            ? failingProducts.slice(0, 2).map(product =>
                `<span class="chip" title="${escapeHtml(product.details)}">${escapeHtml(product.displayName)}</span>`
            ).join('') + (failingProducts.length > 2
                ? `<span class="chip chip-neutral">+${failingProducts.length - 2}</span>`
                : '')
            : machine.isOffline
                ? '<span class="chip chip-neutral">Bekleniyor</span>'
                : '<span class="chip chip-ok">Sorun yok</span>';

        const configWarning = machine.isConfigCurrent
            ? ''
            : '<span class="text-warn"> · eski yapılandırma</span>';

        const firstIp = machine.ipAddresses?.[0] || '-';
        const extraIp = machine.ipAddresses?.length > 1
            ? `<div class="cell-sub">+${machine.ipAddresses.length - 1} adres</div>`
            : '';

        return `
            <tr>
                <td><span class="badge badge-${escapeHtml(machine.status)}">${escapeHtml(machine.statusText)}</span></td>
                <td>
                    <div class="machine-name">${escapeHtml(textOr(machine.machineName))}</div>
                    <div class="cell-sub">${escapeHtml(textOr(machine.domainOrWorkgroup, 'Workgroup'))} · ${escapeHtml(textOr(machine.operatingSystem))}${configWarning}</div>
                </td>
                <td>
                    <div>${escapeHtml(textOr(machine.loggedInUser))}</div>
                    <div class="cell-sub">${escapeHtml(textOr(machine.computerModel))}</div>
                </td>
                <td>
                    <span class="ip-text">${escapeHtml(firstIp)}</span>
                    ${extraIp}
                </td>
                <td><div class="chip-list">${issues}</div></td>
                <td>
                    <div title="${escapeHtml(formatDate(machine.lastSeenUtc))}">${escapeHtml(timeAgo(machine.lastSeenUtc))}</div>
                    <div class="cell-sub">${escapeHtml(formatDate(machine.lastSeenUtc))}</div>
                </td>
                <td class="align-right">
                    <button class="btn btn-secondary btn-sm" type="button" data-machine-id="${escapeHtml(machine.machineId)}">Detay</button>
                </td>
            </tr>`;
    }).join('');
}

function renderPagination() {
    const dashboard = state.dashboard;
    if (!dashboard) {
        byId('pagination-info').textContent = '';
        return;
    }

    const total = dashboard.filteredCount;
    const start = total === 0 ? 0 : (state.filters.page - 1) * state.filters.pageSize + 1;
    const end = Math.min(state.filters.page * state.filters.pageSize, total);
    byId('pagination-info').textContent = total === 0
        ? 'Gösterilecek makine yok'
        : `${start}-${end} / ${total} makine`;
    byId('filtered-count').textContent = `${total} makine`;

    const totalPages = Math.max(1, Math.ceil(total / state.filters.pageSize));
    byId('page-prev').disabled = state.filters.page <= 1;
    byId('page-next').disabled = state.filters.page >= totalPages;
}

function changeStatusFilter(status) {
    state.filters.status = status;
    state.filters.page = 1;
    loadDashboard();
}

function resetFilters() {
    state.filters.status = 'all';
    state.filters.search = '';
    state.filters.product = '';
    state.filters.productState = 'all';
    state.filters.page = 1;
    state.productFilterSignature = '';
    byId('search-input').value = '';
    byId('product-filter').value = '';
    byId('product-state-filter').value = 'all';
    loadDashboard();
}

function exportCsv() {
    const params = dashboardQuery(false);
    const link = document.createElement('a');
    link.href = `/api/admin/export?${params.toString()}`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    toast('CSV indirme isteği başlatıldı.', 'success');
}

async function openMachineDetail(machineId) {
    try {
        const detail = await api(`/api/admin/machines/${encodeURIComponent(machineId)}`);
        openModal(renderMachineDetail(detail), true);
        bindServiceSearch();
    } catch (error) {
        toast(`Makine ayrıntısı alınamadı: ${describeError(error)}`, 'error');
    }
}

function renderMachineDetail(detail) {
    const summary = detail.summary;
    const report = detail.latestReport;
    const riskReasons = summary.riskReasons || [];
    const productStatuses = summary.productStatuses || [];
    const bitLockerVolumes = report.bitLockerVolumes || [];
    const ipAddresses = report.ipAddresses || [];
    const disks = report.physicalDiskDetails || [];
    const services = report.services || [];
    const history = detail.history || [];

    const productRows = productStatuses.length === 0
        ? '<tr><td colspan="3">Etkin ürün tanımı bulunmuyor.</td></tr>'
        : productStatuses.map(product => `
            <tr>
                <td>${escapeHtml(product.displayName)}</td>
                <td class="${product.isHealthy ? 'text-ok' : 'text-bad'}">${escapeHtml(product.statusText)}</td>
                <td>${escapeHtml(product.details || '-')}</td>
            </tr>`).join('');

    const reasonMarkup = riskReasons.length === 0
        ? '<ul class="reason-list ok"><li>Cihaz için risk nedeni bildirilmedi.</li></ul>'
        : `<ul class="reason-list">${riskReasons.map(reason => `<li>${escapeHtml(reason)}</li>`).join('')}</ul>`;

    const bitLockerMarkup = report.isBitLockerApplicable
        ? (bitLockerVolumes.length === 0
            ? '<p class="muted small">BitLocker bilgisi okunamadı.</p>'
            : `<table class="compact-table">
                <thead><tr><th>Sürücü</th><th>Yöntem</th><th>Durum</th></tr></thead>
                <tbody>${bitLockerVolumes.map(volume => `
                    <tr>
                        <td>${escapeHtml(volume.driveLetter)}</td>
                        <td>${escapeHtml(volume.encryptionMethod)}</td>
                        <td class="${volume.isHealthy ? 'text-ok' : 'text-bad'}">${volume.isHealthy ? 'Uygun' : 'Riskli'}</td>
                    </tr>`).join('')}</tbody>
            </table>`)
        : '<p class="muted small">Bu cihaz modelinde BitLocker kontrolü uygulanmıyor (VMware).</p>';

    const network = report.networkQuality || {};
    const ipMarkup = ipAddresses.length === 0
        ? '<p class="muted small">IP adresi bulunamadı.</p>'
        : `<ul class="kv-list">${ipAddresses.map(ip => `
            <li><span>${escapeHtml(ip.address)}</span><span class="muted">${escapeHtml(ip.adapterType)} · ${escapeHtml(ip.adapterName)}</span></li>`).join('')}</ul>`;

    const diskMarkup = disks.length === 0
        ? '<p class="muted small">Disk ayrıntısı yok.</p>'
        : `<ul class="kv-list">${disks.map(disk => `<li><span>${escapeHtml(disk)}</span></li>`).join('')}</ul>`;

    const serviceRows = services.map(service => {
        const search = `${service.name} ${service.displayName} ${service.status}`.toLocaleLowerCase('tr-TR');
        const running = String(service.status).toLowerCase() === 'running';
        return `<tr data-service-search="${escapeHtml(search)}">
            <td>${escapeHtml(service.name)}</td>
            <td>${escapeHtml(service.displayName)}</td>
            <td class="${running ? 'text-ok' : 'text-warn'}">${escapeHtml(service.status)}</td>
        </tr>`;
    }).join('');

    const historyMarkup = history.length === 0
        ? '<p class="muted small">Geçmiş kayıt yok.</p>'
        : `<ul class="history-list">${history.slice(0, 12).map(entry => `
            <li>
                <span>
                    <span class="${entry.isHealthy ? 'text-ok' : 'text-bad'}">${entry.isHealthy ? 'Sağlıklı' : 'Riskli'}</span>
                    · ${escapeHtml(formatDate(entry.collectedAtUtc))}
                </span>
                <span class="muted">${entry.riskReasons?.length ? `${entry.riskReasons.length} risk nedeni` : 'Risk nedeni yok'}</span>
            </li>`).join('')}</ul>`;

    return `
        <div class="modal-head">
            <div>
                <p class="eyebrow">Makine Ayrıntısı</p>
                <h2>${escapeHtml(textOr(summary.machineName))}</h2>
                <div class="detail-meta">
                    <span>${escapeHtml(textOr(summary.domainOrWorkgroup, 'Workgroup'))}</span>
                    <span>${escapeHtml(textOr(summary.operatingSystem))}</span>
                    <span>${escapeHtml(textOr(summary.loggedInUser))}</span>
                </div>
            </div>
            <div class="detail-hero-actions">
                <span class="badge badge-${escapeHtml(summary.status)}">${escapeHtml(summary.statusText)}</span>
                <button class="modal-close" type="button" data-close-modal aria-label="Kapat">×</button>
            </div>
        </div>
        <div class="modal-body">
            <div class="detail-hero">
                <div>
                    <h3>${escapeHtml(textOr(summary.computerModel))}</h3>
                    <div class="detail-meta">
                        <span>Son rapor: ${escapeHtml(formatDate(summary.collectedAtUtc))}</span>
                        <span>Son görülme: ${escapeHtml(timeAgo(summary.lastSeenUtc))}</span>
                        <span>Ajan: ${escapeHtml(textOr(summary.agentVersion))}</span>
                        <span class="${summary.isConfigCurrent ? 'text-ok' : 'text-warn'}">${summary.isConfigCurrent ? 'Yapılandırma güncel' : 'Eski yapılandırma'}</span>
                    </div>
                </div>
                <div class="detail-meta">
                    <span>İlk görülme</span>
                    <strong>${escapeHtml(formatDate(summary.firstSeenUtc))}</strong>
                </div>
            </div>

            <div class="detail-section">
                <h3>Risk Değerlendirmesi</h3>
                ${reasonMarkup}
            </div>

            <div class="detail-section">
                <h3>Ürün Durumları</h3>
                <table class="compact-table product-status-table">
                    <thead><tr><th>Ürün</th><th>Durum</th><th>Detay</th></tr></thead>
                    <tbody>${productRows}</tbody>
                </table>
            </div>

            <div class="detail-grid">
                <div class="detail-card">
                    <h4>Cihaz Bilgileri</h4>
                    <dl class="kv-list">
                        <div><dt>Makine adı</dt><dd>${escapeHtml(textOr(summary.machineName))}</dd></div>
                        <div><dt>Domain</dt><dd>${escapeHtml(textOr(summary.domainOrWorkgroup, 'Workgroup'))}</dd></div>
                        <div><dt>Kullanıcı</dt><dd>${escapeHtml(textOr(summary.loggedInUser))}</dd></div>
                        <div><dt>Model</dt><dd>${escapeHtml(textOr(summary.computerModel))}</dd></div>
                        <div><dt>CPU</dt><dd>${escapeHtml(textOr(report.cpuModel))}</dd></div>
                        <div><dt>Çekirdek / İşlemci</dt><dd>${formatNumber(report.physicalCoreCount)} / ${formatNumber(report.logicalProcessorCount)}</dd></div>
                        <div><dt>RAM</dt><dd>${formatNumber(report.totalRamGb, ' GB')}</dd></div>
                        <div><dt>Toplam disk</dt><dd>${formatNumber(report.totalDiskGb, ' GB')}</dd></div>
                    </dl>
                </div>
                <div class="detail-card">
                    <h4>Ağ Kalitesi</h4>
                    <dl class="kv-list">
                        <div><dt>Adaptör</dt><dd>${escapeHtml(textOr(network.adapterName))}</dd></div>
                        <div><dt>Bağlantı</dt><dd>${network.hasActiveAdapter ? (network.hasGateway ? 'Gateway var' : 'Gateway yok') : 'Offline'}</dd></div>
                        <div><dt>Adaptör hızı</dt><dd>${escapeHtml(formatSpeed(network.bandwidthMbps))}</dd></div>
                        <div><dt>İnternet hızı</dt><dd>${escapeHtml(formatSpeed(network.internetSpeedMbps))}</dd></div>
                        <div><dt>Gecikme</dt><dd>${network.latencyMilliseconds === null || network.latencyMilliseconds === undefined ? '-' : `${formatNumber(network.latencyMilliseconds, ' ms')}`}</dd></div>
                        <div><dt>Hedef</dt><dd>${escapeHtml(textOr(network.internetSpeedTarget))}</dd></div>
                    </dl>
                </div>
                <div class="detail-card">
                    <h4>BitLocker</h4>
                    ${bitLockerMarkup}
                </div>
                <div class="detail-card">
                    <h4>IP Adresleri</h4>
                    ${ipMarkup}
                </div>
            </div>

            <div class="detail-section">
                <h3>Fiziksel Diskler</h3>
                ${diskMarkup}
            </div>

            <div class="detail-section">
                <h3>Windows Servisleri (${services.length})</h3>
                <details class="service-details">
                    <summary>Tüm servis anlık görüntüsünü göster</summary>
                    <input id="detail-service-search" class="service-search" type="search" placeholder="Servis ara...">
                    <div class="table-wrap service-table-wrap">
                        <table class="compact-table">
                            <thead><tr><th>Servis</th><th>Görünen ad</th><th>Durum</th></tr></thead>
                            <tbody id="detail-service-rows">${serviceRows || '<tr><td colspan="3">Servis bilgisi yok.</td></tr>'}</tbody>
                        </table>
                    </div>
                </details>
            </div>

            <div class="detail-section">
                <h3>Rapor Geçmişi</h3>
                ${historyMarkup}
            </div>

            <div class="modal-footer">
                <button class="btn btn-secondary" type="button" data-close-modal>Kapat</button>
            </div>
        </div>`;
}

function bindServiceSearch() {
    const input = byId('detail-service-search');
    if (!input) {
        return;
    }

    input.addEventListener('input', () => {
        const term = input.value.toLocaleLowerCase('tr-TR').trim();
        document.querySelectorAll('#detail-service-rows tr').forEach(row => {
            const searchable = row.dataset.serviceSearch || '';
            row.hidden = term.length > 0 && !searchable.includes(term);
        });
    });
}

async function loadProducts() {
    try {
        state.products = await api('/api/admin/products');
        renderProductTable();
        updateConfigVersionUi(state.session?.configVersion);
    } catch (error) {
        if (error.status !== 401) {
            toast(`Ürün tanımları alınamadı: ${describeError(error)}`, 'error');
            byId('product-table-body').innerHTML =
                '<tr><td colspan="7" class="empty-state">Ürün tanımları yüklenemedi.</td></tr>';
        }
    }
}

function renderProductTable() {
    const body = byId('product-table-body');
    if (state.products.length === 0) {
        body.innerHTML = '<tr><td colspan="7" class="empty-state">Henüz ürün tanımı yok. “Yeni ürün” ile başlayın.</td></tr>';
        return;
    }

    body.innerHTML = state.products.map(product => `
        <tr>
            <td>
                <div class="machine-name">${escapeHtml(product.displayName)}</div>
                <div class="cell-sub">${escapeHtml(product.id)}</div>
            </td>
            <td><div class="chip-list">${product.serviceNames.map(name => `<span class="code-chip">${escapeHtml(name)}</span>`).join('')}</div></td>
            <td>${product.matchMode === 'any' ? 'En az biri çalışmalı' : 'Tümü çalışmalı'}</td>
            <td><span class="badge ${product.enabled ? 'badge-success-soft' : 'badge-neutral'}">${product.enabled ? 'Aktif' : 'Pasif'}</span></td>
            <td>${escapeHtml(product.sortOrder)}</td>
            <td>${escapeHtml(formatDate(product.updatedAtUtc))}</td>
            <td class="align-right">
                <button class="btn btn-ghost btn-sm" type="button" data-product-action="edit" data-product-id="${escapeHtml(product.id)}">Düzenle</button>
                <button class="btn btn-ghost btn-sm text-bad" type="button" data-product-action="delete" data-product-id="${escapeHtml(product.id)}">Sil</button>
            </td>
        </tr>`).join('');
}

function openProductForm(product) {
    const editing = Boolean(product);
    const nextSort = state.products.length === 0
        ? 10
        : Math.max(...state.products.map(item => item.sortOrder || 0)) + 10;

    const markup = `
        <div class="modal-head">
            <div>
                <p class="eyebrow">${editing ? 'Ürünü Düzenle' : 'Yeni Ürün'}</p>
                <h2>${editing ? escapeHtml(product.displayName) : 'Ürün tanımı ekle'}</h2>
            </div>
            <button class="modal-close" type="button" data-close-modal aria-label="Kapat">×</button>
        </div>
        <div class="modal-body">
            <form id="product-form" class="stack" data-product-id="${editing ? escapeHtml(product.id) : ''}">
                <label class="field">
                    <span>Ürün adı</span>
                    <input id="product-name" value="${editing ? escapeHtml(product.displayName) : ''}" maxlength="100" required autofocus>
                </label>
                <label class="field">
                    <span>İzlenen servisler</span>
                    <textarea id="product-services" rows="5" required placeholder="BESClient&#10;IkinciServisAdi">${editing ? escapeHtml(product.serviceNames.join('\n')) : ''}</textarea>
                    <small>Her satıra bir servis adı yazın veya virgülle ayırın. Servis adı veya görünen ad eşleşir.</small>
                </label>
                <div class="field-row">
                    <label class="field">
                        <span>Eşleşme modu</span>
                        <select id="product-match">
                            <option value="all"${editing && product.matchMode === 'all' ? ' selected' : ''}>Tümü çalışmalı</option>
                            <option value="any"${editing && product.matchMode === 'any' ? ' selected' : ''}>En az biri çalışmalı</option>
                        </select>
                    </label>
                    <label class="field">
                        <span>Sıralama</span>
                        <input id="product-sort" type="number" min="0" max="100000" value="${editing ? escapeHtml(product.sortOrder) : nextSort}">
                    </label>
                </div>
                <label class="switch switch-standalone">
                    <input id="product-enabled" type="checkbox"${!editing || product.enabled ? ' checked' : ''}>
                    <span class="switch-track" aria-hidden="true"></span>
                    <span>Ürün aktif olarak değerlendirilsin</span>
                </label>
                <p id="product-form-error" class="form-error hidden" role="alert"></p>
                <div class="modal-footer">
                    <button class="btn btn-secondary" type="button" data-close-modal>Vazgeç</button>
                    <button id="product-save" class="btn btn-primary" type="submit">${editing ? 'Değişiklikleri kaydet' : 'Ürünü oluştur'}</button>
                </div>
            </form>
        </div>`;

    openModal(markup);
    byId('product-form').addEventListener('submit', event => saveProduct(event, product?.id));
    window.setTimeout(() => byId('product-name')?.focus(), 30);
}

async function saveProduct(event, productId) {
    event.preventDefault();

    const errorElement = byId('product-form-error');
    errorElement.classList.add('hidden');
    const saveButton = byId('product-save');

    const displayName = byId('product-name').value.trim();
    const serviceNames = byId('product-services').value
        .split(/[\n,;]+/)
        .map(name => name.trim())
        .filter(Boolean);

    if (!displayName) {
        showProductFormError('Ürün adı zorunludur.');
        return;
    }

    if (serviceNames.length === 0) {
        showProductFormError('En az bir servis adı girilmelidir.');
        return;
    }

    const payload = {
        displayName,
        serviceNames,
        matchMode: byId('product-match').value,
        enabled: byId('product-enabled').checked,
        sortOrder: Number(byId('product-sort').value || 0)
    };

    setButtonBusy(saveButton, true, 'Kaydediliyor...');

    try {
        if (productId) {
            await api(`/api/admin/products/${encodeURIComponent(productId)}`, {
                method: 'PUT',
                body: payload
            });
            toast('Ürün tanımı güncellendi. Yeni tanım anında yeniden değerlendirildi.', 'success');
        } else {
            await api('/api/admin/products', { method: 'POST', body: payload });
            toast('Ürün tanımı oluşturuldu.', 'success');
        }

        closeModal();
        await Promise.all([loadProducts(), loadDashboard({ silent: true }), refreshSession()]);
    } catch (error) {
        showProductFormError(describeError(error));
    } finally {
        setButtonBusy(saveButton, false);
    }
}

function showProductFormError(message) {
    const errorElement = byId('product-form-error');
    if (!errorElement) {
        toast(message, 'error');
        return;
    }

    errorElement.textContent = message;
    errorElement.classList.remove('hidden');
    errorElement.scrollIntoView({ block: 'nearest' });
}

async function deleteProduct(productId) {
    const product = state.products.find(item => item.id === productId);
    if (!product) {
        return;
    }

    const confirmed = window.confirm(
        `"${product.displayName}" ürün tanımı silinsin mi?\n\n` +
        'Bu ürünün servis kontrolü tüm makinelerde kaldırılacak. Geçmiş raporlar silinmez.');
    if (!confirmed) {
        return;
    }

    try {
        await api(`/api/admin/products/${encodeURIComponent(productId)}`, { method: 'DELETE' });
        toast('Ürün tanımı silindi.', 'success');
        await Promise.all([loadProducts(), loadDashboard({ silent: true }), refreshSession()]);
    } catch (error) {
        toast(`Ürün silinemedi: ${describeError(error)}`, 'error');
    }
}

async function loadSettings() {
    try {
        state.settings = await api('/api/admin/settings');
        byId('settings-report-interval').value = state.settings.reportIntervalSeconds;
        byId('settings-offline-threshold').value = state.settings.offlineThresholdMinutes;
        byId('settings-history-limit').value = state.settings.historyLimit;
        updateConfigVersionUi(state.settings.configVersion);
        updateAgentKeyState();
        renderAdSettings(state.settings.ad);
        renderAgentConfigExample();
    } catch (error) {
        if (error.status !== 401) {
            toast(`Sunucu ayarları alınamadı: ${describeError(error)}`, 'error');
        }
    }
}

function updateAgentKeyState() {
    const configured = state.settings?.agentApiKeyConfigured;
    const badge = byId('agent-key-state');
    badge.textContent = configured ? 'Tanımlı' : 'Tanımsız';
    badge.className = `badge ${configured ? 'badge-success-soft' : 'badge-risky'}`;
}

function renderAdSettings(ad) {
    const status = ad || {};
    const enabled = Boolean(status.enabled);
    const configured = status.configured !== false;
    const badge = byId('ad-state-badge');

    if (!enabled) {
        badge.textContent = 'Kapalı';
        badge.className = 'badge badge-neutral';
    } else if (!configured) {
        badge.textContent = 'Yapılandırma hatası';
        badge.className = 'badge badge-risky';
    } else {
        badge.textContent = 'Etkin';
        badge.className = 'badge badge-success-soft';
    }

    byId('ad-enabled').checked = enabled;
    byId('ad-host').value = status.host || '';
    byId('ad-port').value = status.configuredPort > 0 ? status.configuredPort : '';
    byId('ad-encryption').value = status.useSsl
        ? 'ldaps'
        : status.useStartTls
            ? 'starttls'
            : 'insecure';
    byId('ad-timeout').value = status.timeoutSeconds || 10;
    byId('ad-basedn').value = status.baseDn || '';
    byId('ad-binddn').value = status.bindDn || '';
    byId('ad-bindpassword').value = '';
    byId('ad-admingroup').value = status.adminGroup || '';
    byId('ad-fallback').checked = status.allowLocalFallback !== false;

    byId('ad-bind-state').textContent = status.bindAccountConfigured
        ? 'Kayıtlı parola tanımlı. Değiştirmek için yeni parola girin.'
        : 'Kayıtlı parola yok. Hizmet hesabı için parola girin.';

    const errorElement = byId('ad-config-error');
    if (enabled && status.configurationError) {
        errorElement.textContent = status.configurationError;
        errorElement.classList.remove('hidden');
    } else {
        errorElement.textContent = '';
        errorElement.classList.add('hidden');
    }

    renderAdWarnings(status);
    byId('ad-test-result').classList.add('hidden');
}

function renderAdWarnings(status) {
    const list = byId('ad-warning-list');
    list.innerHTML = '';

    const warnings = [];
    if (status.insecureTransport) {
        warnings.push(
            'AD bağlantısı şifrelenmemiş LDAP kullanıyor. Kullanıcı parolaları ağda açık taşınır; ' +
            'yalnızca test ortamında kullanın ve üretimde LDAPS veya StartTLS etkinleştirin.');
    }

    if (status.certificateValidationDisabled) {
        warnings.push(
            'AD sunucu sertifikası doğrulanmıyor. Bu ayar bağlantının dinlenmesine ve sahte sunucuya ' +
            'karşı korumayı kaldırır; yalnızca test ortamında kullanılmalıdır.');
    }

    if (warnings.length === 0) {
        list.classList.add('hidden');
        return;
    }

    warnings.forEach(message => {
        const paragraph = document.createElement('p');
        paragraph.textContent = message;
        list.appendChild(paragraph);
    });
    list.classList.remove('hidden');
}

function collectAdSettings() {
    const encryption = byId('ad-encryption').value;

    return {
        enabled: byId('ad-enabled').checked,
        host: byId('ad-host').value.trim(),
        port: Number(byId('ad-port').value || 0),
        useSsl: encryption === 'ldaps',
        useStartTls: encryption === 'starttls',
        baseDn: byId('ad-basedn').value.trim(),
        bindDn: byId('ad-binddn').value.trim(),
        bindPassword: byId('ad-bindpassword').value,
        adminGroup: byId('ad-admingroup').value.trim(),
        allowLocalFallback: byId('ad-fallback').checked,
        timeoutSeconds: Number(byId('ad-timeout').value || 10)
    };
}

async function saveAdSettings(event) {
    event.preventDefault();

    const button = byId('ad-save-button');
    const resultElement = byId('ad-test-result');
    resultElement.classList.add('hidden');
    setButtonBusy(button, true, 'Kaydediliyor...');

    try {
        const status = await api('/api/admin/ad', {
            method: 'PUT',
            body: collectAdSettings()
        });

        if (state.settings) {
            state.settings.ad = status;
        }

        renderAdSettings(status);
        byId('ad-bindpassword').value = '';
        await refreshSession();
        toast(
            status.enabled
                ? 'Active Directory ayarları kaydedildi. Yeni ayarlar bir sonraki girişten itibaren geçerli.'
                : 'Active Directory entegrasyonu kapatıldı; yerel yönetici girişi kullanılıyor.',
            'success');
    } catch (error) {
        toast(`AD ayarları kaydedilemedi: ${describeError(error)}`, 'error');
    } finally {
        setButtonBusy(button, false);
    }
}

async function testAdConnection() {
    const button = byId('ad-test-button');
    const resultElement = byId('ad-test-result');
    setButtonBusy(button, true, 'Test ediliyor...');
    resultElement.classList.add('hidden');
    resultElement.classList.remove('is-success', 'is-error');

    try {
        const result = await api('/api/admin/ad/test', {
            method: 'POST',
            body: collectAdSettings()
        });
        resultElement.textContent = result.message || (result.success ? 'Bağlantı başarılı.' : 'Bağlantı başarısız.');
        resultElement.classList.remove('hidden');
        resultElement.classList.add(result.success ? 'is-success' : 'is-error');
    } catch (error) {
        resultElement.textContent = `AD bağlantı testi yapılamadı: ${describeError(error)}`;
        resultElement.classList.remove('hidden');
        resultElement.classList.add('is-error');
    } finally {
        setButtonBusy(button, false);
    }
}

function renderAgentConfigExample() {
    const example = {
        serverUrl: window.location.origin,
        apiKey: '<AJAN_API_ANAHTARI>',
        enabled: true
    };

    byId('agent-config-example').textContent = JSON.stringify(example, null, 2);
}

async function saveSettings(event) {
    event.preventDefault();

    const button = event.target.querySelector('button[type="submit"]');
    const payload = {
        reportIntervalSeconds: Number(byId('settings-report-interval').value),
        offlineThresholdMinutes: Number(byId('settings-offline-threshold').value),
        historyLimit: Number(byId('settings-history-limit').value)
    };

    setButtonBusy(button, true, 'Kaydediliyor...');

    try {
        state.settings = await api('/api/admin/settings', { method: 'PUT', body: payload });
        updateConfigVersionUi(state.settings.configVersion);
        updateAgentKeyState();
        renderAdSettings(state.settings.ad);
        await refreshSession();
        toast('Sunucu ayarları kaydedildi. Ajanlar en geç 45 saniye içinde yeni değerleri alacak ve değişiklik algılandığında hemen raporlayacak.', 'success');
        await loadDashboard({ silent: true });
    } catch (error) {
        toast(`Ayarlar kaydedilemedi: ${describeError(error)}`, 'error');
    } finally {
        setButtonBusy(button, false);
    }
}

async function changePassword(event) {
    event.preventDefault();

    const current = byId('password-current').value;
    const next = byId('password-new').value;
    const repeat = byId('password-repeat').value;

    if (next !== repeat) {
        toast('Yeni şifreler birbiriyle eşleşmiyor.', 'error');
        return;
    }

    const button = event.target.querySelector('button[type="submit"]');
    setButtonBusy(button, true, 'Değiştiriliyor...');

    try {
        await api('/api/admin/password', {
            method: 'POST',
            body: { currentPassword: current, newPassword: next }
        });
        byId('password-form').reset();
        toast('Yönetici şifresi değiştirildi. Diğer oturumlar kapatıldı.', 'success');
        await refreshSession();
    } catch (error) {
        toast(`Şifre değiştirilemedi: ${describeError(error)}`, 'error');
    } finally {
        setButtonBusy(button, false);
    }
}

async function rotateAgentKey() {
    const confirmed = window.confirm(
        'Yeni bir ajan API anahtarı üretilsin mi?\n\n' +
        'Mevcut anahtar hemen geçersiz olur ve tüm ajanların ayarları güncellenene kadar raporlama durur.');
    if (!confirmed) {
        return;
    }

    const button = byId('rotate-key-button');
    setButtonBusy(button, true, 'Üretiliyor...');

    try {
        const result = await api('/api/admin/agent-key/rotate', { method: 'POST' });
        byId('rotated-key-panel').classList.remove('hidden');
        byId('rotated-key-value').value = result.apiKey;
        state.settings.agentApiKeyConfigured = true;
        updateAgentKeyState();
        toast('Yeni ajan API anahtarı üretildi. Anahtarı güvenli bir yere kaydedin.', 'success');
    } catch (error) {
        toast(`Anahtar üretilemedi: ${describeError(error)}`, 'error');
    } finally {
        setButtonBusy(button, false);
    }
}

async function copyText(value, successMessage) {
    try {
        await navigator.clipboard.writeText(value);
        toast(successMessage, 'success');
    } catch {
        const textarea = document.createElement('textarea');
        textarea.value = value;
        textarea.className = 'copy-fallback';
        document.body.appendChild(textarea);
        textarea.select();
        document.execCommand('copy');
        textarea.remove();
        toast(successMessage, 'success');
    }
}

function openModal(markup, wide = false) {
    const root = byId('modal-root');
    root.innerHTML = `
        <div class="modal-backdrop" data-modal-backdrop>
            <div class="modal${wide ? ' modal-lg' : ''}" role="dialog" aria-modal="true">
                ${markup}
            </div>
        </div>`;
    document.body.classList.add('modal-open');
}

function closeModal() {
    byId('modal-root').innerHTML = '';
    document.body.classList.remove('modal-open');
}

function configureAutoRefresh() {
    stopAutoRefresh();

    if (!state.autoRefresh || !state.session?.authenticated) {
        return;
    }

    state.refreshTimer = window.setInterval(() => {
        if (document.visibilityState === 'visible' &&
            state.currentView === 'dashboard' &&
            byId('modal-root').children.length === 0 &&
            !state.loadingDashboard) {
            loadDashboard({ silent: true });
        }
    }, 30000);
}

function stopAutoRefresh() {
    if (state.refreshTimer) {
        window.clearInterval(state.refreshTimer);
        state.refreshTimer = null;
    }
}

function bindEvents() {
    byId('login-form').addEventListener('submit', handleLogin);
    byId('logout-button').addEventListener('click', handleLogout);
    byId('refresh-button').addEventListener('click', () => loadDashboard());

    document.querySelectorAll('[data-view-link]').forEach(element => {
        element.addEventListener('click', event => {
            event.preventDefault();
            switchView(element.dataset.viewLink);
        });
    });

    document.querySelectorAll('[data-status-filter]').forEach(element => {
        element.addEventListener('click', () => changeStatusFilter(element.dataset.statusFilter));
    });

    byId('search-input').addEventListener('input', event => {
        window.clearTimeout(state.searchTimer);
        state.searchTimer = window.setTimeout(() => {
            state.filters.search = event.target.value.trim();
            state.filters.page = 1;
            loadDashboard();
        }, 320);
    });

    byId('product-filter').addEventListener('change', event => {
        state.filters.product = event.target.value;
        state.filters.page = 1;
        if (!state.filters.product) {
            state.filters.productState = 'all';
            byId('product-state-filter').value = 'all';
        }
        byId('product-state-filter').disabled = !state.filters.product;
        loadDashboard();
    });

    byId('product-state-filter').addEventListener('change', event => {
        state.filters.productState = event.target.value;
        state.filters.page = 1;
        loadDashboard();
    });

    byId('clear-filters').addEventListener('click', resetFilters);

    byId('export-button').addEventListener('click', exportCsv);

    byId('page-prev').addEventListener('click', () => {
        if (state.filters.page > 1) {
            state.filters.page--;
            loadDashboard();
        }
    });

    byId('page-next').addEventListener('click', () => {
        const totalPages = Math.max(1, Math.ceil((state.dashboard?.filteredCount || 0) / state.filters.pageSize));
        if (state.filters.page < totalPages) {
            state.filters.page++;
            loadDashboard();
        }
    });

    byId('machine-table-body').addEventListener('click', event => {
        const button = event.target.closest('button[data-machine-id]');
        if (button) {
            openMachineDetail(button.dataset.machineId);
        }
    });

    byId('add-product-button').addEventListener('click', () => openProductForm(null));

    byId('product-table-body').addEventListener('click', event => {
        const button = event.target.closest('button[data-product-action]');
        if (!button) {
            return;
        }

        if (button.dataset.productAction === 'edit') {
            const product = state.products.find(item => item.id === button.dataset.productId);
            if (product) {
                openProductForm(product);
            }
        } else if (button.dataset.productAction === 'delete') {
            deleteProduct(button.dataset.productId);
        }
    });

    byId('settings-form').addEventListener('submit', saveSettings);
    byId('ad-form').addEventListener('submit', saveAdSettings);
    byId('password-form').addEventListener('submit', changePassword);
    byId('rotate-key-button').addEventListener('click', rotateAgentKey);
    byId('ad-test-button').addEventListener('click', testAdConnection);

    byId('copy-key-button').addEventListener('click', () => {
        const value = byId('rotated-key-value').value;
        if (value) {
            copyText(value, 'Ajan API anahtarı kopyalandı.');
        }
    });

    byId('copy-config-button').addEventListener('click', () => {
        copyText(byId('agent-config-example').textContent, 'Örnek ajan yapılandırması kopyalandı.');
    });

    byId('auto-refresh').checked = state.autoRefresh;
    byId('auto-refresh').addEventListener('change', event => {
        state.autoRefresh = event.target.checked;
        localStorage.setItem('bertool.autoRefresh', String(state.autoRefresh));
        configureAutoRefresh();
    });

    byId('modal-root').addEventListener('click', event => {
        if (event.target.hasAttribute('data-modal-backdrop') || event.target.closest('[data-close-modal]')) {
            closeModal();
        }
    });

    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && byId('modal-root').children.length > 0) {
            closeModal();
        }
    });

    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible' &&
            state.currentView === 'dashboard' &&
            state.session?.authenticated &&
            !state.loadingDashboard &&
            byId('modal-root').children.length === 0) {
            loadDashboard({ silent: true });
        }
    });
}

async function init() {
    bindEvents();

    try {
        const session = await api('/api/admin/session');
        applyAuthOptions(session);
        if (session.authenticated) {
            state.session = session;
            byId('login-view').classList.add('hidden');
            byId('app-view').classList.remove('hidden');
            updateUserUi();
            configureAutoRefresh();
            await switchView('dashboard');
        } else {
            showLogin();
        }
    } catch (error) {
        showLogin();
        toast(`Sunucuya bağlanılamadı: ${describeError(error)}`, 'error');
    }
}

document.addEventListener('DOMContentLoaded', init);
