---
name: vi-mock-userevent-dynamic-import
description: "vi.mock не доїжджає до lazy import() усередині компонента, якщо клік іде через userEvent — fireEvent ловить, userEvent ні"
metadata: 
  node_type: memory
  type: project
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-22T05:49:49.610Z
---

2026-09-22: тест на «палітра/шухляда вантажиться лінивим `import()`, а не статично» давав хибний зелений під `userEvent.click`, хоча та сама фабрика `vi.mock` (перевірено `console.log` усередині фабрики — вона виконувалась) не потрапляла в змонтоване дерево. З `fireEvent.click` той самий тест ловив мутацію (статичний імпорт) правильно.

А/Б на одному файлі: `fireEvent.click` → рендериться заглушка з моку; `userEvent.click` → у дереві **справжній** модуль.

**Why:** невідома точна причина (імовірно, `userEvent` чекає мікротаски/макротаски інакше, ніж синхронний `fireEvent`, і це якось розходиться з тим, коли резолвиться `import()` під моком) — але наслідок відтворюваний.

**How to apply:** тест, що доводить лінивість модуля через мутацію «статичний імпорт замість `import()`», писати на `fireEvent.click`, не на `userEvent.click`. Перевіряти новий такий тест мутацією обов'язково — і саме мутацією ловиться ця пастка, а не читанням коду тесту.

Див. [[jsdom-getcomputedstyle-cost]], [[flaky-tests-lazy-import-warmup]].
