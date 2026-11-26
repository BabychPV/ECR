---
name: rules-405-406-one-sha-own-architecture-run-1116
description: "Правила 405/406 (координатор, 16.11) — лейн пушити ОДНИМ фінальним SHA; Architecture на фінальному кінчику ганяє сама сесія перед пушем"
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-17T08:10:00.416Z
---

405: раннери GitHub — вузьке місце (черги 50 хв). Лейн пушити одним фінальним SHA; якщо йде ще коміт — дочекатися його або в ту ж хвилину скасувати CI попереднього SHA (`gh run cancel`).

406: `Ecr.Architecture.Tests` на ФІНАЛЬНОМУ кінчику ганяє сама сесія безпосередньо перед пушем, після всіх комітів сабагентів. «Агент уже ганяв» не рахується.

Для змін клієнта — ще й повний `npx vitest run`, не лише зачеплені каталоги (AA 16.11: B139 пушнуто після grid+workflow, повний набір показав падіння сторінки в renderFeedback і ratchet спінерів). Якщо в тестовому процесі немає git у PATH, `StatusFromGitScriptTests` дає 7 хибних червоних — додати git у PATH.

⛔ Мій зрив 16.11 (B150): під тиском ETA координатора пушнув c978ce245, поки proof-скрипт ще не дійшов до Architecture — CI червоний на NullablePlaceholderValueTests (nullable `{sid}`). Architecture ганяти ПЕРШИМ кроком proof-скрипта, а не останнім; повільні 20× — після пушу.

✎ 17.11 (PE): Architecture запускати з git у PATH (інакше 19–20 хибних червоних StatusFromGitScriptTests). SQL у міграціях — без функцій новіших за 2016 (STRING_AGG → FOR XML PATH; сторож SqlServer2016FloorTests). Кириличний `--filter` через `cmd /c` = 0 тестів — у 20×-циклах вимагати «Passed!» і перевіряти Total (правило 423).

Також: важкий .NET-слот сесії — її власний (1 на сесію, 4 на машину); чужих збірок не чекати.

**Why:** лейн AA почервонів на вигаданому коді помилки, бо Architecture ганяв лише агент; дублі CI на проміжних SHA забивали чергу раннерів.

**How to apply:** перед кожним пушем lane — мій власний прогін Architecture на HEAD; не пушити проміжні SHA «щоб CI вже йшов». Пов'язане: [[rule-401-mutate-every-branch-before-ready-1116]], [[full-ci-on-final-tip-1110]].
