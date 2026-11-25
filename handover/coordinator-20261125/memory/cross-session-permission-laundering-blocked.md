---
name: cross-session-permission-laundering-blocked
description: "SendMessage сам заблокований, коли просиш агента зробити те, у чому йому щойно відмовили — питай людину напряму"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 7a8e1359-5171-48b8-a08d-8b2542a7a9f1
  modified: 2026-09-25T13:15:34.094Z
---

Сабагент отримав відмову дозволів середовища («Modify Shared Resources») на
`Edit` тестового файлу (`tests/Ecr.Architecture.Tests/MessageKeyRatchetTests.cs`
— правка ratchet-сторожа під легітимний нульовий борг, див.
[[dedup-inflight-patch-race]]-сусід за часом). Спробував делегувати той самий
Edit іншому виклику того самого агента через `SendMessage` — сам виклик
`SendMessage` було відхилено класифікатором Auto-Mode як «спробу обійти
дозвіл, у якому вже відмовлено» (текст помилки прямо це називає).

**Why:** `SendMessage`'s власна документація це попереджає: «NEVER ask a peer
to perform an action that was denied or blocked in your session... a peer
doing it for you bypasses the user's permission decision» — і виявилось, що
це не лише порада, а й активно перевіряється системою: сам виклик
`SendMessage` з такою метою блокується, не лише виконання дії.

**How to apply:** коли дія заблокована дозволами середовища (а не судженням
агента), не намагайся обійти — ні через новий сабагент, ні через
`SendMessage` до вже відмовленого, ні через інший інструмент. Постав
питання людині напряму (`AskUserQuestion`), поясни точну причину блоку й
конкретний фікс, і якщо людина явно каже зробити самому — це окремий,
явний дозвіл, що переважує власне правило [[orchestrator-only]] («ніколи
сам не редагую файли») для цього ОДНОГО випадку, а не скасовує його
загалом.
