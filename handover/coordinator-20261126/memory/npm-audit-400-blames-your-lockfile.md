---
name: npm-audit-400-blames-your-lockfile
description: "Гейт client червоніє на npm audit із «Invalid package tree, run npm install» — а винен збій npmjs, не lock-файл"
metadata: 
  node_type: memory
  type: reference
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-19T17:16:48.656Z
---

`npm audit` у гейті `client` (`tools/verify-all.ps1`, крок «Безпека
залежностей») може впасти з:

```
npm warn audit 400 Bad Request - POST .../security/audits/quick
{ error: 'Bad Request', message: 'Invalid package tree, run  npm install  to rebuild your package-lock.json' }
```

**Повідомлення бреше.** `package-lock.json` цілий (перевірено:
`lockfileVersion: 3`, одразу після `npm ci`). Справжній ланцюг:

1. npm іде на **bulk**-ендпойнт;
2. той віддає **503 «We are currently performing maintenance»**
   (status.npmjs.org);
3. npm тихо відкочується на **quick**-ендпойнт, який npm **вивела з
   експлуатації**;
4. quick віддає 400 з текстом про lock-файл — і саме його видно.

⚠ npm 10 приховує крок 1: у її виводі є лише 400 про quick. Щоб побачити
503, потрібна npm 11:

```bash
npx --yes npm@11 audit --omit=dev --audit-level=high
```

⛔ Не «лагодити» гейт (знімати `--audit-level`, додавати `|| true`,
перегенеровувати lock) — це збій чужої інфраструктури, він минає сам.
Перезапустити джобу `client`, коли npmjs відновиться.

2026-09-19: у такому стані були [[merge-cadence]] PR #395 і #396 — обидва з
1217/1217 і 204/204 зеленими тестами в тому ж логу. Тести йдуть ПІСЛЯ
аудиту й друкуються далі за ним, тому «client fail» тут не означає
«тести впали»: дивись, який саме крок `✗`.
