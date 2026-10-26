import {test} from 'node:test';
import assert from 'node:assert/strict';
import {InputMixer, ChatGamepad, connectionUrl} from '../PadForge.App/WebAssets/js/integration/core.mjs';
import {CommandParser} from '../PadForge.App/WebAssets/js/integration/input/CommandParser.mjs';
import {CommandExecutor} from '../PadForge.App/WebAssets/js/integration/input/CommandExecutor.mjs';
import {InputCommandQueue} from '../PadForge.App/WebAssets/js/integration/input/InputCommandQueue.mjs';
import {ChatMessage} from '../PadForge.App/WebAssets/js/integration/model/ChatMessage.mjs';
import {createCommandHandler} from '../PadForge.App/WebAssets/js/integration/command/CommandHandler.mjs';

function rig() {
    const sent = [];
    const mixer = new InputMixer(message => sent.push(message));
    const api = {input: (source, message) => mixer.input(source, message), clear: source => mixer.clear(source), status() {}};
    return {sent, mixer, api, gamepad: new ChatGamepad(api)};
}
const message = (content, user = 'alice', command = 'a', flags = {}) => new ChatMessage(user, content, command, flags, {}, content.slice(command.length).trim());
const input = (kind, code, value) => ({type: 'input', kind, code, value});

test('a chat release preserves a physical button and a physical release preserves touch', () => {
    const {mixer, sent, gamepad} = rig();
    mixer.input('physical', input('button', 0, 1));
    gamepad.press('a'); gamepad.release('a');
    assert.equal(sent.at(-1).value, 1);
    mixer.input('touch', input('button', 0, 1)); mixer.clear('physical');
    assert.equal(sent.at(-1).value, 1);
    mixer.clear('touch'); assert.equal(sent.at(-1).value, 0);
});

test('chat sequence reset releases only chat triggers and dpad', () => {
    const {mixer, gamepad} = rig();
    mixer.input('physical', input('axis', 2, 20000));
    gamepad.press('lt'); gamepad.press('u'); gamepad.press('r');
    assert.equal(mixer.output.get('pov:0').value, 4500);
    gamepad.reset();
    assert.equal(mixer.output.get('axis:2').value, 20000);
    assert.equal(mixer.output.get('pov:0').value, -1);
});

test('stick arbitration, opposite dpad directions and reconnection resync', () => {
    const {mixer, sent} = rig();
    mixer.input('touch', input('axis', 0, 40000));
    mixer.input('physical', input('axis', 0, 60000));
    assert.equal(sent.at(-1).value, 60000);
    mixer.clear('physical'); assert.equal(sent.at(-1).value, 40000);
    mixer.input('touch', input('pov', 0, 0)); mixer.input('physical', input('pov', 0, 18000));
    assert.equal(sent.at(-1).value, -1);
    const before = sent.length; mixer.resync(); assert.equal(sent.length, before + mixer.output.size);
});

test('default target uses the page hostname, protocol and port; hidden remote fields are ignored', () => {
    const location = {host: 'padforge.local:9123', port: '9123', protocol: 'http:'};
    const url = new URL(connectionUrl(location, new URLSearchParams({host: 'elsewhere', port: '8080', name: 'Chat pad', code: 'ABCDEFGHJK'}), 'client', 'dualsense', true));
    assert.equal(url.host, location.host); assert.equal(url.protocol, 'ws:');
    assert.equal(url.searchParams.get('name'), 'Chat pad'); assert.equal(url.searchParams.get('code'), 'ABCDEFGHJK');
    assert.equal(url.searchParams.get('touchpad'), '1');
});

test('remote target receives only its access code and preserves client identity', () => {
    const url = new URL(connectionUrl({host: 'local:8080', port: '8080', protocol: 'http:'}, new URLSearchParams({remote: '1', host: 'other.local', port: '9090', protocol: 'ws:', code: 'LOCAL', targetCode: 'REMOTE'}), 'stable', 'xbox360', false));
    assert.equal(url.host, 'other.local:9090'); assert.equal(url.searchParams.get('code'), 'REMOTE');
    assert.equal(url.searchParams.get('id'), 'stable'); assert.ok(!url.href.includes('LOCAL'));
});

test('invalid ports and mixed-content remote connections produce useful errors', () => {
    const location = {host: 'local:8080', port: '8080', protocol: 'https:'};
    for (const port of ['0', '65536', 'hello']) assert.throws(() => connectionUrl(location, new URLSearchParams({remote: '1', host: 'other', port, protocol: 'wss:'}), 'a', 'xbox360', false), /port/);
    assert.throws(() => connectionUrl(location, new URLSearchParams({remote: '1', host: 'other', protocol: 'ws:'}), 'a', 'xbox360', false), /HTTPS/);
});

test('remote IPv6 hosts accept bare and bracketed addresses', () => {
    for (const host of ['::1', '[::1]']) {
        const url = new URL(connectionUrl({host: 'local:8080', protocol: 'http:', port: '8080'}, new URLSearchParams({remote: '1', host, port: '9123'}), 'a', 'xbox360', false));
        assert.equal(url.hostname, '[::1]'); assert.equal(url.port, '9123');
    }
});

test('ForgePlay parser retains simultaneous inputs, holds, releases, delays and groups', () => {
    const parser = new CommandParser(new ChatGamepad({}).validInputs());
    const sequence = parser.parse(message('_a+b500ms #20ms -a [x y]*3'));
    assert.equal(sequence.owner, 'alice'); assert.equal(sequence.steps.length, 9);
    assert.equal(sequence.steps[0].actions[0].actionType, 'hold');
    assert.equal(sequence.steps[0].actions[1].duration, 500);
    assert.equal(sequence.steps[1].delay, 20);
    assert.equal(sequence.steps[2].actions[0].actionType, 'release');
});

test('malformed input and unbounded expansions are rejected', () => {
    const parser = new CommandParser(['a']);
    for (const command of ['unknown', '[a', '[a]*99999999', '[[a]*1000]*1000']) assert.throws(() => parser.parse(message(command)));
});

test('executing and aborting a real chat sequence neutralizes chat without dropping physical holds', async () => {
    const {mixer, gamepad} = rig();
    globalThis.window = {PadForgeController: {status() {}}};
    mixer.input('physical', input('button', 0, 1));
    const executor = new CommandExecutor(gamepad), parser = new CommandParser(gamepad.validInputs());
    const execution = executor.execute(parser.parse(message('a10s')));
    assert.equal(executor.skipCurrent(), true);
    await execution;
    assert.equal(mixer.output.get('button:0').value, 1);
    mixer.clear('physical'); assert.equal(mixer.output.get('button:0').value, 0);
});

test('claim moderation gates other users and moderators can force release', () => {
    const notices = [], {claimControl, commandHandler} = createCommandHandler({}, {say: value => notices.push(value)});
    commandHandler.handle(new ChatMessage('mod', 'claim @alice', 'claim', {mod: true}, {}, '@alice'));
    assert.equal(claimControl.getOwner(), 'alice');
    assert.equal(claimControl.canExecute(message('a', 'alice')), true);
    assert.equal(claimControl.canExecute(message('a', 'bob')), false);
    commandHandler.handle(new ChatMessage('mod', 'release @alice', 'release', {mod: true}, {}, '@alice'));
    assert.equal(claimControl.isActive(), false); assert.ok(notices.length);
});

test('moderator skipAll aborts execution and clears waiting sequences', async () => {
    const {gamepad} = rig(); globalThis.window = {PadForgeController: {status() {}}};
    const executor = new CommandExecutor(gamepad), queue = new InputCommandQueue(executor), parser = new CommandParser(gamepad.validInputs());
    queue.enqueue(parser.parse(message('a10s'))); queue.enqueue(parser.parse(message('b10s')));
    const {commandHandler} = createCommandHandler(queue, {say() {}});
    commandHandler.handle(new ChatMessage('mod', 'skipAll', 'skipall', {mod: true}, {}, ''));
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(queue.size, 0); assert.equal(queue.isRunning, false);
});
