import { useMemo, useState, type JSX } from 'react';
import { Alert, Anchor, Button, Collapse, Group, Stack, Text } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import type { FormulaErrorIssue } from '@/features/calculationIssues/api';
import { copyFieldsOf } from '@/features/documents/sheetCopies/sheetCopyTypes';
import { calcErrorCause, calcErrorSourceText } from '@/features/grid/calcError';
import { formatDateTime } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';

/** Префікс пам'яті «банер згорнуто»; ключ — `префікс:userId` (стан — для кожного користувача, як fg6). */
export const FormulaErrorsCollapsedKey = 'ecr.formulaErrors.collapsed';

function storageKeyOf(userId: number | undefined): string {
  return userId === undefined ? FormulaErrorsCollapsedKey : `${FormulaErrorsCollapsedKey}:${String(userId)}`;
}

/** ⛔ Без збереженого вибору банер РОЗГОРНУТИЙ: помилка блокує подання, її не ховають за замовчуванням. */
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

/** Група банера: одна таблиця (оригінал або копія) з її комірками. */
interface FormulaErrorGroup {
  readonly key: string;
  readonly title: string;
  readonly tableInstanceId: number | null;
  readonly errors: readonly FormulaErrorIssue[];
}

/** Групує помилки за таблицею (код + копія); назви таблиці й аркуша — з переліку таблиць документа. */
export function groupFormulaErrors(
  errors: readonly FormulaErrorIssue[],
  tables: readonly DocumentTableDto[],
): FormulaErrorGroup[] {
  const keyOf = (tableCode: string, copy: number | null): string =>
    copy === null ? tableCode : `${tableCode}#${String(copy)}`;
  const tableOf = new Map<string, DocumentTableDto>();
  for (const table of tables) tableOf.set(keyOf(table.tableCode, copyFieldsOf(table).sheetCopyId), table);

  const groups = new Map<string, FormulaErrorIssue[]>();
  for (const error of errors) {
    const key = keyOf(error.tableCode, error.sheetCopyId ?? null);
    const list = groups.get(key) ?? [];
    list.push(error);
    groups.set(key, list);
  }

  return [...groups.entries()].map(([key, list]) => {
    const table = tableOf.get(key);
    const tableName = table === undefined ? list[0]!.tableCode : localized(table.tableNameL10n) || list[0]!.tableCode;
    const sheetName = table === undefined ? '' : localized(table.sheetNameL10n) || table.sheetCode;
    return {
      key,
      title: sheetName.length > 0 ? `${sheetName} · ${tableName}` : tableName,
      tableInstanceId: table?.tableInstanceId ?? null,
      errors: list,
    };
  });
}

/** Входи помилки текстом «колонка=значення; …» — як їх показує банер. */
function inputsText(error: FormulaErrorIssue): string {
  return (error.inputs ?? []).map((input) => `${input.columnCode}=${input.value ?? '∅'}`).join('; ');
}

/** Рядок банера: одна помилка або кілька однакових (та сама колонка, формула, код і входи) у різних рядках. */
export interface FormulaErrorLine {
  readonly key: string;
  /** Перша комірка — її відкриває «Перейти до комірки». */
  readonly error: FormulaErrorIssue;
  /** Усі комірки рядка банера в порядку сервера (сітки). */
  readonly cells: readonly FormulaErrorIssue[];
}

function rowTextOf(error: FormulaErrorIssue): string {
  return localized(error.rowLabelL10n ?? null) || String(error.rowNumber);
}

/**
 * ✎ B78 (живий прохід W15t, doc 96 Land): одна й та сама власна помилка в кількох рядках — ОДИН рядок банера з
 * переліком рядків і кількістю. Причина повторів: різні рядки таблиці з тими самими входами (два рядки з паливом 37,
 * два — з 36) дають ту саму помилку довідника. Це різні комірки (сервер віддає кожну, лічильник — комірки), але
 * для людини — одна причина. Ключ: колонка + код + формула + входи (`колонка=значення`). «Не оновлено» не зводиться:
 * сервер уже дає один запис на джерело.
 */
export function mergeSameErrors(errors: readonly FormulaErrorIssue[]): FormulaErrorLine[] {
  const lines = new Map<string, FormulaErrorIssue[]>();
  for (const error of errors) {
    const key =
      error.kind === 'Error'
        ? ['E', error.columnCode, error.errorCode, error.formula ?? '', inputsText(error)].join('\u0001')
        : ['B', error.rowKey, error.columnCode, error.source?.tableCode, error.source?.rowKey, error.source?.columnCode]
            .map((part) => part ?? '')
            .join('\u0001');
    const list = lines.get(key) ?? [];
    list.push(error);
    lines.set(key, list);
  }

  return [...lines.entries()].map(([key, cells]) => {
    const first = cells[0]!;
    if (cells.length === 1) return { key, error: first, cells };

    const since = cells.map((c) => c.since).reduce((a, b) => (b < a ? b : a));
    const dependentCount = cells.reduce((sum, c) => sum + c.dependentCount, 0);
    return { key, error: { ...first, since, dependentCount }, cells };
  });
}

/** Опис помилки однієї комірки словами: причина (або джерело для «не оновлено»), формула, входи, з якого часу. */
function describe(error: FormulaErrorIssue): string {
  const parts: string[] = [];
  if (error.kind === 'Blocked') {
    parts.push(
      error.source === null || error.source === undefined
        ? t('document.formulaErrors.blockedHidden')
        : t('document.formulaErrors.blockedBy', { source: calcErrorSourceText(error.source) }),
    );
  } else {
    parts.push(calcErrorCause(error.errorCode));
  }

  if (error.formula !== null && error.formula !== undefined) {
    parts.push(t('document.formulaErrors.formula', { formula: error.formula }));
  }

  if ((error.inputs ?? []).length > 0) {
    parts.push(t('document.formulaErrors.inputs', { inputs: inputsText(error) }));
  }

  parts.push(t('document.formulaErrors.since', { when: formatDateTime(error.since) }));

  // ✎ Корінь показується один раз; залежні — лише кількістю (самі вони позначені в сітці).
  if (error.kind !== 'Blocked' && error.dependentCount > 0) {
    parts.push(t('document.formulaErrors.dependents', { count: String(error.dependentCount) }));
  }

  return parts.join(' · ');
}

/**
 * Підпис комірки: назва фіксованого рядка шаблону, інакше «рядок N»; для «не оновлено» — і кількість комірок;
 * ✎ B78: однакова помилка в кількох рядках — «колонка, рядки 1, 2 (комірок: 2)».
 */
function cellLabel(line: FormulaErrorLine): string {
  const { error, cells } = line;
  const column = localized(error.columnHeaderL10n) || error.columnCode;
  if (cells.length > 1) {
    return t('document.formulaErrors.cellRows', {
      column,
      rows: cells.map(rowTextOf).join(', '),
      count: String(cells.length),
    });
  }

  const row = localized(error.rowLabelL10n ?? null);
  const cell =
    row.length > 0
      ? t('document.formulaErrors.cellNamed', { column, row })
      : t('document.formulaErrors.cell', { column, row: String(error.rowNumber) });

  return error.kind === 'Blocked' && error.dependentCount > 1
    ? t('document.formulaErrors.blockedCells', { cell, count: String(error.dependentCount) })
    : cell;
}

interface FormulaErrorsBannerProps {
  /** Користувач сесії: згортання пам'ятається окремо для кожного. */
  readonly userId?: number | undefined;
  readonly errors: readonly FormulaErrorIssue[];
  /** Скільки комірок з помилкою всього (сервер віддає не більше 500); без — довжина переліку. */
  readonly total?: number | undefined;
  /** Усі таблиці документа за період: назви таблиць і аркушів, екземпляр для переходу. */
  readonly tables: readonly DocumentTableDto[];
  /** Відкрити комірку в сітці: аркуш, прокрутка, фокус (той самий шлях, що «рядки без правила»). */
  readonly onOpenCell: (tableInstanceId: number, rowKey: string, columnCode: string | null) => void;
  /** ✎ B78: перелік у «Технічних деталях» панелі проблем розрахунку — без власного банера й згортання. */
  readonly embedded?: boolean;
}

/**
 * ✎ C-1 (D-475): «Помилки формул» над сіткою — група панелі проблем розрахунку документа.
 *
 * ⛔ Це ПОМИЛКА, що блокує подання (на відміну від «рядків без правила»): тон `statusError`, розгорнуто за замовчуванням.
 * Значення комірок не змінено — сітка показує останнє правильне; банер каже, яка комірка, що за формула, чому
 * (і з якими входами) і з якого часу; посилання відкриває комірку. ✎ Один запис на КОРІНЬ помилки з «N залежних
 * комірок не оновлено» — залежні позначає сітка, перелік їх по одній не показує. Зникає сам після успішного перерахунку: ключ
 * запиту лежить під `['document', id, period]`, який скидає завершений перерахунок.
 */
export function FormulaErrorsBanner({
  userId,
  errors,
  total,
  tables,
  onOpenCell,
  embedded = false,
}: FormulaErrorsBannerProps): JSX.Element | null {
  const groups = useMemo(() => groupFormulaErrors(errors, tables), [errors, tables]);
  const storageKey = storageKeyOf(userId);
  const [collapsed, setCollapsed] = useState(() => readCollapsed(storageKey));

  if (groups.length === 0) return null;

  const toggle = (): void => {
    const next = !collapsed;
    setCollapsed(next);
    writeCollapsed(storageKey, next);
  };
  const bodyId = 'document-formula-errors-body';

  const list = (
    <Stack role="list" gap="xs" mt="xs" aria-label={t('document.formulaErrors.title')}>
      {groups.map((group) => (
        <div role="listitem" key={group.key} data-testid="document-formula-errors-group">
          <Text size="sm" fw={500}>
            {group.title}
          </Text>
          {mergeSameErrors(group.errors).map((line) => {
            const { error } = line;
            const cell = cellLabel(line);
            const tableInstanceId = group.tableInstanceId;
            return (
              <Text
                size="sm"
                key={line.key}
                data-testid="document-formula-error"
                data-kind={error.kind}
                data-cells={line.cells.length}
              >
                <Text span size="sm" fw={500}>
                  {cell}
                </Text>
                {': '}
                {describe(error)}
                {tableInstanceId !== null && (
                  <>
                    {' '}
                    <Anchor
                      component="button"
                      type="button"
                      size="sm"
                      aria-label={`${group.title} · ${cell} · ${t('document.formulaErrors.open')}`}
                      data-testid="document-formula-error-open"
                      onClick={() => onOpenCell(tableInstanceId, error.rowKey, error.columnCode)}
                    >
                      {t('document.formulaErrors.open')}
                    </Anchor>
                  </>
                )}
              </Text>
            );
          })}
        </div>
      ))}
    </Stack>
  );

  // ✎ B78: у панелі «Проблеми розрахунку» — лише перелік, у згорнутих «Технічних деталях» (без власного банера).
  if (embedded) {
    return (
      <div data-testid="document-formula-errors">
        <Text size="sm" fw={600}>
          {t('document.formulaErrors.title')}
        </Text>
        {list}
      </div>
    );
  }

  return (
    <Alert color="statusError" variant="light" py="xs" data-testid="document-formula-errors">
      <Group justify="space-between" wrap="nowrap" gap="sm" align="center" preventGrowOverflow={false}>
        <Text size="sm" flex={1} miw={0}>
          <Text span size="sm" fw={600}>
            {t('document.formulaErrors.title')}
          </Text>
          {' · '}
          <Text span size="sm" role="status" data-testid="document-formula-errors-summary">
            {t('document.formulaErrors.summary', { count: String(Math.max(total ?? 0, errors.length)) })}
          </Text>
        </Text>
        <Button
          variant="subtle"
          size="xs"
          aria-expanded={!collapsed}
          aria-controls={bodyId}
          onClick={toggle}
          style={{ flexShrink: 0 }}
        >
          {collapsed ? t('calculationIssues.show') : t('calculationIssues.hide')}
        </Button>
      </Group>

      <Collapse in={!collapsed} id={bodyId}>
        {list}
      </Collapse>
    </Alert>
  );
}
