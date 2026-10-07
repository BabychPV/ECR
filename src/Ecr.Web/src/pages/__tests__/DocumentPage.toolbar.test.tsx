import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { testTheme } from '@/test/render';

/**
 * Рядок дій сторінки документа за макетом (UI-14; `docs/design/hybrid/screen-document.js`,
 * `renderActions`): одна головна дія, ≤ 2 другорядні, решта — у «More».
 *
 * ⚠ jsdom не міряє ширин, тож тут перевіряється СТРУКТУРА: (1) які кнопки
 * видимі в стані аркуша; (2) рідкісні й небезпечні дії, імпорт, експорт —
 * пунктами меню «More»; (3) стан експорту й подання не додає рядку дій
 * елементів. Ширини — знімками в звіті.
 */
vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

const importOpened = vi.hoisted(() => ({ count: 0 }));

vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: ({ openRef }: { openRef?: { current: (() => void) | null } }): null => {
    if (openRef !== undefined) {
      openRef.current = () => {
        importOpened.count += 1;
      };
    }

    return null;
  },
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

function mockFetch(
  permissions: string[],
  options: { sheetStates?: Record<string, string>; validation?: unknown; noVisibleSheets?: boolean } = {},
): void {
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

      if (url.includes('/recall')) return jsonResponse({ canRecall: false });

      if (url.includes('/validation')) {
        return options.validation === undefined
          ? jsonResponse({ title: 'Not found', status: 404, errorCode: 'ECR-DOC-0404' }, 404)
          : jsonResponse(options.validation);
      }

      if (url.includes('/tables/status')) return jsonResponse([]);

      // ⚠ Усі аркуші сховані від користувача: API віддає порожній перелік таблиць.
      if (url.includes('/tables') && options.noVisibleSheets === true) return jsonResponse([]);

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
          sheetStates: options.sheetStates ?? { GEN: 'Draft' },
          hasLateEdits: false,
        });
      }

      return jsonResponse(null);
    }),
  );
}

function show(permissions: string[], options: Parameters<typeof mockFetch>[1] = {}): void {
  mockFetch(permissions, options);

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
const More = { name: /document\.moreActions/ };
const Delete = { name: '⟦documents.delete⟧' };
const ChangeKey = { name: '⟦documents.changeKey⟧' };

/** Рядок дій — коли в ньому вже є «Submit» (профіль доїхав). */
async function actionsRow(): Promise<HTMLElement> {
  const row = await screen.findByTestId('document-actions', {}, { timeout: SlowEnvTimeout });
  await within(row).findByRole('button', { name: '⟦document.submit⟧' }, { timeout: SlowEnvTimeout });

  return row;
}

/** Підписи видимих кнопок рядка дій у порядку на екрані. */
function visibleActions(row: HTMLElement): string[] {
  return within(row)
    .getAllByRole('button')
    .map((button) => (button.textContent ?? '').replace(/[▾]/g, '').trim());
}

afterEach(() => {
  vi.unstubAllGlobals();
  importOpened.count = 0;
});

describe('DocumentPage: рядок дій (UI-14)', () => {
  it(
    'чернетка: видимі лише Validate · More · Submit, головна — одна',
    async () => {
      show(AllRights);
      const row = await actionsRow();

      // ⛔ Мутаційний доказ: поверни `ExportButton` з тригером у рядок — тут
      // з'явиться «Export», і рівність почервоніє.
      expect(visibleActions(row)).toEqual(['⟦document.validate⟧', '⟦document.moreActions⟧', '⟦document.submit⟧']);

      // `L1`: одна заповнена (primary) кнопка в рядку.
      const filled = within(row)
        .getAllByRole('button')
        .filter((button) => button.getAttribute('data-variant') === 'filled');
      expect(filled.map((button) => button.textContent)).toEqual(['⟦document.submit⟧']);

      // На поданому аркуші головна — «Approve», і вона теж одна (наступний тест).
    },
    SlowEnvTimeout,
  );

  it(
    'імпорт, експорт (формати), зміна ключа й видалення — пунктами «More»',
    async () => {
      show(AllRights);
      const row = await actionsRow();

      expect(within(row).queryByText('⟦documents.delete⟧')).toBeNull();
      expect(within(row).queryByText('⟦import.pick⟧…')).toBeNull();

      fireEvent.click(within(row).getByRole('button', More));

      expect(await screen.findByRole('menuitem', { name: '⟦import.pick⟧…' })).toBeDefined();
      for (const format of ['Xlsx', 'Csv', 'Json']) {
        expect(await screen.findByRole('menuitem', { name: `⟦document.exportFormat${format}⟧` })).toBeDefined();
      }
      expect(await screen.findByRole('menuitem', ChangeKey)).toBeDefined();
      const remove = await screen.findByRole('menuitem', Delete);

      // Небезпечна дія — червоним пунктом.
      expect(remove.getAttribute('style') ?? '').toContain('statusError');

      // ⛔ Підтвердження лишилося: клік по пункту відкриває діалог, а не видаляє.
      fireEvent.click(remove);
      expect(await screen.findByTestId('confirm-verb')).toBeDefined();
    },
    SlowEnvTimeout,
  );

  it(
    'пункт «Import from Excel…» відкриває вибір файлу змонтованої поза меню панелі',
    async () => {
      show(AllRights);
      const row = await actionsRow();

      fireEvent.click(within(row).getByRole('button', More));
      fireEvent.click(await screen.findByRole('menuitem', { name: '⟦import.pick⟧…' }));

      await waitFor(() => expect(importOpened.count).toBe(1));
    },
    SlowEnvTimeout,
  );

  it(
    'експорт і подання в роботі не додають рядку дій елементів; «Building…» — у стані ліворуч',
    async () => {
      show(AllRights);
      const row = await actionsRow();
      const idle = visibleActions(row);

      fireEvent.click(within(row).getByRole('button', More));
      fireEvent.click(await screen.findByRole('menuitem', { name: '⟦document.exportFormatCsv⟧' }));

      const status = screen.getByTestId('document-status');
      await waitFor(() =>
        expect(status.querySelector('[data-export-state]')?.getAttribute('data-export-state')).toBe('running'),
      );

      fireEvent.click(within(row).getByRole('button', { name: '⟦document.submit⟧' }));
      await waitFor(() =>
        expect(within(row).getByRole('button', { name: /document\.submit/ }).hasAttribute('data-loading')).toBe(
          true,
        ),
      );

      expect(visibleActions(row)).toEqual(idle);
    },
    SlowEnvTimeout,
  );

  it(
    'без рідкісних дій у «More» лише History і Compare versions',
    async () => {
      show([]);
      await actionsRow();

      fireEvent.click(screen.getByRole('button', More));
      const items = (await screen.findAllByRole('menuitem')).map((item) => item.textContent);

      expect(items).toEqual(['⟦workflow.history⟧', '⟦document.compare⟧']);
    },
    SlowEnvTimeout,
  );

  it(
    'усі аркуші сховані — рядка дій, а з ним «More» з History/Compare, немає',
    async () => {
      show(AllRights, { noVisibleSheets: true });

      // ⛔ Спершу — що сторінка намальована й сказала «аркушів немає», інакше «меню немає» було б правдою лише через недомальований екран.
      await screen.findByText('⟦document.noSheets⟧', {}, { timeout: SlowEnvTimeout });

      expect(screen.queryByTestId('document-actions')).toBeNull();
      expect(screen.queryByRole('button', More)).toBeNull();
      expect(screen.queryByRole('menuitem', { name: '⟦workflow.history⟧' })).toBeNull();
      expect(screen.queryByRole('menuitem', { name: '⟦document.compare⟧' })).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'поданий аркуш для погоджувача: Reject · More · Approve',
    async () => {
      show(AllRights, { sheetStates: { GEN: 'Submitted' } });
      const row = await screen.findByTestId('document-actions', {}, { timeout: SlowEnvTimeout });
      await within(row).findByRole('button', { name: '⟦workflow.approve⟧' }, { timeout: SlowEnvTimeout });

      expect(visibleActions(row)).toEqual(['⟦workflow.reject⟧', '⟦document.moreActions⟧', '⟦workflow.approve⟧']);
      expect(
        within(row)
          .getAllByRole('button')
          .filter((button) => button.getAttribute('data-variant') === 'filled')
          .map((button) => button.textContent),
      ).toEqual(['⟦workflow.approve⟧']);

      // Перевірка на поданому — у меню, а не поруч із головною дією.
      fireEvent.click(within(row).getByRole('button', More));
      expect(await screen.findByRole('menuitem', { name: '⟦document.validate⟧' })).toBeDefined();
      // ⛔ Імпорт над поданим аркушем не пропонується (`ФВ-5.20a`).
      expect(screen.queryByRole('menuitem', { name: '⟦import.pick⟧…' })).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'стан збереження словами поруч із діями',
    async () => {
      show(AllRights);
      await actionsRow();

      const state = screen.getByTestId('document-save-state');
      expect(state.getAttribute('role')).toBe('status');
      expect(state.textContent).toBe('⟦document.saveState.allSaved⟧');
    },
    SlowEnvTimeout,
  );
});

describe('DocumentPage: прогрес у шапці (UI-15)', () => {
  it(
    'чип стану активного аркуша; без перевірки — жодного числа зауважень',
    async () => {
      show(AllRights);
      await actionsRow();

      const progress = screen.getByTestId('document-progress');
      expect(within(progress).getByText('⟦status.sheet.Draft⟧')).toBeDefined();
      // ⛔ «Не перевіряли» ≠ «0 зауважень» (`A7-28`).
      expect(screen.queryByTestId('document-issues-link')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'K зауважень — лише кнопка інспектора (UI-25), рядок прогресу другого лічильника не дублює',
    async () => {
      show(AllRights, {
        validation: {
          validated: true,
          messages: [
            { severity: 'Error', message: 'E1', ruleCode: 'R1', tableDefId: 1, rowKey: null, columnCode: null },
            { severity: 'Warning', message: 'W1', ruleCode: 'R2', tableDefId: 1, rowKey: null, columnCode: null },
          ],
        },
      });
      await actionsRow();

      // ⚠ Лічильник інспектора рахує лише ВИДИМІ таблиці (`inspectorModel.ts`);
      // другий — за всіма повідомленнями — розійшовся б із ним на звуженій ролі.
      expect(screen.queryByTestId('document-issues-link')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'Scope: роль бачить один аркуш — прихований аркуш, його стан і лічильники не показуються',
    async () => {
      // ⚠ `/tables` віддає лише видимий аркуш `GEN`; `sheetStates` несе ще й
      // прихований `HID` (до фіксу сервера так і було). Клієнт не рахує
      // нічого по `sheetStates` поза видимими аркушами.
      show(AllRights, { sheetStates: { GEN: 'Draft', HID: 'Rejected' } });
      await actionsRow();

      const head = screen.getByTestId('document-toolbar');
      expect(within(head).queryByText('⟦status.sheet.Rejected⟧')).toBeNull();
      expect(head.textContent ?? '').not.toContain('HID');
    },
    SlowEnvTimeout,
  );
});
