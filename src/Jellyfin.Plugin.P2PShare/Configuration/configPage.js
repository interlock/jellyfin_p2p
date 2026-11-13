/* P2P Share admin page. Loaded by Jellyfin as the controller of configPage.html. */
const api = (method, path, body) => {
    const options = { type: method, url: ApiClient.getUrl('P2PShare/Admin/' + path) };
    if (body !== undefined) {
        options.data = JSON.stringify(body);
        options.contentType = 'application/json';
    }
    return ApiClient.ajax(options).then(async (response) => {
        // ApiClient.ajax resolves with a fetch Response when no dataType is given.
        if (!response || response.status === 204 || response.status === 202) return null;
        const text = await response.text();
        return text ? JSON.parse(text) : null;
    }, async (response) => {
        let message = 'Request failed';
        try {
            const err = await response.json();
            message = err.error || message;
        } catch (e) {
            if (response && response.status) message += ' (' + response.status + ')';
        }
        throw new Error(message);
    });
};

const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const $ = (view, sel) => view.querySelector(sel);

function formatBytes(bytes) {
    if (!bytes) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    let v = bytes;
    while (v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
    return v.toFixed(v < 10 && i > 0 ? 1 : 0) + ' ' + units[i];
}

function formatDate(value) {
    if (!value) return '';
    const d = new Date(value);
    return isNaN(d) ? '' : d.toLocaleString();
}

function usageBar(used, quota) {
    if (!quota) return `<div class="p2p-muted">${formatBytes(used)} used (no quota)</div>`;
    const pct = Math.min(100, (used / quota) * 100);
    return `<div class="p2p-bar"><div style="width:${pct.toFixed(1)}%"></div></div>
            <div class="p2p-muted">${formatBytes(used)} of ${formatBytes(quota)} (${pct.toFixed(0)}%)</div>`;
}

function limitText(value, unit) {
    return value > 0 ? `${value} ${unit}` : 'unlimited';
}

export default function (view) {
    const state = { libraries: [], shares: [], connections: [], settings: null, meta: null };

    // ---- Tabs ------------------------------------------------------------------------
    view.querySelectorAll('.p2p-tab').forEach((btn) => btn.addEventListener('click', () => {
        view.querySelectorAll('.p2p-tab').forEach((b) => b.classList.toggle('active', b === btn));
        view.querySelectorAll('.p2p-panel').forEach((p) => p.classList.toggle('active', p.dataset.panel === btn.dataset.tab));
        if (btn.dataset.tab === 'activity') loadActivity();
    }));

    const run = async (fn) => {
        Dashboard.showLoadingMsg();
        try {
            return await fn();
        } catch (e) {
            Dashboard.alert({ message: e.message });
            return undefined;
        } finally {
            Dashboard.hideLoadingMsg();
        }
    };

    // ---- Settings --------------------------------------------------------------------
    function fillSettings() {
        const s = state.settings;
        $(view, '#p2pSharingEnabled').checked = s.sharingEnabled;
        $(view, '#p2pPublicUrl').value = s.publicServerUrl;
        $(view, '#p2pDisplayName').value = s.serverDisplayName;
        $(view, '#p2pRequireHttps').checked = s.requireHttps;
        $(view, '#p2pGlobalBandwidth').value = s.globalMaxBandwidthMbps;
        $(view, '#p2pGlobalQuota').value = s.globalQuotaGB;
        $(view, '#p2pGlobalQuotaInterval').value = s.globalQuotaInterval;
        $(view, '#p2pGlobalStreams').value = s.globalMaxConcurrentStreams;
        $(view, '#p2pAllowTranscoding').checked = s.allowTranscoding;
        $(view, '#p2pMaxTranscodes').value = s.maxConcurrentTranscodes;
        $(view, '#p2pDefaultLifetime').value = s.defaultTokenLifetimeDays;
        $(view, '#p2pLocalProxyUrl').value = s.localProxyBaseUrl;
        $(view, '#p2pSyncHours').value = s.syncIntervalHours;
        $(view, '#p2pDownloadArtwork').checked = s.downloadArtwork;
        $(view, '#p2pNameTemplate').value = s.libraryNameTemplate;
        $(view, '#p2pMirrorRoot').value = s.mirrorRootPath;
        $(view, '#p2pMirrorRootHint').textContent = 'Currently: ' + state.meta.mirrorRoot;
        $(view, '#p2pHttpsWarning').classList.toggle('hide', s.requireHttps);
        $(view, '#p2pPublicUrlWarning').classList.toggle('hide', !!s.publicServerUrl);
    }

    function readSettings() {
        const num = (id) => Number($(view, id).value || 0);
        return {
            ...state.settings,
            sharingEnabled: $(view, '#p2pSharingEnabled').checked,
            publicServerUrl: $(view, '#p2pPublicUrl').value.trim(),
            serverDisplayName: $(view, '#p2pDisplayName').value.trim(),
            requireHttps: $(view, '#p2pRequireHttps').checked,
            globalMaxBandwidthMbps: num('#p2pGlobalBandwidth'),
            globalQuotaGB: num('#p2pGlobalQuota'),
            globalQuotaInterval: $(view, '#p2pGlobalQuotaInterval').value,
            globalMaxConcurrentStreams: num('#p2pGlobalStreams'),
            allowTranscoding: $(view, '#p2pAllowTranscoding').checked,
            maxConcurrentTranscodes: num('#p2pMaxTranscodes'),
            defaultTokenLifetimeDays: num('#p2pDefaultLifetime'),
            localProxyBaseUrl: $(view, '#p2pLocalProxyUrl').value.trim(),
            syncIntervalHours: num('#p2pSyncHours'),
            downloadArtwork: $(view, '#p2pDownloadArtwork').checked,
            libraryNameTemplate: $(view, '#p2pNameTemplate').value.trim(),
            mirrorRootPath: $(view, '#p2pMirrorRoot').value.trim(),
        };
    }

    $(view, '#p2pUseSuggestedUrl').addEventListener('click', (e) => {
        e.preventDefault();
        $(view, '#p2pPublicUrl').value = state.meta.suggestedPublicUrl;
    });

    $(view, '#p2pRequireHttps').addEventListener('change', (e) => {
        $(view, '#p2pHttpsWarning').classList.toggle('hide', e.target.checked);
    });

    $(view, '#p2pSettingsForm').addEventListener('submit', (e) => {
        e.preventDefault();
        $(view, '#p2pSettingsErrors').textContent = '';
        run(async () => {
            try {
                state.settings = await api('PUT', 'Settings', readSettings());
                fillSettings();
                Dashboard.processPluginConfigurationUpdateResult();
            } catch (err) {
                $(view, '#p2pSettingsErrors').textContent = err.message;
            }
        });
    });

    // ---- Outgoing shares -------------------------------------------------------------
    function libraryName(id) {
        const lib = state.libraries.find((l) => l.id === id);
        return lib ? lib.name : '(removed library)';
    }

    function renderShares() {
        const list = $(view, '#p2pShareList');
        if (!state.shares.length) {
            list.innerHTML = '<p class="p2p-muted">No shares yet.</p>';
            return;
        }

        list.innerHTML = state.shares.map((s) => {
            const status = !s.enabled ? '<span class="p2p-warn">disabled</span>'
                : s.expired ? '<span class="p2p-error">expired</span>'
                    : '<span class="p2p-ok">active</span>';
            return `<div class="p2p-card" data-id="${s.id}">
                <h3><span>${esc(s.name)} ${status}</span><span class="p2p-muted">${s.activeStreams} streaming</span></h3>
                <div class="p2p-muted">Libraries: ${s.libraryIds.map((id) => esc(libraryName(id))).join(', ')}</div>
                <div class="p2p-muted">Transcoding ${s.allowTranscoding ? 'allowed' : 'not allowed'} ·
                    bitrate cap ${limitText(s.maxStreamingBitrateMbps, 'Mbps')} ·
                    bandwidth ${limitText(s.maxBandwidthMbps, 'Mbps')} ·
                    streams ${limitText(s.maxConcurrentStreams, '')}
                    ${s.expiresAt ? ' · expires ' + esc(formatDate(s.expiresAt)) : ''}</div>
                <div>${s.quotaInterval} quota: ${usageBar(s.usage.usedBytes, s.usage.quotaBytes)}</div>
                <div class="p2p-muted">Lifetime ${formatBytes(s.usage.lifetimeBytes)}${s.usage.lastActivity ? ' · last used ' + esc(formatDate(s.usage.lastActivity)) : ''}</div>
                <div class="p2p-actions">
                    <button is="emby-button" type="button" class="raised" data-action="edit">Edit</button>
                    <button is="emby-button" type="button" class="raised" data-action="toggle">${s.enabled ? 'Disable' : 'Enable'}</button>
                    <button is="emby-button" type="button" class="raised" data-action="regenerate">New token</button>
                    <button is="emby-button" type="button" class="raised" data-action="delete">Revoke</button>
                </div>
            </div>`;
        }).join('');
    }

    function renderLibraryChecks(selected) {
        const shareable = state.libraries.filter((l) => !l.isMirror);
        $(view, '#p2pShareLibraries').innerHTML = shareable.length
            ? shareable.map((l) => `<div class="checkboxContainer"><label>
                <input is="emby-checkbox" type="checkbox" class="p2pLibCheck" value="${l.id}" ${selected.includes(l.id) ? 'checked' : ''}>
                <span>${esc(l.name)} <span class="p2p-muted">(${esc(l.collectionType)})</span></span></label></div>`).join('')
            : '<p class="p2p-muted">No local libraries.</p>';
    }

    function openShareForm(share) {
        const s = share || {
            name: '', libraryIds: [], enabled: true, allowTranscoding: false, maxStreamingBitrateMbps: 0,
            maxBandwidthMbps: 0, quotaGB: 0, quotaInterval: 'Monthly', maxConcurrentStreams: 2,
            expiresAt: null, allowedNetworks: [], notes: '',
        };
        $(view, '#p2pShareFormTitle').textContent = share ? 'Edit share' : 'New share';
        $(view, '#p2pShareId').value = share ? share.id : '';
        $(view, '#p2pShareName').value = s.name;
        $(view, '#p2pShareEnabled').checked = s.enabled;
        $(view, '#p2pShareTranscode').checked = s.allowTranscoding;
        $(view, '#p2pShareMaxBitrate').value = s.maxStreamingBitrateMbps;
        $(view, '#p2pShareBandwidth').value = s.maxBandwidthMbps;
        $(view, '#p2pShareQuota').value = s.quotaGB;
        $(view, '#p2pShareQuotaInterval').value = s.quotaInterval;
        $(view, '#p2pShareStreams').value = s.maxConcurrentStreams;
        $(view, '#p2pShareExpires').value = s.expiresAt ? s.expiresAt.substring(0, 10) : '';
        $(view, '#p2pShareNetworks').value = (s.allowedNetworks || []).join(', ');
        $(view, '#p2pShareNotes').value = s.notes || '';
        $(view, '#p2pShareErrors').textContent = '';
        renderLibraryChecks(s.libraryIds);
        $(view, '#p2pShareForm').classList.remove('hide');
        $(view, '#p2pShareForm').scrollIntoView({ behavior: 'smooth' });
    }

    function readShareForm() {
        const expires = $(view, '#p2pShareExpires').value;
        return {
            name: $(view, '#p2pShareName').value,
            libraryIds: Array.from(view.querySelectorAll('.p2pLibCheck:checked')).map((c) => c.value),
            enabled: $(view, '#p2pShareEnabled').checked,
            allowTranscoding: $(view, '#p2pShareTranscode').checked,
            maxStreamingBitrateMbps: Number($(view, '#p2pShareMaxBitrate').value || 0),
            maxBandwidthMbps: Number($(view, '#p2pShareBandwidth').value || 0),
            quotaGB: Number($(view, '#p2pShareQuota').value || 0),
            quotaInterval: $(view, '#p2pShareQuotaInterval').value,
            maxConcurrentStreams: Number($(view, '#p2pShareStreams').value || 0),
            expiresAt: expires ? new Date(expires + 'T23:59:59').toISOString() : null,
            allowedNetworks: $(view, '#p2pShareNetworks').value.split(',').map((x) => x.trim()).filter(Boolean),
            notes: $(view, '#p2pShareNotes').value,
        };
    }

    function showToken(token) {
        $(view, '#p2pNewTokenValue').textContent = token;
        $(view, '#p2pNewToken').classList.remove('hide');
        $(view, '#p2pNewToken').scrollIntoView({ behavior: 'smooth' });
    }

    $(view, '#p2pAddShare').addEventListener('click', () => openShareForm(null));
    $(view, '#p2pShareCancel').addEventListener('click', () => $(view, '#p2pShareForm').classList.add('hide'));
    $(view, '#p2pDismissToken').addEventListener('click', () => $(view, '#p2pNewToken').classList.add('hide'));
    $(view, '#p2pCopyToken').addEventListener('click', () => {
        navigator.clipboard?.writeText($(view, '#p2pNewTokenValue').textContent)
            .then(() => Dashboard.alert({ message: 'Token copied.' }))
            .catch(() => Dashboard.alert({ message: 'Select the token and copy it manually.' }));
    });

    $(view, '#p2pShareForm').addEventListener('submit', (e) => {
        e.preventDefault();
        const id = $(view, '#p2pShareId').value;
        const body = readShareForm();
        $(view, '#p2pShareErrors').textContent = '';
        run(async () => {
            try {
                if (id) {
                    await api('PUT', 'Shares/' + id, body);
                } else {
                    const created = await api('POST', 'Shares', body);
                    showToken(created.token);
                }
                $(view, '#p2pShareForm').classList.add('hide');
                await loadShares();
            } catch (err) {
                $(view, '#p2pShareErrors').textContent = err.message;
            }
        });
    });

    $(view, '#p2pShareList').addEventListener('click', (e) => {
        const button = e.target.closest('button[data-action]');
        if (!button) return;
        const id = button.closest('[data-id]').dataset.id;
        const share = state.shares.find((s) => s.id === id);
        switch (button.dataset.action) {
            case 'edit':
                openShareForm(share);
                break;
            case 'toggle':
                run(async () => {
                    await api('PUT', 'Shares/' + id, { ...share, enabled: !share.enabled });
                    await loadShares();
                });
                break;
            case 'regenerate':
                if (!confirm('Create a new token for "' + share.name + '"? The old token stops working immediately.')) return;
                run(async () => {
                    const result = await api('POST', 'Shares/' + id + '/Regenerate');
                    showToken(result.token);
                    await loadShares();
                });
                break;
            case 'delete':
                if (!confirm('Revoke "' + share.name + '"? The other server loses access immediately.')) return;
                run(async () => {
                    await api('DELETE', 'Shares/' + id);
                    await loadShares();
                });
                break;
        }
    });

    async function loadShares() {
        state.shares = await api('GET', 'Shares');
        renderShares();
    }

    // ---- Incoming connections --------------------------------------------------------
    function renderConnections() {
        const list = $(view, '#p2pConnectionList');
        if (!state.connections.length) {
            list.innerHTML = '<p class="p2p-muted">No shares received yet.</p>';
            return;
        }

        list.innerHTML = state.connections.map((c) => `<div class="p2p-card" data-id="${c.id}">
            <h3><span>${esc(c.remoteServerName)} <span class="p2p-muted">${esc(c.remoteServerUrl)}</span></span>
                <span>${c.enabled ? '' : '<span class="p2p-warn">disabled</span>'}</span></h3>
            <div class="${c.lastSyncFailed ? 'p2p-error' : 'p2p-muted'}">
                ${c.lastSyncAt ? 'Last sync ' + esc(formatDate(c.lastSyncAt)) + ': ' : 'Not synced yet. '}${esc(c.lastSyncStatus)}</div>
            <table><thead><tr><th>Mirror</th><th>Remote library</th><th>Local name</th><th>Items</th></tr></thead><tbody>
            ${c.libraries.map((l) => `<tr data-lib="${l.remoteLibraryId}">
                <td><input type="checkbox" class="p2pConnLib" ${l.enabled ? 'checked' : ''} ${l.availableRemotely ? '' : 'disabled'}></td>
                <td>${esc(l.remoteName)} <span class="p2p-muted">${esc(l.collectionType)}</span>${l.availableRemotely ? '' : ' <span class="p2p-warn">(no longer shared)</span>'}</td>
                <td><input is="emby-input" type="text" class="p2pConnLibName" value="${esc(l.localName)}" placeholder="(from template)"></td>
                <td>${l.itemCount}</td></tr>`).join('')}
            </tbody></table>
            <div class="p2p-grid">
                <div class="checkboxContainer"><label><input is="emby-checkbox" type="checkbox" class="p2pConnEnabled" ${c.enabled ? 'checked' : ''}><span>Enabled</span></label></div>
                <div class="checkboxContainer"><label><input is="emby-checkbox" type="checkbox" class="p2pConnTranscode" ${c.preferTranscode ? 'checked' : ''}><span>Ask peer to transcode</span></label></div>
                <div class="inputContainer"><input is="emby-input" type="number" min="0" step="0.1" class="p2pConnBitrate" value="${c.preferredMaxBitrateMbps}" label="Transcode bitrate (Mbps, 0 = peer decides)"></div>
            </div>
            <div class="p2p-actions">
                <button is="emby-button" type="button" class="raised button-submit" data-action="save">Save</button>
                <button is="emby-button" type="button" class="raised" data-action="sync">Sync now</button>
                <button is="emby-button" type="button" class="raised" data-action="remove">Remove</button>
            </div>
        </div>`).join('');
    }

    async function loadConnections() {
        state.connections = await api('GET', 'Connections');
        renderConnections();
    }

    $(view, '#p2pConnectPreviewBtn').addEventListener('click', () => {
        const token = $(view, '#p2pConnectToken').value.trim();
        $(view, '#p2pConnectErrors').textContent = '';
        run(async () => {
            try {
                const p = await api('POST', 'Connections/Preview', { token });
                const s = p.info.share;
                const box = $(view, '#p2pConnectPreview');
                box.innerHTML = `<p><b>${esc(p.info.serverName)}</b> (${esc(p.serverUrl)}) shares
                    ${p.libraries.map((l) => esc(l.name) + ' (' + l.itemCount + ')').join(', ') || 'no libraries'}.</p>
                    <p class="p2p-muted">Transcoding ${s.allowTranscoding ? 'allowed' : 'not allowed'} ·
                    quota ${s.quotaGB > 0 ? s.quotaGB + ' GB ' + s.quotaInterval.toLowerCase() : 'unlimited'} ·
                    ${s.expiresAt ? 'expires ' + esc(formatDate(s.expiresAt)) : 'no expiry'}</p>`;
                box.classList.remove('hide');
            } catch (err) {
                $(view, '#p2pConnectErrors').textContent = err.message;
            }
        });
    });

    $(view, '#p2pConnectForm').addEventListener('submit', (e) => {
        e.preventDefault();
        const token = $(view, '#p2pConnectToken').value.trim();
        $(view, '#p2pConnectErrors').textContent = '';
        run(async () => {
            try {
                await api('POST', 'Connections', { token });
                $(view, '#p2pConnectToken').value = '';
                $(view, '#p2pConnectPreview').classList.add('hide');
                Dashboard.alert({ message: 'Added. The libraries will appear after the first sync completes.' });
                await loadConnections();
            } catch (err) {
                $(view, '#p2pConnectErrors').textContent = err.message;
            }
        });
    });

    $(view, '#p2pConnectionList').addEventListener('click', (e) => {
        const button = e.target.closest('button[data-action]');
        if (!button) return;
        const card = button.closest('[data-id]');
        const id = card.dataset.id;
        const connection = state.connections.find((c) => c.id === id);
        switch (button.dataset.action) {
            case 'save':
                run(async () => {
                    await api('PUT', 'Connections/' + id, {
                        enabled: card.querySelector('.p2pConnEnabled').checked,
                        preferTranscode: card.querySelector('.p2pConnTranscode').checked,
                        preferredMaxBitrateMbps: Number(card.querySelector('.p2pConnBitrate').value || 0),
                        libraries: Array.from(card.querySelectorAll('tr[data-lib]')).map((row) => ({
                            remoteLibraryId: row.dataset.lib,
                            enabled: row.querySelector('.p2pConnLib').checked,
                            localName: row.querySelector('.p2pConnLibName').value.trim(),
                        })),
                    });
                    Dashboard.alert({ message: 'Saved. A sync has been started.' });
                    await loadConnections();
                });
                break;
            case 'sync':
                run(async () => {
                    await api('POST', 'Connections/' + id + '/Sync');
                    Dashboard.alert({ message: 'Sync started. Refresh this page in a moment to see the result.' });
                });
                break;
            case 'remove':
                if (!confirm('Remove "' + connection.remoteServerName + '"? Its libraries will be removed from this server.')) return;
                run(async () => {
                    await api('DELETE', 'Connections/' + id);
                    await loadConnections();
                });
                break;
        }
    });

    // ---- Activity --------------------------------------------------------------------
    async function loadActivity() {
        const status = await api('GET', 'Status');
        const g = status.global;
        $(view, '#p2pGlobalUsage').innerHTML = `<div>${esc(g.interval)} total: ${usageBar(g.usedBytes, g.quotaBytes)}</div>
            <div class="p2p-muted">Previous period ${formatBytes(g.previousBytes)} · lifetime ${formatBytes(g.lifetimeBytes)} · resets ${esc(formatDate(g.windowEnd))}</div>
            ${status.syncRunning ? '<div class="p2p-muted">A sync of incoming shares is running.</div>' : ''}`;
        $(view, '#p2pActiveStreams').innerHTML = status.activeStreams.length
            ? `<table><thead><tr><th>Share</th><th>Item</th><th>Mode</th><th>From</th><th>Started</th><th>Sent</th></tr></thead><tbody>
                ${status.activeStreams.map((s) => `<tr><td>${esc(s.shareName)}</td><td>${esc(s.itemName)}</td>
                    <td>${s.isTranscode ? 'transcode' : 'direct'}</td><td>${esc(s.remoteAddress)}</td>
                    <td>${esc(formatDate(s.startedAt))}</td><td>${formatBytes(s.bytesSent)}</td></tr>`).join('')}
               </tbody></table>`
            : '<p class="p2p-muted">No peer is streaming right now.</p>';
    }

    $(view, '#p2pRefreshActivity').addEventListener('click', () => run(loadActivity));

    // ---- Load ------------------------------------------------------------------------
    view.addEventListener('viewshow', () => run(async () => {
        const meta = await api('GET', 'Settings');
        state.meta = meta;
        state.settings = meta.settings;
        state.libraries = await api('GET', 'Libraries');
        fillSettings();
        await Promise.all([loadShares(), loadConnections()]);
    }));
}
