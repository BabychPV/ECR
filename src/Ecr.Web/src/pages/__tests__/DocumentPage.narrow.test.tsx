import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { testTheme } from '@/test/render';

/**
 * `UI-42`: на вузькому екрані (≤ 640 px) документ — лише для читання з банером
 * (`DIRECTIVE-15-FRONTEND.md`: «Сітка документа на телефоні — читання, не редагування»,
 * `docs-narrow-note`). Дії робочого процесу (подати) лишаються.
 */

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

const SlowEnvTimeout = 400_000;
const Period = 202401;

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockFetch(options: { projectStatus: string; periodState: string; sheetState: string }): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [],
          grants: { 'Project:1': 'Manage' },
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Document.View', 'Document.Import', 'Calculation.Recalculate'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/api/v1/projects?')) {
        return jsonResponse({
          items: [{ id: 1, code: 'P1', status: options.projectStatus, periodCount: 12, periodKind: 'Monthly', currentPeriodId: null, timeZoneId: 'UTC', nameL10n: { values: {} } }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      if (url.includes('/api/v1/projects/1/periods')) {
        return jsonResponse({
          projectId: 1,
          periodKind: 'Monthly',
          currentPeriodMode: 'Auto',
          timeZoneId: 'UTC',
          policy: {},
          periods: [{ id: 5, periodKey: Period, state: options.periodState, isCurrent: false, year: 2024, sequence: 1, startsAt: '2024-01-01T00:00:00Z', endsAt: '2024-02-01T00:00:00Z', graceEndsAt: null, reopenedUntil: null }],
        });
      }

      if (url.includes('/validation')) return jsonResponse({ documentId: 1, periodKey: Period, messages: [], validated: false });
      if (url.includes('/tables/status')) return jsonResponse([]);

      if (url.includes('/tables')) {
        return jsonResponse([
          {
            allowsDynamicRows: false,
            maxDynamicRows: null,
            sheetCode: 'GEN',
            sheetDefId: 1,
            sheetNameL10n: { values: { en: 'General' } },
            sheetOrdinal: 0,
            tableCode: 'T0',
            tableDefId: 1,
            tableInstanceId: 1,
            tableNameL10n: { values: { en: 'Table 0' } },
            tableOrdinal: 0,
          },
        ]);
      }

      if (url.includes('/api/v1/documents/1')) {
        return jsonResponse({
          businessKey: 'DOC-0001',
          createdAt: '2026-01-01T00:00:00Z',
          id: 1,
          nameL10n: { values: {} },
          projectId: 1,
          sheetCount: 1,
          sheetStates: { GEN: options.sheetState },
          hasLateEdits: false,
        });
      }

      return jsonResponse(null);
    }),
  );
}

function show(options: { projectStatus: string; periodState: string; sheetState: string }): void {
  mockFetch(options);

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[`/documents/1?periodKey=${String(Period)}`]}>
        <QueryClientProvider client={client}>
          <Routes>
            <Route path="/documents/:id" element={<DocumentPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const originalMatchMedia = window.matchMedia;

/** Емуляція ширини: `max-width` збігається, `min-width` — ні (екран 500 px). */
function emulateNarrow(narrow: boolean): void {
  window.matchMedia = ((query: string) => ({
    matches: narrow && query.includes('max-width'),
    media: query,
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
}

afterEach(() => {
  vi.unstubAllGlobals();
  window.matchMedia = originalMatchMedia;
});

describe('DocumentPage: вузький екран — лише читання (UI-42)', () => {
  it(
    'чернетка на 500 px: банер, сітка лише для читання, немає Import і Recalculate; Submit лишається',
    async () => {
      emulateNarrow(true);
      show({ projectStatus: 'Active', periodState: 'Open', sheetState: 'Draft' });

      const banner = await screen.findByTestId('docs-narrow-note', {}, { timeout: SlowEnvTimeout });
      expect(banner.textContent).toContain('document.narrow.title');

      const row = screen.getByTestId('document-actions');
      await within(row).findByRole('button', { name: '⟦document.submit⟧' }, { timeout: SlowEnvTimeout });
      expect(within(row).queryByRole('button', { name: /workflow\.recalculate/ })).toBeNull();
      expect(screen.queryByRole('button', { name: 'import-stub' })).toBeNull();

      // Сітка отримала `readOnly` — підказка клавіш у режимі «лише читання».
      const hint = await waitFor(
        () => {
          const node = document.querySelector('[data-grid-key-hint]');
          expect(node).not.toBeNull();

          return node as Element;
        },
        { timeout: SlowEnvTimeout },
      );
      expect(hint.getAttribute('data-grid-key-hint')).toBe('read-only');
    },
    SlowEnvTimeout,
  );

  it(
    'аркуш і так закритий (поданий) — другого банера немає, лише причина блокування',
    async () => {
      emulateNarrow(true);
      show({ projectStatus: 'Active', periodState: 'Open', sheetState: 'Submitted' });

      expect(await screen.findByText('⟦grid.submittedReadOnlyHint⟧', {}, { timeout: SlowEnvTimeout })).toBeTruthy();
      expect(screen.queryByTestId('docs-narrow-note')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'широкий екран (дзеркало): банера немає, Import і Recalculate на місці, сітка редагована',
    async () => {
      emulateNarrow(false);
      show({ projectStatus: 'Active', periodState: 'Open', sheetState: 'Draft' });

      const row = await screen.findByTestId('document-actions', {}, { timeout: SlowEnvTimeout });
      await within(row).findByRole('button', { name: /workflow\.recalculate/ }, { timeout: SlowEnvTimeout });
      expect(screen.getByRole('button', { name: 'import-stub' })).toBeTruthy();
      expect(screen.queryByTestId('docs-narrow-note')).toBeNull();

      const hint = await waitFor(
        () => {
          const node = document.querySelector('[data-grid-key-hint]');
          expect(node).not.toBeNull();

          return node as Element;
        },
        { timeout: SlowEnvTimeout },
      );
      expect(hint.getAttribute('data-grid-key-hint')).toBe('edit');
    },
    SlowEnvTimeout,
  );
});
