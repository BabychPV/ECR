import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { DocumentSummary } from '@/api/types';
import { DocumentQuickLook } from '@/features/documents/DocumentQuickLook';
import { DocumentsBoard } from '@/features/documents/DocumentsBoard';
import { testTheme } from '@/test/render';

/**
 * ✎ B147 (B75): документ, чия версія шаблону за період без подання й затвердження (`workflowMode = None`), у переліку —
 * «без погодження» (`NoApprovalChip`), а не чернетка: картка дошки і заголовок швидкого перегляду.
 *
 * ⚠ Аркуші такого документа сервер віддає `Draft` — тому фікстура має саме `Draft`: тест на «порожніх» станах
 * лишався б зеленим і тоді, коли клієнт малює чернетку.
 */
const noApprovalRow = {
  id: 21,
  projectId: 1,
  businessKey: 'HSE401-000021',
  nameL10n: { values: { en: 'Monthly log' } },
  createdAt: '2026-01-01T00:00:00Z',
  sheetCount: 1,
  sheetStates: { S1: 'Draft' },
  sheets: [{ code: 'S1', nameL10n: { values: { en: 'Log' } }, state: 'Draft' }],
  workflowMode: 'None',
  modifiedAt: '2026-11-01T10:00:00Z',
  modifiedByDisplayName: 'Operator',
  errorCount: 0,
  hasLateEdits: false,
} as unknown as DocumentSummary;

const draftRow = {
  ...noApprovalRow,
  id: 22,
  businessKey: 'LAND-000022',
  workflowMode: 'SubmitApprove',
} as unknown as DocumentSummary;

const Slow = 60_000;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentsBoard: картка документа без погодження (B147)', () => {
  it('картка — у стовпці «без погодження» з чипом, без смужки «чернеток»; документ із погодженням — у Draft', () => {
    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter>
          <DocumentsBoard
            documents={[noApprovalRow, draftRow]}
            documentHref={(id) => `/documents/${String(id)}`}
            projectCode={() => undefined}
            onQuickLook={() => {}}
          />
        </MemoryRouter>
      </MantineProvider>,
    );

    const column = document.querySelector('[data-board-column="NoApproval"]');
    expect(column?.querySelector('h2')?.textContent).toBe('⟦workflow.noApproval⟧');
    const card = column?.querySelector('[data-board-card="HSE401-000021"]');
    expect(card).not.toBeNull();
    expect(card?.querySelector('[data-testid="document-no-approval"]')?.textContent).toContain('⟦workflow.noApproval⟧');
    // ⛔ Стан аркушів `Draft` у такого документа не діє: смужки аркушів і бейджа «Draft» на картці немає.
    expect(card?.querySelector('[data-segment-summary]')).toBeNull();
    expect(card?.querySelector('[data-status-state]')).toBeNull();

    expect(document.querySelector('[data-board-column="Draft"] [data-board-card="LAND-000022"]')).not.toBeNull();
    expect(document.querySelector('[data-board-column="Draft"] [data-board-card="HSE401-000021"]')).toBeNull();
  });
});

describe('DocumentQuickLook: заголовок документа без погодження (B147)', () => {
  function show(row: DocumentSummary): void {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        const json = (body: unknown): Response =>
          new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

        if (url.includes('/tables/status')) return json([]);
        if (url.includes('/workflow/history')) return json([]);
        if (url.includes(`/api/v1/documents/${String(row.id)}`)) return json(row);

        return json(null);
      }),
    );

    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter initialEntries={[`/?panel=${String(row.id)}`]}>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <DocumentQuickLook
              panelId={String(row.id)}
              currentPeriodOf={() => 202611}
              year={2026}
              document={row}
              listReady
              projectCode={() => 'ATR'}
              documentHref={(id) => `/documents/${String(id)}`}
            />
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );
  }

  it(
    'workflowMode=None — у заголовку чип «без погодження», а не бейдж Draft',
    async () => {
      show(noApprovalRow);

      const chip = await screen.findByTestId('document-no-approval', {}, { timeout: Slow });
      expect(chip.textContent).toContain('⟦workflow.noApproval⟧');
      // Чип стоїть на місці бейджа стану документа — поза переліком аркушів шторки.
      expect(chip.closest('[data-quick-look-sheets]')).toBeNull();
    },
    Slow,
  );

  it(
    'контроль: з погодженням — бейдж стану Draft, чипа немає',
    async () => {
      show(draftRow);

      await waitFor(() => expect(document.querySelector('[data-quick-look]')).not.toBeNull(), { timeout: Slow });
      await waitFor(() => expect(document.querySelector('[data-status-state="Draft"]')).not.toBeNull(), { timeout: Slow });
      expect(screen.queryByTestId('document-no-approval')).toBeNull();
    },
    Slow,
  );
});
