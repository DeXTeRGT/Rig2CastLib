const $ = id => document.getElementById(id);
let stationStatus = null,
    snapshot = null,
    ws = null,
    busy = false,
    radioId = null,
    isOwner = false,
    clientRole = 'Observer',
    pttLeaseActive = false,
    pttRenewalTimer = null,
    dualDialRotation = { A: 0, B: 0 },
    meterPollTimer = null,
    meterPollInFlight = false,
    remoteFrequencyRanges = [],
    presentation = null,
    spectrumSocket = null,
    spectrumReconnectTimer = null,
    spectrumFrame = null,
    spectrumDragVfo = null,
    spectrumDisplaySamples = null,
    spectrumAverageRows = [],
    spectrumAverageSums = null,
    spectrumAverageGeometry = null,
    radioPoweredOn = null,
    spectrumTurboGradient = null,
    spectrumTurboGradientHeight = 0,
    waterfallInitialized = false,
    waterfallLastRowAt = 0,
    rotatorStatuses = [],
    rotatorPollTimer = null,
    authenticationEnabled = false,
    currentUser = null,
    stationSession = null,
    stationSessionPollTimer = null,
    adminUsers = [],
    adminPermissionCatalog = [],
    adminSelectedUserId = null;
let confirmationResolve = null,
    confirmationReturnFocus = null;
const waterfallBuffer = document.createElement('canvas');
const turboPalette = (() => {
    const encoded = 'MBI7MRVCMhhKNBtRNR5YNiFfNyNlOCZsOSlyOix5Oy9/PDKFPDWLPTeRPjqWPz2cQEChQEOmQUWrQUiwQku1Q066Q1C+Q1PCRFbHRFjLRVvORV7SRWDWRWPZRmbdRmjgRmvjRm3mRnDoRnPrRnXtRnjwRnryRn30Rn/2RoL4RYT5RYf7RYn8RIz9Q479QpH+QZP+QJb+P5j+Ppv+PJ39O6D8OaL8OKX7Nqj5NKr4M6z2Ma/1L7HzLbTxK7bvKrntKLvrJr3pJcDmI8LkIcThIMbfHsncHcvaHM3XG8/UGtHSGdPPGNXMGNfKF9nHF9rEF9zCF96/GOC9GOG6GeO4GuS2G+W0HeexHuivIOmsIuupJOymJ+2jKe6gLO+dL/CaMvGXNfOUOPSRO/SNP/WKQvaHRveDSviATfl8Ufl5Vfp2WftyXftvYfxsZfxoaf1lbf1icf1fdP5ceP5ZfP5WgP5ThP5Qh/5Ni/5Ljv5Ikv5Glf5EmP5Cm/1Anv0+ofw9pPw7pvs6qfs5rPo3rvk3sfg2s/g1tvc1ufU0u/Q0vvM0wPIzw/Ezxe8zyO4zyu0zzes0z+o00eg01Oc11uU12OM12uI23eA239424dw349o35dg459c46NU46tM57NE57c8578058Ms68sg688Y69MQ69sI698A5+L45+bw5+bo4+rc3+7U3+7M2/LA1/K40/asz/aky/aYx/aMw/qEv/p4u/pst/pgs/ZUr/ZIp/Y8o/Ywn/Ikm/IYk+4Mj+4Ai+n0g+nof+Xce+HQc93Eb924a9msY9WgX9GUW82MV8mAU8V0T71oR7lgQ7VUP7FIO6lAN6U0N6EsM5kkL5UYK40QK4kIJ4EAI3j4I3TwH2zoH2TgG1zYG1jQF1DIF0jAF0C8Ezi0EyysDySkDxygDxSYCwyQCwCMCviECux8BuR4BthwBtBsBsRkBrhgBrBYBqRUBphQBoxIBoBEBnRABmg4Blw0BlAwBkQsBjgoBiwkBhwgBhAcBgQYCfQUCegQC';
    const binary = atob(encoded), bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes
})();
const frequencyWriters = new Map();
const controlWriters = new Map();
const activeControlPointers = new Map();
const frequencyWriteIntervalMs = 125;
const controlWriteIntervalMs = 125;
const controlValues = new Map(), choiceValues = new Map(), switchValues = new Map();
const clientId = sessionStorage.getItem('rig2castClientId') ||
    globalThis.crypto?.randomUUID?.() ||
    `client-${Date.now()}-${Math.random().toString(36).slice(2)}`;
sessionStorage.setItem('rig2castClientId', clientId);
const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;'
} [c]));
const access = (f, kind) => String(f?.access || '').toLowerCase().includes(kind);
const connected = () => !!snapshot;
function hasPermission(key, resourceId = null) {
    if (!authenticationEnabled) return true;
    const grants = currentUser?.permissions?.filter(item => item.key === key) || [];
    if (resourceId !== null) {
        const exact = grants.find(item => item.resourceId === resourceId);
        if (exact) return !!exact.allowed
    }
    return !!grants.find(item => item.resourceId == null)?.allowed
}

function hasAnyResourcePermission(key) {
    if (!authenticationEnabled) return true;
    return (currentUser?.permissions || []).some(item => item.key === key && item.allowed)
}

const cardPermissions = {
    vfo: ['radio.view', 'radio.control'],
    spectrum: ['spectrum.view', 'spectrum.control'],
    tuning: ['tuning.view', 'tuning.control'],
    band: ['band.view', 'band.control'],
    mode: ['mode.view', 'mode.control'],
    dsp: ['dsp.view', 'dsp.control'],
    receiver: ['receiver.view', 'receiver.control'],
    transmit: ['transmit.view', 'transmit.control']
};

function applyPermissionPresentation() {
    for (const [cardName, permissions] of Object.entries(cardPermissions)) {
        const card = document.querySelector(`[data-card="${cardName}"]`);
        if (!card) continue;
        const visible = hasPermission(permissions[0]);
        const writable = hasPermission(permissions[1]);
        card.hidden = !visible;
        card.classList.toggle('permission-readonly', visible && !writable);
        card.setAttribute('data-permission-mode', writable ? 'control' : 'view');
        if (visible && !writable)
            for (const element of card.querySelectorAll('button,input,select')) element.disabled = true
    }

    const canViewRotators = hasAnyResourcePermission('rotator.view');
    if ($('rotator-view-toggle')) $('rotator-view-toggle').hidden = !canViewRotators;
    if ($('radio-power-control') && authenticationEnabled)
        $('radio-power-control').hidden = !stationStatus?.station?.powerControlAvailable ||
            !(hasPermission('radio.power.on') || hasPermission('radio.power.off'));
    if ($('header-audio')) $('header-audio').hidden = !hasPermission('audio.view');
    if ($('station-connect') && authenticationEnabled)
        $('station-connect').hidden = !hasPermission('radio.connect');
    if ($('admin-users')) $('admin-users').hidden = !hasPermission('users.manage');
    if ($('spectrum-minimum')) $('spectrum-minimum').disabled = !hasPermission('spectrum.settings');
    if ($('spectrum-maximum')) $('spectrum-maximum').disabled = !hasPermission('spectrum.settings');
    if ($('spectrum-span') && !hasPermission('spectrum.settings')) $('spectrum-span').disabled = true;
}

function setAdminNotice(message = '', kind = 'error') {
    const error = $('admin-error'), success = $('admin-success');
    error.hidden = kind !== 'error' || !message;
    success.hidden = kind !== 'success' || !message;
    if (kind === 'error') error.textContent = message;
    else success.textContent = message
}

function permissionTitle(key) {
    return String(key).split('.').map(part => part.charAt(0).toUpperCase() + part.slice(1)).join(' · ')
}

function renderPermissionEditor(containerId, grants = []) {
    const container = $(containerId);
    const allowedGlobals = new Set(grants.filter(grant => grant.allowed && grant.resourceId == null).map(grant => grant.key));
    const areas = new Map();
    for (const permission of adminPermissionCatalog) {
        if (!areas.has(permission.area)) areas.set(permission.area, []);
        areas.get(permission.area).push(permission)
    }
    container.innerHTML = [...areas.entries()].map(([area, permissions]) => `
      <section class="admin-permission-group"><h3>${esc(area)}</h3>
        ${permissions.map(permission => `<label class="admin-permission"><input type="checkbox" data-permission-key="${esc(permission.key)}" ${allowedGlobals.has(permission.key) ? 'checked' : ''}><span><strong>${esc(permissionTitle(permission.key))}</strong><small>${esc(permission.description)}${permission.supportsResource ? ' May also be scoped to one rotator.' : ''}</small></span></label>`).join('')}
      </section>`).join('')
}

function readGlobalPermissionEditor(containerId) {
    return [...$(containerId).querySelectorAll('[data-permission-key]:checked')]
        .map(input => ({ key: input.dataset.permissionKey, resourceId: null, allowed: true }))
}

function selectedAdminUser() {
    return adminUsers.find(user => user.id === adminSelectedUserId) || null
}

function renderAdminUserList() {
    $('admin-user-items').innerHTML = adminUsers.map(user => `<button type="button" class="admin-user-item ${user.id === adminSelectedUserId ? 'selected' : ''} ${user.enabled ? '' : 'disabled'}" data-admin-user-id="${esc(user.id)}"><strong>${esc(user.displayName || user.username)}</strong><small>@${esc(user.username)}${user.id === currentUser?.id ? ' · YOU' : ''}</small></button>`).join('');
    for (const button of $('admin-user-items').querySelectorAll('[data-admin-user-id]'))
        button.onclick = () => selectAdminUser(button.dataset.adminUserId)
}

function selectAdminUser(userId) {
    adminSelectedUserId = userId;
    const user = selectedAdminUser();
    $('admin-create-form').hidden = true;
    $('admin-user-empty').hidden = !!user;
    $('admin-user-form').hidden = !user;
    renderAdminUserList();
    if (!user) return;
    $('admin-user-display-name').textContent = user.displayName || user.username;
    $('admin-user-username').textContent = `@${user.username}${user.mustChangePassword ? ' · password change required' : ''}`;
    $('admin-user-enabled').checked = !!user.enabled;
    $('admin-user-enabled').disabled = user.id === currentUser?.id;
    $('admin-user-state').textContent = user.enabled ? 'ENABLED' : 'DISABLED';
    $('admin-user-state').classList.toggle('disabled', !user.enabled);
    $('admin-delete-user').disabled = user.id === currentUser?.id;
    renderPermissionEditor('admin-user-permissions', user.permissions || [])
}

function showAdminCreate() {
    adminSelectedUserId = null;
    renderAdminUserList();
    $('admin-user-empty').hidden = true;
    $('admin-user-form').hidden = true;
    $('admin-create-form').hidden = false;
    renderPermissionEditor('admin-create-permissions');
    requestAnimationFrame(() => $('admin-create-username').focus())
}

async function refreshAdminUsers(preferredUserId = adminSelectedUserId) {
    adminUsers = await api('/admin/users');
    const nextId = adminUsers.some(user => user.id === preferredUserId)
        ? preferredUserId
        : adminUsers[0]?.id || null;
    selectAdminUser(nextId)
}

async function openAdminUsers() {
    if (!hasPermission('users.manage')) return;
    setAdminNotice();
    $('admin-modal').hidden = false;
    document.body.classList.add('modal-open');
    try {
        [adminPermissionCatalog, adminUsers] = await Promise.all([api('/admin/permissions'), api('/admin/users')]);
        selectAdminUser(adminUsers[0]?.id || null)
    } catch (error) {
        setAdminNotice(error.message)
    }
}

function closeAdminUsers() {
    $('admin-modal').hidden = true;
    document.body.classList.remove('modal-open');
    setAdminNotice();
    $('admin-users').focus()
}

async function submitAdminCreate(event) {
    event.preventDefault();
    const password = $('admin-create-password').value;
    if (password !== $('admin-create-password-confirm').value) {
        setAdminNotice('The passwords do not match.');
        return
    }
    await run(async () => {
        const created = await api('/admin/users', { method: 'POST', body: JSON.stringify({
            username: $('admin-create-username').value.trim(),
            displayName: $('admin-create-display-name').value.trim(),
            password,
            mustChangePassword: $('admin-create-must-change').checked,
            permissions: readGlobalPermissionEditor('admin-create-permissions')
        }) });
        $('admin-create-form').reset();
        $('admin-create-must-change').checked = true;
        await refreshAdminUsers(created.id);
        setAdminNotice(`User ${created.username} was created.`, 'success')
    })
}

async function submitAdminUser(event) {
    event.preventDefault();
    const user = selectedAdminUser();
    if (!user) return;
    const globalPermissions = readGlobalPermissionEditor('admin-user-permissions');
    const resourcePermissions = (user.permissions || []).filter(grant => grant.resourceId != null);
    await run(async () => {
        if (user.enabled !== $('admin-user-enabled').checked)
            await api(`/admin/users/${encodeURIComponent(user.id)}/enabled`, { method: 'PUT', body: JSON.stringify({ enabled: $('admin-user-enabled').checked }) });
        await api(`/admin/users/${encodeURIComponent(user.id)}/permissions`, { method: 'PUT', body: JSON.stringify({ permissions: [...globalPermissions, ...resourcePermissions] }) });
        if (user.id === currentUser?.id) {
            currentUser = await api('/auth/me');
            applyPermissionPresentation()
        }
        await refreshAdminUsers(user.id);
        setAdminNotice(`Account ${user.username} was updated.`, 'success')
    })
}

async function resetAdminPassword() {
    const user = selectedAdminUser();
    if (!user) return;
    const password = $('admin-reset-password').value;
    if (password.length < 12) { setAdminNotice('The temporary password must contain at least 12 characters.'); return }
    if (password !== $('admin-reset-confirm').value) { setAdminNotice('The reset passwords do not match.'); return }
    const confirmed = await requestConfirmation({ title: `Reset ${user.username}'s password?`, message: 'Their current password will no longer be accepted for new sign-ins.', confirmLabel: 'RESET PASSWORD' });
    if (!confirmed) return;
    await run(async () => {
        await api(`/admin/users/${encodeURIComponent(user.id)}/password`, { method: 'PUT', body: JSON.stringify({ password, mustChangePassword: $('admin-reset-must-change').checked }) });
        $('admin-reset-password').value = '';
        $('admin-reset-confirm').value = '';
        await refreshAdminUsers(user.id);
        setAdminNotice(`Password reset for ${user.username}.`, 'success')
    })
}

async function deleteAdminUser() {
    const user = selectedAdminUser();
    if (!user || user.id === currentUser?.id) return;
    const confirmed = await requestConfirmation({ title: `Delete ${user.username}?`, message: 'This permanently removes the account, its permissions, and all active sessions.', confirmLabel: 'DELETE USER' });
    if (!confirmed) return;
    await run(async () => {
        await api(`/admin/users/${encodeURIComponent(user.id)}`, { method: 'DELETE' });
        await refreshAdminUsers(null);
        setAdminNotice(`User ${user.username} was deleted.`, 'success')
    })
}
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

async function initializeAuthentication() {
    const statusResponse = await fetch('/api/v1/auth/status');
    if (!statusResponse.ok) throw new Error('Unable to read station authentication status.');
    const authentication = await statusResponse.json();
    authenticationEnabled = !!authentication.enabled;
    if (!authenticationEnabled) return true;

    const response = await fetch('/api/v1/auth/me');
    if (response.ok) {
        currentUser = await response.json();
        $('session-logout').hidden = false;
        if (currentUser.mustChangePassword) {
            showPasswordChange();
            return false
        }
        applyPermissionPresentation();
        return true
    }
    showLogin();
    return false
}

function showLogin(message = '') {
    $('login-error').textContent = message;
    $('login-error').hidden = !message;
    $('login-modal').hidden = false;
    document.body.classList.add('modal-open');
    requestAnimationFrame(() => $('login-username').focus())
}

function hideLogin() {
    $('login-modal').hidden = true;
    document.body.classList.remove('modal-open');
    $('login-password').value = ''
}

function showPasswordChange(message = '') {
    $('password-change-error').textContent = message;
    $('password-change-error').hidden = !message;
    $('password-change-modal').hidden = false;
    document.body.classList.add('modal-open');
    requestAnimationFrame(() => $('password-current').focus())
}

function hidePasswordChange() {
    $('password-change-modal').hidden = true;
    document.body.classList.remove('modal-open');
    $('password-current').value = '';
    $('password-new').value = '';
    $('password-confirm').value = ''
}

async function submitLogin(event) {
    event.preventDefault();
    const button = $('login-submit');
    button.disabled = true;
    $('login-error').hidden = true;
    try {
        const response = await fetch('/api/v1/auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                username: $('login-username').value,
                password: $('login-password').value
            })
        });
        if (!response.ok) throw new Error(response.status === 401
            ? 'The username or password is incorrect.'
            : 'Sign-in failed. Please try again.');
        currentUser = await response.json();
        $('session-logout').hidden = false;
        hideLogin();
        if (currentUser.mustChangePassword) {
            showPasswordChange();
            return
        }
        applyPermissionPresentation();
        await bootstrap()
    } catch (error) {
        showLogin(error.message)
    } finally {
        button.disabled = false
    }
}

async function submitPasswordChange(event) {
    event.preventDefault();
    const button = $('password-change-submit');
    const newPassword = $('password-new').value;
    if (newPassword !== $('password-confirm').value) {
        showPasswordChange('The new passwords do not match.');
        return
    }
    button.disabled = true;
    try {
        const response = await fetch('/api/v1/auth/change-password', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                currentPassword: $('password-current').value,
                newPassword
            })
        });
        let responseBody = null;
        try { responseBody = await response.json() } catch {}
        if (!response.ok) throw new Error(response.status === 401
            ? 'The current password is incorrect.'
            : responseBody?.error || 'The password could not be changed.');
        currentUser = responseBody;
        hidePasswordChange();
        applyPermissionPresentation();
        await bootstrap()
    } catch (error) {
        showPasswordChange(error.message)
    } finally {
        button.disabled = false
    }
}

async function logout() {
    if (connected()) {
        try { await disconnect() } catch { }
    }
    if (stationSessionPollTimer !== null) clearInterval(stationSessionPollTimer);
    try { await fetch('/api/v1/auth/logout', { method: 'POST' }) } catch {}
    location.reload()
}

function log(message, type = 'INFO') {
    const method = type === 'ERROR' ? 'error' : type === 'WARN' ? 'warn' : 'info';
    console[method](`[Rig2Cast] ${message}`)
}

function status(title, text, kind = '') {
    const message = $('operator-message');
    message.hidden = kind !== 'error' && kind !== 'notice';
    message.className = `operator-message ${kind}`;
    message.textContent = text ? `${title}: ${text}` : title
}

function updateHeaderControls() {
    const attached = connected();
    const connectButton = $('station-connect');
    const occupiedByOther = !attached && stationSession && !stationSession.available && !stationSession.ownedByCurrentUser;
    connectButton.textContent = attached
        ? 'CONNECTED'
        : occupiedByOther
            ? stationSession.canTakeOver ? 'TAKE CONTROL' : 'STATION IN USE'
            : 'CONNECT RADIO';
    connectButton.classList.toggle('connected', attached);
    connectButton.classList.toggle('disconnected', !attached);
    connectButton.setAttribute('aria-label', attached ? 'Disconnect radio' : occupiedByOther ? `Station in use by ${stationSession.ownerDisplayName}` : 'Connect radio');
    connectButton.title = attached
        ? 'Disconnect radio'
        : occupiedByOther
            ? `Station in use by ${stationSession.ownerDisplayName}`
            : radioPoweredOn === false ? 'Power on the physical radio before connecting' : 'Connect radio';
    connectButton.disabled = busy || (!attached && radioPoweredOn === false) ||
        (occupiedByOther && !stationSession.canTakeOver);

    const powerButton = $('radio-power-control');
    powerButton.disabled = busy ||
        (radioPoweredOn === true && !hasPermission('radio.power.off')) ||
        (radioPoweredOn === false && !hasPermission('radio.power.on'));
    $('header-audio').disabled = busy || !attached
}

function closeConfirmation(result) {
    if (!confirmationResolve) return;
    const resolve = confirmationResolve;
    const returnFocus = confirmationReturnFocus;
    confirmationResolve = null;
    confirmationReturnFocus = null;
    $('confirmation-modal').hidden = true;
    if ($('admin-modal').hidden) document.body.classList.remove('modal-open');
    resolve(result);
    if (returnFocus?.isConnected) returnFocus.focus()
}

function requestConfirmation({ title, message, confirmLabel, tone = 'danger' }) {
    if (confirmationResolve) closeConfirmation(false);
    confirmationReturnFocus = document.activeElement;
    $('confirmation-title').textContent = title;
    $('confirmation-message').textContent = message;
    $('confirmation-accept').textContent = confirmLabel;
    $('confirmation-dialog').className = `confirmation-dialog ${tone}`;
    $('confirmation-modal').hidden = false;
    document.body.classList.add('modal-open');
    return new Promise(resolve => {
        confirmationResolve = resolve;
        requestAnimationFrame(() => $('confirmation-cancel').focus())
    })
}

function setBusy(value) {
    busy = value;
    document.body.classList.toggle('busy', value);
    updateHeaderControls();
    if ($('operator-ptt') && snapshot) $('operator-ptt').disabled = value || !canControlPtt()
}
async function run(action, label) {
    if (busy) return;
    setBusy(true);
    try {
        await action();
        if (label) log(label)
    } catch (e) {
        status('Operation failed', e.message, 'error');
        if (!$('admin-modal').hidden) setAdminNotice(e.message);
        log(e.message, 'ERROR')
    } finally {
        setBusy(false)
    }
}
async function bootstrap() {
    stationStatus = await api('/status');
    if (!stationStatus.station?.locked)
        throw new Error('The operator console requires a configured station.json.');
    $('station-title').textContent = stationStatus.station.displayName || 'Remote Station';
    remoteFrequencyRanges = stationStatus.station.remoteFrequencyRanges || [];
    presentation = stationStatus.station.presentation || null;
    await refreshStationSession();
    if (stationSessionPollTimer !== null) clearInterval(stationSessionPollTimer);
    stationSessionPollTimer = setInterval(() => refreshStationSession().catch(error =>
        log(`Station session status: ${error.message}`, 'WARN')), 3000);
    globalThis.rig2castAudio?.configure({
        allowMicrophone: stationStatus.station.audioAllowMicrophone && hasPermission('audio.transmit')
    });
    renderOffline();
    if (hasPermission('spectrum.view')) await initializeSpectrumLevels();
    if (hasPermission('radio.view')) await refreshRadioPowerState();
    await initializeRotators(!!presentation?.showRotatorGui && hasAnyResourcePermission('rotator.view'));
    applyPermissionPresentation();
}

async function refreshStationSession() {
    stationSession = await api('/station-session');
    updateHeaderControls();
    if (!connected() && !stationSession.available && !stationSession.ownedByCurrentUser) {
        status('Station in use', `${stationSession.ownerDisplayName} currently has the active station connection.`, 'notice')
    } else if ($('operator-message').classList.contains('notice')) {
        status('', '')
    }
    return stationSession
}

async function initializeSpectrumLevels() {
    const levels = await api('/spectrum/levels');
    $('spectrum-minimum').value = levels.minimumLevel;
    $('spectrum-maximum').value = levels.maximumLevel
}

async function updateSpectrumLevels() {
    const minimumLevel = Number($('spectrum-minimum').value);
    const maximumLevel = Number($('spectrum-maximum').value);
    if (!Number.isInteger(minimumLevel) || !Number.isInteger(maximumLevel) || minimumLevel < 0 || maximumLevel > 255 || minimumLevel >= maximumLevel)
        throw new Error('Spectrum levels require 0 <= minimum < maximum <= 255.');
    await api('/spectrum/levels', { method: 'PUT', body: JSON.stringify({ minimumLevel, maximumLevel }) })
}

async function refreshRadioPowerState() {
    const button = $('radio-power-control');
    const available = !!stationStatus?.station?.powerControlAvailable;
    button.hidden = !available;
    if (!available) return;
    try {
        const state = await api('/radio-power');
        radioPoweredOn = !!state.poweredOn;
        button.textContent = radioPoweredOn ? 'POWERED ON' : 'POWERED OFF';
        button.classList.toggle('powered-on', radioPoweredOn);
        button.classList.toggle('powered-off', !radioPoweredOn);
        button.classList.remove('power-unknown');
        button.setAttribute('aria-label', radioPoweredOn ? 'Power off physical radio' : 'Power on physical radio');
        button.title = radioPoweredOn ? 'Power off physical radio' : 'Power on physical radio'
    } catch (error) {
        radioPoweredOn = null;
        button.textContent = 'POWER UNKNOWN';
        button.classList.remove('powered-on', 'powered-off');
        button.classList.add('power-unknown');
        button.setAttribute('aria-label', 'Read radio power state');
        button.title = 'Read radio power state';
        log(`Power state: ${error.message}`, 'WARN')
    }
    updateHeaderControls()
}

function openSpectrumSocket() {
    if (!connected()) return;
    if (spectrumReconnectTimer !== null) {
        clearTimeout(spectrumReconnectTimer);
        spectrumReconnectTimer = null
    }
    if (spectrumSocket) {
        spectrumSocket.onclose = null;
        spectrumSocket.close()
    }
    const protocol = location.protocol === 'https:' ? 'wss' : 'ws';
    spectrumSocket = new WebSocket(`${protocol}://${location.host}/api/v1/spectrum/stream?clientId=${encodeURIComponent(clientId)}`);
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
        if (connected()) spectrumReconnectTimer = setTimeout(() => {
            spectrumReconnectTimer = null;
            if (connected()) openSpectrumSocket()
        }, 2000)
    }
}

function closeSpectrumSocket() {
    if (spectrumReconnectTimer !== null) clearTimeout(spectrumReconnectTimer);
    spectrumReconnectTimer = null;
    if (spectrumSocket) {
        spectrumSocket.onclose = null;
        spectrumSocket.close();
        spectrumSocket = null
    }
    spectrumFrame = null;
    spectrumDisplaySamples = null;
    spectrumDragVfo = null;
    resetSpectrumAverage();
    for (const id of ['spectrum-canvas', 'waterfall-canvas']) {
        const canvas = $(id), context = canvas.getContext('2d');
        context.clearRect(0, 0, canvas.width, canvas.height)
    }
    $('spectrum-state').textContent = 'OFFLINE';
    $('spectrum-state').className = 'badge offline';
    $('spectrum-range').textContent = 'Waiting for station connection';
    $('spectrum-vfo-a').textContent = 'VFO A —';
    $('spectrum-vfo-b').textContent = 'VFO B —'
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
function connectBody() {
    return {
        clientId,
        modelId: '',
        transport: '',
        settings: {},
        enableWrites: !!stationStatus?.serverAllowsWrites,
        modeRestrictions: 'Enforce'
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
    renderSnapshot();
    openSocket();
    openSpectrumSocket();
    await refreshAll();
    await initializeTuningStep();
    await initializeSpectrumSpan();
    await refreshRadioPowerState();
    startMeterPolling();
    await refreshStationSession();
    status(result.readOnly ? 'Attached read-only' : 'Connected as operator', result.message, 'online');
    log(`${result.message} Radio ID: ${radioId}.`)
}
async function disconnect() {
    globalThis.rig2castAudio?.stop();
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
    closeSpectrumSocket();
    const action = isOwner ? 'Radio closed.' : 'Observer detached.';
    snapshot = null;
    radioId = null;
    isOwner = false;
    clientRole = 'Observer';
    pttLeaseActive = false;
    renderOffline();
    await refreshStationSession();
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
    ws.onclose = async () => {
        ws = null;
        if (!connected()) return;
        try {
            await refreshStationSession();
            if (!stationSession.ownedByCurrentUser) {
                loseStationSession('Your active station session was ended by an administrator.');
                return
            }
        } catch {}
        if (connected()) setTimeout(openSocket, 1500)
    }
}

function loseStationSession(message) {
    globalThis.rig2castAudio?.stop();
    stopPttRenewal();
    stopMeterPolling();
    closeSpectrumSocket();
    snapshot = null;
    radioId = null;
    isOwner = false;
    clientRole = 'Observer';
    pttLeaseActive = false;
    renderOffline();
    status('Station disconnected', message, 'error')
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
    updateHeaderControls();
    renderPtt(c, s);
    renderTransmit(c, s, writes);
    renderOperatorVfo(c, s, writes);
    renderDualTuning(c, s, writes);
    renderBandMode(c, s, writes);
    renderDsp(c, s, writes);
    renderReceiver(c, s, writes);
    applyModeAvailability(c, s.mode, writes);
    setBusy(false);
    applyPermissionPresentation()
}

function isPresented(kind, id) {
    const configured = presentation?.[kind];
    return !Array.isArray(configured) || configured.some(value => String(value).toLowerCase() === String(id).toLowerCase())
}

function compactFrequency(value) {
    if (!value) return '--.---.---';
    return Math.trunc(value).toLocaleString('de-DE').replace(/,/g, '.')
}

function renderOperatorVfo(c, s, writes) {
    const active = String(s.activeVfo || 'A');
    const secondary = active.toLowerCase() === 'a' ? 'B' : 'A';
    const activeHz = s.frequenciesHz?.[active] ?? s.vfos?.[active]?.frequencyHz;
    const secondaryHz = s.frequenciesHz?.[secondary] ?? s.vfos?.[secondary]?.frequencyHz;
    $('operator-radio-state').textContent = s.isTransmitting ? 'TRANSMIT' : 'RECEIVE';
    $('operator-rx-flag').textContent = s.isTransmitting ? 'TX' : 'RX';
    $('operator-rx-flag').classList.toggle('tx', s.isTransmitting);
    $('operator-active-vfo').textContent = `VFO ${active}`;
    $('operator-active-vfo').disabled = !(writes && access(c.vfos.selection, 'write'));
    $('operator-active-vfo').onclick = () => window.selectVfo(secondary);
    $('operator-mode').textContent = operatorModeLabel(s.vfos?.[active]?.mode ?? s.mode);
    $('operator-split').textContent = `SPLIT ${s.isSplit ? 'ON' : 'OFF'}`;
    $('operator-split').classList.toggle('active', s.isSplit);
    $('operator-split').disabled = !(writes && access(c.vfos.split, 'write'));
    $('operator-split').onclick = () => setOperatorSplit(!s.isSplit);
    $('operator-frequency').textContent = compactFrequency(activeHz);
    $('operator-secondary-name').textContent = `VFO ${secondary}`;
    $('operator-secondary-frequency').textContent = compactFrequency(secondaryHz);
    const actions = Object.values(c.actions || {}).filter(action => isPresented('actions', action.id));
    $('operator-vfo-actions').innerHTML = actions.map(action => `<button ${writes && access(action.feature, 'write') ? '' : 'disabled'} onclick="executeRadioAction('${esc(action.id)}')">${action.id === 'CopyVfoAToB' ? 'A → B' : action.id === 'CopyVfoBToA' ? 'B → A' : 'SWAP'}</button>`).join('')
}

window.setOperatorSplit = enabled => run(async () => {
    snapshot = await api(`/radios/${radioId}/split`, { method: 'PUT', body: JSON.stringify({ value: enabled }) });
    renderSnapshot(false)
}, `Split ${enabled ? 'enabled' : 'disabled'}.`);

function renderDualTuning(c, s, writes) {
    const stepChoice = findChoice(c, 'TuningStep');
    const stepButtons = $('operator-tuning-step');
    if (stepChoice && isPresented('choices', stepChoice.id)) {
        const key = String(stepChoice.id).toLowerCase();
        const options = tuningStepOptions(c, s.mode);
        let selected = choiceValues.get(key);
        if (!options.some(option => String(option.value) === String(selected))) {
            selected = options.find(option => parseStepHz(option.value) === 10)?.value ?? options[0]?.value;
            if (selected !== undefined) choiceValues.set(key, selected)
        }
        const writable = writes && access(stepChoice.feature, 'write');
        stepButtons.innerHTML = options.map(option => {
            const label = stepOptionLabel(option.value);
            const active = String(option.value) === String(selected);
            return `<button type="button" class="${active ? 'active' : ''}" aria-pressed="${active}" ${writable ? '' : 'disabled'} onclick="setTuningStep('${esc(stepChoice.id)}','${esc(option.value)}','${esc(label)}')">${label}</button>`
        }).join('')
    } else {
        stepButtons.innerHTML = `<button type="button" class="active" aria-pressed="true" disabled>${formatStep(selectedTuningStep(c))}</button>`
    }
    for (const requested of ['A', 'B']) {
        const vfo = c.vfos.available.find(value => String(value).toLowerCase() === requested.toLowerCase());
        const dial = $(`operator-tuning-dial-${requested}`);
        const frequency = vfo ? s.frequenciesHz?.[vfo] ?? s.vfos?.[vfo]?.frequencyHz : null;
        const writable = !!vfo && writes && access(c.frequency.feature, 'write') && c.frequency.targets.some(value => String(value).toLowerCase() === String(vfo).toLowerCase());
        dial.dataset.vfo = vfo || requested;
        dial.setAttribute('aria-disabled', String(!writable));
        dial.tabIndex = writable ? 0 : -1;
        $(`operator-tuning-frequency-${requested}`).textContent = compactFrequency(frequency);
        const state = $(`operator-tuning-state-${requested}`);
        const isRx = String(s.activeVfo).toLowerCase() === requested.toLowerCase();
        const isTx = s.isSplit && String(s.transmitVfo).toLowerCase() === requested.toLowerCase();
        state.textContent = !writable ? 'READ ONLY' : [isRx ? 'RX' : 'STANDBY', isTx ? 'TX' : ''].filter(Boolean).join(' · ');
        state.className = isTx ? 'tx' : isRx ? 'active' : ''
    }
}

function isPresentedMode(mode) {
    const configured = presentation?.modes;
    return !Array.isArray(configured) || configured.some(value => String(value).toLowerCase() === String(mode).toLowerCase())
}

function operatorModeLabel(mode) {
    const labels = { lsb: 'LSB', usb: 'USB', cw: 'CWU', cwreverse: 'CWR' };
    return labels[String(mode).toLowerCase()] || String(mode).toUpperCase()
}

function inferredBand(options, frequencyHz) {
    if (!Number.isFinite(frequencyHz)) return null;
    const mhz = frequencyHz / 1000000;
    return options.map(option => ({ option, nominal: Number.parseFloat(option.value) }))
        .filter(item => Number.isFinite(item.nominal))
        .sort((left, right) => Math.abs(left.nominal - mhz) - Math.abs(right.nominal - mhz))[0]?.option.value ?? null
}

function renderBandMode(c, s, writes) {
    const band = findChoice(c, 'Band');
    const bandOptions = band && isPresented('choices', band.id) ? Object.values(band.options) : [];
    const activeFrequency = s.frequenciesHz?.[s.activeVfo] ?? s.vfos?.[s.activeVfo]?.frequencyHz;
    const selectedBand = inferredBand(bandOptions, activeFrequency);
    $('operator-band-buttons').innerHTML = bandOptions.length
        ? bandOptions.map(option => `<button class="${String(option.value) === String(selectedBand) ? 'active' : ''}" ${writes && access(band.feature, 'write') ? '' : 'disabled'} onclick="setBand('${esc(band.id)}','${esc(option.value)}')">${esc(option.displayName)}</button>`).join('')
        : '<span class="empty-state">Band selection is not advertised.</span>';
    const modes = (c.modes.values || []).filter(isPresentedMode);
    $('operator-mode-buttons').innerHTML = modes.length
        ? modes.map(mode => `<button class="${String(mode).toLowerCase() === String(s.mode).toLowerCase() ? 'active' : ''}" ${writes && access(c.modes.feature, 'write') ? '' : 'disabled'} onclick="setMode('${esc(mode)}')">${operatorModeLabel(mode)}</button>`).join('')
        : '<span class="empty-state">No approved modes are advertised.</span>'
}

window.setBand = (id, value) => run(async () => {
    await api(`/radios/${radioId}/choices/${id}`, { method: 'PUT', body: JSON.stringify({ value }) });
    choiceValues.set(String(id).toLowerCase(), value);
    snapshot = await api(`/radios/${radioId}/snapshot`);
    renderSnapshot(false)
}, `Band changed to ${value}.`);

function receiverLabel(id, displayName) {
    const labels = { noiseblanker: 'NB', noisereduction: 'DNR', autonotch: 'NOTCH', attenuator: 'ATT', preamp: 'FRONT END', agc: 'AGC', roofingfilter: 'ROOFING FILTER', filterwidth: 'FILTER WIDTH', ifshifthz: 'IF SHIFT' };
    return labels[String(id).toLowerCase()] || String(displayName).toUpperCase()
}

function renderDsp(c, s, writes) {
    const dspIds = new Set(['noiseblanker', 'noisereduction', 'autonotch']);
    const switches = Object.values(c.switches || {}).filter(item => dspIds.has(String(item.id).toLowerCase()) && isPresented('switches', item.id));
    $('operator-dsp-switches').innerHTML = switches.length ? switches.map(item => {
        const enabled = switchValues.get(String(item.id).toLowerCase()) === true;
        const writable = writes && access(item.feature, 'write') && modeAllows(item, 'write', s.mode);
        return `<button class="${enabled ? 'active' : ''}" ${writable ? '' : 'disabled'} onclick="setReceiverSwitch('${esc(item.id)}',${!enabled})">${receiverLabel(item.id, item.displayName)}</button>`
    }).join('') : '<span class="empty-state">No approved DSP switches are advertised.</span>'
}

function renderReceiver(c, s, writes) {
    if ([...activeControlPointers.values()].some(id => ['afgain', 'rfgain', 'ifshifthz'].includes(String(id).toLowerCase()))) return;
    const choiceOrder = ['Attenuator', 'Preamp', 'RoofingFilter', 'Agc', 'FilterWidth'];
    const choices = choiceOrder.map(id => findChoice(c, id)).filter(item => item && isPresented('choices', item.id));
    $('operator-receiver-choices').innerHTML = choices.map(item => {
        const selected = choiceValues.get(String(item.id).toLowerCase());
        const options = Object.values(item.options).filter(option => !option.applicableModes || option.applicableModes.some(mode => String(mode).toLowerCase() === String(s.mode).toLowerCase()));
        const writable = writes && access(item.feature, 'write') && modeAllows(item, 'write', s.mode);
        if (['attenuator', 'preamp', 'roofingfilter'].includes(String(item.id).toLowerCase()))
            return `<div class="operator-receiver-choice-buttons"><span>${receiverLabel(item.id, item.displayName)}</span><div>${options.map(option => `<button class="${String(option.value) === String(selected) ? 'active' : ''}" ${writable && option.writable !== false ? '' : 'disabled'} onclick="setReceiverChoice('${esc(item.id)}','${esc(option.value)}')">${esc(option.displayName)}</button>`).join('')}</div></div>`;
        return `<label>${receiverLabel(item.id, item.displayName)}<select ${writable ? '' : 'disabled'} onchange="setReceiverChoice('${esc(item.id)}',this.value)">${options.map(option => `<option value="${esc(option.value)}" ${String(option.value) === String(selected) ? 'selected' : ''}>${esc(option.displayName)}</option>`).join('')}</select></label>`
    }).join('');
    const levelOrder = ['AfGain', 'RfGain', 'IfShiftHz'];
    const levels = levelOrder.map(id => Object.values(c.controls || {}).find(item => String(item.id).toLowerCase() === id.toLowerCase())).filter(item => item && isPresented('controls', item.id));
    $('operator-receiver-levels').innerHTML = levels.map(item => {
        const value = controlValues.get(String(item.id).toLowerCase()) ?? item.minimum;
        const writable = writes && access(item.feature, 'write') && modeAllows(item, 'write', s.mode);
        const slider = sliderPresentation(item, value);
        return `<label><span class="operator-receiver-level-head"><span>${receiverLabel(item.id, item.displayName)}</span><strong id="operator-level-value-${item.id}">${levelValue(item, value)}</strong></span><input id="operator-level-input-${item.id}" class="${slider.className}" style="${slider.style}" type="range" min="${item.minimum}" max="${item.maximum}" step="${item.step}" value="${value}" ${writable ? '' : 'disabled'} onpointerdown="beginControlDrag(event,'${esc(item.id)}')" oninput="queueOperatorControl('${esc(item.id)}',Number(this.value))"></label>`
    }).join('')
}

function levelValue(item, value) {
    if (['transmitpower', 'ifshifthz'].includes(String(item.id).toLowerCase())) return `${value} ${item.unit}`;
    const ratio = item.maximum === item.minimum ? 0 : (value - item.minimum) / (item.maximum - item.minimum);
    return `${Math.round(Math.max(0, Math.min(1, ratio)) * 100)}%`
}

function sliderPresentation(item, value) {
    if (!(item.minimum < 0 && item.maximum > 0)) return { className: '', style: '' };
    const position = ((value - item.minimum) / (item.maximum - item.minimum)) * 100;
    const center = ((0 - item.minimum) / (item.maximum - item.minimum)) * 100;
    return {
        className: 'bipolar-range',
        style: `--slider-low:${Math.min(position, center)}%;--slider-high:${Math.max(position, center)}%`
    }
}

window.setReceiverSwitch = (id, enabled) => run(async () => {
    await api(`/radios/${radioId}/switches/${id}`, { method: 'PUT', body: JSON.stringify({ value: enabled }) });
    switchValues.set(String(id).toLowerCase(), enabled);
    renderDsp(snapshot.capabilities, snapshot.state, snapshot.authorization.canControl)
}, `${id} updated.`);

window.setReceiverChoice = (id, value) => run(async () => {
    await api(`/radios/${radioId}/choices/${id}`, { method: 'PUT', body: JSON.stringify({ value }) });
    choiceValues.set(String(id).toLowerCase(), value);
    renderReceiver(snapshot.capabilities, snapshot.state, snapshot.authorization.canControl)
}, `${id} updated.`);


function findChoice(c, id) {
    return Object.values(c.choices || {}).find(choice => String(choice.id).toLowerCase() === id.toLowerCase())
}

async function initializeSpectrumSpan() {
    const select = $('spectrum-span');
    const choice = snapshot && findChoice(snapshot.capabilities, 'SpectrumSpan');
    if (!choice) { select.innerHTML = ''; select.disabled = true; return }
    select.innerHTML = Object.values(choice.options || {}).map(option =>
        `<option value="${esc(option.value)}">${esc(option.displayName)}</option>`).join('');
    select.disabled = !(snapshot.authorization.canControl && access(choice.feature, 'write'));
    if (access(choice.feature, 'read')) try {
        const current = await api(`/radios/${radioId}/choices/${choice.id}/read`, { method: 'POST' });
        select.value = current.value
    } catch (error) { log(`Spectrum span: ${error.message}`, 'WARN') }
}

function selectedTuningStep(c) {
    const choice = findChoice(c, 'TuningStep');
    const selected = choiceValues.get(String(choice?.id).toLowerCase());
    const parsed = parseStepHz(selected);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : 10
}

function tuningStepOptions(c, mode) {
    const choice = findChoice(c, 'TuningStep');
    const advertised = Object.values(choice?.options || {}).filter(option => {
        const hz = parseStepHz(option.value);
        const applicable = !option.applicableModes || option.applicableModes.some(value => String(value).toLowerCase() === String(mode).toLowerCase());
        return applicable && (hz === 10 || hz === 100)
    });
    const options = c.frequency.smallestStepHz <= 1 ? [{ value: '1hz', displayName: '1 Hz' }, ...advertised] : advertised;
    return options.sort((left, right) => parseStepHz(left.value) - parseStepHz(right.value))
}

async function initializeTuningStep() {
    const c = snapshot?.capabilities;
    const choice = c && findChoice(c, 'TuningStep');
    if (!choice || !isPresented('choices', choice.id)) return;
    const key = String(choice.id).toLowerCase();
    const normal = tuningStepOptions(c, snapshot.state.mode).find(option => parseStepHz(option.value) === 10);
    const selected = normal?.value ?? tuningStepOptions(c, snapshot.state.mode)[0]?.value;
    if (selected === undefined) return;
    choiceValues.set(key, selected);
    if (normal && snapshot.authorization.canControl && access(choice.feature, 'write')) {
        await api(`/radios/${radioId}/choices/${choice.id}`, { method: 'PUT', body: JSON.stringify({ value: normal.value }) })
    }
    renderSnapshot(false)
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

function selectedMeters(c) {
    const meters = Object.values(c.meters || {}).filter(x => isPresented('meters', x.id));
    const byId = new Map(meters.map(x => [String(x.id).toLowerCase(), x]));
    return ['signalstrength', 'power', 'swr'].map(id => byId.get(id)).filter(Boolean)
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
    if (knob) {
        knob.style.setProperty('--knob-angle', `${-135 + ratio * 270}deg`);
        knob.style.setProperty('--knob-fill', `${ratio * 75}%`);
        knob.setAttribute('aria-valuenow', String(value))
    }
    const knobValue = $(`front-knob-value-${id}`);
    if (knobValue) knobValue.textContent = formatFrontControlValue(descriptor, value);
    const levelInput = $(`operator-level-input-${id}`), levelOutput = $(`operator-level-value-${id}`);
    if (levelInput) {
        if (Number(levelInput.value) !== value) levelInput.value = value;
        const slider = sliderPresentation(descriptor, value);
        if (slider.className) {
            const position = ((value - descriptor.minimum) / (descriptor.maximum - descriptor.minimum)) * 100;
            const center = ((0 - descriptor.minimum) / (descriptor.maximum - descriptor.minimum)) * 100;
            levelInput.style.setProperty('--slider-low', `${Math.min(position, center)}%`);
            levelInput.style.setProperty('--slider-high', `${Math.max(position, center)}%`)
        }
    }
    if (levelOutput) levelOutput.textContent = levelValue(descriptor, value);
    const receiverInput = $(`operator-receiver-input-${id}`), receiverOutput = $(`operator-receiver-value-${id}`);
    if (receiverInput && Number(receiverInput.value) !== value) receiverInput.value = value;
    if (receiverOutput) receiverOutput.textContent = `${value} ${descriptor.unit}`
}

function controlWriter(id) {
    const key = String(id).toLowerCase();
    if (!controlWriters.has(key)) controlWriters.set(key, { id, pending: null, inFlight: false, timer: null, lastStartedAt: 0 });
    return controlWriters.get(key)
}

function scheduleControlWrite(writer) {
    if (writer.inFlight || writer.timer !== null || writer.pending === null || !connected()) return;
    const delay = Math.max(0, controlWriteIntervalMs - (performance.now() - writer.lastStartedAt));
    if (delay === 0) flushControlWrite(writer);
    else writer.timer = setTimeout(() => { writer.timer = null; flushControlWrite(writer) }, delay)
}

async function flushControlWrite(writer) {
    if (writer.inFlight || writer.pending === null || !connected()) return;
    const value = writer.pending, requestRadioId = radioId;
    writer.pending = null;
    writer.inFlight = true;
    writer.lastStartedAt = performance.now();
    try {
        await api(`/radios/${requestRadioId}/controls/${writer.id}`, { method: 'PUT', body: JSON.stringify({ value }) })
    } catch (error) {
        status(`${writer.id} update failed`, error.message, 'error');
        log(`${writer.id}: ${error.message}`, 'ERROR')
    } finally {
        writer.inFlight = false;
        if (requestRadioId === radioId) scheduleControlWrite(writer)
    }
}

window.queueOperatorControl = (id, value) => {
    const descriptor = frontControlDescriptor(id);
    if (!descriptor || !snapshot?.authorization.canControl || !access(descriptor.feature, 'write') || !modeAllows(descriptor, 'write', snapshot.state.mode)) return;
    const normalized = Math.max(descriptor.minimum, Math.min(descriptor.maximum, value));
    controlValues.set(String(id).toLowerCase(), normalized);
    previewFrontControl(id, normalized);
    const writer = controlWriter(id);
    writer.pending = normalized;
    scheduleControlWrite(writer)
};

window.beginControlDrag = (event, id) => {
    activeControlPointers.set(event.pointerId, id)
};

function endControlDrag(event) {
    if (!activeControlPointers.delete(event.pointerId) || !snapshot) return;
    renderReceiver(snapshot.capabilities, snapshot.state, snapshot.authorization.canControl);
    renderTransmit(snapshot.capabilities, snapshot.state, snapshot.authorization.canControl)
}

window.addEventListener('pointerup', endControlDrag);
window.addEventListener('pointercancel', endControlDrag);

function adjustFrontControl(id, direction, multiplier = 1) {
    const descriptor = frontControlDescriptor(id);
    if (!descriptor || !snapshot.authorization.canControl || !access(descriptor.feature, 'write') || !modeAllows(descriptor, 'write', snapshot.state.mode)) return false;
    const key = String(id).toLowerCase();
    const current = controlValues.get(key) ?? descriptor.minimum;
    const value = Math.max(descriptor.minimum, Math.min(descriptor.maximum, current + direction * descriptor.step * multiplier));
    if (value === current) return false;
    window.queueOperatorControl(id, value);
    return true
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

window.setTuningStep = (id, value, label) => {
    run(async () => {
        if (parseStepHz(value) !== 1)
            await api(`/radios/${radioId}/choices/${id}`, { method: 'PUT', body: JSON.stringify({ value }) });
        choiceValues.set(String(id).toLowerCase(), value);
        renderSnapshot(false)
    }, `Tuning step changed to ${label}.`)
};

function canControlPtt() {
    return !!snapshot && clientRole.toLowerCase() === 'operator' &&
        hasPermission('transmit.ptt') &&
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
    const rangeViolation = remotePttViolation(c, s);
    const title = busy || !canControlPtt()
        ? rangeViolation || 'PTT requires an Operator session and writable transmit capability.'
        : 'Toggle PTT. A 10-second safety lease is renewed every 5 seconds while active.';
    const operatorButton = $('operator-ptt');
    operatorButton.classList.toggle('transmitting', s.isTransmitting);
    operatorButton.querySelector('span').textContent = s.isTransmitting ? 'PTT ON' : 'PTT';
    operatorButton.disabled = busy || !canControlPtt();
    operatorButton.title = title;
    $('operator-transmit-state').textContent = s.isTransmitting ? 'TX · LEASE ACTIVE' : 'RX · READY';
    $('operator-ptt-note').textContent = rangeViolation || (operatorButton.disabled ? 'PTT requires an Operator session and writable transmit capability.' : '10-second safety lease · renewed every 5 seconds while transmitting.');
    $('operator-ptt-note').classList.toggle('warning', !!rangeViolation);
    if (!s.isTransmitting) {
        pttLeaseActive = false;
        stopPttRenewal()
    }
}

function renderTransmit(c, s, writes) {
    const meters = ['Power', 'Swr'].map(id => Object.values(c.meters || {}).find(item => String(item.id).toLowerCase() === id.toLowerCase())).filter(item => item && isPresented('meters', item.id));
    const panel = $('operator-transmit-meters');
    const signature = meters.map(item => item.id).join('|');
    if (panel.dataset.signature !== signature) {
        panel.dataset.signature = signature;
        panel.innerHTML = meters.length ? meters.map(item => `<div class="operator-transmit-meter"><div class="operator-transmit-meter-head"><span>${esc(item.displayName).toUpperCase()}</span><strong id="operator-transmit-value-${item.id}">${item.requiresTransmit && !s.isTransmitting ? 'TX only' : '—'}</strong></div><div class="operator-transmit-meter-track"><div id="operator-transmit-fill-${item.id}"></div></div></div>`).join('') : '<span class="empty-state">No approved transmit meters are advertised.</span>'
    }
    if ([...activeControlPointers.values()].some(id => String(id).toLowerCase() === 'transmitpower')) return;
    const item = Object.values(c.controls || {}).find(control => String(control.id).toLowerCase() === 'transmitpower' && isPresented('controls', control.id));
    if (!item) {
        $('operator-transmit-levels').innerHTML = '';
        return
    }
    const value = controlValues.get(String(item.id).toLowerCase()) ?? item.minimum;
    const writable = hasPermission('transmit.power') && writes && access(item.feature, 'write') && modeAllows(item, 'write', s.mode);
    $('operator-transmit-levels').innerHTML = `<label class="operator-level"><span class="operator-level-head"><span>${esc(item.displayName)}</span><strong id="operator-level-value-${item.id}">${levelValue(item, value)}</strong></span><input id="operator-level-input-${item.id}" type="range" min="${item.minimum}" max="${item.maximum}" step="${item.step}" value="${value}" ${writable ? '' : 'disabled'} onpointerdown="beginControlDrag(event,'${esc(item.id)}')" oninput="queueOperatorControl('${esc(item.id)}',Number(this.value))"></label>`
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
    const remainder = ((current % step) + step) % step;
    const aligned = direction > 0
        ? (remainder === 0 ? current + step : current + step - remainder)
        : (remainder === 0 ? current - step : current - remainder);
    const next = nearestAllowedFrequency(aligned, ranges);
    snapshot.state.frequenciesHz[vfo] = next;
    if (snapshot.state.vfos?.[vfo]) snapshot.state.vfos[vfo].frequencyHz = next;
    const display = $(`frequency-${vfo}`), input = $(`freq-${vfo}`);
    if (display) display.textContent = frequencyText(next);
    if (input) input.value = next;
    const dualFrequency = $(`operator-tuning-frequency-${String(vfo).toUpperCase()}`);
    if (dualFrequency) dualFrequency.textContent = compactFrequency(next);
    if (String(snapshot.state.activeVfo).toLowerCase() === String(vfo).toLowerCase()) $('operator-frequency').textContent = compactFrequency(next);
    else $('operator-secondary-frequency').textContent = compactFrequency(next);
    animateDial(direction, multiplier, vfo);
    queueFrequencyWrite(vfo, next);
    return true
}

function animateDial(direction, multiplier, vfo) {
    const degrees = 7 + Math.min(20, Math.log10(Math.max(1, multiplier)) * 7);
    const requested = String(vfo).toUpperCase();
    const dualMotion = $(`operator-tuning-motion-${requested}`), dualDial = $(`operator-tuning-dial-${requested}`);
    if (dualMotion && dualDial) {
        dualDialRotation[requested] = (dualDialRotation[requested] || 0) + direction * degrees;
        dualMotion.style.transform = `rotate(${dualDialRotation[requested]}deg)`;
        dualDial.classList.add('tuning');
        clearTimeout(dualDial.animationTimer);
        dualDial.animationTimer = setTimeout(() => dualDial.classList.remove('tuning'), 160)
    }
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
    renderSnapshot(false)
}, `Mode changed to ${mode}.`);

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

async function refreshAll() {
    const root = `/radios/${radioId}`;
    snapshot = await api(`${root}/refresh`, {
        method: 'POST'
    });
    renderSnapshot();
    const c = snapshot.capabilities,
        mode = snapshot.state.mode;
    for (const x of Object.values(c.controls || {}))
        if (isPresented('controls', x.id) && access(x.feature, 'read') && modeAllows(x, 'read', mode)) try {
            const v = await api(`${root}/controls/${x.id}/read`, {
                method: 'POST'
            });
            controlValues.set(String(x.id).toLowerCase(), v.value);
        } catch (e) {
            log(`${x.displayName}: ${e.message}`, 'WARN')
        }
    for (const x of Object.values(c.switches || {}))
        if (isPresented('switches', x.id) && access(x.feature, 'read') && modeAllows(x, 'read', mode)) try {
            const v = await api(`${root}/switches/${x.id}/read`, {
                method: 'POST'
            });
            switchValues.set(String(x.id).toLowerCase(), v.enabled);
        } catch (e) {
            log(`${x.displayName}: ${e.message}`, 'WARN')
        }
    for (const x of Object.values(c.choices || {}))
        if (isPresented('choices', x.id) && access(x.feature, 'read') && modeAllows(x, 'read', mode)) try {
            const v = await api(`${root}/choices/${x.id}/read`, {
                method: 'POST'
            });
            choiceValues.set(String(x.id).toLowerCase(), v.value);
        } catch (e) {
            log(`${x.displayName}: ${e.message}`, 'WARN')
        }
    renderSnapshot(false);
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
        const selected = selectedMeters(snapshot.capabilities);
        const transmit = Object.values(snapshot.capabilities.meters || {}).filter(item => ['power', 'swr'].includes(String(item.id).toLowerCase()) && isPresented('meters', item.id));
        const meters = [...new Map([...selected, ...transmit].map(item => [String(item.id).toLowerCase(), item])).values()];
        for (const x of meters) {
            const applicable = modeAllows(x, 'read', mode) && (!x.requiresTransmit || snapshot.state.isTransmitting);
            const frontValue = $(`front-meter-value-${x.id}`), frontFill = $(`front-meter-fill-${x.id}`),
                transmitValue = $(`operator-transmit-value-${x.id}`), transmitFill = $(`operator-transmit-fill-${x.id}`);
            if (!applicable) {
                if (frontValue) frontValue.textContent = x.requiresTransmit ? 'TX only' : '—';
                if (frontFill) frontFill.style.width = '0%';
                if (transmitValue) transmitValue.textContent = x.requiresTransmit ? 'TX only' : '—';
                if (transmitFill) transmitFill.style.width = '0%';
                continue
            }
            try {
                const v = await api(`/radios/${radioId}/meters/${x.id}/read`, { method: 'POST' });
                const e = $(`meter-value-${x.id}`), f = $(`meter-fill-${x.id}`);
                if (e) e.textContent = `${v.rawValue} ${x.rawUnit}`;
                if (frontValue) frontValue.textContent = `${v.rawValue} ${x.rawUnit}`;
                if (transmitValue) transmitValue.textContent = `${v.rawValue} ${x.rawUnit}`;
                const width = `${Math.max(0, Math.min(100, v.normalizedValue * 100))}%`;
                if (f) f.style.width = width;
                if (frontFill) frontFill.style.width = width;
                if (transmitFill) transmitFill.style.width = width;
                if (String(x.id).toLowerCase() === 'signalstrength') {
                    $('operator-signal-value').textContent = `${v.rawValue} ${x.rawUnit}`;
                    $('operator-signal-fill').style.width = width
                }
            } catch (error) { log(`${x.displayName}: ${error.message}`, 'WARN') }
        }
    } finally { meterPollInFlight = false }
}
window.executeRadioAction = id => run(async () => {
    await api(`/radios/${radioId}/actions/${id}`, { method: 'POST' })
}, `${id} executed.`);
function renderOffline() {
    stopPttRenewal();
    stopMeterPolling();
    pttLeaseActive = false;
    controlValues.clear();
    choiceValues.clear();
    switchValues.clear();
    for (const writer of frequencyWriters.values())
        if (writer.timer !== null) clearTimeout(writer.timer);
    frequencyWriters.clear();
    for (const writer of controlWriters.values()) if (writer.timer !== null) clearTimeout(writer.timer);
    controlWriters.clear();
    activeControlPointers.clear();
    updateHeaderControls();
    $('operator-ptt').disabled = true;
    $('operator-ptt').classList.remove('transmitting');
    $('operator-ptt').querySelector('span').textContent = 'PTT';
    $('operator-transmit-state').textContent = 'RX · READY';
    $('operator-ptt-note').textContent = 'PTT requires an Operator session and writable transmit capability.';
    $('operator-ptt-note').classList.remove('warning');
    $('operator-transmit-meters').innerHTML = '';
    $('operator-transmit-meters').dataset.signature = '';
    $('operator-radio-state').textContent = 'OFFLINE';
    $('operator-rx-flag').textContent = 'RX';
    $('operator-rx-flag').classList.remove('tx');
    $('operator-active-vfo').textContent = 'VFO A';
    $('operator-active-vfo').disabled = true;
    $('operator-mode').textContent = '---';
    $('operator-split').textContent = 'SPLIT OFF';
    $('operator-split').disabled = true;
    $('operator-split').classList.remove('active');
    $('operator-frequency').textContent = '--.---.---';
    $('operator-secondary-name').textContent = 'VFO B';
    $('operator-secondary-frequency').textContent = '--.---.---';
    $('operator-signal-value').textContent = '--';
    $('operator-signal-fill').style.width = '0%';
    $('operator-vfo-actions').innerHTML = '';
    $('operator-band-buttons').innerHTML = '';
    $('operator-mode-buttons').innerHTML = '';
    $('operator-dsp-switches').innerHTML = '';
    $('operator-receiver-choices').innerHTML = '';
    $('operator-receiver-levels').innerHTML = '';
    $('operator-transmit-levels').innerHTML = '';
    $('operator-tuning-step').innerHTML = '';
    for (const vfo of ['A', 'B']) {
        $(`operator-tuning-frequency-${vfo}`).textContent = '--.---.---';
        $(`operator-tuning-state-${vfo}`).textContent = 'READ ONLY';
        $(`operator-tuning-state-${vfo}`).className = '';
        $(`operator-tuning-dial-${vfo}`).setAttribute('aria-disabled', 'true');
        $(`operator-tuning-dial-${vfo}`).tabIndex = -1;
        $(`operator-tuning-motion-${vfo}`).style.transform = '';
        dualDialRotation[vfo] = 0
    }
    setBusy(false)
}

async function initializeRotators(visible) {
    $('rotator-view-toggle').hidden = !visible;
    if (!visible) return;
    const configuration = await api('/rotators/configuration');
    rotatorStatuses = configuration.rotators || [];
    const dashboard = $('rotator-dashboard');
    dashboard.innerHTML = rotatorStatuses.map(renderRotatorCard).join('');
    initializeRotatorCards();
    initializeRotatorDashboardLayout();
    $('rotator-view-toggle').onclick = () => showRotatorWorkspace($('rotator-workspace').hidden);
    renderRotators();
    rotatorPollTimer = setInterval(refreshRotators, 500)
}

function renderRotatorCard(item) {
    const name = esc(item.displayName || item.id);
    return `<section class="operator-card rotator-unit-card" data-card="${esc(`rotator:${item.id}`)}" data-rotator-id="${esc(item.id)}" aria-label="${name} dial and controls">
      <div class="operator-card-heading"><span data-role="name">${name}</span><strong data-role="status">OFFLINE</strong></div>
      <div class="rotator-unit-body"><div class="rotator-dial-pane"><div class="rotator-map" data-role="map">
        <div class="rotator-map-image" data-role="map-image"></div><div class="rotator-beam" data-role="beam"></div><div class="rotator-compass-ring"></div>
        <div class="rotator-scale" data-role="scale" aria-hidden="true"></div><div class="rotator-bearing" data-role="bearing"><i></i></div>
        <div class="rotator-target-bearing" data-role="target-bearing"><i></i></div><div class="rotator-map-center"></div>
      </div></div><div class="rotator-control-card">
        <div class="rotator-readout"><span>CURRENT AZIMUTH</span><strong data-role="azimuth">---&deg;</strong><em class="rotator-overlap" data-role="overlap" hidden><i></i>OVERLAP</em></div>
        <div class="rotator-manual-controls"><button data-action="ccw" type="button">CCW</button><button class="rotator-stop" data-action="stop" type="button">STOP</button><button data-action="cw" type="button">CW</button></div>
        <label class="rotator-target-entry">TARGET AZIMUTH<div><input data-role="target" type="number" min="0" max="450" step="1"><button data-action="go" type="button">GO</button></div></label>
        <div class="rotator-presets" aria-label="Azimuth presets">${[0, 60, 90, 180, 270, 360].map(value => `<button type="button" data-rotator-preset="${value}">${value}&deg;</button>`).join('')}</div>
        <button data-action="park" type="button" hidden>PARK</button><p class="operator-ptt-note warning" data-role="error"></p>
      </div></div>
    </section>`
}

function rotatorPart(card, role) { return card.querySelector(`[data-role="${role}"]`) }
function rotatorCard(id) { return dashboardCards($('rotator-dashboard')).find(card => card.dataset.rotatorId === id) }
function rotatorStatus(id) { return rotatorStatuses.find(item => item.id === id) }

function initializeRotatorCards() {
    for (const card of dashboardCards($('rotator-dashboard'))) {
        const id = card.dataset.rotatorId;
        const map = rotatorPart(card, 'map');
        const chooseTarget = event => {
            const item = rotatorStatus(id);
            const bounds = map.getBoundingClientRect();
            const radians = Math.atan2(event.clientX - (bounds.left + bounds.width / 2), -(event.clientY - (bounds.top + bounds.height / 2)));
            const heading = (radians * 180 / Math.PI - (item?.mapNorthOffsetDegrees || 0) + 360) % 360;
            const target = rotatorPart(card, 'target');
            target.value = String(Math.round(resolveRotatorAzimuth(id, heading)));
            renderRotatorTarget(id);
            return Number(target.value)
        };
        map.onclick = chooseTarget;
        map.ondblclick = event => sendRotatorCommand(id, 'azimuth', 'PUT', { value: chooseTarget(event) });
        rotatorPart(card, 'target').oninput = () => renderRotatorTarget(id);
        card.querySelector('[data-action="go"]').onclick = () => sendRotatorCommand(id, 'azimuth', 'PUT', { value: Number(rotatorPart(card, 'target').value) });
        card.querySelectorAll('[data-rotator-preset]').forEach(button => button.onclick = () => {
            const value = Number(button.dataset.rotatorPreset);
            rotatorPart(card, 'target').value = String(value);
            renderRotatorTarget(id);
            sendRotatorCommand(id, 'azimuth', 'PUT', { value })
        });
        for (const [action, command] of [['cw', 'clockwise'], ['ccw', 'counterclockwise'], ['stop', 'stop'], ['park', 'park']])
            card.querySelector(`[data-action="${action}"]`).onclick = () => sendRotatorCommand(id, command);
        renderRotatorScale(card)
    }
}

function showRotatorWorkspace(show) {
    setLayoutEditing(false);
    $('rotator-workspace').hidden = !show;
    $('operator-dashboard').hidden = show;
    $('rotator-view-toggle').textContent = show ? 'Radio' : 'Rotators';
    $('rotator-view-toggle').classList.toggle('active', show)
}

async function refreshRotators() {
    try {
        rotatorStatuses = await api('/rotators');
        renderRotators()
    } catch (error) { log(`Rotator: ${error.message}`, 'WARN') }
}

function resolveRotatorAzimuth(id, heading) {
    const item = rotatorStatus(id);
    const minimum = item?.capabilities?.minimumAzimuth ?? 0;
    const maximum = item?.capabilities?.maximumAzimuth ?? 360;
    const current = item?.position?.azimuth;
    const candidates = [];
    for (let value = heading; value <= maximum + 0.0001; value += 360) {
        if (value >= minimum - 0.0001) candidates.push(value)
    }
    if (!candidates.length) return Math.max(minimum, Math.min(maximum, heading));
    if (!Number.isFinite(current)) return candidates[0];
    return candidates.reduce((closest, value) => Math.abs(value - current) < Math.abs(closest - current) ? value : closest)
}

function renderRotatorScale(card) {
    const marks = [];
    for (let degrees = 0; degrees < 360; degrees += 5) {
        const major = degrees % 30 === 0;
        marks.push(`<i class="rotator-tick${major ? ' major' : ''}" style="--angle:${degrees}deg"></i>`);
        if (major) marks.push(`<span class="rotator-degree" style="--angle:${degrees}deg"><b style="--counter-angle:${-degrees}deg">${degrees}°</b></span>`)
    }
    rotatorPart(card, 'scale').innerHTML = marks.join('')
}

function renderRotators() {
    rotatorStatuses.forEach(item => renderRotator(item, rotatorCard(item.id)))
}

function renderRotator(item, card) {
    if (!item || !card) return;
    rotatorPart(card, 'name').textContent = item.displayName.toUpperCase();
    const connection = rotatorPart(card, 'status');
    connection.textContent = item.connected ? 'CONNECTED' : 'OFFLINE';
    connection.classList.toggle('online', item.connected);
    rotatorPart(card, 'error').textContent = item.error || '';
    const azimuth = item.position?.azimuth;
    rotatorPart(card, 'azimuth').textContent = Number.isFinite(azimuth) ? `${azimuth.toFixed(1)}°` : '---°';
    const overlap = Number.isFinite(azimuth) && azimuth >= 360 && (item.capabilities?.maximumAzimuth ?? 360) > 360;
    rotatorPart(card, 'overlap').hidden = !overlap;
    if (Number.isFinite(azimuth)) {
        const rotation = azimuth + item.mapNorthOffsetDegrees;
        rotatorPart(card, 'bearing').style.transform = `rotate(${rotation}deg)`;
        rotatorPart(card, 'beam').style.transform = `rotate(${rotation}deg)`
    }
    const mapImage = rotatorPart(card, 'map-image');
    mapImage.style.backgroundImage = item.mapImage ? `url("${String(item.mapImage).replace(/["\\]/g, '')}")` : 'none';
    mapImage.style.opacity = String(item.mapOpacity ?? 0.65);
    rotatorPart(card, 'map').style.setProperty('--rotator-bezel', item.bezelColor || '#36cfe0');
    const beam = rotatorPart(card, 'beam');
    const beamWidth = Math.max(0, Math.min(360, Number(item.beamWidthDegrees) || 0));
    beam.hidden = !item.showBeam || beamWidth === 0;
    beam.style.opacity = String(item.beamOpacity ?? 0.28);
    beam.style.background = `conic-gradient(from ${-beamWidth / 2}deg at 50% 50%, ${item.beamColor || '#ffb548'} 0deg ${beamWidth}deg, transparent ${beamWidth}deg 360deg)`;
    const capabilities = item.capabilities;
    const writable = item.connected && item.allowControl;
    card.querySelector('[data-action="ccw"]').disabled = !(writable && capabilities?.canMoveManually);
    card.querySelector('[data-action="cw"]').disabled = !(writable && capabilities?.canMoveManually);
    card.querySelector('[data-action="stop"]').disabled = !(writable && capabilities?.canStop);
    card.querySelector('[data-action="go"]').disabled = !(writable && capabilities?.canMoveTo);
    const target = rotatorPart(card, 'target');
    target.disabled = !(writable && capabilities?.canMoveTo);
    target.min = capabilities?.minimumAzimuth ?? 0;
    target.max = capabilities?.maximumAzimuth ?? 360;
    card.querySelectorAll('[data-rotator-preset]').forEach(button => {
        const value = Number(button.dataset.rotatorPreset);
        button.disabled = !(writable && capabilities?.canMoveTo) || value < (capabilities?.minimumAzimuth ?? 0) || value > (capabilities?.maximumAzimuth ?? 360)
    });
    const park = card.querySelector('[data-action="park"]');
    park.hidden = !capabilities?.canPark;
    park.disabled = !writable
}

function renderRotatorTarget(id) {
    const card = rotatorCard(id);
    const item = rotatorStatus(id);
    const value = Number(rotatorPart(card, 'target').value);
    if (Number.isFinite(value) && item) rotatorPart(card, 'target-bearing').style.transform = `rotate(${value + item.mapNorthOffsetDegrees}deg)`
}

async function sendRotatorCommand(id, action, method = 'POST', body = null) {
    if (!id) return;
    try {
        await api(`/rotators/${encodeURIComponent(id)}/${action}`, {
            method,
            ...(body ? { body: JSON.stringify(body) } : {})
        });
        await refreshRotators()
    } catch (error) {
        status('Rotator command failed', error.message, 'error')
    }
}

const dashboardLayoutKey = 'rig2castOperatorLayoutV3';
const rotatorLayoutKey = 'rig2castRotatorLayoutV2';
const defaultDashboardLayout = {
    vfo: { order: 1, span: 4 },
    spectrum: { order: 2, span: 8 },
    band: { order: 3, span: 8 },
    mode: { order: 4, span: 4 },
    dsp: { order: 5, span: 3 },
    receiver: { order: 6, span: 9 },
    tuning: { order: 7, span: 6 },
    transmit: { order: 8, span: 6 }
};

function dashboardCards(dashboard = $('operator-dashboard')) {
    return [...dashboard.querySelectorAll(':scope > [data-card]')]
}

function readDashboardLayout(storageKey = dashboardLayoutKey, defaults = defaultDashboardLayout) {
    try {
        const saved = JSON.parse(localStorage.getItem(storageKey) || '{}');
        return Object.fromEntries(Object.entries(defaults).map(([id, cardDefaults]) => {
            const value = saved[id] || {};
            return [id, {
                order: Number.isInteger(value.order) ? value.order : cardDefaults.order,
                span: Math.max(3, Math.min(12, Number(value.span) || cardDefaults.span))
            }]
        }))
    } catch {
        return structuredClone(defaults)
    }
}

function applyDashboardLayout(layout, dashboard = $('operator-dashboard'), defaults = defaultDashboardLayout) {
    for (const card of dashboardCards(dashboard)) {
        const value = layout[card.dataset.card] || defaults[card.dataset.card];
        card.style.setProperty('--card-order', value.order);
        card.style.setProperty('--card-span', value.span);
        card.dataset.layoutSpan = value.span;
        const label = card.querySelector('.layout-size-label');
        if (label) label.textContent = `${value.span} / 12`
    }
}

function saveDashboardLayout(dashboard = $('operator-dashboard'), storageKey = dashboardLayoutKey) {
    const layout = {};
    for (const card of dashboardCards(dashboard)) layout[card.dataset.card] = {
        order: Number(card.style.getPropertyValue('--card-order')),
        span: Number(card.style.getPropertyValue('--card-span'))
    };
    localStorage.setItem(storageKey, JSON.stringify(layout))
}

function initializeDashboardLayout(dashboard, storageKey, defaults) {
    let draggedCard = null;
    for (const card of dashboardCards(dashboard)) {
        const tools = document.createElement('div');
        tools.className = 'card-layout-tools';
        tools.innerHTML = `<button class="card-drag-handle" type="button" aria-label="Move ${esc(card.getAttribute('aria-label') || card.dataset.card)}" title="Drag to reorder">⠿</button><button class="card-resize-handle" type="button" aria-label="Resize ${esc(card.getAttribute('aria-label') || card.dataset.card)}" title="Drag horizontally to resize">↔</button>`;
        card.append(document.createElement('span'), tools);
        card.lastElementChild.previousElementSibling.className = 'layout-size-label';
        const drag = tools.querySelector('.card-drag-handle');
        const resize = tools.querySelector('.card-resize-handle');
        drag.addEventListener('pointerdown', () => { if (dashboard.classList.contains('layout-editing')) card.draggable = true });
        drag.addEventListener('pointerup', () => { card.draggable = false });
        card.addEventListener('dragstart', event => {
            if (!dashboard.classList.contains('layout-editing')) return event.preventDefault();
            draggedCard = card;
            card.classList.add('layout-dragging');
            event.dataTransfer.effectAllowed = 'move'
        });
        card.addEventListener('dragover', event => {
            if (!draggedCard || draggedCard === card) return;
            event.preventDefault();
            const bounds = card.getBoundingClientRect();
            card.dataset.dropPosition = event.clientX >= bounds.left + bounds.width / 2 ? 'after' : 'before';
            card.classList.add('layout-drop-target')
        });
        card.addEventListener('dragleave', () => {
            card.classList.remove('layout-drop-target');
            delete card.dataset.dropPosition
        });
        card.addEventListener('drop', event => {
            event.preventDefault();
            card.classList.remove('layout-drop-target');
            if (!draggedCard || draggedCard === card) return;
            const cards = dashboardCards(dashboard).sort((left, right) =>
                Number(left.style.getPropertyValue('--card-order')) - Number(right.style.getPropertyValue('--card-order')));
            const reordered = cards.filter(item => item !== draggedCard);
            const targetIndex = reordered.indexOf(card);
            const insertionIndex = targetIndex + (card.dataset.dropPosition === 'after' ? 1 : 0);
            reordered.splice(insertionIndex, 0, draggedCard);
            reordered.forEach((item, index) => item.style.setProperty('--card-order', index + 1));
            delete card.dataset.dropPosition;
            saveDashboardLayout(dashboard, storageKey)
        });
        card.addEventListener('dragend', () => {
            draggedCard = null;
            card.draggable = false;
            dashboardCards(dashboard).forEach(item => {
                item.classList.remove('layout-dragging', 'layout-drop-target');
                delete item.dataset.dropPosition
            })
        });
        resize.addEventListener('pointerdown', event => {
            if (!dashboard.classList.contains('layout-editing')) return;
            event.preventDefault();
            resize.setPointerCapture(event.pointerId);
            const startX = event.clientX;
            const startSpan = Number(card.style.getPropertyValue('--card-span')) || 12;
            const columnWidth = dashboard.getBoundingClientRect().width / 12;
            const move = moveEvent => {
                const span = Math.max(3, Math.min(12, startSpan + Math.round((moveEvent.clientX - startX) / columnWidth)));
                card.style.setProperty('--card-span', span);
                card.dataset.layoutSpan = span;
                card.querySelector('.layout-size-label').textContent = `${span} / 12`
            };
            const end = endEvent => {
                resize.releasePointerCapture(endEvent.pointerId);
                resize.removeEventListener('pointermove', move);
                resize.removeEventListener('pointerup', end);
                resize.removeEventListener('pointercancel', end);
                saveDashboardLayout(dashboard, storageKey)
            };
            resize.addEventListener('pointermove', move);
            resize.addEventListener('pointerup', end);
            resize.addEventListener('pointercancel', end)
        })
    }
    applyDashboardLayout(readDashboardLayout(storageKey, defaults), dashboard, defaults);
    return {
        dashboard,
        setEditing(editing) { dashboard.classList.toggle('layout-editing', editing) },
        reset() {
            localStorage.removeItem(storageKey);
            applyDashboardLayout(defaults, dashboard, defaults)
        }
    }
}

const operatorLayoutController = initializeDashboardLayout($('operator-dashboard'), dashboardLayoutKey, defaultDashboardLayout);
let rotatorLayoutController = null;

function initializeRotatorDashboardLayout() {
    const count = rotatorStatuses.length;
    const defaults = Object.fromEntries(rotatorStatuses.map((item, index) => [
        `rotator:${item.id}`,
        { order: index + 1, span: count === 1 ? 12 : 6 }
    ]));
    rotatorLayoutController = initializeDashboardLayout($('rotator-dashboard'), rotatorLayoutKey, defaults)
}

function layoutControllers() {
    return [operatorLayoutController, rotatorLayoutController].filter(Boolean)
}

function activeLayoutController() {
    return $('rotator-workspace').hidden ? operatorLayoutController : (rotatorLayoutController || operatorLayoutController)
}

function setLayoutEditing(editing) {
    layoutControllers().forEach(controller => controller.setEditing(false));
    if (editing) activeLayoutController().setEditing(true);
    $('layout-edit').classList.toggle('active', editing);
    $('layout-edit').setAttribute('aria-pressed', String(editing));
    $('layout-edit').textContent = editing ? 'Done' : 'Arrange';
    $('layout-reset').hidden = !editing
}

$('layout-edit').onclick = () => setLayoutEditing(!activeLayoutController().dashboard.classList.contains('layout-editing'));
$('layout-reset').onclick = () => activeLayoutController().reset();
$('operator-ptt').onclick = window.togglePtt;
$('station-connect').onclick = async () => {
    if (connected()) {
        const confirmed = await requestConfirmation({
            title: 'Disconnect radio?',
            message: 'The GUI will detach from the station radio. The physical radio will remain powered on.',
            confirmLabel: 'DISCONNECT'
        });
        if (!confirmed) return;
        await run(disconnect, 'Radio disconnected.');
        return
    }
    await refreshStationSession();
    if (!stationSession.available && !stationSession.ownedByCurrentUser) {
        if (!stationSession.canTakeOver) return;
        const confirmed = await requestConfirmation({
            title: `Take control from ${stationSession.ownerDisplayName}?`,
            message: 'Their radio, spectrum, and audio connection will be safely ended before this browser connects.',
            confirmLabel: 'TAKE CONTROL',
            tone: 'danger'
        });
        if (!confirmed) return;
        await run(async () => {
            await api('/station-session/takeover', { method: 'POST' });
            await connect();
            await globalThis.rig2castAudio?.start()
        }, 'Station control transferred.');
        return
    }
    await run(async () => {
        try {
            await connect();
            await globalThis.rig2castAudio?.start()
        } catch (error) {
            globalThis.rig2castAudio?.stop();
            throw error
        }
    })
};
$('header-audio').onclick = () => {
    if (!connected()) return;
    if (globalThis.rig2castAudio?.isActive()) globalThis.rig2castAudio.stop();
    else globalThis.rig2castAudio?.start()
};
addEventListener('rig2cast-audio-state', event => {
    const active = !!event.detail?.active;
    const button = $('header-audio');
    button.querySelector('span').textContent = active ? 'Audio on' : 'Audio off';
    button.setAttribute('aria-pressed', String(active))
});
for (const vfo of ['A', 'B']) {
    const dial = $(`operator-tuning-dial-${vfo}`);
    dial.addEventListener('wheel', event => {
        if (tuneBy(dial.dataset.vfo, event.deltaY < 0 ? 1 : -1, tuningMultiplier(event))) event.preventDefault()
    }, { passive: false });
    dial.addEventListener('keydown', event => window.tuneFrequencyKey(event, dial.dataset.vfo));
    dial.addEventListener('pointerdown', event => {
        if (dial.getAttribute('aria-disabled') !== 'true') window.startTuningKnob(event, dial.dataset.vfo)
    })
}
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
for (const id of ['spectrum-minimum', 'spectrum-maximum']) {
    $(id).addEventListener('change', () => run(async () => {
        await updateSpectrumLevels();
        if (spectrumFrame) {
            spectrumFrame.minimumLevel = Number($('spectrum-minimum').value);
            spectrumFrame.maximumLevel = Number($('spectrum-maximum').value);
            renderSpectrum()
        }
    }, 'Spectrum display levels updated.'))
}
$('spectrum-span').addEventListener('change', event => run(async () => {
    if (!radioId) throw new Error('Connect to the radio before changing the spectrum span.');
    await api(`/radios/${radioId}/choices/SpectrumSpan`, {
        method: 'PUT', body: JSON.stringify({ value: event.target.value })
    });
    log(`Spectrum span changed to ${event.target.options[event.target.selectedIndex]?.text || event.target.value}.`)
}));
$('radio-power-control').addEventListener('click', async () => {
    await run(refreshRadioPowerState);
    if (radioPoweredOn === null) {
        status('Operation failed', 'The current radio power state could not be read.', 'error');
        return
    }
    const desired = !radioPoweredOn;
    const verb = desired ? 'ON' : 'OFF';
    const confirmed = await requestConfirmation({
        title: `Power radio ${verb.toLowerCase()}?`,
        message: desired
            ? 'The physical radio will be powered on through the station USB CAT connection.'
            : 'The physical radio will stop receiving and transmitting, and this GUI will disconnect.',
        confirmLabel: `POWER ${verb}`,
        tone: desired ? 'positive' : 'danger'
    });
    if (!confirmed) return;
    await run(async () => {
        await api('/radio-power', { method: 'PUT', body: JSON.stringify({ value: desired }) });
        radioPoweredOn = desired;
        if (!desired && connected()) {
            try { await disconnect() } catch { snapshot = null; radioId = null; renderOffline() }
        }
        if (desired) await new Promise(resolve => setTimeout(resolve, 4000));
        await refreshRadioPowerState()
    }, 'Radio power command completed.')
});

$('confirmation-cancel').onclick = () => closeConfirmation(false);
$('confirmation-accept').onclick = () => closeConfirmation(true);
$('confirmation-modal').addEventListener('pointerdown', event => {
    if (event.target === $('confirmation-modal')) closeConfirmation(false)
});
$('confirmation-dialog').addEventListener('keydown', event => {
    if (event.key === 'Escape') {
        event.preventDefault();
        closeConfirmation(false);
        return
    }
    if (event.key !== 'Tab') return;
    const focusable = [...$('confirmation-dialog').querySelectorAll('button:not([disabled])')];
    if (!focusable.length) return;
    const first = focusable[0], last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus()
    } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus()
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
$('login-form').addEventListener('submit', submitLogin);
$('password-change-form').addEventListener('submit', submitPasswordChange);
$('session-logout').addEventListener('click', logout);
$('admin-users').addEventListener('click', openAdminUsers);
$('admin-close').addEventListener('click', closeAdminUsers);
$('admin-create-select').addEventListener('click', showAdminCreate);
$('admin-create-form').addEventListener('submit', submitAdminCreate);
$('admin-user-form').addEventListener('submit', submitAdminUser);
$('admin-reset-submit').addEventListener('click', resetAdminPassword);
$('admin-delete-user').addEventListener('click', deleteAdminUser);
$('admin-modal').addEventListener('pointerdown', event => {
    if (event.target === $('admin-modal')) closeAdminUsers()
});
$('admin-modal').addEventListener('keydown', event => {
    if (event.key === 'Escape' && $('confirmation-modal').hidden) {
        event.preventDefault();
        closeAdminUsers()
    }
});
initializeAuthentication()
    .then(authenticated => authenticated && bootstrap())
    .catch(e => status('Startup failed', e.message, 'error'));
