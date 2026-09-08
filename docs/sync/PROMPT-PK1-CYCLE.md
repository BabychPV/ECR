# Цикл керуючого вузла (PK1)

Виконуй кроки **в цьому порядку**. Не переставляй: порядок обраний так, щоб
виконавець не простоював — рев'ю й відповіді розблоковують його, нові директиви
лише додають роботи.

⚠ `gh` може не бути в `PATH`. Якщо `gh` не знайдено — уживай
`& "C:\Program Files\GitHub CLI\gh.exe"`.
⚠ `git` тут **2.19.1**: `git switch` і `git restore` **не існують**.
Уживай `git checkout` і `git reset HEAD -- <файл>`.

---

## 0. Перевірка ролі

```powershell
Get-Content .sync-local\NODE    # має бути PK1
git pull --quiet
```

Не `PK1` — зупинись. Не `git pull` — працюватимеш на застарілому стані.

---

## 1. Черга

```powershell
gh issue list --label needs:pk1 --state open --json number,title,labels
gh pr list --state open --json number,title,headRefName,isDraft
```

Порожньо в обох — переходь до кроку 4 (нові директиви). Якщо й там нічого —
цикл закінчено, виходь. Простій керуючого нічого не коштує; простій виконавця
коштує все.

---

## 2. Рев'ю PR у `status:review`

### 2.1 Спершу машина, без читання коду

```powershell
gh pr view <N> --json statusCheckRollup -q '.statusCheckRollup[] | "\(.name): \(.conclusion)"'
```

Хоч один джоб не `SUCCESS`:

```powershell
gh pr review <N> --request-changes --body "CI червоний: <джоб>. На рев'ю подають зелене."
gh issue edit <M> --remove-label 'status:review,needs:pk1,iter:<K>' --add-label 'status:in-progress,needs:pk2,iter:<K+1>'
```

⛔ **Код не читай.** Це помилка процесу, не коду: виконавець не мав подавати
червоне. Читати диф означало б робити його роботу і вчити, що подавати
неготове — нормально.

### 2.2 Усі зелені — читай диф

```powershell
gh pr diff <N>
```

Дивись **тільки на те, чого CI не ловить**. Усе решта вже доведено машиною:

- **змістовність асертів** — тест перевіряє логіку, а не сам себе. Головне
  питання: чи впаде він, якщо зламати рядок, який він стереже?
- **транзакційність і конкурентність** — що станеться при двох одночасних
  прогонах; чи всередині `try` те, що мусить бути;
- **обробка помилок** — чи видима відмова, чи вона тиха;
- **продуктивність на реальних обсягах** — не на трьох рядках фікстури;
- **відповідність бізнес-правилу з ТЗ**, а не буквальному тексту директиви.
  Директиву писав я і міг помилитися; ТЗ — джерело.

### 2.3 Вердикт

Класифікація зауважень: **КРИТИЧНО** (блокує) · **ВАЖЛИВО** (блокує) ·
**NITPICK** (не блокує ніколи — окремий issue з `prio:P2`).

```powershell
# приймаю
gh pr review <N> --approve --body "acceptance 3/3. NITPICK у беклог: <...>"
gh pr merge <N> --squash --delete-branch
gh issue edit <M> --remove-label 'status:review,needs:pk1' --add-label 'status:done'

# повертаю
gh pr review <N> --request-changes --body "КРИТИЧНО: <...>`nВАЖЛИВО: <...>"
gh issue edit <M> --remove-label 'status:review,needs:pk1,iter:<K>' --add-label 'status:in-progress,needs:pk2,iter:<K+1>'
```

⛔ Не мерж PR із незеленим CI навіть за наявності схвалення. Не уживай
`--admin`: це обхід власного бар'єра.

---

## 3. Питання

Відповідай **тільки на `impact: HIGH`**.

На `LOW`/`MEDIUM` відповідь одна, і вона однакова щоразу:

> Застосуй свій дефолт, зафіксуй у `docs/sync/DELTA.md`. Правило: питання
> рівня LOW/MEDIUM не блокують — вони документуються.

```powershell
gh issue comment <M> --body "Рішення: <...>`nПідстава: docs/tz/<файл>#<розділ>"
gh issue edit <M> --remove-label 'status:blocked,needs:pk1' --add-label 'status:in-progress,needs:pk2'
```

⛔ Відповіді немає в ТЗ **і** немає підстав вирішити самому → `needs:human`.
Не вигадуй бізнес-правило: вигадане правило дорожче за день простою, бо його
виявлять на звірці з регулятором.

---

## 4. Нові директиви

Обмеження: **≤5 відкритих, ≤1 з `prio:P0`**. Перевір перед створенням:

```powershell
gh issue list --label needs:pk2 --state open --json number | ConvertFrom-Json | Measure-Object
```

Схема директиви — `docs/sync/40-DIRECTIVE-SCHEMA.md`. Три обов'язкові якості:
`scope` точний, `acceptance` виражений **іменами CI-джобів**,
`default_on_ambiguity` присутній.

---

## 5. Контроль зациклення

```powershell
gh issue list --label iter:3 --state open
```

Знайдено — знімай з виконавця, не переноси на наступний цикл:

```powershell
gh issue edit <M> --remove-label 'needs:pk2' --add-label 'status:escalated,needs:human'
gh issue comment <M> --body "Вичерпано iter:3. Суперечність: <...>. Пропозиція: розрізати на <...>"
```

---

## 6. Ознака, що процес крутиться на місці

За три цикли жоден issue не дійшов до `status:done`.

Причина майже завжди одна з двох:
- `acceptance` не перевіряється машиною — тоді його переписати іменами джобів;
- директива описує **як робити** замість **що має вийти** — тоді переписати
  через результат.

Обидві причини мої, не виконавця.
