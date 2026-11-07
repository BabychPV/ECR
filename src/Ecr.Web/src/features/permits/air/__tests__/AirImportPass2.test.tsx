import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { EcrApiError } from '@/api/client';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as airApi from '@/features/permits/air/airApi';
import type { ImportIssue } from '../airTypes';
import { AirImportWizard } from '../import/AirImportWizard';
import { analysis, preview, profile, profiles, result, scopeCodes, scopeFields, tree, xlsx } from './airFixtures';

/**
 * Human pass 2 (07.11) on the AIR «Import parameters…» wizard: P2 «Save as profile…» under a taken code / the built-in code,
 * P2 missing NAE are not «file errors», P3 the step pills.
 *
 * Red without the fix (checked by reverting each part in the worktree, nothing committed):
 * - `onSaveProfile` without the `profileSaveConflict` branch (refusal → `setFailure`): «taken code» and «built-in» are red (no
 *   banner, no Overwrite / Choose another code);
 * - `applyGate` back to `!canApply → errors` and `PreviewStep` counting every error: «missing NAE are a warning group» is red;
 * - the step pills back to plain `<li>` text: «a completed step goes back» is red.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/features/permits/air/airApi', () => ({
  listImportProfiles: vi.fn(),
  getImportProfile: vi.fn(),
  saveImportProfile: vi.fn(),
  analyseAirImport: vi.fn(),
  previewAirImport: vi.fn(),
  applyAirImport: vi.fn(),
  getNaeTree: vi.fn(),
  getAirScopeCodes: vi.fn(),
  getAirScopeFields: vi.fn(),
}));
vi.mock('@/features/permits/api/typesApi', () => ({
  getPermitType: vi.fn(),
  openPermitTypeVersion: vi.fn(),
  replacePermitType: vi.fn(),
  publishPermitType: vi.fn(),
}));

const api = vi.mocked(airApi);
const text = (key: string): string => Catalog[key] ?? key;
const fill = (key: string, params: Record<string, string>): string =>
  text(key).replace(/\{(\w+)\}/g, (match, name: string) => params[name] ?? match);

beforeEach(() => {
  api.listImportProfiles.mockResolvedValue(profiles);
  api.getImportProfile.mockImplementation((code) =>
    Promise.resolve(code === 'W15K_HP' ? { ...profile(), code, builtIn: false, rowVersion: 'rv-current' } : profile()),
  );
  api.analyseAirImport.mockResolvedValue(analysis());
  api.previewAirImport.mockResolvedValue(preview());
  api.applyAirImport.mockResolvedValue(result());
  api.getNaeTree.mockResolvedValue(tree());
  api.getAirScopeCodes.mockResolvedValue(scopeCodes());
  api.getAirScopeFields.mockResolvedValue(scopeFields());
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function mount(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <AirImportWizard permitId={5} permitNumber="KZ87VCZ14622157" mayImport mayEditProfile />
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>,
  );
}

const button = (key: string): HTMLButtonElement => screen.getByRole('button', { name: text(key) });

async function pick(label: string, option: RegExp): Promise<void> {
  await userEvent.click(screen.getByRole('textbox', { name: new RegExp(`^${label}`) }));
  await userEvent.click(await screen.findByRole('option', { name: option }));
}

async function toMapping(): Promise<void> {
  mount();
  await screen.findByLabelText(new RegExp(text('permits.air.import.region.label')));
  await pick(text('permits.air.import.region.label'), /^Atyrau/);
  await pick(text('permits.air.import.area.label'), /^Offshore/);
  fireEvent.click(button('wizard.next'));
  await screen.findByRole('heading', { name: text('permits.air.import.step.file') });
  const input = document.querySelector<HTMLInputElement>('input[type="file"]');
  if (input === null) throw new Error('file input');
  await userEvent.upload(input, xlsx());
  fireEvent.click(button('permits.air.import.analyse'));
  await screen.findByTestId('air-header-report');
}

function conflict(messageKey: string, code: string): EcrApiError {
  return new EcrApiError({
    title: 'Registry entry conflict',
    status: 409,
    errorCode: 'ECR-REG-0409',
    correlationId: 'c-409',
    detail: `refused ${code}`,
    extensions2: { messageKey, code },
  });
}

async function saveAs(name: string, code: string): Promise<void> {
  fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.map.saveProfile') }));
  fireEvent.change(screen.getByLabelText(text('permits.air.import.save.name')), { target: { value: name } });
  fireEvent.change(screen.getByLabelText(text('permits.air.import.save.code')), { target: { value: code } });
  fireEvent.click(button('permits.air.import.save.submit'));
}

describe('Air pass2: «Save as profile…» under an existing code', () => {
  it('a taken code says so and offers Overwrite, which re-saves with the profile\'s current token', async () => {
    api.saveImportProfile
      .mockRejectedValueOnce(conflict('err.ECR-REG-0409.profileCodeTaken', 'W15K_HP'))
      .mockResolvedValueOnce({ ...profile(), code: 'W15K_HP', builtIn: false, rowVersion: 'rv-next' });
    await toMapping();
    await saveAs('Mine', 'W15K_HP');

    const banner = await screen.findByTestId('air-profile-conflict');
    expect(within(banner).getByText(text('permits.air.import.save.codeTakenTitle'))).toBeTruthy();
    expect(within(banner).getByText(fill('permits.air.import.save.codeTaken', { code: 'W15K_HP' }))).toBeTruthy();
    // Not the misleading «changed after you opened it», and not a generic failure.
    expect(screen.queryByText(/changed after you opened it/)).toBeNull();
    expect(screen.queryByText('refused W15K_HP')).toBeNull();
    expect(api.saveImportProfile.mock.calls[0]![1].rowVersion).toBeNull();

    fireEvent.click(button('permits.air.import.save.overwrite'));
    await waitFor(() => expect(api.saveImportProfile).toHaveBeenCalledTimes(2));
    expect(api.getImportProfile).toHaveBeenCalledWith('W15K_HP');
    const [code, body] = api.saveImportProfile.mock.calls[1]!;
    expect(code).toBe('W15K_HP');
    expect(body.rowVersion).toBe('rv-current');
    expect(await screen.findByText('Profile W15K_HP saved and selected.')).toBeTruthy();
    expect(screen.queryByLabelText(text('permits.air.import.save.name'))).toBeNull();
    expect(screen.queryByTestId('air-profile-conflict')).toBeNull();
  });

  it('Overwrite of a code saved earlier in this session closes the form again', async () => {
    api.saveImportProfile
      .mockResolvedValueOnce({ ...profile(), code: 'W15K_HP', builtIn: false, rowVersion: 'rv-1' })
      .mockRejectedValueOnce(conflict('err.ECR-REG-0409.profileCodeTaken', 'W15K_HP'))
      .mockResolvedValueOnce({ ...profile(), code: 'W15K_HP', builtIn: false, rowVersion: 'rv-2' });
    await toMapping();
    await saveAs('Mine', 'W15K_HP');
    expect(await screen.findByText('Profile W15K_HP saved and selected.')).toBeTruthy();
    await saveAs('Mine again', 'W15K_HP');
    await screen.findByTestId('air-profile-conflict');
    fireEvent.click(button('permits.air.import.save.overwrite'));
    await waitFor(() => expect(api.saveImportProfile).toHaveBeenCalledTimes(3));
    await waitFor(() => expect(screen.queryByLabelText(text('permits.air.import.save.name'))).toBeNull());
    expect(screen.getByText('Profile W15K_HP saved and selected.')).toBeTruthy();
  });

  it('the built-in code is refused with its own text; only «Choose another code» is offered and it clears the refusal', async () => {
    api.saveImportProfile.mockRejectedValueOnce(conflict('err.ECR-REG-0409.profileBuiltIn', 'ERA_TABLE_33'));
    await toMapping();
    await saveAs('Mine', 'ERA_TABLE_33');

    const banner = await screen.findByTestId('air-profile-conflict');
    expect(within(banner).getByText(text('permits.air.import.save.builtInTitle'))).toBeTruthy();
    expect(within(banner).getByText(fill('permits.air.import.save.builtIn', { code: 'ERA_TABLE_33' }))).toBeTruthy();
    expect(screen.queryByRole('button', { name: text('permits.air.import.save.overwrite') })).toBeNull();

    fireEvent.click(button('permits.air.import.save.chooseAnother'));
    await waitFor(() => expect(screen.queryByTestId('air-profile-conflict')).toBeNull());
    expect(document.activeElement).toBe(screen.getByLabelText(text('permits.air.import.save.code')));
    expect(api.saveImportProfile).toHaveBeenCalledTimes(1);
  });
});

const missingNae = (code: string, row: number): ImportIssue => ({
  severity: 'Error',
  messageKey: 'err.ECR-IMP-0422.regReferenceMissing',
  row,
  column: 'G',
  field: 'NaeCode',
  args: { kind: 'nae', key: code },
});

describe('Air pass2: missing NAE are a warning group, not file errors', () => {
  it('no «fix the file» gate; create all under a node, run the preview again, Apply opens only on the green preview', async () => {
    api.previewAirImport.mockResolvedValueOnce(
      preview({ canApply: false, issues: [missingNae('3544', 40), missingNae('3545', 41)] }),
    );
    await toMapping();
    fireEvent.click(button('permits.air.import.toPreview'));
    await screen.findByText(text('permits.air.import.replace.title'));

    expect(screen.queryByText(text('permits.air.import.gate.errors'))).toBeNull();
    expect(screen.getByText(text('permits.air.import.gate.unresolved'))).toBeTruthy();
    const group = screen.getByTestId('air-missing-group');
    expect(within(group).getByText(text('permits.air.import.missing.warning'))).toBeTruthy();
    const errorsStat = screen.getByText(text('permits.air.import.stats.errors')).closest('.pi-stat');
    expect(errorsStat?.querySelector('b')?.textContent).toBe('0');
    fireEvent.click(screen.getByRole('checkbox', { name: text('permits.air.import.replace.confirm') }));
    expect(button('permits.air.import.apply').disabled).toBe(true);

    await pick(text('permits.air.import.missing.createAllUnder'), /DC05_Island/);
    fireEvent.click(screen.getByRole('button', { name: fill('permits.air.import.missing.createAll', { count: '2' }) }));
    // Decided, but the preview is stale: still no Apply until it is run again.
    expect(button('permits.air.import.apply').disabled).toBe(true);

    api.previewAirImport.mockResolvedValueOnce(preview({ fingerprint: 'fp-2', missing: [], issues: [] }));
    fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.rerun') }));
    await waitFor(() => expect(api.previewAirImport).toHaveBeenCalledTimes(2));
    expect(api.previewAirImport.mock.calls[1]![2].resolutions).toEqual([
      { kind: 'nae', code: '3544', action: 'create', createUnder: '3' },
      { kind: 'nae', code: '3545', action: 'create', createUnder: '3' },
    ]);
    await waitFor(() => expect(button('permits.air.import.apply').disabled).toBe(false));
  });

  it('an unknown substance is still a file error next to missing NAE', async () => {
    api.previewAirImport.mockResolvedValueOnce(
      preview({
        canApply: false,
        issues: [
          missingNae('3544', 40),
          { severity: 'Error', messageKey: 'err.ECR-IMP-0422.regReferenceMissing', row: 50, column: 'K', field: 'SubstanceCode', args: { kind: 'substance', key: '9999' } },
        ],
      }),
    );
    await toMapping();
    fireEvent.click(button('permits.air.import.toPreview'));
    await screen.findByText(text('permits.air.import.replace.title'));
    expect(screen.getByText(text('permits.air.import.gate.errors'))).toBeTruthy();
    const errorsStat = screen.getByText(text('permits.air.import.stats.errors')).closest('.pi-stat');
    expect(errorsStat?.querySelector('b')?.textContent).toBe('1');
  });
});

describe('Air pass2: step pills', () => {
  it('a completed step goes back to it (draft kept); the current and later steps are not buttons', async () => {
    await toMapping();
    const steps = screen.getByRole('list', { name: text('permits.air.import.steps.aria') });
    const current = within(steps).getByText(`3 ${text('permits.air.import.step.mapping')}`);
    expect(current.closest('li')?.getAttribute('aria-current')).toBe('step');
    expect(within(steps).queryByRole('button', { name: `3 ${text('permits.air.import.step.mapping')}` })).toBeNull();
    expect(within(steps).queryByRole('button', { name: `4 ${text('permits.air.import.step.preview')}` })).toBeNull();
    expect(within(steps).getAllByRole('button')).toHaveLength(2);

    fireEvent.click(within(steps).getByRole('button', { name: `1 ${text('permits.air.import.step.scope')}` }));
    expect(await screen.findByRole('heading', { name: text('permits.air.import.step.scope') })).toBeTruthy();
    expect(within(steps).queryAllByRole('button')).toHaveLength(0);
    expect(screen.getByRole<HTMLInputElement>('textbox', { name: new RegExp(`^${text('permits.air.import.region.label')}`) }).value).toMatch(
      /^Atyrau/,
    );
    // Forward still goes through the checks: «Next» → File, the file is kept.
    fireEvent.click(button('wizard.next'));
    await screen.findByRole('heading', { name: text('permits.air.import.step.file') });
    expect(within(steps).getAllByRole('button')).toHaveLength(1);
  });
});
