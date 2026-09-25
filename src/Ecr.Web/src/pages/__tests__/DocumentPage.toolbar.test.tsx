import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { testTheme } from '@/test/render';

/**
 * Рядок дій сторінки документа (знімок людини, 1290 px): «Delete document»
 * переїжджав на другий рядок під поле періоду, окремо від решти кнопок.
 *
 * ⚠ jsdom не міряє ширин, тож тут перевіряється СТРУКТУРА, яка перенос
 * визначає: (1) рідкісні й небезпечні дії живуть у меню «More», а не в
 * рядку кнопок; (2) прямі діти рядка — лише цілі групи, тобто переноситися
 * може лише група, а не одна кнопка; (3) стан експорту й подання не додає
 * рядку елементів. Ширини — знімками 1024/1290/1920 у звіті.
 */
vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

const AllRights = [
  'Document.View',
  'Document.Import',
  'Document.Export',
  'Document.Delete',
  'Document.ChangeKey',
];

function mockFetch(permissions: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = String(init?.method ?? 'GET');

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [],
          grants: { 'Project:1': 'Manage' },
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      // ⚠ Задачі, що «ще йдуть»: подання не відповідає ніколи, експорт стоїть
      // у `Running` — саме в цих станах рядок і переповнювався.
      if (url.endsWith('/submit') && method === 'POST') return new Promise<Response>(() => {});
      if (url.endsWith('/export') && method === 'POST') return jsonResponse({ jobId: 'job-1' }, 202);
      if (url.includes('/jobs/job-1')) {
        return jsonResponse({ jobId: 'job-1', state: 'Running', percent: 40, message: null, error: null });
      }

      if (url.includes('/validation')) {
        return jsonResponse({ title: 'Not found', status: 404, errorCode: 'ECR-DOC-0404' }, 404);
      }

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
          sheetStates: { GEN: 'Draft' },
          hasLateEdits: false,
        });
      }

      return jsonResponse(null);
    }),
  );
}

function show(permissions: string[]): void {
  mockFetch(permissions);

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
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

const SlowEnvTimeout = 400_000;
const More = { name: '⟦document.moreActions⟧' };
const Delete = { name: '⟦documents.delete⟧' };
const ChangeKey = { name: '⟦documents.changeKey⟧' };

/** Рядок щоденних дій — лише коли в ньому вже є «Submit» (профіль доїхав). */
async function actionsRow(): Promise<HTMLElement> {
  const row = await screen.findByTestId('document-actions', {}, { timeout: SlowEnvTimeout });
  await within(row).findByRole('button', { name: '⟦document.submit⟧' }, { timeout: SlowEnvTimeout });
  await within(row).findByRole('button', { name: '⟦document.export⟧' }, { timeout: SlowEnvTimeout });

  return row;
}

/** Що рядок кладе в перенос: прямі діти та ЇХНІ прямі діти. */
function footprint(row: HTMLElement): string[] {
  return [...row.children].flatMap((item) => [
    `${item.tagName}[${item.getAttribute('data-action-group') ?? item.getAttribute('data-testid') ?? ''}]`,
    ...[...item.children].map((child) => child.tagName),
  ]);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentPage: рядок дій', () => {
  it(
    'зміна ключа й видалення — у меню «More», а не в рядку кнопок',
    async () => {
      show(AllRights);
      const row = await actionsRow();

      // ⛔ Мутаційний доказ: поверни `deletion.menuItem` кнопкою в `ActionGroup`
      // — перший рядок почервоніє.
      expect(within(row).queryByText('⟦documents.delete⟧')).toBeNull();
      expect(within(row).queryByText('⟦documents.changeKey⟧')).toBeNull();

      // ⛔ Меню — ПОЗА рядком, що переноситься, і в тому самому зовнішньому
      // рядку: інакше воно само могло б «випасти» на окремий рядок.
      const more = screen.getByRole('button', More);
      expect(row.contains(more)).toBe(false);
      expect(screen.getByTestId('document-toolbar').contains(more)).toBe(true);

      fireEvent.click(more);

      const remove = await screen.findByRole('menuitem', Delete);
      expect(await screen.findByRole('menuitem', ChangeKey)).toBeDefined();

      // Небезпечна дія — червоним пунктом.
      expect(remove.getAttribute('style') ?? '').toContain('statusError');

      // ⛔ Підтвердження лишилося: клік по пункту відкриває діалог, а не видаляє.
      fireEvent.click(remove);
      expect(await screen.findByTestId('confirm-verb')).toBeDefined();
    },
    SlowEnvTimeout,
  );

  it(
    'прямі діти рядка — лише цілі групи, жодної «голої» кнопки',
    async () => {
      show(AllRights);
      const row = await actionsRow();

      // ⛔ Мутаційний доказ: винеси «Validate» з `ActionGroup` прямо в рядок —
      // кнопка стане окремою одиницею переносу, і тест почервоніє.
      for (const item of [...row.children]) {
        const isGroup =
          item.hasAttribute('data-action-group') || item.getAttribute('data-testid') === 'export-unit';
        expect(isGroup, `${item.tagName} ${item.textContent ?? ''}`).toBe(true);
      }
    },
    SlowEnvTimeout,
  );

  it(
    'експорт і подання в роботі не додають рядку елементів',
    async () => {
      show(AllRights);
      const row = await actionsRow();
      const idle = footprint(row);

      fireEvent.click(within(row).getByRole('button', { name: '⟦document.export⟧' }));
      await waitFor(() =>
        expect(
          row.querySelector('[data-export-state]')?.getAttribute('data-export-state'),
        ).toBe('running'),
      );

      fireEvent.click(within(row).getByRole('button', { name: '⟦document.submit⟧' }));
      await waitFor(() =>
        expect(
          within(row).getByRole('button', { name: /document\.submit/ }).hasAttribute('data-loading'),
        ).toBe(true),
      );

      expect(footprint(row)).toEqual(idle);
    },
    SlowEnvTimeout,
  );

  it(
    'без прав на зміну ключа й видалення — меню «More» немає зовсім',
    async () => {
      show(['Document.View', 'Document.Export']);
      await actionsRow();

      expect(screen.queryByRole('button', More)).toBeNull();
    },
    SlowEnvTimeout,
  );
});
