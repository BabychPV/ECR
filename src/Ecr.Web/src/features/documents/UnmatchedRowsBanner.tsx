import { Fragment, useMemo, useState, type JSX } from 'react';
import { Alert, Anchor, Button, Collapse, Group, Stack, Text } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import { formatDateTime } from '@/shared/format/datetime';
import { language, t } from '@/shared/i18n';
import type { UnmatchedRows } from './useUnmatchedRows';
import { StepTraceModal, type StepTraceTarget } from './StepTraceModal';
import {
  groupRowCount,
  groupUnmatchedRows,
  unmatchedRowCount,
  type UnmatchedRowRef,
  type UnmatchedTableHint,
} from './unmatchedRowsModel';

/** Префікс пам'яті «банер згорнуто»; ключ — `префікс:userId` (правило людини fg6: стан — для кожного користувача). */
export const UnmatchedCollapsedKey = 'ecr.unmatchedRows.collapsed';

/** Ключ пам'яті згортання для користувача; без відомого користувача — спільний префікс. */
export function unmatchedCollapsedKey(userId: number | undefined): string {
  return userId === undefined ? UnmatchedCollapsedKey : `${UnmatchedCollapsedKey}:${String(userId)}`;
}

/** Скільки допустимих значень показується в підказці таблиці; решта — «+N» з переліком у тултіпі. */
export const AllowedValuesShown = 8;

/** Скільки груп-таблиць видно в розгорнутому банері (~160 px); решта — за «Show all N tables» (fg6 F13). */
export const GroupsShown = 3;

/** Скільки посилань на рядки показується в групі таблиці; решта — «і ще N». */
export const RowLinksPerTable = 20;

/**
 * ✎ fg6 F13 (людина: «не має займати все вікно і має мати можливість згортатися»): без збереженого вибору банер
 * ЗГОРНУТИЙ — лише рядок підсумку; розгорнутим він лишається тільки після явного «Show rows» (`0` у сховищі).
 */
function readCollapsed(key: string): boolean {
  try {
    return window.localStorage.getItem(key) !== '0';
  } catch {
    return true;
  }
}

function writeCollapsed(key: string, collapsed: boolean): void {
  try {
    window.localStorage.setItem(key, collapsed ? '1' : '0');
  } catch {
    // Сховище недоступне — вибір просто не запам'ятається.
  }
}

interface UnmatchedRowsBannerProps {
  /** Користувач сесії: згортання пам'ятається окремо для кожного (fg6). */
  readonly userId?: number | undefined;
  readonly rows: UnmatchedRows;
  /** Усі таблиці документа за період: назви таблиць і аркушів, екземпляр для переходу. */
  readonly tables: readonly DocumentTableDto[];
  /**
   * Відкрити рядок у сітці: аркуш, прокрутка, фокус і підсвітка (шлях зауважень, `ФВ-5.6`). ✎ 10.11: для відхиленого
   * рядка — одразу на колонці, яку треба заповнити (`columnCode`); `null` — перша колонка.
   */
  readonly onOpenRow: (tableInstanceId: number, rowKey: string, columnCode: string | null) => void;
  /** Вузький екран (документ лише для читання): дія підказки — відкрити документ ширше, а не F9 (UAT 08.11). */
  readonly narrow?: boolean;
  /** ✎ B78: вміст у «Технічних деталях» панелі проблем розрахунку — без власного банера й згортання. */
  readonly embedded?: boolean;
  /**
   * ✎ D-565: документ і період — щоб рядок StepError, для якого прогін записав кроки, відкривав крок, що впав
   * (`GET …/calculation-steps`). Без них кнопки немає (лише перехід до рядка, як доти).
   */
  readonly documentId?: number | undefined;
  readonly periodKey?: number | undefined;
}

/** ✎ D-565: період прогону рядка — для річної розкладки місяць рядка, інакше період сторінки. */
function rowPeriodKey(periodKey: number, monthNumber: number | null): number {
  return monthNumber === null ? periodKey : Math.floor(periodKey / 100) * 100 + monthNumber;
}

/**
 * «Частину рядків не взято в розрахунок» над сіткою (D14 + AN-69; UAT 08.11 b1–b4).
 *
 * ⛔ Це ІНФОРМАЦІЯ, а не помилка: тон `info` макета (`Alert color="brand" variant="light"`); підказка каже, чому рядки
 * не рахуються і що зробити, ЯКЩО рядок треба рахувати (свіжий погляд 08.11: без «нічого виправляти» поруч із дією). Рядки, відсіяні статусом «не Running» (D-425), як
 * «без правила» не потрапляють; ✎ B66 — лише ОДНИМ підсумком на таблицю (`RowInactive`, кількість без посилань), а помилки
 * кроків методики — групою `StepError` (рядок · крок · код).
 *
 * ⚠ Групи — таблиця з аркушем і кількістю («Utility · 9.01 Unit 400 — rows: 2»), кожен рядок — кнопка-посилання,
 * що відкриває його в сітці. Згортання пам'ятається (`localStorage` під `try/catch`), кількість видно і згорнутим.
 *
 * ⚠ Попередня згортна панель AN-69 (`CalculationIssuesPanel`, жовта, лише активний аркуш) показувала ТІ САМІ рядки
 * вдруге іншим тоном — її замінено цим банером; позначки в дереві таблиць лишились.
 */
export function UnmatchedRowsBanner({
  userId,
  rows,
  tables,
  onOpenRow,
  narrow = false,
  embedded = false,
  documentId,
  periodKey,
}: UnmatchedRowsBannerProps): JSX.Element | null {
  const groups = useMemo(
    () => groupUnmatchedRows(rows.issues, tables, rows.hints),
    [rows.issues, tables, rows.hints],
  );
  const storageKey = unmatchedCollapsedKey(userId);
  const [collapsed, setCollapsed] = useState(() => readCollapsed(storageKey));
  const [allGroups, setAllGroups] = useState(false);
  const [stepTarget, setStepTarget] = useState<StepTraceTarget | null>(null);

  if (groups.length === 0) return null;

  const count = unmatchedRowCount(groups);
  const toggle = (): void => {
    const next = !collapsed;
    setCollapsed(next);
    writeCollapsed(storageKey, next);
  };
  const bodyId = 'document-unmatched-rows-body';

  const body = (
    <>
      {groups.some((group) => !group.rejected && !group.stepError && !group.inactive) && (
        <Text size="sm" mt="xs" data-testid="document-unmatched-rows-hint">
          {t(narrow ? 'document.unmatchedRows.hintNarrow' : 'document.unmatchedRows.hint')}
        </Text>
      )}
      {groups.some((group) => group.rejected && !group.missingInputs) && (
        <Text size="sm" mt="xs" data-testid="document-rejected-rows-hint">
          {t('document.rejectedRows.hint')}
        </Text>
      )}
      {groups.some((group) => group.missingInputs) && (
        <Text size="sm" mt="xs" data-testid="document-missing-input-rows-hint">
          {t('document.missingInputRows.hint')}
        </Text>
      )}
      {groups.some((group) => group.stepError) && (
        <Text size="sm" mt="xs" data-testid="document-step-error-rows-hint">
          {t('document.stepErrorRows.hint')}
        </Text>
      )}
      {groups.some((group) => group.inactive) && (
        <Text size="sm" mt="xs" data-testid="document-inactive-rows-hint">
          {t('document.inactiveRows.hint')}
        </Text>
      )}
      <Stack
        role="list"
        gap="xs"
        mt="xs"
        aria-label={t('calculationIssues.listLabel')}
      >
        {(allGroups ? groups : groups.slice(0, GroupsShown)).map((group) => {
          const shown = group.rows.slice(0, RowLinksPerTable);
          const more = group.rows.length - shown.length;
          const table = group.sheetName.length > 0 ? `${group.sheetName} · ${group.tableName}` : group.tableName;

          return (
            <div role="listitem" key={group.key} data-testid="document-unmatched-rows-group">
              <Text size="sm" span fw={500}>
                {groupTitle(group.inactive, group.stepError, group.missingInputs, group.rejected, table, groupRowCount(group))}
              </Text>
              {group.hint === null && group.methodologyCode !== null && (
                <Text size="xs" span>
                  {` · ${group.methodologyCode}`}
                </Text>
              )}
              {group.hint !== null && <AllowedValues hint={group.hint} />}
              {/* ✎ D-565: кроків цього прогону немає — сказати чому й хто може ввімкнути трасування (з переходом). */}
              {group.stepError && group.traceOff.length > 0 && (
                <Text size="sm" data-testid="document-step-error-trace-off">
                  {t('document.stepErrorRows.traceOff', {
                    methodologies: group.traceOff.map((m) => m.code ?? String(m.methodologyId)).join(', '),
                  })}{' '}
                  {group.traceOff.map((m, index) => (
                    <Fragment key={m.methodologyId}>
                      {index > 0 && ' · '}
                      <Anchor
                        href={`/admin/methodologies/${String(m.methodologyId)}/versions`}
                        size="sm"
                        data-testid="document-step-error-trace-link"
                      >
                        {t('document.stepErrorRows.traceOffLink', { methodology: m.code ?? String(m.methodologyId) })}
                      </Anchor>
                    </Fragment>
                  ))}
                </Text>
              )}
              {/* B66: підсумок неактивних — лише кількість, без поштучних посилань (виключення за задумом). */}
              {!group.inactive && (
                <Group gap="xs" wrap="wrap">
                  {shown.map((row, index) => {
                    const label = rowLabel(row, group.rejected);
                    const tableInstanceId = group.tableInstanceId;
                    // ✎ Свіжий погляд 08.11: посилання розділені видимою « · », інакше «Row 2: «Coal» Row 3: «Oil»» читається однією фразою.
                    const separator =
                      index > 0 ? (
                        <Text span size="sm" aria-hidden="true">
                          ·
                        </Text>
                      ) : null;

                    return tableInstanceId === null ? (
                      <Fragment key={row.rowKey}>
                        {separator}
                        <Text size="sm" span>
                          {label}
                        </Text>
                      </Fragment>
                    ) : (
                      <Fragment key={row.rowKey}>
                      {separator}
                      <Anchor
                        component="button"
                        type="button"
                        size="sm"
                        data-testid={
                          group.stepError
                            ? 'document-step-error-row-link'
                            : group.rejected
                              ? 'document-rejected-row-link'
                              : 'document-unmatched-row-link'
                        }
                        data-row-key={row.rowKey}
                        aria-label={`${table} · ${label}`}
                        onClick={() =>
                          onOpenRow(tableInstanceId, row.rowKey, row.missingColumns.find((c) => c.column)?.code ?? null)
                        }
                      >
                        {label}
                      </Anchor>
                      {/* ✎ D-565: прогін записав кроки — відкрити крок, що впав, і його входи. */}
                      {row.stepError?.trace === 'Recorded' && documentId !== undefined && periodKey !== undefined && (
                        <Anchor
                          component="button"
                          type="button"
                          size="xs"
                          data-testid="document-step-error-show-step"
                          data-row-key={row.rowKey}
                          aria-label={`${table} · ${label} · ${t('document.stepErrorRows.showStep')}`}
                          onClick={() =>
                            setStepTarget({
                              documentId,
                              periodKey: rowPeriodKey(periodKey, row.stepError?.monthNumber ?? null),
                              tableInstanceId,
                              rowKey: row.rowKey,
                              label,
                              stepCode: row.stepError?.step ?? null,
                              methodologyId: row.stepError?.methodologyId ?? null,
                            })
                          }
                        >
                          ({t('document.stepErrorRows.showStep')})
                        </Anchor>
                      )}
                      </Fragment>
                    );
                  })}
                  {more > 0 && (
                    <Text size="sm" span>
                      {t('document.unmatchedRows.more', { count: String(more) })}
                    </Text>
                  )}
                </Group>
              )}
            </div>
          );
        })}
      </Stack>
      {!allGroups && groups.length > GroupsShown && (
        <Button
          variant="subtle"
          size="xs"
          mt="xs"
          onClick={() => setAllGroups(true)}
          data-testid="document-unmatched-rows-all-tables"
        >
          {t('document.unmatchedRows.showAllTables', { count: String(groups.length) })}
        </Button>
      )}
      {/* ⚠ Лише відкритий: запит кроків (і провайдер запитів) потрібні тільки після кліку. */}
      {stepTarget !== null && <StepTraceModal target={stepTarget} onClose={() => setStepTarget(null)} />}
    </>
  );

  // ✎ B78: у панелі «Проблеми розрахунку» — лише вміст, у згорнутих «Технічних деталях» (без власного банера).
  if (embedded) {
    return (
      <div data-testid="document-unmatched-rows">
        <Text size="sm" fw={600} data-testid="document-unmatched-rows-title">
          {t('document.unmatchedRows.title')}
        </Text>
        {body}
      </div>
    );
  }

  return (
    // ✎ fg6 (людина 08.11): інформаційний банер компактний - один рядок підсумку (назва · кількість · час), підказка
    // ≤ 2 рядків при 1335 px, далі список; згорнутий - лише рядок підсумку.
    <Alert color="brand" variant="light" py="xs" data-testid="document-unmatched-rows">
      {/* ⛔ Перевірка людини 08.11 (b4 при 375 px): `preventGrowOverflow` Group за замовчуванням ділить ширину між
          дітьми, і кнопка стискалась до «Shov». Тепер підсумок переноситься (flex 1, miw 0), а кнопка не стискається. */}
      <Group justify="space-between" wrap="nowrap" gap="sm" align="center" preventGrowOverflow={false}>
        <Text size="sm" flex={1} miw={0}>
          <Text span size="sm" fw={600} data-testid="document-unmatched-rows-title">
            {t('document.unmatchedRows.title')}
          </Text>
          {' · '}
          {/* ⚠ `role="status"` — на тексті з кількістю: Mantine `Alert` власну `role` не приймає (завжди `alert`). */}
          <Text span size="sm" role="status" data-testid="document-unmatched-rows-summary">
            {t('document.unmatchedRows.summary', {
              rows: plural('document.unmatchedRows.rows', count),
              tables: plural('document.unmatchedRows.tables', groups.length),
            })}
            {rows.finishedAt !== null && ` · ${t('document.unmatchedRows.lastRun', { time: formatDateTime(rows.finishedAt) })}`}
          </Text>
        </Text>
        <Button
          variant="subtle"
          size="xs"
          aria-expanded={!collapsed}
          aria-controls={bodyId}
          onClick={toggle}
          data-testid="document-unmatched-rows-toggle"
          style={{ flexShrink: 0 }}
        >
          {collapsed ? t('calculationIssues.show') : t('calculationIssues.hide')}
        </Button>
      </Group>

      <Collapse in={!collapsed} id={bodyId}>
        {body}
      </Collapse>
    </Alert>
  );
}

/**
 * Кількість словом мовою інтерфейсу: ключі `<key>.one|few|other` (`Intl.PluralRules`; `many` читається як `other`).
 * ⚠ Внутрішній код `kz` — для Intl це `kk` (казахська розрізняє лише one/other; у каталозі kz форми однакові).
 */
export function plural(key: 'document.unmatchedRows.rows' | 'document.unmatchedRows.tables', count: number): string {
  let category: string;
  try {
    category = new Intl.PluralRules(language() === 'kz' ? 'kk' : language()).select(count);
  } catch {
    category = count === 1 ? 'one' : 'other';
  }
  const params = { count: String(count) };

  // ⛔ Ключі — літералами: сторож `EndpointCoverageTests` не приймає ключ каталогу, зібраний зі змінних.
  if (key === 'document.unmatchedRows.rows') {
    if (category === 'one') return t('document.unmatchedRows.rows.one', params);
    if (category === 'few') return t('document.unmatchedRows.rows.few', params);

    return t('document.unmatchedRows.rows.other', params);
  }
  if (category === 'one') return t('document.unmatchedRows.tables.one', params);
  if (category === 'few') return t('document.unmatchedRows.tables.few', params);

  return t('document.unmatchedRows.tables.other', params);
}

/** Заголовок групи за видом: неактивні (B66), помилка кроку (B66), без входів, відхилені, без правила. */
function groupTitle(
  inactive: boolean,
  stepError: boolean,
  missingInputs: boolean,
  rejected: boolean,
  table: string,
  count: number,
): string {
  const rows = plural('document.unmatchedRows.rows', count);
  if (inactive) return t('document.inactiveRows.group', { table, rows });
  if (stepError) return t('document.stepErrorRows.group', { table, rows });
  if (missingInputs) return t('document.missingInputRows.group', { table, rows });
  if (rejected) return t('document.rejectedRows.group', { table, rows });

  return t('document.unmatchedRows.group', { table, rows });
}

/**
 * Підпис рядка: номер і (з підказкою) поточне значення в колонках правил — «Row 2: «Sample A-0»».
 * ✎ 10.11: відхилений правилом категорії — правило й колонки, які треба заповнити: «Row 1 · rule R_C05_01_01: fill …».
 */
function rowLabel(row: UnmatchedRowRef, rejected: boolean): string {
  // B66: помилка кроку — «Row 4 · step «Total»: #VALUE» (+ скільки ще кроків рядка впало).
  if (row.stepError !== null) {
    const params = { row: String(row.rowNumber), step: row.stepError.step, error: row.stepError.error };
    return row.stepError.failedSteps > 1
      ? t('document.stepErrorRows.rowMore', { ...params, more: String(row.stepError.failedSteps - 1) })
      : t('document.stepErrorRows.row', params);
  }
  if (rejected) {
    const rule = row.ruleCode ?? t('document.rejectedRows.ruleUnknown');
    if (row.missingColumns.length === 0) return t('document.rejectedRows.row', { row: String(row.rowNumber), rule });

    return t('document.rejectedRows.rowFill', {
      row: String(row.rowNumber),
      rule,
      // AN-164b: вхід поза колонками таблиці вже підписаний каталогом («input data «код»») — без других лапок.
      columns: row.missingColumns.map((column) => (column.column ? `«${column.name}»` : column.name)).join(', '),
    });
  }
  if (row.currentValues === null) return t('document.unmatchedRows.row', { row: String(row.rowNumber) });

  const value = row.currentValues.map((v) => (v === '' ? t('document.unmatchedRows.emptyValue') : v)).join(' / ');

  return t('document.unmatchedRows.rowValue', { row: String(row.rowNumber), value });
}

/**
 * «Calculated only for <колонка>: 0903, 0905, … +N» — допустимі значення правил таблиці (UAT 08.11 b3).
 *
 * ⚠ Решта понад `AllowedValuesShown` — за кнопкою «+N more», що розгортає перелік на місці. Не `Tooltip` Mantine:
 * він тягне Floating UI у граф маршруту, і бюджет документа (`D-132`, 250 КБ gzip) переповнювався на 0.7 КБ;
 * кнопка ще й доступна з клавіатури й на дотик без наведення.
 */
function AllowedValues({ hint }: { readonly hint: UnmatchedTableHint }): JSX.Element {
  const [all, setAll] = useState(false);
  const shown = all ? hint.options : hint.options.slice(0, AllowedValuesShown);
  const rest = hint.options.length - shown.length;
  const methodology = hint.methodologies.join(', ');

  return (
    <Text size="sm" data-testid="document-unmatched-rows-allowed">
      {t('document.unmatchedRows.allowed', { column: hint.columns.join(', '), values: shown.join(', ') })}
      {rest > 0 && (
        <>
          {' '}
          <Anchor
            component="button"
            type="button"
            size="sm"
            onClick={() => setAll(true)}
            data-testid="document-unmatched-rows-allowed-more"
          >
            {t('document.unmatchedRows.allowedMore', { count: String(rest) })}
          </Anchor>
        </>
      )}
      {methodology.length > 0 && ` · ${t('document.unmatchedRows.methodology', { name: methodology })}`}
    </Text>
  );
}
