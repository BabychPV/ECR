#!/usr/bin/env bash
# scope-guard — PR не має права торкатися файлів поза `scope` своєї директиви.
#
# Це головний сторож процесу. Без нього `scope` у директиві — просто побажання,
# а виконавець розширює межі мовчки: жодного симптому, доки на рев'ю хтось не
# прочитає весь диф руками.
#
# Логіка:
#   1. З опису PR дістати `Closes #N` — посилання на директиву.
#   2. З тіла issue #N дістати список `scope:`.
#   3. Порівняти зі списком змінених файлів PR.
#   4. Будь-який файл поза `scope` — відмова з переліком усіх порушень.
#
# Свідомо НЕ реалізовано: автоматичне розширення scope. Якщо файл справді
# потрібен — це рішення керуючого вузла, а не сторожа.

set -uo pipefail

fail() { printf '\n\033[31m✗ scope-guard: %s\033[0m\n' "$1"; exit 1; }
info() { printf '  %s\n' "$1"; }

# ── 1. Знайти директиву ───────────────────────────────────────────────────────

PR_BODY="$(gh pr view "$PR_NUMBER" --json body -q .body 2>/dev/null || true)"

if [ -z "$PR_BODY" ]; then
  fail "не прочитався опис PR #${PR_NUMBER}. Без опису знайти директиву нема як."
fi

# `Closes|Fixes|Resolves #N` у будь-якому регістрі.
ISSUE="$(printf '%s' "$PR_BODY" \
  | grep -oiE '(closes|fixes|resolves)[[:space:]]+#[0-9]+' \
  | grep -oE '[0-9]+' \
  | head -n1 || true)"

if [ -z "$ISSUE" ]; then
  fail "PR не зв'язаний з директивою.
  В описі PR немає рядка \`Closes #N\`.
  Виправити: gh pr edit ${PR_NUMBER} --body \"Closes #<номер директиви>\"
  Причина правила: без зв'язку сторож не знає, який scope перевіряти, —
  а PR без директиви означає роботу, якої ніхто не замовляв."
fi

info "директива: #${ISSUE}"

ISSUE_BODY="$(gh issue view "$ISSUE" --json body -q .body 2>/dev/null || true)"
if [ -z "$ISSUE_BODY" ]; then
  fail "issue #${ISSUE} не прочитався. Директива видалена або номер хибний."
fi

# ── 2. Дістати scope ──────────────────────────────────────────────────────────

# Беремо рядки після `scope:` і до першого рядка, який не є елементом списку
# і не порожній. Формат елемента — `  - шлях`.
SCOPE="$(printf '%s\n' "$ISSUE_BODY" | awk '
  /^[[:space:]]*scope:[[:space:]]*$/ { inscope = 1; next }
  inscope {
    if ($0 ~ /^[[:space:]]*-[[:space:]]+[^[:space:]]/) {
      line = $0
      sub(/^[[:space:]]*-[[:space:]]+/, "", line)
      sub(/[[:space:]]+$/, "", line)
      gsub(/^["\x27]|["\x27]$/, "", line)
      print line
      next
    }
    if ($0 ~ /^[[:space:]]*$/) { next }
    inscope = 0
  }
')"

if [ -z "$SCOPE" ]; then
  fail "директива #${ISSUE} невалідна: у тілі немає списку \`scope:\`.
  Очікуваний формат — у YAML-блоці:
      scope:
        - src/Ecr.Core/Foo.cs
        - tests/Ecr.Core.Tests/FooTests.cs
  Виконавець має право не брати таку директиву в роботу
  (див. docs/sync/30-GH-CONTROL-PLANE.md)."
fi

info "scope директиви:"
printf '%s\n' "$SCOPE" | sed 's/^/    /'

# ── 3. Змінені файли ──────────────────────────────────────────────────────────

CHANGED="$(git diff --name-only "${BASE_SHA}...${HEAD_SHA}" 2>/dev/null || true)"
if [ -z "$CHANGED" ]; then
  info "змінених файлів немає — нічого перевіряти"
  exit 0
fi

info "змінені файли:"
printf '%s\n' "$CHANGED" | sed 's/^/    /'

# ── 4. Порівняння ─────────────────────────────────────────────────────────────

# `docs/sync/DELTA.md` дозволений ЗАВЖДИ і не мусить бути в scope: журнал
# відхилень ведеться на кожній директиві, і вимагати його переліку щоразу
# означало б, що директиву без нього не можна виконати чесно.
ALWAYS_ALLOWED='docs/sync/DELTA.md'

VIOLATIONS=''
while IFS= read -r file; do
  [ -z "$file" ] && continue
  [ "$file" = "$ALWAYS_ALLOWED" ] && continue

  matched=0
  while IFS= read -r entry; do
    [ -z "$entry" ] && continue
    # Точний збіг.
    if [ "$file" = "$entry" ]; then matched=1; break; fi
    # Каталог: `path/`, `path/**`, `path/*`.
    prefix="${entry%%\**}"
    prefix="${prefix%/}"
    case "$entry" in
      */\*\*|*/\*|*/)
        case "$file" in "$prefix"/*) matched=1; break ;; esac
        ;;
    esac
  done <<EOF
$SCOPE
EOF

  if [ "$matched" -eq 0 ]; then
    VIOLATIONS="${VIOLATIONS}${file}
"
  fi
done <<EOF
$CHANGED
EOF

if [ -n "$VIOLATIONS" ]; then
  printf '\n\033[31m✗ scope-guard: файли поза scope директиви #%s\033[0m\n\n' "$ISSUE"
  printf '%s' "$VIOLATIONS" | sed 's/^/    /'
  cat <<'MSG'

  Що це означає: PR змінює те, чого директива не замовляла.
  Що робити:
    - зміна не потрібна  -> прибрати її з PR
    - зміна потрібна     -> коментар у issue з обґрунтуванням і мітка
                            `needs:pk1`. Розширювати scope самому — заборонено.
  Причина правила: розширення межі мовчки — це робота без замовлення, і
  виявляється вона в найдорожчий момент, на звірці результатів.
MSG
  exit 1
fi

printf '\n\033[32m✓ scope-guard: усі %s змінених файлів у межах scope\033[0m\n' \
  "$(printf '%s\n' "$CHANGED" | grep -c .)"
