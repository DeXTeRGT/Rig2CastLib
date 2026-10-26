const $ = id => document.getElementById(id);
let models = [],
    snapshot = null,
    ws = null,
    busy = false,
    radioId = null,
    isOwner = false,
    clientRole = 'Observer',
    pttLeaseActive = false,
    pttRenewalTimer = null,
    dialRotation = 0,
    directEntryVfo = null,
    meterPollTimer = null,
    meterPollInFlight = false,
    remoteFrequencyRanges = [],
    spectrumSocket = null,
    spectrumFrame = null,
    spectrumDragVfo = null,
    spectrumDisplaySamples = null,
    spectrumAverageRows = [],
    spectrumAverageSums = null,
    spectrumAverageGeometry = null,
    spectrumTurboGradient = null,
    spectrumTurboGradientHeight = 0,
    waterfallInitialized = false,
    waterfallLastRowAt = 0;
const waterfallBuffer = document.createElement('canvas');
const turboPalette = (() => {
    const encoded = 'MBI7MRVCMhhKNBtRNR5YNiFfNyNlOCZsOSlyOix5Oy9/PDKFPDWLPTeRPjqWPz2cQEChQEOmQUWrQUiwQku1Q066Q1C+Q1PCRFbHRFjLRVvORV7SRWDWRWPZRmbdRmjgRmvjRm3mRnDoRnPrRnXtRnjwRnryRn30Rn/2RoL4RYT5RYf7RYn8RIz9Q479QpH+QZP+QJb+P5j+Ppv+PJ39O6D8OaL8OKX7Nqj5NKr4M6z2Ma/1L7HzLbTxK7bvKrntKLvrJr3pJcDmI8LkIcThIMbfHsncHcvaHM3XG8/UGtHSGdPPGNXMGNfKF9nHF9rEF9zCF96/GOC9GOG6GeO4GuS2G+W0HeexHuivIOmsIuupJOymJ+2jKe6gLO+dL/CaMvGXNfOUOPSRO/SNP/WKQvaHRveDSviATfl8Ufl5Vfp2WftyXftvYfxsZfxoaf1lbf1icf1fdP5ceP5ZfP5WgP5ThP5Qh/5Ni/5Ljv5Ikv5Glf5EmP5Cm/1Anv0+ofw9pPw7pvs6qfs5rPo3rvk3sfg2s/g1tvc1ufU0u/Q0vvM0wPIzw/Ezxe8zyO4zyu0zzes0z+o00eg01Oc11uU12OM12uI23eA239424dw349o35dg459c46NU46tM57NE57c8578058Ms68sg688Y69MQ69sI698A5+L45+bw5+bo4+rc3+7U3+7M2/LA1/K40/asz/aky/aYx/aMw/qEv/p4u/pst/pgs/ZUr/ZIp/Y8o/Ywn/Ikm/IYk+4Mj+4Ai+n0g+nof+Xce+HQc93Eb924a9msY9WgX9GUW82MV8mAU8V0T71oR7lgQ7VUP7FIO6lAN6U0N6EsM5kkL5UYK40QK4kIJ4EAI3j4I3TwH2zoH2TgG1zYG1jQF1DIF0jAF0C8Ezi0EyysDySkDxygDxSYCwyQCwCMCviECux8BuR4BthwBtBsBsRkBrhgBrBYBqRUBphQBoxIBoBEBnRABmg4Blw0BlAwBkQsBjgoBiwkBhwgBhAcBgQYCfQUCegQC';
    const binary = atob(encoded), bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes
})();
const frequencyWriters = new Map();
const controlWriteTimers = new Map();
const frequencyWriteIntervalMs = 125;
const controlValues = new Map(), choiceValues = new Map();
const clientId = sessionStorage.getItem('rig2castClientId') ||
    globalThis.crypto?.randomUUID?.() ||
    `client-${Date.now()}-${Math.random().toString(36).slice(2)}`;
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

function applyViewPreferences() {
    const sidebarHidden = localStorage.getItem('rig2castHideSidebar') === 'true';
    const panelsHidden = localStorage.getItem('rig2castHideBottomPanels') === 'true';
    document.body.classList.toggle('hide-sidebar', sidebarHidden);
    document.body.classList.toggle('hide-bottom-panels', panelsHidden);
    $('sidebar-toggle').setAttribute('aria-pressed', String(sidebarHidden));
    $('sidebar-toggle').title = sidebarHidden ? 'Show the connection panel' : 'Hide the connection panel';
    $('panels-toggle').setAttribute('aria-pressed', String(panelsHidden));
    $('panels-toggle').title = panelsHidden ? 'Show the lower panels' : 'Hide the lower panels'
}

function toggleViewPreference(storageKey, bodyClass) {
    const hidden = !document.body.classList.contains(bodyClass);
    localStorage.setItem(storageKey, String(hidden));
    applyViewPreferences()
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
    applyViewPreferences();
    const s = await api('/status');
    remoteFrequencyRanges = s.station?.remoteFrequencyRanges || [];
    models = await api('/models');
    ui.model.innerHTML = models.map(m => `<option value="${esc(m.id)}">${esc(m.manufacturer)} ${esc(m.model)}</option>`).join('');
    await loadPorts();
    renderModel();
    if (s.station?.locked) {
        $('connection-form').hidden = true;
        $('station-lock-summary').hidden = false;
        $('station-lock-summary').textContent = s.station.displayName;
        $('audio-config').hidden = true;
        $('audio-mic').checked = !!s.station.audioAllowMicrophone;
        $('audio-mic').disabled = !s.station.audioAllowMicrophone;
        ui.connect.textContent = 'Attach station';
    }
    if (!s.serverAllowsWrites) {
        $('enable-writes').disabled = true;
        $('enable-writes').title = 'Start with Rig2Cast__AllowWrites=true to permit operator sessions.'
    }
    status('Offline', `${s.activeRadios} server-owned radio(s) active. ${s.serverAllowsWrites ? 'Writes may be requested.' : 'Server is read-only.'}`);
    openSpectrumSocket();
}

function openSpectrumSocket() {
    if (spectrumSocket) spectrumSocket.close();
    const protocol = location.protocol === 'https:' ? 'wss' : 'ws';
    spectrumSocket = new WebSocket(`${protocol}://${location.host}/api/v1/spectrum/stream`);
    spectrumSocket.onmessage = event => {
        spectrumFrame = JSON.parse(event.data);
        initializeSpectrumAverage(spectrumFrame.defaultAverage);
        initializeWaterfall(spectrumFrame);
        spectrumDisplaySamples = averageSpectrumSamples(spectrumFrame);
        appendWaterfallRow(spectrumDisplaySamples, spectrumFrame);
        $('spectrum-state').textContent = 'LIVE';
        $('spectrum-state').className = 'badge online';
        renderSpectrum()
    };
    spectrumSocket.onerror = () => {
        $('spectrum-state').textContent = 'OFFLINE';
        $('spectrum-state').className = 'badge offline'
    };
    spectrumSocket.onclose = () => {
        spectrumSocket = null;
        $('spectrum-state').textContent = 'OFFLINE';
        $('spectrum-state').className = 'badge offline';
        setTimeout(openSpectrumSocket, 2000)
    }
}

function spectrumVfo(frame, name) {
    if (frame.activeVfo === name) return { name, mode: frame.activeMode, frequencyHz: frame.activeFrequencyHz, active: true };
    return { name, mode: frame.inactiveMode, frequencyHz: frame.inactiveFrequencyHz, active: false }
}

function spectrumX(frequency, width, frame = spectrumFrame) {
    return (frequency - frame.leftHz) / (frame.rightHz - frame.leftHz) * width
}

function spectrumFrequency(clientX, canvas) {
    const rect = canvas.getBoundingClientRect();
    const ratio = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
    return Math.round(spectrumFrame.leftHz + ratio * (spectrumFrame.rightHz - spectrumFrame.leftHz))
}

function decodeSpectrumSamples(value) {
    const binary = atob(value), samples = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) samples[i] = binary.charCodeAt(i);
    return samples
}

function initializeSpectrumAverage(defaultValue) {
    const select = $('spectrum-average');
    if (!select.options.length) {
        select.innerHTML = Array.from({ length: 10 }, (_, index) => `<option>${index + 1}</option>`).join('');
        const stored = Number(localStorage.getItem('rig2castSpectrumAverage'));
        select.value = String(stored >= 1 && stored <= 10 ? stored : defaultValue)
    }
}

function resetSpectrumAverage() {
    spectrumAverageRows = [];
    spectrumAverageSums = null;
    spectrumAverageGeometry = null;
    resetWaterfall()
}

function averageSpectrumSamples(frame) {
    const samples = decodeSpectrumSamples(frame.samples);
    const geometry = `${frame.leftHz}:${frame.rightHz}:${samples.length}`;
    if (geometry !== spectrumAverageGeometry) {
        resetSpectrumAverage();
        spectrumAverageGeometry = geometry
    }
    const depth = Math.max(1, Math.min(10, Number($('spectrum-average').value) || frame.defaultAverage || 1));
    if (!spectrumAverageSums) spectrumAverageSums = new Uint32Array(samples.length);
    spectrumAverageRows.push(samples);
    for (let i = 0; i < samples.length; i++) spectrumAverageSums[i] += samples[i];
    while (spectrumAverageRows.length > depth) {
        const removed = spectrumAverageRows.shift();
        for (let i = 0; i < removed.length; i++) spectrumAverageSums[i] -= removed[i]
    }
    const averaged = new Uint8Array(samples.length);
    for (let i = 0; i < averaged.length; i++) averaged[i] = Math.round(spectrumAverageSums[i] / spectrumAverageRows.length);
    return averaged
}

function turboColorAt(normalized) {
    const index = Math.trunc(Math.max(0, Math.min(1, normalized)) * 255) * 3;
    return `rgb(${turboPalette[index]},${turboPalette[index + 1]},${turboPalette[index + 2]})`
}

function turboGradient(ctx, height) {
    if (spectrumTurboGradient && spectrumTurboGradientHeight === height) return spectrumTurboGradient;
    const gradient = ctx.createLinearGradient(0, 0, 0, height);
    for (let index = 0; index < 9; index++) {
        const position = index / 8;
        gradient.addColorStop(position, turboColorAt(1 - position))
    }
    spectrumTurboGradient = gradient;
    spectrumTurboGradientHeight = height;
    return gradient
}

function initializeWaterfall(frame) {
    const height = Math.max(100, Math.min(800, Number(frame.waterfallHeight) || 260));
    $('waterfall-canvas').style.height = `${height}px`;
    if (waterfallBuffer.height !== height) {
        waterfallBuffer.height = height;
        resetWaterfall()
    }
    if (waterfallInitialized) return;
    waterfallInitialized = true;
    const stored = localStorage.getItem('rig2castShowWaterfall');
    setWaterfallVisible(stored === null ? !!frame.showWaterfall : stored === 'true')
}

function setWaterfallVisible(visible) {
    $('waterfall-wrap').hidden = !visible;
    $('waterfall-toggle').setAttribute('aria-pressed', String(visible));
    if (visible) requestAnimationFrame(renderWaterfall)
}

function resetWaterfall() {
    waterfallLastRowAt = 0;
    if (!waterfallBuffer.width || !waterfallBuffer.height) return;
    const ctx = waterfallBuffer.getContext('2d');
    ctx.fillStyle = '#000';
    ctx.fillRect(0, 0, waterfallBuffer.width, waterfallBuffer.height)
}

function appendWaterfallRow(samples, frame) {
    if (!samples?.length) return;
    const now = performance.now(), rate = Math.max(1, Math.min(60, Number(frame.waterfallRate) || 15));
    if (waterfallLastRowAt && now - waterfallLastRowAt < 1000 / rate) return;
    waterfallLastRowAt = now;
    if (waterfallBuffer.width !== samples.length) {
        waterfallBuffer.width = samples.length;
        resetWaterfall()
    }
    const ctx = waterfallBuffer.getContext('2d'), width = waterfallBuffer.width, height = waterfallBuffer.height;
    if (height > 1) ctx.drawImage(waterfallBuffer, 0, 0, width, height - 1, 0, 1, width, height - 1);
    const row = ctx.createImageData(width, 1);
    const range = Math.max(1, frame.maximumLevel - frame.minimumLevel);
    for (let i = 0; i < width; i++) {
        const normalized = Math.max(0, Math.min(1, (samples[i] - frame.minimumLevel) / range));
        const paletteIndex = Math.trunc(normalized * 255) * 3, pixel = i * 4;
        row.data[pixel] = turboPalette[paletteIndex];
        row.data[pixel + 1] = turboPalette[paletteIndex + 1];
        row.data[pixel + 2] = turboPalette[paletteIndex + 2];
        row.data[pixel + 3] = 255
    }
    ctx.putImageData(row, 0, 0)
}

function renderWaterfall() {
    if (!spectrumFrame || $('waterfall-wrap').hidden || !waterfallBuffer.width) return;
    const canvas = $('waterfall-canvas'), rect = canvas.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
    const width = Math.max(1, Math.round(rect.width)), height = Math.max(1, Math.round(rect.height));
    if (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(height * dpr)) {
        canvas.width = Math.round(width * dpr); canvas.height = Math.round(height * dpr)
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.imageSmoothingEnabled = true;
    ctx.drawImage(waterfallBuffer, 0, 0, width, height);
    drawWaterfallMarker(ctx, spectrumVfo(spectrumFrame, 'A'), width, height, '#55d7ff');
    drawWaterfallMarker(ctx, spectrumVfo(spectrumFrame, 'B'), width, height, '#ffbd55')
}

function drawWaterfallMarker(ctx, vfo, width, height, color) {
    if (!Number.isFinite(vfo.frequencyHz) || vfo.frequencyHz < spectrumFrame.leftHz || vfo.frequencyHz > spectrumFrame.rightHz) return;
    const x = spectrumX(vfo.frequencyHz, width);
    ctx.strokeStyle = color; ctx.globalAlpha = vfo.active ? .65 : .45; ctx.lineWidth = vfo.active ? 1.5 : 1;
    ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, height); ctx.stroke(); ctx.globalAlpha = 1
}

function renderSpectrum() {
    if (!spectrumFrame) return;
    const canvas = $('spectrum-canvas'), rect = canvas.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
    const width = Math.max(1, Math.round(rect.width)), height = Math.max(1, Math.round(rect.height));
    if (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(height * dpr)) {
        canvas.width = Math.round(width * dpr); canvas.height = Math.round(height * dpr);
        spectrumTurboGradient = null
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, height);
    const gradient = ctx.createLinearGradient(0, 0, 0, height);
    gradient.addColorStop(0, '#0c1921'); gradient.addColorStop(1, '#03070a');
    ctx.fillStyle = gradient; ctx.fillRect(0, 0, width, height);
    ctx.strokeStyle = '#17303c'; ctx.lineWidth = 1;
    for (let i = 1; i < 10; i++) { const x = i * width / 10; ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, height); ctx.stroke() }
    for (let i = 1; i < 5; i++) { const y = i * height / 5; ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(width, y); ctx.stroke() }
    const samples = spectrumDisplaySamples || decodeSpectrumSamples(spectrumFrame.samples);
    const levelRange = Math.max(1, spectrumFrame.maximumLevel - spectrumFrame.minimumLevel);
    const point = i => {
        const normalized = Math.max(0, Math.min(1, (samples[i] - spectrumFrame.minimumLevel) / levelRange));
        return [i * width / (samples.length - 1), height - normalized * height]
    };
    ctx.beginPath();
    for (let i = 0; i < samples.length; i++) { const [x, y] = point(i); i ? ctx.lineTo(x, y) : ctx.moveTo(x, y) }
    ctx.lineTo(width, height); ctx.lineTo(0, height); ctx.closePath();
    ctx.fillStyle = turboGradient(ctx, height); ctx.fill();
    ctx.beginPath();
    for (let i = 0; i < samples.length; i++) { const [x, y] = point(i); i ? ctx.lineTo(x, y) : ctx.moveTo(x, y) }
    ctx.strokeStyle = 'rgba(245,245,245,.92)'; ctx.lineWidth = 1; ctx.stroke();
    drawSpectrumMarker(ctx, spectrumVfo(spectrumFrame, 'A'), width, height, '#55d7ff');
    drawSpectrumMarker(ctx, spectrumVfo(spectrumFrame, 'B'), width, height, '#ffbd55');
    $('spectrum-range').textContent = `${frequencyText(spectrumFrame.leftHz)} – ${frequencyText(spectrumFrame.rightHz)} Hz · span ${frequencyText(spectrumFrame.spanHz)} Hz`;
    const a = spectrumVfo(spectrumFrame, 'A'), b = spectrumVfo(spectrumFrame, 'B');
    $('spectrum-vfo-a').textContent = `VFO A ${frequencyText(a.frequencyHz)} ${a.mode}${a.active ? ' · ACTIVE' : ''}`;
    $('spectrum-vfo-b').textContent = `VFO B ${frequencyText(b.frequencyHz)} ${b.mode}${b.active ? ' · ACTIVE' : ''}`;
    renderWaterfall()
}

function drawSpectrumMarker(ctx, vfo, width, height, color) {
    if (!Number.isFinite(vfo.frequencyHz) || vfo.frequencyHz < spectrumFrame.leftHz || vfo.frequencyHz > spectrumFrame.rightHz) return;
    const x = spectrumX(vfo.frequencyHz, width);
    ctx.strokeStyle = color; ctx.lineWidth = vfo.active ? 2 : 1.25; ctx.setLineDash(vfo.active ? [] : [5, 4]);
    ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, height); ctx.stroke(); ctx.setLineDash([]);
    ctx.fillStyle = color; ctx.beginPath(); ctx.moveTo(x - 7, 0); ctx.lineTo(x + 7, 0); ctx.lineTo(x, 10); ctx.closePath(); ctx.fill();
    ctx.font = '800 11px system-ui'; ctx.fillText(`VFO ${vfo.name} · ${vfo.mode}`, Math.max(4, Math.min(width - 78, x + 6)), 18)
}

function spectrumCanTune(vfo) {
    if (!snapshot || !radioId || !snapshot.authorization.canControl) return false;
    const c = snapshot.capabilities;
    return access(c.frequency.feature, 'write') && c.frequency.targets.some(value => String(value).toLowerCase() === vfo.toLowerCase())
}

function nearestSpectrumMarker(clientX, canvas) {
    const rect = canvas.getBoundingClientRect();
    let nearest = null, distance = 15;
    for (const name of ['A', 'B']) {
        const vfo = spectrumVfo(spectrumFrame, name);
        if (!Number.isFinite(vfo.frequencyHz)) continue;
        const current = Math.abs(clientX - rect.left - spectrumX(vfo.frequencyHz, rect.width));
        if (current < distance) { nearest = name; distance = current }
    }
    return nearest
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
    startMeterPolling();
    status(result.readOnly ? 'Attached read-only' : 'Connected as operator', result.message, 'online');
    log(`${result.message} Radio ID: ${radioId}.`)
}
async function disconnect() {
    stopPttRenewal();
    stopMeterPolling();
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
        applyOptimisticFrequencies();
        renderSnapshot(false)
    };
    ws.onerror = () => log('WebSocket event stream interrupted.', 'WARN');
    ws.onclose = () => {
        if (connected()) setTimeout(openSocket, 1500)
    }
}

function frequencyText(value) {
    if (!value) return '—';
    return Math.trunc(value).toLocaleString('de-DE').replace(/,/g, '.')
}

function renderSnapshot(rebuild = true) {
    if (!snapshot) return;
    const c = snapshot.capabilities,
        s = snapshot.state,
        writes = snapshot.authorization.canControl;
    $('radio-title').textContent = `${c.manufacturer} ${c.model}`;
    const rangeViolation = remotePttViolation(c, s);
    $('radio-summary').textContent = `VFO ${s.activeVfo} · ${s.mode} · Split ${s.isSplit ? 'ON' : 'OFF'} · ${s.isTransmitting ? 'TX' : 'RX'} · ${clientRole}${rangeViolation ? ` · ${rangeViolation}` : ''}`;
    $('connection-badge').textContent = `${s.connection} · ${clientRole.toUpperCase()}`;
    $('connection-badge').className = 'badge online';
    renderPtt(c, s);
    renderVfos(c, s, writes);
    renderTuningDial(c, s, writes);
    renderCore(c, s, writes);
    renderSoftControls(c, s, writes);
    if (rebuild) {
        renderControls(c, writes);
        renderMeters(c);
        renderFrontMeters(c);
        renderMoreCapabilities(c);
        renderCommonKnobs(c, writes)
    }
    applyModeAvailability(c, s.mode, writes);
    setBusy(false)
}

function renderVfos(c, s, writes) {
    const deck = $('vfo-deck');
    deck.className = 'vfo-deck';
    const visibleVfos = c.vfos.available.filter(v => String(v).toLowerCase() !== 'memory');
    const vfos = visibleVfos.length ? visibleVfos : c.vfos.available;
    deck.innerHTML = vfos.map(v => {
        const hz = s.frequenciesHz[v] ?? s.vfos?.[v]?.frequencyHz ?? 0;
        const writable = writes && access(c.frequency.feature, 'write') && c.frequency.targets.some(x => String(x).toLowerCase() === String(v).toLowerCase());
        const splitTx = s.isSplit && v === s.transmitVfo;
        const role = v === s.activeVfo ? `ACTIVE · RX${splitTx ? ' · TX' : ''}` : splitTx ? 'SPLIT · TX' : 'STANDBY';
        const mode = s.vfos?.[v]?.mode ?? s.mode;
        const step = selectedTuningStep(c);
        const stepChoice = findChoice(c, 'TuningStep');
        const stepOptions = stepChoice ? Object.values(stepChoice.options).filter(option => !option.applicableModes || option.applicableModes.some(value => String(value).toLowerCase() === String(mode).toLowerCase())) : [];
        const stepControl = stepChoice ? `<select class="vfo-step-select" aria-label="VFO tuning step in hertz" ${writes && access(stepChoice.feature, 'write') ? '' : 'disabled'} onclick="event.stopPropagation()" onpointerdown="event.stopPropagation()" onchange="setTuningStep(event, '${stepChoice.id}')">${stepOptions.map(option => `<option value="${esc(option.value)}" ${String(option.value) === String(choiceValues.get(String(stepChoice.id).toLowerCase())) ? 'selected' : ''}>${stepOptionLabel(option.value)}</option>`).join('')}</select>` : `<strong>${formatStep(step)}</strong>`;
        const hint = writable ? 'Use the tuning knob, mouse wheel, or arrow keys. Shift: 10×; Ctrl: 100×' : 'Read-only VFO';
        const selectable = writes && access(c.vfos.selection, 'write');
        return `<article class="vfo-card ${v === s.activeVfo ? 'active' : ''} ${splitTx ? 'split-tx' : ''} ${selectable ? 'selectable' : ''}" ${selectable ? `tabindex="0" role="button" aria-label="Select VFO ${esc(v)}" onclick="selectVfo('${v}')" onkeydown="selectVfoKey(event, '${v}')"` : ''}><div class="vfo-card-header"><div class="vfo-name">VFO ${esc(v)}</div><span class="vfo-role">${role}</span></div><div id="frequency-${v}" class="frequency ${writable ? 'tunable' : ''}" title="${hint}" onclick="event.stopPropagation()" ${writable ? `tabindex="0" role="slider" aria-label="Tune VFO ${esc(v)}" onwheel="tuneFrequency(event, '${v}')" onkeydown="tuneFrequencyKey(event, '${v}')"` : ''}>${frequencyText(hz)}</div><div class="vfo-details"><div class="vfo-detail"><small>MODE</small><strong>${esc(mode)}</strong></div><div class="vfo-detail"><small>STEP</small>${stepControl}</div><div class="vfo-detail"><small>STATE</small><strong>${splitTx ? 'TX' : v === s.activeVfo ? 'RX' : 'READY'}</strong></div></div></article>`
    }).join('')
}

function findChoice(c, id) {
    return Object.values(c.choices || {}).find(choice => String(choice.id).toLowerCase() === id.toLowerCase())
}

function selectedTuningStep(c) {
    const choice = findChoice(c, 'TuningStep');
    const selected = choiceValues.get(String(choice?.id).toLowerCase());
    const parsed = parseStepHz(selected);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : Math.max(1, c.frequency.smallestStepHz || 1)
}

function parseStepHz(value) {
    const text = String(value ?? '').trim().toLowerCase();
    const number = Number.parseFloat(text);
    if (!Number.isFinite(number)) return NaN;
    return text.includes('khz') ? number * 1000 : text.includes('mhz') ? number * 1000000 : number
}

function stepOptionLabel(value) {
    const hz = parseStepHz(value);
    if (!Number.isFinite(hz)) return esc(value);
    if (hz >= 1000000) return `${hz / 1000000}M`;
    if (hz >= 1000) return `${hz / 1000}k`;
    return String(hz)
}

function formatStep(step) {
    return step >= 1000 ? `${step / 1000} kHz` : `${step} Hz`
}

function activeWritableVfo(c, s, writes) {
    if (!writes || String(s.activeVfo).toLowerCase() === 'memory' || !access(c.frequency.feature, 'write')) return null;
    return c.frequency.targets.find(v => String(v).toLowerCase() === String(s.activeVfo).toLowerCase()) ?? null
}

function renderTuningDial(c, s, writes) {
    const vfo = activeWritableVfo(c, s, writes), dial = $('tuning-dial');
    const step = selectedTuningStep(c);
    $('tuning-step').textContent = vfo ? `VFO ${vfo} · ${formatStep(step)}` : 'READ ONLY';
    dial.dataset.vfo = vfo ?? '';
    dial.setAttribute('aria-disabled', String(!vfo));
    dial.tabIndex = vfo ? 0 : -1;
    $('rx-state').textContent = s.isTransmitting ? 'TX' : 'RX';
    $('rx-state').classList.toggle('transmitting', s.isTransmitting)
}

function renderSoftControls(c, s, writes) {
    const modeWritable = writes && access(c.modes.feature, 'write');
    $('mode-open').disabled = !c.modes.values?.length;
    $('mode-open').classList.toggle('active', modeWritable);
    $('direct-open').disabled = !activeWritableVfo(c, s, writes);
    $('split-soft').disabled = !(writes && access(c.vfos.split, 'write'));
    $('split-soft').classList.toggle('active', s.isSplit);
    $('more-open').disabled = !(Object.keys(c.controls || {}).length || Object.keys(c.switches || {}).length || Object.keys(c.choices || {}).length);
    renderModeChoices(c, s, modeWritable)
}

function renderModeChoices(c, s, writable) {
    const preferred = ['USB', 'LSB', 'CW', 'AM', 'FM'];
    const values = [...(c.modes.values || [])].map(String);
    const primary = preferred.filter(mode => values.some(value => value.toUpperCase() === mode));
    if (!primary.length) primary.push(...values.slice(0, 5));
    const secondary = values.filter(value => !primary.includes(value));
    const buttons = modes => modes.map(mode => `<button class="${String(s.mode).toLowerCase() === mode.toLowerCase() ? 'active' : ''}" ${writable ? '' : 'disabled'} onclick="setMode('${esc(mode)}')">${esc(mode)}</button>`).join('');
    $('primary-modes').innerHTML = buttons(primary);
    $('more-modes').innerHTML = buttons(secondary);
    $('more-modes-toggle').hidden = !secondary.length
}

function renderFrontMeters(c) {
    const meters = selectedMeters(c);
    const panel = $('front-meters');
    panel.className = meters.length ? 'front-meters' : 'front-meters empty-state';
    panel.innerHTML = meters.length ? meters.map(m => `<div class="front-meter"><div class="front-meter-head"><span>${esc(m.displayName)}</span><small>${esc(m.rawUnit)}</small></div><div id="front-meter-value-${m.id}" class="front-meter-value">—</div><div class="front-meter-track"><div id="front-meter-fill-${m.id}" class="front-meter-fill"></div></div></div>`).join('') : 'This radio advertises no meters.'
}

function selectedMeters(c) {
    const meters = Object.values(c.meters || {}), byId = new Map(meters.map(x => [String(x.id).toLowerCase(), x]));
    let saved = [];
    try { saved = JSON.parse(localStorage.getItem('rig2castFrontMeters') || '[]') } catch {}
    let selected = saved.map(id => byId.get(String(id).toLowerCase())).filter(Boolean).slice(0, 3);
    if (!selected.length) {
        const priority = ['signalstrength', 'power', 'swr'];
        selected = priority.map(id => byId.get(id)).filter(Boolean);
        if (selected.length < Math.min(3, meters.length))
            selected.push(...meters.filter(x => !selected.includes(x)).slice(0, 3 - selected.length))
    }
    return selected
}

function renderMeterPicker(c) {
    const selected = new Set(selectedMeters(c).map(x => String(x.id).toLowerCase()));
    $('meter-picker').innerHTML = Object.values(c.meters || {}).map(m => `<label class="meter-choice"><input type="checkbox" value="${esc(m.id)}" ${selected.has(String(m.id).toLowerCase()) ? 'checked' : ''} onchange="chooseFrontMeter(this)">${esc(m.displayName)}</label>`).join('') || '<span class="empty-state">No meters advertised.</span>'
}

window.chooseFrontMeter = checkbox => {
    const checked = [...document.querySelectorAll('#meter-picker input:checked')];
    if (checked.length > 3) {
        checkbox.checked = false;
        status('Meter limit', 'Choose a maximum of three front-panel meters.', 'error');
        return
    }
    localStorage.setItem('rig2castFrontMeters', JSON.stringify(checked.map(x => x.value)));
    renderFrontMeters(snapshot.capabilities);
    refreshSelectedMeters()
};

function renderMoreCapabilities(c) {
    const items = [
        ...Object.values(c.controls || {}).map(x => [x.displayName, 'Level']),
        ...Object.values(c.switches || {}).map(x => [x.displayName, 'Switch']),
        ...Object.values(c.choices || {}).map(x => [x.displayName, 'Choice'])
    ];
    $('more-capabilities').innerHTML = items.length ? items.map(x => `<div class="capability-chip"><strong>${esc(x[0])}</strong><small>${x[1]}</small></div>`).join('') : '<div class="empty-state">No additional controls are advertised.</div>'
    renderMeterPicker(c)
}

function renderCommonKnobs(c, writes) {
    const preferred = ['afgain', 'rfgain', 'squelch'];
    const controls = preferred.map(id => Object.values(c.controls || {}).find(x => String(x.id).toLowerCase() === id)).filter(Boolean);
    $('common-knobs').innerHTML = controls.map(x => {
        const value = controlValues.get(String(x.id).toLowerCase()) ?? x.minimum;
        const ratio = x.maximum === x.minimum ? 0 : (value - x.minimum) / (x.maximum - x.minimum);
        const angle = -135 + Math.max(0, Math.min(1, ratio)) * 270;
        const writable = writes && access(x.feature, 'write') && modeAllows(x, 'write', snapshot.state.mode);
        return `<div class="knob-control"><span>${esc(x.displayName).toUpperCase()}</span><div id="front-knob-${x.id}" class="control-knob" style="--knob-angle:${angle}deg;--knob-fill:${Math.max(0, Math.min(75, ratio * 75))}%" role="slider" aria-label="${esc(x.displayName)}" aria-valuemin="${x.minimum}" aria-valuemax="${x.maximum}" aria-valuenow="${value}" tabindex="${writable ? '0' : '-1'}" aria-disabled="${!writable}" onpointerdown="startControlKnob(event, '${x.id}')" onwheel="turnControlWheel(event, '${x.id}')" onkeydown="turnControlKey(event, '${x.id}')"><div class="knob-pointer"></div></div><strong id="front-knob-value-${x.id}" class="knob-value">${formatFrontControlValue(x, value)}</strong></div>`
    }).join('')
}

function frontControlDescriptor(id) {
    return Object.values(snapshot?.capabilities.controls || {}).find(x => String(x.id).toLowerCase() === String(id).toLowerCase())
}

function formatFrontControlValue(descriptor, value) {
    const ratio = descriptor.maximum === descriptor.minimum ? 0 : (value - descriptor.minimum) / (descriptor.maximum - descriptor.minimum);
    return `${Math.round(Math.max(0, Math.min(1, ratio)) * 100)}%`
}

function previewFrontControl(id, value) {
    const descriptor = frontControlDescriptor(id);
    if (!descriptor) return;
    const ratio = (value - descriptor.minimum) / (descriptor.maximum - descriptor.minimum);
    const knob = $(`front-knob-${id}`);
    knob.style.setProperty('--knob-angle', `${-135 + ratio * 270}deg`);
    knob.style.setProperty('--knob-fill', `${ratio * 75}%`);
    knob.setAttribute('aria-valuenow', String(value));
    $(`front-knob-value-${id}`).textContent = formatFrontControlValue(descriptor, value)
}

function adjustFrontControl(id, direction, multiplier = 1) {
    const descriptor = frontControlDescriptor(id);
    if (!descriptor || !snapshot.authorization.canControl || !access(descriptor.feature, 'write')) return false;
    const key = String(id).toLowerCase();
    const current = controlValues.get(key) ?? descriptor.minimum;
    const value = Math.max(descriptor.minimum, Math.min(descriptor.maximum, current + direction * descriptor.step * multiplier));
    if (value === current) return false;
    controlValues.set(key, value);
    previewFrontControl(id, value);
    clearTimeout(controlWriteTimers.get(key));
    controlWriteTimers.set(key, setTimeout(() => commitFrontControl(id, value), 200));
    return true
}

async function commitFrontControl(id, value) {
    controlWriteTimers.delete(String(id).toLowerCase());
    try { await api(`/radios/${radioId}/controls/${id}`, { method: 'PUT', body: JSON.stringify({ value }) }) }
    catch (error) { status(`${id} update failed`, error.message, 'error'); log(`${id}: ${error.message}`, 'ERROR') }
}

window.turnControlWheel = (event, id) => {
    const descriptor = frontControlDescriptor(id);
    if (!descriptor || !snapshot?.authorization.canControl || !access(descriptor.feature, 'write')) return;
    event.preventDefault();
    adjustFrontControl(id, event.deltaY < 0 ? 1 : -1, tuningMultiplier(event))
};

window.turnControlKey = (event, id) => {
    if (!['ArrowUp', 'ArrowRight', 'ArrowDown', 'ArrowLeft'].includes(event.key)) return;
    const direction = event.key === 'ArrowUp' || event.key === 'ArrowRight' ? 1 : -1;
    if (adjustFrontControl(id, direction, tuningMultiplier(event))) event.preventDefault()
};

window.setTuningStep = (event, id) => {
    event.stopPropagation();
    const value = event.target.value;
    run(async () => {
        await api(`/radios/${radioId}/choices/${id}`, { method: 'PUT', body: JSON.stringify({ value }) });
        choiceValues.set(String(id).toLowerCase(), value);
        renderSnapshot(false)
    }, `Tuning step changed to ${event.target.selectedOptions[0].textContent}.`)
};

function canControlPtt() {
    return !!snapshot && clientRole.toLowerCase() === 'operator' &&
        snapshot.authorization.canControl && access(snapshot.capabilities.transmit, 'write') &&
        !remotePttViolation(snapshot.capabilities, snapshot.state)
}

function frequencyInRemoteRange(frequency) {
    return !remoteFrequencyRanges.length || remoteFrequencyRanges.some(range =>
        frequency >= range.minimumHz && frequency <= range.maximumHz)
}

function remotePttViolation(c, s) {
    if (!remoteFrequencyRanges.length) return '';
    for (const vfo of c.vfos.available.filter(vfo => String(vfo).toLowerCase() !== 'memory')) {
        const frequency = s.frequenciesHz[vfo] ?? s.vfos?.[vfo]?.frequencyHz;
        if (!Number.isFinite(frequency) || !frequencyInRemoteRange(frequency))
            return `VFO ${vfo} OUTSIDE REMOTE RANGE`
    }
    return ''
}

function renderPtt(c, s) {
    const button = $('ptt');
    button.textContent = s.isTransmitting ? 'PTT ON' : 'PTT OFF';
    button.classList.toggle('transmitting', s.isTransmitting);
    button.disabled = busy || !canControlPtt();
    const rangeViolation = remotePttViolation(c, s);
    button.title = button.disabled
        ? rangeViolation || 'PTT requires an Operator session and writable transmit capability.'
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

function effectiveFrequencyRanges(driverRanges) {
    if (!remoteFrequencyRanges.length) return driverRanges;
    return driverRanges.flatMap(driver => remoteFrequencyRanges.map(policy => ({
        ...driver,
        minimumHz: Math.max(driver.minimumHz, policy.minimumHz),
        maximumHz: Math.min(driver.maximumHz, policy.maximumHz)
    }))).filter(range => range.minimumHz <= range.maximumHz)
}

function tuningMultiplier(event) {
    return event.ctrlKey ? 100 : event.shiftKey ? 10 : 1
}

function tuneBy(vfo, direction, multiplier = 1) {
    const c = snapshot?.capabilities.frequency;
    if (!c || !snapshot.authorization.canControl || !access(c.feature, 'write') ||
        !c.targets.some(x => String(x).toLowerCase() === String(vfo).toLowerCase())) return false;
    const step = selectedTuningStep(snapshot.capabilities) * multiplier;
    const current = snapshot.state.frequenciesHz[vfo] ?? snapshot.state.vfos?.[vfo]?.frequencyHz;
    if (!Number.isFinite(current)) return false;
    if (remoteFrequencyRanges.length && !frequencyInRemoteRange(current)) return false;
    const ranges = effectiveFrequencyRanges(c.ranges);
    if (remoteFrequencyRanges.length && !ranges.length) return false;
    const next = nearestAllowedFrequency(current + direction * step, ranges);
    snapshot.state.frequenciesHz[vfo] = next;
    if (snapshot.state.vfos?.[vfo]) snapshot.state.vfos[vfo].frequencyHz = next;
    const display = $(`frequency-${vfo}`), input = $(`freq-${vfo}`);
    if (display) display.textContent = frequencyText(next);
    if (input) input.value = next;
    animateDial(direction, multiplier);
    renderTuningDial(snapshot.capabilities, snapshot.state, true);
    queueFrequencyWrite(vfo, next);
    return true
}

function animateDial(direction, multiplier) {
    const degrees = 7 + Math.min(20, Math.log10(Math.max(1, multiplier)) * 7);
    dialRotation += direction * degrees;
    $('dial-motion').style.transform = `rotate(${dialRotation}deg)`;
    const dial = $('tuning-dial');
    dial.classList.add('tuning');
    clearTimeout(dial.animationTimer);
    dial.animationTimer = setTimeout(() => dial.classList.remove('tuning'), 160)
}

window.tuneFrequency = (event, vfo) => {
    if (tuneBy(vfo, event.deltaY < 0 ? 1 : -1, tuningMultiplier(event))) event.preventDefault()
};

window.tuneFrequencyKey = (event, vfo) => {
    if (!['ArrowUp', 'ArrowRight', 'ArrowDown', 'ArrowLeft'].includes(event.key)) return;
    const direction = event.key === 'ArrowUp' || event.key === 'ArrowRight' ? 1 : -1;
    if (tuneBy(vfo, direction, tuningMultiplier(event))) event.preventDefault()
};

function pointerAngle(event, element) {
    const bounds = element.getBoundingClientRect();
    return Math.atan2(event.clientY - (bounds.top + bounds.height / 2), event.clientX - (bounds.left + bounds.width / 2)) * 180 / Math.PI
}

function normalizedAngleDelta(current, previous) {
    let delta = current - previous;
    if (delta > 180) delta -= 360;
    if (delta < -180) delta += 360;
    return delta
}

function startRotaryGesture(event, turn, degreesPerStep = 5) {
    if (event.button !== 0 && event.pointerType === 'mouse') return;
    const target = event.currentTarget;
    let accumulated = 0, previousAngle = pointerAngle(event, target);
    event.preventDefault();
    target.setPointerCapture(event.pointerId);
    target.classList.add(target.classList.contains('control-knob') ? 'turning' : 'tuning');
    const move = e => {
        const angle = pointerAngle(e, target);
        accumulated += normalizedAngleDelta(angle, previousAngle);
        previousAngle = angle;
        while (Math.abs(accumulated) >= degreesPerStep) {
            turn(Math.sign(accumulated), e);
            accumulated -= Math.sign(accumulated) * degreesPerStep
        }
    };
    const end = () => {
        target.classList.remove('tuning', 'turning');
        target.removeEventListener('pointermove', move);
        target.removeEventListener('pointerup', end);
        target.removeEventListener('pointercancel', end)
    };
    target.addEventListener('pointermove', move);
    target.addEventListener('pointerup', end);
    target.addEventListener('pointercancel', end)
}

window.startTuningKnob = (event, vfo) => startRotaryGesture(event,
    (direction, moveEvent) => tuneBy(vfo, direction, tuningMultiplier(moveEvent)), 9);

window.startControlKnob = (event, id) => {
    if (event.currentTarget.getAttribute('aria-disabled') === 'true') return;
    startRotaryGesture(event, (direction, moveEvent) => adjustFrontControl(id, direction, tuningMultiplier(moveEvent)))
};

function tuneFromDial(event, direction) {
    const vfo = $('tuning-dial').dataset.vfo;
    if (vfo && tuneBy(vfo, direction, tuningMultiplier(event))) event.preventDefault()
}

function frequencyWriter(vfo) {
    if (!frequencyWriters.has(vfo)) frequencyWriters.set(vfo, {
        pending: null, optimistic: null, inFlight: false, timer: null, lastStartedAt: 0
    });
    return frequencyWriters.get(vfo)
}

function queueFrequencyWrite(vfo, value) {
    const writer = frequencyWriter(vfo);
    writer.pending = value;
    writer.optimistic = value;
    scheduleFrequencyWrite(vfo, writer)
}

function applyOptimisticFrequencies() {
    if (!snapshot) return;
    for (const [vfo, writer] of frequencyWriters)
        if ((writer.inFlight || writer.pending !== null) && writer.optimistic !== null) {
            snapshot.state.frequenciesHz[vfo] = writer.optimistic;
            if (snapshot.state.vfos?.[vfo]) snapshot.state.vfos[vfo].frequencyHz = writer.optimistic
        }
}

function scheduleFrequencyWrite(vfo, writer) {
    if (writer.inFlight || writer.timer !== null || writer.pending === null || !connected()) return;
    const delay = Math.max(0, frequencyWriteIntervalMs - (performance.now() - writer.lastStartedAt));
    if (delay === 0) flushFrequencyWrite(vfo, writer);
    else writer.timer = setTimeout(() => {
        writer.timer = null;
        flushFrequencyWrite(vfo, writer)
    }, delay)
}

async function flushFrequencyWrite(vfo, writer) {
    if (writer.inFlight || writer.pending === null || !connected()) return;
    const value = writer.pending, requestRadioId = radioId;
    writer.pending = null;
    writer.inFlight = true;
    writer.lastStartedAt = performance.now();
    try {
        const response = await api(`/radios/${requestRadioId}/frequency/${vfo}`, {
            method: 'PUT',
            body: JSON.stringify({ value })
        });
        if (requestRadioId === radioId && writer.pending === null) {
            snapshot = response;
            writer.optimistic = null;
            renderSnapshot(false)
        }
    } catch (e) {
        status('Frequency update failed', e.message, 'error');
        log(`VFO ${vfo}: ${e.message}`, 'ERROR');
        if (requestRadioId === radioId && writer.pending === null) {
            writer.optimistic = null;
            try { snapshot = await api(`/radios/${radioId}/snapshot`) } catch {}
            renderSnapshot(false)
        }
    } finally {
        writer.inFlight = false;
        if (requestRadioId === radioId) scheduleFrequencyWrite(vfo, writer)
    }
}

window.selectVfo = vfo => {
    if (String(snapshot?.state.activeVfo).toLowerCase() === String(vfo).toLowerCase()) return;
    run(async () => {
        snapshot = await api(`/radios/${radioId}/active-vfo`, {
            method: 'PUT', body: JSON.stringify({ value: vfo })
        });
        renderSnapshot(false)
    }, `VFO ${vfo} selected.`)
};

window.selectVfoKey = (event, vfo) => {
    if (event.key !== 'Enter' && event.key !== ' ') return;
    event.preventDefault();
    window.selectVfo(vfo)
};

window.setMode = mode => run(async () => {
    snapshot = await api(`/radios/${radioId}/mode`, {
        method: 'PUT', body: JSON.stringify({ value: mode })
    });
    $('mode-dialog').close();
    renderSnapshot(false)
}, `Mode changed to ${mode}.`);

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
            controlValues.set(String(x.id).toLowerCase(), v.value);
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
            choiceValues.set(String(x.id).toLowerCase(), v.value);
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
    renderSnapshot(false);
    renderCommonKnobs(c, snapshot.authorization.canControl);
    await refreshSelectedMeters()
}

function startMeterPolling() {
    stopMeterPolling();
    meterPollTimer = setInterval(refreshSelectedMeters, 1000)
}

function stopMeterPolling() {
    if (meterPollTimer !== null) clearInterval(meterPollTimer);
    meterPollTimer = null;
    meterPollInFlight = false
}

async function refreshSelectedMeters() {
    if (!snapshot || !radioId || meterPollInFlight) return;
    meterPollInFlight = true;
    try {
        const mode = snapshot.state.mode;
        for (const x of selectedMeters(snapshot.capabilities)) {
            const applicable = modeAllows(x, 'read', mode) && (!x.requiresTransmit || snapshot.state.isTransmitting);
            const frontValue = $(`front-meter-value-${x.id}`), frontFill = $(`front-meter-fill-${x.id}`);
            if (!applicable) {
                if (frontValue) frontValue.textContent = x.requiresTransmit ? 'TX only' : '—';
                if (frontFill) frontFill.style.width = '0%';
                continue
            }
            try {
                const v = await api(`/radios/${radioId}/meters/${x.id}/read`, { method: 'POST' });
                const e = $(`meter-value-${x.id}`), f = $(`meter-fill-${x.id}`);
                if (e) e.textContent = `${v.rawValue} ${x.rawUnit}`;
                if (frontValue) frontValue.textContent = `${v.rawValue} ${x.rawUnit}`;
                const width = `${Math.max(0, Math.min(100, v.normalizedValue * 100))}%`;
                if (f) f.style.width = width;
                if (frontFill) frontFill.style.width = width
            } catch (error) { log(`${x.displayName}: ${error.message}`, 'WARN') }
        }
    } finally { meterPollInFlight = false }
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
    stopMeterPolling();
    pttLeaseActive = false;
    controlValues.clear();
    choiceValues.clear();
    for (const writer of frequencyWriters.values())
        if (writer.timer !== null) clearTimeout(writer.timer);
    frequencyWriters.clear();
    for (const timer of controlWriteTimers.values()) clearTimeout(timer);
    controlWriteTimers.clear();
    $('radio-title').textContent = 'No radio connected';
    $('radio-summary').textContent = 'Capability-driven controls appear after connection.';
    $('connection-badge').textContent = 'OFFLINE';
    $('connection-badge').className = 'badge offline';
    $('ptt').textContent = 'PTT OFF';
    $('ptt').className = 'ptt';
    $('ptt').disabled = true;
    $('rx-state').textContent = 'RX';
    $('rx-state').classList.remove('transmitting');
    $('tuning-step').textContent = '—';
    $('tuning-dial').dataset.vfo = '';
    $('tuning-dial').setAttribute('aria-disabled', 'true');
    $('tuning-dial').tabIndex = -1;
    $('dial-motion').style.transform = '';
    $('front-meters').className = 'front-meters empty-state';
    $('front-meters').textContent = 'Advertised meters appear after connection.';
    ['mode-open', 'direct-open', 'split-soft', 'more-open'].forEach(id => {
        $(id).disabled = true;
        $(id).classList.remove('active')
    });
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
$('sidebar-toggle').onclick = () => toggleViewPreference('rig2castHideSidebar', 'hide-sidebar');
$('panels-toggle').onclick = () => toggleViewPreference('rig2castHideBottomPanels', 'hide-bottom-panels');
const frequencyDialog = $('frequency-dialog');
$('direct-open').onclick = () => {
    if (!snapshot) return;
    directEntryVfo = snapshot.state.activeVfo;
    const hz = snapshot.state.frequenciesHz[directEntryVfo] ?? snapshot.state.vfos?.[directEntryVfo]?.frequencyHz;
    $('frequency-dialog-title').textContent = `Set VFO ${directEntryVfo}`;
    $('direct-frequency').value = hz ?? '';
    frequencyDialog.showModal();
    $('direct-frequency').focus();
    $('direct-frequency').select()
};
$('direct-apply').onclick = event => {
    event.preventDefault();
    const value = Number($('direct-frequency').value), vfo = directEntryVfo;
    if (!vfo || !Number.isFinite(value)) return;
    if (!frequencyInRemoteRange(value)) {
        status('Frequency blocked', 'The requested frequency is outside the ranges assigned to this remote station.', 'error');
        return
    }
    frequencyDialog.close();
    run(async () => {
        snapshot = await api(`/radios/${radioId}/frequency/${vfo}`, {
            method: 'PUT', body: JSON.stringify({ value })
        });
        renderSnapshot(false)
    }, `VFO ${vfo} frequency updated.`)
};
$('mode-open').onclick = () => $('mode-dialog').showModal();
$('more-modes-toggle').onclick = () => {
    const panel = $('more-modes');
    panel.hidden = !panel.hidden;
    $('more-modes-toggle').textContent = panel.hidden ? 'MORE MODES' : 'FEWER MODES'
};
$('split-soft').onclick = () => run(async () => {
    snapshot = await api(`/radios/${radioId}/split`, {
        method: 'PUT', body: JSON.stringify({ value: !snapshot.state.isSplit })
    });
    renderSnapshot(false)
}, 'Split state updated.');
$('more-open').onclick = () => $('more-dialog').showModal();
document.querySelectorAll('[data-close-dialog]').forEach(button => button.onclick = () => $(button.dataset.closeDialog).close());
$('advanced-controls-open').onclick = () => {
    $('more-dialog').close();
    document.querySelector('.tab[data-tab="controls"]').click()
};
const tuningDial = $('tuning-dial');
tuningDial.addEventListener('wheel', event => tuneFromDial(event, event.deltaY < 0 ? 1 : -1), { passive: false });
tuningDial.addEventListener('keydown', event => {
    if (!['ArrowUp', 'ArrowRight', 'ArrowDown', 'ArrowLeft'].includes(event.key)) return;
    tuneFromDial(event, event.key === 'ArrowUp' || event.key === 'ArrowRight' ? 1 : -1)
});
tuningDial.addEventListener('pointerdown', event => {
    const vfo = tuningDial.dataset.vfo;
    if (vfo) window.startTuningKnob(event, vfo)
});
$('spectrum-average').addEventListener('change', event => {
    const value = Math.max(1, Math.min(10, Number(event.target.value) || 1));
    event.target.value = String(value);
    localStorage.setItem('rig2castSpectrumAverage', String(value));
    resetSpectrumAverage();
    if (spectrumFrame) {
        spectrumDisplaySamples = averageSpectrumSamples(spectrumFrame);
        renderSpectrum()
    }
});
$('waterfall-toggle').addEventListener('click', () => {
    const visible = $('waterfall-wrap').hidden;
    localStorage.setItem('rig2castShowWaterfall', String(visible));
    setWaterfallVisible(visible)
});
function bindSpectrumTuning(canvas) {
    canvas.addEventListener('pointerdown', event => {
        if (!spectrumFrame) return;
        spectrumDragVfo = nearestSpectrumMarker(event.clientX, canvas) || spectrumFrame.activeVfo;
        if (!spectrumCanTune(spectrumDragVfo)) {
            status('Spectrum tuning unavailable', `VFO ${spectrumDragVfo} is not writable for this browser session.`, 'error');
            spectrumDragVfo = null;
            return
        }
        event.preventDefault();
        canvas.setPointerCapture(event.pointerId);
        canvas.classList.add('dragging');
        const frequency = spectrumFrequency(event.clientX, canvas);
        if (frequencyInRemoteRange(frequency)) queueFrequencyWrite(spectrumDragVfo, frequency)
    });
    canvas.addEventListener('pointermove', event => {
        if (!spectrumDragVfo || !spectrumFrame || !canvas.hasPointerCapture(event.pointerId)) return;
        event.preventDefault();
        const frequency = spectrumFrequency(event.clientX, canvas);
        if (frequencyInRemoteRange(frequency)) queueFrequencyWrite(spectrumDragVfo, frequency)
    });
    const end = event => {
        if (!canvas.hasPointerCapture(event.pointerId)) return;
        canvas.releasePointerCapture(event.pointerId);
        spectrumDragVfo = null;
        canvas.classList.remove('dragging')
    };
    canvas.addEventListener('pointerup', end);
    canvas.addEventListener('pointercancel', end)
}
bindSpectrumTuning($('spectrum-canvas'));
bindSpectrumTuning($('waterfall-canvas'));
window.addEventListener('resize', () => requestAnimationFrame(renderSpectrum));
$('clear-log').onclick = () => $('log').textContent = '';
document.querySelectorAll('.tab').forEach(t => t.onclick = () => {
    document.querySelectorAll('.tab,.tab-page').forEach(x => x.classList.remove('active'));
    t.classList.add('active');
    $(`tab-${t.dataset.tab}`).classList.add('active')
});
bootstrap().catch(e => status('Startup failed', e.message, 'error'));
