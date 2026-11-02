import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { apiFetch, EcrApiError } from '@/api/client';
import { renderWithQuery } from '@/test/render';
import { AddRegionModal } from '../AddRegionModal';
import { NaeExcelImportPanel } from '../NaeExcelImportPanel';
import { NaeStructurePage } from '../NaeStructurePage';
import type { NaeTreeImportReport } from '../naeManualApi';

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
  apiFetchResponse: vi.fn(() => Promise.resolve({ blob: () => Promise.resolve(new Blob(['x'])) })),
}));
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, unknown>) => (params === undefined ? key : `${key} ${JSON.stringify(params)}`),
}));

const session = { permissions: ['Registry.View', 'Registry.EditData', 'Registry.EditDefinition'] };
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: session.permissions } }),
}));

/**
 * Структура НВ без AF: порожній стан із трьома шляхами, «Додати регіон» (тіло запиту), «Створити регіони з довідників»
 * лише для порожнього дерева (і розбір 422), майстер імпорту з Excel (перегляд -> застосування з відбитком, попередження).
 * Мережа - підмінений `apiFetch`: обробники сервера на цій лінії ще скелет, справжній прогін - `e2e/naeTreeManual.spec.ts`.
 */

const definition = { id: 9, code: 'NAE_TREE', codeMode: 'Auto', fields: [] };
const levels = [{ id: 1, ordinal: 1, code: 'REGION', name: { values: { en: 'Region' } }, isOptional: false, entryCount: 0 }];
const emptyTree = { registryCode: 'NAE_TREE', isHierarchical: true, dataRevision: 1, levels, nodes: [] };
const fullTree = {
  ...emptyTree,
  nodes: [
    { id: 1, code: 'T-1', display: 'Atyrau', levelCode: 'REGION', parentEntryId: null, ordinal: 1, validFrom: null, validTo: null, isActive: true, childCount: 0, isMatch: false },
  ],
};

let treeBody: unknown;
let definitionError: Error | null;
let seedError: Error | null;

const problem = (status: number, detail: string): EcrApiError =>
  new EcrApiError({ type: 'about:blank', title: 'x', status, detail, code: 'naeTreeNotEmpty' } as never);

const calls = (path: string) =>
  vi.mocked(apiFetch).mock.calls.filter(([p]) => p === path).map(([, init]) => init as RequestInit | undefined);

beforeEach(() => {
  session.permissions = ['Registry.View', 'Registry.EditData', 'Registry.EditDefinition'];
  treeBody = emptyTree;
  definitionError = null;
  seedError = null;
  vi.mocked(apiFetch).mockReset();
  vi.mocked(apiFetch).mockImplementation((path: string) => {
    if (path === '/api/v1/nae-tree/roots') return Promise.resolve({ created: true, regionEntryId: 5, areaEntryId: null, registryCode: 'NAE_TREE', registryProvisioned: false });
    if (path === '/api/v1/nae-tree/seed-roots') {
      return seedError === null ? Promise.resolve({ created: ['ATYRAU', 'MANGYSTAU'], registryCode: 'NAE_TREE', registryProvisioned: false, sourceRegistryCode: 'LAND_REGION' }) : Promise.reject(seedError);
    }
    if (path.includes('/registries/LAND_REGION/rows')) {
      return Promise.resolve({ items: [{ id: 1, code: 'ATYRAU', display: 'Atyrau' }, { id: 2, code: 'MANGYSTAU', display: 'Mangystau' }], nextCursor: null, totalCount: 2 });
    }
    if (path.endsWith('/definition')) return definitionError === null ? Promise.resolve(definition) : Promise.reject(definitionError);
    if (path.endsWith('/hierarchy')) return Promise.resolve({ registryCode: 'NAE_TREE', isHierarchical: true, maxDepth: 6, levels });
    if (path.includes('/tree')) return Promise.resolve(treeBody);
    if (path.endsWith('/languages')) return Promise.resolve([{ code: 'en', nameNative: 'English', isDefault: true }]);

    return Promise.reject(new Error(`unexpected ${path}`));
  });
});

afterEach(cleanup);

const mountPage = () =>
  renderWithQuery(
    <MemoryRouter initialEntries={['/admin/permits/nae']}>
      <NaeStructurePage />
    </MemoryRouter>,
  );

describe('порожнє дерево НВ', () => {
  it('показує три шляхи наповнення і дії', async () => {
    mountPage();

    const empty = await screen.findByText('permits.nae.manual.emptyTitle');
    expect(empty).toBeTruthy();
    for (const key of ['wayAf', 'wayExcel', 'wayManual']) {
      expect(screen.getByText(`permits.nae.manual.${key}`)).toBeTruthy();
    }
    expect(document.querySelector('[data-nae-seed-roots]')).not.toBeNull();
    expect(document.querySelector('[data-nae-empty-add]')).not.toBeNull();
  });

  it('довідник ще не створено (404): ті самі три шляхи, якщо є EditDefinition', async () => {
    definitionError = problem(404, 'no');
    mountPage();

    expect(await screen.findByText('permits.nae.manual.emptyTitle')).toBeTruthy();
    expect(document.querySelector('[data-nae-seed-roots]')).not.toBeNull();
  });

  it('довідника немає і немає EditDefinition: лише підказка «до адміністратора», без дій створення', async () => {
    session.permissions = ['Registry.View', 'Registry.EditData'];
    definitionError = problem(404, 'no');
    mountPage();

    expect(await screen.findByText('permits.nae.import.hintAskAdmin')).toBeTruthy();
    expect(document.querySelector('[data-nae-seed-roots]')).toBeNull();
    expect(document.querySelector('[data-nae-add-region-open]')).toBeNull();
  });

  it('непорожнє дерево: «Створити регіони з довідників» не пропонується, «Додати регіон» лишається', async () => {
    treeBody = fullTree;
    mountPage();

    expect(await screen.findByText('Atyrau')).toBeTruthy();
    expect(document.querySelector('[data-nae-seed-roots]')).toBeNull();
    expect(screen.queryByText('permits.nae.manual.emptyTitle')).toBeNull();
    expect(document.querySelector('[data-nae-add-region-open]')).not.toBeNull();
  });

  it('seed-roots: успіх сповіщає про кількість, 422 - не падає й перечитує дерево', async () => {
    const user = userEvent.setup();
    mountPage();
    await user.click(await screen.findByText('permits.nae.manual.seed'));
    await waitFor(() => expect(calls('/api/v1/nae-tree/seed-roots')).toHaveLength(1));
    expect(calls('/api/v1/nae-tree/seed-roots')[0]?.method).toBe('POST');

    seedError = problem(422, 'tree is not empty');
    await user.click(await screen.findByText('permits.nae.manual.seed'));
    await waitFor(() => expect(calls('/api/v1/nae-tree/seed-roots')).toHaveLength(2));
    // 422 не ламає сторінку: порожній стан лишається, кнопка знову доступна.
    expect(screen.getByText('permits.nae.manual.emptyTitle')).toBeTruthy();
    await waitFor(() => expect((document.querySelector('[data-nae-seed-roots]') as HTMLButtonElement).disabled).toBe(false));
  });
});

describe('AddRegionModal', () => {
  it('шле регіон, район і назви тілом POST /nae-tree/roots', async () => {
    const user = userEvent.setup();
    const onAdded = vi.fn();
    renderWithQuery(<AddRegionModal opened onClose={vi.fn()} onAdded={onAdded} />);

    const select = (await screen.findByLabelText(/permits\.nae\.manual\.region/)) as HTMLSelectElement;
    await waitFor(() => expect(select.options.length).toBe(2));
    await user.selectOptions(select, 'MANGYSTAU');
    await user.type(screen.getByLabelText(/permits\.nae\.manual\.area$/), 'OFFSHORE');
    await user.type(screen.getByLabelText('permits.nae.manual.nameEn'), 'Mangystau shelf');
    await user.click(screen.getByText('permits.nae.manual.addRegionSubmit'));

    await waitFor(() => expect(calls('/api/v1/nae-tree/roots')).toHaveLength(1));
    expect(JSON.parse(String(calls('/api/v1/nae-tree/roots')[0]?.body))).toEqual({
      regionCode: 'MANGYSTAU',
      areaCode: 'OFFSHORE',
      nameEn: 'Mangystau shelf',
      nameRu: null,
      nameKz: null,
    });
    await waitFor(() => expect(onAdded).toHaveBeenCalledWith(5, expect.objectContaining({ regionEntryId: 5 })));
  });
});

const report = (over: Partial<NaeTreeImportReport> = {}): NaeTreeImportReport => ({
  applied: false,
  canApply: true,
  droppedIssues: 0,
  fileSha256: 'abc',
  fingerprint: 'fp-1',
  issues: [
    { severity: 'warning', code: 'nameDiffers', row: 3, column: 'Name_En', naeCode: '1085', detail: 'x' },
    { severity: 'error', code: 'badCode', row: 4, column: 'NAE code', naeCode: null, detail: 'y' },
  ],
  levels: [{ levelCode: 'REGION', create: 2, existing: 1 }],
  registryCode: 'NAE_TREE',
  registryProvisioned: false,
  rowsRead: 7,
  sheetName: 'NAE',
  ...over,
});

describe('NaeExcelImportPanel', () => {
  let reply: NaeTreeImportReport;
  let applyError: Error | null;

  beforeEach(() => {
    reply = report({ issues: [{ severity: 'warning', code: 'nameDiffers', row: 3, column: 'Name_En', naeCode: '1085', detail: 'x' }] });
    applyError = null;
    vi.mocked(apiFetch).mockImplementation((path: string) => {
      if (path === '/api/v1/nae-tree/import/preview') return Promise.resolve(reply);
      if (path === '/api/v1/nae-tree/import/apply') {
        return applyError === null ? Promise.resolve({ ...reply, applied: true }) : Promise.reject(applyError);
      }

      return Promise.resolve({});
    });
  });

  const upload = async (user: ReturnType<typeof userEvent.setup>) => {
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(input, new File(['x'], 'nae.xlsx'));
  };

  it('перегляд показує звіт і попередження, «Застосувати» передає відбиток перегляду', async () => {
    const user = userEvent.setup();
    renderWithQuery(<NaeExcelImportPanel onClose={vi.fn()} />);
    const applyButton = document.querySelector('[data-nae-excel-apply]') as HTMLButtonElement;
    expect(applyButton.disabled).toBe(true);

    await upload(user);
    await user.click(screen.getByText('permits.nae.manual.excel.preview'));

    expect(await screen.findByText('permits.nae.manual.excel.previewDone')).toBeTruthy();
    expect(document.querySelector('[data-nae-excel-issues="warning"]')?.textContent).toContain('permits.nae.manual.issue.nameDiffers');
    expect(document.querySelector('[data-nae-excel-issues="error"]')).toBeNull();
    await waitFor(() => expect(applyButton.disabled).toBe(false));

    await user.click(applyButton);
    await waitFor(() => expect(calls('/api/v1/nae-tree/import/apply')).toHaveLength(1));
    const form = calls('/api/v1/nae-tree/import/apply')[0]?.body as FormData;
    expect(JSON.parse(String(form.get('request')))).toEqual({ expectedFingerprint: 'fp-1' });
    expect((form.get('file') as File).name).toBe('nae.xlsx');
    expect(await screen.findByText('permits.nae.manual.excel.applied', { selector: '[role="status"]' })).toBeTruthy();
  });

  it('помилки у файлі блокують «Застосувати»', async () => {
    reply = report({ canApply: false });
    const user = userEvent.setup();
    renderWithQuery(<NaeExcelImportPanel onClose={vi.fn()} />);
    await upload(user);
    await user.click(screen.getByText('permits.nae.manual.excel.preview'));

    expect(await screen.findByText('permits.nae.manual.excel.hasErrors')).toBeTruthy();
    expect(document.querySelector('[data-nae-excel-issues="error"]')?.textContent).toContain('permits.nae.manual.issue.badCode');
    expect((document.querySelector('[data-nae-excel-apply]') as HTMLButtonElement).disabled).toBe(true);
  });

  it('відбиток не збігся: застосування відхилене, перегляд скинуто з підказкою повторити', async () => {
    applyError = problem(409, 'fingerprint changed');
    const user = userEvent.setup();
    renderWithQuery(<NaeExcelImportPanel onClose={vi.fn()} />);
    await upload(user);
    await user.click(screen.getByText('permits.nae.manual.excel.preview'));
    const applyButton = document.querySelector('[data-nae-excel-apply]') as HTMLButtonElement;
    await waitFor(() => expect(applyButton.disabled).toBe(false));
    await user.click(applyButton);

    expect(await screen.findByText('permits.nae.manual.excel.stale')).toBeTruthy();
    expect(document.querySelector('[data-nae-excel-report]')).toBeNull();
    expect(applyButton.disabled).toBe(true);
  });

  it('«Download template» качає шаблон', async () => {
    const created = vi.fn(() => 'blob:t');
    URL.createObjectURL = created;
    URL.revokeObjectURL = vi.fn();
    const user = userEvent.setup();
    renderWithQuery(<NaeExcelImportPanel onClose={vi.fn()} />);
    await user.click(screen.getByText('permits.nae.manual.excel.template'));

    await waitFor(() => expect(created).toHaveBeenCalledTimes(1));
  });
});
