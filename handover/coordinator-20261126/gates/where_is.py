#!/usr/bin/env python3
"""where_is.py - де зараз коміт: live / main / rc / гілка / лише локально.

Пускати ПЕРЕД тим, як сказати людині «готово», «на стенді» чи «працює».
Лише читає git (і робить `git fetch`, якщо не задано --no-fetch); нічого не змінює.

Приклади:
  python where_is.py a2f8c6be6 --live a9a3c0067
  python where_is.py lane/integ-nae-import
  python where_is.py 5827ac7d7 275bdc17a          # SHA live береться з .sync-local/LIVE-SHA

SHA live: --live, інакше перший рядок <repo>/.sync-local/LIVE-SHA.
Коди виходу: 0 - усе на live; 1 - хоча б одне НЕ на live (або live невідомий); 2 - помилка.
"""
import argparse
import os
import re
import subprocess
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

GIT = "git"


def git(repo, *args, check=False):
    p = subprocess.run([GIT, "-C", repo, "-c", "core.quotepath=off", *args],
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    out = p.stdout.decode("utf-8", "replace").strip()
    if check and p.returncode != 0:
        raise RuntimeError(p.stderr.decode("utf-8", "replace").strip() or "git " + " ".join(args))
    return p.returncode, out


def resolve(repo, ref):
    for cand in (ref, "origin/" + ref):
        rc, out = git(repo, "rev-parse", "--verify", "--quiet", cand + "^{commit}")
        if rc == 0 and out:
            return out
    return None


def is_ancestor(repo, sha, target):
    rc, _ = git(repo, "merge-base", "--is-ancestor", sha, target)
    return rc == 0


def same_subject(repo, subject, target, limit=5000):
    """Копія коміту з тим самим заголовком (cherry-pick / rebase) в історії target."""
    rc, out = git(repo, "log", target, "-n", str(limit), "--format=%H%x1f%s")
    if rc != 0:
        return None
    for line in out.splitlines():
        h, _, s = line.partition("\x1f")
        if s == subject:
            return h
    return None


def branches_containing(repo, sha, pattern, remote=True):
    args = ["branch"] + (["-r"] if remote else []) + ["--contains", sha, "--list", pattern]
    rc, out = git(repo, *args)
    if rc != 0:
        return []
    names = []
    for line in out.splitlines():
        # префікс git branch: "* " поточна, "+ " відкрита в іншій робочій копії, "  " решта.
        # Перший рядок уже без пробілів (git() обрізає вивід), тому зрізаємо лише явні "* " і "+ ".
        line = line.strip()
        if line[:2] in ("* ", "+ "):
            line = line[2:].strip()
        if line and "->" not in line:
            names.append(line)
    return names


def read_live(repo, live):
    if live:
        return live, "--live"
    # .sync-local лежить лише в основній папці (H:\ECR), а скрипт можуть
    # запустити з робочої копії агента - тому шукаємо і там, і в основній.
    dirs = [repo]
    rc, common = git(repo, "rev-parse", "--git-common-dir")
    if rc == 0 and common:
        if not os.path.isabs(common):
            common = os.path.join(repo, common)
        dirs.append(os.path.dirname(os.path.normpath(common)))
    for d in dirs:
        f = os.path.join(d, ".sync-local", "LIVE-SHA")
        if os.path.isfile(f):
            with open(f, encoding="utf-8-sig") as fh:
                tok = (fh.readline().split() or [""])[0]
            if tok:
                return tok, f
    return None, None


LIVE_PORT = 5094
_PS_LIVE = ("$c = Get-NetTCPConnection -LocalPort {port} -State Listen -ErrorAction SilentlyContinue | "
            "Select-Object -First 1; if ($c) {{ $w = Get-CimInstance Win32_Process -Filter "
            "(\"ProcessId=\" + $c.OwningProcess); \"$($c.OwningProcess)`t$($w.ExecutablePath)`t$($w.CommandLine)\" }}")


def binary_paths(text):
    """Шляхи до .dll/.exe у командному рядку процесу; Ecr.Api - першим."""
    found = re.findall(r'([A-Za-z]:\\[^"\t\r\n]*?\.(?:dll|exe))(?=["\s]|$)', text or "")
    found = [p.strip() for p in found]
    return sorted(dict.fromkeys(found), key=lambda p: (0 if "ecr.api" in p.lower() else 1))


def sha_from_build_done(text):
    m = re.search(r"HEAD\W{0,5}([0-9a-fA-F]{7,40})\b", text or "")
    if m:
        return m.group(1).lower()
    m = re.search(r"\b([0-9a-f]{40})\b", text or "")
    return m.group(1) if m else None


def find_build_done(binary, levels=3):
    d = os.path.dirname(binary)
    for _ in range(levels):
        p = os.path.join(d, "build.done")
        if os.path.isfile(p):
            return p
        nd = os.path.dirname(d)
        if nd == d:
            break
        d = nd
    return None


def read_build_done(path):
    try:
        with open(path, encoding="utf-8-sig", errors="replace") as f:
            return sha_from_build_done(f.read())
    except OSError:
        return None


def detect_live(port=LIVE_PORT, build_done=None):
    """(SHA, джерело) версії, що реально працює на стенді, або (None, причина).
    Як координатор робив вручну 23.11: процес на :5094 -> його Ecr.Api.dll -> build.done -> HEAD."""
    if build_done:
        sha = read_build_done(build_done)
        return (sha, build_done) if sha else (None, "у %s немає HEAD <sha>" % build_done)
    if os.name != "nt":
        return None, "визначення живого застосунку - лише на Windows"
    try:
        p = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", _PS_LIVE.format(port=port)],
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
    except (OSError, subprocess.TimeoutExpired) as e:
        return None, "powershell: %s" % e
    out = p.stdout.decode("utf-8", "replace").strip()
    if not out:
        return None, "на порту %d ніхто не слухає (стенд лежить?)" % port
    for b in binary_paths(out):
        bd = find_build_done(b)
        if bd:
            sha = read_build_done(bd)
            if sha:
                return sha, bd
    return None, "біля %s немає build.done з HEAD" % (binary_paths(out) or ["?"])[0]


def same_commit(repo, a, b):
    """Чи це той самий коміт (повні SHA, якщо відомі репозиторію; інакше - за префіксом)."""
    ra, rb = resolve(repo, a), resolve(repo, b)
    if ra and rb:
        return ra == rb
    a, b = a.lower(), b.lower()
    return a.startswith(b) or b.startswith(a)


def locate(repo, ref, live_sha, main="origin/main"):
    """Повертає dict: code (LIVE|LIVE_PROBABLE|MAIN|RC|LANE|LOCAL|NOWHERE|NOT_FOUND), text."""
    sha = resolve(repo, ref)
    if not sha:
        return {"ref": ref, "code": "NOT_FOUND", "text": "НЕ ЗНАЙДЕНО: такого коміту чи гілки немає (fetch? друкарська помилка?)"}
    _, subject = git(repo, "log", "-1", "--format=%s", sha)
    r = {"ref": ref, "sha": sha[:9], "subject": subject}
    if live_sha:
        if is_ancestor(repo, sha, live_sha):
            r.update(code="LIVE", text="НА LIVE")
            return r
        twin = same_subject(repo, subject, live_sha)
        if twin:
            r.update(code="LIVE_PROBABLE",
                     text="ЙМОВІРНО НА LIVE: на live є коміт з тим самим заголовком, але інший SHA %s - звір вміст" % twin[:9])
            return r
    on_main = is_ancestor(repo, sha, main)
    twin_main = None if on_main else same_subject(repo, subject, main)
    if on_main or twin_main:
        extra = "" if on_main else " (копія %s з тим самим заголовком)" % twin_main[:9]
        if live_sha:
            r.update(code="MAIN", text="У MAIN%s, АЛЕ НЕ НА LIVE - дійде з наступним вікном" % extra)
        else:
            r.update(code="MAIN", text="У MAIN%s; live невідомий" % extra)
        return r
    rcs = branches_containing(repo, sha, "origin/rc/*")
    if rcs:
        r.update(code="RC", text="У RC (%s), ЩЕ НЕ В MAIN" % ", ".join(rcs[:3]))
        return r
    lanes = branches_containing(repo, sha, "origin/lane/*")
    if lanes:
        more = "" if len(lanes) <= 4 else " +%d" % (len(lanes) - 4)
        r.update(code="LANE", text="ЛИШЕ В ГІЛЦІ: %s%s - не в main, не на live" % (", ".join(lanes[:4]), more))
        return r
    others = branches_containing(repo, sha, "origin/*")
    if others:
        r.update(code="LANE", text="ЛИШЕ В ГІЛЦІ: %s - не в main, не на live" % ", ".join(others[:4]))
        return r
    local = branches_containing(repo, sha, "*", remote=False)
    if local:
        r.update(code="LOCAL", text="НЕ ЗАПУШЕНО: лише локально (%s)" % ", ".join(local[:4]))
        return r
    r.update(code="NOWHERE", text="НІДЕ: коміт не належить жодній гілці")
    return r


def main(argv=None):
    global GIT
    ap = argparse.ArgumentParser(description="Де зараз коміт: live / main / rc / гілка / локально.")
    ap.add_argument("refs", nargs="+", help="SHA або назва гілки (можна кілька: по одному на кожен AC)")
    ap.add_argument("--live", help="SHA, що стоїть на live (інакше .sync-local/LIVE-SHA)")
    ap.add_argument("--repo", default=".", help="шлях до репозиторію (типово поточна папка)")
    ap.add_argument("--main", default="origin/main")
    ap.add_argument("--git", default=os.environ.get("GIT", "git"))
    ap.add_argument("--no-fetch", action="store_true", help="не робити git fetch")
    a = ap.parse_args(argv)
    GIT = a.git
    rc, top = git(a.repo, "rev-parse", "--show-toplevel")
    if rc != 0:
        print("ПОМИЛКА: %s - не git-репозиторій" % a.repo)
        return 2
    repo = top
    if not a.no_fetch:
        frc, _ = git(repo, "fetch", "--quiet", "--prune", "origin")
        if frc != 0:
            print("УВАГА: git fetch не вдався - відповідь за локальними даними, вони можуть бути застарілі")
    live, src = read_live(repo, a.live)
    if live:
        lsha = resolve(repo, live)
        if not lsha:
            print("ПОМИЛКА: SHA live %s не знайдено в репозиторії" % live)
            return 2
        print("live = %s (джерело: %s)" % (lsha[:9], src))
    else:
        lsha = None
        print("live НЕВІДОМИЙ: передай --live <SHA> або запиши SHA у .sync-local/LIVE-SHA")
    worst = 0
    for ref in a.refs:
        r = locate(repo, ref, lsha, a.main)
        sha = r.get("sha", "-")
        name = r["ref"] if sha.startswith(r["ref"][:9]) else "%s (%s)" % (r["ref"], sha)
        head = "%s %s" % (name, ("«%s»" % r["subject"]) if r.get("subject") else "")
        print("- %s\n  -> %s" % (head.strip(), r["text"]))
        if r["code"] == "NOT_FOUND":
            worst = 2
        elif r["code"] != "LIVE" and worst < 1:
            worst = 1
    if lsha is None and worst < 1:
        worst = 1
    return worst


if __name__ == "__main__":
    sys.exit(main())
