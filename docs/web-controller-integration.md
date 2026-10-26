# Web Controller integration

Everything is served by PadForge's existing Web Controller server and embedded
in the normal application build. ForgePlay.exe, Node.js and a second HTTP server
are not needed to use the integration.

Enable **Web Controller** on Dashboard, then scan the **integration setup** QR
code. The original QR code and landing page still open the standard controller
picker. The integration page is also linked from that picker.

Choose a layout and its attached color option, enter an optional **Controller Name**, and choose
whether to pass through a physical gamepad connected to the browser's device.
Press a physical button to make the pad available to the browser. An Integration
button on the controller opens its pad selector, chat controls and statistics.
Physical gamepads require a browser that exposes the Gamepad API; use HTTPS or
localhost when the browser restricts it to secure pages.

WLG artwork is available for DualSense, Xbox 360 and Xbox Series, both in the
standard picker and in integration setup. The controller page keeps PadForge's
current touch, motion, lightbar, player indicator and fullscreen features.
In integration mode, the status bar stays visible above the controls and shows
the user and input sequence currently playing in larger, centered text. Utility
buttons fade after three seconds of inactivity and reappear on pointer movement,
touch or keyboard activity. Open integration controls and keyboard focus keep
the toolbar available. The setup displays four layout groups per desktop row,
with a color rail attached to each preview, and two groups per row on mobile.

Named devices retain their controller identity, for example
`Xbox 360 Web Controller: Living Room`.

## Twitch

Under **Advanced setup**, enable **Twitch chat inputs** and enter a channel.
Without a token, the page reads chat and applies commands without sending chat
replies. To enable replies, enter the bot login and its OAuth token. Tokens are
passed in the browser URL fragment, which is not sent to PadForge's HTTP server,
and are not stored in application settings.

Inputs are queued and executed one sequence at a time. Examples:

| Command | Behavior |
| --- | --- |
| `!a` | Press A for 200 ms |
| `!a+b` | Press both buttons together |
| `!a500ms` or `!a1s` | Press for the specified duration |
| `!_a` | Hold A until released or the sequence ends |
| `!-a` | Release A |
| `!a #500ms b` | Press A, wait, then press B |
| `![a b]*3` | Repeat the group three times |
| `![a+b]500ms` | Apply a duration to grouped actions |

Inputs: `a b x y lb rb lt rt back select start ls rs home tpad`, plus
`up down left right` (or `u d l r`). Touchpad click takes effect on layouts that
have it. Physical sticks retain analog movement.

The claim, release and skip behavior is adapted from the supplied ForgePlay
source, including its two-user confirmations:

| Command | Behavior |
| --- | --- |
| `!claim` | Request exclusive control for five minutes |
| `!claim @user`, `!accept @user` | Vote for a claim; a moderator can assign immediately |
| `!claim status` | Report ownership and pending requests |
| `!release` | Release your claim |
| `!release @user` | A moderator releases that user's claim |
| `!skip` | Vote to skip the current sequence; moderators act immediately |
| `!skipAll` | Also clear waiting sequences |

The browser host can pause chat, skip the current sequence or clear the queue
using the Integration panel. Per-user statistics count queued, started and
rejected sequences and moderation commands. They last for the controller page's
session and can be exported as JSON or reset. Browser navigation starts fresh.

Chat, touch and physical inputs have independent state. A chat sequence ending
cannot release a button still held physically. Disconnecting clears integration input and cancels queued chat work. Hiding
the page releases touch and physical input; Twitch chat continues while the
page remains open and connected. Closing or navigating away cancels chat work.

## Connecting to another PadForge instance

By default, the controller uses the hostname, port and HTTP/HTTPS protocol of
the page serving it. Under **Advanced setup**, explicitly enable **Connect to a
different PadForge instance** to enter a host, port, WS/WSS protocol and optional
target access code. The selected target must have its Web Controller enabled.

An HTTPS page needs a WSS target. Open the setup page over HTTP when targeting
an HTTP/WS instance. The layout artwork and setup remain served by the original
instance; controller input and feedback use the selected WebSocket target.

## Source and verification

The command parser, executor, queue, message model, claim controls and skip
controls were adapted from the supplied ForgePlay source (author YURIY;
its package metadata declares ISC). Its supplied WLG images are included in
PadForge's existing finish system. ComfyJS 1.1.29 and its bundled tmi.js client
are vendored with their MIT license notices.

The frontend uses browser modules directly; no JavaScript build step or npm
installation is needed. For development checks:

```powershell
node --test tools/web-integration.test.mjs
node tools/web-integration-browser.mjs
dotnet test PadForge.Tests/PadForge.Tests.csproj --filter "FullyQualifiedName~WebController"
```

The browser smoke test uses headless Edge, a local fixture server and simulated
Twitch/gamepad input. It does not connect to Twitch or install controller drivers.
Set `EDGE_PATH` to use a different Chromium browser executable. Add
`--screenshots` to save setup and controller images under `artifacts/`.
