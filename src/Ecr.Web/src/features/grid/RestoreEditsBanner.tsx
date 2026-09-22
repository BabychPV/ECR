import { useEffect, useState, type JSX } from 'react';
import { Stack, Text } from '@mantine/core';
import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { TableSliceDto } from '@/api/types';
import { Banner } from '@/shared/ui/Banner';
import { t } from '@/shared/i18n';
import {
  clearLostEdits,
  peekLostEdits,
  restorableCount,
  type LostEdits,
  type RestoreSlice,
} from './lostEdits';
import {
  applyRestorePlan,
  planRestore,
  type RestoreConflict,
  type SliceVersions,
} from './restoreEdits';

/**
 * Пропозиція повернути правки, які загинули разом із сесією (`ФВ-3.6`,
 * `D14-12` крок 3; макет `docs/design/hybrid/screen-document.js:342`).
 *
 * ⛔ Чому це стоїть на ДОКУМЕНТІ, а не на сторінці входу. Правку не можна
 * покласти в сховище незбереженого, доки документа немає на екрані: сховище
 * прив'язане до одного документа (`pendingStore.openDocument`), і запис до
 * нього з чужої сторінки або нікуди не подівся б, або поїхав би не в той
 * документ. Сторінка входу лише КАЖЕ, що є що відновлювати, і веде туди, де
 * це можливо.
 *
 * ⚠ Скільки ключів заведено в каталозі — жодного: рядки `document.restoreEdits.*`
 * заводить інтегратор у `09-seed.sql`, доти видно позначені ключі (`⟦…⟧`). Це
 * усвідомлений борг, названий в описі PR, а не недогляд.
 */
export interface RestoreEditsBannerProps {
  readonly documentId: number;

  /**
   * Власник сесії; `undefined` — профіль ще не доїхав.
   *
   * ⛔ Без нього банер не малюється взагалі. Слід лежить під ключем із
   * `userId`, і «показати, доки не знаємо чий» тут означало б показати перше,
   * що трапилося, — тобто, на спільному комп'ютері, чужі числа звітності.
   */
  readonly userId?: number | undefined;
}

/** Чим скінчилося відновлення. */
interface RestoreOutcome {
  readonly applied: number;
  readonly conflicts: readonly RestoreConflict[];
}

/** Скільки конфліктів перелічити поіменно, перш ніж згорнути в число. */
const ShownConflicts = 20;

/**
 * Свіжі версії рядків зрізу; `null` — прочитати не вдалося.
 *
 * ⛔ `fetchQuery` зі `staleTime: 0`, а не `getQueryData`. Зріз у кеші живе
 * п'ять хвилин (`SliceStaleTime`), і саме на ньому перевірка версій стала б
 * фікцією: ми звіряли б `baseVersion` правки з тим, що лежало в кеші ДО
 * обриву сесії, тобто з самим собою. Ціна — один `GET` на зріз, і платиться
 * він рівно тоді, коли людина натиснула «Відновити».
 *
 * ⚠ Адреса — та сама, що в `DocumentGrid` (`tableInstanceId` уже визначає
 * період, `?periodKey=` сервер не читає), і ключ кеша той самий: прочитане тут
 * не пропаде даремно, а дістанеться сітці, коли вона змонтується.
 */
async function loadVersions(
  queryClient: QueryClient,
  documentId: number,
  slice: RestoreSlice,
): Promise<SliceVersions> {
  try {
    const data = await queryClient.fetchQuery({
      queryKey: queryKeys.slices.one(slice.tableInstanceId, slice.periodKey),
      queryFn: () =>
        apiFetch<TableSliceDto>(
          `/api/v1/documents/${String(documentId)}/tables/${String(slice.tableInstanceId)}`,
        ),
      staleTime: 0,
    });

    return new Map(data.rows.map((row) => [row.rowKey, row.rowVersion]));
  } catch {
    // ⛔ Саме `null`, а не порожня мапа: «не знаємо» і «рядків немає» —
    // різні твердження, і друге дозволило б застосувати правки створення
    // рядків, яких ми не бачили.
    return null;
  }
}

export function RestoreEditsBanner({ documentId, userId }: RestoreEditsBannerProps): JSX.Element {
  const queryClient = useQueryClient();
  const [lost, setLost] = useState<LostEdits | null>(null);
  const [outcome, setOutcome] = useState<RestoreOutcome | null>(null);
  const [busy, setBusy] = useState(false);

  /*
   * ⚠ Слід читається ЩОРАЗУ, коли стає відомим власник або змінюється
   * документ, і лише СВІЙ: чужий не дістати навіть теоретично — ключ несе
   * `userId`. Слід іншого документа не показується: повертати правки
   * документа #7 на екрані документа #9 нікуди.
   */
  useEffect(() => {
    if (userId === undefined) {
      setLost(null);
      return;
    }

    const found = peekLostEdits(userId);
    setLost(found !== null && found.documentId === documentId ? found : null);
  }, [documentId, userId]);

  async function apply(current: LostEdits, owner: number): Promise<void> {
    setBusy(true);

    try {
      const versions = new Map<string, SliceVersions>();
      for (const slice of current.slices) {
        versions.set(
          `${String(slice.tableInstanceId)}:${String(slice.periodKey)}`,
          await loadVersions(queryClient, documentId, slice),
        );
      }

      const plan = planRestore(
        current.slices,
        (tableInstanceId, periodKey) =>
          versions.get(`${String(tableInstanceId)}:${String(periodKey)}`) ?? null,
      );

      const applied = applyRestorePlan(plan);

      // ⛔ Слід стирається ПІСЛЯ застосування і в будь-якому разі: правки вже
      // або в сховищі незбереженого, або названі конфліктом на екрані.
      // Лишити його означало б запропонувати те саме вдруге — і другий раз
      // уже поверх власних, щойно відновлених значень.
      clearLostEdits(owner);
      setLost(null);
      setOutcome({ applied, conflicts: plan.conflicts });
    } finally {
      setBusy(false);
    }
  }

  if (lost === null) return outcomeBanner(outcome, () => setOutcome(null));

  const stored = restorableCount(lost);

  return (
    <Banner
      tone="warning"
      testId="restore-edits"
      title={t('document.restoreEdits.title')}
      text={
        <Stack gap="xs">
          <Text size="sm">{t('document.restoreEdits.text', { count: stored })}</Text>

          {/* ⚠ Різниця між «було незбережено» і «вміщено у слід» називається
              вголос. Мовчазне «відновити 500» там, де правок було 640, — та
              сама тиха втрата, від якої весь цей механізм. */}
          {lost.count > stored && (
            <Text size="sm">
              {t('document.restoreEdits.partial', { count: stored, total: lost.count })}
            </Text>
          )}
        </Stack>
      }
      actions={[
        {
          label: t('document.restoreEdits.discard'),
          onClick: () => {
            if (userId !== undefined) clearLostEdits(userId);
            setLost(null);
          },
        },
        {
          // ⚠ Підпис НЕ змінюється на час читання зрізів: окремий рядок
          // каталогу заради секунди очікування — ще один ключ, який хтось має
          // завести й перекласти трьома мовами. Повторне натискання відсікає
          // `busy` нижче.
          label: t('document.restoreEdits.apply'),
          variant: 'filled',
          onClick: () => {
            if (userId === undefined || busy) return;
            void apply(lost, userId);
          },
        },
      ]}
    />
  );
}

/**
 * Підсумок відновлення: скільки повернуто і що пропущено.
 *
 * ⛔ Конфлікти показуються ОКРЕМО й поіменно. «Відновлено 2 з 3» без переліку
 * не веде до жодної дії: людина не знає, яку саме комірку вводити заново, і
 * шукати її доведеться очима по всьому аркушу.
 */
function outcomeBanner(outcome: RestoreOutcome | null, dismiss: () => void): JSX.Element {
  if (outcome === null) return <></>;

  const conflicts = outcome.conflicts;

  return (
    <Banner
      tone={conflicts.length === 0 ? 'success' : 'warning'}
      testId="restore-edits-result"
      title={t('document.restoreEdits.applied', { count: outcome.applied })}
      dismiss={{ label: t('document.restoreEdits.close'), onDismiss: dismiss }}
      text={
        conflicts.length === 0 ? undefined : (
          <Stack gap="xs">
            <Text size="sm">
              {t('document.restoreEdits.conflicts', { count: conflicts.length })}
            </Text>

            {conflicts.slice(0, ShownConflicts).map((conflict) => (
              <Text key={conflictKey(conflict)} size="sm" data-testid="restore-edits-conflict">
                {t(
                  conflict.reason === 'version'
                    ? 'document.restoreEdits.conflictRow'
                    : 'document.restoreEdits.unavailableRow',
                  {
                    rowKey: conflict.edit.rowKey,
                    columnCode: conflict.edit.columnCode,
                    // ⚠ Версії — рядки оптимістичного блокування, не дати; людині
                    // вони нічого не кажуть, і тому в тексті їх немає. Те, що
                    // вона може зробити, — ввести значення заново, і саме воно
                    // названо в рядку каталогу.
                  },
                )}
              </Text>
            ))}

            {conflicts.length > ShownConflicts && (
              <Text size="sm">
                {t('document.restoreEdits.more', { count: conflicts.length - ShownConflicts })}
              </Text>
            )}
          </Stack>
        )
      }
    />
  );
}

function conflictKey(conflict: RestoreConflict): string {
  return [
    conflict.tableInstanceId,
    conflict.periodKey,
    conflict.edit.rowKey,
    conflict.edit.columnCode,
  ].join(':');
}
