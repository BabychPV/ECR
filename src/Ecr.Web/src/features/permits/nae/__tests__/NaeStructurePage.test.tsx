import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { renderWithQuery } from '@/test/render';
import { CreateNodeModal } from '../CreateNodeModal';
import { NaeStructurePage } from '../NaeStructurePage';
import { NodeAttributesForm } from '../NodeAttributesForm';
import { WhereUsed } from '../WhereUsed';

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, unknown>) => (params === undefined ? key : `${key} ${JSON.stringify(params)}`),
}));

const session = { permissions: ['Registry.View', 'Registry.EditData'] };
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: session.permissions } }),
}));

/**
 * Сторінка структури NAE: брама прав (без `Registry.EditData` - жодної дії зміни), вибір вузла з адреси, форма атрибутів
 * (автокод, успадкований регіон, район «-»), створення вузла, «Де використовується» (порожньо / перелік / збій).
 * Мережа - підмінений `apiFetch`: обробники сервера на цій лінії ще скелет, тож справжній e2e - окремо.
 */

const fields = [
  { id: 1, code: 'NaeCode', nameL10n: { values: { en: 'NAE code' } }, dataType: 'String', isRequired: true },
  { id: 2, code: 'RegionCode', nameL10n: { values: { en: 'Region code' } }, dataType: 'String', isRequired: true },
  { id: 3, code: 'AreaCode', nameL10n: { values: { en: 'Area code' } }, dataType: 'String', isRequired: true },
];

const definition = { id: 9, code: 'NAE_TREE', codeMode: 'Auto', fields };

const levels = [
  { id: 1, ordinal: 1, code: 'REGION', name: { values: { en: 'Region' } }, isOptional: false, entryCount: 1 },
  { id: 2, ordinal: 2, code: 'SITE', name: { values: { en: 'Site' } }, isOptional: false, entryCount: 1 },
  { id: 3, ordinal: 3, code: 'SRC', name: { values: { en: 'NAE' } }, isOptional: false, entryCount: 1 },
];

const treeNode = (over: Record<string, unknown>) => ({
  parentEntryId: null,
  ordinal: 1,
  validFrom: null,
  validTo: null,
  isActive: true,
  childCount: 0,
  isMatch: false,
  ...over,
});

const tree = {
  registryCode: 'NAE_TREE',
  isHierarchical: true,
  dataRevision: 1,
  levels,
  nodes: [
    treeNode({ id: 1, code: 'T-1', display: 'Atyrau', levelCode: 'REGION', childCount: 1 }),
    treeNode({ id: 2, code: 'T-2', display: 'Barge 7', levelCode: 'SITE', parentEntryId: 1, childCount: 1 }),
    treeNode({ id: 3, code: 'T-3', display: 'Pipe 1085', levelCode: 'SRC', parentEntryId: 2 }),
  ],
};

const detail = (id: number, values: Record<string, string | null>) => ({
  id,
  code: `T-${String(id)}`,
  displayL10n: { values: { en: `Name ${String(id)}` } },
  parentEntryId: id === 1 ? null : id - 1,
  values,
});

let references: unknown;
let referencesError: Error | null;

beforeEach(() => {
  session.permissions = ['Registry.View', 'Registry.EditData'];
  references = { registryCode: 'NAE_TREE', entryId: 3, items: [], total: 0 };
  referencesError = null;
  vi.mocked(apiFetch).mockReset();
  vi.mocked(apiFetch).mockImplementation((path: string, init?: RequestInit) => {
    if (path.endsWith('/definition')) return Promise.resolve(definition);
    if (path.endsWith('/hierarchy')) return Promise.resolve({ registryCode: 'NAE_TREE', isHierarchical: true, maxDepth: 6, levels });
    if (path.includes('/tree')) return Promise.resolve(tree);
    if (path.endsWith('/languages')) return Promise.resolve([{ code: 'en', nameNative: 'English', isDefault: true }]);
    if (path.endsWith('/references')) return referencesError === null ? Promise.resolve(references) : Promise.reject(referencesError);
    if (path.endsWith('/entries') && init?.method === 'POST') return Promise.resolve({ id: 10 });
    const entry = /\/entries\/(\d+)$/.exec(path);
    if (entry !== null) {
      const id = Number(entry[1]);

      return Promise.resolve(detail(id, { NaeCode: id === 3 ? '1085' : null, RegionCode: 'ATYRAU', AreaCode: id === 1 ? '-' : 'ONSHORE' }));
    }

    return Promise.reject(new Error(`unexpected ${path}`));
  });
});

afterEach(cleanup);

function mountPage(search = '') {
  return renderWithQuery(
    <MemoryRouter initialEntries={[`/admin/permits/nae${search}`]}>
      <NaeStructurePage />
    </MemoryRouter>,
  );
}

const posts = () =>
  vi.mocked(apiFetch).mock.calls.filter(([, init]) => init?.method === 'POST').map(([path, init]) => ({ path, body: JSON.parse(String(init?.body)) as Record<string, unknown> }));

describe('NaeStructurePage', () => {
  it('?node= вибирає вузол: панель показує форму з даними запису, код - лише для читання', async () => {
    mountPage('?node=3');

    const form = await screen.findByRole('form', { name: 'permits.nae.form.editTitle' });
    expect((within(form).getByLabelText(/NAE code/) as HTMLInputElement).value).toBe('1085');
    const code = within(form).getByLabelText('registries.code') as HTMLInputElement;
    expect(code.readOnly).toBe(true);
    expect(code.value).toBe('T-3');
    expect(screen.getByTestId('tree-panel-move')).toBeTruthy();
  });

  it('технічний код запису не показується в рядках дерева', async () => {
    mountPage();

    await screen.findByRole('treeitem', { name: /Atyrau/ });
    expect(screen.queryByText('T-1')).toBeNull();
  });

  it('без Registry.EditData: дерево, пошук і панель є, а дій зміни немає (прибрані, не вимкнені)', async () => {
    session.permissions = ['Registry.View'];
    mountPage('?node=3');

    await screen.findByRole('form', { name: 'permits.nae.form.editTitle' });
    expect(document.querySelector('[data-nae-readonly]')).not.toBeNull();
    expect(screen.queryByTestId('tree-panel-move')).toBeNull();
    expect(screen.queryByRole('button', { name: 'common.save' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'treeEditor.retire.action' })).toBeNull();
    expect(screen.queryByRole('button', { name: /treeEditor\.addRoot/ })).toBeNull();
    expect((within(screen.getByRole('form')).getByLabelText(/NAE code/) as HTMLInputElement).closest('fieldset')?.disabled).toBe(true);
  });

  it('з правом: «Зберегти» вимкнена, доки нічого не змінено, і шле лише змінене поле', async () => {
    const user = userEvent.setup();
    mountPage('?node=3');
    const form = await screen.findByRole('form', { name: 'permits.nae.form.editTitle' });
    const save = within(form).getByRole('button', { name: 'common.save' }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);

    const nae = within(form).getByLabelText(/NAE code/);
    await user.clear(nae);
    await user.type(nae, '1086');
    expect(save.disabled).toBe(false);
    await user.click(save);

    await waitFor(() => expect(posts()).toHaveLength(1));
    const [post] = posts();
    expect(post?.path).toBe('/api/v1/registries/NAE_TREE/entries');
    expect(post?.body).toMatchObject({ id: 3, levelCode: null, parentEntryId: 2, values: { NaeCode: '1086' } });
  });
});

describe('CreateNodeModal', () => {
  const draftUnder = (parent: { id: number; label: string } | null) => ({
    parent: parent === null ? null : { ...parent, parentId: null, ordinal: 1 },
    level: { code: 'SRC', ordinal: 3, name: 'NAE', isOptional: false },
  });

  it('автокод: поля коду немає; регіон і район підказані від батька; створення шле код порожнім і рівень', async () => {
    const user = userEvent.setup();
    const onCreated = vi.fn();
    renderWithQuery(
      <MemoryRouter>
        <CreateNodeModal
          registryCode="NAE_TREE"
          registryId={9}
          codeAutomatic
          fields={fields as never}
          draft={draftUnder({ id: 2, label: 'Barge 7' })}
          onClose={vi.fn()}
          onCreated={onCreated}
        />
      </MemoryRouter>,
    );

    const form = await screen.findByRole('form');
    expect(within(form).queryByLabelText('registries.code')).toBeNull();
    await waitFor(() => expect((within(form).getByLabelText(/Region code/) as HTMLInputElement).value).toBe('ATYRAU'));
    expect((within(form).getByLabelText(/Area code/) as HTMLInputElement).value).toBe('ONSHORE');

    await user.type(within(form).getByLabelText(/registries\.name/), 'Pipe 1090');
    await user.type(within(form).getByLabelText(/NAE code/), '1090');
    await user.click(within(form).getByRole('button', { name: 'common.save' }));

    await waitFor(() => expect(onCreated).toHaveBeenCalledWith(10));
    expect(posts()[0]?.body).toMatchObject({
      id: null,
      code: '',
      parentEntryId: 2,
      levelCode: 'SRC',
      display: { values: { en: 'Pipe 1090' } },
      values: { NaeCode: '1090', RegionCode: 'ATYRAU', AreaCode: 'ONSHORE' },
    });
  });

  it('порожні обов’язкові поля не йдуть на сервер, а район лишається необов’язковим («-»)', async () => {
    const user = userEvent.setup();
    renderWithQuery(
      <MemoryRouter>
        <CreateNodeModal
          registryCode="NAE_TREE"
          registryId={9}
          codeAutomatic
          fields={fields as never}
          draft={draftUnder(null)}
          onClose={vi.fn()}
          onCreated={vi.fn()}
        />
      </MemoryRouter>,
    );

    const form = await screen.findByRole('form');
    await user.click(within(form).getByRole('button', { name: 'common.save' }));
    expect(posts()).toHaveLength(0);
    expect(within(form).getAllByText('permits.nae.form.fieldRequired')).toHaveLength(2);
    expect(within(form).getByText('permits.nae.form.nameRequired')).toBeTruthy();
  });
});

describe('NodeAttributesForm: відмова ділового ключа', () => {
  it('дубль ключа показується в формі, а не губиться', async () => {
    const user = userEvent.setup();
    const { EcrApiError } = await import('@/api/client');
    vi.mocked(apiFetch).mockImplementation((path: string, init?: RequestInit) => {
      if (path.endsWith('/languages')) return Promise.resolve([{ code: 'en', nameNative: 'English', isDefault: true }]);
      if (init?.method === 'POST') {
        return Promise.reject(new EcrApiError({ status: 409, title: 'taken', extensions2: { messageKey: 'err.ECR-REG-4092.keyTaken' } } as never));
      }

      return Promise.reject(new Error(`unexpected ${path}`));
    });
    renderWithQuery(
      <MemoryRouter>
        <NodeAttributesForm
          registryCode="NAE_TREE"
          registryId={9}
          codeAutomatic
          fields={fields as never}
          target={{ mode: 'create', parent: null, level: { code: 'REGION', ordinal: 1, name: 'Region', isOptional: false } }}
          readOnly={false}
          onSaved={vi.fn()}
        />
      </MemoryRouter>,
    );

    const form = await screen.findByRole('form');
    await user.type(within(form).getByLabelText(/registries\.name/), 'Atyrau');
    await user.type(within(form).getByLabelText(/NAE code/), '0001');
    await user.type(within(form).getByLabelText(/Region code/), 'ATYRAU');
    await user.click(within(form).getByRole('button', { name: 'common.save' }));

    expect(await within(form).findByText('permits.nae.form.keyTaken')).toBeTruthy();
  });
});

describe('WhereUsed', () => {
  const mount = () =>
    renderWithQuery(
      <MemoryRouter>
        <WhereUsed registryCode="NAE_TREE" entryId={3} />
      </MemoryRouter>,
    );

  it('читає /references вузла; порожній перелік - явний порожній стан, а не «недоступно»', async () => {
    mount();

    expect(await screen.findByText('permits.nae.used.empty')).toBeTruthy();
    expect(document.querySelector('[data-nae-used]')?.getAttribute('data-nae-used')).toBe('empty');
    expect(vi.mocked(apiFetch).mock.calls.map(([path]) => path)).toContain('/api/v1/registries/NAE_TREE/entries/3/references');
  });

  it('перелік: дозвіл (посилання), тип, термін, кількість речовин', async () => {
    references = {
      registryCode: 'NAE_TREE',
      entryId: 3,
      total: 1,
      items: [{ kind: 'permit', permitId: 55, permitNumber: 'KZ-1', permitTypeCode: 'AIR', validFrom: '2026-01-01', validTo: '2027-01-01', substanceCount: 4 }],
    };
    mount();

    const link = await screen.findByRole('link', { name: 'KZ-1' });
    expect(link.getAttribute('href')).toBe('/admin/permits/55');
    const row = link.closest('tr')!;
    expect(within(row).getByText('AIR')).toBeTruthy();
    expect(within(row).getByText('4')).toBeTruthy();
    expect(document.querySelector('[data-nae-used]')?.getAttribute('data-nae-used')).toBe('list');
  });

  it('збій читання - відмова з «Повторити», а не порожній стан', async () => {
    referencesError = new Error('500');
    mount();

    await waitFor(() => expect(document.querySelector('[data-nae-used]')?.getAttribute('data-nae-used')).toBe('loading'));
    await waitFor(() => expect(screen.queryByText('permits.nae.used.empty')).toBeNull());
    expect(await screen.findByRole('alert')).toBeTruthy();
  });
});
