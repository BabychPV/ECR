import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';
import { holdWrites, renderKeepMounted } from '@/test/closedDialogProbe';

/**
 * MS T4: версія з рушієм `Script` на сторінці версій — «Open» веде в редактор скрипта, секції старого конфігуратора
 * не показуються; «New version» шле `engine` ЯВНО (типово `Script`), для копії — рушій джерела.
 */

vi.mock('@/features/methodologies/MethodologyContentPanels', () => ({
  MethodologyConstantsPanel: () => <div data-testid="legacy-panel" />,
  MethodologyOutputsPanel: () => <div data-testid="legacy-panel" />,
  MethodologyRulesPanel: () => <div data-testid="legacy-panel" />,
  MethodologyRequiredInputsPanel: () => <div data-testid="legacy-panel" />,
  MethodologyTestsPanel: () => <div data-testid="legacy-panel" />,
  MethodologyBindingsPanel: () => <div data-testid="legacy-panel" />,
  MethodologyModesForm: () => <div data-testid="legacy-panel" />,
}));

const Base = {
  status: 'Draft',
  isEditable: true,
  effectiveFrom: null,
  numericMode: 'Strict',
  calendarMode: 'Actual',
  traceLevel: 'Off',
  createdByUserId: 1,
  level: 'Configuration',
};
const ScriptVersion = { ...Base, id: 12, versionNumber: '2.0', engine: 'Script' };
const FormulaVersion = { ...Base, id: 10, versionNumber: '1.0', engine: 'Formulas', status: 'Published', isEditable: false };

function read(url: string): unknown {
  if (url.endsWith('/api/v1/me')) {
    return {
      userId: 1,
      userName: 'tester',
      language: 'en',
      permissions: ['Calculation.EditFormula', 'Calculation.View'],
      isSimulation: false,
      denies: [],
      grants: {},
      mustChangePassword: false,
      simulatedForUserId: null,
    };
  }
  if (url.endsWith('/api/v1/methodologies/1/versions')) return [FormulaVersion, ScriptVersion];
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

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyVersionsPage: версія-скрипт', { timeout: 30_000 }, () => {
  it('«Open» для скрипта — посилання на редактор, для формул — кнопка; секцій конфігуратора для скрипта немає', async () => {
    holdWrites({ read });
    show();

    const link = await screen.findByRole('link', { name: '⟦methodologies.openVersion⟧' });
    expect(link.getAttribute('href')).toBe('/admin/methodologies/1/versions/12/script');
    // Одна версія — формули (кнопка), одна — скрипт (посилання).
    expect(screen.getAllByRole('button', { name: '⟦methodologies.openVersion⟧' })).toHaveLength(1);

    // За замовчуванням відкрита чернетка — це скрипт: замість панелей — підказка з переходом у редактор.
    const note = await screen.findByTestId('script-version-note');
    expect(within(note).getByRole('link').getAttribute('href')).toBe('/admin/methodologies/1/versions/12/script');
    expect(screen.queryAllByTestId('legacy-panel')).toHaveLength(0);
    expect(screen.queryByText('⟦methodologies.formulas⟧')).toBeNull();
  });

  it('«New version»: порожня чернетка шле engine «Script» явно; вибір рушія зникає, коли обрано джерело', async () => {
    const server = holdWrites({ read });
    show();

    fireEvent.click(await screen.findByRole('button', { name: '⟦methodologies.newVersion⟧' }));
    const dialog = await screen.findByRole('dialog', { name: '⟦methodologies.newVersionTitle⟧' });
    const choice = within(dialog).getByRole('radiogroup', { name: '⟦methodologyScript.engine⟧' });
    expect(within(choice).getByRole('radio', { name: '⟦methodologyScript.engine.Script⟧' })).toHaveProperty('checked', true);

    fireEvent.change(within(dialog).getByLabelText('⟦methodologies.versionNumber⟧'), { target: { value: '3.0' } });
    fireEvent.click(within(dialog).getByRole('button', { name: '⟦methodologies.createVersion⟧' }));

    await waitFor(() => {
      expect(server.sent).toEqual(['POST /api/v1/methodologies/1/versions']);
    });
    expect(server.bodies[0]).toMatchObject({ versionNumber: '3.0', copyFromVersionId: null, engine: 'Script' });
  });
});
