"""Generates PadForge.App/Views/KeyboardFamilies.g.cs from QMK layout data.

Each keyboard family the Keyboard + Mouse preview draws is one QMK layout:
the key positions come from the layout's info.json (or a keyboard's
keyboard.json), and each position's identity from that layout's default
keymap, in the same order. QMK's basic keycodes are HID Keyboard/Keypad page
usage IDs (quantum/keycodes.h), so a key is its usage. The handful of QMK
consumer keycodes a keymap uses map to their Consumer page usages, and a
layer key, a lighting key or an empty position is drawn inert.

usage: python gen_kbm_layouts.py QMK_ROOT KEYCHRON_QMK_ROOT OUT_CS
"""
import json
import os
import re
import sys

QMK, KEYCHRON, OUT = sys.argv[1], sys.argv[2], sys.argv[3]

# ── QMK keycodes ─────────────────────────────────────────────────────────
src = open(os.path.join(KEYCHRON, "quantum", "keycodes.h"), encoding="utf-8").read()
codes = {}
for name, value in re.findall(r"\b(KC_[A-Z0-9_]+)\s*=\s*(0x[0-9A-Fa-f]+)", src):
    codes.setdefault(name, int(value, 16))
for _ in range(3):  # aliases may chain
    for alias, target in re.findall(r"\b(KC_[A-Z0-9_]+)\s*=\s*(KC_[A-Z0-9_]+)\s*,", src):
        if target in codes and alias not in codes:
            codes[alias] = codes[target]

# QMK consumer keycodes (its own numbers, not HID) to Consumer page usages:
# the usages PadForge's key map sends for the same media keys.
CONSUMER = {
    "KC_AUDIO_MUTE": 0xE2, "KC_AUDIO_VOL_UP": 0xE9, "KC_AUDIO_VOL_DOWN": 0xEA,
    "KC_MEDIA_NEXT_TRACK": 0xB5, "KC_MEDIA_PREV_TRACK": 0xB6, "KC_MEDIA_STOP": 0xB7,
    "KC_MEDIA_PLAY_PAUSE": 0xCD, "KC_MAIL": 0x18A, "KC_CALCULATOR": 0x192,
    "KC_MY_COMPUTER": 0x194, "KC_WWW_SEARCH": 0x221, "KC_WWW_HOME": 0x223,
    "KC_WWW_BACK": 0x224, "KC_WWW_FORWARD": 0x225, "KC_WWW_STOP": 0x226,
    "KC_WWW_REFRESH": 0x227, "KC_WWW_FAVORITES": 0x22A, "KC_MEDIA_SELECT": 0x183,
}
consumer_by_code = {codes[k]: v for k, v in CONSUMER.items() if k in codes}

# macOS spellings in keymaps share the PC keys' codes.
for mac, pc in (("KC_LCMD", "KC_LGUI"), ("KC_RCMD", "KC_RGUI"), ("KC_LOPT", "KC_LALT"),
                ("KC_ROPT", "KC_RALT"), ("KC_LWIN", "KC_LGUI"), ("KC_RWIN", "KC_RGUI")):
    if pc in codes:
        codes.setdefault(mac, codes[pc])


def code_of(token):
    """(page << 16 | usage), or 0 for an inert position."""
    token = token.strip()
    if token in ("_______", "XXXXXXX", "KC_NO", "KC_TRNS", "KC_TRANSPARENT") or not token:
        return 0
    if not token.startswith("KC_"):
        return 0          # MO(1), RM_NEXT and other firmware functions
    if token not in codes:
        raise SystemExit(f"unknown keycode {token}")
    c = codes[token]
    if c in consumer_by_code:
        return (0x0C << 16) | consumer_by_code[c]
    if 0x04 <= c <= 0xE7:
        return (0x07 << 16) | c
    return 0          # brightness and other system controls no row sends


def layout_keys(path, layout_name=None):
    j = json.load(open(path, encoding="utf-8"))
    layouts = j["layouts"]
    name = layout_name or next(iter(layouts))
    return name, layouts[name]["layout"]


def keymap_tokens(path, layout_name, layer_tag=None):
    text = open(path, encoding="utf-8").read()
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    text = re.sub(r"//[^\n]*", "", text)
    tag = re.escape(layer_tag) if layer_tag else "0"
    m = re.search(r"\[" + tag + r"\]\s*=\s*" + re.escape(layout_name) + r"\(", text)
    if not m:
        raise SystemExit(f"no {layout_name} in {path}")
    # The layer's arguments: up to the parenthesis that closes the call.
    i, depth = m.end(), 1
    start = i
    while i < len(text) and depth > 0:
        if text[i] == "(":
            depth += 1
        elif text[i] == ")":
            depth -= 1
        i += 1
    body = text[start:i - 1]
    tokens, depth, cur = [], 0, ""
    for ch in body:
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
        if ch == "," and depth == 0:
            tokens.append(cur.strip())
            cur = ""
        else:
            cur += ch
    if cur.strip():
        tokens.append(cur.strip())
    return tokens


FAMILIES = [
    # name, layout json, layout name or None, keymap, layer tag or None, source note
    ("FullsizeAnsi", f"{QMK}/layouts/default/fullsize_ansi/info.json", None,
     f"{QMK}/layouts/default/fullsize_ansi/default_fullsize_ansi/keymap.c", None, "qmk layouts/default/fullsize_ansi"),
    ("FullsizeIso", f"{QMK}/layouts/default/fullsize_iso/info.json", None,
     f"{QMK}/layouts/default/fullsize_iso/default_fullsize_iso/keymap.c", None, "qmk layouts/default/fullsize_iso"),
    ("FullsizeExtended", f"{QMK}/layouts/default/fullsize_extended_ansi/info.json", None,
     f"{QMK}/layouts/default/fullsize_extended_ansi/default_fullsize_extended_ansi/keymap.c", None, "qmk layouts/default/fullsize_extended_ansi"),
    ("Tkl", f"{QMK}/layouts/default/tkl_ansi/info.json", None,
     f"{QMK}/layouts/default/tkl_ansi/default_tkl_ansi/keymap.c", None, "qmk layouts/default/tkl_ansi"),
    ("SeventyFive", f"{QMK}/layouts/default/75_ansi/info.json", None,
     f"{QMK}/layouts/default/75_ansi/default_75_ansi/keymap.c", None, "qmk layouts/default/75_ansi"),
    ("SixtyFive", f"{QMK}/layouts/default/65_ansi/info.json", None,
     f"{QMK}/layouts/default/65_ansi/default_65_ansi/keymap.c", None, "qmk layouts/default/65_ansi"),
    ("SixtyFiveBlocker", f"{QMK}/layouts/default/65_ansi_blocker/info.json", None,
     f"{QMK}/layouts/default/65_ansi_blocker/default_65_ansi_blocker/keymap.c", None, "qmk layouts/default/65_ansi_blocker"),
    ("Sixty", f"{QMK}/layouts/default/60_ansi/info.json", None,
     f"{QMK}/layouts/default/60_ansi/default_60_ansi/keymap.c", None, "qmk layouts/default/60_ansi"),
    ("Hhkb", f"{QMK}/layouts/default/60_hhkb/info.json", None,
     f"{QMK}/layouts/default/60_hhkb/default_60_hhkb/keymap.c", None, "qmk layouts/default/60_hhkb"),
    ("HhkbJis", f"{QMK}/keyboards/hhkb/jp/keyboard.json", "LAYOUT_jp",
     f"{QMK}/keyboards/hhkb/jp/keymaps/default/keymap.c", None, "qmk keyboards/hhkb/jp"),
    ("KeychronQ6", f"{KEYCHRON}/keyboards/keychron/q6/ansi/keyboard.json", "LAYOUT_ansi_108",
     f"{KEYCHRON}/keyboards/keychron/q6/ansi/keymaps/default/keymap.c", "WIN_BASE", "keychron qmk keyboards/keychron/q6/ansi, WIN_BASE layer"),
]

out = []
out.append("// <auto-generated>")
out.append("//     Generated by tools/gen_kbm_layouts.py from QMK's layout data")
out.append("//     (qmk/qmk_firmware layouts/default and keyboards/hhkb/jp, and")
out.append("//     Keychron's QMK fork for the Q6). Do not edit by hand.")
out.append("// </auto-generated>")
out.append("namespace PadForge.Views")
out.append("{")
out.append("    internal static partial class KeyboardFamilies")
out.append("    {")
summary = []
for name, lay_path, lay_name, km_path, layer, note in FAMILIES:
    lname, keys = layout_keys(lay_path, lay_name)
    tokens = keymap_tokens(km_path, lname, layer)
    if len(tokens) != len(keys):
        raise SystemExit(f"{name}: {len(keys)} positions, {len(tokens)} keycodes")
    out.append(f"        /// <summary>{len(keys)} keys, {note}.</summary>")
    out.append(f"        internal static readonly LayoutKey[] {name} =")
    out.append("        {")
    inert = 0
    for k, t in zip(keys, tokens):
        c = code_of(t)
        if c == 0:
            inert += 1
        x, y = k["x"], k["y"]
        w, h = k.get("w", 1), k.get("h", 1)
        label = "" if c else f", \"{t}\""
        out.append(f"            new({x:g}, {y:g}, {w:g}, {h:g}, 0x{c:06X}{label}),")
    out.append("        };")
    out.append("")
    summary.append(f"{name}: {len(keys)} keys, {inert} inert")
out.append("    }")
out.append("}")
open(OUT, "w", encoding="utf-8", newline="\r\n").write("\n".join(out) + "\n")
print("\n".join(summary))
