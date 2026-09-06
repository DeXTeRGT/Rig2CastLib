const $ = id => document.getElementById(id);
let models = [],
    snapshot = null,
    ws = null,
    busy = false,
    radioId = null,
    isOwner = false,
    clientRole = 'Observer',
    pttLeaseActive = false,
    pttRenewalTimer = null;
const frequencyWriteTimers = new Map();
const clientId = sessionStorage.getItem('rig2castClientId') || crypto.randomUUID();
sessionStorage.setItem('rig2castClientId', clientId);
const ui = {
    model: $('model'),
    transport: $('transport'),
    port: $('serial-port'),
    baud: $('baud'),
    settings: $('model-settings'),
    connect: $('connect'),
    disconnect: $('disconnect'),
    refresh: $('refresh-all')
};
const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;'
} [c]));
const access = (f, kind) => String(f?.access || '').toLowerCase().includes(kind);
const connected = () => !!snapshot;
async function api(path, options = {}) {
    const response = await fetch('/api/v1' + path, {
        ...options,
        headers: {
            'Content-Type': 'application/json',
            'X-Rig2Cast-Client': clientId,
            ...options.headers
        }
    });
    if (!response.ok) {
        let e;
        try {
            e = (await response.json()).error
        } catch {}
        throw new Error(e || `${response.status} ${response.statusText}`)
    }
    return response.status === 204 ? null : response.json()
}

function log(message, type = 'INFO') {
    const line = `${new Date().toLocaleTimeString()}  ${type.padEnd(5)}  ${message}\n`;
    $('log').textContent = line + $('log').textContent
}

function status(title, text, kind = '') {
    $('status-title').textContent = title;
    $('status-text').textContent = text;
    $('status-card').className = 'status-card ' + kind
}

function setBusy(value) {
    busy = value;
    document.body.classList.toggle('busy', value);
    ui.connect.disabled = value || connected();
    ui.disconnect.disabled = value || !connected();
    ui.refresh.disabled = value || !connected();
    if ($('ptt') && snapshot) $('ptt').disabled = value || !canControlPtt()
}
async function run(action, label) {
    if (busy) return;
    setBusy(true);
    try {
        await action();
        if (label) log(label)
    } catch (e) {
        status('Operation failed', e.message, 'error');
        log(e.message, 'ERROR')
    } finally {
        setBusy(false)
    }
}
async function bootstrap() {
    models = await api('/models');
    ui.model.innerHTML = models.map(m => `<option value="${esc(m.id)}">${esc(m.manufacturer)} ${esc(m.model)}</option>`).join('');
    await loadPorts();
    renderModel();
    const s = await api('/status');
    if (!s.serverAllowsWrites) {
        $('enable-writes').disabled = true;
        $('enable-writes').title = 'Start with Rig2Cast__AllowWrites=true to permit operator sessions.'
    }
    status('Offline', `${s.activeRadios} server-owned radio(s) active. ${s.serverAllowsWrites ? 'Writes may be requested.' : 'Server is read-only.'}`);
}
async function loadPorts() {
    const ports = await api('/serial-ports');
    ui.port.innerHTML = ports.length ? ports.map(p => `<option value="${esc(p.portName)}">${esc(p.displayName)}</option>`).join('') : '<option value="">No ports discovered</option>'
}

function selectedModel() {
    return models.find(m => m.id === ui.model.value)
}

function renderModel() {
    const m = selectedModel();
    if (!m) return;
    const transports = [...m.transports];
    if (m.simulatorAvailable) transports.push('Simulator');
    ui.transport.innerHTML = [...new Set(transports)].map(t => `<option>${esc(t)}</option>`).join('');
    ui.baud.innerHTML = m.baudRates.map(b => `<option ${b === m.defaultBaudRate ? 'selected' : ''}>${b}</option>`).join('');
    ui.settings.innerHTML = m.connectionSettings.map(s => {
        const value = s.defaultValue ?? '';
        if (s.valueType === 'Boolean') return `<label class="check setting"><input data-setting="${esc(s.id)}" type="checkbox" ${String(value).toLowerCase() === 'true' ? 'checked' : ''}>${esc(s.displayName)}</label><small title="${esc(s.description)}"></small>`;
        return `<label title="${esc(s.description)}">${esc(s.displayName)}<input data-setting="${esc(s.id)}" value="${esc(value)}" ${s.isRequired ? 'required' : ''}></label>`
    }).join('');
    renderTransport()
}

function renderTransport() {
    const t = ui.transport.value;
    $('serial-fields').hidden = t !== 'Serial';
    $('tcp-fields').hidden = t !== 'Tcp'
}

function connectBody() {
    const settingValues = {};
    document.querySelectorAll('[data-setting]').forEach(e => settingValues[e.dataset.setting] = e.type === 'checkbox' ? String(e.checked) : e.value);
    const override = $('port-override-enabled').checked;
    return {
        clientId,
        modelId: ui.model.value,
        transport: ui.transport.value,
        serialPort: override ? $('port-override').value : ui.port.value,
        baudRate: Number(ui.baud.value) || null,
        tcpHost: $('tcp-host').value,
        tcpPort: Number($('tcp-port').value),
        settings: settingValues,
        enableWrites: $('enable-writes').checked,
        modeRestrictions: $('mode-policy').value
    }
}
async function connect() {
    const result = await api('/radios/connect', {
        method: 'POST',
        body: JSON.stringify(connectBody())
    });
    radioId = result.radioId;
    snapshot = result.snapshot;
    isOwner = result.isOwner;
    clientRole = result.role;
    ui.disconnect.textContent = isOwner ? 'Close radio' : 'Detach';
    renderSnapshot();
    openSocket();
    await refreshAll();
    status(result.result === 'attached' ? 'Attached read-only' : 'Connected', result.message, 'online');
    log(`${result.message} Radio ID: ${radioId}.`)
}
async function disconnect() {
    stopPttRenewal();
    if (ws) {
        ws.onclose = null;
        ws.close();
        ws = null
    }
    if (isOwner) await api(`/radios/${radioId}`, {
        method: 'DELETE'
    });
    else await api(`/radios/${radioId}/detach`, {
        method: 'POST'
    });
    const action = isOwner ? 'Radio closed.' : 'Observer detached.';
    snapshot = null;
    radioId = null;
    isOwner = false;
    clientRole = 'Observer';
    pttLeaseActive = false;
    ui.disconnect.textContent = 'Disconnect';
    renderOffline();
    status('Offline', action);
    log(action)
}

function openSocket() {
    if (ws) ws.close();
    const protocol = location.protocol === 'https:' ? 'wss' : 'ws';
    ws = new WebSocket(`${protocol}://${location.host}/api/v1/radios/${radioId}/events?clientId=${encodeURIComponent(clientId)}`);
    ws.onmessage = e => {
        const message = JSON.parse(e.data);
        snapshot = message.snapshot;
        renderSnapshot(false)
    };
    ws.onerror = () => log('WebSocket event stream interrupted.', 'WARN');
    ws.onclose = () => {
        if (connected()) setTimeout(openSocket, 1500)
    }
}

function frequencyText(value) {
    return value ? `${(value / 1e6).toFixed(6)} MHz` : '—'
}

function renderSnapshot(rebuild = true) {
    if (!snapshot) return;
    const c = snapshot.capabilities,
        s = snapshot.state,
        writes = snapshot.authorization.canControl;
    $('radio-title').textContent = `${c.manufacturer} ${c.model}`;
    $('radio-summary').textContent = `VFO ${s.activeVfo} · ${s.mode} · Split ${s.isSplit ? 'ON' : 'OFF'} · ${s.isTransmitting ? 'TX' : 'RX'} · ${clientRole}`;
    $('connection-badge').textContent = `${s.connection} · ${clientRole.toUpperCase()}`;
    $('connection-badge').className = 'badge online';
    renderPtt(c, s);
    renderVfos(c, s, writes);
    renderCore(c, s, writes);
    if (rebuild) {
        renderControls(c, writes);
        renderMeters(c)
    }
    applyModeAvailability(c, s.mode, writes);
    setBusy(false)
}

function renderVfos(c, s, writes) {
    const deck = $('vfo-deck');
    deck.className = 'vfo-deck';
    deck.innerHTML = c.vfos.available.map(v => {
        const hz = s.frequenciesHz[v] ?? s.vfos?.[v]?.frequencyHz ?? 0;
        const writable = writes && access(c.frequency.feature, 'write') && c.frequency.targets.some(x => String(x).toLowerCase() === String(v).toLowerCase());
        const splitTx = s.isSplit && v === s.transmitVfo;
        const role = v === s.activeVfo ? `ACTIVE · RX${splitTx ? ' · TX' : ''}` : splitTx ? 'SPLIT · TX' : 'STANDBY';
        const hint = writable ? 'Mouse wheel: base step; Shift: 10×; Ctrl: 100×' : '';
        return `<article class="vfo-card ${v === s.activeVfo ? 'active' : ''} ${splitTx ? 'split-tx' : ''}"><span class="vfo-role">${role}</span><div class="vfo-name">VFO ${esc(v)}</div><div class="frequency ${writable ? 'tunable' : ''}" title="${hint}" onwheel="tuneFrequency(event, '${v}')">${frequencyText(hz)}</div><div class="frequency-editor"><input id="freq-${v}" type="number" value="${hz}"><button ${writable ? '' : 'disabled'} onclick="writeFrequency('${v}')">Apply</button></div></article>`
    }).join('')
}

function canControlPtt() {
    return !!snapshot && isOwner && clientRole.toLowerCase() === 'operator' &&
        snapshot.authorization.canControl && access(snapshot.capabilities.transmit, 'write')
}

function renderPtt(c, s) {
    const button = $('ptt');
    button.textContent = s.isTransmitting ? 'PTT ON' : 'PTT OFF';
    button.classList.toggle('transmitting', s.isTransmitting);
    button.disabled = busy || !canControlPtt();
    button.title = button.disabled
        ? 'PTT requires the owning Operator page and writable transmit capability.'
        : 'Toggle PTT. A 10-second safety lease is renewed every 5 seconds while active.';
    if (!s.isTransmitting) {
        pttLeaseActive = false;
        stopPttRenewal()
    }
}

function startPttRenewal() {
    stopPttRenewal();
    pttRenewalTimer = setInterval(renewPtt, 5000)
}

function stopPttRenewal() {
    if (pttRenewalTimer !== null) clearInterval(pttRenewalTimer);
    pttRenewalTimer = null
}

async function renewPtt() {
    if (!pttLeaseActive || !connected()) return stopPttRenewal();
    try {
        await api(`/radios/${radioId}/ptt/renew`, { method: 'POST' })
    } catch (e) {
        pttLeaseActive = false;
        stopPttRenewal();
        status('PTT renewal failed', `${e.message} The safety lease will force RX.`, 'error');
        log(`PTT renewal failed: ${e.message}`, 'ERROR');
        try {
            snapshot = await api(`/radios/${radioId}/ptt`, {
                method: 'PUT',
                body: JSON.stringify({ value: false })
            });
            renderSnapshot(false)
        } catch {}
    }
}

window.togglePtt = () => run(async () => {
    const enable = !snapshot.state.isTransmitting;
    snapshot = await api(`/radios/${radioId}/ptt`, {
        method: 'PUT',
        body: JSON.stringify({ value: enable })
    });
    pttLeaseActive = enable;
    if (enable) startPttRenewal(); else stopPttRenewal();
    renderSnapshot(false)
}, snapshot?.state.isTransmitting ? 'PTT released.' : 'PTT enabled with a 10-second safety lease.');

function nearestAllowedFrequency(value, ranges) {
    if (!ranges?.length) return value;
    for (const range of ranges)
        if (value >= range.minimumHz && value <= range.maximumHz) return value;
    const boundaries = ranges.flatMap(range => [range.minimumHz, range.maximumHz]);
    return boundaries.reduce((nearest, candidate) =>
        Math.abs(candidate - value) < Math.abs(nearest - value) ? candidate : nearest)
}

window.tuneFrequency = (event, vfo) => {
    const c = snapshot?.capabilities.frequency;
    if (!c || !snapshot.authorization.canControl || !access(c.feature, 'write') ||
        !c.targets.some(x => String(x).toLowerCase() === String(vfo).toLowerCase())) return;
    event.preventDefault();
    const step = Math.max(1, c.smallestStepHz || 1) * (event.ctrlKey ? 100 : event.shiftKey ? 10 : 1);
    const current = snapshot.state.frequenciesHz[vfo] ?? snapshot.state.vfos?.[vfo]?.frequencyHz;
    if (!Number.isFinite(current)) return;
    const next = nearestAllowedFrequency(current + (event.deltaY < 0 ? step : -step), c.ranges);
    snapshot.state.frequenciesHz[vfo] = next;
    if (snapshot.state.vfos?.[vfo]) snapshot.state.vfos[vfo].frequencyHz = next;
    renderVfos(snapshot.capabilities, snapshot.state, true);
    clearTimeout(frequencyWriteTimers.get(vfo));
    frequencyWriteTimers.set(vfo, setTimeout(() => commitWheelFrequency(vfo, next), 200))
};

async function commitWheelFrequency(vfo, value) {
    frequencyWriteTimers.delete(vfo);
    try {
        snapshot = await api(`/radios/${radioId}/frequency/${vfo}`, {
            method: 'PUT',
            body: JSON.stringify({ value })
        });
        renderSnapshot(false)
    } catch (e) {
        status('Frequency update failed', e.message, 'error');
        log(`VFO ${vfo}: ${e.message}`, 'ERROR');
        try { snapshot = await api(`/radios/${radioId}/snapshot`) } catch {}
        renderSnapshot(false)
    }
}

function renderCore(c, s, writes) {
    const passband = c.passband && String(c.passband.feature?.support).toLowerCase() !== 'unsupported';
    $('core-card').className = 'panel';
    $('core-card').innerHTML = `<h3>Radio state</h3><div class="core-grid"><div class="core-item"><small>Driver</small><strong>${esc(c.driverId)}</strong></div><div class="core-item"><small>Capability revision</small><strong>${c.revision}</strong></div><div class="core-item"><small>Connection</small><strong>${esc(s.connection)}</strong></div><div class="core-item"><small>Observed</small><strong>${new Date(s.observedAt).toLocaleTimeString()}</strong></div></div><div class="core-actions"><div class="field-action"><label>Active VFO<select id="active-vfo">${c.vfos.available.map(v => `<option ${v === s.activeVfo ? 'selected' : ''}>${v}</option>`).join('')}</select></label><button ${writes && access(c.vfos.selection, 'write') ? '' : 'disabled'} onclick="writeCore('active-vfo')">Apply</button></div><div class="field-action"><label>Mode<select id="mode">${c.modes.values.map(v => `<option ${v === s.mode ? 'selected' : ''}>${v}</option>`).join('')}</select></label><button ${writes && access(c.modes.feature, 'write') ? '' : 'disabled'} onclick="writeCore('mode')">Apply</button></div><label class="switch-tile">Split enabled<input id="split" class="toggle" type="checkbox" ${s.isSplit ? 'checked' : ''} ${writes && access(c.vfos.split, 'write') ? '' : 'disabled'} onchange="writeCore('split')"></label>${passband ? `<div class="field-action"><label>Passband (Hz)<input id="passband" type="number" min="${c.passband.minimumHz}" max="${c.passband.maximumHz}" step="${c.passband.stepHz}"></label><button onclick="writePassband()" ${writes && access(c.passband.feature, 'write') ? '' : 'disabled'}>Apply</button></div>` : ''}</div>`
}

function renderControls(c, writes) {
    const numeric = Object.values(c.controls || {}),
        switches = Object.values(c.switches || {}),
        choices = Object.values(c.choices || {});
    let html = '';
    if (numeric.length) html += `<div class="panel"><h3>Numeric controls <small>raw / driver values</small></h3><div class="control-table">${numeric.map(x => `<div class="name">${esc(x.displayName)}<small>${esc(x.unit)} · ${esc(x.feature.access)}</small></div><input data-control-id="${x.id}" id="control-${x.id}" type="number" min="${x.minimum}" max="${x.maximum}" step="${x.step}"><button data-control-write="${x.id}" onclick="writeControl('${x.id}')" ${writes && access(x.feature, 'write') ? '' : 'disabled'}>Apply</button>`).join('')}</div></div>`;
    if (choices.length) html += `<div class="panel"><h3>Choice controls</h3><div class="control-table">${choices.map(x => `<div class="name">${esc(x.displayName)}<small>${esc(x.feature.access)}</small></div><select data-choice-id="${x.id}" id="choice-${x.id}">${Object.values(x.options).map(o => `<option value="${esc(o.value)}">${esc(o.displayName)}</option>`).join('')}</select><button data-choice-write="${x.id}" onclick="writeChoice('${x.id}')" ${writes && access(x.feature, 'write') ? '' : 'disabled'}>Apply</button>`).join('')}</div></div>`;
    if (switches.length) html += `<div class="panel"><h3>Switches</h3><div class="switch-grid">${switches.map(x => `<label class="switch-tile">${esc(x.displayName)}<input data-switch-id="${x.id}" id="switch-${x.id}" class="toggle" type="checkbox" ${writes && access(x.feature, 'write') ? '' : 'disabled'} onchange="writeSwitch('${x.id}')"></label>`).join('')}</div></div>`;
    $('controls-grid').innerHTML = html || '<div class="panel empty-state">This driver advertises no extended controls.</div>'
}

function modeAllows(descriptor, kind, mode) {
    const modes = descriptor?.modeApplicability?.[kind + 'Modes'];
    return !modes || modes.some(x => x.toLowerCase() === String(mode).toLowerCase())
}

function applyModeAvailability(c, mode, writes) {
    for (const x of Object.values(c.controls || {})) {
        const input = $(`control-${x.id}`),
            button = document.querySelector(`[data-control-write="${x.id}"]`);
        if (input) input.disabled = !modeAllows(x, 'read', mode);
        if (button) button.disabled = !(writes && access(x.feature, 'write') && modeAllows(x, 'write', mode))
    }
    for (const x of Object.values(c.switches || {})) {
        const input = $(`switch-${x.id}`);
        if (input) input.disabled = !(writes && access(x.feature, 'write') && modeAllows(x, 'write', mode))
    }
    for (const x of Object.values(c.choices || {})) {
        const input = $(`choice-${x.id}`),
            button = document.querySelector(`[data-choice-write="${x.id}"]`);
        if (input) input.disabled = !modeAllows(x, 'read', mode);
        if (button) button.disabled = !(writes && access(x.feature, 'write') && modeAllows(x, 'write', mode))
    }
}

function renderMeters(c) {
    const meters = Object.values(c.meters || {});
    $('meters-grid').innerHTML = meters.length ? meters.map(m => `<article class="meter"><div class="meter-head"><strong>${esc(m.displayName)}</strong><small>${esc(m.rawUnit)}</small></div><div id="meter-value-${m.id}" class="meter-value">—</div><div class="meter-track"><div id="meter-fill-${m.id}" class="meter-fill"></div></div></article>`).join('') : '<div class="panel empty-state">This driver advertises no meters.</div>'
}
async function refreshAll() {
    const root = `/radios/${radioId}`;
    snapshot = await api(`${root}/refresh`, {
        method: 'POST'
    });
    renderSnapshot();
    const c = snapshot.capabilities,
        mode = snapshot.state.mode;
    for (const x of Object.values(c.controls || {}))
        if (access(x.feature, 'read') && modeAllows(x, 'read', mode)) try {
            const v = await api(`${root}/controls/${x.id}/read`, {
                method: 'POST'
            });
            const e = $(`control-${x.id}`);
            if (e) e.value = v.value
        } catch (e) {
            log(`${x.displayName}: ${e.message}`, 'WARN')
        }
    for (const x of Object.values(c.switches || {}))
        if (access(x.feature, 'read') && modeAllows(x, 'read', mode)) try {
            const v = await api(`${root}/switches/${x.id}/read`, {
                method: 'POST'
            });
            const e = $(`switch-${x.id}`);
            if (e) e.checked = v.enabled
        } catch (e) {
            log(`${x.displayName}: ${e.message}`, 'WARN')
        }
    for (const x of Object.values(c.choices || {}))
        if (access(x.feature, 'read') && modeAllows(x, 'read', mode)) try {
            const v = await api(`${root}/choices/${x.id}/read`, {
                method: 'POST'
            });
            const e = $(`choice-${x.id}`);
            if (e) e.value = v.value
        } catch (e) {
            log(`${x.displayName}: ${e.message}`, 'WARN')
        }
    if (c.passband && access(c.passband.feature, 'read')) try {
        const v = await api(`${root}/passband/read`, {
            method: 'POST'
        });
        if ($('passband')) $('passband').value = v.widthHz
    } catch (e) {
        log(`Passband: ${e.message}`, 'WARN')
    }
    for (const x of Object.values(c.meters || {}))
        if (modeAllows(x, 'read', mode) && (!x.requiresTransmit || snapshot.state.isTransmitting)) try {
            const v = await api(`${root}/meters/${x.id}/read`, {
                method: 'POST'
            });
            const e = $(`meter-value-${x.id}`),
                f = $(`meter-fill-${x.id}`);
            if (e) e.textContent = `${v.rawValue} ${x.rawUnit}`;
            if (f) f.style.width = `${Math.max(0, Math.min(100, v.normalizedValue * 100))}%`
        } catch (e) {
            log(`${x.displayName}: ${e.message}`, 'WARN')
        }
    renderSnapshot(false)
}
window.writeFrequency = v => run(async () => {
    snapshot = await api(`/radio/frequency/${v}`, {
        method: 'PUT',
        body: JSON.stringify({
            value: Number($(`freq-${v}`).value)
        })
    });
    renderSnapshot(false)
}, `VFO ${v} frequency updated.`);
window.writeCore = id => run(async () => {
    const endpoint = id === 'active-vfo' ? '/radio/active-vfo' : `/radio/${id}`;
    const value = id === 'split' ? $(id).checked : $(id).value;
    snapshot = await api(endpoint, {
        method: 'PUT',
        body: JSON.stringify({
            value
        })
    });
    renderSnapshot(false)
}, `${id} updated.`);
window.writeControl = id => run(async () => {
    await api(`/radio/controls/${id}`, {
        method: 'PUT',
        body: JSON.stringify({
            value: Number($(`control-${id}`).value)
        })
    })
}, `${id} updated.`);
window.writeSwitch = id => run(async () => {
    await api(`/radio/switches/${id}`, {
        method: 'PUT',
        body: JSON.stringify({
            value: $(`switch-${id}`).checked
        })
    })
}, `${id} updated.`);
window.writeChoice = id => run(async () => {
    await api(`/radio/choices/${id}`, {
        method: 'PUT',
        body: JSON.stringify({
            value: $(`choice-${id}`).value
        })
    })
}, `${id} updated.`);
window.writePassband = () => run(async () => {
    await api('/radio/passband', {
        method: 'PUT',
        body: JSON.stringify({
            value: Number($('passband').value)
        })
    })
}, 'Passband updated.');
// Multi-radio routes supersede the initial single-radio POC handlers above.
window.writeFrequency = v => run(async () => {
    snapshot = await api(`/radios/${radioId}/frequency/${v}`, {
        method: 'PUT',
        body: JSON.stringify({
            value: Number($(`freq-${v}`).value)
        })
    });
    renderSnapshot(false)
}, `VFO ${v} frequency updated.`);
window.writeCore = id => run(async () => {
    const endpoint = id === 'active-vfo' ? `/radios/${radioId}/active-vfo` : `/radios/${radioId}/${id}`;
    const value = id === 'split' ? $(id).checked : $(id).value;
    snapshot = await api(endpoint, {
        method: 'PUT',
        body: JSON.stringify({
            value
        })
    });
    renderSnapshot(false)
}, `${id} updated.`);
window.writeControl = id => run(async () => {
    await api(`/radios/${radioId}/controls/${id}`, {
        method: 'PUT',
        body: JSON.stringify({
            value: Number($(`control-${id}`).value)
        })
    })
}, `${id} updated.`);
window.writeSwitch = id => run(async () => {
    await api(`/radios/${radioId}/switches/${id}`, {
        method: 'PUT',
        body: JSON.stringify({
            value: $(`switch-${id}`).checked
        })
    })
}, `${id} updated.`);
window.writeChoice = id => run(async () => {
    await api(`/radios/${radioId}/choices/${id}`, {
        method: 'PUT',
        body: JSON.stringify({
            value: $(`choice-${id}`).value
        })
    })
}, `${id} updated.`);
window.writePassband = () => run(async () => {
    await api(`/radios/${radioId}/passband`, {
        method: 'PUT',
        body: JSON.stringify({
            value: Number($('passband').value)
        })
    })
}, 'Passband updated.');

function renderOffline() {
    stopPttRenewal();
    pttLeaseActive = false;
    for (const timer of frequencyWriteTimers.values()) clearTimeout(timer);
    frequencyWriteTimers.clear();
    $('radio-title').textContent = 'No radio connected';
    $('radio-summary').textContent = 'Capability-driven controls appear after connection.';
    $('connection-badge').textContent = 'OFFLINE';
    $('connection-badge').className = 'badge offline';
    $('ptt').textContent = 'PTT OFF';
    $('ptt').className = 'ptt';
    $('ptt').disabled = true;
    $('vfo-deck').className = 'vfo-deck empty';
    $('vfo-deck').innerHTML = '<div class="empty-state">Connect to display the radio\'s advertised VFO topology.</div>';
    $('core-card').className = 'panel empty-state';
    $('core-card').textContent = 'Runtime capabilities and core state will appear here.';
    $('controls-grid').innerHTML = '';
    $('meters-grid').innerHTML = '';
    setBusy(false)
}
ui.model.onchange = renderModel;
ui.transport.onchange = renderTransport;
$('port-override-enabled').onchange = e => $('port-override').disabled = !e.target.checked;
$('refresh-ports').onclick = () => run(loadPorts, 'Serial ports refreshed.');
ui.connect.onclick = () => run(connect);
ui.disconnect.onclick = () => run(disconnect);
ui.refresh.onclick = () => run(refreshAll, 'All readable values refreshed.');
$('ptt').onclick = window.togglePtt;
$('clear-log').onclick = () => $('log').textContent = '';
document.querySelectorAll('.tab').forEach(t => t.onclick = () => {
    document.querySelectorAll('.tab,.tab-page').forEach(x => x.classList.remove('active'));
    t.classList.add('active');
    $(`tab-${t.dataset.tab}`).classList.add('active')
});
bootstrap().catch(e => status('Startup failed', e.message, 'error'));
