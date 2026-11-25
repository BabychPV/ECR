#!/usr/bin/env python3
"""overlap.py - перетини активних гілок і гарячі файли (таблиця з CLAUDE.md).

Режими:
  python overlap.py --repo H:\\ECR                     # гілки карток у роботі (типово)
  python overlap.py --repo H:\\ECR --all               # усі lane/* зі змінами за --days
  python overlap.py --repo H:\\ECR lane/integ-a lane/b  # задані гілки (зведення пачки)
  python overlap.py --repo H:\\ECR --plan src/Ecr.Application/Recalc tests/X.cs
                                                     # перед брифом: чи чіпає хтось ці файли

Типово беруться гілки з карток у роботі (planned … batched): 23.11 режим «усі за 7 днів»
дав 147 перетинів, здебільшого зі старими гілками сесій, що стоять, - і його перестали
запускати. Без карток - активні lane/* за 2 дні. Пачки lane/integ-b<N> і lane/integ-batch-*
виключено: вони містять чужі зміни і давали б хибні перетини.
Лише читає git. Коди виходу: 0 - без позначок; 1 - є [!]; 2 - помилка.
"""
import argparse
import glob
import os
import re
import subprocess
import sys
import time

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

GIT = "git"

# Гарячі файли - CLAUDE.md, «Гарячі файли - черги, а не домовленості на ходу».
HOT = [
    ("migration", r"(^|/)Migrations/|EcrDbContextModelSnapshot\.cs$",
     "міграції EF + знімок: одна міграція в польоті (токен міграції)"),
    ("seed", r"(^|/)Sql/09-seed\.sql$", "сід: append лише у свою іменовану секцію"),
    ("contract", r"openapi\.snapshot\.json$|schema\.d\.ts$",
     "контракт: перегенерація останнім кроком після rebase, у тому ж коміті"),
    ("errors", r"(^|/)ErrorCodes\.cs$|ExceptionHandlingMiddleware\.cs$|02-contracts\.md$",
     "коди помилок: append у кінець, один код = один коміт"),
    ("shared", r"(^|/)shared/ui/|queryKeys\.ts$|(^|/)test/setup\.ts$|DependencyInjection\.cs$",
     "спільне: попередження перед правкою"),
    ("policy", r"^CLAUDE\.md$|^\.github/|(^|/)package-lock\.json$|Directory\.Packages\.props$",
     "політики й пакети: окремим комітом через lane із зеленим CI"),
    ("tz", r"^docs/tz/|^docs/CHECKSUMS\.txt$",
     "ТЗ і реєстр рішень: CHECKSUMS у тому ж коміті; при зведенні лишати обидва рядки"),
]
BATCH_RE = re.compile(r"^lane/integ-(b\d+|batch-.*)$")


def git(repo, *args):
    p = subprocess.run([GIT, "-C", repo, "-c", "core.quotepath=off", *args],
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return p.returncode, p.stdout.decode("utf-8", "replace").strip()


def hot_rules(path):
    """Правила гарячих файлів для шляху; шлях без розширення трактується і як папка."""
    p = path.replace("\\", "/").strip("/")
    variants = (p, p + "/")
    return [(k, why) for k, rx, why in HOT if any(re.search(rx, v, re.IGNORECASE) for v in variants)]


def active_lanes(repo, base, days, include_batches, now=None):
    now = now or time.time()
    rc, out = git(repo, "for-each-ref", "--format=%(refname:short)\t%(committerdate:raw)",
                  "refs/heads/lane", "refs/remotes/origin/lane")
    if rc != 0:
        raise RuntimeError("git for-each-ref впав")
    by_name = {}
    for line in out.splitlines():
        ref, _, raw = line.partition("\t")
        name = ref[len("origin/"):] if ref.startswith("origin/") else ref
        ts = float((raw.split() or ["0"])[0])
        cur = by_name.get(name)
        # локальна гілка має перевагу: у ній можуть бути ще не запушені коміти
        if cur is None or (not ref.startswith("origin/") and cur[0].startswith("origin/")):
            by_name[name] = (ref, ts)
    lanes = []
    for name, (ref, ts) in sorted(by_name.items()):
        if not include_batches and BATCH_RE.match(name):
            continue
        if days is not None and now - ts > days * 86400:
            continue
        _, ahead = git(repo, "rev-list", "--count", "%s..%s" % (base, ref))
        if int(ahead or 0) > 0:
            lanes.append(ref)
    return lanes


IN_WORK = {"planned", "briefed", "in-progress", "returned", "reported", "accepted", "batched"}


def card_branches(cards_dir, statuses=IN_WORK):
    """Гілки карток v3 у роботі (без імпорту board.py, щоб не було кола імпортів)."""
    res = []
    for p in sorted(glob.glob(os.path.join(cards_dir, "*.md"))):
        try:
            with open(p, encoding="utf-8-sig", errors="replace") as f:
                text = f.read()
        except OSError:
            continue
        if not re.search(r"(?m)^#\s*Картка", text):
            continue
        st = re.search(r"(?m)^Статус:[ \t]*(\S+)", text)
        br = re.search(r"(?m)^Гілка:[ \t]*(\S+)", text)
        if st and br and st.group(1).lower() in statuses:
            res.append(br.group(1))
    return sorted(set(res))


def lanes_from_cards(repo, cards_dir, base="origin/main"):
    lanes = []
    for br in card_branches(cards_dir):
        for cand in (br, "origin/" + br):
            rc, _ = git(repo, "rev-parse", "--verify", "--quiet", cand + "^{commit}")
            if rc == 0:
                _, ahead = git(repo, "rev-list", "--count", "%s..%s" % (base, cand))
                if int(ahead or 0) > 0:
                    lanes.append(cand)
                break
    return lanes


def changed_files(repo, base, ref):
    rc, mb = git(repo, "merge-base", base, ref)
    if rc != 0 or not mb:
        return None
    _, out = git(repo, "diff", "--name-only", mb, ref)
    return [l for l in out.splitlines() if l.strip()]


def analyze(repo, refs, base="origin/main"):
    files = {}
    for ref in refs:
        f = changed_files(repo, base, ref)
        if f is None:
            raise RuntimeError("немає спільної бази для %s і %s" % (ref, base))
        files[ref] = f
    owners = {}
    for ref, fl in files.items():
        for f in fl:
            owners.setdefault(f, []).append(ref)
    shared = {f: o for f, o in owners.items() if len(o) > 1}
    hot = {}
    for ref, fl in files.items():
        for f in fl:
            for k, why in hot_rules(f):
                hot.setdefault(k, {"why": why, "lanes": {}})["lanes"].setdefault(ref, []).append(f)
    flags = []
    for f, o in sorted(shared.items()):
        flags.append("ПЕРЕТИН %s: %s" % (f, ", ".join(o)))
    mig = hot.get("migration")
    if mig and len(mig["lanes"]) > 1:
        flags.append("МІГРАЦІЇ у %d гілках (%s) - %s; при зведенні: порядок і перегенерація знімка EF"
                     % (len(mig["lanes"]), ", ".join(sorted(mig["lanes"])), mig["why"]))
    return {"files": files, "shared": shared, "hot": hot, "flags": flags}


def plan_check(repo, planned, lanes, base="origin/main"):
    norm = lambda p: p.replace("\\", "/").strip("/").lower()  # noqa: E731
    want = [norm(p) for p in planned]
    hits, flags = {}, []
    for ref in lanes:
        for f in changed_files(repo, base, ref) or []:
            nf = norm(f)
            for w in want:
                if nf == w or nf.startswith(w + "/"):
                    hits.setdefault(ref, []).append(f)
    for ref, fl in sorted(hits.items()):
        flags.append("ЗАЙНЯТО гілкою %s: %s" % (ref, ", ".join(sorted(set(fl))[:6])))
    notes = []
    for p in planned:
        for _, why in hot_rules(norm(p)):
            notes.append("%s - гарячий файл: %s" % (p, why))
    return flags, notes


def main(argv=None):
    global GIT
    ap = argparse.ArgumentParser(description="Перетини активних гілок і гарячі файли.")
    ap.add_argument("refs", nargs="*", help="гілки для перевірки (типово - гілки карток у роботі)")
    ap.add_argument("--repo", default=".")
    ap.add_argument("--base", default="origin/main")
    ap.add_argument("--cards", help="папка карток (типово <repo>/.sync-local/ready)")
    ap.add_argument("--all", action="store_true", help="усі активні lane/* за --days, а не лише з карток")
    ap.add_argument("--days", type=float, default=2, help="з --all або без карток: зміни за стільки днів (типово 2)")
    ap.add_argument("--include-batches", action="store_true")
    ap.add_argument("--plan", nargs="+", help="файли/папки, які збирається змінювати нова картка")
    ap.add_argument("--git", default=os.environ.get("GIT", "git"))
    ap.add_argument("--no-fetch", action="store_true")
    a = ap.parse_args(argv)
    GIT = a.git
    rc, top = git(a.repo, "rev-parse", "--show-toplevel")
    if rc != 0:
        print("ПОМИЛКА: %s - не git-репозиторій" % a.repo)
        return 2
    if not a.no_fetch and git(top, "fetch", "--quiet", "--prune", "origin")[0] != 0:
        print("УВАГА: git fetch не вдався - дані можуть бути застарілі")
    try:
        if a.refs:
            lanes, how = a.refs, "задані"
        else:
            from_cards = [] if a.all else lanes_from_cards(
                top, a.cards or os.path.join(top, ".sync-local", "ready"), a.base)
            if from_cards:
                lanes, how = from_cards, "з карток у роботі"
            else:
                lanes = active_lanes(top, a.base, a.days, a.include_batches)
                how = "активні за %g дн.%s" % (a.days, "" if a.all else " (карток у роботі немає)")
        print("Гілок у перевірці: %d (%s)" % (len(lanes), how))
        if a.plan:
            flags, notes = plan_check(top, a.plan, lanes, a.base)
            for n in notes:
                print("  ! " + n)
            for f in flags:
                print("[!] " + f)
            if not flags:
                print("Вільно: жодна активна гілка не чіпає ці файли.")
            return 1 if flags else 0
        r = analyze(top, lanes, a.base)
    except RuntimeError as e:
        print("ПОМИЛКА: %s" % e)
        return 2
    for k, h in sorted(r["hot"].items()):
        for ref, fl in sorted(h["lanes"].items()):
            print("  гаряче [%s] %s: %s - %s" % (k, ref, ", ".join(fl[:4]) + (" …" if len(fl) > 4 else ""), h["why"]))
    for f in r["flags"]:
        print("[!] " + f)
    print("Перетинів файлів: %d; позначок: %d" % (len(r["shared"]), len(r["flags"])))
    return 1 if r["flags"] else 0


if __name__ == "__main__":
    sys.exit(main())
