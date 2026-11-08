import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as airApi from '@/features/permits/air/airApi';
import type { ImportProfileSummary } from '../airTypes';
import { AirImportWizard } from '../import/AirImportWizard';
import { analysis, preview, profile, profiles, result, scopeCodes, scopeFields, tree, xlsx } from './airFixtures';

/**
 * UAT pass 3 (08.11) on the AIR «Import parameters…» wizard: existing profiles are edited, re-saved over the same code (with
 * the token they were read with) and deleted after a confirmation; the built-in profile says why it is read-only; a field
 * that matches no table column is reported («will not be saved»).
 *
 * Red without the fix (checked by reverting each part in the worktree, nothing committed): `FileStep` without `ProfileActions`
 * — the edit / delete / built-in tests are red; `MappingStep` without the `unsavedFields` banner — the last test is red.
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
  deleteImportProfile: vi.fn(),
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

const mine: ImportProfileSummary = { code: 'MINE', builtIn: false, nameL10n: { en: 'Mine', ru: 'Мой' }, updatedAt: '2026-11-08T00:00:00Z' };
const mineProfile = () => ({ ...profile(), code: 'MINE', builtIn: false, nameL10n: { en: 'Mine', ru: 'Мой' }, rowVersion: 'rv-mine' });

beforeEach(() => {
  api.listImportProfiles.mockResolvedValue(profiles);
  api.getImportProfile.mockImplementation((code) => Promise.resolve(code === 'MINE' ? mineProfile() : profile()));
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

const button = (key: string, params: Record<string, string> = {}): HTMLButtonElement =>
  screen.getByRole('button', { name: fill(key, params) });

async function pick(label: string, option: RegExp): Promise<void> {
  await userEvent.click(screen.getByRole('textbox', { name: new RegExp(`^${label}`) }));
  await userEvent.click(await screen.findByRole('option', { name: option }));
}

async function toFile(): Promise<void> {
  mount();
  await screen.findByLabelText(new RegExp(text('permits.air.import.region.label')));
  await pick(text('permits.air.import.region.label'), /^Atyrau/);
  await pick(text('permits.air.import.area.label'), /^Offshore/);
  fireEvent.click(button('wizard.next'));
  await screen.findByRole('heading', { name: text('permits.air.import.step.file') });
  const input = document.querySelector<HTMLInputElement>('input[type="file"]');
  if (input === null) throw new Error('file input');
  await userEvent.upload(input, xlsx());
}

describe('Air pass3: profiles on the File step', () => {
  it('the built-in profile says in visible text that it is read-only and offers no edit or delete', async () => {
    await toFile();
    expect((await screen.findByTestId('air-profile-builtin')).textContent).toBe(
      fill('permits.air.import.profile.builtInNote', { code: 'ERA_TABLE_33' }),
    );
    expect(screen.queryByRole('button', { name: text('permits.air.import.profile.delete') })).toBeNull();
    expect(screen.queryByRole('button', { name: text('permits.air.import.profile.edit') })).toBeNull();
  });

  it('Delete profile… asks first, then deletes with the token the profile has now and falls back to the first profile', async () => {
    api.listImportProfiles.mockResolvedValueOnce([mine, ...profiles]).mockResolvedValue(profiles);
    api.deleteImportProfile.mockResolvedValue(undefined);
    await toFile();

    fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.profile.delete') }));
    const confirm = await screen.findByTestId('air-profile-delete-confirm');
    expect(within(confirm).getByText(fill('permits.air.import.profile.deleteTitle', { name: 'Mine', code: 'MINE' }))).toBeTruthy();
    expect(api.deleteImportProfile).not.toHaveBeenCalled();

    fireEvent.click(button('permits.air.import.profile.deleteConfirm'));
    await waitFor(() => expect(api.deleteImportProfile).toHaveBeenCalledWith('MINE', 'rv-mine'));
    // The list is read again without it; the built-in one is chosen and read-only.
    expect(await screen.findByTestId('air-profile-builtin')).toBeTruthy();
  });

  it('Cancel on the confirmation deletes nothing', async () => {
    api.listImportProfiles.mockResolvedValue([mine, ...profiles]);
    await toFile();
    fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.profile.delete') }));
    fireEvent.click(within(await screen.findByTestId('air-profile-actions')).getByRole('button', { name: text('common.cancel') }));
    expect(screen.queryByTestId('air-profile-delete-confirm')).toBeNull();
    expect(api.deleteImportProfile).not.toHaveBeenCalled();
  });

  it('Edit profile opens its mapping in the editor and «Save to profile» re-saves it under the same code with its token', async () => {
    api.listImportProfiles.mockResolvedValue([mine, ...profiles]);
    api.saveImportProfile.mockResolvedValue({ ...mineProfile(), rowVersion: 'rv-next' });
    await toFile();

    fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.profile.edit') }));
    await screen.findByTestId('air-header-report');
    // The editor is open with the profile's own fields, and no re-read is asked for (the file was read with this profile).
    expect((await screen.findAllByLabelText(text('permits.air.import.map.fieldName'))).length).toBe(2);
    expect(screen.queryByText(text('permits.air.import.map.reanalyseFirst'))).toBeNull();

    fireEvent.click(button('permits.air.import.save.resave', { code: 'MINE' }));
    const confirm = await screen.findByTestId('air-profile-resave-confirm');
    expect(within(confirm).getByText(fill('permits.air.import.save.resaveTitle', { code: 'MINE' }))).toBeTruthy();
    expect(api.saveImportProfile).not.toHaveBeenCalled();

    fireEvent.click(within(confirm).getByRole('button', { name: text('permits.air.import.save.overwrite') }));
    await waitFor(() => expect(api.saveImportProfile).toHaveBeenCalledTimes(1));
    const [code, body] = api.saveImportProfile.mock.calls[0]!;
    expect(code).toBe('MINE');
    expect(body.rowVersion).toBe('rv-mine');
    expect(body.nameL10n).toEqual({ en: 'Mine', ru: 'Мой' });
    expect(body.spec.fields?.map((field) => field.name)).toEqual(['NaeCode', 'TonsPerYear']);
  });
});

describe('Air pass3: a field that matches no table column', () => {
  it('the mapping step warns that its values will not be saved', async () => {
    api.analyseAirImport.mockResolvedValue({ ...analysis(), unsavedFields: ['цех'] });
    await toFile();
    fireEvent.click(button('permits.air.import.analyse'));
    const banner = await screen.findByTestId('air-unsaved-fields');
    expect(within(banner).getByText(text('permits.air.import.unsaved.title'))).toBeTruthy();
    expect(within(banner).getByText(fill('permits.air.import.unsaved.text', { fields: 'цех' }))).toBeTruthy();
  });
});
