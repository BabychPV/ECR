---
name: enqueue-exclusive-supersedes
description: "IBackgroundJobScheduler.EnqueueExclusiveAsync ВИТІСНЯЄ задачу, що вже виконується (Quartz перериває, Database — CancelRequested); для автотригерів брати EnqueueCoalescedAsync (з 493da05e)"
metadata:
  node_type: memory
  type: project
  originSessionId: fbcb71e0-68fa-420a-b5a5-480d1ea09ee5
  modified: 2026-09-29T18:50:23.428Z
---

`EnqueueExclusiveAsync` не просто прибирає дублі. Він скасовує попередника з тим самим ключем:
- у Quartz (`QuartzJobScheduler`, H-23c) — видаляє з черги або перериває виконувану задачу в межах процесу; між інстансами дедупу немає;
- у Database (`DbBackgroundJobScheduler`, F1c) — `SupersedeRunning=true`: виконувана задача отримує `CancelRequested`.

Ключ у Database — `{TypeName}~{target}`. Дедуп між двома викликачами буде лише тоді, коли збігаються і маркер задачі, і target. Наприклад, перерахунок методологій — це `IRecalculationJob` з target `doc{id}-p{period}` (`RecalculateDocumentHandler.TargetOf`). PATCH комірок ставить інше: `IFormulaRecalculationJob` без target.

2026-09-29 «Аналіз» додала `EnqueueCoalescedAsync` (у вершині з `493da05e`). У Database вона зливає нову задачу з тією, що чекає в Queued, і не перериває Running. У Quartz вона пропускає нову задачу, якщо така вже є, а після завершення Running робить рівно одну перепостановку.

**Why:** автотригер HSE301 A4 спершу стояв на Exclusive. Матеріалізація йде окремим прогоном на кожну сутність, тож на гарячому документі перерахунок міг ніколи не дорахуватися.

**How to apply:** для автоматичних тригерів (після матеріалізації, синку) бери `EnqueueCoalescedAsync`. `Exclusive` — лише для явної дії людини «перерахувати зараз». Пов'язане: [[hse301-priority-eventframe]].
