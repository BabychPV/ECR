---
name: human-live-grant-pt01-1113
description: Людина 13.11 16:20Z «я дозволяю» — GRANT EXECUTE на doc.fn_TemplateVersionOf логіну live-застосунку у вікні W15u (лише цей грант)
metadata:
  node_type: memory
  type: project
  originSessionId: 63e87d11-16e6-467b-afcb-ccd5cf06c240
  modified: 2026-11-13T16:23:50.262Z
---

**Слово людини.** 13.11 ~16:20Z, у сесії координатора, людина відповіла «я дозволяю». Це відповідь на моє попередження: PT01 (B74) додає функцію `doc.fn_TemplateVersionOf`. Якщо live-застосунок входить у базу під обмеженим логіном, цьому логіну потрібен `GRANT EXECUTE`.

**Межі дозволу:**
- Тільки один грант: `GRANT EXECUTE ON OBJECT::doc.fn_TemplateVersionOf TO <логін live-застосунку>`.
- Тільки у вікні W15u і тільки після міграції PT01.
- Виконує «Аудит-2» (AU) і записує в журнал вікна.

**Why:** ACL і права на live змінюються лише за окремим словом людини. Це слово стосується саме цього гранту.

**How to apply:** не поширювати на інші гранти чи вікна. Новий об'єкт БД, якому потрібен грант на live, вимагає нового слова людини. Пов'язано: [[human-db-grants-in-install-guide-1102]], [[no-accounts-or-rights-even-scratch-1108]].
