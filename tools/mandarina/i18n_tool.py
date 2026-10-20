"""Mandarina i18n helper.
  add:   python i18n_tool.py add <repo> <spec.json>   spec = {"key": {"en": "...", "es": "...", "ko": "...", "zh-CN": "..."}}
  merge: python i18n_tool.py merge <from-repo> <to-repo>   appends keys that <from-repo> has and <to-repo> lacks (all 4 languages)
Keys are appended at the end of src/Filee.App/Assets/i18n/<lang>.json, which is how the files are laid out by hand."""
import json, sys, os
LANGS = ["en", "es", "ko", "zh-CN"]

def path(repo, lang):
    return os.path.join(repo, "src", "Filee.App", "Assets", "i18n", lang + ".json")

def load(repo, lang):
    with open(path(repo, lang), encoding="utf-8") as f:
        return json.load(f)

def append(repo, lang, items, header=None):
    p = path(repo, lang)
    with open(p, encoding="utf-8") as f:
        text = f.read()
    existing = json.loads(text)
    items = {k: v for k, v in items.items() if k not in existing}
    if not items:
        return 0
    body = text.rstrip()
    assert body.endswith("}"), p
    body = body[:-1].rstrip()
    if not body.endswith(","):
        body += ","
    lines = []
    if header:
        lines.append("")
    for k, v in items.items():
        lines.append("  %s: %s," % (json.dumps(k, ensure_ascii=False), json.dumps(v, ensure_ascii=False)))
    lines[-1] = lines[-1].rstrip(",")
    out = body + "\n" + "\n".join(lines) + "\n}\n"
    json.loads(out)  # must stay valid
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(out)
    return len(items)

if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "add":
        repo, spec = sys.argv[2], json.load(open(sys.argv[3], encoding="utf-8"))
        for lang in LANGS:
            n = append(repo, lang, {k: v[lang] for k, v in spec.items()}, header=True)
            print(lang, "added", n)
    elif cmd == "merge":
        src, dst = sys.argv[2], sys.argv[3]
        en_src, en_dst = load(src, "en"), load(dst, "en")
        new = [k for k in en_src if k not in en_dst]
        for lang in LANGS:
            s = load(src, lang)
            n = append(dst, lang, {k: s[k] for k in new if k in s}, header=True)
            print(lang, "merged", n)
        print("keys:", len(new))
