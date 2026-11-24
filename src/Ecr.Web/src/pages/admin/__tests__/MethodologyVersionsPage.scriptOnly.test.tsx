import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';
import { holdWrites, renderKeepMounted } from '@/test/closedDialogProbe';

/**
 * ✎ 24.11, рішення людини: «має бути тільки наш новий редактор». Сторінка методології відкривається на ПОТОЧНІЙ
 * версії-скрипті (не на першій у переліку формульній V1), формульного редактора й панелей формульного рушія немає,
 * формульних версій в інтерфейсі немає зовсім (лише в базі), «New version» створює лише скрипт.
 *
 * Набір як на live (ECW_C05_01): V1 формули з 2024-01-01, V2 скрипт з 2024-01-02, чернетка формул «test»,
 * плюс чернетка-скрипт 3.0.
 */

vi.mock('@/features/methodologies/MethodologyContentPanels', () => ({
  MethodologyBindingsPanel: () => <div data-testid="bindings-panel" />,
  MethodologyModesForm: ({ version, editable }: { version: { id: number }; editable: boolean }) => (
    <div data-testid="modes-form" data-version={version.id} data-editable={String(editable)} />
  ),
}));
vi.mock('@/features/methodologies/MethodologyPublicationsPanel', () => ({
  MethodologyPublicationsPanel: () => <div data-testid="publications-panel" />,
}));
vi.mock('@/features/methodologies/TraceStepsPanel', () => ({
  MethodologyTraceStepsPanel: () => <div data-testid="trace-panel" />,
}));

const Base = {
  numericMode: 'Strict',
  calendarMode: 'Actual',
  traceLevel: 'Off',
  createdByUserId: 1,
  level: 'Configuration',
};
const V1 = { ...Base, id: 10, versionNumber: 'V1', engine: 'Formulas', status: 'Published', isEditable: false, effectiveFrom: '2024-01-01' };
const TestDraft = { ...Base, id: 58, versionNumber: 'test', engine: 'Formulas', status: 'Draft', isEditable: true, effectiveFrom: null };
const V2 = { ...Base, id: 12, versionNumber: 'V2', engine: 'Script', status: 'Published', isEditable: false, effectiveFrom: '2024-01-02' };
const V3 = { ...Base, id: 13, versionNumber: '3.0', engine: 'Script', status: 'Draft', isEditable: true, effectiveFrom: null };

function me(): unknown {
  return {
    userId: 1,
    userName: 'tester',
    language: 'en',
    permissions: ['Calculation.EditFormula', 'Calculation.View', 'Calculation.Publish'],
    isSimulation: false,
    denies: [],
    grants: {},
    mustChangePassword: false,
    simulatedForUserId: null,
  };
}

function reader(versions: unknown[]): (url: string) => unknown {
  return (url) => {
    if (url.endsWith('/api/v1/me')) return me();
    if (url.endsWith('/api/v1/methodologies/1/versions')) return versions;

    return [];
  };
}

function show(): void {
  renderKeepMounted(
    <Routes>
      <Route path="/admin/methodologies/:id/versions" element={<MethodologyVersionsPage />} />
      <Route path="/admin/methodologies/:id/versions/:vid/script" element={<div data-testid="script-editor-route" />} />
    </Routes>,
    '/admin/methodologies/1/versions',
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyVersionsPage: лише редактор скрипта', { timeout: 30_000 }, () => {
  it('поточна — чинний скрипт V2, а не формульна V1 першою в переліку; формульних панелей немає', async () => {
    holdWrites({ read: reader([V1, TestDraft, V2, V3]) });
    show();

    const card = await screen.findByTestId('current-script-version');
    expect(within(card).getByText('V2')).toBeTruthy();
    expect(within(card).getByRole('link', { name: '⟦methodologyScript.versions.openEditor⟧' }).getAttribute('href')).toBe(
      '/admin/methodologies/1/versions/12/script',
    );


    // Робоча таблиця — лише скрипти, «Open» кожного — посилання в редактор скрипта.
    const table = screen.getByTestId('script-versions');
    const opens = within(table).getAllByRole('link', { name: '⟦methodologies.openVersion⟧' });
    expect(opens.map((link) => link.getAttribute('href'))).toEqual([
      '/admin/methodologies/1/versions/12/script',
      '/admin/methodologies/1/versions/13/script',
    ]);
    expect(within(table).queryByText('V1')).toBeNull();
    expect(within(table).queryByText('test')).toBeNull();

    // Жодного формульного редактора: ні секції формул, ні кнопок формул/констант/правил, ні редактора виразу.
    expect(screen.queryByText('⟦methodologies.formulas⟧')).toBeNull();
    expect(screen.queryByText('⟦methodologies.addFormula⟧')).toBeNull();
    expect(screen.queryByRole('button', { name: '⟦methodologies.openVersion⟧' })).toBeNull();
    expect(document.querySelector('.monaco-editor')).toBeNull();

    // Методологічні панелі на місці.
    expect(screen.getByTestId('bindings-panel')).toBeTruthy();
    expect(screen.getByTestId('publications-panel')).toBeTruthy();
    expect(screen.getByTestId('trace-panel')).toBeTruthy();
  });

  it('формульних версій в інтерфейсі немає зовсім: ні таблиці історії, ні рядка, ні дії над ними', async () => {
    holdWrites({ read: reader([V1, TestDraft, V2, V3]) });
    show();

    await screen.findByTestId('current-script-version');
    expect(screen.queryByTestId('formula-history')).toBeNull();
    expect(screen.queryByText('V1')).toBeNull();
    expect(screen.queryByText('test')).toBeNull();
    // Режими — чернетки-скрипта 3.0 (її можна правити), а не опублікованої V2.
    const modes = await screen.findByTestId('modes-form');
    expect(modes.getAttribute('data-version')).toBe('13');
    expect(modes.getAttribute('data-editable')).toBe('true');
  });

  it('лише формульні версії (до вікна перенесення): пояснення «переноситься», без редактора й таблиці', async () => {
    holdWrites({ read: reader([V1]) });
    show();

    expect(await screen.findByTestId('methodology-converting')).toBeTruthy();
    expect(screen.queryByTestId('current-script-version')).toBeNull();
    expect(screen.queryByTestId('script-versions')).toBeNull();
    expect(screen.queryByTestId('modes-form')).toBeNull();
    expect(screen.queryByText('V1')).toBeNull();
    expect(screen.queryByRole('link', { name: '⟦methodologyScript.versions.openEditor⟧' })).toBeNull();
  });

  it('без жодної версії: «версій немає», а не «переноситься»', async () => {
    holdWrites({ read: reader([]) });
    show();

    expect(await screen.findByTestId('no-versions')).toBeTruthy();
    expect(screen.queryByTestId('methodology-converting')).toBeNull();
  });
  it('«New version»: без вибору рушія, джерело копії — лише скрипти, запит із engine «Script»', async () => {
    const server = holdWrites({ read: reader([V1, TestDraft, V2, V3]) });
    show();

    fireEvent.click(await screen.findByRole('button', { name: '⟦methodologies.newVersion⟧' }));
    const dialog = await screen.findByRole('dialog', { name: '⟦methodologies.newVersionTitle⟧' });
    expect(within(dialog).queryByRole('radiogroup')).toBeNull();
    expect(within(dialog).queryByText('⟦methodologyScript.engine.Formulas⟧')).toBeNull();

    fireEvent.click(within(dialog).getByRole('textbox', { name: '⟦methodologies.copyFrom⟧' }));
    const options = await screen.findAllByRole('option');
    expect(options.map((option) => option.textContent)).toEqual([
      'V2 · ⟦status.version.Published⟧',
      '3.0 · ⟦status.version.Draft⟧',
    ]);

    fireEvent.change(within(dialog).getByLabelText('⟦methodologies.versionNumber⟧'), { target: { value: '4.0' } });
    fireEvent.click(within(dialog).getByRole('button', { name: '⟦methodologies.createVersion⟧' }));

    await waitFor(() => {
      expect(server.sent).toEqual(['POST /api/v1/methodologies/1/versions']);
    });
    expect(server.bodies[0]).toMatchObject({ versionNumber: '4.0', copyFromVersionId: null, engine: 'Script' });
  });
});