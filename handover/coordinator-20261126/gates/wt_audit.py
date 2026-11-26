#!/usr/bin/env python3
"""wt_audit.py - звірка робочих копій агентів: нічого не загублено?

Бере ВСІ робочі копії з `git worktree list` (а не з однієї папки) і для кожної
показує незакомічене, незапушене і скільки хвилин немає руху. Окремо шукає
«сироти»: папки в коренях робочих копій, які git не знає (зламана або чужа копія).
Лише читає; нічого не змінює й не видаляє.

Приклад:
  python wt_audit.py --repo H:\\ECR --roots H:\\ECR-wt H:\\ECR-worktrees H:\\ECR\\.claude\\worktrees
  python wt_audit.py --repo H:\\ECR --ack        # після розбору: запам'ятати теперішні позначки
  python wt_audit.py --repo H:\\ECR --new-only   # далі показувати лише нові (так робить hourly.py)

Коди виходу: 0 - проблем немає; 1 - є позначки [!]; 2 - помилка.
"""
import argparse
import json
import os
import subprocess
import sys
import time

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

GIT = "git"


def git(cwd, *args):
    p = subprocess.run([GIT, "-C", cwd, "-c", "core.quotepath=off", *args],
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return p.returncode, p.stdout.decode("utf-8", "replace"), p.stderr.decode("utf-8", "replace").strip()


def norm(path):
    return os.path.normcase(os.path.realpath(os.path.normpath(path)))


def list_worktrees(repo):
    rc, out, err = git(repo, "worktree", "list", "--porcelain")
    if rc != 0:
        raise RuntimeError(err or "git worktree list")
    items, cur = [], {}
    for line in out.splitlines():
        if not line.strip():
            if cur:
                items.append(cur)
                cur = {}
            continue
        key, _, val = line.partition(" ")
        if key == "worktree":
            cur = {"path": val}
        elif key == "branch":
            cur["branch"] = val[len("refs/heads/"):] if val.startswith("refs/heads/") else val
        elif key in ("HEAD", "detached", "locked", "prunable", "bare"):
            cur[key] = val or True
    if cur:
        items.append(cur)
    return items


def dirty_paths(wt):
    """Шляхи з `git status --porcelain -z`; None, якщо git не читає папку."""
    rc, out, err = git(wt, "status", "--porcelain", "-z")
    if rc != 0:
        return None, err.splitlines()[0] if err else "git status впав"
    toks = out.split("\0")
    paths, i = [], 0
    while i < len(toks):
        t = toks[i]
        i += 1
        if len(t) < 4:
            continue
        xy, p = t[:2], t[3:]
        paths.append(p)
        if "R" in xy or "C" in xy:
            i += 1  # наступний токен - старий шлях
    return paths, None


def newest_mtime(wt, paths, cap=2000):
    newest, seen = 0.0, 0
    for p in paths:
        full = os.path.join(wt, p)
        if os.path.isdir(full):
            for root, dirs, files in os.walk(full):
                for f in files:
                    seen += 1
                    try:
                        newest = max(newest, os.path.getmtime(os.path.join(root, f)))
                    except OSError:
                        pass
                    if seen >= cap:
                        return newest
        else:
            try:
                newest = max(newest, os.path.getmtime(full))
            except OSError:
                pass
    return newest


def flag(row, kind, text):
    row["flags"].append(text)
    row["keys"].append(kind)


def flag_key(path, kind):
    return "%s|%s" % (norm(path), kind)


def default_baseline(repo):
    return os.path.join(repo, ".sync-local", "gates", "wt-baseline.json")


def load_baseline(path):
    """Позначки, які координатор уже розібрав (--ack). Брудна основна папка сюди не потрапляє ніколи."""
    try:
        with open(path, encoding="utf-8") as f:
            return set(json.load(f).get("acknowledged", []))
    except (OSError, ValueError):
        return set()


def current_keys(rows, orphans):
    keys = []
    for r in rows:
        for k in r["keys"]:
            keys.append(flag_key(r["path"], k))  # основну папку однаково покаже only_new
    keys += [flag_key(child, "orphan") for child, _ in orphans]
    return keys


def save_baseline(path, keys):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"acknowledged": sorted(set(keys)),
                   "updated": time.strftime("%Y-%m-%d %H:%M")}, f, ensure_ascii=False, indent=1)


def only_new(rows, orphans, acked):
    """Лишає тільки нерозібрані позначки (основна папка - завжди)."""
    out_rows = []
    for r in rows:
        pairs = [(k, f) for k, f in zip(r["keys"], r["flags"])
                 if k == "main-dirty" or flag_key(r["path"], k) not in acked]
        nr = dict(r, keys=[k for k, _ in pairs], flags=[f for _, f in pairs])
        out_rows.append(nr)
    out_orphans = [(c, kind) for c, kind in orphans if flag_key(c, "orphan") not in acked]
    return out_rows, out_orphans


def audit(repo, roots=(), idle_min=60, now=None):
    now = now or time.time()
    wts = list_worktrees(repo)
    rows, registered = [], set()
    main_path = norm(wts[0]["path"]) if wts else norm(repo)
    for w in wts:
        path = w["path"]
        npath = norm(path)
        registered.add(npath)
        is_main = npath == main_path
        row = {"path": path, "branch": w.get("branch") or "(detached %s)" % str(w.get("HEAD", ""))[:9],
               "flags": [], "keys": [], "info": [], "main": is_main}
        rows.append(row)
        if not os.path.isdir(path):
            flag(row, "missing", "ПАПКИ НЕМАЄ - запис git залишився (git worktree prune лише після перевірки, що робота не потрібна)")
            continue
        paths, err = dirty_paths(path)
        if paths is None:
            flag(row, "git", "git не читає папку: " + err)
            continue
        if is_main:
            if paths:
                flag(row, "main-dirty", "ОСНОВНА ПАПКА НЕЧИСТА (%d): %s" % (len(paths), ", ".join(paths[:5])))
            else:
                row["info"].append("чисто")
            continue
        _, ct, _ = git(path, "log", "-1", "--format=%ct")
        last = max(float(ct.strip() or 0), newest_mtime(path, paths))
        idle = int((now - last) / 60) if last else None
        row["dirty"] = len(paths)
        br = w.get("branch")
        unpushed, note = None, ""
        if br:
            rc, _, _ = git(path, "rev-parse", "--verify", "--quiet", "refs/remotes/origin/" + br)
            if rc == 0:
                _, n, _ = git(path, "rev-list", "--count", "origin/%s..HEAD" % br)
                unpushed = int(n.strip() or 0)
            else:
                _, n, _ = git(path, "rev-list", "--count", "origin/main..HEAD")
                unpushed = int(n.strip() or 0)
                note = " (гілки на origin немає; комітів поза main: %d)" % unpushed
        row["unpushed"] = unpushed
        row["idle"] = idle
        row["info"].append("незакомічено=%d незапушено=%s%s рух=%s хв тому"
                           % (len(paths), "?" if unpushed is None else unpushed, note,
                              "?" if idle is None else idle))
        if idle is not None and idle >= idle_min:
            if paths:
                flag(row, "idle-dirty", "ПРОСТІЙ %d хв з НЕЗАКОМІЧЕНОЮ роботою - агент живий? спершу зберегти" % idle)
            elif unpushed:
                flag(row, "idle-unpushed", "ПРОСТІЙ %d хв з незапушеними комітами (%d)" % (idle, unpushed))
    orphans = []
    scan = set(roots or ())
    for r in rows:
        if not r["main"]:
            scan.add(os.path.dirname(os.path.normpath(r["path"])))
    for root in sorted(scan):
        if not os.path.isdir(root):
            continue
        for name in sorted(os.listdir(root)):
            child = os.path.join(root, name)
            if not os.path.isdir(child):
                continue
            nchild = norm(child)
            if nchild in registered or nchild == main_path:
                continue
            if any(reg.startswith(nchild + os.sep) for reg in registered):
                continue  # папка-контейнер з робочими копіями всередині
            gitp = os.path.join(child, ".git")
            if os.path.isfile(gitp):
                kind = "зламана робоча копія (.git-файл є, git її не знає)"
            elif os.path.isdir(gitp):
                kind = "окремий клон, не робоча копія H:\\ECR"
            else:
                kind = "не git-папка"
            orphans.append((child, kind))
    return rows, orphans


def main(argv=None):
    global GIT
    ap = argparse.ArgumentParser(description="Звірка робочих копій агентів.")
    ap.add_argument("--repo", default=".")
    ap.add_argument("--roots", nargs="*", default=[], help="папки з робочими копіями для пошуку сиріт")
    ap.add_argument("--idle-min", type=int, default=60, help="поріг простою, хв (типово 60)")
    ap.add_argument("--baseline", help="файл розібраних позначок (типово <repo>/.sync-local/gates/wt-baseline.json)")
    ap.add_argument("--new-only", action="store_true", help="показати лише нерозібрані позначки")
    ap.add_argument("--ack", action="store_true",
                    help="запам'ятати всі теперішні позначки як розібрані (крім брудної основної папки)")
    ap.add_argument("--git", default=os.environ.get("GIT", "git"))
    a = ap.parse_args(argv)
    GIT = a.git
    try:
        rows, orphans = audit(a.repo, a.roots, a.idle_min)
    except Exception as e:  # noqa: BLE001
        print("ПОМИЛКА: %s" % e)
        return 2
    _, top, _ = git(a.repo, "rev-parse", "--show-toplevel")
    bpath = a.baseline or default_baseline(top.strip() or a.repo)
    if a.ack:
        keys = current_keys(rows, orphans)
        save_baseline(bpath, keys)
        print("Запам'ятовано як розібрані: %d позначок -> %s" % (len(keys), bpath))
        return 0
    if a.new_only:
        rows, orphans = only_new(rows, orphans, load_baseline(bpath))
    flagged = 0
    for r in rows:
        if a.new_only and not r["flags"]:
            continue
        mark = "[!]" if r["flags"] else "   "
        flagged += bool(r["flags"])
        title = "ОСНОВНА " if r["main"] else ""
        print("%s %s%s  [%s]  %s" % (mark, title, r["path"], r["branch"], "; ".join(r["info"])))
        for f in r["flags"]:
            print("      ! " + f)
    for child, kind in orphans:
        flagged += 1
        print("[!] СИРОТА %s - %s" % (child, kind))
    print("Разом робочих копій: %d; з позначками: %d; сиріт: %d" % (len(rows), flagged - len(orphans), len(orphans)))
    return 1 if flagged else 0


if __name__ == "__main__":
    sys.exit(main())
