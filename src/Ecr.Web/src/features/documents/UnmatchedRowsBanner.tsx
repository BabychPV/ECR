import { Fragment, useMemo, useState, type JSX } from 'react';
import { Alert, Anchor, Button, Collapse, Group, Stack, Text } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import { formatDateTime } from '@/shared/format/datetime';
import { language, t } from '@/shared/i18n';
import type { UnmatchedRows } from './useUnmatchedRows';
import { groupUnmatchedRows, unmatchedRowCount, type UnmatchedRowRef, type UnmatchedTableHint } from './unmatchedRowsModel';

/** Префікс пам'яті «банер згорнуто»; ключ — `префікс:userId` (правило людини fg6: стан — для кожного користувача). */
export const UnmatchedCollapsedKey = 'ecr.unmatchedRows.collapsed';

/** Ключ пам'яті згортання для користувача; без відомого користувача — спільний префікс. */
export function unmatchedCollapsedKey(userId: number | undefined): string {
  return userId === undefined ? UnmatchedCollapsedKey : `${UnmatchedCollapsedKey}:${String(userId)}`;
}

/** Скільки допустимих значень показується в підказці таблиці; решта — «+N» з переліком у тултіпі. */
export const AllowedValuesShown = 8;

/** Скільки посилань на рядки показується в групі таблиці; решта — «і ще N». */
export const RowLinksPerTable = 20;

function readCollapsed(key: string): boolean {
  try {
    return window.localStorage.getItem(key) === '1';
  } catch {
    return false;
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
  /** Відкрити рядок у сітці: аркуш, прокрутка, фокус і підсвітка (шлях зауважень, `ФВ-5.6`). */
  readonly onOpenRow: (tableInstanceId: number, rowKey: string) => void;
  /** Вузький екран (документ лише для читання): дія підказки — відкрити документ ширше, а не F9 (UAT 08.11). */
  readonly narrow?: boolean;
}

/**
 * «Частину рядків не взято в розрахунок» над сіткою (D14 + AN-69; UAT 08.11 b1–b4).
 *
 * ⛔ Це ІНФОРМАЦІЯ, а не помилка: тон `info` макета (`Alert color="brand" variant="light"`); підказка каже, чому рядки
 * не рахуються і що зробити, ЯКЩО рядок треба рахувати (свіжий погляд 08.11: без «нічого виправляти» поруч із дією). Рядки, відсіяні статусом «не Running» (D-425), сюди не
 * потрапляють — сервер пише лише ті, яким не підійшло жодне правило методології (`CalculationOrchestrator.UnmatchedRowsOf`).
 *
 * ⚠ Групи — таблиця з аркушем і кількістю («Utility · 9.01 Unit 400 — rows: 2»), кожен рядок — кнопка-посилання,
 * що відкриває його в сітці. Згортання пам'ятається (`localStorage` під `try/catch`), кількість видно і згорнутим.
 *
 * ⚠ Попередня згортна панель AN-69 (`CalculationIssuesPanel`, жовта, лише активний аркуш) показувала ТІ САМІ рядки
 * вдруге іншим тоном — її замінено цим банером; позначки в дереві таблиць лишились.
 */
export function UnmatchedRowsBanner({ userId, rows, tables, onOpenRow, narrow = false }: UnmatchedRowsBannerProps): JSX.Element | null {
  const groups = useMemo(
    () => groupUnmatchedRows(rows.issues, tables, rows.hints),
    [rows.issues, tables, rows.hints],
  );
  const storageKey = unmatchedCollapsedKey(userId);
  const [collapsed, setCollapsed] = useState(() => readCollapsed(storageKey));

  if (groups.length === 0) return null;

  const count = unmatchedRowCount(groups);
  const toggle = (): void => {
    const next = !collapsed;
    setCollapsed(next);
    writeCollapsed(storageKey, next);
  };
  const bodyId = 'document-unmatched-rows-body';

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
        <Text size="sm" mt="xs" data-testid="document-unmatched-rows-hint">
          {t(narrow ? 'document.unmatchedRows.hintNarrow' : 'document.unmatchedRows.hint')}
        </Text>
        <Stack
          role="list"
          gap="xs"
          mt="xs"
          aria-label={t('calculationIssues.listLabel')}
          mah={260}
          style={{ overflowY: 'auto' }}
        >
          {groups.map((group) => {
            const shown = group.rows.slice(0, RowLinksPerTable);
            const more = group.rows.length - shown.length;
            const table = group.sheetName.length > 0 ? `${group.sheetName} · ${group.tableName}` : group.tableName;

            return (
              <div role="listitem" key={group.key} data-testid="document-unmatched-rows-group">
                <Text size="sm" span fw={500}>
                  {t('document.unmatchedRows.group', { table, rows: plural('document.unmatchedRows.rows', group.rows.length) })}
                </Text>
                {group.hint === null && group.methodologyCode !== null && (
                  <Text size="xs" span>
                    {` · ${group.methodologyCode}`}
                  </Text>
                )}
                {group.hint !== null && <AllowedValues hint={group.hint} />}
                <Group gap="xs" wrap="wrap">
                  {shown.map((row, index) => {
                    const label = rowLabel(row);
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
                        data-testid="document-unmatched-row-link"
                        data-row-key={row.rowKey}
                        aria-label={`${table} · ${label}`}
                        onClick={() => onOpenRow(tableInstanceId, row.rowKey)}
                      >
                        {label}
                      </Anchor>
                      </Fragment>
                    );
                  })}
                  {more > 0 && (
                    <Text size="sm" span>
                      {t('document.unmatchedRows.more', { count: String(more) })}
                    </Text>
                  )}
                </Group>
              </div>
            );
          })}
        </Stack>
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

/** Підпис рядка: номер і (з підказкою) поточне значення в колонках правил — «Row 2: «Sample A-0»». */
function rowLabel(row: UnmatchedRowRef): string {
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