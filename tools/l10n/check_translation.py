"""Checks translations of the interface against the rules in rules.py.

usage: python tools/l10n/check_translation.py TAG [TAG...]
       python tools/l10n/check_translation.py --all

Reads PadForge.App/Resources/Strings/Strings.<tag>.resx beside Strings.resx and the
language table, prints every problem with the key it is in, and exits 1 when there is any.
A key a translation leaves out is not a problem: that string shows in English.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rules  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")
table = rules.languages()
tags = [t for t in table if t != "en-US"] if sys.argv[1:] == ["--all"] else sys.argv[1:]
if not tags:
    sys.exit(__doc__)
english = rules.read_resx(os.path.join(rules.STRINGS, "Strings.resx"))
failed = 0
for tag in tags:
    if tag not in table:
        print("%s: no row in Languages.tsv" % tag)
        failed += 1
        continue
    translated = rules.read_resx(os.path.join(rules.STRINGS, "Strings.%s.resx" % tag))
    problems = rules.check(english, translated, table[tag][3])
    for kind, key, message in problems:
        print("%s %s %s: %s" % (tag, kind, key, message))
    print("%s (%s): %d of %d strings, %d problem(s)" % (tag, table[tag][0], len(translated), len(english), len(problems)))
    failed += bool(problems)
sys.exit(1 if failed else 0)
