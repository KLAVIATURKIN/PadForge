// Touch, chat and physical input own independent states. Releasing one source
// must not release a button still held by another source.
export class InputMixer {
    constructor(emit, render = () => {}) {
        this.emit = emit;
        this.render = render;
        this.sources = new Map();
        this.output = new Map();
    }

    neutral(kind, code) { return kind === 'pov' ? -1 : kind === 'axis' && ![2, 5].includes(code) ? 32767 : 0; }

    input(source, message) {
        if (!this.sources.has(source)) this.sources.set(source, new Map());
        const key = `${message.kind}:${message.code}`;
        this.sources.get(source).set(key, message);
        this.combine(key, message.kind, message.code);
    }

    combine(key, kind, code) {
        const neutral = this.neutral(kind, code);
        const values = [...this.sources.values()].map(s => s.get(key)?.value ?? neutral);
        let value = neutral;
        if (kind === 'button' || kind === 'axis' && [2, 5].includes(code)) value = Math.max(neutral, ...values);
        else if (kind === 'axis') value = values.reduce((a, b) => Math.abs(b - neutral) > Math.abs(a - neutral) ? b : a, neutral);
        else if (kind === 'pov') {
            let x = 0, y = 0;
            for (const angle of values) if (angle >= 0) {
                x += Math.round(Math.sin(angle / 18000 * Math.PI));
                y -= Math.round(Math.cos(angle / 18000 * Math.PI));
            }
            value = x || y ? Math.round((Math.atan2(x, -y) * 180 / Math.PI + 360) % 360 / 45) % 8 * 4500 : -1;
        }
        const message = {type: 'input', kind, code, value};
        const changed = this.output.get(key)?.value !== value;
        this.output.set(key, message);
        this.render(message, this.output);
        if (changed) this.emit(message);
    }

    clear(source) {
        const previous = this.sources.get(source);
        this.sources.delete(source);
        if (previous) for (const [key, message] of previous) this.combine(key, message.kind, message.code);
    }

    resync() { for (const message of this.output.values()) this.emit(message); }
}

export function connectionUrl(location, params, clientId, layout, touchpad) {
    const remote = params.get('remote') === '1';
    const protocol = remote ? params.get('protocol') || (location.protocol === 'https:' ? 'wss:' : 'ws:') : location.protocol === 'https:' ? 'wss:' : 'ws:';
    if (!['ws:', 'wss:'].includes(protocol)) throw new Error('Select WS or WSS for the target.');
    const url = new URL(`${protocol}//${location.host}/ws`);
    if (remote) {
        const host = (params.get('host') || '').trim();
        if (!host || /[\s\/?#@]/.test(host)) throw new Error('Enter a target hostname or IP address.');
        const port = params.get('port');
        if (port && (!/^\d+$/.test(port) || Number(port) < 1 || Number(port) > 65535)) throw new Error('Enter a port from 1 to 65535.');
        url.hostname = host.includes(':') && !host.startsWith('[') ? `[${host}]` : host;
        url.port = params.get('port') || location.port;
        if (url.hostname !== host.toLowerCase() && url.hostname !== `[${host}]`) throw new Error('Invalid target hostname.');
        if (location.protocol === 'https:' && protocol === 'ws:') throw new Error('An HTTPS page needs a WSS target. Open this page over HTTP to use WS.');
    }
    url.searchParams.set('id', clientId);
    url.searchParams.set('layout', layout);
    if (touchpad) url.searchParams.set('touchpad', '1');
    const name = params.get('name')?.trim();
    if (name) url.searchParams.set('name', name.slice(0, 80));
    const code = params.get(remote ? 'targetCode' : 'code');
    if (code) url.searchParams.set('code', code);
    return url.href;
}

export class ChatGamepad {
    constructor(api) {
        this.api = api;
        this.buttons = {a: 0, b: 1, x: 2, y: 3, lb: 4, rb: 5, back: 6, select: 6, start: 7, ls: 8, rs: 9, home: 10, tpad: 16};
        this.directions = {u: 'up', d: 'down', l: 'left', r: 'right', up: 'up', down: 'down', left: 'left', right: 'right'};
        this.held = new Set();
    }
    validInputs() { return [...Object.keys(this.buttons), ...Object.keys(this.directions), 'lt', 'rt']; }
    press(input) { this.set(input, true); }
    release(input) { this.set(input, false); }
    set(input, pressed) {
        if (this.directions[input]) {
            const direction = this.directions[input];
            if (pressed) this.held.add(direction); else this.held.delete(direction);
            const x = Number(this.held.has('right')) - Number(this.held.has('left'));
            const y = Number(this.held.has('down')) - Number(this.held.has('up'));
            const value = x || y ? Math.round((Math.atan2(x, -y) * 180 / Math.PI + 360) % 360 / 45) % 8 * 4500 : -1;
            this.api.input('chat', {type: 'input', kind: 'pov', code: 0, value});
        } else if (input === 'lt' || input === 'rt') {
            this.api.input('chat', {type: 'input', kind: 'axis', code: input === 'lt' ? 2 : 5, value: pressed ? 65535 : 0});
        } else if (this.buttons[input] !== undefined) {
            this.api.input('chat', {type: 'input', kind: 'button', code: this.buttons[input], value: pressed ? 1 : 0});
        }
    }
    reset() { this.held.clear(); this.api.clear('chat'); }
}
