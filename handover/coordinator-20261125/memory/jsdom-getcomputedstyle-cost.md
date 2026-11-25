---
name: jsdom-getcomputedstyle-cost
description: "плаваючі таймаути тестів форм під навантаженням — це getComputedStyle від getByRole і user-event, а не компонент"
metadata: 
  node_type: memory
  type: project
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-21T09:03:41.404Z
---

2026-09-21: `GroupAssignmentsPanel.test.tsx` і `StyleEditor.fontSizeDecimal` падали на таймауті лише в повному прогоні під навантаженням. Окремо вони завжди проходили.

Профіль (`node:inspector`): ~40 % CPU іде на jsdom `getComputedStyle`. Джерела:
- `getByRole`/`findByRole` — `queryAllByRole` рахує видимість і доступне ім'я, піднімаючись предками до `<html>`;
- перевірка `pointer-events` у `user-event` перед кожним кліком і `tab()`.

Кеш стилів скидається після кожної зміни DOM. Заглушка `:modal`/`:fullscreen` у `setup.ts` тут ні до чого.

Лікування в тесті (коміт `a57740bf`):
- `userEvent.setup({ delay: null, pointerEventsCheck: Never })`;
- `getByLabelText` замість `getByRole` для полів;
- кнопку шукати один раз;
- довгий ввід робити через `fireEvent.change`.

Окремий випадок — `lazy()` на рівні модуля: перший `findBy*` у файлі чекає 1 с разом із Suspense. Рішення — `beforeAll`, що один раз рендерить форму.

**Why:** «підняти таймаут» ховає проблему; корінь — вартість запитів за роллю в jsdom.
**How to apply:** плаваючий таймаут у тесті великої форми → спершу профіль і ці заміни, не таймаут. Див. [[slow-modal-test]].
