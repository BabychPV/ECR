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

  const inputs = error.inputs ?? [];
  if (inputs.length > 0) {
    parts.push(
      t('document.formulaErrors.inputs', {
        inputs: inputs.map((input) => `${input.columnCode}=${input.value ?? '∅'}`).join('; '),
      }),
    );
  }

  parts.push(t('document.formulaErrors.since', { when: formatDateTime(error.since) }));

  return parts.join(' · ');
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
}

/**
 * ✎ C-1 (D-475): «Помилки формул» над сіткою — група панелі проблем розрахунку документа.
 *
 * ⛔ Це ПОМИЛКА, що блокує подання (на відміну від «рядків без правила»): тон `red`, розгорнуто за замовчуванням.
 * Значення комірок не змінено — сітка показує останнє правильне; банер каже, яка комірка, що за формула, чому
 * (і з якими входами) і з якого часу; посилання відкриває комірку. Зникає сам після успішного перерахунку: ключ
 * запиту лежить під `['document', id, period]`, який скидає завершений перерахунок.
 */
export function FormulaErrorsBanner({
  userId,
  errors,
  total,
  tables,
  onOpenCell,
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

  return (
    <Alert color="red" variant="light" py="xs" data-testid="document-formula-errors">
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
        <Stack role="list" gap="xs" mt="xs" aria-label={t('document.formulaErrors.title')}>
          {groups.map((group) => (
            <div role="listitem" key={group.key} data-testid="document-formula-errors-group">
              <Text size="sm" fw={500}>
                {group.title}
              </Text>
              {group.errors.map((error) => {
                const column = localized(error.columnHeaderL10n) || error.columnCode;
                const cell = t('document.formulaErrors.cell', { column, row: String(error.rowNumber) });
                const tableInstanceId = group.tableInstanceId;
                return (
                  <Text
                    size="sm"
                    key={`${error.rowKey}:${error.columnCode}`}
                    data-testid="document-formula-error"
                    data-kind={error.kind}
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
      </Collapse>
    </Alert>
  );
}
