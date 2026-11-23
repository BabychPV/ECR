import { describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import type { SourceIssue } from '@/features/calculationIssues/api';
import { CalculationIssuesPanel } from '@/features/documents/CalculationIssuesPanel';
import type { UnmatchedRows } from '@/features/documents/useUnmatchedRows';
import { testTheme } from '@/test/render';

/**
 * ✎ B80 N4 (N6 частина B): причини джерела HSE400 у панелі «Проблеми розрахунку».
 *
 * ⚠ Каталог у тестах не завантажено — `t` дає позначений ключ `⟦ключ (параметри)⟧`.
 *
 * Мутація: показати «Відкрити HSE400» без перевірки `sourceDocumentId` — червоне «недоступне джерело».
 */

function source(over: Partial<SourceIssue>): SourceIssue {
  return {
    kind: 'SourceMonthNotApproved',
    monthNumber: 3,
    sourcePeriodKey: 202603,
    sourceDocumentId: 412,
    sourceProjectCode: 'KPO-2026',
    sourceDocumentKey: 'HSE400-2026',
    sourceApprovedAt: null,
    approvedAt: null,
    ...over,
  };
}

const Hidden: Partial<SourceIssue> = {
  sourceDocumentId: null,
  sourceProjectCode: null,
  sourceDocumentKey: null,
  sourceApprovedAt: null,
};

function show(sourceIssues: readonly SourceIssue[] | undefined) {
  const onOpenSource = vi.fn();
  const rows: UnmatchedRows = {
    issues: [],
    hints: [],
    finishedAt: null,
    formulaErrors: [],
    ...(sourceIssues === undefined ? {} : { sourceIssues }),
  };
  render(
    <MantineProvider theme={testTheme}>
      <CalculationIssuesPanel periodKey={202601} rows={rows} tables={[]} onOpenCell={vi.fn()} onOpenSource={onOpenSource} />
    </MantineProvider>,
  );

  return onOpenSource;
}

describe('CalculationIssuesPanel: причини джерела HSE400 (B80 N4)', () => {
  it('не затверджені місяці джерела — текст «не затверджено», бейдж місяців, блокує подання, документ-джерело назвою', () => {
    show([source({}), source({ monthNumber: 4, sourcePeriodKey: 202604 })]);

    expect(screen.getByTestId('document-calc-issues').getAttribute('data-blocking')).toBe('true');
    const cause = screen.getByTestId('document-calc-issue');
    expect(cause.getAttribute('data-kind')).toBe('sourceNotApproved');
    expect(screen.getByTestId('document-calc-issue-title').textContent).toContain(
      'document.calcIssues.source.notApproved.title',
    );
    expect(screen.getByTestId('document-calc-issue-count').textContent).toContain('document.calcIssues.months');
    expect(screen.getByTestId('document-calc-issue-count').textContent).toContain('count=2');
    expect(screen.getByTestId('document-calc-issue-detail').textContent).toBe('KPO-2026 · HSE400-2026');
    expect(screen.queryByTestId('document-calc-issue-source-unavailable')).toBeNull();
    // Дати — лише для «змінено після затвердження».
    expect(screen.queryByTestId('document-calc-issue-source-dates')).toBeNull();
  });

  it('змінено після затвердження — свій текст і дві дати (Timestamp); не блокує подання', () => {
    show([
      source({
        kind: 'SourceChangedAfterApproval',
        sourceApprovedAt: '2026-04-10T09:00:00Z',
        approvedAt: '2026-04-02T08:00:00Z',
      }),
    ]);

    expect(screen.getByTestId('document-calc-issues').getAttribute('data-blocking')).toBe('false');
    expect(screen.getByTestId('document-calc-issue').getAttribute('data-kind')).toBe('sourceChanged');
    expect(screen.getByTestId('document-calc-issue-title').textContent).toContain('document.calcIssues.source.changed.title');
    const dates = screen.getByTestId('document-calc-issue-source-dates');
    expect(dates.textContent).toContain('document.calcIssues.source.sourceApprovedAt');
    expect(dates.textContent).toContain('document.calcIssues.source.approvedAt');
    expect(Array.from(dates.querySelectorAll('time')).map((time) => time.getAttribute('dateTime'))).toEqual([
      '2026-04-10T09:00:00Z',
      '2026-04-02T08:00:00Z',
    ]);
  });

  /**
   * ✎ F5-R (D-570, P-1122-4): зв'язка Any — змінено ДАНІ джерела (`sourceChangedAt`): свій заголовок, дата зміни замість
   * дати затвердження джерела, підказка дії. Мутації: заголовок без гілки `sourceChangedAt` — червоне «changedData»;
   * прибрати рядок підказки — червоне «hint».
   */
  it('змінено дані джерела (Any) — свій текст, дата зміни, підказка дії; не блокує', () => {
    show([
      source({
        kind: 'SourceChangedAfterApproval',
        approvedAt: '2026-04-02T08:00:00Z',
        sourceChangedAt: '2026-04-05T11:00:00Z',
      }),
    ]);

    expect(screen.getByTestId('document-calc-issues').getAttribute('data-blocking')).toBe('false');
    expect(screen.getByTestId('document-calc-issue').getAttribute('data-kind')).toBe('sourceChanged');
    expect(screen.getByTestId('document-calc-issue-title').textContent).toContain(
      'document.calcIssues.source.changedData.title',
    );
    const dates = screen.getByTestId('document-calc-issue-source-dates');
    expect(dates.textContent).toContain('document.calcIssues.source.sourceChangedAt');
    expect(dates.textContent).not.toContain('document.calcIssues.source.sourceApprovedAt');
    expect(Array.from(dates.querySelectorAll('time')).map((time) => time.getAttribute('dateTime'))).toEqual([
      '2026-04-05T11:00:00Z',
      '2026-04-02T08:00:00Z',
    ]);
    expect(screen.getByTestId('document-calc-issue-source-hint').textContent).toContain(
      'document.calcIssues.source.changed.hint',
    );
  });

  it('підказка дії є й для причини ApprovedOnly; для «не затверджено» — ні', () => {
    show([
      source({ kind: 'SourceChangedAfterApproval', sourceApprovedAt: '2026-04-10T09:00:00Z', approvedAt: '2026-04-02T08:00:00Z' }),
      source({ monthNumber: 5, sourcePeriodKey: 202605 }),
    ]);

    const hints = screen.getAllByTestId('document-calc-issue-source-hint');
    expect(hints).toHaveLength(1);
    expect(hints[0]!.closest('[data-kind]')?.getAttribute('data-kind')).toBe('sourceChanged');
  });

  it('невідомий вид — загальний текст, без збою', () => {
    show([source({ kind: 'SomethingNew' })]);

    expect(screen.getByTestId('document-calc-issue-title').textContent).toContain('document.calcIssues.source.other.title');
  });

  it('«Відкрити HSE400» веде на місяць документа-джерела', async () => {
    const onOpenSource = show([source({ monthNumber: 5, sourcePeriodKey: 202605 }), source({})]);

    await userEvent.click(screen.getByTestId('document-calc-issue-open-source'));

    expect(onOpenSource).toHaveBeenCalledWith(412, 202603);
  });

  it('недоступне джерело — примітка без назви, посилання й дати джерела', () => {
    show([source({ ...Hidden, kind: 'SourceChangedAfterApproval', approvedAt: '2026-04-02T08:00:00Z' })]);

    const cause = screen.getByTestId('document-calc-issue');
    expect(within(cause).queryByTestId('document-calc-issue-open-source')).toBeNull();
    expect(within(cause).queryByTestId('document-calc-issue-detail')).toBeNull();
    expect(within(cause).getByTestId('document-calc-issue-source-unavailable').textContent).toContain(
      'document.calcIssues.source.unavailable',
    );
    const dates = within(cause).getByTestId('document-calc-issue-source-dates');
    expect(dates.textContent).not.toContain('document.calcIssues.source.sourceApprovedAt');
    expect(dates.querySelectorAll('time')).toHaveLength(1);
  });

  it('sourceIssues немає — панелі немає', () => {
    show(undefined);

    expect(screen.queryByTestId('document-calc-issues')).toBeNull();
  });
});
