import { useMemo, useState, type JSX } from 'react';
import { Alert, Badge, Button, Collapse, Group, Stack, Text } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import { calcErrorCause } from '@/features/grid/calcError';
import { formatDateTime } from '@/shared/format/datetime';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { language, t } from '@/shared/i18n';
import { FormulaErrorsBanner } from './FormulaErrorsBanner';
import { UnmatchedRowsBanner, plural } from './UnmatchedRowsBanner';
import { buildIssueCauses, namesOf, type IssueCause, type IssueTarget } from './calculationIssuesPanelModel';
import type { UnmatchedRows } from './useUnmatchedRows';

interface CalculationIssuesPanelProps {
  /** Користувач сесії (пам'ять згортання вкладених переліків). */
  readonly userId?: number | undefined;
  readonly periodKey: number;
  readonly rows: UnmatchedRows;
  /** Усі таблиці документа за період: назви, екземпляр для переходу. */
  readonly tables: readonly DocumentTableDto[];
  /** Відкрити комірку/рядок у сітці (той самий шлях, що «Перейти до комірки»). */
  readonly onOpenCell: (tableInstanceId: number, rowKey: string, columnCode: string | null) => void;
  /**
   * «Відкрити HSE400»: перехід до документа HSE400 року; `false` — документа не знайдено (панель каже про це).
   * Без обробника кнопки немає.
   */
  readonly onOpenHse400?: (() => Promise<boolean>) | undefined;
  readonly narrow?: boolean;
}

/** «N клітинок» мовою інтерфейсу (`Intl.PluralRules`; ключі літералами — сторож каталогу). */
function cellsText(count: number): string {
  let category: string;
  try {
    category = new Intl.PluralRules(language() === 'kz' ? 'kk' : language()).select(count);
  } catch {
    category = count === 1 ? 'one' : 'other';
  }
  const params = { count: String(count) };
  if (category === 'one') return t('document.calcIssues.cells.one', params);
  if (category === 'few') return t('document.calcIssues.cells.few', params);

  return t('document.calcIssues.cells.other', params);
}

/** Заголовок причини — людськими словами, без формул і кодів. */
function causeTitle(cause: IssueCause): string {
  switch (cause.kind) {
    case 'hse400':
      return cause.subjects.length > 0
        ? t('document.calcIssues.hse400.title', { count: String(cause.subjects.length) })
        : t('document.calcIssues.hse400.titleAny');
    case 'formula':
      return t('document.calcIssues.formula.title', { cause: calcErrorCause(cause.errorCode ?? '') });
    case 'blocked':
      return t('document.calcIssues.blocked.title');
    case 'missingInputs':
      return t('document.calcIssues.missingInputs.title', { rows: plural('document.unmatchedRows.rows', cause.count) });
    case 'rejected':
      return t('document.calcIssues.rejected.title', { rows: plural('document.unmatchedRows.rows', cause.count) });
    case 'unmatched':
      return t('document.calcIssues.unmatched.title', { rows: plural('document.unmatchedRows.rows', cause.count) });
  }
}

/** Підрядок причини: що саме (паливо), які показники, де (таблиці). */
function causeDetail(cause: IssueCause): string {
  const parts: string[] = [];
  if (cause.subjects.length > 0) parts.push(namesOf(cause.subjects));
  if (cause.unit === 'cells' && cause.indicators.length > 0) {
    parts.push(
      t('document.calcIssues.indicators', {
        count: String(cause.indicators.length),
        names: namesOf(cause.indicators),
      }),
    );
  }
  if (cause.tables.length > 0) {
    parts.push(
      cause.tables.length === 1
        ? cause.tables[0]!
        : t('document.calcIssues.inTables', { tables: plural('document.unmatchedRows.tables', cause.tables.length) }),
    );
  }

  return parts.join(' · ');
}

/**
 * ✎ B78 (людина 13.11, погоджений макет `.sync-local\reports\B78-calc-issues-mockup.md`): ОДНА панель «Проблеми
 * розрахунку» замість двох банерів («Formula errors» і «Some rows are not included»).
 *
 * - Заголовок: «Розрахунок за <місяць> не завершено · подання заблоковано» (є помилки формул) і час прогону.
 * - Один рядок на ПРИЧИНУ (не на комірку) з бейджем кількості й людськими назвами (паливо назвою, не Id).
 * - Дії: «Відкрити HSE400» — лише коли причина в складі HSE400; «Показати клітинки/рядки» — перехід до першої
 *   комірки причини (решта позначена в сітці) і розгорнуті деталі з переліком.
 * - Формули, коди, переліки комірок і номери рядків — лише в згорнутих «Технічних деталях» (колишні банери, вбудовано).
 *
 * ⛔ Тон: є помилка формули — `statusError` (блокує подання); лише рядки поза розрахунком — `brand` (інформація).
 */
export function CalculationIssuesPanel({
  userId,
  periodKey,
  rows,
  tables,
  onOpenCell,
  onOpenHse400,
  narrow = false,
}: CalculationIssuesPanelProps): JSX.Element | null {
  const causes = useMemo(() => buildIssueCauses(rows, tables), [rows, tables]);
  const [details, setDetails] = useState(false);
  // ✎ F13 P3-2: підсумок «Відкрити HSE400» — документа немає або відкрити не вдалося (відмова API).
  const [hse400State, setHse400State] = useState<'idle' | 'pending' | 'notFound' | 'failed'>('idle');

  if (causes.length === 0) return null;

  const blocking = rows.formulaErrors.length > 0;
  const period = formatPeriodKey(periodKey);
  const detailsId = 'document-calc-issues-details';

  const show = (target: IssueTarget | null): void => {
    setDetails(true);
    if (target !== null) onOpenCell(target.tableInstanceId, target.rowKey, target.columnCode);
  };

  const openHse400 = async (): Promise<void> => {
    if (onOpenHse400 === undefined) return;
    setHse400State('pending');
    try {
      setHse400State((await onOpenHse400()) ? 'idle' : 'notFound');
    } catch {
      setHse400State('failed');
    }
  };

  return (
    <Alert
      color={blocking ? 'statusError' : 'brand'}
      variant="light"
      py="xs"
      data-testid="document-calc-issues"
      data-blocking={blocking ? 'true' : 'false'}
    >
      <Group justify="space-between" wrap="wrap" gap="xs" align="baseline">
        {/* ⚠ `role="status"` — на тексті заголовка: Mantine `Alert` власну `role` не приймає (завжди `alert`). */}
        <Text size="sm" fw={600} role="status" data-testid="document-calc-issues-title" flex={1} miw={0}>
          {blocking
            ? t('document.calcIssues.titleBlocked', { period })
            : t('document.calcIssues.title', { period })}
        </Text>
        {rows.finishedAt !== null && (
          <Text size="xs" c="dimmed" data-testid="document-calc-issues-updated">
            {t('document.unmatchedRows.lastRun', { time: formatDateTime(rows.finishedAt) })}
          </Text>
        )}
      </Group>

      <Stack role="list" gap="xs" mt="xs" aria-label={t('document.calcIssues.listLabel')}>
        {causes.map((cause) => {
          const title = causeTitle(cause);
          const detail = causeDetail(cause);
          const badge = cause.unit === 'cells' ? cellsText(cause.count) : plural('document.unmatchedRows.rows', cause.count);

          return (
            <Group
              role="listitem"
              key={cause.key}
              gap="sm"
              wrap={narrow ? 'wrap' : 'nowrap'}
              align="flex-start"
              data-testid="document-calc-issue"
              data-kind={cause.kind}
              data-count={cause.count}
            >
              <Badge
                variant="light"
                color={cause.unit === 'cells' ? 'statusError' : 'brand'}
                miw={88}
                style={{ flexShrink: 0 }}
                data-testid="document-calc-issue-count"
              >
                {badge}
              </Badge>
              <div style={{ flex: 1, minWidth: 0 }}>
                <Text size="sm" fw={500} data-testid="document-calc-issue-title">
                  {title}
                </Text>
                {detail.length > 0 && (
                  <Text size="xs" c="dimmed" data-testid="document-calc-issue-detail">
                    {detail}
                  </Text>
                )}
                {cause.kind === 'hse400' && hse400State === 'notFound' && (
                  <Text size="xs" data-testid="document-calc-issue-hse400-missing">
                    {t('document.calcIssues.hse400NotFound', { year: String(Math.trunc(periodKey / 100)) })}
                  </Text>
                )}
                {cause.kind === 'hse400' && hse400State === 'failed' && (
                  <Text size="xs" fw={600} role="alert" data-testid="document-calc-issue-hse400-failed">
                    {t('document.calcIssues.hse400OpenFailed')}
                  </Text>
                )}
              </div>
              <Group gap="xs" wrap="nowrap" style={{ flexShrink: 0 }}>
                {cause.kind === 'hse400' && onOpenHse400 !== undefined && (
                  <Button
                    size="xs"
                    variant="light"
                    data-testid="document-calc-issue-open-hse400"
                    loading={hse400State === 'pending'}
                    aria-label={`${title} · ${t('document.calcIssues.openHse400')}`}
                    onClick={() => void openHse400()}
                  >
                    {t('document.calcIssues.openHse400')}
                  </Button>
                )}
                {cause.target !== null && (
                  <Button
                    size="xs"
                    variant="subtle"
                    data-testid="document-calc-issue-show"
                    aria-label={`${title} · ${cause.unit === 'cells' ? t('document.calcIssues.showCells') : t('document.calcIssues.showRows')}`}
                    onClick={() => show(cause.target)}
                  >
                    {cause.unit === 'cells' ? t('document.calcIssues.showCells') : t('document.calcIssues.showRows')}
                  </Button>
                )}
              </Group>
            </Group>
          );
        })}
      </Stack>

      <Button
        variant="subtle"
        size="xs"
        mt="xs"
        aria-expanded={details}
        aria-controls={detailsId}
        onClick={() => setDetails((open) => !open)}
        data-testid="document-calc-issues-details-toggle"
      >
        {details ? `▴ ${t('document.calcIssues.details')}` : `▾ ${t('document.calcIssues.details')}`}
      </Button>
      <Collapse in={details} id={detailsId}>
        <Stack gap="sm" mt="xs">
          {rows.formulaErrors.length > 0 && (
            <FormulaErrorsBanner
              embedded
              userId={userId}
              errors={rows.formulaErrors}
              total={rows.formulaErrorTotal}
              tables={tables}
              onOpenCell={onOpenCell}
            />
          )}
          {rows.issues.length > 0 && (
            <UnmatchedRowsBanner
              embedded
              userId={userId}
              rows={rows}
              tables={tables}
              onOpenRow={onOpenCell}
              narrow={narrow}
            />
          )}
        </Stack>
      </Collapse>
    </Alert>
  );
}
