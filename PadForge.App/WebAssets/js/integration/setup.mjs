import {connectionUrl} from './core.mjs';

const $ = id => document.getElementById(id);
let selected = 'xbox360';
let layouts = [];
$('port').value = location.port || (location.protocol === 'https:' ? '443' : '80');
$('protocol').value = location.protocol === 'https:' ? 'wss:' : 'ws:';
$('advanced').addEventListener('change', () => { $('advanced-fields').hidden = !$('advanced').checked; });
for (const [toggle, fields] of [['chat', ['channel', 'login', 'token']], ['remote', ['host', 'port', 'protocol', 'targetCode']]]) {
    $(toggle).addEventListener('change', () => { for (const id of fields) $(id).disabled = !$(toggle).checked; });
}

const choices = new Map();
const finishes = new Map();
const previews = new Map();
const finishNames = { '': 'Default', wlg: 'WLG', GlacierWhite: 'Glacier White', MagmaRed: 'Magma Red', MidnightBlue: 'Midnight Blue', CosmicRed: 'Cosmic Red', GalacticPurple: 'Galactic Purple', NovaPink: 'Nova Pink', StarlightBlue: 'Starlight Blue', DeepPink: 'Deep Pink', ElectricVolt: 'Electric Volt', PulseRed: 'Pulse Red', ShockBlue: 'Shock Blue' };
const finishColors = { '': '#c9cbd3', wlg: '#bb477b', GlacierWhite: '#e3e7ec', Gold: '#bda468', MagmaRed: '#ad304b', MidnightBlue: '#334e79', CosmicRed: '#a33355', GalacticPurple: '#714b94', Midnight: '#252831', NovaPink: '#d177a7', StarlightBlue: '#6aade3', Carbon: '#30343c', DeepPink: '#e464a4', ElectricVolt: '#d0ee4b', PulseRed: '#ce3a4b', ShockBlue: '#4381ce' };

async function preview(layout, finish) {
    const choice = choices.get(layout.type);
    if (!finish) { choice.image.src = '/img/' + layout.basePath; return; }
    const key = layout.type + ':' + finish;
    try {
        if (!previews.has(key)) previews.set(key, fetch('/api/layout?type=' + encodeURIComponent(layout.type) + '&finish=' + encodeURIComponent(finish))
            .then(response => { if (!response.ok) throw new Error('Preview unavailable'); return response.json(); })
            .then(result => result.basePath));
        const basePath = await previews.get(key);
        if (finishes.get(layout.type) === finish) choice.image.src = '/img/' + basePath;
    } catch { previews.delete(key); }
}

function choose(layout, finish = finishes.get(layout.type) || '') {
    selected = layout.type;
    finishes.set(selected, finish);
    $('finish').value = finish;
    for (const [type, choice] of choices) {
        choice.group.classList.toggle('selected', type === selected);
        choice.card.setAttribute('aria-pressed', String(type === selected));
        for (const option of choice.colors.children)
            option.setAttribute('aria-pressed', String(option.dataset.finish === (finishes.get(type) || '')));
    }
    preview(layout, finish);
}

async function load() {
    const response = await fetch('/api/layouts', {cache: 'no-store'});
    if (!response.ok) throw new Error('Could not load controller layouts.');
    layouts = await response.json();
    for (const layout of layouts) {
        const group = document.createElement('div'); group.className = 'layout-choice';
        const card = document.createElement('button');
        card.type = 'button'; card.className = 'card'; card.dataset.layout = layout.type;
        const image = document.createElement('img'); image.src = '/img/' + layout.basePath; image.alt = '';
        const name = document.createElement('span'); name.className = 'card-name'; name.textContent = layout.name;
        card.append(image, name);
        card.addEventListener('click', () => choose(layout));
        const rail = document.createElement('div'); rail.className = 'finish-group';
        rail.setAttribute('role', 'group'); rail.setAttribute('aria-label', layout.name + ' colors');
        const title = document.createElement('span'); title.className = 'finish-title'; title.textContent = 'Color';
        const colors = document.createElement('div'); colors.className = 'finish-options';
        for (const finish of ['', ...layout.finishes]) {
            const option = document.createElement('button'); option.type = 'button'; option.className = 'finish-option'; option.dataset.finish = finish;
            option.setAttribute('aria-label', layout.name + ': ' + (finishNames[finish] || finish));
            const swatch = document.createElement('span'); swatch.className = 'swatch'; swatch.style.background = finishColors[finish] || '#9c8bb3'; swatch.setAttribute('aria-hidden', 'true');
            const label = document.createElement('span'); label.textContent = finishNames[finish] || finish;
            option.append(swatch, label); option.addEventListener('click', () => choose(layout, finish)); colors.appendChild(option);
        }
        rail.append(title, colors); group.append(card, rail);
        choices.set(layout.type, {group, card, image, colors});
        $('layouts').appendChild(group);
    }
    choose(layouts.find(l => l.type === selected) || layouts[0]);
    $('status').textContent = ''; $('connect').disabled = false;
}
$('connect').addEventListener('click', () => {
    try {
        const query = new URLSearchParams({integration: '1', layout: selected});
        for (const key of ['name', 'finish']) if ($(key).value.trim()) query.set(key, $(key).value.trim());
        if ($('physical').checked) query.set('physical', '1');
        const advanced = $('advanced').checked;
        if (advanced && $('chat').checked) {
            const channel = $('channel').value.trim().replace(/^#/, '');
            if (!/^[a-zA-Z0-9_]{1,25}$/.test(channel)) throw new Error('Enter a Twitch channel name.');
            query.set('channel', channel);
            if ($('login').value.trim()) query.set('login', $('login').value.trim());
        }
        if (advanced && $('remote').checked) {
            query.set('remote', '1');
            for (const key of ['host', 'port', 'protocol', 'targetCode']) if ($(key).value.trim()) query.set(key, $(key).value.trim());
        }
        // Preserve a same-instance access code when the setup was opened by QR.
        const code = new URLSearchParams(location.search).get('code');
        if (code && query.get('remote') !== '1') query.set('code', code);
        connectionUrl(location, query, 'validate', selected, false);
        const url = new URL('/controller.html', location.href);
        url.search = query.toString();
        if (advanced && $('chat').checked && $('token').value.trim()) url.hash = new URLSearchParams({token: $('token').value.trim()}).toString();
        location.assign(url.href);
    } catch (error) { $('status').textContent = error.message; }
});
load().catch(error => { $('status').textContent = error.message; });
