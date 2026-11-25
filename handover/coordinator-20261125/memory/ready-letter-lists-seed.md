---
name: ready-letter-lists-seed
description: "Лист готовності (.sync-local/ready) має явно називати зміни сіду (UiString/seed/features/*.sql), не лише міграцію/контракт"
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-05T08:06:12.715Z
---

У листі готовності lane завжди окремим рядком перелічувати зміни сіду: нові/змінені файли `seed/features/*.sql`, ключі UiString, `09-seed.sql`. Також міграцію, контракт, shared/ui, lock, .github — навіть коли «немає».

**Why:** координатор 05.11 (#734 frozen-cols): у READY не було згадки про сід `UI-grid-frozen-columns.sql`, хоча він ризиковий для оновлення бази (MERGE UiString лише вставляє).

**How to apply:** у рядку «Risky parts» писати `seed: <файли або none>` поруч із migration/contract. Див. [[dev-rules]].
