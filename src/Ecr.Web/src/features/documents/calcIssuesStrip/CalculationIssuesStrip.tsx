import { useId, useMemo, useState, type JSX } from 'react';
import { Button, Collapse, Group, Stack, Table, Text, UnstyledButton } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import { formatMonthName, formatPeriodKey } from '@/shared/format';
import { language, t } from '@/shared/i18n';
import { CalculationIssuesPanel } from '../CalculationIssuesPanel';
import type { IssueCauseKind } from '../calculationIssuesPanelModel';
import type { MonthUnmatchedRows } from '../useUnmatchedRows';
import { monthCount, readDetailsOpen, summarizeCalcIssues, writeDetailsOpen, type StripReason } from './calcIssuesStripModel';
import './calcIssuesStrip.css';

export interface CalculationIssuesStripProps {
  readonly userId?: number | undefined;
  readonly documentId?: number | undefined;
  /** Місяці з проблемами (документ «рік» — кожен місяць; копія на місяць — один). */
  readonly months: readonly MonthUnmatchedRows[];
  readonly tables: readonly DocumentTableDto[];
  readonly onOpenCell: (tableInstanceId: number, rowKey: string, columnCode: string | null) => void;
  /** «Open HSE400» місяця; `false` — документа HSE400 не знайдено. */
  readonly onOpenHse400?: ((periodKey: number) => Promise<boolean>) | undefined;
  readonly onOpenSource?: ((documentId: number, periodKey: number) => void) | undefined;
  readonly narrow?: boolean;
}

/** Коротка назва виду причини для смуги й матриці — ключі ЛІТЕРАЛАМИ (сторож каталогу). */
function kindLabel(kind: IssueCauseKind): string {
  switch (kind) {
    case 'sourceNotApproved':
      return t('document.notices.kind.sourceNotApproved');
    case 'sourceChanged':
      return t('document.notices.kind.sourceChanged');
    case 'sourceOther':
      return t('document.notices.kind.sourceOther');
    case 'hse400':
      return t('document.notices.kind.hse400');
    case 'formula':
      return t('document.notices.kind.formula');
    case 'blocked':
      return t('document.notices.kind.blocked');
    case 'missingInputs':
      return t('document.notices.kind.missingInputs');
    case 'rejected':
      return t('document.notices.kind.rejected');
    case 'unmatched':
      return t('document.notices.kind.unmatched');
    case 'stepError':
      return t('document.notices.kind.stepError');
    case 'inactive':
      return t('document.notices.kind.inactive');
  }
}

/** «N months» — ті самі ключі, що бейдж місяців панелі B78 (літералами: сторож каталогу; форми one/few/other). */
function monthsText(count: number): string {
  let category: string;
  try {
    category = new Intl.PluralRules(language() === 'kz' ? 'kk' : language()).select(count);
  } catch {
    category = count === 1 ? 'one' : 'other';
  }
  const params = { count: String(count) };
  if (category === 'one') return t('document.calcIssues.months.one', params);
  if (category === 'few') return t('document.calcIssues.months.few', params);

  return t('document.calcIssues.months.other', params);
}

/** Місяць у заголовку матриці: «Sep» (рік — у підписі смуги, якщо місяці одного року), інакше «Sep 2026». */
function monthHead(periodKey: number, oneYear: boolean): string {
  const name = formatMonthName(periodKey % 100).slice(0, 3);
  return oneYear ? name : `${name} ${String(Math.trunc(periodKey / 100))}`;
}

/**
 * ✎ b14 doc-notices-compact (людина 25.11: три блоки «Calculation for <місяць>…» по 3 рядки + технічні деталі — сітка з
 * другого екрана): проблеми розрахунку — ОДНА смуга заввишки в рядок, згорнута за замовчуванням:
 * «Calculation: rows not included in 3 months — 20 formula failed · 76 missing input · 3 inactive [Details]».
 * Тон — за найгіршою причиною (помилка формули / не затверджене джерело — `statusError`, інакше `brand`).
 *
 * [Details] розгортає одну матрицю «причина × місяць» (кількість і «Show rows» — той самий перехід до першої комірки
 * причини, що в панелі B78) і, згорнуті, «Technical details» — колишні панелі місяців без змін (формули, коди, переліки,
 * дати джерела, «Open HSE400»). Розгорнуто чи ні — пам'ятається на людину (localStorage, без нього — згорнуто).
 *
 * ⚠ a11y: смуга — `section` з назвою (region), Details — кнопка з `aria-expanded`/`aria-controls`.
 */
export function CalculationIssuesStrip({
  userId,
  documentId,
  months,
  tables,
  onOpenCell,
  onOpenHse400,
  onOpenSource,
  narrow = false,
}: CalculationIssuesStripProps): JSX.Element | null {
  const summary = useMemo(() => summarizeCalcIssues(months, tables), [months, tables]);
  const [open, setOpen] = useState(() => readDetailsOpen(userId));
  const [technical, setTechnical] = useState(false);
  const [hse400, setHse400] = useState<'idle' | 'pending' | 'notFound' | 'failed'>('idle');
  const detailsId = useId();
  const technicalId = useId();

  if (summary.reasons.length === 0) return null;

  const oneYear = new Set(summary.months.map((key) => Math.trunc(key / 100))).size === 1;
  const where =
    summary.months.length === 1
      ? formatPeriodKey(summary.months[0]!)
      : monthsText(summary.months.length);
  const title = summary.blocking
    ? t('document.notices.calcTitleBlocked', { months: where })
    : t('document.notices.calcTitle', { months: where });
  const reasonsText = summary.reasons.map((reason) => `${String(reason.total)} ${kindLabel(reason.kind)}`).join(' · ');

  const toggle = (): void => {
    setOpen((value) => {
      writeDetailsOpen(userId, !value);
      return !value;
    });
  };

  const openHse400 = async (periodKey: number): Promise<void> => {
    if (onOpenHse400 === undefined) return;
    setHse400('pending');
    try {
      setHse400((await onOpenHse400(periodKey)) ? 'idle' : 'notFound');
    } catch {
      setHse400('failed');
    }
  };

  const cellAction = (reason: StripReason, periodKey: number): JSX.Element | null => {
    const causes = reason.byMonth.get(periodKey) ?? [];
    const label = `${kindLabel(reason.kind)} · ${formatPeriodKey(periodKey)}`;
    if (reason.kind === 'hse400' && onOpenHse400 !== undefined) {
      return (
        <Button size="xs" variant="subtle" loading={hse400 === 'pending'} aria-label={`${label} · ${t('document.calcIssues.openHse400')}`} onClick={() => void openHse400(periodKey)} data-testid="document-calc-strip-open-hse400">
          {t('document.calcIssues.openHse400')}
        </Button>
      );
    }
    const source = causes.find((cause) => cause.source?.documentId != null)?.source;
    if (source !== undefined && source.documentId !== null && onOpenSource !== undefined) {
      const sourceId = source.documentId;
      return (
        <Button size="xs" variant="subtle" aria-label={`${label} · ${t('document.calcIssues.openHse400')}`} onClick={() => onOpenSource(sourceId, source.periodKey)} data-testid="document-calc-strip-open-source">
          {t('document.calcIssues.openHse400')}
        </Button>
      );
    }
    const target = causes.find((cause) => cause.target !== null);
    if (target === undefined || target.target === null) return null;
    const goal = target.target;
    const text = target.unit === 'cells' ? t('document.calcIssues.showCells') : t('document.calcIssues.showRows');

    return (
      <Button
        size="xs"
        variant="subtle"
        aria-label={`${label} · ${text}`}
        onClick={() => onOpenCell(goal.tableInstanceId, goal.rowKey, goal.columnCode)}
        data-testid="document-calc-strip-show"
        data-kind={reason.kind}
        data-period={periodKey}
      >
        {text}
      </Button>
    );
  };

  return (
    <section
      className="ecr-calc-strip"
      aria-label={t('document.calcIssues.listLabel')}
      data-testid="document-calc-strip"
      data-blocking={summary.blocking ? 'true' : 'false'}
      data-months={summary.months.join(',')}
    >
      <div className="ecr-calc-strip-line" data-tone={summary.blocking ? 'error' : 'info'}>
        <span className="ecr-calc-strip-icon" aria-hidden="true">
          {summary.blocking ? '!' : 'i'}
        </span>
        <Text size="sm" className="ecr-calc-strip-text" title={`${title} — ${reasonsText}`} data-testid="document-calc-strip-text">
          <Text span fw={600} inherit>
            {title}
          </Text>
          {' — '}
          {reasonsText}
        </Text>
        <UnstyledButton
          className="ecr-calc-strip-toggle"
          aria-expanded={open}
          aria-controls={detailsId}
          onClick={toggle}
          data-testid="document-calc-strip-toggle"
        >
          {open ? `▴ ${t('document.notices.details')}` : `▾ ${t('document.notices.details')}`}
        </UnstyledButton>
      </div>

      <Collapse in={open} id={detailsId}>
        <Stack gap="xs" mt="xs" data-testid="document-calc-strip-details">
          <div className="ecr-calc-strip-matrix">
            <Table withTableBorder withColumnBorders verticalSpacing={4} horizontalSpacing="xs" data-testid="document-calc-strip-matrix">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">{t('document.notices.reason')}</Table.Th>
                  {summary.months.map((key) => (
                    <Table.Th key={key} scope="col" data-period={key}>
                      {monthHead(key, oneYear)}
                    </Table.Th>
                  ))}
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {summary.reasons.map((reason) => (
                  <Table.Tr key={reason.kind} data-kind={reason.kind}>
                    <Table.Th scope="row">{kindLabel(reason.kind)}</Table.Th>
                    {summary.months.map((key) => {
                      const count = monthCount(reason, key);
                      return (
                        <Table.Td key={key} data-period={key} data-count={count}>
                          {count === 0 ? (
                            <Text size="xs" c="dimmed">
                              —
                            </Text>
                          ) : (
                            <Group gap="xs" wrap="nowrap">
                              <Text size="sm" ff="monospace">
                                {count}
                              </Text>
                              {cellAction(reason, key)}
                            </Group>
                          )}
                        </Table.Td>
                      );
                    })}
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </div>
          {hse400 === 'notFound' && (
            <Text size="xs" data-testid="document-calc-strip-hse400-missing">
              {t('document.calcIssues.hse400NotFound', { year: String(Math.trunc((summary.months[0] ?? 0) / 100)) })}
            </Text>
          )}
          {hse400 === 'failed' && (
            <Text size="xs" fw={600} role="alert">
              {t('document.calcIssues.hse400OpenFailed')}
            </Text>
          )}

          <Button
            variant="subtle"
            size="xs"
            style={{ alignSelf: 'flex-start' }}
            aria-expanded={technical}
            aria-controls={technicalId}
            onClick={() => setTechnical((value) => !value)}
            data-testid="document-calc-strip-technical-toggle"
          >
            {technical ? `▴ ${t('document.calcIssues.details')}` : `▾ ${t('document.calcIssues.details')}`}
          </Button>
          <Collapse in={technical} id={technicalId}>
            <Stack gap="xs">
              {technical &&
                months.map((month) => (
                  <CalculationIssuesPanel
                    key={month.periodKey}
                    userId={userId}
                    documentId={documentId}
                    periodKey={month.periodKey}
                    rows={month.rows}
                    tables={tables}
                    onOpenCell={onOpenCell}
                    onOpenHse400={onOpenHse400 === undefined ? undefined : () => onOpenHse400(month.periodKey)}
                    onOpenSource={onOpenSource}
                    narrow={narrow}
                  />
                ))}
            </Stack>
          </Collapse>
        </Stack>
      </Collapse>
    </section>
  );
}
