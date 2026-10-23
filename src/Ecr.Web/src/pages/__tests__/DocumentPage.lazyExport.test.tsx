import { useEffect, type JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ExportFormat } from '@/features/export/exportFormat';

/**
 * Кнопка експорту сторінки документа — лінивий чанк, що вантажиться з ПЕРШИМ запуском експорту (`D-132`,
 * борг клієнта: маршрут стояв на 249.5 з 250 КБ).
 *
 * ⚠ Той самий прийом, що `DocumentPage.lazyPanels.test.tsx`: фабрика `vi.mock` виконується, коли модуль
 * імпортують уперше, тож `exportEvaluated` каже, чи потрапила кнопка в статичний граф сторінки.
 *
 * ⛔ Головне — не те, що модуль лінивий, а що перший пункт меню НЕ губиться: до першого запуску кнопки
 * немає, тож формат чекає в черзі, доки вона змонтується й покладе свій запуск у `startRef`. Заглушка
 * записує кожен запуск — рівно один на клік.
 */
const probe = vi.hoisted(() => ({ exportEvaluated: false, started: [] as string[] }));

vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: (): JSX.Element => <div data-testid="grid-stub" />,
}));

vi.mock('@/features/export/ExportButton', () => {
  probe.exportEvaluated = true;

  return {
    ExportButton: ({
      startRef,
    }: {
      readonly startRef?: { current: ((format: ExportFormat) => void) | null };
    }): JSX.Element => {
      // Як справжня кнопка: запуск кладеться в `startRef` ефектом і знімається при розмонтуванні.
      useEffect(() => {
        if (startRef === undefined) return undefined;
        const start = (format: ExportFormat): void => {
          probe.started.push(format);
        };
        startRef.current = start;

        return () => {
          if (startRef.current === start) startRef.current = null;
        };
      });

      return <div data-testid="export-stub" />;
    },
  };
});

import { DocumentPage } from '@/pages/DocumentPage';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function show(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Document.View', 'Document.Export'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/validation')) {
        return jsonResponse({ title: 'Not found', status: 404, detail: 'not validated', errorCode: 'ECR-DOC-0404' }, 404);
      }

      if (url.includes('/tables')) {
        return jsonResponse([
          {
            allowsDynamicRows: false,
            maxDynamicRows: null,
            sheetCode: 'GEN',
            sheetDefId: 1,
            sheetNameL10n: { values: { en: 'General' } },
            sheetOrdinal: 0,
            tableCode: 'T1',
            tableDefId: 1,
            tableInstanceId: 1,
            tableNameL10n: { values: { en: 'Table 1' } },
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
          sheetStates: { GEN: 'Draft' },
        });
      }

      return jsonResponse(null);
    }),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/documents/1?periodKey=202401']}>
        <QueryClientProvider client={client}>
          <Routes>
            <Route path="/documents/:id" element={<DocumentPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Mantine у jsdom іде довго (`D1-12`) — той самий поріг, що в сусідніх тестах. */
const SlowEnvTimeout = 400_000;

async function clickExportItem(format: 'Csv' | 'Json'): Promise<void> {
  fireEvent.click(await screen.findByTestId('document-more', {}, { timeout: SlowEnvTimeout }));
  fireEvent.click(
    await screen.findByRole('menuitem', { name: `⟦document.exportFormat${format}⟧` }, { timeout: SlowEnvTimeout }),
  );
}

describe('DocumentPage: кнопка експорту — лінивий чанк із першим запуском (D-132)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it(
    'до першого експорту модуль не обчислено; перший пункт меню доходить до кнопки рівно один раз, наступні — напряму',
    async () => {
      show();

      // ⛔ Спершу сторінка справді намальована — інакше «не обчислено» було б зелено через порожній екран.
      await screen.findByRole('button', { name: '⟦document.validate⟧' }, { timeout: SlowEnvTimeout });
      expect(probe.exportEvaluated).toBe(false);
      expect(screen.queryByTestId('export-stub')).toBeNull();

      await clickExportItem('Csv');

      // ⛔ Мутаційний доказ: прибери чергу з `useExportStarter` (DocumentActionBar.tsx) — кнопка змонтується,
      // а перший клік загубиться (`started` лишиться порожнім).
      await waitFor(() => expect(probe.started).toEqual(['csv']), { timeout: SlowEnvTimeout });
      expect(probe.exportEvaluated).toBe(true);

      await clickExportItem('Json');
      await waitFor(() => expect(probe.started).toEqual(['csv', 'json']), { timeout: SlowEnvTimeout });
    },
    SlowEnvTimeout,
  );
});
