import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * P1 «прихований аркуш» (друге рев'ю безпеки, група 1): роль зі scope на
 * ОДИН аркуш. Сервер віддає лише видимий аркуш (`GEN`), лічильники `null`;
 * прихований аркуш поданий, тож видалення сервер відхиляє.
 *
 * ⛔ Клієнт не стверджує «чернетка/усе гаразд» з того, що бачить: діалог
 * каже, що перевіряє сервер (усі аркуші), відмова — серверним текстом, а
 * прихований аркуш не з'являється ні в чипі, ні в лічильниках.
 */
vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: (): JSX.Element => <div data-testid="grid-stub" />,
}));

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const ServerRefusal = 'The document cannot be deleted: one of its sheets is not a draft.';

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (init?.method === 'DELETE') {
        return jsonResponse(
          {
            title: 'Forbidden',
            status: 403,
            detail: ServerRefusal,
            errorCode: 'ECR-DOC-0409',
            messageKey: 'err.ECR-DOC-0409.notDraft',
          },
          403,
        );
      }

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions: ['Document.View', 'Document.Delete'], simulatedForUserId: null, userId: 1, userName: 'scoped',
        });
      }

      if (url.includes('/validation')) {
        return jsonResponse({ title: 'Not found', status: 404, errorCode: 'ECR-DOC-0404' }, 404);
      }

      if (url.includes('/tables/status')) return jsonResponse([]);

      if (url.includes('/tables')) {
        return jsonResponse([
          {
            allowsDynamicRows: false, maxDynamicRows: null, sheetCode: 'GEN', sheetDefId: 1,
            sheetNameL10n: { values: { en: 'General' } }, sheetOrdinal: 0, tableCode: 'T1', tableDefId: 1,
            tableInstanceId: 1, tableNameL10n: { values: { en: 'Table 1' } }, tableOrdinal: 0,
          },
        ]);
      }

      if (url.includes('/api/v1/documents/1')) {
        return jsonResponse({
          businessKey: 'DOC-0001', createdAt: '2026-01-01T00:00:00Z', id: 1, nameL10n: { values: {} }, projectId: 1,
          // Сервер (поки A7 не виправлено) може віддати 2, хоча видимий один.
          sheetCount: 2,
          sheetStates: { GEN: 'Draft' },
          errorCount: null,
          warningCount: null,
        });
      }

      return jsonResponse(null);
    }),
  );
}

const SlowEnvTimeout = 400_000;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentPage: роль зі scope на один аркуш (P1 прихований аркуш)', () => {
  it(
    'видалення: діалог не стверджує «чернетка», відмова — текстом сервера; лічильників і чужого аркуша немає',
    async () => {
      mockFetch();
      render(
        <MantineProvider>
          <MemoryRouter initialEntries={['/documents/1?periodKey=202401']}>
            <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
              <Routes>
                <Route path="/documents/:id" element={<DocumentPage />} />
              </Routes>
            </QueryClientProvider>
          </MemoryRouter>
        </MantineProvider>,
      );

      const toolbar = await screen.findByTestId('document-toolbar', {}, { timeout: SlowEnvTimeout });

      // null-лічильники і неперевірений документ: ні «0», ні «зауважень немає».
      expect(within(toolbar).queryByText('⟦document.noIssues⟧')).toBeNull();
      expect(within(toolbar).queryByTestId('document-issues-link')).toBeNull();
      expect(toolbar.textContent).not.toMatch(/Submitted/);

      fireEvent.click(within(toolbar).getByRole('button', { name: '⟦document.moreActions⟧' }));
      fireEvent.click(await screen.findByRole('menuitem', { name: '⟦documents.delete⟧' }, { timeout: SlowEnvTimeout }));

      const dialog = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      // ⛔ Мутаційний доказ: прибери примітку — тут червоне.
      expect(within(dialog).getByText('⟦documents.deleteServerChecks⟧')).toBeDefined();

      fireEvent.click(within(dialog).getByRole('button', { name: '⟦documents.delete⟧' }));

      const alert = await screen.findByRole('alert', {}, { timeout: SlowEnvTimeout });
      expect(alert.textContent).toContain(ServerRefusal);
    },
    SlowEnvTimeout,
  );
});
