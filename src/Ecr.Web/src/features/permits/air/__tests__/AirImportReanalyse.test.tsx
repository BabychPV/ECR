import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as airApi from '@/features/permits/air/airApi';
import { AirImportWizard } from '../import/AirImportWizard';
import { analysis, preview, profile, profiles, result, scopeCodes, scopeFields, tree, xlsx } from './airFixtures';

/**
 * P1 UAT 07.11 (AIR permit, wizard «Import parameters…», step «Mapping»): a field added in the editor had no way to point at a
 * file column, «Re-analyse file» ended in the generic «The request failed» and «Next: preview» stayed disabled — the user was
 * stuck. Here: the column picker, the blocked save with its reason, the real reason of a request that got no answer (the chosen
 * file can no longer be read / the server was not reached) with a way forward, and «Re-analyse» reachable after a save.
 *
 * Mutation proofs (done by hand in the worktree, each restored):
 * - `sendFailureOf` always `null` → «the file became unreadable…» fails (the generic ErrorAlert is shown);
 * - the column `<Select>` removed from `FieldEditor` → «a field added without a column…» fails;
 * - the status-line «Re-analyse» removed → «after a save with unread edits…» fails.
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

const api = vi.mocked(airApi);
const text = (key: string): string => Catalog[key] ?? key;
const button = (key: string): HTMLButtonElement => screen.getByRole('button', { name: text(key) });

beforeEach(() => {
  api.listImportProfiles.mockResolvedValue(profiles);
  api.getImportProfile.mockResolvedValue(profile());
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

/**
 * A chosen file the browser can no longer read (saved in Excel after it was chosen). jsdom cannot change a file on disk, so its
 * slice is made unreadable instead: the `FileReader` probe fails as Chrome's does with `NotReadableError`.
 */
function unreadable(file: File): File {
  Object.defineProperty(file, 'slice', {
    value: () => ({ arrayBuffer: () => Promise.reject(new DOMException('The file was changed', 'NotReadableError')) }),
  });

  return file;
}

async function pick(label: string, option: RegExp): Promise<void> {
  await userEvent.click(screen.getByRole('textbox', { name: new RegExp(`^${label}`) }));
  await userEvent.click(await screen.findByRole('option', { name: option }));
}

async function toMapping(file: File = xlsx()): Promise<void> {
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
  await screen.findByLabelText(new RegExp(text('permits.air.import.region.label')));
  await pick(text('permits.air.import.region.label'), /^Atyrau/);
  await pick(text('permits.air.import.area.label'), /^Offshore/);
  fireEvent.click(button('wizard.next'));
  await screen.findByRole('heading', { name: text('permits.air.import.step.file') });
  const input = document.querySelector<HTMLInputElement>('input[type="file"]');
  if (input === null) throw new Error('file input');
  await userEvent.upload(input, file);
  fireEvent.click(button('permits.air.import.analyse'));
  await screen.findByTestId('air-header-report');
}

async function addField(name: string): Promise<void> {
  fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.map.edit') }));
  fireEvent.change(await screen.findByRole('textbox', { name: text('permits.air.import.map.newName') }), { target: { value: name } });
  fireEvent.click(button('permits.air.import.map.add'));
}

describe('поле без колонки', () => {
  it('a field added without a column: highlighted, save blocked with the reason; a column picked from the file columns', async () => {
    await toMapping();
    await addField('Workshop');

    const picker = await screen.findByRole('textbox', { name: 'File column of field Workshop' });
    expect(picker.getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByTestId('air-field-without-column').textContent).toContain('Field "Workshop" has no file column');
    expect(button('permits.air.import.map.saveProfile').disabled).toBe(true);
    // Re-analysing is allowed (the server reports the field as not found) — the user is never stuck.
    expect(button('permits.air.import.map.reanalyse').disabled).toBe(false);

    // The picker offers the file's columns in file order, letter and header text.
    await userEvent.click(picker);
    const options = (await screen.findAllByRole('option')).map((option) => option.textContent);
    expect(options).toEqual(['B — Shop', 'G — Source number', 'AA — t/year']);
    await userEvent.click(screen.getByRole('option', { name: 'B — Shop' }));

    expect(screen.queryByTestId('air-field-without-column')).toBeNull();
    expect(button('permits.air.import.map.saveProfile').disabled).toBe(false);
    fireEvent.click(button('permits.air.import.map.reanalyse'));
    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(2));
    expect(api.analyseAirImport.mock.calls[1]![1].spec?.fields?.[2]).toMatchObject({ name: 'Workshop', column: 'B' });
    await waitFor(() => expect(button('permits.air.import.toPreview').disabled).toBe(false));
  });

  it('a field without a column is re-analysed as is (the finding comes from the server) and the preview opens', async () => {
    await toMapping();
    await addField('Workshop');
    api.analyseAirImport.mockResolvedValue(
      analysis({
        issues: [{ severity: 'Warning', messageKey: 'err.ECR-IMP-0422.regColumnMissing', field: 'Workshop', args: { field: 'Workshop' } }],
      }),
    );

    fireEvent.click(button('permits.air.import.map.reanalyse'));
    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(2));
    expect(api.analyseAirImport.mock.calls[1]![1].spec?.fields?.[2]).toEqual({ name: 'Workshop', patterns: [], level: 'Child', parser: 'Text' });
    await waitFor(() => expect(button('permits.air.import.toPreview').disabled).toBe(false));
    expect(screen.queryByRole('alert', { name: text('state.errorTitle') })).toBeNull();
  });
});

describe('запит без відповіді', () => {
  it('the file became unreadable after it was chosen: the reason instead of «The request failed», and «Choose the file again» keeps the mapping', async () => {
    await toMapping(unreadable(xlsx('params.xlsx')));
    await addField('Workshop');
    api.analyseAirImport.mockRejectedValueOnce(new TypeError('Failed to fetch'));

    fireEvent.click(button('permits.air.import.map.reanalyse'));
    const failure = await screen.findByTestId('air-send-failure');
    expect(within(failure).getByText(text('permits.air.import.fileUnreadable.title'))).toBeTruthy();
    expect(within(failure).getByText(text('permits.air.import.fileUnreadable.text'))).toBeTruthy();
    expect(screen.queryByText(text('state.errorTitle'))).toBeNull();
    expect(within(failure).queryByRole('button', { name: text('common.retry') })).toBeNull();

    const input = document.querySelector<HTMLInputElement>('input[type="file"]');
    if (input === null) throw new Error('file button input');
    await userEvent.upload(input, xlsx('params-again.xlsx'));

    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(3));
    const [file, options] = api.analyseAirImport.mock.calls[2]!;
    expect(file.name).toBe('params-again.xlsx');
    expect(options.spec?.fields?.map((field) => field.name)).toEqual(['NaeCode', 'TonsPerYear', 'Workshop']);
    await waitFor(() => expect(screen.queryByTestId('air-send-failure')).toBeNull());
    await waitFor(() => expect(button('permits.air.import.toPreview').disabled).toBe(false));
  });

  it('the server was not reached (the file is readable): its own reason and «Try again» repeats the analysis', async () => {
    await toMapping();
    await addField('Workshop');
    api.analyseAirImport.mockRejectedValueOnce(new TypeError('Failed to fetch'));

    fireEvent.click(button('permits.air.import.map.reanalyse'));
    const failure = await screen.findByTestId('air-send-failure');
    expect(within(failure).getByText(text('permits.air.import.network.title'))).toBeTruthy();

    fireEvent.click(within(failure).getByRole('button', { name: text('common.retry') }));
    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(3));
    expect(api.analyseAirImport.mock.calls[2]![1].spec?.fields?.[2]?.name).toBe('Workshop');
    await waitFor(() => expect(button('permits.air.import.toPreview').disabled).toBe(false));
  });
});

describe('після збереження', () => {
  it('after a save with unread edits the editor is closed, yet «Re-analyse» is next to the reason and opens the preview', async () => {
    api.saveImportProfile.mockResolvedValue({ ...profile(), code: 'TEST_1172026', builtIn: false });
    await toMapping();
    fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.map.edit') }));
    fireEvent.change(await screen.findByLabelText('Header patterns of field TonsPerYear'), { target: { value: 't/year | Tons' } });

    fireEvent.click(button('permits.air.import.map.saveProfile'));
    fireEvent.change(screen.getByLabelText(text('permits.air.import.save.name')), { target: { value: 'Test 1172026' } });
    fireEvent.click(button('permits.air.import.save.submit'));
    await screen.findByText('Profile TEST_1172026 saved and selected.');

    expect(screen.getByText(text('permits.air.import.map.reanalyseFirst'))).toBeTruthy();
    expect(button('permits.air.import.toPreview').disabled).toBe(true);
    fireEvent.click(button('permits.air.import.map.reanalyse'));
    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(2));
    expect(api.analyseAirImport.mock.calls[1]![1]).toEqual({ profileCode: 'TEST_1172026' });
    await waitFor(() => expect(button('permits.air.import.toPreview').disabled).toBe(false));
  });
});
