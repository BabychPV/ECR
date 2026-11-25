#!/usr/bin/env python3
"""board.py - табло карток: заявлений статус проти того, де код насправді.

Читає картки v3 (`# Картка` + рядок `Статус:`) у .sync-local\\ready\\ і для кожної
перевіряє git. Старі листи готовності без рядка `Статус:` пропускає.

  python board.py --repo H:\\ECR                     # табло і позначки [!]
  python board.py --repo H:\\ECR --window <SHA|гілка> # що їде вікном, чеклист live, таблиця для людини

Ловить:
  - статус «reported» і далі, а в AC немає SHA (зроблено не все - клас B74);
  - заявлено main / live, а коміт AC лише в гілці (клас NAE);
  - СТАТУС ВІДСТАЄ: код уже в main чи на live, а картка досі «accepted» (24.11);
  - активна гілка без картки (24.11: шість нових гілок без карток);
  - «в роботі», а немає ні робочої копії, ні комітів, ні змін (урок 462);
  - простій із незакоміченою роботою; «прийнято» і за добу не в пачці.
У комірці SHA може бути кілька SHA через кому. AC-0 (випадок людини) без SHA не вважається
незробленим: його доводить перевірка на live, а не коміт. Решта AC без SHA - не зроблено,
навіть з доказом «live» (B74: UI-частина P4 теж «перевіряється на live», але мусить мати код).
Лише читає. Коди виходу: 0 - без позначок; 1 - є [!]; 2 - помилка.
"""
import argparse
import glob
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import overlap  # noqa: E402
import where_is  # noqa: E402
import wt_audit  # noqa: E402

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

STATUSES = ["planned", "briefed", "in-progress", "returned", "reported", "accepted",
            "batched", "rc", "main", "live-verified", "closed"]
WORKING = {"briefed", "in-progress", "returned"}
NEED_SHA = {"reported", "accepted", "batched", "rc", "main", "live-verified", "closed"}
BEFORE_MAIN = WORKING | {"reported", "accepted", "batched", "rc"}
SHA_TOKEN = re.compile(r"\b(?=[0-9a-fA-F]*\d)[0-9a-fA-F]{7,40}\b")
AC_ROW = re.compile(r"^\|\s*(AC-[\w.-]+)\s*\|")
RANK = {"NOT_FOUND": 0, "NOWHERE": 1, "LOCAL": 2, "LANE": 3, "RC": 4, "MAIN": 5, "LIVE_PROBABLE": 6, "LIVE": 7}


def set_git(path):
    where_is.GIT = path
    wt_audit.GIT = path
    overlap.GIT = path


def parse_card(path):
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        text = f.read()
    if not re.search(r"(?m)^#\s*Картка", text) or not re.search(r"(?m)^Статус:", text):
        return None

    def field(name):
        m = re.search(r"(?m)^%s:[ \t]*(.*?)\s*$" % name, text)
        return m.group(1).strip() if m else ""

    title = re.search(r"(?m)^#\s*Картка[^:\n]*:?\s*(.*)$", text)
    parts = field("Статус").split()
    card = {"path": path, "title": (title.group(1).strip() if title else os.path.basename(path)),
            "status": parts[0].lower() if parts else "", "status_arg": parts[1] if len(parts) > 1 else "",
            "branch": field("Гілка").split()[0] if field("Гілка") else "",
            "batch": field("Пачка").split()[0] if field("Пачка") else "",
            "risk": field("Ризик"), "mtime": os.path.getmtime(path), "ac": []}
    card["id"] = card["title"].split()[0] if card["title"] else os.path.basename(path)
    for line in text.splitlines():
        s = line.strip()
        m = AC_ROW.match(s)
        if not m:
            continue
        cells = [c.strip() for c in s.strip("|").split("|")]
        shas = SHA_TOKEN.findall(cells[-1]) if len(cells) > 1 else []
        card["ac"].append({"id": m.group(1), "shas": shas, "sha": shas[0] if shas else "",
                           "what": cells[1] if len(cells) > 1 else "",
                           "proof": cells[2] if len(cells) > 3 else "",
                           "live": "live" in s.lower()})
    return card


def worktrees_by_branch(repo):
    res = {}
    try:
        for w in wt_audit.list_worktrees(repo):
            if w.get("branch"):
                res[w["branch"]] = w["path"]
    except RuntimeError:
        pass
    return res


def branch_ref(repo, branch):
    for cand in (branch, "origin/" + branch):
        rc, out = where_is.git(repo, "rev-parse", "--verify", "--quiet", cand + "^{commit}")
        if rc == 0 and out:
            return cand
    return None


def contained(repo, sha, target):
    if where_is.is_ancestor(repo, sha, target):
        return True
    _, subj = where_is.git(repo, "log", "-1", "--format=%s", sha)
    return bool(where_is.same_subject(repo, subj, target))


def ac_location(repo, ac, live, main):
    """Найгірше місце серед SHA цього AC (усі його коміти мають бути там, де заявлено)."""
    worst = None
    for sha in ac["shas"]:
        r = where_is.locate(repo, sha, live, main)
        if worst is None or RANK[r["code"]] < RANK[worst["code"]]:
            worst = dict(r, sha_ref=sha)
    return worst


def check_card(repo, card, live, now, idle_min, wts, main="origin/main"):
    flags, info = [], []
    st = card["status"]
    if st not in STATUSES:
        flags.append("невідомий статус «%s» (дозволено: %s)" % (st, ", ".join(STATUSES)))
        return flags, info, {}
    acs = card["ac"]
    if not acs and st != "planned":
        flags.append("немає жодного AC - нема з чим звіряти")
    live_only = [a["id"] for a in acs if not a["shas"] and a["id"] == "AC-0"]
    missing = [a["id"] for a in acs if not a["shas"] and a["id"] != "AC-0"]
    if st in NEED_SHA and missing:
        flags.append("AC без SHA = не зроблено: %s" % ", ".join(missing))
    if live_only:
        info.append("доводяться на live, не комітом: %s" % ", ".join(live_only))
    locs = {}
    for a in acs:
        if a["shas"]:
            locs[a["id"]] = ac_location(repo, a, live, main)
            if locs[a["id"]]["code"] == "NOT_FOUND":
                flags.append("%s: SHA %s не знайдено в репозиторії" % (a["id"], locs[a["id"]]["sha_ref"]))
    found = {k: v for k, v in locs.items() if v["code"] != "NOT_FOUND"}
    if st == "batched":
        target = card["batch"] or card["status_arg"]
        tref = branch_ref(repo, target) if target else None
        if not tref:
            flags.append("статус batched, але пачку «%s» не знайдено (рядок «Пачка:»)" % (target or "-"))
        else:
            for a in acs:
                for sha in a["shas"]:
                    if a["id"] in found and not contained(repo, sha, tref):
                        flags.append("%s (%s) не в пачці %s" % (a["id"], sha, target))
    want = {"rc": {"RC", "MAIN", "LIVE", "LIVE_PROBABLE"}, "main": {"MAIN", "LIVE", "LIVE_PROBABLE"},
            "live-verified": {"LIVE"}, "closed": {"LIVE"}}.get(st)
    if want:
        if st in ("live-verified", "closed") and not live:
            flags.append("заявлено live, але SHA live невідомий - запиши .sync-local/LIVE-SHA")
        else:
            for k, v in found.items():
                if v["code"] not in want:
                    flags.append("заявлено «%s», а %s: %s" % (st, k, v["text"]))
    if found and len(found) == len(locs) and not missing:
        codes = {v["code"] for v in found.values()}
        if codes == {"LIVE"} and st not in ("live-verified", "closed"):
            flags.append("СТАТУС ВІДСТАЄ: код усіх AC уже НА LIVE, а статус «%s» - перевір на live (ref\\06 E9) і онови" % st)
        elif codes <= {"MAIN", "LIVE", "LIVE_PROBABLE"} and st in BEFORE_MAIN:
            flags.append("СТАТУС ВІДСТАЄ: код усіх AC уже в main, а статус «%s» - онови (main / live-verified)" % st)
    age_min = int((now - card["mtime"]) / 60)
    if st in WORKING:
        br = card["branch"]
        ref = branch_ref(repo, br) if br else None
        ahead = 0
        if ref:
            _, n = where_is.git(repo, "rev-list", "--count", "%s..%s" % (main, ref))
            ahead = int(n or 0)
        wt = wts.get(br) if br else None
        if not br:
            flags.append("в роботі, але в картці немає «Гілка:»")
        elif wt and os.path.isdir(wt):
            paths, _ = wt_audit.dirty_paths(wt)
            paths = paths or []
            if ahead == 0 and not paths:
                quiet = int((now - max(card["mtime"], os.path.getmtime(wt))) / 60)
                if quiet >= idle_min:
                    flags.append("НЕ ПОЧАТО? робоча копія без змін і комітів %d хв" % quiet)
            elif paths:
                idle = int((now - wt_audit.newest_mtime(wt, paths)) / 60)
                if idle >= idle_min:
                    flags.append("ПРОСТІЙ %d хв із незакоміченою роботою (%d файлів)" % (idle, len(paths)))
            info.append("робоча копія %s; комітів поза main: %d; незакомічено: %d" % (wt, ahead, len(paths)))
        elif ahead > 0:
            _, ct = where_is.git(repo, "log", "-1", "--format=%ct", ref)
            idle = int((now - float(ct or 0)) / 60)
            if idle >= idle_min:
                flags.append("немає робочої копії, останній коміт %d хв тому - агент живий?" % idle)
        elif age_min >= idle_min:
            flags.append("НЕ ПОЧАТО? «%s» %d хв, а немає ні робочої копії, ні комітів у %s" % (st, age_min, br))
    if st == "accepted" and age_min >= 24 * 60 and not any(v["code"] in ("MAIN", "LIVE", "LIVE_PROBABLE")
                                                          for v in found.values()):
        flags.append("прийнято %d год тому і досі не в пачці" % (age_min // 60))
    return flags, info, locs


def lanes_without_cards(repo, cards, days=2, now=None, base="origin/main"):
    """Активні lane-гілки (коміти поза main або жива робоча копія за `days` днів) без картки."""
    now = now or time.time()
    have = {c["branch"] for c in cards if c["branch"]}
    active = {}
    for ref in overlap.active_lanes(repo, base, days, False, now):
        name = ref[len("origin/"):] if ref.startswith("origin/") else ref
        active[name] = "коміти поза main"
    try:
        wts = wt_audit.list_worktrees(repo)
    except RuntimeError:
        wts = []
    for w in wts[1:]:
        br = w.get("branch") or ""
        if not br.startswith("lane/") or overlap.BATCH_RE.match(br) or br in active:
            continue
        path = w["path"]
        if not os.path.isdir(path):
            continue
        paths, _ = wt_audit.dirty_paths(path)
        _, ct = where_is.git(path, "log", "-1", "--format=%ct")
        last = max(float(ct or 0), wt_audit.newest_mtime(path, paths or []), os.path.getmtime(path))
        if paths and now - last <= days * 86400:
            active[br] = "робоча копія зі змінами"
    return sorted((b, why) for b, why in active.items() if b not in have)


def load_cards(cards_dir):
    cards, skipped = [], 0
    for p in sorted(glob.glob(os.path.join(cards_dir, "*.md"))):
        c = parse_card(p)
        if c:
            cards.append(c)
        else:
            skipped += 1
    return cards, skipped


def window(repo, cards, target):
    going, partial, staying = [], [], []
    for c in cards:
        shas = [(a, s) for a in c["ac"] for s in a["shas"]]
        missing = [a["id"] for a in c["ac"] if not a["shas"] and a["id"] != "AC-0"]
        if not shas:
            if c["status"] in NEED_SHA:
                staying.append((c, "немає жодного SHA"))
            continue
        outside = sorted({a["id"] for a, s in shas if not contained(repo, s, target)})
        if not outside and not missing:
            going.append(c)
        elif len(outside) < len({a["id"] for a, _ in shas}):
            partial.append((c, outside + missing))
        elif c["status"] in NEED_SHA:
            staying.append((c, "жоден AC не в %s" % target[:9]))
    return going, partial, staying


def human_view(card):
    """Що людина побачить: AC-0 або перший live-AC, інакше назва картки."""
    for a in card["ac"]:
        if a["id"] == "AC-0" and a["what"]:
            return a["what"]
    for a in card["ac"]:
        if a["live"] and a["what"]:
            return a["what"]
    return card["title"]


def live_check(repo, live_file_sha, port=where_is.LIVE_PORT, build_done=None):
    """[!] коли файл LIVE-SHA не збігається з тим, що реально працює на стенді."""
    det, src = where_is.detect_live(port, build_done)
    if not det:
        return [], ["версію живого застосунку не визначено: %s" % src]
    if not live_file_sha:
        return ["LIVE-SHA не записано, а на стенді працює %s (%s) - запиши .sync-local\\LIVE-SHA" % (det[:9], src)], []
    if not where_is.same_commit(repo, live_file_sha, det):
        return ["LIVE-SHA у файлі %s, а на стенді працює %s (%s) - після вікна онови .sync-local\\LIVE-SHA"
                % (live_file_sha[:9], det[:9], src)], []
    return [], ["live %s підтверджено: %s" % (det[:9], src)]


def main(argv=None):
    ap = argparse.ArgumentParser(description="Табло карток: статус проти git.")
    ap.add_argument("--repo", default=".")
    ap.add_argument("--cards", help="папка карток (типово <repo>/.sync-local/ready)")
    ap.add_argument("--live", help="SHA live (інакше .sync-local/LIVE-SHA)")
    ap.add_argument("--live-port", type=int, default=where_is.LIVE_PORT)
    ap.add_argument("--live-build-done", help="шлях до build.done живого застосунку (замість пошуку за портом)")
    ap.add_argument("--idle-min", type=int, default=60)
    ap.add_argument("--lane-days", type=float, default=2, help="гілка активна, якщо мінялась за стільки днів")
    ap.add_argument("--window", help="SHA або гілка, що їде на стенд: що їде, що ні, чеклист live")
    ap.add_argument("--git", default=os.environ.get("GIT", "git"))
    ap.add_argument("--no-fetch", action="store_true")
    a = ap.parse_args(argv)
    set_git(a.git)
    rc, top = where_is.git(a.repo, "rev-parse", "--show-toplevel")
    if rc != 0:
        print("ПОМИЛКА: %s - не git-репозиторій" % a.repo)
        return 2
    if not a.no_fetch and where_is.git(top, "fetch", "--quiet", "--prune", "origin")[0] != 0:
        print("УВАГА: git fetch не вдався - дані можуть бути застарілі")
    cards_dir = a.cards or os.path.join(top, ".sync-local", "ready")
    cards, skipped = load_cards(cards_dir)
    live_raw, _ = where_is.read_live(top, a.live)
    live = where_is.resolve(top, live_raw) if live_raw else None
    print("Карток v3: %d (старих листів пропущено: %d); live = %s"
          % (len(cards), skipped, live[:9] if live else "НЕВІДОМИЙ"))
    if a.window:
        target = where_is.resolve(top, a.window)
        if not target:
            print("ПОМИЛКА: %s не знайдено" % a.window)
            return 2
        going, partial, staying = window(top, cards, target)
        print("\n== Їде вікном %s (%s): %d карток" % (a.window, target[:9], len(going)))
        for c in going:
            print("  %s [%s] %s" % (c["id"], c["status"], c["title"]))
        for c, out in partial:
            print("[!] ЧАСТКОВО %s: не їдуть %s" % (c["id"], ", ".join(out)))
        for c, why in staying:
            print("  не їде: %s [%s] - %s" % (c["id"], c["status"], why))
        print("\n== Чеклист перевірки на live (AC з доказом «live»):")
        n = 0
        for c in going + [p[0] for p in partial]:
            for ac in c["ac"]:
                if ac["live"] and (not ac["shas"] or all(contained(top, s, target) for s in ac["shas"])):
                    n += 1
                    print("- [ ] %s %s: %s - доказ: %s" % (c["id"], ac["id"], ac["what"], ac["proof"]))
        if not n:
            print("  (немає AC з доказом на live - перевір картки)")
        print("\n== Для людини до вікна: що побачите після вікна")
        for c in going:
            print("- %s: %s" % (c["id"], human_view(c)))
        print("== Чого ще НЕ буде після цього вікна")
        for c, out in partial:
            print("- %s: частково - без %s" % (c["id"], ", ".join(out)))
        for c, why in staying:
            print("- %s: %s" % (c["id"], c["title"]))
        if not partial and not staying:
            print("- (усе з карток їде; якщо людина чекає чогось іще - з'ясувати ДО вікна, урок 512)")
        return 1 if partial else 0
    now = time.time()
    wts = worktrees_by_branch(top)
    flagged = 0
    lf, li = live_check(top, live, a.live_port, a.live_build_done)
    for f in lf:
        flagged += 1
        print("[!] " + f)
    for i in li:
        print("    " + i)
    for c in cards:
        flags, info, _ = check_card(top, c, live, now, a.idle_min, wts)
        with_sha = sum(1 for x in c["ac"] if x["shas"])
        mark = "[!]" if flags else "   "
        flagged += bool(flags)
        print("%s %s [%s] %s AC %d/%d з SHA - %s" % (mark, c["id"], c["status"], c["risk"] or "-",
                                                  with_sha, len(c["ac"]), os.path.basename(c["path"])))
        for f in flags:
            print("      ! " + f)
        for i in info:
            print("        " + i)
    for br, why in lanes_without_cards(top, cards, a.lane_days, now):
        flagged += 1
        print("[!] ГІЛКА БЕЗ КАРТКИ %s (%s) - заведи картку з CARD-TEMPLATE.md" % (br, why))
    by = {}
    for c in cards:
        by[c["status"]] = by.get(c["status"], 0) + 1
    print("Разом: " + (", ".join("%s %d" % (s, by[s]) for s in STATUSES if s in by) or "0")
          + "; з позначками: %d" % flagged)
    return 1 if flagged else 0


if __name__ == "__main__":
    sys.exit(main())
