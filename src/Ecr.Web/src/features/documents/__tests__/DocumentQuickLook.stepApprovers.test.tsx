import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentQuickLook } from '@/features/documents/DocumentQuickLook';
import type { DocumentSummary } from '@/api/types';
import { testTheme } from '@/test/render';

/**
 * D-466 (AN-59, варіант C): рядок «Can approve now» у швидкому перегляді.
 * Поля `stepApproverNames`/`stepApproverNamesTruncated` віддає лише картка документа (`GET /documents/{id}`), а не перелік,
 * тож шторка дочитує картку навіть тоді, коли рядок документа вже є на сторінці.
 */
const Period = 202609;
const SlowEnvTimeout = 20_000;
const TestTimeout = 60_000;

const listRow = {
  id: 11,
  businessKey: 'DOC-000011',
  nameL10n: { values: { en: 'Atyrau refinery' } },
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: { GEN: 'Submitted' },
  sheets: [{ code: 'GEN', nameL10n: { values: { en: 'General info' } }, state: 'Submitted' }],
  errorCount: 0,
  warningCount: 0,
  hasLateEdits: false,
} as unknown as DocumentSummary;

function mockCard(extra: Record<string, unknown>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/tables/status')) return json([]);
      if (url.includes('/workflow/history')) return json([]);
      if (url.includes('/api/v1/documents/11')) return json({ ...listRow, ...extra });

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[`/?periodKey=${String(Period)}&panel=11`]}>
        <QueryClientProvider client={client}>
          <DocumentQuickLook panelId="11" periodKey={Period} document={listRow} listReady projectCode={() => 'ATR'} documentHref={(id) => `/documents/${String(id)}`} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

async function waitForSheets(): Promise<void> {
  await waitFor(() => expect(document.querySelector('[data-quick-look-sheets]')).not.toBeNull(), { timeout: SlowEnvTimeout });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentQuickLook: хто може діяти на поточному кроці (D-466)', () => {
  it(
    'null: рядка немає взагалі (нічого не подано / звужений читач)',
    async () => {
      mockCard({ stepApproverNames: null, stepApproverNamesTruncated: false });
      show();
      await waitForSheets();
      // Картка вже дочитана (виклик є), а рядка однаково немає.
      await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('/api/v1/documents/11'))).toBe(true), {
        timeout: SlowEnvTimeout,
      });

      expect(screen.queryByText(/quickLookStepApprovers/)).toBeNull();
      expect(document.querySelector('[data-quick-look-step-approvers]')).toBeNull();
      expect(document.querySelector('[data-quick-look-step-approvers-none]')).toBeNull();
    },
    TestTimeout,
  );

  it(
    'список: імена в тексті під заголовком, без «and others»',
    async () => {
      mockCard({ stepApproverNames: ['Aigerim Sadykova', 'Marat Zhakupov'], stepApproverNamesTruncated: false });
      show();

      const names = await waitFor(
        () => {
          const found = document.querySelector('[data-quick-look-step-approvers]');
          if (found === null) throw new Error('рядка ще немає');
          return found;
        },
        { timeout: SlowEnvTimeout },
      );

      expect(names.textContent).toBe('Aigerim Sadykova, Marat Zhakupov');
      expect(screen.getByText('⟦documents.quickLookStepApprovers⟧')).toBeTruthy();
    },
    TestTimeout,
  );

  it(
    'обрізано: після імен стоїть «and others»',
    async () => {
      mockCard({ stepApproverNames: ['Aigerim Sadykova'], stepApproverNamesTruncated: true });
      show();

      const names = await waitFor(
        () => {
          const found = document.querySelector('[data-quick-look-step-approvers]');
          if (found === null) throw new Error('рядка ще немає');
          return found;
        },
        { timeout: SlowEnvTimeout },
      );

      expect(names.textContent).toBe('Aigerim Sadykova ⟦documents.quickLookStepApproversMore⟧');
    },
    TestTimeout,
  );

  it(
    '[]: подано, але діяти не може ніхто - текстове застереження, без списку імен',
    async () => {
      mockCard({ stepApproverNames: [], stepApproverNamesTruncated: false });
      show();

      const warning = await waitFor(
        () => {
          const found = document.querySelector('[data-quick-look-step-approvers-none]');
          if (found === null) throw new Error('застереження ще немає');
          return found;
        },
        { timeout: SlowEnvTimeout },
      );

      expect(warning.textContent).toContain('⟦documents.quickLookStepApproversNone⟧');
      // Mantine `Alert` має роль `alert`: скрінрідер оголошує застереження, воно не лише кольорове.
      expect(warning.getAttribute('role')).toBe('alert');
      expect(document.querySelector('[data-quick-look-step-approvers]')).toBeNull();
    },
    TestTimeout,
  );
});
