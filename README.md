# ECR Web

Універсальний інструмент структурованої звітності. Замінює Excel/VBA-рішення
екологічної звітності (~45 000 рядків VBA) і розрахунковий бекенд на
SQL/CLR/PI AF/SSRS.

## З чого почати

**Уся документація — у `docs/`.**

* **`docs/build/00-START-HERE.md`** — якщо ви розробляєте систему
* `docs/tz/00-README.md` — консолідоване технічне завдання
* `docs/tz/10-decisions.md` — реєстр рішень (джерело істини)

## Збірка

```bash
dotnet restore Ecr.sln
dotnet build Ecr.sln
dotnet test Ecr.sln --filter "Category!=Integration"
```

Повний перелік команд — `docs/build/09-commands.md`.

## Ліцензійна дисципліна

Нова залежність додається **тільки** після перевірки ліцензії за першоджерелом
і запису в `docs/15-verified-stack.md`. Заборонені пакети перелічені в
`Directory.Packages.props` і в `docs/build/04-environment.md` §3.3; CI падає на
будь-якому з них.
