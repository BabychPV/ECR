import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { gateReads, probeThreeWindows } from '@/test/stillOpenProbe';

/**
 * B149: діалоги екрана версій методології, що закрилися (ще в DOM) або ще чекають перечитування, не шлють
 * запит удруге. Зонд на СПРАВЖНІЙ сторінці (власник діалогів), запис тримає тест, перечитування — ворота.
 *
 * - «New version» (група «скидання після успіху») — вікно 2: `await invalidateQueries(versionsOf)`,
 *   `if (create.isPending) return`; вікно 3: номер версії скинуто → `disabled`. ЗАКРИТО, один `POST …/versions`.
 * - «Publish» (та сама група) — вікно 2: `if (publish.isPending) return`; вікно 3: причину й дату скинуто →
 *   `disabled` (ціль `publishing?.versionId ?? 0` уже не досяжна). ЗАКРИТО, один `POST …/versions/10/publish`.
 * - «Remove formula» (група «null-захист в обробнику») — вікно 2: `!remove.isPending`; вікно 3:
 *   `deleteTarget === null` → обробник нічого не шле. ЗАКРИТО, один `DELETE …/formulas/F1`.
 *
 * F13 (база 0c1ec8ad9): «New version» і «Publish», закриті БЕЗ запису, були ВІДКРИТІ — діалоги змонтовані завжди,
 * «Cancel» поля не скидає, кнопка закритого `Modal` слала `POST …/versions` / `POST …/publish`. Захист в обробниках
 * (`!creating ||`, `publishing === null ||`); без нього — червоний відповідний тест «закрито без запису».
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

const Version = {
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

const Formula = {
  id: 1,
  code: 'F1',
  expression: '1 + 1',
  resultType: 'Number',
  evaluationOrder: 1,
  outputUnitId: null,
  argumentsCsv: null,
};

function read(url: string): unknown {
  if (url.endsWith('/api/v1/me')) {
    return {
      userId: 1,
      userName: 'tester',
      language: 'en',
      permissions: ['Calculation.EditFormula', 'Calculation.Publish', 'Calculation.View'],
      isSimulation: false,
      denies: [],
      grants: {},
      mustChangePassword: false,
      simulatedForUserId: null,
    };
  }
  if (url.endsWith('/api/v1/methodologies/1/versions')) return [Version];
  if (url.endsWith('/formulas')) return [Formula];
  // Панелі покриття — поза предметом зонда; порожні відповіді правильної форми.
  if (url.endsWith('/rule-coverage') || url.endsWith('/coverage')) return { combinations: [], outputs: [], waitingBindings: [] };

  return [];
}

function show(): void {
  renderKeepMounted(
    <Routes>
      <Route path="/admin/methodologies/:id/versions" element={<MethodologyVersionsPage />} />
    </Routes>,
    '/admin/methodologies/1/versions',
  );
}

const dialogOpen = (name: string) => (): boolean => screen.queryByRole('dialog', { name }) !== null;

/*
 * ⛔ Правило 386 / F13: зонд плавав під навантаженням. Причина — холодні ліниві чанки сторінки (поле дати діалогу
 * «Publish», панелі публікацій/покриття/правил категорій, довідка синтаксису): перше `import()` під навантаженням
 * з'їдало бюджет `findBy` і межу тесту. Прогрів — до тестів; сторінка далі бере їх тим самим `lazy()`.
 */
beforeAll(async () => {
  await Promise.all([
    import('@/shared/dates/DateInputWithStyles'),
    import('@/features/methodologies/MethodologyPublicationsPanel'),
    import('@/features/methodologies/RuleCoveragePanel'),
    import('@/features/methodologies/CategoryRulePanel'),
    import('@/features/methodologies/FormulaSyntaxHelp'),
    import('@/features/methodologies/MethodologyCoveragePanel'),
  ]);
}, 60_000);

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: MethodologyVersionsPage — один запит на відкриття діалогу', () => {
  it('«New version»: один POST …/versions у всіх трьох вікнах', async () => {
    const server = holdWrites({ read, write: () => ({ body: { ...Version, id: 11, versionNumber: '2.0' } }) });
    const gate = gateReads();
    show();

    fireEvent.click(await screen.findByRole('button', { name: '⟦methodologies.newVersion⟧' }));
    const dialog = await screen.findByRole('dialog', { name: '⟦methodologies.newVersionTitle⟧' });
    fireEvent.change(within(dialog).getByLabelText('⟦methodologies.versionNumber⟧'), { target: { value: '2.0' } });
    const create = within(dialog).getByRole('button', { name: '⟦methodologies.createVersion⟧' });

    await probeThreeWindows({
      server,
      gate,
      button: () => create,
      isOpen: dialogOpen('⟦methodologies.newVersionTitle⟧'),
      expected: ['POST /api/v1/methodologies/1/versions'],
      awaitedRead: '/api/v1/methodologies/1/versions',
    });
  });

  it('«Publish»: один POST …/versions/10/publish у всіх трьох вікнах', async () => {
    const server = holdWrites({
      read,
      write: () => ({
        body: {
          methodologyVersionId: 10,
          previousVersionId: null,
          numeric: { before: 'Strict', after: 'Strict' },
          calendar: { before: 'Actual', after: 'Actual' },
          changes: [],
          warnings: [],
        },
      }),
    });
    const gate = gateReads();
    const user = userEvent.setup();
    show();

    await screen.findByText('F1');
    fireEvent.click(screen.getAllByRole('button', { name: '⟦methodologies.publish⟧' })[0] as HTMLElement);
    const dialog = await screen.findByRole('dialog', { name: '⟦methodologies.publishTitle⟧' });
    fireEvent.change(within(dialog).getByLabelText('⟦methodologies.reason⟧'), { target: { value: 'Новий коефіцієнт' } });
    await user.type(await within(dialog).findByLabelText('⟦methodologies.effectiveFrom⟧', {}, { timeout: 10_000 }), '2026-01-01');
    const publish = within(dialog).getByRole('button', { name: '⟦methodologies.publish⟧' });
    await waitFor(() => {
      expect(publish).toHaveProperty('disabled', false);
    });

    await probeThreeWindows({
      server,
      gate,
      button: () => publish,
      isOpen: dialogOpen('⟦methodologies.publishTitle⟧'),
      expected: ['POST /api/v1/methodologies/1/versions/10/publish'],
      awaitedRead: '/api/v1/methodologies/1/versions',
    });
    expect(server.bodies[0]).toEqual({ changeReason: 'Новий коефіцієнт', effectiveFrom: '2026-01-01' });
  });

  it('«New version»: номер введено, вікно закрито без запису — кнопка закритого вікна нічого не шле', async () => {
    const server = holdWrites({ read });
    show();

    fireEvent.click(await screen.findByRole('button', { name: '⟦methodologies.newVersion⟧' }));
    const dialog = await screen.findByRole('dialog', { name: '⟦methodologies.newVersionTitle⟧' });
    fireEvent.change(within(dialog).getByLabelText('⟦methodologies.versionNumber⟧'), { target: { value: '2.0' } });
    const create = within(dialog).getByRole('button', { name: '⟦methodologies.createVersion⟧' });
    await waitFor(() => {
      expect(create).toHaveProperty('disabled', false);
    });

    fireEvent.keyDown(dialog, { key: 'Escape' });
    await waitFor(() => {
      expect(dialogOpen('⟦methodologies.newVersionTitle⟧')()).toBe(false);
    });
    expect(create.isConnected).toBe(true);
    fireEvent.click(create);
    await settle();
    expect(server.sent).toEqual([]);
  });

  it('«Publish»: причину й дату введено, вікно закрито без запису — кнопка закритого вікна нічого не шле', async () => {
    const server = holdWrites({ read });
    const user = userEvent.setup();
    show();

    await screen.findByText('F1');
    fireEvent.click(screen.getAllByRole('button', { name: '⟦methodologies.publish⟧' })[0] as HTMLElement);
    const dialog = await screen.findByRole('dialog', { name: '⟦methodologies.publishTitle⟧' });
    fireEvent.change(within(dialog).getByLabelText('⟦methodologies.reason⟧'), { target: { value: 'Новий коефіцієнт' } });
    await user.type(await within(dialog).findByLabelText('⟦methodologies.effectiveFrom⟧', {}, { timeout: 10_000 }), '2026-01-01');
    const publish = within(dialog).getByRole('button', { name: '⟦methodologies.publish⟧' });
    await waitFor(() => {
      expect(publish).toHaveProperty('disabled', false);
    });

    fireEvent.keyDown(dialog, { key: 'Escape' });
    await waitFor(() => {
      expect(dialogOpen('⟦methodologies.publishTitle⟧')()).toBe(false);
    });
    expect(publish.isConnected).toBe(true);
    fireEvent.click(publish);
    await settle();
    expect(server.sent).toEqual([]);
  });

  it('«Remove formula»: один DELETE …/formulas/F1 у всіх трьох вікнах', async () => {
    const server = holdWrites({ read, write: () => ({ status: 204 }) });
    const gate = gateReads();
    show();

    fireEvent.click(await screen.findByRole('button', { name: '⟦methodologies.deleteFormula⟧' }));
    const dialog = await screen.findByRole('dialog', { name: '⟦methodologies.deleteFormulaConfirmTitle⟧' });
    const remove = within(dialog).getByRole('button', { name: '⟦methodologies.deleteFormulaConfirmTitle⟧' });

    await probeThreeWindows({
      server,
      gate,
      button: () => remove,
      isOpen: dialogOpen('⟦methodologies.deleteFormulaConfirmTitle⟧'),
      expected: ['DELETE /api/v1/methodologies/1/versions/10/formulas/F1'],
      awaitedRead: '/formulas',
    });
  });
});
