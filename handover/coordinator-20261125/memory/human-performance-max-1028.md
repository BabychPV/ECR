---
name: human-performance-max-1028
description: "Людина 28.10: продуктивність — максимально швидко; бюджети перфу жорсткі, не «≤+30 % до попереднього»"
metadata:
  node_type: memory
  type: feedback
  originSessionId: f754f160-40fb-436c-bec5-3c438a33d976
  modified: 2026-10-28T08:10:02.580Z
---

Людина 28.10 (після перф-регресії p66 у звітних в'ю Q-17): «з приводу продуктивності ми маємо бути максимально швидкі».

**Why:** відносна ціль «не гірше попереднього +30 %» дозволяла повільні базові значення (T3_00 232 с) і регресії до десятків секунд.

**How to apply:** у брифах ставити АБСОЛЮТНІ бюджети: GET слайсу/status ≤ 1 с теплий; звітна в'ю ON ≤ 1,2× OFF і ≤ 2 с теплий для будь-якої таблиці Land, найважча (T3_00) ≤ 5 с теплий / ≤ 15 с холодний; запис комірки p95 ≤ 200 мс. Індекси/міграції/матеріалізацію дозволяти одразу, якщо потрібні для бюджету. Land-замір обов'язковий до мержу. Пов'язано: [[self-improve-practices]], [[human-q17-month-source-1026]].

**10.11 reminder:** «Не забувай про продуктивність на великих навантаженнях» — load numbers on the large Land backup (predelete2, 95 docs, ~6M cells) are required in every ready letter for calc/access/template-layout changes (AN calc issues, PE new Land version + Layout mode).
