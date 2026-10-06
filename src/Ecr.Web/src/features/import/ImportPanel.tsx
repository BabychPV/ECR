import { useEffect, useRef, useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, Modal, Stack, Table, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type {
  ImportApplyRequest,
  ImportChange,
  ImportPreview,
  ImportRejection,
  JobAcceptedResponse,
  PatchCellsResponse,
} from '@/api/types';
import { denyText } from '@/features/grid/permissions';
import { useOpenerFocusReturn } from '@/features/projects/useOpenerFocusReturn';
import { invalidateSlices } from '@/features/grid/sliceCache';
import { calculationResults } from '@/features/methodologies/api';
import { RecalculateHintId, calculationResultsKey } from '@/features/methodologies/calculationResultsKey';
import { notificationCloseButtonProps, showApiError, showDone } from '@/shared/ui/notify';
import { useDurationIndicator } from '@/shared/ui/useDurationIndicator';
import { DurationProgress } from '@/shared/ui/DurationProgress';
import { t } from '@/shared/i18n';
import { useSettledAction } from '@/features/grid/settleEdits';
import { localized } from '@/shared/i18n/localized';

/** Куди імпортувати. */
export interface ImportPanelProps {
  /** Документ. */
  documentId: number;
  /** Період — після застосування перечитуються саме його зрізи. */
  periodKey: number;
}

/**
 * Імпорт із **обов'язковим переглядом diff** (`ФВ-4.3`, модуль 6.10).
 *
 * ⛔ Перегляд — не крок майстра, який можна пропустити, а сам механізм
 * захисту. Аркуш Excel, застосований без перегляду, непомітно перезаписує
 * чужу роботу: оператор бачить у себе свої числа, а в базі лежать чужі, і
 * дізнається про це через місяць зі звірки. Тому тут два виклики, а не один,
 * і другий приймає **токен першого**: застосувати можна лише те, що показали.
 *
 * ⚠ Обидві дії не мали в інтерфейсі жодного споживача до аудиту (`A7-39`) —
 * тобто весь модуль імпорту існував на сервері й був недосяжний.
 */
export function ImportPanel({ documentId, periodKey }: ImportPanelProps): JSX.Element {
  const queryClient = useQueryClient();
  const picker = useRef<HTMLInputElement>(null);
  const [preview, setPreview] = useState<ImportPreview | null>(null);

  // ⛔ T3-05: після Esc у діалозі перегляду фокус падав на `BODY`. Діалог відкривається після ВИБОРУ
  // файлу системним вікном, коли Mantine запам'ятовує вже не кнопку, а `body`. Відкривач фіксується
  // у мить кліку по кнопці й отримує фокус назад, щойно перегляд закрито (будь-яким шляхом).
  const { remember, restore } = useOpenerFocusReturn();
  const wasOpen = useRef(false);
  useEffect(() => {
    if (preview !== null) {
      wasOpen.current = true;
    } else if (wasOpen.current) {
      wasOpen.current = false;
      restore();
    }
  }, [preview, restore]);

  const load = useMutation({
    mutationFn: (file: File) => {
      const form = new FormData();

      // ⚠ Ім'я поля — `file`: саме так називається параметр `IFormFile file`
      // у контролері. Будь-яке інше дає 400 з порожнім тілом, і причина
      // виглядає як «файл не підійшов».
      form.append('file', file);

      return apiFetch<ImportPreview>(`/api/v1/documents/${documentId}/import/preview`, {
        method: 'POST',
        body: form,
      });
    },
    onSuccess: setPreview,
    onError: showApiError,
  });

  const apply = useMutation({
    mutationFn: (previewToken: string) =>
      apiFetch<PatchCellsResponse | JobAcceptedResponse>(`/api/v1/documents/${documentId}/import/apply`, {
        method: 'POST',
        body: JSON.stringify({ previewToken } satisfies ImportApplyRequest),
      }),
    onSuccess: async (result) => {
      // ⛔ F-01: понад поріг (`LargeImportThreshold`, 2000 комірок) сервер
      // відповідає `202` з `jobId` — імпорт ЩЕ НЕ застосовано. «Imported»
      // тут було б неправдою: зрізи перечитались би до запису, а людина
      // вирішила б, що зміни вже в документі. Результат — у «My tasks».
      if (isQueued(result)) {
        setPreview(null);
        await queryClient.invalidateQueries({ queryKey: ['jobs'] });
        showDone(t('import.queued'));
        return;
      }

      // Зрізи таблиць перечитуються цілком: імпорт зачіпає рядки, яких немає
      // на екрані, і часткове оновлення показало б половину змін.
      //
      // ⛔ `CL-02`: але «цілком» — це таблиці ЦЬОГО документа в ЦЬОМУ періоді,
      // а не збіг за префіксом `['table-slice']`, під який підпадав кожен
      // змонтований зріз застосунку. Решта зрізів позначаються застарілими
      // без жодного запиту — вони перечитаються, коли їх покажуть.
      await invalidateSlices(queryClient, { documentId, periodKey });
      await queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey] });

      setPreview(null);
      showDone(t('import.applied'));

      // ⚠ P3: перерахунок після Apply — лише формул (`recalculationJobId`).
      // Числа методологій сам імпорт не перераховує: змінений вхід лишає їх
      // застарілими, і людина бачила «Imported» без жодного слова, що звіт
      // ще рахує старі входи. Підказка — за правдою сервера (`isStale`), а не
      // за здогадом клієнта про те, які колонки читає методологія.
      // Один тост на тип: id прибирає дубль при повторних імпортах; знімає його і панель результатів.
      notifications.hide(RecalculateHintId);
      if (await calculationResultsStale(queryClient, documentId, periodKey)) {
        notifications.show({
          id: RecalculateHintId,
          color: 'statusWarning',
          title: t('import.recalculateTitle'),
          message: t('import.recalculateHint', { action: t('workflow.recalculate') }),
          autoClose: false,
          closeButtonProps: notificationCloseButtonProps,
        });
      }
    },
    // ⚠ Конфлікт версій рядків (`ECR-CELL-0409`) означає, що між переглядом і
    // застосуванням хтось змінив ті самі комірки. Батч відхиляється цілком —
    // часткове застосування заборонене (`B04` §2.3).
    onError: showApiError,
  });

  /*
   * ⚠ `ФВ-14.26`: розбір книги й застосування тривають від десятків мілісекунд
   * до кількох секунд — залежно від розміру файлу. Доти кнопка вмикала спінер
   * з першої мілісекунди (блимання на швидкому файлі) і нічого не казала на
   * повільному. Тепер: до 100 мс — нічого, далі — стан кнопки, від 1 с —
   * текст. Понад поріг `LargeImportThreshold` сервер сам іде у фон (`202`).
   */
  const loadPhase = useDurationIndicator(load.isPending);
  const applyPhase = useDurationIndicator(apply.isPending);
  // AN-28 P2-2: Apply з тим самим previewToken - лише один, і на час збереження набраного теж.
  const settled = useSettledAction(apply.isPending);

  const blocked = (preview?.conflicts.length ?? 0) > 0 || (preview?.rejected.length ?? 0) > 0;
  const rounded = preview?.changes.filter(isRounded) ?? [];

  return (
    <>
      {/* ⚠ Прихований `input[type=file]` за кнопкою: рідний елемент не
          піддається оформленню, але саме він дає діалог вибору файлу і
          працює з клавіатури. Кнопка лише натискає його. */}
      <input
        ref={picker}
        type="file"
        accept=".xlsx"
        hidden
        aria-hidden="true"
        tabIndex={-1}
        onChange={(event) => {
          const file = event.currentTarget.files?.[0];
          if (file !== undefined) load.mutate(file);

          // Скидання дозволяє обрати ТОЙ САМИЙ файл удруге: без нього
          // повторний вибір не викликає `change`, і кнопка «не працює».
          event.currentTarget.value = '';
        }}
      />

      <Button
        variant="default"
        loading={loadPhase !== 'none'}
        onClick={() => {
          // ⛔ Перші 100 мс кнопка не в стані `loading` (`ФВ-14.26`), тож
          // повторне натискання відсікає обробник, а не вигляд кнопки.
          remember();
          if (!load.isPending) picker.current?.click();
        }}
      >
        {t('import.pick')}
      </Button>

      <DurationProgress phase={loadPhase} label={t('common.loading')} />

      <Modal
        opened={preview !== null}
        onClose={() => setPreview(null)}
        title={t('import.title')}
        size="xl"
      >
        {preview !== null && (
          <Stack gap="sm">
            <Group gap="xs">
              <Badge variant="light">{t('import.changes', { count: preview.changes.length })}</Badge>
              {preview.conflicts.length > 0 && (
                <Badge color="statusWarning">
                  {t('import.conflicts', { count: preview.conflicts.length })}
                </Badge>
              )}
              {preview.rejected.length > 0 && (
                <Badge color="statusError">
                  {t('import.rejected', { count: preview.rejected.length })}
                </Badge>
              )}
              {rounded.length > 0 && (
                <Badge color="statusWarning" variant="light">
                  {t('import.rounded', { count: rounded.length })}
                </Badge>
              )}
            </Group>

            {blocked && (
              // ⛔ Застосування заблоковане цілком, а не «застосуємо решту».
              // Часткове застосування заборонене на рівні API, і імітувати
              // його тут означало б показати успіх там, де сервер відмовить.
              <Alert color="statusWarning" title={t('import.blockedTitle')}>
                {t('import.blockedHint')}
              </Alert>
            )}

            {preview.changes.length === 0 && !blocked && (
              <Text size="sm">{t('import.noChanges')}</Text>
            )}

            {rounded.length > 0 && (
              // ⚠ ФВ-9.16b: імпорт округлює число до `Scale` колонки (від нуля) —
              // записано буде не те, що стоїть у книзі. Людина бачить це ДО
              // застосування, окремим переліком: серед тисяч змін позначка в
              // рядку легко губиться, а саме ці комірки варто звірити з файлом.
              <Alert color="statusWarning" title={t('import.roundedTitle')}>
                <Stack gap="xs">
                  <Text size="sm">{t('import.roundedHint')}</Text>
                  <Table withTableBorder aria-label={t('import.roundedTitle')}>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>{t('import.table')}</Table.Th>
                        <Table.Th>{t('import.row')}</Table.Th>
                        <Table.Th>{t('import.column')}</Table.Th>
                        <Table.Th>{t('import.inFile')}</Table.Th>
                        <Table.Th>{t('import.becomes')}</Table.Th>
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {rounded.map((change) => (
                        <Table.Tr key={keyOf(change)}>
                          <Table.Td>{tableOf(change)}</Table.Td>
                          <Table.Td>{change.rowKey}</Table.Td>
                          <Table.Td>{change.columnCode}</Table.Td>
                          <Table.Td>{show(change.roundedFrom)}</Table.Td>
                          <Table.Td>{show(change.newValue)}</Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                </Stack>
              </Alert>
            )}

            {preview.changes.length > 0 && (
              <Table striped withTableBorder className="ecr-sticky-head">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('import.table')}</Table.Th>
                    <Table.Th>{t('import.row')}</Table.Th>
                    <Table.Th>{t('import.column')}</Table.Th>
                    <Table.Th>{t('import.was')}</Table.Th>
                    <Table.Th>{t('import.becomes')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {preview.changes.map((change) => (
                    <Table.Tr key={keyOf(change)}>
                      <Table.Td>{tableOf(change)}</Table.Td>
                      <Table.Td>{change.rowKey}</Table.Td>
                      <Table.Td>{change.columnCode}</Table.Td>
                      <Table.Td>{show(change.oldValue)}</Table.Td>
                      <Table.Td>
                        {show(change.newValue)}
                        {isRounded(change) && (
                          // ⚠ Позначка несе число з файлу текстом, а не лише
                          // підказкою: підказку не прочитає ні читалка, ні
                          // людина, що дивиться знімок екрана.
                          <Badge ml="xs" size="xs" color="statusWarning" variant="outline">
                            {t('import.roundedMark', { value: show(change.roundedFrom) })}
                          </Badge>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}

            {preview.rejected.length > 0 && (
              <Table striped withTableBorder>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('import.table')}</Table.Th>
                    <Table.Th>{t('import.row')}</Table.Th>
                    <Table.Th>{t('import.column')}</Table.Th>
                    <Table.Th>{t('import.excelCell')}</Table.Th>
                    <Table.Th>{t('import.reason')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {preview.rejected.map((rejection) => (
                    <Table.Tr
                      key={`${rejection.tableCode ?? ''}:${rejection.excelCell ?? rejection.rowKey}:${rejection.columnCode}`}
                    >
                      <Table.Td>{tableOf(rejection)}</Table.Td>
                      <Table.Td>{rejection.rowKey}</Table.Td>
                      <Table.Td>{rejection.columnCode}</Table.Td>
                      {/* ⚠ P3: адреса комірки книги — окремою колонкою в кожній
                          відмові: ключ рядка `R17` в Excel не знайти, `F23` — одним
                          переходом. Поза рядками таблиці (`V-10`) вона єдиний
                          орієнтир; прочерк — відмова цілої таблиці. */}
                      <Table.Td>{show(rejection.excelCell)}</Table.Td>
                      {/* ⛔ T3-06: технічний код причини (`ECR-CELL-0422`) НЕ в людському реченні, але й не
                          втрачений для підтримки: окремий дрібний рядок під текстом (виділяється й
                          копіюється). Сирий messageKey не показується. */}
                      <Table.Td>
                        {rejectionText(rejection)}
                        <Text size="xs" c="dimmed" data-testid="import-reason-code">
                          {rejection.reasonCode}
                        </Text>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}

            <Group justify="flex-end">
              <DurationProgress phase={applyPhase} label={t('grid.saving')} />
              <Button variant="default" onClick={() => setPreview(null)}>
                {t('common.cancel')}
              </Button>
              <Button
                disabled={blocked || preview.changes.length === 0}
                loading={applyPhase !== 'none' || settled.settling}
                onClick={() => settled.run(() => apply.mutateAsync(preview.previewToken))}
              >
                {t('import.apply')}
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </>
  );
}

/** Ключ рядка зміни: таблиця + рядок + колонка (`R1`/`C1` однакові в різних таблицях). */
function keyOf(change: ImportChange): string {
  return `${change.tableCode ?? ''}:${change.rowKey}:${change.columnCode}`;
}

/**
 * Чи імпорт округлив число з книги до `Scale` колонки (ФВ-9.16b).
 *
 * ⚠ `null`/відсутнє поле — не округлено: так приходить і план, збережений до
 * появи поля.
 */
function isRounded(change: ImportChange): boolean {
  return change.roundedFrom !== null && change.roundedFrom !== undefined;
}

/**
 * Чи застаріли числа методологій документа після застосування (P3).
 *
 * ⚠ Відмова читання (немає права, мережа) — не застаріло: підказка тут
 * необов'язкова, а помилка про неї після успішного імпорту читалася б як
 * провал самого імпорту.
 */
async function calculationResultsStale(
  queryClient: QueryClient,
  documentId: number,
  periodKey: number,
): Promise<boolean> {
  try {
    const results = await queryClient.fetchQuery({
      queryKey: calculationResultsKey(documentId, periodKey),
      queryFn: () => calculationResults(documentId, periodKey),
    });

    return results.some((result) => result.isStale);
  } catch {
    return false;
  }
}

/** Чи відповідь застосування — «поставлено в чергу», а не готовий результат. */
function isQueued(result: PatchCellsResponse | JobAcceptedResponse): result is JobAcceptedResponse {
  return 'jobId' in result && typeof result.jobId === 'string' && result.jobId !== '';
}

/**
 * Значення комірки в таблиці diff.
 *
 * ⚠ Порожнеча показується прочерком, а не порожнім місцем: «було порожньо —
 * стане 12» і «було 12 — стане порожньо» мають виглядати як зміни, а не як
 * половина рядка.
 */
function show(value: unknown): string {
  if (value === null || value === undefined || value === '') return '—';

  return trimFractionZeros(String(value));
}

/**
 * Прибирає хвостові нулі дробової частини: колонка decimal(25,16) віддає
 * 1.5000000000000000, і в переліку імпорту це шістнадцять знаків шуму замість
 * числа, яке людина звіряє з книгою (P3, walk3 2026-10-01). Лише відображення:
 * значення в запиті не змінюється; ціле й нечислове лишається як є.
 */
export function trimFractionZeros(text: string): string {
  return /^-?\d+\.\d+$/.test(text) ? text.replace(/\.?0+$/, '') : text;
}
/**
 * Таблиця рядка переліку — назвою мовою інтерфейсу, а коли назви немає, кодом.
 *
 * ⛔ `V-10`: у 91 таблиці шаблону ключі рядків і колонок однакові
 * (`R1`/`C1`), тож «R1 · C1 · 5 → 6» без таблиці не каже, ДЕ зміниться число.
 * Прочерк — лише для плану, побудованого до цієї правки (поля ще не було).
 */
function tableOf(item: ImportChange | ImportRejection): string {
  const name = localized(item.tableNameL10n);

  return name !== '' ? name : (item.tableCode ?? '—');
}

/**
 * Причина відмови мовою інтерфейсу — за `messageKey` відмови.
 *
 * ⛔ `V-10`: доти тут стояв `rejection.message` — готове українське речення
 * сервера («Правило доступу: лише читання.») незалежно від мови інтерфейсу.
 * `message` лишається діагностикою для журналу й тут більше не показується.
 *
 * ⚠ Літерали, а не `t(rejection.messageKey)`: сторож
 * `EndpointCoverageTests.Кожен_рядок_якого_просить_клієнт_є_в_каталозі`
 * перевіряє лише ключі-літерали. Відмова правами (`deny.<причина>`) бере
 * ТОЙ САМИЙ текст, що підказка сірої комірки сітки (`denyText`).
 */
function rejectionText(rejection: ImportRejection): string {
  const key = rejection.messageKey ?? '';

  switch (key) {
    case 'err.ECR-CELL-4221.importCalculated':
      return t('err.ECR-CELL-4221.importCalculated');
    // ⚠ P3: комірку не змінювали — її перерахувала система після експорту.
    case 'err.ECR-CELL-4221.importCalculatedStale':
      return t('err.ECR-CELL-4221.importCalculatedStale');
    case 'err.ECR-ROW-0404.importNoRow':
      return t('err.ECR-ROW-0404.importNoRow');
    case 'err.ECR-ROW-0404.importOutsideRows':
      return t('err.ECR-ROW-0404.importOutsideRows');
    case 'err.ECR-CELL-0422.importIntegerDigits':
      return t('err.ECR-CELL-0422.importIntegerDigits');
    // ⛔ ФВ-9.16b: після округлення до `Scale` число не вміщується в `Precision`.
    case 'err.ECR-CELL-0422.importPrecision':
      return t('err.ECR-CELL-0422.importPrecision');
    // RC5: текст довший за стовпець сховища — у перегляді, а не 422 на Apply.
    case 'err.ECR-CELL-0422.importValueTooLong':
      return t('err.ECR-CELL-0422.importValueTooLong');
    case 'err.ECR-IMP-0422.importInstanceMissing':
      return t('err.ECR-IMP-0422.importInstanceMissing');
    case 'err.ECR-IMP-0422.importTableMissing':
      return t('err.ECR-IMP-0422.importTableMissing');
    // ⛔ F-06: відмова типу — у перегляді, а не 422 на Apply.
    case 'err.ECR-CELL-0422.importExpectsNumber':
      return t('err.ECR-CELL-0422.importExpectsNumber');
    case 'err.ECR-CELL-0422.importExpectsBoolean':
      return t('err.ECR-CELL-0422.importExpectsBoolean');
    case 'err.ECR-CELL-0422.importExpectsDate':
      return t('err.ECR-CELL-0422.importExpectsDate');
    case 'err.ECR-CELL-0422.importExpectsIdentifier':
      return t('err.ECR-CELL-0422.importExpectsIdentifier');
    default:
      return (key.startsWith('deny.') ? denyText(key.slice('deny.'.length)) : null) ?? t('import.rejectedCell');
  }
}
