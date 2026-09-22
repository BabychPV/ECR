import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import { t } from '@/shared/i18n';

/**
 * Бейдж стану прогону збору — ЛОКАЛЬНИЙ, а не `shared/ui/StatusBadge`
 * (ФВ-5.23, явна вимога постановки: `Running` — info, `Succeeded` — success,
 * `Degraded` — warning, `Failed` — danger).
 *
 * ⛔ Не той самий словник, що `statusTable.collectionRun`
 * (`shared/ui/StatusBadge.tsx`). Той словник — ФУНДАМЕНТ (спільний ресурс:
 * `statusTable` уже споживає `SourcesPage.tsx` і сторож
 * `EndpointCoverageTests.DynamicKeySites` статично перелічує КОЖЕН його
 * ключ), і чіпати його поза списком дозволених файлів цієї задачі не можна.
 * До того ж він навмисно не має тону «успіх»: `KIT.md` §1.3 забороняє зелений
 * для «усе гаразд» на статичному стані. Постановка ФВ-5.23 хоче саме
 * чотириколірну шкалу — це свідоме рішення ЦЬОГО екрана, не заднім числом
 * застосоване до решти застосунку.
 *
 * ⚠ Токени кольору — ті самі `statusSuccess`/`statusWarning`/`statusError`
 * теми (`shared/theme/theme.ts`), що вже несуть контрастну перевірку
 * (`ConsistencyIssuesPage.tsx`, `SourcesPage.tsx`). Для `Running` контрастного
 * токена «інформація» в темі немає (лише три статусних кольори), тому
 * використано вбудований Mantine `blue` — лінтер забороняє голі `red`/
 * `orange` (W4.2), але не `blue`. Названо прогалину: токен `statusInfo` варто
 * завести окремим рішенням, коли з'явиться другий споживач тону «інформація».
 *
 * ⛔ Невідомий стан (розбіжність клієнта й сервера) — теж видимий, не тихий:
 * `data-state-known="false"` і підпис самим кодом, а не мовчазний нейтральний
 * колір (той самий аргумент, що й `UnknownStateTone` у `StatusBadge.tsx`).
 */
const Colors: Readonly<Record<string, string>> = {
  Running: 'blue',
  Succeeded: 'statusSuccess',
  Degraded: 'statusWarning',
  Failed: 'statusError',
};

/**
 * Підпис стану — ЛІТЕРАЛЬНИМИ викликами `t(...)`, а не ключем зі змінної.
 *
 * ⛔ `t(Tones[state].labelKey)` сторож `EndpointCoverageTests` бачив би як
 * ДИНАМІЧНИЙ виклик (перший аргумент — не літерал) і вимагав би запису в
 * `DynamicKeySites` — файлі поза дозволеним списком цієї задачі. `switch` із
 * літералом у кожній гілці лишається для сторожа звичайним `t('...')`.
 */
function stateLabel(state: string): string {
  switch (state) {
    case 'Running':
      return t('collectionRuns.stateRunning');
    case 'Succeeded':
      return t('collectionRuns.stateSucceeded');
    case 'Degraded':
      return t('collectionRuns.stateDegraded');
    case 'Failed':
      return t('collectionRuns.stateFailed');
    default:
      // ⛔ Невідомий стан — сам код, а не мовчазний переклад: розбіжність
      // клієнта й сервера мусить бути видимою (`data-state-known` нижче).
      return state;
  }
}

export function CollectionRunStateBadge({ state }: { readonly state: string }): JSX.Element {
  const known = Colors[state] !== undefined;

  return (
    <Badge
      size="sm"
      miw="fit-content"
      variant="light"
      color={Colors[state] ?? 'statusWarning'}
      data-state-known={String(known)}
    >
      {stateLabel(state)}
    </Badge>
  );
}
