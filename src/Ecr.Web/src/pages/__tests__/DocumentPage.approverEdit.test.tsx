import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { testTheme } from '@/test/render';

/**
 * ✎ борг D-445: поданий аркуш править і імпортує лише погоджувач ПОТОЧНОГО кроку маршруту. Хто це —
 * знає сервер (`GET …/approver-edit`); клієнт за його відповіддю відкриває сітку й «Імпорт…» або лишає лише
 * читання. Доти погоджувач ІНШОГО кроку (право погоджувати є, черга не його) бачив обидва, а сервер відмовляв.
 */

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

const SlowEnvTimeout = 400_000;
const Period = 202401;
const approverEditAsked = vi.hoisted(() => ({ count: 0 }));

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockFetch(canApproverEdit: boolean): void {
  const options = { projectStatus: 'Active', periodState: 'Open', sheetState: 'Submitted' };
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

      // ✎ D-441: ціль подання — як рахує сервер: відкритий/пільговий період і неподаний аркуш, проєкт не архівний.
      if (url.includes('/submission-target')) {
        const open = options.periodState === 'Open' || options.periodState === 'Grace';
        const submittable =
          open && options.projectStatus !== 'Archived' && (options.sheetState === 'Draft' || options.sheetState === 'Rejected');
        return jsonResponse({
          targetPeriodKey: submittable ? Period : null,
          nextPeriodKey: null,
          periods: open ? [{ periodKey: Period, state: options.periodState, sheetStatus: options.sheetState, submittable }] : [],
          monthColumns: [],
        });
      }

      if (url.includes('/recall')) return jsonResponse({ canRecall: false });

      // ✎ борг D-445: рішення сервера «чи погоджувач поточного кроку» — до загального маршруту документа нижче.
      if (url.includes('/approver-edit')) {
        approverEditAsked.count += 1;
        return jsonResponse({ canApproverEdit });
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

function show(canApproverEdit: boolean): void {
  mockFetch(canApproverEdit);

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

/** Підказка клавіш сітки: `edit` — сітка редаговна, `read-only` — лише читання. */
async function gridHint(expected: string): Promise<void> {
  await waitFor(
    () => {
      const node = document.querySelector('[data-grid-key-hint]');
      expect(node).not.toBeNull();
      expect((node as Element).getAttribute('data-grid-key-hint')).toBe(expected);
    },
    { timeout: SlowEnvTimeout },
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  approverEditAsked.count = 0;
});

describe('DocumentPage: правка поданого аркуша — лише погоджувач поточного кроку (борг D-445)', () => {
  it(
    'погоджувач поточного кроку: «Імпорт…» є, сітка редаговна',
    async () => {
      show(true);

      const row = await screen.findByTestId('document-actions', {}, { timeout: SlowEnvTimeout });
      await within(row).findByRole('button', { name: '⟦workflow.approve⟧' }, { timeout: SlowEnvTimeout });

      expect(await screen.findByRole('button', { name: 'import-stub' }, { timeout: SlowEnvTimeout })).toBeTruthy();
      await gridHint('edit');
    },
    SlowEnvTimeout,
  );

  it(
    'погоджувач іншого кроку: право погоджувати є, але «Імпорт…» сховано, сітка лише для читання',
    async () => {
      show(false);

      const row = await screen.findByTestId('document-actions', {}, { timeout: SlowEnvTimeout });
      // ⚠ Кнопка Approve на місці — клієнт бачить право погоджувати; рішення про чергу — лише від сервера.
      await within(row).findByRole('button', { name: '⟦workflow.approve⟧' }, { timeout: SlowEnvTimeout });

      // ⛔ Спершу — що сервер справді спитано: інакше «Імпорту немає» було б правдою лише через недочитану відповідь.
      await waitFor(() => expect(approverEditAsked.count).toBeGreaterThan(0), { timeout: SlowEnvTimeout });
      await gridHint('read-only');

      // Мутація «approverEditable без canApproverEdit» повертає і пункт імпорту, і редаговну сітку.
      expect(screen.queryByRole('button', { name: 'import-stub' })).toBeNull();
    },
    SlowEnvTimeout,
  );
});
