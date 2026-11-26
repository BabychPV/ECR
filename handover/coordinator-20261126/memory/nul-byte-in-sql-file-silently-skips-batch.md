---
name: nul-byte-in-sql-file-silently-skips-batch
description: Байт NUL у коментарі Sql/*.sql змушує sqlcmd -b -I мовчки пропустити ALTER (exit 0); DeployScriptsRerunTests (SqlClient) цього не ловить
metadata:
  node_type: memory
  type: reference
  originSessionId: cb9a6802-7e74-4b2a-a2e8-5d1277869b04
  modified: 2026-09-30T21:01:01.442Z
---

2026-09-30: у `Sql/11-audit-tables.sql` (коміт 7d44aa2d, ФВ-2.16) у коментар потрапив NUL (через Edit/Write з `\u0000`). `sqlcmd -b -I -i` відпрацював з exit 0, але блок `IF COL_LENGTH(...) IS NULL ALTER TABLE ... ADD` не виконався: на оновлюваній базі колонки немає, PATCH комірок — 500. На нових базах (CREATE TABLE) усе гаразд. Знайшла перевірка оновлення main→вершина на базі з даними.

Виправлено 811f370d: сторож `ScriptControlBytesTests` (байти <0x20 в .sql/.ps1/…) і `DeployAuditUpgradeSqlcmdTests` (справжній sqlcmd). Причина, імовірно: PowerShell у подвійних лапках — `` `0 `` = NUL, `` `a `` = BEL (там же знайдено BEL у 12-archive-tables.sql).

**Why:** тести розгортання ганяють скрипти через SqlClient, а прод — через sqlcmd.
**How to apply:** перевіряти байти 0x00 у всіх Sql/*.sql і deploy-скриптах (скрипт на .NET, `grep` на Windows не допоможе); сторож Architecture «жодного NUL» додає «Аудит»; перевірку оновлення робити саме через sqlcmd. Пов'язано: [[write-tool-decodes-unicode-escapes]], [[ripgrep-skips-files-with-nul]].
