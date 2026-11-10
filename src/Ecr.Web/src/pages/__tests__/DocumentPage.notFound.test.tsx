import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach, beforeAll } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * D10 (a11y): «Document not found» малювався голим `ErrorAlert` — на сторінці не було жодного
 * заголовка (читалка не мала куди стрибнути, `page-has-heading-one`/навігація заголовками), і
 * стояла марна «Retry». Тепер — службова сторінка як `/404`: заголовок, код і шлях до переліку.
 */
vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: (): JSX.Element => <div data-testid="grid-stub" />,
}));

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

const NotFound = {
  title: 'Document not found',
  status: 404,
  detail: 'x',
  errorCode: 'ECR-DOC-0404',
  correlationId: 'cid-doc-404',
};

const SlowEnvTimeout = 400_000;

/*
 * ⚠ Сторінка сама вантажить `SheetWorkspace` і `cellNavigation` через `import()` (при монтуванні
 * й розмонтуванні). Цей тест закінчується раніше, ніж вони доїдуть, і запізнілий `import()` падав
 * «Vite module runner has been closed». Прогріваємо їх до рендера — тоді `import()` бере з кешу.
 */
beforeAll(async () => {
  await Promise.all([
    import('@/features/grid/SheetWorkspace'),
    import('@/features/grid/cellNavigation'),
    import('@/features/grid/RestoreEditsBanner'),
    import('@/features/documents/DocumentActionBar'),
    import('@/features/documents/DocumentHeaderPanel'),
    import('@/features/documents/inspector/DocumentInspector'),
    import('@/features/documents/sheetCopies/SheetCopyBar'),
    import('@/features/documents/UnmatchedRowsBanner'),
    import('@/features/editAssignments/ExecutorStrip'),
    import('@/features/permits/bindings/PermitRefreshStrip'),
    import('@/features/permits/history/HeaderSplitBanner'),
    import('@/features/projects/allProjects'),
    import('@/features/recalc/ProjectRecalcBannerSlot'),
    import('@/features/tableChat/TableChatEntry'),
    import('@/shared/ui/PeriodPicker'),
  ]);
}, SlowEnvTimeout);

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentPage: документа немає (D10)', () => {
  it(
    'заголовок сторінки, код і кореляція, «Go to Documents» — і без «Retry»',
    async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async (input: RequestInfo | URL) => {
          const url = String(input);

          if (url.includes('/api/v1/me')) {
            return jsonResponse({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester' });
          }

          if (url.includes('/api/v1/documents/404')) return jsonResponse(NotFound, 404);

          return jsonResponse(null);
        }),
      );

      const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

      render(
        <MantineProvider>
          <MemoryRouter initialEntries={['/documents/404?periodKey=202401']}>
            <QueryClientProvider client={client}>
              <Routes>
                <Route path="/documents/:id" element={<DocumentPage />} />
              </Routes>
            </QueryClientProvider>
          </MemoryRouter>
        </MantineProvider>,
      );

      // F13-p229: службова сторінка «документа немає» — рівно один H1 (раніше H2 без H1).
      const heading = await screen.findByRole('heading', { level: 1 }, { timeout: SlowEnvTimeout });

      expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
      expect(heading.textContent ?? '').not.toBe('');
      expect(screen.getByText('ECR-DOC-0404')).not.toBeNull();
      expect(screen.getByText('cid-doc-404')).not.toBeNull();
      expect(screen.getByRole('link', { name: '⟦nav.goToDocuments⟧' }).getAttribute('href')).toBe('/');
      expect(screen.queryByRole('button', { name: '⟦common.retry⟧' })).toBeNull();
    },
    SlowEnvTimeout,
  );
});
