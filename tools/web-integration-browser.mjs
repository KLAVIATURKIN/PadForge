// Offline browser smoke test. No npm dependencies, Twitch login, drivers or
// running PadForge instance needed. EDGE_PATH can override the browser path.
import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {fileURLToPath} from 'node:url';
import {spawn} from 'node:child_process';
import assert from 'node:assert/strict';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const assets = path.join(root, 'PadForge.App/WebAssets');
const requests = [], errors = [];
const modelSource = await fs.readFile(path.join(root, 'PadForge.App/Models2D/ControllerOverlayLayout.cs'), 'utf8');
const serverSource = await fs.readFile(path.join(root, 'PadForge.App/Services/WebControllerServer.cs'), 'utf8');
const catalog = serverSource.split('new() { TypeKey = ').slice(1).map(segment => {
    const type = segment.match(/^"([^"]+)"/)[1], name = segment.match(/NameStem = "([^"]+)"/)[1];
    const model = segment.match(/BasePath = (\w+)\.BasePath/)[1];
    const basePath = modelSource.match(new RegExp('public static class ' + model + '\\b[\\s\\S]*?BasePath = "([^"]+)"'))[1];
    const finishList = segment.match(/Finishes = new\[\] \{([^}]+)\}/)?.[1] || '';
    return {type, name, basePath, finishes: [...finishList.matchAll(/"([^"]+)"/g)].map(match => match[1])};
});
const layout = {
    baseWidth: 1545, baseHeight: 955, basePath: '2DModels/XBOX360/XB360_base.png', stickMaxTravel: 25,
    overlays: [
        {image: '2DModels/XBOX360/XB360_A_Button.png', target: 'ButtonA', type: 'button', x: 1200, y: 380, w: 90, h: 90, inputKind: 'button', inputCode: 0},
        {image: '2DModels/XBOX360/XB360_LeftStick.png', target: 'LeftThumbRing', type: 'stickRing', x: 240, y: 230, w: 200, h: 200},
        {image: '2DModels/XBOX360/XB360_RightStick.png', target: 'RightThumbRing', type: 'stickRing', x: 830, y: 450, w: 200, h: 200}
    ]
};
const server = http.createServer(async (request, response) => {
    requests.push(request.url);
    const url = new URL(request.url, 'http://localhost');
    try {
        if (url.pathname === '/api/layouts') { response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify(catalog)); return; }
        if (url.pathname === '/api/layout') {
            const selected = catalog.find(entry => entry.type === url.searchParams.get('type')) || catalog[0];
            const finish = url.searchParams.get('finish');
            const basePath = finish && selected.finishes.includes(finish) ? selected.basePath.replace('.png', `_${finish}.png`) : selected.basePath;
            response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify({...layout, basePath})); return;
        }
        if (url.pathname === '/api/info') { response.setHeader('Content-Type', 'application/json'); response.end('{"secureUrl":null}'); return; }
        if (url.pathname === '/js/comfy.min.js') {
            response.setHeader('Content-Type', 'application/javascript');
            response.end('window.ComfyJS={Init(){this.onConnected?.()},Say(text){window.botMessages.push(text)},Disconnect(){}}'); return;
        }
        const image = url.pathname.startsWith('/img/');
        const relative = image ? `PadForge.App/${url.pathname.slice(5)}` : `PadForge.App/WebAssets/${url.pathname === '/' ? 'index.html' : url.pathname.slice(1)}`;
        const target = path.resolve(root, relative);
        if (!target.startsWith(root + path.sep)) throw Error('Bad path');
        let content;
        try { content = await fs.readFile(target); }
        catch (error) {
            if (!image) throw error;
            // Only fixture overlay filenames differ between pack revisions.
            content = await fs.readFile(path.join(root, 'PadForge.App/2DModels/XBOX360/XB360_base.png'));
        }
        response.setHeader('Content-Type', image ? 'image/png' : /\.m?js$/.test(target) ? 'application/javascript' : target.endsWith('.css') ? 'text/css' : 'text/html; charset=utf-8');
        response.end(content);
    } catch { response.statusCode = 404; response.end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const profile = await fs.mkdtemp(path.join(os.tmpdir(), 'padforge-web-smoke-'));
const edge = spawn(process.env.EDGE_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', [
    '--headless=new', '--disable-gpu', '--no-first-run', '--disable-background-networking',
    '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'
], {windowsHide: true, stdio: 'ignore'});
let socket, commandId = 0;
const pending = new Map();
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(predicate, description) {
    for (let attempt = 0; attempt < 150; attempt++) { if (await predicate()) return; await sleep(100); }
    throw Error(`Timed out: ${description}`);
}
function cdp(method, params = {}) {
    const id = ++commandId;
    return new Promise((resolve, reject) => { pending.set(id, {resolve, reject}); socket.send(JSON.stringify({id, method, params})); });
}
async function evaluate(expression) {
    const response = await cdp('Runtime.evaluate', {expression, returnByValue: true, awaitPromise: true});
    if (response.exceptionDetails) throw Error(response.exceptionDetails.exception?.description || response.exceptionDetails.text);
    return response.result.value;
}
async function screenshot(name) {
    const metrics = await cdp('Page.getLayoutMetrics');
    const size = metrics.cssContentSize;
    const picture = await cdp('Page.captureScreenshot', {captureBeyondViewport: true, clip: {x: 0, y: 0, width: size.width, height: Math.min(size.height, 2600), scale: 1}});
    await fs.writeFile(path.join(root, `artifacts/${name}.png`), Buffer.from(picture.data, 'base64'));
}
try {
    let port;
    await until(async () => { try { port = (await fs.readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; return !!port; } catch { return false; } }, 'Edge debugging port');
    const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    socket = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
    await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
    socket.onmessage = event => {
        const message = JSON.parse(event.data);
        if (message.id) {
            const promise = pending.get(message.id); pending.delete(message.id);
            if (message.error) promise?.reject(Error(message.error.message)); else promise?.resolve(message.result);
        } else if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description || message.params.exceptionDetails.text);
    };
    await cdp('Page.enable'); await cdp('Runtime.enable');
    await cdp('Emulation.setDeviceMetricsOverride', {width: 1280, height: 720, deviceScaleFactor: 1, mobile: false});
    await cdp('Page.addScriptToEvaluateOnNewDocument', {source: `
        sessionStorage.setItem('_ctrl_reloaded', '1');
        window.testPads=[]; window.testSent=[]; window.testSockets=[]; window.botMessages=[];
        Object.defineProperty(navigator,'getGamepads',{value:()=>window.testPads});
        class TestSocket {
            static OPEN=1; static CONNECTING=0;
            constructor(url){this.url=url;this.readyState=0;testSockets.push(this);setTimeout(()=>{this.readyState=1;this.onopen?.();this.onmessage?.({data:JSON.stringify({type:'connected',name:'Smoke pad'})})},20)}
            send(text){testSent.push(JSON.parse(text))}
            close(){this.readyState=3;this.onclose?.({code:1000})}
        }
        window.WebSocket=TestSocket;
    `});
    await cdp('Page.navigate', {url: `${origin}/integration.html?code=ABCDEFGHJK`});
    await until(() => evaluate(`document.querySelectorAll('#layouts .card').length===${catalog.length}`), 'integration layout');
    assert.equal(await evaluate("getComputedStyle(document.getElementById('layouts')).gridTemplateColumns.split(' ').length"), 4);
    assert.equal(await evaluate("document.getElementById('client')===null"), true);
    assert.equal(await evaluate("document.getElementById('name').placeholder"), 'Name in PadForge');
    await evaluate("document.querySelector('.physical-title').click()");
    assert.equal(await evaluate("document.getElementById('physical').checked"), true);
    await evaluate("document.querySelector('.physical-title').click()");
    assert.equal(await evaluate("document.getElementById('physical').checked"), false);
    assert.equal(await evaluate("document.querySelector('.finish-option').closest('.card')===null"), true);
    await evaluate("document.querySelector('[data-layout=xbox360]').parentElement.querySelector('[data-finish=wlg]').click()");
    await until(() => evaluate("document.querySelector('[data-layout=xbox360] img').src.endsWith('_wlg.png')"), 'live color preview');
    assert.equal(await evaluate("document.getElementById('remote').checked"), false);
    assert.equal(await evaluate("document.getElementById('host').disabled"), true);
    if (process.argv.includes('--screenshots')) {
        await fs.mkdir(path.join(root, 'artifacts'), {recursive: true});
        await screenshot('web-integration-setup');
    }
    await cdp('Emulation.setDeviceMetricsOverride', {width: 390, height: 844, deviceScaleFactor: 1, mobile: false});
    assert.equal(await evaluate('document.documentElement.scrollWidth<=innerWidth'), true);
    assert.equal(await evaluate("getComputedStyle(document.getElementById('layouts')).gridTemplateColumns.split(' ').length"), 2);
    if (process.argv.includes('--screenshots')) await screenshot('web-integration-setup-mobile');
    await cdp('Emulation.setDeviceMetricsOverride', {width: 1280, height: 720, deviceScaleFactor: 1, mobile: false});
    await evaluate(`document.getElementById('name').value='Smoke pad';document.getElementById('physical').checked=true;document.getElementById('advanced').checked=true;document.getElementById('advanced').dispatchEvent(new Event('change'));document.getElementById('chat').checked=true;document.getElementById('chat').dispatchEvent(new Event('change'));document.getElementById('channel').value='smoke';document.getElementById('token').value='secret-test-token';document.getElementById('finish').value='wlg';document.getElementById('connect').click();`);
    await until(() => evaluate("!!window.PadForgeController?.integrationStarted && !!window.ComfyJS"), 'controller integration');
    assert.equal(await evaluate('new URL(testSockets[0].url).host'), new URL(origin).host);
    assert.equal(await evaluate("new URL(testSockets[0].url).searchParams.get('name')"), 'Smoke pad');
    assert.equal(await evaluate("new URL(testSockets[0].url).searchParams.get('code')"), 'ABCDEFGHJK');
    assert.equal(await evaluate("new URL(location.href).searchParams.get('finish')"), 'wlg');
    assert.equal(await evaluate("document.body.classList.contains('integration-mode')"), true);
    assert.equal(await evaluate("getComputedStyle(document.getElementById('statusBar')).textAlign"), 'center');
    assert.ok(await evaluate("parseFloat(getComputedStyle(document.getElementById('statusBar')).fontSize)>=16"));
    assert.equal(await evaluate("!!document.querySelector('button.utility-control')"), true);
    await until(() => evaluate("document.body.classList.contains('utilities-idle')"), 'idle toolbar');
    await until(() => evaluate("getComputedStyle(document.getElementById('fsMount')).visibility==='hidden'"), 'toolbar fade completes');
    assert.equal(await evaluate("[...document.querySelectorAll('.utility-control')].every(control=>getComputedStyle(control).visibility==='hidden')"), true);
    await evaluate("document.dispatchEvent(new PointerEvent('pointermove'))");
    assert.equal(await evaluate("document.body.classList.contains('utilities-idle')"), false);
    await until(() => evaluate("document.body.classList.contains('utilities-idle')"), 'toolbar hides again');
    await evaluate("document.dispatchEvent(new Event('touchstart'))");
    assert.equal(await evaluate("document.body.classList.contains('utilities-idle')"), false);
    await evaluate("ComfyJS.onCommand('alice','a','',{},{});");
    await until(() => evaluate("testSent.some(m=>m.kind==='button'&&m.code===0&&m.value===1)"), 'chat button');
    assert.equal(await evaluate("document.querySelector('img[data-target=ButtonA]').classList.contains('active')"), true);
    assert.match(await evaluate("document.getElementById('statusBar').textContent"), /alice/);
    await until(() => evaluate("testSent.some(m=>m.kind==='button'&&m.code===0&&m.value===0)"), 'chat release');
    await evaluate("testPads=[{index:0,id:'Test physical pad',axes:[0.7,0,0,0],buttons:Array.from({length:18},(_,i)=>({pressed:i===0,value:i===0?1:0})),vibrationActuator:{playEffect(type,parameters){window.testRumble={type,parameters};return Promise.resolve()}}}];window.dispatchEvent(new Event('gamepadconnected'));");
    await until(() => evaluate("document.querySelector('img[data-target=ButtonA]').classList.contains('active')"), 'physical overlay');
    await evaluate("testSockets[0].onmessage({data:JSON.stringify({type:'rumble',left:65535,right:32767})});");
    assert.equal(await evaluate('testRumble.parameters.strongMagnitude'), 1);
    await evaluate("ComfyJS.onCommand('alice','a','',{},{});");
    await sleep(300);
    assert.equal(await evaluate("document.querySelector('img[data-target=ButtonA]').classList.contains('active')"), true);
    await evaluate("testPads=[];window.dispatchEvent(new Event('gamepaddisconnected'));");
    await until(() => evaluate("!document.querySelector('img[data-target=ButtonA]').classList.contains('active')"), 'physical release');
    await evaluate("Object.defineProperty(document,'hidden',{configurable:true,get:()=>true});document.dispatchEvent(new Event('visibilitychange'));ComfyJS.onCommand('background','a','',{},{});");
    await until(() => evaluate("document.querySelector('img[data-target=ButtonA]').classList.contains('active')"), 'background chat input');
    await until(() => evaluate("!document.querySelector('img[data-target=ButtonA]').classList.contains('active')"), 'background chat release');
    await evaluate("Object.defineProperty(document,'hidden',{configurable:true,get:()=>false});document.dispatchEvent(new Event('visibilitychange'));");
    await evaluate("ComfyJS.onCommand('mod','claim','@alice',{mod:true},{});ComfyJS.onCommand('bob','a','',{},{});document.querySelector('#fsMount button:last-child').click();");
    assert.match(await evaluate("document.getElementById('integration-users').textContent"), /alice/);
    assert.match(await evaluate("document.getElementById('integration-users').textContent"), /bob/);
    assert.match(await evaluate("document.getElementById('integration-owner').textContent"), /alice/);
    assert.ok(!requests.some(url => url.includes('secret-test-token')));
    if (process.argv.includes('--screenshots'))
        await fs.writeFile(path.join(root, 'artifacts/web-integration-controller.png'), Buffer.from((await cdp('Page.captureScreenshot')).data, 'base64'));
    await cdp('Page.addScriptToEvaluateOnNewDocument', {source: "Object.defineProperty(window,'isSecureContext',{configurable:true,value:false});Object.defineProperty(navigator,'maxTouchPoints',{configurable:true,value:1});"});
    await cdp('Page.navigate', {url: `${origin}/controller.html?integration=1&remote=1&host=other.local&port=9123&protocol=ws:&name=Remote`});
    await until(() => evaluate("!!window.PadForgeController?.integrationStarted && testSockets[0]?.readyState===1"), 'remote controller');
    assert.equal(await evaluate('new URL(testSockets[0].url).host'), 'other.local:9123');
    assert.equal(await evaluate("new URL(testSockets[0].url).searchParams.get('name')"), 'Remote');
    assert.equal(await evaluate("document.querySelector('a.utility-control').textContent"), 'Motion needs HTTPS');
    await cdp('Page.navigate', {url: `${origin}/controller.html?layout=xbox360`});
    await until(() => evaluate("!!window.PadForgeController && testSockets[0]?.readyState===1"), 'standard controller');
    assert.equal(await evaluate("document.body.classList.contains('integration-mode')"), false);
    assert.equal(await evaluate("document.getElementById('integration-panel')===null"), true);
    assert.deepEqual(errors, []);
    console.log('Browser smoke passed: setup, default target, name, access code, WLG selection, chat inputs, physical overlays, shared holds, moderation, statistics, token isolation.');
} finally {
    if (socket?.readyState === WebSocket.OPEN) { cdp('Browser.close').catch(() => {}); await sleep(700); socket.close(); }
    edge.kill(); server.close();
    const resolved = path.resolve(profile), tempRoot = path.resolve(os.tmpdir()) + path.sep;
    if (!resolved.startsWith(tempRoot) || !path.basename(resolved).startsWith('padforge-web-smoke-')) throw Error('Unexpected browser profile path');
    try { await fs.rm(resolved, {recursive: true, force: true}); } catch { /* Edge may still be releasing its profile. */ }
}
