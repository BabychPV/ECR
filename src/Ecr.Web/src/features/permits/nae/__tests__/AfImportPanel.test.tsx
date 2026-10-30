import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { apiFetch, EcrApiError } from '@/api/client';
import { renderWithQuery } from '@/test/render';
import { AfImportPanel, hasNewWork } from '../AfImportPanel';
import { NaeStructurePage } from '../NaeStructurePage';
import type { AfImportReport } from '../afImportApi';

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
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
 * Завантаження структури NAE з PI AF: перегляд (dry run) -> застосування, звіт, скидання перегляду при зміні полів,
 * показ кнопки лише з EditDefinition і стан «довідник не завантажено». Мережа - підмінений `apiFetch`
 * (обробник сервера на цій лінії ще скелет; справжній прогін - коли він з'явиться).
 */

const sources = [
  { id: 4, code: 'AF1', isActive: true, nameL10n: { en: 'PI AF prod' } },
  { id: 5, code: 'AF2', isActive: false, nameL10n: { en: 'Old AF' } },
];

const report = (over: Partial<AfImportReport> = {}): AfImportReport => ({
  registryCode: 'NAE_TREE',
  dryRun: true,
  applied: false,
  registryProvisioned: true,
  elementsRead: 120,
  sourceComplete: true,
  levels: [
    { levelCode: 'REGION', create: 2, existing: 0 },
    { levelCode: 'SRC', create: 80, existing: 3 },
  ],
  skipped: 1,
  issues: [{ severity: 'skipped', code: 'noRegion', path: '/AF/Barge 9', detail: 'x' }],
  ...over,
});

const imports = () =>
  vi.mocked(apiFetch).mock.calls
    .filter(([path]) => path === '/api/v1/nae-tree/af-import')
    .map(([, init]) => JSON.parse(String(init?.body)) as Record<string, unknown>);

let reply: (body: Record<string, unknown>) => AfImportReport;

beforeEach(() => {
  session.permissions = ['Registry.View', 'Registry.EditData', 'Registry.EditDefinition'];
  reply = (body) => report({ dryRun: body['dryRun'] === true, applied: body['dryRun'] !== true });
  vi.mocked(apiFetch).mockReset();
  vi.mocked(apiFetch).mockImplementation((path: string, init?: RequestInit) => {
    if (path === '/api/v1/data-sources') return Promise.resolve(sources);
    if (path === '/api/v1/nae-tree/af-import') return Promise.resolve(reply(JSON.parse(String(init?.body))));

    return Promise.reject(new Error(`unexpected ${path}`));
  });
});

afterEach(cleanup);

const mountPanel = () => renderWithQuery(<AfImportPanel registryCode="NAE_TREE" onClose={vi.fn()} />);

describe('AfImportPanel', () => {
  it('джерела - лише активні; «Завантажити» вимкнена, доки немає перегляду', async () => {
    mountPanel();

    const select = (await screen.findByLabelText('permits.nae.import.source')) as HTMLSelectElement;
    await waitFor(() => expect(select.options).toHaveLength(1));
    expect(select.options[0]?.textContent).toBe('PI AF prod');
    expect((screen.getByRole('button', { name: 'permits.nae.import.apply' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('перегляд шле dryRun=true з джерелом, коренем і кодом довідника та показує звіт; застосування - dryRun=false', async () => {
    const user = userEvent.setup();
    mountPanel();
    await waitFor(() => expect((screen.getByLabelText('permits.nae.import.source') as HTMLSelectElement).options).toHaveLength(1));

    await user.type(screen.getByLabelText('permits.nae.import.root'), ' ECR_01_Air Equipment/Atyrau ');
    await user.click(screen.getByRole('button', { name: 'permits.nae.import.preview' }));

    const view = await screen.findByText('permits.nae.import.previewDone');
    expect(imports()[0]).toEqual({ dataSourceId: 4, rootPath: 'ECR_01_Air Equipment/Atyrau', registryCode: 'NAE_TREE', dryRun: true });
    const region = view.closest('[data-nae-import-report]')!;
    expect(region.getAttribute('data-nae-import-report')).toBe('preview');
    expect(within(region as HTMLElement).getByText('permits.nae.import.willProvision')).toBeTruthy();
    expect(within(region as HTMLElement).getByText('80')).toBeTruthy();
    expect(within(region as HTMLElement).getByText(/permits\.nae\.import\.issue\.noRegion/)).toBeTruthy();
    expect(within(region as HTMLElement).getByText('/AF/Barge 9')).toBeTruthy();

    const apply = screen.getByRole('button', { name: 'permits.nae.import.apply' }) as HTMLButtonElement;
    expect(apply.disabled).toBe(false);
    await user.click(apply);

    await waitFor(() => expect(imports()).toHaveLength(2));
    expect(imports()[1]).toMatchObject({ dryRun: false, dataSourceId: 4 });
    expect(await screen.findByText('permits.nae.import.applied')).toBeTruthy();
  });

  it('зміна кореня скидає перегляд: застосувати те, чого не бачив, не можна', async () => {
    const user = userEvent.setup();
    mountPanel();
    await waitFor(() => expect((screen.getByLabelText('permits.nae.import.source') as HTMLSelectElement).options).toHaveLength(1));
    await user.click(screen.getByRole('button', { name: 'permits.nae.import.preview' }));
    await screen.findByText('permits.nae.import.previewDone');

    await user.type(screen.getByLabelText('permits.nae.import.root'), 'X');

    expect(screen.getByText('permits.nae.import.stale')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'permits.nae.import.apply' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('нічого нового (усе вже є) - застосувати нічого, сказано прямо', async () => {
    reply = () => report({ registryProvisioned: false, levels: [{ levelCode: 'SRC', create: 0, existing: 9 }], skipped: 0, issues: [] });
    const user = userEvent.setup();
    mountPanel();
    await waitFor(() => expect((screen.getByLabelText('permits.nae.import.source') as HTMLSelectElement).options).toHaveLength(1));
    await user.click(screen.getByRole('button', { name: 'permits.nae.import.preview' }));

    expect(await screen.findByText('permits.nae.import.nothingNew')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'permits.nae.import.apply' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('неповний список AF показується попередженням', async () => {
    reply = () => report({ sourceComplete: false });
    const user = userEvent.setup();
    mountPanel();
    await waitFor(() => expect((screen.getByLabelText('permits.nae.import.source') as HTMLSelectElement).options).toHaveLength(1));
    await user.click(screen.getByRole('button', { name: 'permits.nae.import.preview' }));

    expect(await screen.findByText('permits.nae.import.incomplete')).toBeTruthy();
  });

  it('hasNewWork: нові вузли або довідник, що створюється', () => {
    expect(hasNewWork(report({ registryProvisioned: false, levels: [{ levelCode: 'A', create: 0, existing: 1 }] }))).toBe(false);
    expect(hasNewWork(report({ registryProvisioned: false, levels: [{ levelCode: 'A', create: 1, existing: 1 }] }))).toBe(true);
    expect(hasNewWork(report({ registryProvisioned: true, levels: [] }))).toBe(true);
  });
});

describe('NaeStructurePage: довідник не завантажено', () => {
  beforeEach(() => {
    vi.mocked(apiFetch).mockImplementation((path: string) => {
      if (path.endsWith('/definition') || path.includes('/tree') || path.endsWith('/hierarchy')) {
        return Promise.reject(new EcrApiError({ status: 404, title: 'not found' } as never));
      }
      if (path === '/api/v1/data-sources') return Promise.resolve(sources);

      return Promise.reject(new Error(`unexpected ${path}`));
    });
  });

  const mount = () =>
    renderWithQuery(
      <MemoryRouter initialEntries={['/admin/permits/nae']}>
        <NaeStructurePage />
      </MemoryRouter>,
    );

  it('з EditDefinition: підказка «завантажте з PI AF» і кнопка, що відкриває панель; дерева немає', async () => {
    const user = userEvent.setup();
    mount();

    expect(await screen.findByText('permits.nae.import.hintNotProvisioned')).toBeTruthy();
    expect(screen.queryByRole('tree')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'permits.nae.import.open' }));
    expect(await screen.findByRole('button', { name: 'permits.nae.import.preview' })).toBeTruthy();
  });

  it('без EditDefinition: кнопки немає, підказка просить адміністратора', async () => {
    session.permissions = ['Registry.View', 'Registry.EditData'];
    mount();

    expect(await screen.findByText('permits.nae.import.hintAskAdmin')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'permits.nae.import.open' })).toBeNull();
  });

  it('лише читання: ні кнопки, ні панелі', async () => {
    session.permissions = ['Registry.View', 'Registry.EditDefinition'];
    mount();

    expect(await screen.findByText('permits.nae.import.hintAskAdmin')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'permits.nae.import.open' })).toBeNull();
  });
});
