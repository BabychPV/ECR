#!/usr/bin/env python3
"""hourly.py - один виклик для щогодинного контролю координатора (cron о :13).

Збирає разом те, що 23.11 після першого запуску перестали робити вручну:
  - стан live: файл LIVE-SHA проти того, що реально працює на стенді (порт 5094 -> build.done);
  - табло карток (board.py): статус проти git, статус що відстав, AC без SHA, невзяті задачі;
  - активні гілки без картки;
  - робочі копії (wt_audit.py): лише НОВІ позначки, розібрані запам'ятовує `wt_audit.py --ack`;
  - вільне місце на дисках робочих копій (24.11 H: дійшов до 0 ГБ, і копія агента зникла).
Друкує лише позначки [!]; якщо все чисто - один рядок «OK». Лише читає.

  python hourly.py --repo H:\\ECR --roots H:\\ECR-wt H:\\ECR-worktrees H:\\ECR\\.claude\\worktrees C:\\ecr-worktrees F:\\ecr-wt

Коди виходу: 0 - чисто; 1 - є [!]; 2 - помилка.
"""
import argparse
import os
import shutil
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import board  # noqa: E402
import where_is  # noqa: E402
import wt_audit  # noqa: E402

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def disk_flags(paths, min_gb):
    """Вільне місце на кожному диску, де лежать репозиторій і робочі копії."""
    seen, flags, info = {}, [], []
    for p in paths:
        if not p or not os.path.exists(p):
            continue
        drive = os.path.splitdrive(os.path.abspath(p))[0]
        key = drive.upper() if drive else os.stat(p).st_dev
        if key in seen:
            continue
        seen[key] = p
        try:
            free = shutil.disk_usage(p).free / 1024 ** 3
        except OSError:
            continue
        label = (drive + "\\") if drive else p
        if free < min_gb:
            flags.append("ДИСК %s: вільно %.0f ГБ < %.0f - нових агентів не запускати; прибрати влиті робочі копії"
                         % (label, free, min_gb))
        else:
            info.append("%s %.0f ГБ" % (label, free))
    return flags, info


def run(repo, roots=(), idle_min=60, min_disk_gb=20.0, lane_days=2.0, live_port=where_is.LIVE_PORT,
        live_build_done=None, baseline=None, now=None):
    now = now or time.time()
    flags, info = [], []
    live_raw, _ = where_is.read_live(repo, None)
    live = where_is.resolve(repo, live_raw) if live_raw else None
    lf, li = board.live_check(repo, live or live_raw, live_port, live_build_done)
    flags += lf
    info += li
    cards, _ = board.load_cards(os.path.join(repo, ".sync-local", "ready"))
    wts = board.worktrees_by_branch(repo)
    for c in cards:
        f, _, _ = board.check_card(repo, c, live, now, idle_min, wts)
        flags += ["%s [%s]: %s" % (c["id"], c["status"], x) for x in f]
    for br, why in board.lanes_without_cards(repo, cards, lane_days, now):
        flags.append("ГІЛКА БЕЗ КАРТКИ %s (%s) - заведи картку з CARD-TEMPLATE.md" % (br, why))
    rows, orphans = wt_audit.audit(repo, roots, idle_min, now)
    acked = wt_audit.load_baseline(baseline or wt_audit.default_baseline(repo))
    new_rows, new_orphans = wt_audit.only_new(rows, orphans, acked)
    for r in new_rows:
        flags += ["%s%s [%s]: %s" % ("ОСНОВНА " if r["main"] else "", r["path"], r["branch"], x) for x in r["flags"]]
    flags += ["СИРОТА %s - %s" % (c, k) for c, k in new_orphans]
    df, di = disk_flags([repo] + list(roots) + [r["path"] for r in rows], min_disk_gb)
    flags += df
    info.append("карток %d, робочих копій %d, диски: %s" % (len(cards), len(rows), ", ".join(di) or "-"))
    return flags, info


def main(argv=None):
    ap = argparse.ArgumentParser(description="Щогодинний контроль: лише позначки [!].")
    ap.add_argument("--repo", default=".")
    ap.add_argument("--roots", nargs="*", default=[], help="папки з робочими копіями (для сиріт і дисків)")
    ap.add_argument("--idle-min", type=int, default=60)
    ap.add_argument("--min-disk-gb", type=float, default=20.0)
    ap.add_argument("--lane-days", type=float, default=2.0)
    ap.add_argument("--live-port", type=int, default=where_is.LIVE_PORT)
    ap.add_argument("--live-build-done", help="шлях до build.done живого застосунку (замість пошуку за портом)")
    ap.add_argument("--baseline", help="файл розібраних позначок wt_audit")
    ap.add_argument("--verbose", action="store_true", help="друкувати й довідкові рядки")
    ap.add_argument("--git", default=os.environ.get("GIT", "git"))
    ap.add_argument("--no-fetch", action="store_true")
    a = ap.parse_args(argv)
    board.set_git(a.git)
    rc, top = where_is.git(a.repo, "rev-parse", "--show-toplevel")
    if rc != 0:
        print("ПОМИЛКА: %s - не git-репозиторій" % a.repo)
        return 2
    if not a.no_fetch and where_is.git(top, "fetch", "--quiet", "--prune", "origin")[0] != 0:
        print("УВАГА: git fetch не вдався - дані можуть бути застарілі")
    try:
        flags, info = run(top, a.roots, a.idle_min, a.min_disk_gb, a.lane_days, a.live_port,
                          a.live_build_done, a.baseline)
    except RuntimeError as e:
        print("ПОМИЛКА: %s" % e)
        return 2
    for f in flags:
        print("[!] " + f)
    if a.verbose or not flags:
        for i in info:
            print(("OK: " if not flags else "    ") + i)
    if flags:
        print("Позначок: %d. Кожну - розібрати зараз; нічого не видаляти." % len(flags))
    return 1 if flags else 0


if __name__ == "__main__":
    sys.exit(main())
