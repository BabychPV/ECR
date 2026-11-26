#!/usr/bin/env python3
"""route.py - де запускати важкий прогін зараз: у CI чи локально.

Рішення людини 23.11: «залежить від навантаження GitHub або локального ПК».
Скрипт міряє обидва і каже одне слово: CI / ЛОКАЛЬНО / ЧЕКАТИ (для agent - МОЖНА / ЧЕКАТИ).

  --kind full    повні набори тестів (Application, Infrastructure, Api ...)
  --kind smoke   smoke.ps1 / e2e-stand.ps1 (створюють бази - потрібен диск)
  --kind timing  тести часу і черг, 20 повторів (рахуються лише локально)
  --kind agent   чи можна запустити ще одного агента: >= 6 ГБ пам'яті і >= 20 ГБ на диску робочих копій (--disk)

Пороги типово взяті з правил координатора: новий важкий процес - при >= 6 ГБ
вільної пам'яті; тести часу - при >= 4 ГБ; важких тестових процесів <= 2
(CLAUDE.md, 30.09); диск H: >= 50 ГБ (поріг прибирання в ctl.ps1).
Значення можна задати вручну (--queued, --free-gb, --testhost, --disk-gb).
"""
import argparse
import csv
import glob
import io
import json
import os
import shutil
import subprocess
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def decide(kind, queued, free_gb, testhost, disk_gb, min_free=6.0, min_free_timing=4.0,
           max_testhost=2, min_disk=50.0, min_disk_agent=20.0):
    """Чиста функція рішення. Повертає (слово, [причини])."""
    why = []
    if kind == "agent":
        if free_gb is None:
            return "ЧЕКАТИ", ["пам'ять не виміряно - нового агента не запускати наосліп"]
        if free_gb < min_free:
            return "ЧЕКАТИ", ["вільно %.1f ГБ < %.1f: новий агент не запускати" % (free_gb, min_free)]
        # 24.11: диск H: дійшов до 0 ГБ, і робоча копія агента зникла посеред роботи
        if disk_gb is not None and disk_gb < min_disk_agent:
            return "ЧЕКАТИ", ["на диску робочих копій вільно %.0f ГБ < %.0f: спершу прибрати влиті копії (тріаж wt_audit)"
                              % (disk_gb, min_disk_agent)]
        why = ["вільно %.1f ГБ >= %.1f" % (free_gb, min_free)]
        why.append("диск %s" % ("не виміряно" if disk_gb is None else "%.0f ГБ >= %.0f" % (disk_gb, min_disk_agent)))
        return "МОЖНА", why
    if kind == "timing":
        if free_gb is None:
            return "ЧЕКАТИ", ["пам'ять не виміряно - тест часу без цього не рахується"]
        if free_gb < min_free_timing:
            return "ЧЕКАТИ", ["вільно %.1f ГБ < %.1f: тест часу зараз не рахується" % (free_gb, min_free_timing)]
        if testhost is not None and testhost > max_testhost:
            return "ЧЕКАТИ", ["іде %d тестових процесів > %d: час буде спотворено" % (testhost, max_testhost)]
        return "ЛОКАЛЬНО", ["вільно %.1f ГБ, тестових процесів %s" % (free_gb, testhost)]
    local_ok, local_why = True, []
    if free_gb is None:
        local_ok = False
        local_why.append("пам'ять не виміряно")
    elif free_gb < min_free:
        local_ok = False
        local_why.append("вільно %.1f ГБ < %.1f" % (free_gb, min_free))
    if testhost is not None and testhost > max_testhost:
        local_ok = False
        local_why.append("тестових процесів %d > %d" % (testhost, max_testhost))
    if kind == "smoke":
        if disk_gb is None or disk_gb < min_disk:
            local_ok = False
            local_why.append("диск %s ГБ < %.0f" % ("?" if disk_gb is None else "%.0f" % disk_gb, min_disk))
    if queued == 0:
        why.append("черга GitHub порожня - CI стартує одразу, локальні ресурси лишаються агентам")
        return "CI", why
    if local_ok:
        if queued is None:
            why.append("стан черги GitHub невідомий, локально ресурс є")
        else:
            why.append("у черзі GitHub %d прогонів, локально ресурс є" % queued)
        if kind == "smoke":
            why.append("smoke/e2e локально - послідовно, не паралельно з іншими важкими (CLAUDE.md)")
        return "ЛОКАЛЬНО", why
    why.append("локально не можна: " + "; ".join(local_why))
    if queued:
        why.append("у черзі GitHub %d прогонів - чекати CI, двох важких одночасно не запускати" % queued)
    return "CI", why


def find_gh(explicit):
    if explicit:
        return explicit
    p = shutil.which("gh")
    if p:
        return p
    la = os.environ.get("LOCALAPPDATA")
    if la:  # шлях, яким користується ctl.ps1
        hits = glob.glob(os.path.join(la, "Microsoft", "WinGet", "Packages", "GitHub.cli_*", "bin", "gh.exe"))
        if hits:
            return hits[0]
    return None


def gh_count(gh, slug, status):
    try:
        p = subprocess.run([gh, "run", "list", "-R", slug, "--status", status, "--limit", "100",
                            "--json", "databaseId"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        return None
    if p.returncode != 0:
        return None
    try:
        return len(json.loads(p.stdout.decode("utf-8", "replace") or "[]"))
    except ValueError:
        return None


def free_memory_gb():
    if os.name == "nt":
        import ctypes

        class MEMORYSTATUSEX(ctypes.Structure):
            _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                        ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                        ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                        ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                        ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
        st = MEMORYSTATUSEX()
        st.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
        if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(st)):
            return None
        return st.ullAvailPhys / 1024 ** 3  # те саме «Доступно», що в диспетчері задач
    try:
        with open("/proc/meminfo") as f:
            for line in f:
                if line.startswith("MemAvailable:"):
                    return int(line.split()[1]) / 1024 ** 2
    except OSError:
        pass
    return None


def process_names():
    try:
        if os.name == "nt":
            p = subprocess.run(["tasklist", "/FO", "CSV", "/NH"], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            text = p.stdout.decode("mbcs", "replace")
            return [row[0] for row in csv.reader(io.StringIO(text)) if row]
        p = subprocess.run(["ps", "-eo", "comm="], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        return [l.strip() for l in p.stdout.decode("utf-8", "replace").splitlines() if l.strip()]
    except OSError:
        return None


def count(names, prefix):
    if names is None:
        return None
    return sum(1 for n in names if n.lower().startswith(prefix))


def main(argv=None):
    ap = argparse.ArgumentParser(description="CI чи локально: рішення за навантаженням.")
    ap.add_argument("--kind", choices=["full", "smoke", "timing", "agent"], required=True)
    ap.add_argument("--repo-slug", default="BabychPV/ECR")
    ap.add_argument("--gh")
    ap.add_argument("--disk", default=".", help="шлях на диску, де живуть бази й робочі копії (типово поточна папка)")
    ap.add_argument("--min-free-gb", type=float, default=6.0)
    ap.add_argument("--min-free-timing-gb", type=float, default=4.0)
    ap.add_argument("--max-testhost", type=int, default=2)
    ap.add_argument("--min-disk-gb", type=float, default=50.0)
    ap.add_argument("--min-disk-agent-gb", type=float, default=20.0)
    ap.add_argument("--queued", type=int)
    ap.add_argument("--free-gb", type=float)
    ap.add_argument("--testhost", type=int)
    ap.add_argument("--disk-gb", type=float)
    a = ap.parse_args(argv)

    queued, inprog = a.queued, None
    if queued is None and a.kind not in ("timing", "agent"):
        gh = find_gh(a.gh)
        if gh:
            queued = gh_count(gh, a.repo_slug, "queued")
            inprog = gh_count(gh, a.repo_slug, "in_progress")
    names = process_names() if (a.testhost is None or a.free_gb is None) else None
    free_gb = a.free_gb if a.free_gb is not None else free_memory_gb()
    testhost = a.testhost if a.testhost is not None else count(names, "testhost")
    disk_gb = a.disk_gb
    if disk_gb is None:
        try:
            disk_gb = shutil.disk_usage(a.disk).free / 1024 ** 3
        except OSError:
            disk_gb = None
    word, why = decide(a.kind, queued, free_gb, testhost, disk_gb, a.min_free_gb,
                       a.min_free_timing_gb, a.max_testhost, a.min_disk_gb, a.min_disk_agent_gb)
    print("РІШЕННЯ (%s): %s" % (a.kind, word))
    for w in why:
        print("  - " + w)
    fmt = lambda v, f="%s": "?" if v is None else f % v  # noqa: E731
    print("  виміряно: черга GitHub=%s, іде=%s; вільно=%s ГБ; testhost=%s; VBCSCompiler=%s; dotnet=%s; диск=%s ГБ"
          % (fmt(queued), fmt(inprog), fmt(free_gb, "%.1f"), fmt(testhost),
             fmt(count(names, "vbcscompiler")), fmt(count(names, "dotnet")), fmt(disk_gb, "%.0f")))
    return 0


if __name__ == "__main__":
    sys.exit(main())
