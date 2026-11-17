import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { holdReads } from '@/test/heldReads';

/**
 * B149 (група 3, «тіло знімається разом зі станом»): редактор формули методології не шле `PUT` удруге.
 *
 * Зонд (справжня сторінка й діалог, закритий `Modal` лишається в DOM, відповіді віддає тест):
 * - вікно «ще відкритий»: `onSuccess` чекає перечитування формул ВСЕРЕДИНІ мутації — `isPending` тримається,
 *   клік по «Save formula» відсікає `if (save.isPending) return`;
 * - після закриття (`editing = null`) тіло `{editing !== null && …}` знімається в тому ж рендері — кнопка
 *   від'єднана. Ціль запиту (код і версія) — знімок `editing` у мить кліку.
 * Вердикт: ЗАКРИТО; цей тест — регресія.
 *
 * ⚠ Monaco і перевірку виразу мокнуто тим самим прийомом, що `MethodologyVersionsPage.formulaSyntaxError.test.tsx`.
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
  };
});

vi.mock('@/features/expressions/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/expressions/api')>();
  return {
    ...actual,
    expressionMetadata: vi.fn().mockResolvedValue({ functions: [], constants: [] }),
    validateExpression: vi.fn(async () => ({ diagnostics: [], resultType: 'Number', skippedChecks: [] })),
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

const Formulas = '/api/v1/methodologies/1/versions/10/formulas';
const Put = `PUT ${Formulas}/F1`;

afterEach(() => {
  vi.unstubAllGlobals();
  changeHandler = null;
  currentValue = '';
});

describe('B149: редактор формули методології — один PUT на відкриття', () => {
  it('ні під час перечитування (діалог відкритий), ні після закриття другого PUT немає', async () => {
    const server = holdWrites({
      read: (url) => {
        if (url.endsWith('/api/v1/me')) {
          return { userId: 1, userName: 'test', language: 'en', permissions: ['Calculation.EditFormula'], isSimulation: false };
        }
        if (url.endsWith('/api/v1/methodologies/1/versions')) return [DraftVersion];
        // Панелі покриття (правил і виходів) — порожні.
        if (url.includes('coverage')) return { combinations: [], outputs: [], waitingBindings: [] };
        return [];
      },
      write: () => ({ body: { code: 'F1' } }),
    });
    const reads = holdReads((url) => url === Formulas);
    renderKeepMounted(
      <Routes>
        <Route path="/admin/methodologies/:id/versions" element={<MethodologyVersionsPage />} />
      </Routes>,
      '/admin/methodologies/1/versions',
    );

    fireEvent.click(await screen.findByRole('button', { name: '⟦methodologies.addFormula⟧' }));
    const dialog = await screen.findByRole('dialog', { name: '⟦methodologies.formulaTitle⟧' });
    fireEvent.change(within(dialog).getByLabelText(/methodologies\.formulaCode/), { target: { value: 'F1' } });
    await act(async () => {
      await Promise.resolve();
    });
    await waitFor(() => {
      expect(changeHandler).not.toBeNull();
    });
    await act(async () => {
      currentValue = '1 + 2';
      changeHandler?.();
    });

    const save = within(dialog).getByRole('button', { name: '⟦methodologies.saveFormula⟧' });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual([Put]);
    });

    // Сервер прийняв; мутація чекає перечитування формул — діалог ще відкритий.
    reads.arm();
    server.release();
    await waitFor(() => {
      expect(reads.waiting()).toBeGreaterThan(0);
    });
    expect(screen.getByRole('dialog', { name: '⟦methodologies.formulaTitle⟧' })).not.toBeNull();
    expect(save.isConnected).toBe(true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([Put]);

    // Перечитано, діалог закрито: тіло знято разом із кнопкою.
    reads.open();
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: '⟦methodologies.formulaTitle⟧' })).toBeNull();
    });
    expect(save.isConnected).toBe(false);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([Put]);
  });
});
