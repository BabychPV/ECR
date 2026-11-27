import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { clearScrollBeforePeriodSwitch } from '@/features/documents/periodSwitchScroll';
import { testTheme } from '@/test/render';

/**
 * PERIODNAV (живий стенд): клік «Previous period» з прокрученої сторінки (scrollY 120) замінював ВЕСЬ документ скелетом,
 * `scrollHeight` падав до вікна, браузер притискав прокрутку до 0, і утримання бачило вже 0. Тут: (1) утримання
 * викликається з позицією ДО кліку, (2) поки період вантажиться, коробка документа тримає висоту попереднього.
 */

const hold = vi.hoisted(() => vi.fn(() => () => undefined));
vi.mock('@/features/documents/holdScrollPosition', () => ({ holdScrollPosition: hold }));
vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));
vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

const Timeout = 20_000;
const Tall = 1010;

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

let releaseSlow: (() => void) | null = null;

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [], grants: { 'Project:1': 'Manage' }, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions: ['Document.View'], simulatedForUserId: null, userId: 1, userName: 'tester',
        });
      }
      if (url.includes('/api/v1/projects?')) return jsonResponse({ items: [], nextCursor: null, totalCount: 0 });
      if (url.includes('/api/v1/projects/1/periods')) {
        const period = (periodKey: number, sequence: number): unknown => ({
          id: sequence, periodKey, state: 'Open', isCurrent: periodKey === 202002, year: 2020, sequence,
          startsAt: '2020-01-01T00:00:00Z', endsAt: '2020-02-01T00:00:00Z', graceEndsAt: null, reopenedUntil: null,
        });

        return jsonResponse({
          projectId: 1, periodKind: 'Monthly', currentPeriodMode: 'Auto', timeZoneId: 'UTC', policy: {},
          periods: [period(202001, 1), period(202002, 2)],
        });
      }
      if (url.includes('/validation')) return jsonResponse({ documentId: 1, periodKey: 202002, messages: [], validated: false });
      if (url.includes('/tables/status')) return jsonResponse([]);
      if (url.includes('/tables')) {
        // Попередній період (202001) відповідає повільно: саме на ньому сторінка показує скелет.
        if (url.includes('periodKey=202001')) await new Promise<void>((resolve) => { releaseSlow = resolve; });

        return jsonResponse([
          {
            allowsDynamicRows: false, maxDynamicRows: null, sheetCode: 'GEN', sheetDefId: 1,
            sheetNameL10n: { values: { en: 'General' } }, sheetOrdinal: 0, tableCode: 'T0', tableDefId: 1,
            tableInstanceId: 1, tableNameL10n: { values: { en: 'Table 0' } }, tableOrdinal: 0,
          },
        ]);
      }
      if (url.includes('/api/v1/documents/1')) {
        return jsonResponse({
          businessKey: 'DOC-0001', createdAt: '2026-01-01T00:00:00Z', id: 1, nameL10n: { values: {} }, projectId: 1,
          sheetCount: 1, sheetStates: {}, hasLateEdits: false,
        });
      }

      return jsonResponse(null);
    }),
  );
}

beforeEach(() => {
  hold.mockClear();
  clearScrollBeforePeriodSwitch();
  releaseSlow = null;
  Object.defineProperty(HTMLElement.prototype, 'offsetHeight', { configurable: true, get: () => Tall });
  Object.defineProperty(window, 'scrollY', { configurable: true, writable: true, value: 120 });
  mockFetch();
});

afterEach(() => {
  vi.unstubAllGlobals();
  Reflect.deleteProperty(HTMLElement.prototype, 'offsetHeight');
});

describe('DocumentPage: перемикання періоду з прокрученої сторінки', () => {
  it(
    'скелет нового періоду тримає висоту документа, а утримання прокрутки отримує позицію ДО кліку',
    async () => {
      const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      const { container } = render(
        <MantineProvider theme={testTheme}>
          <MemoryRouter initialEntries={['/documents/1?periodKey=202002']}>
            <QueryClientProvider client={client}>
              <Routes>
                <Route path="/documents/:id" element={<DocumentPage />} />
              </Routes>
            </QueryClientProvider>
          </MemoryRouter>
        </MantineProvider>,
      );

      const previous = await screen.findByRole('button', { name: /period.previous/ }, { timeout: Timeout });
      const box = (): HTMLElement | null => container.querySelector('[data-document-box]');
      await waitFor(() => { expect(box()).not.toBeNull(); }, { timeout: Timeout });
      await waitFor(() => { expect(box()?.style.minHeight).toBe(''); }, { timeout: Timeout });
      hold.mockClear();

      // Клік — і одразу «браузер притиснув прокрутку» (скелет нижчий за вікно): рендер нового періоду бачить уже 0.
      fireEvent.click(previous);
      Object.defineProperty(window, 'scrollY', { configurable: true, writable: true, value: 0 });

      await waitFor(() => { expect(box()?.style.minHeight).toBe(`${String(Tall)}px`); }, { timeout: Timeout });

      releaseSlow?.();
      await waitFor(() => { expect(hold).toHaveBeenCalledTimes(1); }, { timeout: Timeout });
      expect(hold).toHaveBeenCalledWith(120);
    },
    120_000,
  );
});
