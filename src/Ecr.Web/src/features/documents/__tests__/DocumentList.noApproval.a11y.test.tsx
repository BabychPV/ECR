import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import type { DocumentSummary, PeriodCalendarDto } from '@/api/types';
import { DocumentListSummaryStrip } from '@/features/documents/DocumentListSummaryStrip';
import { DocumentsBoard } from '@/features/documents/DocumentsBoard';
import { DocumentYearStrip, DocumentYearStripLegend } from '@/features/documents/DocumentYearStrip';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * ✎ B147 (B75): нові видимі елементи переліку для документа «без погодження» — плитка смуги лічильників (активна),
 * клітинка смуги 12 місяців і пункт легенди, стовпець дошки з чипом — в обох темах без блокуючих порушень axe.
 */
const calendar = {
  projectId: 1,
  periodKind: 'Monthly',
  timeZoneId: 'UTC',
  currentPeriodMode: 'Auto',
  policy: { id: 1, code: 'STD', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 30, yearGraceOffsetDays: 0 },
  periods: [1, 2, 3].map((sequence) => ({
    id: sequence,
    periodKey: 202600 + sequence,
    year: 2026,
    sequence,
    state: 'Open',
    startsAt: '2026-01-01T00:00:00Z',
    endsAt: '2026-12-31T00:00:00Z',
    graceEndsAt: '2026-12-15T00:00:00Z',
    reopenedUntil: null,
    isCurrent: sequence === 2,
  })),
} as unknown as PeriodCalendarDto;

const documents = [
  {
    id: 9,
    projectId: 1,
    businessKey: 'HSE401-000009',
    nameL10n: { values: { en: 'Monthly log' } },
    sheetStates: { S1: 'Draft' },
    workflowMode: 'None',
    modifiedAt: '2026-11-01T10:00:00Z',
    modifiedByDisplayName: 'Operator',
    errorCount: 0,
    hasLateEdits: false,
  },
] as unknown as DocumentSummary[];

function client(): QueryClient {
  const queries = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
  queries.setQueryData(['documents', 'summary', 'year', 2026, null], {
    draft: 1,
    partlySubmitted: 0,
    submitted: 0,
    approved: 0,
    rejected: 0,
    withIssues: 0,
    noApproval: 2,
  });
  return queries;
}

describe('B147 «без погодження» у переліку — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: плитка смуги, клітинка року, легенда, стовпець дошки — без блокуючих порушень`, async () => {
      render(
        <Shell colorScheme={colorScheme} client={client()}>
          <DocumentListSummaryStrip periodKey={null} year={2026} filters={{ state: 'NoApproval', setState: vi.fn() }} />
          <DocumentYearStripLegend />
          <DocumentYearStrip
            documentId={9}
            businessKey="HSE401-000009"
            year={2026}
            periodStates={[
              { periodKey: 202601, state: 'NoApproval' },
              { periodKey: 202602, state: 'NoApproval' },
            ]}
            calendar={calendar}
          />
          <DocumentsBoard
            documents={documents}
            documentHref={(id) => `/documents/${String(id)}`}
            projectCode={() => undefined}
            onQuickLook={() => {}}
          />
        </Shell>,
      );

      const tile = await screen.findByRole('button', { name: /noApproval|No approval|Without approval/i });
      expect(tile.getAttribute('aria-pressed')).toBe('true');
      expect(document.querySelector('a[data-month="202601"]')?.getAttribute('data-cell')).toBe('noApproval');
      expect(document.querySelector('[data-board-column="NoApproval"] [data-testid="document-no-approval"]')).not.toBeNull();

      const violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
