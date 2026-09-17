import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within, fireEvent, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * UI-аудит, lane 5: «Formula with a mismatched-parenthesis syntax error
 * saves with zero error message»
 * (`docs/build/audit-drafts/lane5-formula-syntax-error-saved-silently.md`,
 * severity High).
 *
 * ⛔ Той самий `ExpressionEditor`, той самий сервер, що вже повертає
 * діагностику синтаксису (`Q-303`, робоче на `/admin/expressions`) — але
 * діалог формули методології не мав куди цю діагностику показати і не
 * блокував збереження: `(1 + 2))` зберігався як є з видимим лише
 * непідписаним підкресленням у Monaco.
 *
 * ✎ `Select` тут був заглушений як «той, що зависає під jsdom» (`Q-299`).
 * Причина зависання знайдена й усунена (рекурсія jsdom ↔ nwsapi на станових
 * псевдокласах — коментар у `src/test/setup.ts`), заглушку прибрано: жодне
 * твердження цього файлу `Select` не торкалося.
 *
 * ⚠ Monaco мокнуто тим самим прийомом, що й
 * `ExpressionEditor.staleOnChange.test.tsx`: тестовий гачок
 * `__typeIntoEditor` викликає САМЕ ТОЙ `onDidChangeModelContent`-слухач,
 * що реєструє компонент.
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


let changeHandler: (() => void) | null = null;
let currentValue = '';

vi.mock('@/features/expressions/monaco', () => {
  const model = { getValue: () => currentValue, dispose: () => {} };

  return {
    LightTheme: 'ecr-light',
    DarkTheme: 'ecr-dark',
    prepare: () => 'ecr-methodology',
    applyGrammar: () => {},
    setTheme: () => {},
    showDiagnostics: vi.fn(),
    create: () => ({
      getValue: () => currentValue,
      setValue: (next: string) => {
        currentValue = next;
      },
      getModel: () => model,
      onDidChangeModelContent: (handler: () => void) => {
        changeHandler = handler;
      },
      dispose: () => {},
    }),
    __typeIntoEditor: (text: string) => {
      currentValue = text;
      changeHandler?.();
    },
  };
});

const InvalidExpressionDiagnostic = {
  code: 'ECR-TMPL-0422',
  message: 'Unexpected token ")"',
  messageKey: null,
  messageParams: null,
  position: 7,
  length: 1,
};

vi.mock('@/features/expressions/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/expressions/api')>();
  return {
    ...actual,
    expressionMetadata: vi.fn().mockResolvedValue({ functions: [], constants: [] }),
    validateExpression: vi.fn(async (expression: string) => ({
      diagnostics: expression.includes('))') ? [InvalidExpressionDiagnostic] : [],
      resultType: 'Number',
      skippedChecks: [],
    })),
  };
});

const DraftVersion = {
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
  'methodologies.addFormula': 'Add formula',
  'methodologies.formulaTitle': 'Formula',
  'methodologies.formulaCode': 'Code',
  'methodologies.formulaCodeHint': 'It cannot be renamed: other formulas point at it.',
  'methodologies.expression': 'Expression',
  'methodologies.resultType': 'Result type',
  'methodologies.resultTypeHint': 'What the formula produces.',
  'methodologies.resultNumber': 'Number',
  'methodologies.resultText': 'Text',
  'methodologies.arguments': 'Arguments',
  'methodologies.argumentsHint': 'Comma-separated.',
  'methodologies.outputUnit': 'Output unit',
  'methodologies.outputUnitHint': 'Unit of the result.',
  'methodologies.noUnit': 'No unit',
  'methodologies.saveFormula': 'Save formula',
  'methodologies.noFormulas': 'This version has no formulas',
  'methodologies.noFormulasHint': 'Add one.',
  'methodologies.readOnly': 'Read only',
  'methodologies.readOnlyHint': 'This version is published.',
  'expressions.findings': 'Findings: {count}',
  'common.cancel': 'Cancel',
};

function mockApi(): void {
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

      if (/\/methodologies\/1\/versions\/\d+\/formulas$/.test(url) && method === 'GET') {
        return json([]);
      }

      if (/\/methodologies\/1\/versions\/\d+\/formulas$/.test(url) && method === 'PUT') {
        throw new Error('Save formula не мало б викликатися: кнопка заблокована.');
      }

      if (url.endsWith('/api/v1/methodologies/1/versions')) {
        return json([DraftVersion]);
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );
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
  changeHandler = null;
  currentValue = '';
});

describe('MethodologyVersionsPage: синтаксична помилка виразу більше не зберігається мовчки (lane5)', () => {
  it('невалідний вираз показує знахідки і блокує "Save formula"', async () => {
    mockApi();
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');
    const user = userEvent.setup();
    show();

    await screen.findByText('1.0');

    await user.click(await screen.findByRole('button', { name: 'Add formula' }));

    const dialog = await screen.findByRole('dialog', { name: 'Formula' });

    fireEvent.change(within(dialog).getByLabelText('Code'), {
      target: { value: 'LANE5_F1' },
    });

    // Дати редактору змонтуватися (мокнутий `import('./monaco')`).
    await act(async () => {
      await Promise.resolve();
    });

    // Користувач набирає вираз із зайвою дужкою.
    await act(async () => {
      currentValue = '(1 + 2))';
      changeHandler?.();
    });

    // ⛔ Мутаційний доказ (RED на невиправленому коді): без підключення
    // `onValidated` цей запит не знайшов би жодних знахідок і кнопка
    // лишалася б активною.
    await screen.findByText('Findings: 1');
    expect(screen.getByText('Unexpected token ")"')).toBeDefined();

    const saveButton = within(dialog).getByRole('button', { name: 'Save formula' });
    expect(saveButton).toHaveProperty('disabled', true);
  });

  it('коректний вираз не показує знахідок і дозволяє зберегти', async () => {
    mockApi();
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');
    const user = userEvent.setup();
    show();

    await screen.findByText('1.0');

    await user.click(await screen.findByRole('button', { name: 'Add formula' }));

    const dialog = await screen.findByRole('dialog', { name: 'Formula' });

    fireEvent.change(within(dialog).getByLabelText('Code'), {
      target: { value: 'LANE5_F2' },
    });

    await act(async () => {
      await Promise.resolve();
    });

    await act(async () => {
      currentValue = '1 + 2';
      changeHandler?.();
    });

    await waitFor(() => {
      expect(screen.queryByText(/Findings:/)).toBeNull();
    });

    const saveButton = within(dialog).getByRole('button', { name: 'Save formula' });
    expect(saveButton).toHaveProperty('disabled', false);
  });
});
