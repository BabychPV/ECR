---
name: disks-ssd-vs-hdd-1124
description: "Диски машини: C:/G:/H: — SSD (швидкий), D:/E:/F: — один HDD RAID (повільний, 24.11 був 100 % зайнятий). Робочі копії, тестові/scratch бази, збірки — на SSD (C:\\ecr-wt, C:\\EcrData); HDD лише для бекапів/архівів"
metadata:
  node_type: memory
  type: user
  originSessionId: d15bf7c2-34d1-400b-ad82-057f935d757c
  modified: 2026-11-24T20:49:07.717Z
---

Людина 24.11 ~22:40 (скрин диспетчера задач): «врахуй що я маю швидкий і повільний диск». Disk 0 (D: E: F:) — HDD RAID, 100 % активності; Disk 1 (C: G: H:) — SSD RAID, 10 %. Пам'ять 87 %.

**Why:** я 24.11 гнав агентів і scratch-бази на E:/F: «бо там місце» → HDD 100 %, hourly.py 69 хв, файл на F: писався 330 мс, стенд e2e виділяв 4 ГБ за 45 хв.

✎ 24.11 ~23:00 людина звільнила SSD: «я звільнив місце на ссд G:\EcrData» — G: ~284 ГБ вільно → scratch/тестові бази першочергово `-DataPath G:\EcrData`.

**How to apply:** нові робочі копії — `C:\ecr-wt\<ім'я>`; scratch/тестові бази — `-DataPath C:\EcrData` (C: ~128 ГБ вільно); H: — живі бази й код; G: — запас. D:/E:/F: — лише бекапи (.bak), архіви, рідко читане. Перед стартом агента дивитись і вільне місце SSD, і завантаження HDD. Уточнює [[stand-data-lives-on-two-disks]] і правило CLAUDE.md «worktree лише H:\ecr-wt або F:» (F: — повільний). Пов'язано: [[self-improve-practices]] урок 514.
