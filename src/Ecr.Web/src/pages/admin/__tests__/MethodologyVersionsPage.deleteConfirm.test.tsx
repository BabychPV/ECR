import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';
import { loadCatalog } from '@/shared/i18n';

/**
 * Видалення формули чернетки без підтвердження (UX-аудит, `ФВ-9.15`).
 *
 * ⛔ До фіксу кнопка «Remove» у рядку формули викликала `remove.mutate(...)`
 * ПРЯМО з кліку — жодного «ти впевнений?» на дії, яку не можна скасувати
 * (формула щезає з чернетки назавжди). Випадковий клік по сусідній кнопці
 * (рядок формул компактний, «Edit» і «Remove» стоять поруч) губив формулу
 * без жодного шансу передумати — на відміну від перемикання джерела реєстру
 * (`SourceKindSwitch`) і публікації версії (`ReasonModal` у
 * `TemplateVersionPage`), обидва з яких вимагають підтвердження.
 *
 * Мутаційна перевірка: перший тест ловить СТАРУ поведінку (клик по «Remove»
 * одразу шле `DELETE`) — до фіксу він падав, бо запиту на видалення не було
 * взагалі жодного разу до підтвердження; після фіксу — зелений, бо клік по
 * рядковій кнопці лише відкриває модалку, а `DELETE` іде тільки після кліку
 * на «Remove formula» в ній.
 */

vi.mock('@/features/methodologies/MethodologyContentPanels', () => ({
  MethodologyConstantsPanel: () => null,
  MethodologyOutputsPanel: () => null,
  MethodologyRulesPanel: () => null,
  MethodologyTestsPanel: () => null,
  MethodologyBindingsPanel: () => null,
  MethodologyModesForm: () => null,
}));

const VersionFixture = {
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

const FormulaFixture = {
  id: 1,
  code: 'F1',
  expression: '1 + 1',
  resultType: 'Number',
  evaluationOrder: 1,
  outputUnitId: null,
  argumentsCsv: null,
};

/**
 * Фейковий `fetch`, що ЖИВЕ — список формул справді меншає після `DELETE`,
 * інакше тест довів би лише «запит пішов», а не «формула справді зникла з
 * екрана лише після підтвердження».
 */
function mockApi(): { deleteCalls: string[] } {
  let formulas = [FormulaFixture];
  const deleteCalls: string[] = [];

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
        return json({
          languageCode: 'en',
          revision: 1,
          strings: {
            'methodologies.versionsTitle': 'Versions and formulas',
            'methodologies.version': 'Version',
            'methodologies.status': 'Status',
            'methodologies.modes': 'Modes',
            'methodologies.effectiveFrom': 'Effective from',
            'methodologies.openVersion': 'Open',
            'methodologies.noVersions': 'This methodology has no versions',
            'methodologies.noVersionsHint': 'A version is what actually calculates.',
            'methodologies.formulas': 'Formulas',
            'methodologies.formulaCode': 'Code',
            'methodologies.expression': 'Expression',
            'methodologies.resultType': 'Result type',
            'methodologies.evaluationOrder': 'Order',
            'methodologies.addFormula': 'Add formula',
            'methodologies.editFormula': 'Edit',
            'methodologies.deleteFormula': 'Remove',
            'methodologies.deleteFormulaConfirmTitle': 'Remove formula',
            'methodologies.deleteFormulaConfirmText':
              'Remove formula {code} from this draft? This cannot be undone.',
            'methodologies.noFormulas': 'This version has no formulas',
            'methodologies.noFormulasHint': 'Add one.',
            'methodologies.formulaDeleted': 'The formula has been removed.',
            'methodologies.readOnly': 'Read only',
            'methodologies.readOnlyHint': 'This version is published.',
            'common.cancel': 'Cancel',
          },
        });
      }

      // ⚠ `endsWith`, не `includes`: `/api/v1/methodologies/...` містить
      // підрядок `/api/v1/me` (перші літери `me`thodologies!) — `includes`
      // тут ловив запит версій методології замість `/me` і віддавав профіль
      // користувача там, де сторінка чекала масив версій.
      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'test',
          language: 'en',
          permissions: ['Calculation.EditFormula'],
          isSimulation: false,
        });
      }

      if (url.includes('/api/v1/units')) {
        return json([]);
      }

      if (/\/methodologies\/1\/versions\/10\/formulas\/[^/?]+$/.test(url) && method === 'DELETE') {
        const code = decodeURIComponent(url.split('/').pop() ?? '');
        deleteCalls.push(code);
        formulas = formulas.filter((formula) => formula.code !== code);
        return new Response(null, { status: 204 });
      }

      if (url.endsWith('/api/v1/methodologies/1/versions/10/formulas')) {
        return json(formulas);
      }

      if (url.endsWith('/api/v1/methodologies/1/versions')) {
        return json([VersionFixture]);
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );

  return { deleteCalls };
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
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

describe('MethodologyVersionsPage: видалення формули питає підтвердження', () => {
  it(
    'клік по «Remove» у рядку НЕ шле DELETE і НЕ прибирає формулу — лише відкриває модалку',
    async () => {
      const { deleteCalls } = mockApi();
      await loadCatalog('en', 'public');
      await loadCatalog('en', 'private');
      const user = userEvent.setup();
      show();

      await screen.findByText('F1');

      await user.click(screen.getByRole('button', { name: 'Remove' }));

      // ⛔ Головне твердження РЕГРЕСІЇ: до фіксу цей самий клік одразу слав
      // DELETE. Модалка мусить з'явитися, а запит — НІ.
      await screen.findByRole('dialog', { name: 'Remove formula' });
      expect(deleteCalls).toHaveLength(0);
      expect(screen.getByText('F1')).toBeDefined();

      // Скасування — формула лишається, запиту й досі не було.
      await user.click(screen.getByRole('button', { name: 'Cancel' }));
      await waitFor(() => {
        expect(screen.queryByRole('dialog')).toBeNull();
      });
      expect(deleteCalls).toHaveLength(0);
      expect(screen.getByText('F1')).toBeDefined();
    },
    30_000,
  );

  it(
    'підтвердження в модалці ШЛЕ DELETE і прибирає формулу зі списку',
    async () => {
      const { deleteCalls } = mockApi();
      await loadCatalog('en', 'public');
      await loadCatalog('en', 'private');
      const user = userEvent.setup();
      show();

      await screen.findByText('F1');

      await user.click(screen.getByRole('button', { name: 'Remove' }));
      await screen.findByRole('dialog', { name: 'Remove formula' });

      // Підтверджуємо саме кнопкою модалки («Remove formula»), не рядковою
      // («Remove») — обидві видимі одночасно, доки модалка відкрита.
      await user.click(screen.getByRole('button', { name: 'Remove formula' }));

      await waitFor(() => {
        expect(deleteCalls).toEqual(['F1']);
      });

      await waitFor(() => {
        expect(screen.queryByText('F1')).toBeNull();
      });

      await waitFor(() => {
        expect(screen.queryByRole('dialog')).toBeNull();
      });
    },
    30_000,
  );
});
