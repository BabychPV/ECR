import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * Публікація версії з екрана конфігуратора (аудит, Критична: «на
 * `MethodologyVersionsPage` немає жодного елемента керування публікацією»).
 *
 * ⛔ До фіксу цей екран — той самий, на якому доводять версію до готовності
 * (формули, константи, виходи, правила, обов'язкові колонки, золотий набір,
 * прив'язки) — не мав кнопки «Publish» НІДЕ: ні в рядку таблиці версій, ні в
 * розгорнутій панелі. Єдиний робочий шлях був на іншому екрані
 * (`MethodologiesPage.tsx`, перелік методологій), про який людина, що щойно
 * заповнила всі панелі версії, знати не зобов'язана.
 *
 * Мутаційна перевірка: перший тест ловить СТАРУ поведінку (кнопки «Publish»
 * немає взагалі на цьому екрані) — до фіксу він падав, бо `findByRole` не
 * знаходив кнопку в жодному з двох місць. Другий тест — гейт за правом і
 * статусом: кнопка не з'являється без `Calculation.Publish` і не з'являється
 * для вже опублікованої версії. Третій — наскрізний: заповнення причини й
 * дати та клік «Publish» справді шле `POST …/publish` з тим тілом, якого
 * чекає сервер (`PublishMethodologyHandler`), і показує «зелений» diff.
 */

vi.mock('@/features/methodologies/MethodologyContentPanels', () => ({
  MethodologyConstantsPanel: () => null,
  MethodologyOutputsPanel: () => null,
  MethodologyRulesPanel: () => null,
  MethodologyRequiredInputsPanel: () => null,
  MethodologyTestsPanel: () => null,
  MethodologyBindingsPanel: () => null,
  MethodologyModesForm: () => null,
}));

const DraftVersion: {
  id: number;
  versionNumber: string;
  status: string;
  isEditable: boolean;
  effectiveFrom: string | null;
  numericMode: string;
  calendarMode: string;
  traceLevel: string;
  createdByUserId: number;
  level: string;
} = {
  id: 10,
  versionNumber: '1.0',
  status: 'Draft',
  isEditable: true,
  effectiveFrom: null,
  numericMode: 'Strict',
  calendarMode: 'Actual',
  traceLevel: 'Off',
  createdByUserId: 1,
  level: 'Configuration',
};

const PublishedVersion = {
  ...DraftVersion,
  id: 11,
  versionNumber: '0.9',
  status: 'Published',
  isEditable: false,
  effectiveFrom: '2025-01-01',
};

const Strings = {
  'methodologies.versionsTitle': 'Versions and formulas',
  'methodologies.version': 'Version',
  'methodologies.status': 'Status',
  'methodologies.modes': 'Modes',
  'methodologies.effectiveFrom': 'Effective from',
  'methodologies.effectiveFromHint': 'Periods from this date on are calculated by this version.',
  'methodologies.openVersion': 'Open',
  'methodologies.noVersions': 'This methodology has no versions',
  'methodologies.noVersionsHint': 'A version is what actually calculates.',
  'methodologies.formulas': 'Formulas',
  'methodologies.noFormulas': 'This version has no formulas',
  'methodologies.noFormulasHint': 'Add one.',
  'methodologies.readOnly': 'Read only',
  'methodologies.readOnlyHint': 'This version is published.',
  'methodologies.publish': 'Publish',
  'methodologies.publishTitle': 'Publish methodology version',
  'methodologies.reason': 'Reason for the change',
  'methodologies.reasonHint': 'Recorded in the change log.',
  'methodologies.published': 'The version has been published.',
  'methodologies.diffTitle': 'What the publication changed in the numbers',
  'methodologies.diffNumeric': 'Arithmetic',
  'methodologies.diffCalendar': 'Calendar convention',
  'methodologies.diffChanges': 'Differences on the golden set',
  'methodologies.diffNone': 'No number changed.',
  'common.cancel': 'Cancel',
};

/** Фейковий `fetch` з керованим переліком версій і правом виклику. */
function mockApi(options: {
  versions: (typeof DraftVersion)[];
  permissions: string[];
}): { publishCalls: { url: string; body: unknown }[] } {
  const publishCalls: { url: string; body: unknown }[] = [];
  let versions = options.versions;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }

      // ⚠ `endsWith`, не `includes`: `/api/v1/methodologies/...` містить
      // підрядок `/api/v1/me` (перші літери «me»thodologies!).
      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'test',
          language: 'en',
          permissions: options.permissions,
          isSimulation: false,
        });
      }

      if (url.includes('/api/v1/units')) {
        return json([]);
      }

      if (/\/methodologies\/1\/versions\/\d+\/publish$/.test(url) && method === 'POST') {
        const body: unknown = JSON.parse(String(init?.body ?? '{}'));
        publishCalls.push({ url, body });

        const versionId = Number(url.match(/versions\/(\d+)\/publish$/)?.[1]);
        versions = versions.map((version) =>
          version.id === versionId
            ? {
                ...version,
                status: 'Published',
                isEditable: false,
                effectiveFrom: (body as { effectiveFrom: string }).effectiveFrom,
              }
            : version,
        );

        return json({
          methodologyVersionId: versionId,
          previousVersionId: null,
          numeric: { before: 'Strict', after: 'Strict' },
          calendar: { before: 'Actual', after: 'Actual' },
          changes: [],
          warnings: [],
        });
      }

      if (/\/methodologies\/1\/versions\/\d+\/formulas$/.test(url)) {
        return json([]);
      }

      if (url.endsWith('/api/v1/methodologies/1/versions')) {
        return json(versions);
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );

  return { publishCalls };
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/methodologies/1/versions']}>
          <Routes>
            <Route path="/admin/methodologies/:id/versions" element={<MethodologyVersionsPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyVersionsPage: публікація версії', () => {
  it(
    'з правом Calculation.Publish кнопка «Publish» з\'являється для чернетки',
    async () => {
      mockApi({ versions: [DraftVersion], permissions: ['Calculation.Publish'] });
      await loadCatalog('en', 'public');
      await loadCatalog('en', 'private');
      show();

      await screen.findByText('1.0');

      // ⛔ ГОЛОВНЕ твердження регресії: до фіксу цей запит не знаходив
      // жодної кнопки «Publish» на цьому екрані взагалі.
      const publishButtons = await screen.findAllByRole('button', { name: 'Publish' });
      expect(publishButtons.length).toBeGreaterThan(0);
    },
    30_000,
  );

  it(
    'без права Calculation.Publish кнопки «Publish» немає',
    async () => {
      mockApi({ versions: [DraftVersion], permissions: ['Calculation.EditFormula'] });
      await loadCatalog('en', 'public');
      await loadCatalog('en', 'private');
      show();

      await screen.findByText('1.0');
      expect(screen.queryByRole('button', { name: 'Publish' })).toBeNull();
    },
    30_000,
  );

  it(
    'для вже опублікованої версії кнопки «Publish» немає навіть із правом',
    async () => {
      mockApi({ versions: [PublishedVersion], permissions: ['Calculation.Publish'] });
      await loadCatalog('en', 'public');
      await loadCatalog('en', 'private');
      show();

      await screen.findByText('0.9');
      expect(screen.queryByRole('button', { name: 'Publish' })).toBeNull();
    },
    30_000,
  );

  it(
    'заповнення причини й дати та клік «Publish» у діалозі шле POST .../publish з тілом ' +
      'changeReason+effectiveFrom і показує diff',
    async () => {
      const { publishCalls } = mockApi({
        versions: [DraftVersion],
        permissions: ['Calculation.Publish'],
      });
      await loadCatalog('en', 'public');
      await loadCatalog('en', 'private');
      const user = userEvent.setup();
      show();

      await screen.findByText('1.0');

      const [publishButton] = await screen.findAllByRole('button', { name: 'Publish' });
      await user.click(publishButton!);

      const dialog = await screen.findByRole('dialog', { name: 'Publish methodology version' });

      // ⛔ Порожня форма НЕ шле запит: обидва поля обов'язкові на сервері
      // (`ECR-CALC-0422`), і кнопка підтвердження вимкнена, доки вони порожні.
      expect(publishCalls).toHaveLength(0);

      const reason = within(dialog).getByLabelText('Reason for the change');
      await user.type(reason, 'Оновлено коефіцієнт викидів на 2026 рік');

      const effectiveFrom = dialog.querySelector('input[type="date"]');
      expect(effectiveFrom).not.toBeNull();
      await user.type(effectiveFrom as HTMLInputElement, '2026-01-01');

      // Кнопка підтвердження — та, що ВСЕРЕДИНІ діалогу: рядкова й
      // заголовкова кнопки з тим самим підписом лишаються поза ним.
      await user.click(within(dialog).getByRole('button', { name: 'Publish' }));

      await waitFor(() => {
        expect(publishCalls).toHaveLength(1);
      });

      expect(publishCalls[0]!.url).toBe('/api/v1/methodologies/1/versions/10/publish');
      expect(publishCalls[0]!.body).toEqual({
        changeReason: 'Оновлено коефіцієнт викидів на 2026 рік',
        effectiveFrom: '2026-01-01',
      });

      // Diff показується ПІСЛЯ публікації — заголовок модалки diff-у видно.
      await screen.findByRole('dialog', { name: 'What the publication changed in the numbers' });

      // Версія перечитана як опублікована: рядкової кнопки «Publish» вже немає.
      await waitFor(() => {
        expect(screen.queryByRole('button', { name: 'Publish' })).toBeNull();
      });
    },
    30_000,
  );
});
