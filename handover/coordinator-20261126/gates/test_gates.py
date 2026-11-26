#!/usr/bin/env python3
"""Тести скриптів воріт. Перший запуск на машині координатора:

    python test_gates.py

Створює тимчасові git-репозиторії (потрібен git у PATH або змінна GIT),
нічого поза тимчасовою папкою не чіпає.
"""
import contextlib
import io
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import board  # noqa: E402
import hourly  # noqa: E402
import overlap  # noqa: E402
import route  # noqa: E402
import where_is  # noqa: E402
import wt_audit  # noqa: E402

GIT = os.environ.get("GIT", "git")
board.set_git(GIT)
overlap.GIT = GIT


def g(cwd, *args, env=None):
    e = dict(os.environ, **env) if env else None
    p = subprocess.run([GIT, "-C", cwd, *args], stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=e)
    if p.returncode != 0:
        raise RuntimeError("git %s: %s" % (" ".join(args), p.stderr.decode("utf-8", "replace")))
    return p.stdout.decode("utf-8", "replace").strip()


def commit(cwd, name, msg, date=None):
    full = os.path.join(cwd, name)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "a", encoding="utf-8") as f:
        f.write(msg + "\n")
    g(cwd, "add", "--", name)
    env = {"GIT_AUTHOR_DATE": date, "GIT_COMMITTER_DATE": date} if date else None
    g(cwd, "commit", "-q", "-m", msg, env=env)
    return g(cwd, "rev-parse", "HEAD")


def rmtree(path):
    def onerr(func, p, _):  # Windows: git робить файли в .git лише для читання
        os.chmod(p, stat.S_IWRITE)
        func(p)
    if sys.version_info >= (3, 12):
        shutil.rmtree(path, onexc=onerr)
    else:
        shutil.rmtree(path, onerror=onerr)


def new_repo(tmp):
    origin = os.path.join(tmp, "origin.git")
    repo = os.path.join(tmp, "ECR")
    subprocess.run([GIT, "init", "-q", "--bare", origin], check=True)
    subprocess.run([GIT, "clone", "-q", origin, repo], check=True, stderr=subprocess.DEVNULL)
    for k, v in (("user.email", "t@t"), ("user.name", "t"), ("commit.gpgsign", "false")):
        g(repo, "config", k, v)
    commit(repo, "a.txt", "init")
    g(repo, "branch", "-M", "main")
    g(repo, "push", "-q", "origin", "main")
    return origin, repo


class WhereIsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp()
        _, r = new_repo(cls.tmp)
        cls.repo = r
        c2 = commit(r, "a.txt", "feat: base")
        # гілка агента від c2: справжній коміт, який потім cherry-pick у main
        g(r, "checkout", "-q", "-b", "lane/integ-x")
        cls.cherry_src = commit(r, "x.txt", "виправлення: NAE «Цех»")
        g(r, "push", "-q", "origin", "lane/integ-x")
        g(r, "checkout", "-q", "main")
        cls.on_live = commit(r, "a.txt", "feat: on live")
        g(r, "cherry-pick", "-x", cls.cherry_src)
        # cherry-pick -x додає рядок у тіло, заголовок той самий
        cls.live = g(r, "rev-parse", "HEAD")
        cls.main_only = commit(r, "a.txt", "feat: main not live")
        g(r, "push", "-q", "origin", "main")
        g(r, "checkout", "-q", "-b", "rc/2026-11-23-b9")
        cls.rc_only = commit(r, "a.txt", "feat: rc only")
        g(r, "push", "-q", "origin", "rc/2026-11-23-b9")
        g(r, "checkout", "-q", "-b", "lane/integ-nae", c2)
        cls.lane_only = commit(r, "n.txt", "fix: nae lane only")
        g(r, "push", "-q", "origin", "lane/integ-nae")
        g(r, "checkout", "-q", "-b", "lane/local", "main")
        cls.local_only = commit(r, "l.txt", "wip local")
        g(r, "checkout", "-q", "main")
        g(r, "branch", "-D", "lane/integ-nae")  # лишається лише origin/lane/integ-nae
        g(r, "fetch", "-q", "origin")

    @classmethod
    def tearDownClass(cls):
        rmtree(cls.tmp)

    def code(self, ref, live=True):
        return where_is.locate(self.repo, ref, self.live if live else None)["code"]

    def test_verdicts(self):
        self.assertEqual(self.code(self.on_live), "LIVE")
        self.assertEqual(self.code(self.cherry_src), "LIVE_PROBABLE")
        self.assertEqual(self.code(self.main_only), "MAIN")
        self.assertEqual(self.code(self.rc_only), "RC")
        self.assertEqual(self.code(self.lane_only), "LANE")
        self.assertEqual(self.code("lane/integ-nae"), "LANE")  # назва гілки через origin/
        self.assertEqual(self.code(self.local_only), "LOCAL")
        self.assertEqual(self.code("deadbeef1"), "NOT_FOUND")

    def test_branch_names_in_text_are_exact(self):
        # перший рядок виводу git branch не має губити символи («igin/lane/…»)
        lane = where_is.locate(self.repo, self.lane_only, self.live)["text"]
        self.assertIn("ЛИШЕ В ГІЛЦІ: origin/lane/integ-nae", lane)
        rc = where_is.locate(self.repo, self.rc_only, self.live)["text"]
        self.assertIn("(origin/rc/2026-11-23-b9)", rc)
        self.assertEqual(where_is.branches_containing(self.repo, self.lane_only, "origin/lane/*"),
                         ["origin/lane/integ-nae"])

    def test_local_branch_open_in_worktree(self):
        wt = os.path.join(self.tmp, "wt-local")
        g(self.repo, "worktree", "add", "-q", wt, "lane/local")  # git позначить гілку "+ "
        try:
            r = where_is.locate(self.repo, self.local_only, self.live)
            self.assertEqual(r["code"], "LOCAL")
            self.assertIn("(lane/local)", r["text"])
        finally:
            g(self.repo, "worktree", "remove", "--force", wt)

    def test_live_unknown(self):
        r = where_is.locate(self.repo, self.main_only, None)
        self.assertEqual(r["code"], "MAIN")
        self.assertIn("live невідомий", r["text"])

    def test_exit_codes_and_live_file(self):
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(where_is.main([self.on_live, "--live", self.live, "--repo", self.repo, "--no-fetch"]), 0)
            self.assertEqual(where_is.main([self.on_live, self.lane_only, "--live", self.live,
                                            "--repo", self.repo, "--no-fetch"]), 1)
            self.assertEqual(where_is.main(["deadbeef1", "--live", self.live, "--repo", self.repo, "--no-fetch"]), 2)
            os.makedirs(os.path.join(self.repo, ".sync-local"), exist_ok=True)
            with open(os.path.join(self.repo, ".sync-local", "LIVE-SHA"), "w", encoding="utf-8") as f:
                f.write(self.live[:9] + "  W15y\n")
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(where_is.main([self.on_live, "--repo", self.repo, "--no-fetch"]), 0)
            # запуск з робочої копії агента: LIVE-SHA береться з основної папки
            wt = os.path.join(self.tmp, "wt-agent")
            g(self.repo, "worktree", "add", "-q", "--detach", wt, "main")
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(where_is.main([self.on_live, "--repo", wt, "--no-fetch"]), 0)
            g(self.repo, "worktree", "remove", "--force", wt)
        os.remove(os.path.join(self.repo, ".sync-local", "LIVE-SHA"))


class WtAuditTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        _, self.repo = new_repo(self.tmp)
        r = self.repo
        self.rootA = os.path.join(self.tmp, "ECR-wt")
        self.rootB = os.path.join(self.tmp, "ECR-worktrees")
        self.rootC = os.path.join(r, ".claude", "worktrees")
        os.makedirs(self.rootC)
        with open(os.path.join(r, ".git", "info", "exclude"), "a") as f:
            f.write(".claude/\n")
        self.w1 = os.path.join(self.rootA, "w1")
        g(r, "worktree", "add", "-q", "-b", "lane/integ-w1", self.w1, "main")
        g(self.w1, "push", "-q", "origin", "lane/integ-w1")
        self.w2 = os.path.join(self.rootA, "lane", "w2")  # вкладена
        g(r, "worktree", "add", "-q", "-b", "lane/integ-w2", self.w2, "main")
        g(self.w2, "push", "-q", "origin", "lane/integ-w2")
        with open(os.path.join(self.w2, "Новий файл.cs"), "w", encoding="utf-8") as f:
            f.write("// незбережена робота\n")
        self.w3 = os.path.join(self.rootB, "w3")
        g(r, "worktree", "add", "-q", "-b", "lane/integ-w3", self.w3, "main")
        commit(self.w3, "w3.txt", "feat: unpushed")
        w4 = os.path.join(self.rootA, "w4")
        g(r, "worktree", "add", "-q", "-b", "lane/integ-w4", w4, "main")
        rmtree(w4)  # папку видалили, запис git лишився
        os.makedirs(os.path.join(self.rootA, "broken"))
        with open(os.path.join(self.rootA, "broken", "work.cs"), "w") as f:
            f.write("x")
        os.makedirs(os.path.join(self.rootB, "fakewt"))
        with open(os.path.join(self.rootB, "fakewt", ".git"), "w") as f:
            f.write("gitdir: /nowhere\n")
        subprocess.run([GIT, "clone", "-q", self.repo, os.path.join(self.rootB, "clone")], check=True,
                       stderr=subprocess.DEVNULL)

    def tearDown(self):
        rmtree(self.tmp)

    def find(self, rows, path):
        n = wt_audit.norm(path)
        return next(r for r in rows if wt_audit.norm(r["path"]) == n)

    def test_audit_now(self):
        rows, orphans = wt_audit.audit(self.repo, [self.rootA, self.rootB, self.rootC], idle_min=60)
        self.assertEqual(len(rows), 5)  # основна + w1..w4
        self.assertFalse(self.find(rows, self.repo)["flags"])
        self.assertFalse(self.find(rows, self.w1)["flags"])
        w2 = self.find(rows, self.w2)
        self.assertEqual(w2["dirty"], 1)
        self.assertFalse(w2["flags"])  # рух щойно
        w3 = self.find(rows, self.w3)
        self.assertEqual(w3["unpushed"], 1)
        missing = [r for r in rows if r["path"].endswith("w4")][0]
        self.assertTrue(any("ПАПКИ НЕМАЄ" in f for f in missing["flags"]))
        kinds = {os.path.basename(p): k for p, k in orphans}
        self.assertEqual(set(kinds), {"broken", "fakewt", "clone"})
        self.assertIn("не git-папка", kinds["broken"])
        self.assertIn("зламана", kinds["fakewt"])
        self.assertIn("окремий клон", kinds["clone"])
        self.assertNotIn("lane", kinds)  # контейнер вкладеної копії - не сирота

    def test_idle_and_dirty_main(self):
        with open(os.path.join(self.repo, "stray.cs"), "w") as f:
            f.write("x")
        rows, _ = wt_audit.audit(self.repo, [self.rootA, self.rootB], idle_min=60, now=time.time() + 3 * 3600)
        self.assertTrue(any("ОСНОВНА ПАПКА НЕЧИСТА" in f for f in self.find(rows, self.repo)["flags"]))
        self.assertTrue(any("НЕЗАКОМІЧЕНОЮ" in f for f in self.find(rows, self.w2)["flags"]))
        self.assertTrue(any("незапушеними" in f for f in self.find(rows, self.w3)["flags"]))
        self.assertFalse(self.find(rows, self.w1)["flags"])

    def test_cli_exit_code(self):
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(wt_audit.main(["--repo", self.repo, "--roots", self.rootA]), 1)

    def test_baseline_shows_only_new(self):
        """23.11: 116 позначок і 122 сироти - після першого разу звірку перестали запускати."""
        later = time.time() + 3 * 3600
        with open(os.path.join(self.repo, "stray.cs"), "w") as f:
            f.write("x")  # брудна основна папка в момент --ack: її однаково не можна «розібрати»
        rows, orphans = wt_audit.audit(self.repo, [self.rootA, self.rootB], 60, later)
        b = os.path.join(self.tmp, "baseline.json")
        wt_audit.save_baseline(b, wt_audit.current_keys(rows, orphans))
        nr, no = wt_audit.only_new(rows, orphans, wt_audit.load_baseline(b))
        self.assertEqual([f for r in nr if not r["main"] for f in r["flags"]], [])
        self.assertEqual(no, [])
        os.makedirs(os.path.join(self.rootA, "new-orphan"))
        rows, orphans = wt_audit.audit(self.repo, [self.rootA, self.rootB], 60, later)
        nr, no = wt_audit.only_new(rows, orphans, wt_audit.load_baseline(b))
        self.assertEqual([os.path.basename(c) for c, _ in no], ["new-orphan"])
        main_flags = [f for r in nr if r["main"] for f in r["flags"]]
        self.assertTrue(any("ОСНОВНА ПАПКА НЕЧИСТА" in f for f in main_flags))  # основну не «розібрати»
        self.assertEqual(sum(len(r["flags"]) for r in nr if not r["main"]), 0)

    def test_cli_ack_then_new_only(self):
        b = os.path.join(self.tmp, "b2.json")
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(wt_audit.main(["--repo", self.repo, "--roots", self.rootA, "--baseline", b, "--ack"]), 0)
            self.assertEqual(wt_audit.main(["--repo", self.repo, "--roots", self.rootA, "--baseline", b,
                                            "--new-only"]), 0)


class HourlyTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        _, r = new_repo(self.tmp)
        self.repo = r
        self.base = commit(r, "a.txt", "feat: база")
        g(r, "push", "-q", "origin", "main")
        os.makedirs(os.path.join(r, ".sync-local", "ready"))
        with open(os.path.join(r, ".git", "info", "exclude"), "a") as f:
            f.write(".sync-local/\n")
        self.bd = os.path.join(self.tmp, "app", "build.done")
        os.makedirs(os.path.dirname(self.bd))
        with open(self.bd, "w") as f:
            f.write("HEAD %s\n" % self.base)
        with open(os.path.join(r, ".sync-local", "LIVE-SHA"), "w") as f:
            f.write(self.base + "\n")

    def tearDown(self):
        rmtree(self.tmp)

    def run_hourly(self, *extra):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = hourly.main(["--repo", self.repo, "--no-fetch", "--live-build-done", self.bd,
                              "--min-disk-gb", "0", *extra])
        return rc, out.getvalue()

    def test_clean_is_one_line(self):
        rc, text = self.run_hourly()
        self.assertEqual(rc, 0, text)
        self.assertTrue(text.startswith("OK: "), text)
        self.assertNotIn("[!]", text)

    def test_acknowledged_worktree_is_silent(self):
        wt = os.path.join(self.tmp, "wt", "wip")
        g(self.repo, "worktree", "add", "-q", "-b", "wip/x", wt, "main")
        with open(os.path.join(wt, "w.cs"), "w") as f:
            f.write("x")
        rc, text = self.run_hourly("--idle-min", "0")
        self.assertEqual(rc, 1)
        self.assertIn("ПРОСТІЙ", text)
        with contextlib.redirect_stdout(io.StringIO()):
            wt_audit.main(["--repo", self.repo, "--idle-min", "0", "--ack"])
        rc, text = self.run_hourly("--idle-min", "0")
        self.assertEqual(rc, 0, text)

    def test_flags_collected(self):
        g(self.repo, "checkout", "-q", "-b", "lane/integ-new", "main")
        commit(self.repo, "n.txt", "feat: нова гілка без картки")
        g(self.repo, "checkout", "-q", "main")
        write_card(os.path.join(self.repo, ".sync-local", "ready"), "old.md", "OLD", "accepted",
                   [("AC-1", "x", "тест", self.base[:9])])
        newer = commit(self.repo, "a.txt", "feat: вийшло вікно")
        with open(self.bd, "w") as f:
            f.write("HEAD %s\n" % newer)
        rc, text = self.run_hourly("--min-disk-gb", "1e9")
        self.assertEqual(rc, 1)
        self.assertIn("ГІЛКА БЕЗ КАРТКИ lane/integ-new", text)
        self.assertIn("OLD [accepted]: СТАТУС ВІДСТАЄ", text)
        self.assertIn("LIVE-SHA у файлі", text)
        self.assertIn("ДИСК", text)


def write_card(folder, name, cid, status, acs, branch="", batch="", risk="R3", age_h=0.0):
    rows = "\n".join("| %s | %s | %s | %s |" % a for a in acs)
    text = ("# Картка: %s тест\nСтатус: %s\nГілка: %s\nПачка: %s\nРизик: %s\n\n"
            "| AC | Що | Доказ | SHA |\n|----|----|-------|-----|\n%s\n" % (cid, status, branch, batch, risk, rows))
    path = os.path.join(folder, name)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    if age_h:
        t = time.time() - age_h * 3600
        os.utime(path, (t, t))
    return path


class BoardTests(unittest.TestCase):
    """Відтворення реальних подій із TRACKING / RESUME координатора."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp()
        _, r = new_repo(cls.tmp)
        cls.repo = r
        cls.p1 = commit(r, "pt.txt", "B74 P1: схема PT01")
        cls.p2 = commit(r, "pt.txt", "B74 P2: читання")
        cls.ok = commit(r, "ok.txt", "feat: готова фіча")
        cls.live = g(r, "rev-parse", "HEAD")
        cls.main_only = commit(r, "m.txt", "feat: у main, ще не на live")
        g(r, "push", "-q", "origin", "main")
        g(r, "checkout", "-q", "-b", "lane/integ-nae-import", cls.live)
        cls.nae = commit(r, "nae.txt", "fix: NAE «Цех» D-569")
        g(r, "push", "-q", "origin", "lane/integ-nae-import")
        g(r, "checkout", "-q", "-b", "lane/integ-b11")
        g(r, "push", "-q", "origin", "lane/integ-b11")
        g(r, "checkout", "-q", "main")
        cls.cards = os.path.join(r, ".sync-local", "ready")
        os.makedirs(cls.cards)
        with open(os.path.join(r, ".git", "info", "exclude"), "a") as f:
            f.write(".sync-local/\n")
        with open(os.path.join(cls.cards, "aa-b114.md"), "w", encoding="utf-8") as f:
            f.write("# Лист готовності гілки (старий формат)\n- [ ] Хеш tip: abcdef1\n")
        cls.wt_idle = os.path.join(cls.tmp, "wt", "idle")
        g(r, "worktree", "add", "-q", "-b", "lane/integ-idle", cls.wt_idle, "main")
        cls.wt_dirty = os.path.join(cls.tmp, "wt", "dirty")
        g(r, "worktree", "add", "-q", "-b", "lane/integ-dirty", cls.wt_dirty, "main")
        with open(os.path.join(cls.wt_dirty, "wip.cs"), "w") as f:
            f.write("// робота\n")

    @classmethod
    def tearDownClass(cls):
        rmtree(cls.tmp)

    def flags(self, path, now_shift_h=0.0):
        c = board.parse_card(path)
        wts = board.worktrees_by_branch(self.repo)
        f, info, _ = board.check_card(self.repo, c, self.live, time.time() + now_shift_h * 3600, 60, wts)
        return " | ".join(f), " | ".join(info)

    def test_nae_claimed_live_but_only_in_lane(self):
        p = write_card(self.cards, "nae.md", "NAE", "live-verified",
                       [("AC-0", "кейс людини: Substance row + колонка B", "live, кроки людини", self.nae[:9])],
                       branch="lane/integ-nae-import")
        f, _ = self.flags(p)
        self.assertIn("заявлено «live-verified»", f)
        self.assertIn("ЛИШЕ В ГІЛЦІ", f)

    def test_b74_parts_without_sha(self):
        p = write_card(self.cards, "b74.md", "B74", "main",
                       [("AC-1", "P1 схема", "тест", self.p1[:9]), ("AC-2", "P2 читання", "тест", self.p2[:9]),
                        ("AC-3", "P3 шляхи запису", "тест", "(вписує агент)"),
                        ("AC-4", "P4 UI крок Template", "live", "")])
        f, _ = self.flags(p)
        self.assertIn("AC без SHA = не зроблено: AC-3, AC-4", f)

    def test_lesson_462_not_started_no_worktree(self):
        p = write_card(self.cards, "an-r2.md", "AN-r2", "in-progress", [("AC-1", "фікс", "тест", "")],
                       branch="lane/integ-an-r2", age_h=5)
        f, _ = self.flags(p)
        self.assertIn("НЕ ПОЧАТО?", f)

    def test_not_started_with_empty_worktree(self):
        p = write_card(self.cards, "idle.md", "IDLE", "in-progress", [("AC-1", "x", "тест", "")],
                       branch="lane/integ-idle")
        self.assertEqual(self.flags(p)[0], "")  # щойно видано - ще не простій
        self.assertIn("НЕ ПОЧАТО?", self.flags(p, now_shift_h=2)[0])

    def test_dirty_idle(self):
        p = write_card(self.cards, "dirty.md", "DIRTY", "in-progress", [("AC-1", "x", "тест", "")],
                       branch="lane/integ-dirty")
        self.assertIn("ПРОСТІЙ", self.flags(p, now_shift_h=2)[0])

    def test_good_card_and_drift(self):
        p = write_card(self.cards, "good.md", "GOOD", "live-verified",
                       [("AC-1", "фіча видно в UI", "live: скриншот", self.ok[:9])])
        self.assertEqual(self.flags(p)[0], "")
        # 24.11: картки лишились «accepted», хоча код уже в main і на live - тепер це позначка
        p2 = write_card(self.cards, "drift.md", "DRIFT", "reported", [("AC-1", "x", "тест", self.ok[:9])])
        self.assertIn("СТАТУС ВІДСТАЄ: код усіх AC уже НА LIVE", self.flags(p2)[0])
        p3 = write_card(self.cards, "drift2.md", "DRIFT2", "accepted", [("AC-1", "x", "тест", self.main_only[:9])])
        self.assertIn("СТАТУС ВІДСТАЄ: код усіх AC уже в main", self.flags(p3)[0])
        p4 = write_card(self.cards, "drift3.md", "DRIFT3", "main", [("AC-1", "x", "тест", self.main_only[:9])])
        self.assertEqual(self.flags(p4)[0], "")  # статус чесний - тиша

    def test_main_claim_but_only_lane_and_accepted_stale(self):
        p = write_card(self.cards, "m.md", "M", "main", [("AC-1", "x", "тест", self.nae[:9])])
        self.assertIn("заявлено «main»", self.flags(p)[0])
        p2 = write_card(self.cards, "acc.md", "ACC", "accepted", [("AC-1", "x", "тест", self.nae[:9])], age_h=30)
        self.assertIn("досі не в пачці", self.flags(p2)[0])

    def test_batched(self):
        ok = write_card(self.cards, "bok.md", "BOK", "batched", [("AC-1", "x", "тест", self.nae[:9])],
                        batch="lane/integ-b11")
        self.assertEqual(self.flags(ok)[0], "")
        bad = write_card(self.cards, "bbad.md", "BBAD", "batched", [("AC-1", "x", "тест", self.main_only[:9])],
                         batch="lane/integ-b11")
        self.assertIn("не в пачці", self.flags(bad)[0])
        nob = write_card(self.cards, "bnob.md", "BNOB", "batched", [("AC-1", "x", "тест", self.nae[:9])])
        self.assertIn("пачку", self.flags(nob)[0])

    def test_real_card_formats_24_11(self):
        """Формати з реальних карток 23–24.11: кілька SHA в комірці і AC-0 без коду."""
        p = write_card(self.cards, "builder.md", "BUILDER", "main",
                       [("AC-0", "live після вікна: doc 96, FormulaError = 0", "live: SQL до/після + UI", ""),
                        ("AC-1", "клон версії", "тести", "%s, %s" % (self.p1[:9], self.p2[:9])),
                        ("AC-3", "перепідписання одиниць", "6 тестів", "%s, %s, %s" % (self.p1[:9], self.p2[:9], self.ok[:9]))])
        c = board.parse_card(p)
        self.assertEqual(c["ac"][1]["shas"], [self.p1[:9], self.p2[:9]])
        self.assertEqual(len(c["ac"][2]["shas"]), 3)
        f, info = self.flags(p)
        self.assertNotIn("AC без SHA", f)
        self.assertIn("доводяться на live, не комітом: AC-0", info)
        # ...але не будь-який live-AC: UI-частина без коду лишається незробленою (B74 P4)
        p2 = write_card(self.cards, "b74ui.md", "B74UI", "accepted",
                        [("AC-0", "кейс людини", "live", ""), ("AC-P4", "UI крок Template", "live", "")])
        self.assertIn("AC без SHA = не зроблено: AC-P4", self.flags(p2)[0])

    def test_multi_sha_worst_location_wins(self):
        p = write_card(self.cards, "mix.md", "MIX", "main",
                       [("AC-1", "x", "тест", "%s, %s" % (self.ok[:9], self.nae[:9]))])
        f, _ = self.flags(p)
        self.assertIn("заявлено «main», а AC-1: ЛИШЕ В ГІЛЦІ", f)

    def test_lanes_without_cards(self):
        got = dict(board.lanes_without_cards(self.repo, [], days=2))
        self.assertIn("lane/integ-nae-import", got)          # коміти поза main
        self.assertIn("lane/integ-dirty", got)               # робоча копія зі змінами
        self.assertNotIn("lane/integ-b11", got)              # пачка
        self.assertNotIn("lane/integ-idle", got)             # порожня копія - це «НЕ ПОЧАТО?» картки, не тут
        with_card = [{"branch": "lane/integ-nae-import"}]
        self.assertNotIn("lane/integ-nae-import", dict(board.lanes_without_cards(self.repo, with_card, days=2)))

    def test_live_check_against_build_done(self):
        d = tempfile.mkdtemp(dir=self.tmp)
        bd = os.path.join(d, "build.done")
        with open(bd, "w") as f:
            f.write("build ok\nHEAD %s\n" % self.live)
        self.assertEqual(board.live_check(self.repo, self.live, build_done=bd)[0], [])
        mism = board.live_check(self.repo, self.p1, build_done=bd)[0]
        self.assertTrue(mism and "LIVE-SHA у файлі" in mism[0], mism)
        self.assertIn("не записано", board.live_check(self.repo, None, build_done=bd)[0][0])
        with open(bd, "w") as f:
            f.write("порожньо\n")
        flags, info = board.live_check(self.repo, self.live, build_done=bd)
        self.assertEqual(flags, [])
        self.assertIn("не визначено", info[0])

    def test_window_human_table(self):
        d = tempfile.mkdtemp(dir=self.tmp)
        write_card(d, "good.md", "GOOD", "main", [("AC-0", "людина бачить звіт у вкладці", "live", ""),
                                                  ("AC-1", "x", "тест", self.ok[:9])])
        write_card(d, "nae.md", "NAE", "accepted", [("AC-1", "x", "тест", self.nae[:9])])
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            board.main(["--repo", self.repo, "--cards", d, "--live", self.live, "--no-fetch", "--window", "origin/main"])
        text = out.getvalue()
        self.assertIn("що побачите після вікна\n- GOOD: людина бачить звіт у вкладці", text)
        self.assertIn("Чого ще НЕ буде після цього вікна\n- NAE:", text)

    def test_unknown_status_and_old_letters_skipped(self):
        p = write_card(self.cards, "u.md", "U", "done", [("AC-1", "x", "тест", self.ok[:9])])
        self.assertIn("невідомий статус", self.flags(p)[0])
        cards, skipped = board.load_cards(self.cards)
        self.assertGreaterEqual(skipped, 1)
        self.assertNotIn("aa-b114.md", [os.path.basename(c["path"]) for c in cards])

    def test_window(self):
        d = tempfile.mkdtemp(dir=self.tmp)
        write_card(d, "good.md", "GOOD", "main", [("AC-1", "фіча в UI", "live: скриншот", self.ok[:9])])
        write_card(d, "b74.md", "B74", "main", [("AC-1", "P1", "тест", self.p1[:9]), ("AC-3", "P3", "тест", "")])
        write_card(d, "nae.md", "NAE", "accepted", [("AC-0", "кейс людини", "live", self.nae[:9])])
        cards, _ = board.load_cards(d)
        going, partial, staying = board.window(self.repo, cards, g(self.repo, "rev-parse", "origin/main"))
        self.assertEqual([c["id"] for c in going], ["GOOD"])
        self.assertEqual([(c["id"], out) for c, out in partial], [("B74", ["AC-3"])])
        self.assertEqual([c["id"] for c, _ in staying], ["NAE"])
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = board.main(["--repo", self.repo, "--cards", d, "--live", self.live, "--no-fetch",
                             "--window", "origin/main"])
        self.assertEqual(rc, 1)  # частковий B74 - позначка
        self.assertIn("- [ ] GOOD AC-1: фіча в UI", out.getvalue())


class OverlapTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp()
        _, r = new_repo(cls.tmp)
        cls.repo = r
        mig = "src/Ecr.Infrastructure/Persistence/Migrations/"
        g(r, "checkout", "-q", "-b", "lane/a", "main")
        commit(r, "src/A.cs", "a1")
        commit(r, mig + "2026_a.cs", "a-mig")
        g(r, "push", "-q", "origin", "lane/a")
        g(r, "checkout", "-q", "-b", "lane/b", "main")
        commit(r, "src/A.cs", "b1")
        commit(r, "src/B.cs", "b2")
        commit(r, mig + "2026_b.cs", "b-mig")
        commit(r, mig + "EcrDbContextModelSnapshot.cs", "b-snap")
        g(r, "push", "-q", "origin", "lane/b")
        g(r, "checkout", "-q", "-b", "lane/c", "main")
        commit(r, "docs/tz/10-decisions.md", "c-decision")  # лише локально
        g(r, "checkout", "-q", "-b", "lane/integ-b11", "lane/a")  # пачка: містить чужі зміни
        commit(r, "src/B.cs", "batch: зведення")
        g(r, "checkout", "-q", "-b", "lane/old", "main")
        commit(r, "src/Old.cs", "old", date="2026-01-01T10:00:00")
        g(r, "checkout", "-q", "-b", "lane/merged", "main")
        g(r, "checkout", "-q", "main")
        g(r, "fetch", "-q", "origin")

    @classmethod
    def tearDownClass(cls):
        rmtree(cls.tmp)

    def test_active_lanes(self):
        lanes = overlap.active_lanes(self.repo, "origin/main", 7, False)
        self.assertEqual(sorted(lanes), ["lane/a", "lane/b", "lane/c"])
        with_b = overlap.active_lanes(self.repo, "origin/main", 7, True)
        self.assertIn("lane/integ-b11", with_b)
        self.assertIn("lane/old", overlap.active_lanes(self.repo, "origin/main", None, False))

    def test_local_unpushed_commits_count(self):
        wt = os.path.join(self.tmp, "wt-b")
        g(self.repo, "worktree", "add", "-q", wt, "lane/b")
        try:
            commit(wt, "src/C.cs", "b3 ще не запушено")
            lanes = overlap.active_lanes(self.repo, "origin/main", 7, False)
            self.assertIn("lane/b", lanes)
            self.assertNotIn("origin/lane/b", lanes)
            self.assertIn("src/C.cs", overlap.changed_files(self.repo, "origin/main", "lane/b"))
        finally:
            g(self.repo, "worktree", "remove", "--force", wt)
            g(self.repo, "branch", "-f", "lane/b", "origin/lane/b")

    def test_overlap_and_migrations(self):
        r = overlap.analyze(self.repo, ["lane/a", "lane/b", "lane/c"])
        self.assertEqual(sorted(r["shared"]["src/A.cs"]), ["lane/a", "lane/b"])
        text = " | ".join(r["flags"])
        self.assertIn("ПЕРЕТИН src/A.cs", text)
        self.assertIn("МІГРАЦІЇ у 2 гілках", text)
        self.assertIn("tz", r["hot"])
        self.assertNotIn("src/B.cs", r["shared"])

    def test_plan(self):
        lanes = ["lane/a", "lane/b", "lane/c"]
        flags, _ = overlap.plan_check(self.repo, ["src/B.cs"], lanes)
        self.assertEqual(len(flags), 1)
        self.assertIn("lane/b", flags[0])
        flags, notes = overlap.plan_check(self.repo, ["docs\\tz"], lanes)
        self.assertIn("lane/c", " ".join(flags))
        self.assertTrue(any("гарячий файл" in n for n in notes))
        self.assertEqual(overlap.plan_check(self.repo, ["src/Free.cs"], lanes)[0], [])
        # гарячі файли з великими літерами і з Windows-шляхом
        for p in ("src\\Ecr.Infrastructure\\Persistence\\EcrDbContextModelSnapshot.cs", "CLAUDE.md",
                  "src/Ecr.Api/ErrorCodes.cs", "Directory.Packages.props", "src/Ecr.Web/src/shared/ui"):
            self.assertTrue(overlap.plan_check(self.repo, [p], [])[1], p)
        self.assertFalse(overlap.plan_check(self.repo, ["src/Ecr.Api/Plain.cs"], [])[1])

    def test_cli(self):
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(overlap.main(["--repo", self.repo, "--no-fetch", "--all", "--days", "7"]), 1)
            self.assertEqual(overlap.main(["--repo", self.repo, "--no-fetch", "lane/c"]), 0)

    def test_default_is_cards_in_work(self):
        """23.11 режим «усі за 7 днів» дав 147 перетинів - типово беремо лише гілки карток у роботі."""
        d = tempfile.mkdtemp(dir=self.tmp)
        write_card(d, "a.md", "A", "in-progress", [("AC-1", "x", "тест", "")], branch="lane/a")
        write_card(d, "c.md", "C", "closed", [("AC-1", "x", "тест", "")], branch="lane/c")
        write_card(d, "m.md", "M", "in-progress", [("AC-1", "x", "тест", "")], branch="lane/merged")
        self.assertEqual(overlap.card_branches(d), ["lane/a", "lane/merged"])
        self.assertEqual(overlap.lanes_from_cards(self.repo, d), ["lane/a"])  # без комітів поза main - нема що перетинати
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = overlap.main(["--repo", self.repo, "--no-fetch", "--cards", d])
        self.assertEqual(rc, 0, out.getvalue())  # одна гілка - перетинати нема з чим
        self.assertIn("з карток у роботі", out.getvalue())
        empty = tempfile.mkdtemp(dir=self.tmp)
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            overlap.main(["--repo", self.repo, "--no-fetch", "--cards", empty])
        self.assertIn("карток у роботі немає", out.getvalue())


class LiveDetectTests(unittest.TestCase):
    """Розбір того, що на Windows повертає PowerShell для процесу на :5094 (сам виклик - лише на Windows)."""

    def test_binary_paths(self):
        out = ('7712\tC:\\Program Files\\dotnet\\dotnet.exe\t"C:\\Program Files\\dotnet\\dotnet.exe" '
               'F:\\ecr-backup\\land-ui\\app-W15y\\api\\Ecr.Api.dll --urls http://*:5094')
        self.assertEqual(where_is.binary_paths(out)[0], "F:\\ecr-backup\\land-ui\\app-W15y\\api\\Ecr.Api.dll")
        self.assertEqual(where_is.binary_paths('1\tF:\\app\\Ecr.Api.exe\t"F:\\app\\Ecr.Api.exe"'), ["F:\\app\\Ecr.Api.exe"])
        self.assertEqual(where_is.binary_paths(""), [])

    def test_sha_from_build_done(self):
        full = "a9a3c0067e4bbe8d9aea8377f4ef071818ff6417"
        self.assertEqual(where_is.sha_from_build_done("HEAD %s\n" % full), full)
        self.assertEqual(where_is.sha_from_build_done("built 24.11\nHEAD=b3060f0fd\n"), "b3060f0fd")
        self.assertEqual(where_is.sha_from_build_done("commit %s" % full), full)
        self.assertIsNone(where_is.sha_from_build_done("ok"))

    def test_find_build_done_up_the_tree(self):
        tmp = tempfile.mkdtemp()
        try:
            api = os.path.join(tmp, "app-W15z", "api")
            os.makedirs(api)
            with open(os.path.join(tmp, "app-W15z", "build.done"), "w") as f:
                f.write("HEAD b3060f0fd\n")
            dll = os.path.join(api, "Ecr.Api.dll")
            self.assertEqual(where_is.find_build_done(dll), os.path.join(tmp, "app-W15z", "build.done"))
            sha, src = where_is.detect_live(build_done=os.path.join(tmp, "app-W15z", "build.done"))
            self.assertEqual(sha, "b3060f0fd")
        finally:
            rmtree(tmp)


class RouteTests(unittest.TestCase):
    def test_agent(self):
        d = route.decide
        self.assertEqual(d("agent", None, 5.9, 0, 100)[0], "ЧЕКАТИ")
        self.assertEqual(d("agent", None, 6.0, 0, 100)[0], "МОЖНА")
        self.assertEqual(d("agent", 0, None, 0, 100)[0], "ЧЕКАТИ")
        # 24.11: диск H: 0 ГБ - робоча копія агента зникла
        self.assertEqual(d("agent", None, 8.0, 0, 19.9)[0], "ЧЕКАТИ")
        self.assertEqual(d("agent", None, 8.0, 0, 20.0)[0], "МОЖНА")
        self.assertEqual(d("agent", None, 8.0, 0, None)[0], "МОЖНА")

    def test_decide(self):
        d = route.decide
        self.assertEqual(d("full", 0, 2.0, 5, 10)[0], "CI")
        # головне правило: черга порожня -> CI, навіть коли локально все вільно
        self.assertEqual(d("full", 0, 16.0, 0, 200)[0], "CI")
        self.assertEqual(d("smoke", 0, 16.0, 0, 200)[0], "CI")
        self.assertEqual(d("full", 5, 8.0, 1, 100)[0], "ЛОКАЛЬНО")
        self.assertEqual(d("full", 5, 3.0, 1, 100)[0], "CI")
        self.assertEqual(d("full", 5, 8.0, 3, 100)[0], "CI")
        self.assertEqual(d("full", None, 8.0, 0, 100)[0], "ЛОКАЛЬНО")
        self.assertEqual(d("full", None, None, None, None)[0], "CI")
        self.assertEqual(d("smoke", 3, 8.0, 0, 40)[0], "CI")
        self.assertEqual(d("smoke", 3, 8.0, 0, 80)[0], "ЛОКАЛЬНО")
        self.assertEqual(d("timing", 9, 3.9, 0, 10)[0], "ЧЕКАТИ")
        self.assertEqual(d("timing", 9, 4.0, 2, 10)[0], "ЛОКАЛЬНО")
        self.assertEqual(d("timing", 0, 8.0, 3, 10)[0], "ЧЕКАТИ")
        self.assertEqual(d("timing", 0, None, 0, 10)[0], "ЧЕКАТИ")
        self.assertEqual(d("full", 5, 6.0, 2, 100)[0], "ЛОКАЛЬНО")  # межа включно

    def test_cli_manual(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            route.main(["--kind", "full", "--queued", "4", "--free-gb", "7.5", "--testhost", "0", "--disk-gb", "80"])
        self.assertIn("РІШЕННЯ (full): ЛОКАЛЬНО", out.getvalue())

    def test_cli_with_fake_gh(self):
        if os.name == "nt":
            self.skipTest("фейковий gh - лише POSIX")
        tmp = tempfile.mkdtemp()
        try:
            fake = os.path.join(tmp, "gh")
            with open(fake, "w") as f:
                f.write('#!/bin/sh\ncase "$*" in *queued*) echo "[{},{},{}]";; *) echo "[{}]";; esac\n')
            os.chmod(fake, 0o755)
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                route.main(["--kind", "full", "--gh", fake, "--free-gb", "1", "--testhost", "0"])
            text = out.getvalue()
            self.assertIn("черга GitHub=3", text)
            self.assertIn("РІШЕННЯ (full): CI", text)
        finally:
            rmtree(tmp)


if __name__ == "__main__":
    unittest.main(verbosity=2)
