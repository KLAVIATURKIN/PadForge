import * as core from './core.mjs';
import {CommandParser} from './input/CommandParser.mjs';
import {CommandExecutor} from './input/CommandExecutor.mjs';
import {InputCommandQueue} from './input/InputCommandQueue.mjs';
import {createCommandHandler} from './command/CommandHandler.mjs';
import {ChatMessage} from './model/ChatMessage.mjs';

window.PadForgeIntegrationCore = core;

function initialize() {
    const api = window.PadForgeController;
    if (!api || api.integrationStarted) return;
    api.integrationStarted = true;
    const params = api.params;
    if (params.get('integration') !== '1' && params.get('physical') !== '1' && !params.get('channel')) return;
    const secrets = new URLSearchParams(location.hash.slice(1));
    const channel = (params.get('channel') || '').replace(/^#/, '').trim();
    const login = params.get('login') || channel;
    const token = secrets.get('token') || '';
    const gamepad = new core.ChatGamepad(api);
    const parser = new CommandParser(gamepad.validInputs());
    const executor = new CommandExecutor(gamepad);
    const queue = new InputCommandQueue(executor);
    const users = new Map();
    let paused = false, active = null, twitch = null, physicalIndex = null;
    let physicalCache = new Map(), physicalFrame = null;

    const panel = document.createElement('section');
    panel.id = 'integration-panel';
    panel.hidden = true;
    panel.innerHTML = `<h2>Controller integration</h2>
        <p id="integration-twitch" role="status">Twitch chat is off.</p>
        <p id="integration-queue"></p><p id="integration-owner"></p>
        <label><input id="integration-paused" type="checkbox"> Pause chat inputs</label><br>
        <button id="integration-skip" type="button">Skip current</button>
        <button id="integration-clear" type="button">Skip all</button>
        <p><label>Physical gamepad <select id="integration-pad"><option value="">Automatic</option></select></label></p>
        <p id="integration-physical" role="status"></p>
        <p id="integration-notice" role="status"></p>
        <table><thead><tr><th>User</th><th>Queued</th><th>Started</th><th>Rejected</th><th>Controls</th></tr></thead><tbody id="integration-users"></tbody></table>
        <button id="integration-export" type="button">Export statistics</button>
        <button id="integration-reset" type="button">Reset statistics</button>
        <details><summary>Chat commands</summary><p>!a · !a+b · !a500ms · ![a b]*3 · !_a · !-a · !#500ms</p>
        <p>Inputs: a b x y lb rb lt rt back start ls rs home tpad up down left right (u d l r).</p>
        <p>!claim · !claim @user · !accept @user · !claim status · !release · !release @user · !skip · !skipAll</p>
        <p>Claims last five minutes. Two different users confirm claims or skips; moderators can act immediately.</p></details>`;
    document.body.appendChild(panel);
    const element = id => document.getElementById(`integration-${id}`);
    const toggle = document.createElement('button');
    toggle.type = 'button'; toggle.textContent = 'Integration'; toggle.className = 'integration-toggle';
    toggle.setAttribute('aria-controls', panel.id); toggle.setAttribute('aria-expanded', 'false');
    toggle.addEventListener('click', () => { panel.hidden = !panel.hidden; toggle.setAttribute('aria-expanded', String(!panel.hidden)); render(); });
    const utilityMount = document.getElementById('fsMount');
    utilityMount.appendChild(toggle);
    utilityMount.classList.add('utility-control');
    let utilityTimer;
    function revealUtilities() {
        document.body.classList.remove('utilities-idle');
        clearTimeout(utilityTimer);
        utilityTimer = setTimeout(() => {
            // Keep an open panel and keyboard navigation usable. Incoming chat
            // statuses do not restart the timer: only the user's activity does.
            const focused = document.activeElement;
            if (!panel.hidden || focused?.matches(':focus-visible') && focused.closest('.utility-control')) {
                revealUtilities(); return;
            }
            document.body.classList.add('utilities-idle');
        }, 3000);
    }
    for (const event of ['pointermove', 'pointerdown', 'touchstart', 'keydown', 'focusin'])
        document.addEventListener(event, revealUtilities, {passive: true});
    document.addEventListener('visibilitychange', () => { if (!document.hidden) revealUtilities(); });
    revealUtilities();

    function record(user) {
        const key = user.toLowerCase();
        if (!users.has(key)) {
            if (users.size >= 500) users.delete(users.keys().next().value);
            users.set(key, {user, queued: 0, started: 0, rejected: 0, controls: 0});
        }
        return users.get(key);
    }
    const notifier = {say(message) {
        element('notice').textContent = message;
        api.status(message, true);
        if (token && twitch) twitch.Say(message, channel);
        render();
    }};
    const {commandHandler, claimControl} = createCommandHandler(queue, notifier);
    const execute = executor.execute.bind(executor);
    executor.execute = async sequence => {
        active = sequence;
        record(sequence.owner).started++;
        render();
        try { await execute(sequence); }
        finally { active = null; render(); }
    };
    function receive(user, command, message, flags = {}, extra = {}) {
        const chat = new ChatMessage(user, `${command} ${message}`.trim(), command.toLowerCase(), flags, extra, message);
        const row = record(user);
        if (commandHandler.handle(chat)) { row.controls++; render(); return; }
        if (paused || !api.isConnected() || !claimControl.canExecute(chat) || queue.size >= 100) {
            row.rejected++; render(); return;
        }
        try {
            const sequence = parser.parse(chat);
            if (!sequence.isEmpty()) { row.queued++; queue.enqueue(sequence); }
        } catch (error) { row.rejected++; element('notice').textContent = `${user}: ${error.message}`; }
        render();
    }

    function render() {
        element('queue').textContent = `${active ? `Playing ${active.owner}: ${active.messageContent}` : 'Ready'} · ${queue.size} queued`;
        element('owner').textContent = claimControl.isActive() ? `Chat control: ${claimControl.getOwner()}` : 'Chat control: everyone';
        const body = element('users');
        body.replaceChildren();
        for (const row of [...users.values()].sort((a, b) => b.queued - a.queued)) {
            const tr = document.createElement('tr');
            for (const key of ['user', 'queued', 'started', 'rejected', 'controls']) {
                const td = document.createElement('td'); td.textContent = row[key]; tr.appendChild(td);
            }
            body.appendChild(tr);
        }
    }
    element('paused').addEventListener('change', event => {
        paused = event.target.checked;
        if (paused) queue.skipAll();
        api.status(paused ? 'Chat inputs paused' : 'Chat inputs enabled', true);
    });
    element('skip').addEventListener('click', () => { queue.skipCurrent(); render(); });
    element('clear').addEventListener('click', () => { queue.skipAll(); render(); });
    element('reset').addEventListener('click', () => { users.clear(); render(); });
    element('export').addEventListener('click', () => {
        const blob = new Blob([JSON.stringify({channel, users: [...users.values()]}, null, 2)], {type: 'application/json'});
        const url = URL.createObjectURL(blob), link = document.createElement('a');
        link.href = url; link.download = 'padforge-chat-statistics.json'; link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    });

    if (channel) {
        element('twitch').textContent = `Connecting to Twitch #${channel}…`;
        const script = document.createElement('script');
        script.src = '/js/comfy.min.js';
        script.onload = () => {
            twitch = window.ComfyJS;
            twitch.onCommand = receive;
            twitch.onConnected = () => { element('twitch').textContent = `Twitch #${channel} connected${token ? '' : ' (read only)'}`; };
            twitch.onError = error => { element('twitch').textContent = `Twitch: ${String(error?.message || error)}`; };
            twitch.onDisconnected = () => { element('twitch').textContent = 'Twitch disconnected'; queue.skipAll(); };
            // Register handlers first: fast connections must not drop early messages.
            if (token) twitch.Init(login, token, channel); else twitch.Init(channel);
        };
        script.onerror = () => { element('twitch').textContent = 'Could not load Twitch chat.'; };
        document.head.appendChild(script);
    }

    function clearPhysical() { api.clear('physical'); physicalCache.clear(); }
    function physicalInput(kind, code, value) {
        const key = `${kind}:${code}`;
        if (physicalCache.get(key) === value) return;
        physicalCache.set(key, value);
        api.input('physical', {type: 'input', kind, code, value});
    }
    function pads() { return [...(navigator.getGamepads?.() || [])].filter(Boolean); }
    function refreshPads() {
        const select = element('pad'), selected = select.value;
        select.replaceChildren(new Option('Automatic', ''));
        try { for (const pad of pads()) select.appendChild(new Option(pad.id, String(pad.index))); }
        catch (error) { element('physical').textContent = error.message; }
        select.value = [...select.options].some(o => o.value === selected) ? selected : '';
    }
    element('pad').addEventListener('change', () => { physicalIndex = null; clearPhysical(); });
    function tickPhysical() {
        if (document.hidden) { physicalFrame = null; return; }
        try {
            const available = pads();
            const selected = element('pad').value;
            const pad = selected === '' ? available.find(p => p.index === physicalIndex) || available[0] : available.find(p => p.index === Number(selected));
            if (!pad || physicalIndex !== pad.index) {
                clearPhysical(); physicalIndex = pad?.index ?? null;
            }
            element('physical').textContent = pad ? pad.id : 'Connect a gamepad and press a button to make it visible to the browser.';
            const rumble = !!(pad?.vibrationActuator || pad?.hapticActuators?.length);
            if (rumble !== tickPhysical.rumble) { tickPhysical.rumble = rumble; api.capabilities(rumble); }
            if (pad && api.isConnected()) {
                const pressed = i => Number(!!pad.buttons[i]?.pressed || (pad.buttons[i]?.value || 0) > 0.5);
                const buttonMap = {0: 0, 1: 1, 2: 2, 3: 3, 4: 4, 5: 5, 8: 6, 9: 7, 10: 8, 11: 9, 16: 10, 17: 16};
                for (const [index, slot] of Object.entries(buttonMap)) physicalInput('button', slot, pressed(Number(index)));
                for (const [index, axis] of [[6, 2], [7, 5]]) physicalInput('axis', axis, Math.round(Math.max(0, Math.min(1, pad.buttons[index]?.value || 0)) * 65535));
                for (const [index, axis] of [[0, 0], [1, 1], [2, 3], [3, 4]]) {
                    let value = Math.max(-1, Math.min(1, pad.axes[index] || 0));
                    if (Math.abs(value) < 0.08) value = 0;
                    physicalInput('axis', axis, value === 0 ? 32767 : Math.round((value + 1) * 32767.5));
                }
                const x = pressed(15) - pressed(14), y = pressed(13) - pressed(12);
                physicalInput('pov', 0, x || y ? Math.round((Math.atan2(x, -y) * 180 / Math.PI + 360) % 360 / 45) % 8 * 4500 : -1);
            }
        } catch (error) { clearPhysical(); element('physical').textContent = `Physical gamepad: ${error.message}`; }
        physicalFrame = requestAnimationFrame(tickPhysical);
    }
    if (params.get('physical') === '1') {
        if (typeof navigator.getGamepads !== 'function') {
            element('physical').textContent = 'This browser requires HTTPS (or localhost) to read a physical gamepad.';
        } else { refreshPads(); tickPhysical(); }
        window.addEventListener('gamepadconnected', refreshPads);
        window.addEventListener('gamepaddisconnected', event => {
            if (!event.gamepad || event.gamepad.index === physicalIndex) { clearPhysical(); physicalIndex = null; }
            refreshPads();
        });
    } else { element('pad').disabled = true; element('physical').textContent = 'Physical passthrough is off. Enable it on the integration setup page.'; }
    window.addEventListener('padforge-rumble', event => {
        if (physicalIndex === null || document.hidden || !api.isConnected()) return;
        try {
            const pad = pads().find(p => p.index === physicalIndex), actuator = pad?.vibrationActuator || pad?.hapticActuators?.[0];
            const strongMagnitude = Math.max(0, Math.min(1, (event.detail.left || 0) / 65535));
            const weakMagnitude = Math.max(0, Math.min(1, (event.detail.right || 0) / 65535));
            const result = actuator?.playEffect ? actuator.playEffect('dual-rumble', {duration: 200, strongMagnitude, weakMagnitude}) : actuator?.pulse?.(Math.max(strongMagnitude, weakMagnitude), 200);
            result?.catch(() => {});
        } catch { /* The pad may disconnect between enumeration and rumble. */ }
    });
    window.addEventListener('padforge-connection', () => { queue.skipAll(); clearPhysical(); tickPhysical.rumble = null; });
    document.addEventListener('visibilitychange', () => {
        if (document.hidden) { clearPhysical(); }
        else if (params.get('physical') === '1' && physicalFrame === null) tickPhysical();
    });
    window.addEventListener('pagehide', () => { queue.skipAll(); clearPhysical(); if (physicalFrame !== null) cancelAnimationFrame(physicalFrame); twitch?.Disconnect(); });
    render();
}
window.addEventListener('padforge-controller-ready', initialize);
if (window.PadForgeController) initialize();
