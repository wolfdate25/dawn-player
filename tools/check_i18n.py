"""Localization consistency checker for Dawn Player.

Verifies, against the ko-KR Resources.resw as reference:
  1. key parity across all language files (+ no duplicates)
  2. every x:Uid in App XAML has at least one matching resw key
  3. every literal AppStrings.Get/Format/GetPlural key exists in resw
  4. interpolated lookups (AppStrings.Get($"Prefix_{expr}", ...)) have resw keys under Prefix_
  5. resw keys not referenced by any of the above are reported
  6. Korean string literals in App C# outside AppStrings fallback/log positions are reported

Exit code is non-zero only for problems 1-4; 5-6 are informational (review the list).
"""
import collections
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

APP = "src/DawnPlayer.App"
LANGS = ["en-US", "ja-JP", "ko-KR"]
HANGUL = re.compile("[\uac00-\ud7a3]")
CALL = re.compile(r"AppStrings\.(Get|GetString|Format|GetPlural)\s*\(")
LOG = re.compile(r"\bLog\(|Logger\.|Debug\.|_log\b|App\.Log\b", re.IGNORECASE)


def load_keys(lang):
    path = f"{APP}/Strings/{lang}/Resources.resw"
    return [d.get("name") for d in ET.parse(path).getroot().findall("data")]


def app_files(ext):
    return [
        f
        for f in glob.glob(f"{APP}/**/*{ext}", recursive=True)
        if f"{os.sep}bin{os.sep}" not in f and f"{os.sep}obj{os.sep}" not in f
    ]


def main():
    problems = []

    # 1) cross-language parity + duplicates
    keys = {lang: load_keys(lang) for lang in LANGS}
    for lang, names in keys.items():
        dups = [k for k, v in collections.Counter(names).items() if v > 1]
        if dups:
            problems.append(f"{lang}: duplicate keys {dups}")
        print(f"{lang}: {len(names)} keys")
    ref = set(keys["ko-KR"])
    for lang in LANGS:
        diff = ref - set(keys[lang])
        if diff:
            problems.append(f"missing in {lang}: {sorted(diff)}")

    res = set(keys["ko-KR"])
    uidbases = {k.split(".")[0] for k in res}

    # 2) x:Uid usage in XAML
    uids = []
    for f in app_files(".xaml"):
        for m in re.finditer(r'x:Uid="([^"]+)"', open(f, encoding="utf-8").read()):
            uids.append(m.group(1))
    missing_uids = sorted({u for u in uids if u not in uidbases})
    if missing_uids:
        problems.append(f"x:Uid missing from resw: {missing_uids}")
    print(f"x:Uid usages: {len(uids)} (unique {len(set(uids))}), missing: {len(missing_uids)}")

    # 3+4) C# lookups: literal keys and interpolated prefixes
    literal_re = re.compile(r'AppStrings\.(?:Get|GetString|Format|GetPlural)\(\s*"([^"}]+)"')
    prefix_re = re.compile(r'AppStrings\.(?:Get|GetString|Format|GetPlural)\(\s*\$"([^"{]+)\{')
    lit_keys, prefixes = set(), set()
    for f in app_files(".cs"):
        text = open(f, encoding="utf-8-sig").read()
        lit_keys.update(literal_re.findall(text))
        prefixes.update(prefix_re.findall(text))
    missing_lit = sorted(k for k in lit_keys if k not in res)
    if missing_lit:
        problems.append(f"literal keys missing from resw: {missing_lit}")
    empty_prefixes = sorted(p for p in prefixes if not any(k.startswith(p) for k in res))
    if empty_prefixes:
        problems.append(f"interpolated prefixes with no resw keys: {empty_prefixes}")
    print(f"C# literal keys: {len(lit_keys)} (missing {len(missing_lit)}), "
          f"interpolated prefixes: {sorted(prefixes)} (empty {len(empty_prefixes)})")

    # 5) unused keys
    referenced = set(lit_keys) | {u for u in uids} | {k for k in res for p in prefixes if k.startswith(p)}
    unused = [k for k in sorted(res) if k.split(".")[0] not in {u for u in uids}
              and k not in lit_keys
              and not any(k.startswith(p) for p in prefixes)]
    print(f"resw keys not referenced by x:Uid / literal / interpolated lookup: {len(unused)}")
    for k in unused:
        print("  UNUSED:", k)

    # 6) Korean literals outside fallback/log lines
    gaps = []
    for f in app_files(".cs"):
        rel = os.path.relpath(f, APP)
        if rel.startswith("Localization" + os.sep):
            continue
        for i, line in enumerate(open(f, encoding="utf-8-sig").read().splitlines(), 1):
            s = line.strip()
            if s.startswith(("/", "*", "<")) or not HANGUL.search(line):
                continue
            if CALL.search(line) or LOG.search(line):
                continue
            if any(HANGUL.search(m.group(1)) for m in re.finditer(r'"((?:[^"\\]|\\.)*)"', line)):
                gaps.append(f"{rel}:{i}: {s[:110]}")
    print(f"Korean literals outside AppStrings fallback/log lines: {len(gaps)}")
    for g in gaps:
        print("  GAP:", g)

    if problems:
        print("\nPROBLEMS:")
        for p in problems:
            print(" -", p)
        return 1
    print("\nOK: parity, x:Uid, literal and interpolated lookups all resolve.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
