import type { JSX } from 'react';
import { describe as suite, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClientProvider } from '@tanstack/react-query';
import { describe as report, findViolations } from '@/test/a11y';
import { Themes, createScanClient } from '@/test/__tests__/a11yFixtures';
import { mantineProviderProps } from '@/shared/theme/provider';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * PERIODNAV: стан «період поза проєктом» (кнопка «Go to <період>» + вибір періоду) під axe в обох темах.
 */

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: {} });
      if (url.includes('/api/v1/me')) {
        return json({
          denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions: ['Document.View'], simulatedForUserId: null, userId: 1, userName: 'tester',
        });
      }
      if (url.includes('/api/v1/projects?')) return json({ items: [], nextCursor: null, totalCount: 0 });
      if (url.includes('/api/v1/projects/1/periods')) {
        return json({
          projectId: 1, periodKind: 'Monthly', currentPeriodMode: 'Auto', timeZoneId: 'UTC', policy: {},
          periods: [202611, 202612].map((periodKey, id) => ({
            id, periodKey, state: 'Open', isCurrent: false, year: 2026, sequence: id + 11,
            startsAt: '2026-01-01T00:00:00Z', endsAt: '2026-02-01T00:00:00Z', graceEndsAt: null, reopenedUntil: null,
          })),
        });
      }
      if (/\/api\/v1\/documents\/1(\?|$|\/tables)/.test(url) && url.includes('periodKey=')) {
        return json({ title: 'Invalid period request', status: 422, detail: 'x', errorCode: 'ECR-PRD-0422' }, 422);
      }
      if (/\/api\/v1\/documents\/1$/.test(url)) {
        return json({
          businessKey: 'DOC-0001', createdAt: '2026-01-01T00:00:00Z', id: 1, nameL10n: { values: {} }, projectId: 1,
          sheetCount: 0, sheetStates: {}, hasLateEdits: false,
        });
      }

      return json(null);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

suite.each(Themes)('PERIODNAV: відновлення періоду (%s)', (colorScheme) => {
  it('без порушень critical і serious', async () => {
    const { container } = render(
      <MantineProvider {...mantineProviderProps} forceColorScheme={colorScheme}>
        <QueryClientProvider client={createScanClient()}>
          <MemoryRouter initialEntries={['/documents/1?periodKey=202612']}>
            <Routes>
              <Route path="/documents/:id" element={<DocumentPage />} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    await screen.findByTestId('period-recovery-go', undefined, { timeout: 20_000 });
    await screen.findByRole('button', { name: /next/i }, { timeout: 20_000 });

    const violations = await findViolations(container);

    expect(violations, report(violations)).toHaveLength(0);
  }, 60_000);
});
