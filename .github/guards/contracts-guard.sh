#!/usr/bin/env bash
# contracts-guard — контракт не змінюється мимохідь.
#
# Контракт тут — це те, на що спирається інший бік: схема API, знімок OpenAPI,
# каталог помилок, таблиця ендпоінтів, ТЗ і контрольні суми пакета документації.
# Зміна будь-чого з цього має наслідки за межами PR, тому вимагає ОКРЕМОЇ
# директиви з міткою `type:contract` — тобто окремого рішення керуючого вузла,
# а не рядка, що приїхав разом із реалізацією.
#
# Свідомо НЕ заборонено повністю: контракти мусять мати змогу змінюватися.
# Заборонено лише робити це тихо.

set -uo pipefail

# Шляхи, зміна яких вважається зміною контракту.
# ⚠ `docs/tz/**` і `docs/CHECKSUMS.txt` — власність замовника: пакет ТЗ
# делегований, і його правка нашою рукою руйнує саму можливість звірки.
CONTRACT_PATHS='
contracts/
docs/build/02-contracts.md
docs/tz/
docs/CHECKSUMS.txt
'

CHANGED="$(git diff --name-only "${BASE_SHA}...${HEAD_SHA}" 2>/dev/null || true)"
if [ -z "$CHANGED" ]; then
  printf '\033[32m✓ contracts-guard: змін немає\033[0m\n'
  exit 0
fi

TOUCHED=''
while IFS= read -r file; do
  [ -z "$file" ] && continue
  while IFS= read -r guarded; do
    [ -z "$guarded" ] && continue
    case "$guarded" in
      */)  case "$file" in "$guarded"*) TOUCHED="${TOUCHED}${file}
" ;; esac ;;
      *)   [ "$file" = "$guarded" ] && TOUCHED="${TOUCHED}${file}
" ;;
    esac
  done <<EOF
$CONTRACT_PATHS
EOF
done <<EOF
$CHANGED
EOF

if [ -z "$TOUCHED" ]; then
  printf '\033[32m✓ contracts-guard: контрактних файлів не зачеплено\033[0m\n'
  exit 0
fi

printf '  зачеплено контрактні файли:\n'
printf '%s' "$TOUCHED" | sed 's/^/    /'

# Чи має директива право на це?
PR_BODY="$(gh pr view "$PR_NUMBER" --json body -q .body 2>/dev/null || true)"
ISSUE="$(printf '%s' "$PR_BODY" \
  | grep -oiE '(closes|fixes|resolves)[[:space:]]+#[0-9]+' \
  | grep -oE '[0-9]+' | head -n1 || true)"

if [ -z "$ISSUE" ]; then
  printf '\n\033[31m✗ contracts-guard: контракт змінено, а директиви немає\033[0m\n'
  printf '  Зв\x27яжіть PR із директивою (Closes #N) — і вона мусить мати мітку type:contract.\n'
  exit 1
fi

LABELS="$(gh issue view "$ISSUE" --json labels -q '.labels[].name' 2>/dev/null || true)"

if printf '%s\n' "$LABELS" | grep -qx 'type:contract'; then
  printf '\n\033[32m✓ contracts-guard: директива #%s має мітку type:contract — зміна дозволена\033[0m\n' "$ISSUE"
  exit 0
fi

cat <<MSG

$(printf '\033[31m✗ contracts-guard: зміна контракту без директиви type:contract\033[0m')

  Директива #${ISSUE} має мітки: $(printf '%s' "$LABELS" | tr '\n' ' ')

  Що це означає: PR міняє те, на що спирається інший бік системи, —
  і робить це в межах директиви, яка про контракт не домовлялася.

  Що робити:
    - зміна випадкова  -> відкотити ці файли з PR
    - зміна потрібна   -> окрема директива з міткою \`type:contract\`;
                          у ній має бути названо, ЩО саме в контракті
                          змінюється і хто на це спирається

  ⛔ Якщо серед файлів вище є \`docs/tz/**\` або \`docs/CHECKSUMS.txt\` —
  це пакет замовника. Його не править жоден вузол: розбіжність оформлюється
  як питання з міткою \`needs:human\`.
MSG
exit 1
