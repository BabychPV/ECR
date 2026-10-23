import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { PermitGroupsPage } from '@/pages/admin/PermitGroupsPage';
import type { PermitGroupDto } from '../groupsApi';

/**
 * Екран «Groups» (групи типів дозволів): перелік з одного запиту, права, шторка створення/редагування, помилки сервера
 * (`messageKey` біля поля, `4097`, `4098`, `4099` з перечитуванням), деактивація, видалення.
 *
 * Мутаційний доказ (вручну, відновлено): без `canEdit &&` перед «New group» глядач бачить кнопку (падає «без права»);
 * без `rowVersion` від першого PUT у другому запиті (`typesBody(draft, version)`) тест «склад іде з токеном після
 * збереження назви» бачить старий токен; прибери гілку `conflict` у `fail` - тест «4099» не побачить перечитування.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
  language: () => 'en',
}));

const session = vi.hoisted(() => ({ permissions: ['Registry.View', 'Permit.EditType'] as string[] }));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: session.permissions, isSimulation: false } }),
}));

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function problem(status: number, errorCode: string, messageKey: string, extra: Record<string, unknown> = {}): Response {
  return json({ title: errorCode, status, errorCode, correlationId: 'c', messageKey, detail: `detail of ${messageKey}`, ...extra }, status);
}

const typeDto = (code: string, name: string) => ({ code, nameL10n: { en: name }, category: 'Water', headerRegistryCode: `PRM_${code}` });

function group(over: Partial<PermitGroupDto> & { code: string }): PermitGroupDto {
  return {
    descriptionL10n: {},
    isActive: true,
    nameL10n: { en: over.code },
    registryCode: `PRMG_${over.code}`,
    registryDefId: 90,
    rowVersion: 'v1',
    types: [],
    updatedAt: '2026-10-20T10:00:00Z',
    usedInColumns: 0,
    usedInTemplates: 0,
    ...over,
  };
}

const PublishedTypes = [
  { code: 'WaterIntake', status: 'Published', nameL10n: { en: 'Water intake' }, category: 'Water', hasUnpublishedChanges: false, permitCount: 1, tables: [], updatedAt: null, updatedByDisplayName: null, version: 1 },
  { code: 'Discharge', status: 'Published', nameL10n: { en: 'Discharge' }, category: 'Water', hasUnpublishedChanges: false, permitCount: 1, tables: [], updatedAt: null, updatedByDisplayName: null, version: 1 },
  { code: 'Draft1', status: 'Draft', nameL10n: { en: 'Only a draft' }, category: 'Air', hasUnpublishedChanges: true, permitCount: 0, tables: [], updatedAt: null, updatedByDisplayName: null, version: 1 },
];

interface Sent {
  method: string;
  url: string;
  body: unknown;
}

interface Backend {
  groups: PermitGroupDto[];
  /** Відповідь на наступний запис (POST/PUT/DELETE) замість успіху. */
  failNext: Response | null;
  sent: Sent[];
  gets: string[];
}

let backend: Backend;

function stub(groups: PermitGroupDto[]): void {
  backend = { groups, failNext: null, sent: [], gets: [] };
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.includes('/api/v1/languages')) {
        return json([
          { code: 'en', nameNative: 'English', isDefault: true },
          { code: 'ru', nameNative: 'Русский', isDefault: false },
          { code: 'kz', nameNative: 'Қазақша', isDefault: false },
        ]);
      }
      if (url.includes('/api/v1/permit-types')) return json({ items: PublishedTypes, nextCursor: null, totalCount: 3 });
      if (!url.includes('/api/v1/permit-groups')) return json(null);

      const tail = url.split('/api/v1/permit-groups')[1] ?? '';
      const parts = tail.split('/').filter((part) => part !== '');
      const code = parts[0] === undefined ? undefined : decodeURIComponent(parts[0]);
      if (method === 'GET') {
        backend.gets.push(url);
        if (code === undefined) return json(backend.groups);
        const found = backend.groups.find((item) => item.code === code);

        return found === undefined ? problem(404, 'ECR-PRM-0405', 'err.ECR-PRM-0405.group', { code }) : json(found);
      }

      const body = init?.body === undefined ? null : (JSON.parse(String(init.body)) as unknown);
      backend.sent.push({ method, url, body });
      if (backend.failNext !== null) {
        const failure = backend.failNext;
        backend.failNext = null;

        return failure;
      }
      if (method === 'POST') {
        const request = body as { code: string; nameL10n: Record<string, string>; typeCodes: string[] | null };
        const created = group({
          code: request.code,
          nameL10n: request.nameL10n,
          types: (request.typeCodes ?? []).map((typeCode) => typeDto(typeCode, typeCode)),
        });
        backend.groups = [...backend.groups, created];

        return json(created, 201);
      }
      if (method === 'DELETE') {
        backend.groups = backend.groups.filter((item) => item.code !== code);

        return new Response(null, { status: 204 });
      }
      const current = backend.groups.find((item) => item.code === code) as PermitGroupDto;
      const next: PermitGroupDto =
        parts[1] === 'types'
          ? { ...current, types: (body as { typeCodes: string[] }).typeCodes.map((typeCode) => typeDto(typeCode, typeCode)), rowVersion: `${current.rowVersion}+t` }
          : { ...current, ...(body as Partial<PermitGroupDto>), rowVersion: `${current.rowVersion}+m` };
      backend.groups = backend.groups.map((item) => (item.code === code ? next : item));

      return json(next);
    }),
  );
}

function Where(): null {
  (globalThis as { __where?: string }).__where = useLocation().search;

  return null;
}

function show(entry = '/admin/permits/groups', client = new QueryClient({ defaultOptions: { queries: { retry: false } } })): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={client}>
          <Where />
          <Routes>
            <Route path="/admin/permits/groups" element={<PermitGroupsPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const where = (): string => (globalThis as { __where?: string }).__where ?? '';

beforeEach(() => {
  session.permissions = ['Registry.View', 'Permit.EditType'];
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  delete (globalThis as { __where?: string }).__where;
});

const water = group({
  code: 'WATER',
  nameL10n: { en: 'Water permits' },
  descriptionL10n: { en: 'All water permits' },
  rowVersion: 'w1',
  types: [typeDto('WaterIntake', 'Water intake'), typeDto('Discharge', 'Discharge')],
});
const used = group({ code: 'AIRUSED', nameL10n: { en: 'Air used' }, usedInColumns: 2, usedInTemplates: 1, rowVersion: 'a1' });

describe('Groups: перелік і права', () => {
  it('показує групу: назва, код, опис, склад і використання з одного запиту', async () => {
    stub([water, used]);
    show();

    expect(await screen.findByText('Water permits')).not.toBeNull();
    // Той, хто може правити, бачить «New group» у шапці (лінивий чанк): без цього перевірка «немає кнопки» в тесті прав нічого б не доводила.
    expect(await screen.findByRole('button', { name: 'permits.groups.new' })).not.toBeNull();
    expect(screen.getByText('WATER')).not.toBeNull();
    expect(screen.getByText('All water permits')).not.toBeNull();
    expect(screen.getByText('Water intake · Discharge')).not.toBeNull();
    expect(screen.getByText('permits.groups.used {"templates":"1","columns":"2"}')).not.toBeNull();
    expect(backend.gets.filter((url) => url.endsWith('/api/v1/permit-groups'))).toHaveLength(1);
  });

  it('порожній перелік: підказка і створення для того, хто може', async () => {
    stub([]);
    show();

    expect(await screen.findByText('permits.groups.empty.title')).not.toBeNull();
    expect(screen.getAllByRole('button', { name: 'permits.groups.new' }).length).toBeGreaterThan(0);
  });

  it('без права Permit.EditType: немає «New group», шторка лише для читання без Save', async () => {
    session.permissions = ['Registry.View'];
    stub([water]);
    show('/admin/permits/groups?panel=WATER');

    await screen.findByText('permits.groups.drawer.readOnly');
    // Кнопка шапки живе в лінивому чанку `PageHeaderActions`: даємо йому з'явитися, інакше «немає кнопки» було б хибно-зеленим.
    await new Promise((resolve) => setTimeout(resolve, 500));
    expect(screen.queryByRole('button', { name: 'permits.groups.new' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'permits.groups.drawer.save' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'permits.groups.drawer.delete' })).toBeNull();
    expect((screen.getByRole('switch') as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByText('permits.groups.readOnly')).not.toBeNull();
  });

  it('клік по рядку відкриває шторку в адресі, пошук читає й опис', async () => {
    stub([water, used]);
    show('/admin/permits/groups?q=all%20water');

    fireEvent.click(await screen.findByText('Water permits'));
    expect(screen.queryByText('Air used')).toBeNull();
    await waitFor(() => expect(where()).toContain('panel=WATER'));
  });
});

describe('Groups: створення', () => {
  it('нова група: POST з кодом, назвою й складом з опублікованих типів; шторка закривається', async () => {
    stub([]);
    show('/admin/permits/groups?panel=_new');

    fireEvent.change(await screen.findByLabelText(/^permits\.groups\.field\.code/), { target: { value: 'WATER' } });
    fireEvent.change(await screen.findByLabelText(/permits\.groups\.field\.name · English/), { target: { value: 'Water permits' } });

    const types = await screen.findByLabelText('permits.groups.field.types');
    fireEvent.click(types);
    expect(screen.queryByRole('option', { name: 'Only a draft' })).toBeNull();
    fireEvent.click(await screen.findByRole('option', { name: 'Water intake' }));
    fireEvent.click(types);
    fireEvent.click(await screen.findByRole('option', { name: 'Discharge' }));
    // Живий e2e: коли обрано ВСІ типи, список не має показувати оманливе «опублікованих типів не знайдено»
    // (воно ще й перекривало кнопку Create). Мутація: поверни `hidePickedOptions` - тест почервоніє.
    expect(screen.queryByText('permits.groups.field.typesEmpty')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.create' }));

    await waitFor(() => expect(backend.sent).toHaveLength(1));
    expect(backend.sent[0]).toMatchObject({
      method: 'POST',
      body: { code: 'WATER', nameL10n: { en: 'Water permits' }, descriptionL10n: null, typeCodes: ['WaterIntake', 'Discharge'] },
    });
    await waitFor(() => expect(where()).not.toContain('panel='));
  });

  it('недопустимий код і порожня назва - помилки біля полів, запиту немає', async () => {
    stub([]);
    show('/admin/permits/groups?panel=_new');

    fireEvent.change(await screen.findByLabelText(/^permits\.groups\.field\.code/), { target: { value: '1bad' } });
    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.create' }));

    expect(await screen.findByText(/err\.ECR-PRM-0422\.permitGroupCodeInvalid/)).not.toBeNull();
    expect(screen.getByText('err.ECR-PRM-0422.permitGroupNameRequired')).not.toBeNull();
    expect(backend.sent).toHaveLength(0);
  });

  it('409 4097 (код зайнятий): людське повідомлення за messageKey біля поля коду, шторка лишається', async () => {
    stub([]);
    show('/admin/permits/groups?panel=_new');
    backend.failNext = problem(409, 'ECR-PRM-4097', 'err.ECR-PRM-4097.code', { code: 'WATER' });

    fireEvent.change(await screen.findByLabelText(/^permits\.groups\.field\.code/), { target: { value: 'WATER' } });
    fireEvent.change(await screen.findByLabelText(/permits\.groups\.field\.name · English/), { target: { value: 'Water' } });
    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.create' }));

    expect(await screen.findByText('err.ECR-PRM-4097.code {"code":"WATER"}')).not.toBeNull();
    expect(where()).toContain('panel=_new');
  });

  it('422 permitGroupMemberNotPublished: помилка біля поля складу', async () => {
    stub([]);
    show('/admin/permits/groups?panel=_new');
    backend.failNext = problem(422, 'ECR-PRM-0422', 'err.ECR-PRM-0422.permitGroupMemberNotPublished', { typeCode: 'Draft1' });

    fireEvent.change(await screen.findByLabelText(/^permits\.groups\.field\.code/), { target: { value: 'WATER' } });
    fireEvent.change(await screen.findByLabelText(/permits\.groups\.field\.name · English/), { target: { value: 'Water' } });
    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.create' }));

    expect(await screen.findByText('err.ECR-PRM-0422.permitGroupMemberNotPublished {"typeCode":"Draft1"}')).not.toBeNull();
  });
});

describe('Groups: редагування, конкурентність, деактивація, видалення', () => {
  it('склад іде з токеном, який повернуло збереження назви; код не редагується', async () => {
    stub([water]);
    show('/admin/permits/groups?panel=WATER');

    const code = (await screen.findByLabelText(/^permits\.groups\.field\.code/)) as HTMLInputElement;
    expect(code.disabled).toBe(true);

    fireEvent.change(await screen.findByLabelText(/permits\.groups\.field\.name · English/), { target: { value: 'Water 2' } });
    const types = await screen.findByLabelText('permits.groups.field.types');
    fireEvent.click(types);
    fireEvent.keyDown(types, { key: 'Backspace' });
    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.save' }));

    await waitFor(() => expect(backend.sent).toHaveLength(2));
    expect(backend.sent[0]).toMatchObject({ method: 'PUT', body: { nameL10n: { en: 'Water 2' }, isActive: true, rowVersion: 'w1' } });
    expect(backend.sent[1]?.url).toContain('/api/v1/permit-groups/WATER/types');
    expect(backend.sent[1]?.body).toMatchObject({ rowVersion: 'w1+m', typeCodes: ['WaterIntake'] });
    await waitFor(() => expect(where()).not.toContain('panel='));
  });

  it('деактивація: перемикач «Active» шле isActive=false з токеном; без змін Save недоступний', async () => {
    stub([water]);
    show('/admin/permits/groups?panel=WATER');

    const save = (await screen.findByRole('button', { name: 'permits.groups.drawer.save' })) as HTMLButtonElement;
    expect(save.disabled).toBe(true);

    fireEvent.click(screen.getByRole('switch'));
    expect(save.disabled).toBe(false);
    fireEvent.click(save);

    await waitFor(() => expect(backend.sent).toHaveLength(1));
    expect(backend.sent[0]?.body).toMatchObject({ isActive: false, rowVersion: 'w1' });
    expect(backend.sent[0]?.url).not.toContain('/types');
  });

  it('409 4099: показує причину, перечитує групу (GET) і лишає шторку з актуальним станом', async () => {
    stub([water]);
    show('/admin/permits/groups?panel=WATER');
    const name = await screen.findByLabelText(/permits\.groups\.field\.name · English/);

    backend.failNext = problem(409, 'ECR-PRM-4099', 'err.ECR-PRM-4099.changed', { code: 'WATER' });
    // Чужа правка вже в базі: актуальний стан інший, ніж у шторці.
    backend.groups = [{ ...water, nameL10n: { en: 'Renamed by someone' }, rowVersion: 'w9' }];
    fireEvent.change(name, { target: { value: 'My edit' } });
    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.save' }));

    expect(await screen.findByText('permits.groups.drawer.conflict')).not.toBeNull();
    await waitFor(() => expect((screen.getByLabelText(/permits\.groups\.field\.name · English/) as HTMLInputElement).value).toBe('Renamed by someone'));
    expect(backend.gets.some((url) => url.endsWith('/api/v1/permit-groups/WATER'))).toBe(true);

    // Повторна правка іде вже зі свіжим токеном.
    fireEvent.change(screen.getByLabelText(/permits\.groups\.field\.name · English/), { target: { value: 'My edit' } });
    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.save' }));
    await waitFor(() => expect(backend.sent).toHaveLength(2));
    expect(backend.sent[1]?.body).toMatchObject({ rowVersion: 'w9' });
  });

  // Мутаційний доказ: прибери `invalidateQueries(entries(PRMG_…))` у `useInvalidateGroups` - запис піклера лишиться свіжим
  // (5 хв) і тест почервоніє: відкритий документ показував би старий склад.
  it('збереження групи скидає кеш піклера PRMG_<код> (склад у відкритому документі не застаріває)', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const pickerKey = ['registries', 'entries', 'PRMG_WATER', '2026-10-31'];
    const otherKey = ['registries', 'entries', 'PRMG_OTHER', '2026-10-31'];
    client.setQueryData(pickerKey, [{ id: 1 }]);
    client.setQueryData(otherKey, [{ id: 2 }]);
    // «Unlisted» ціль (`by-id`) теж не має лишатись старою після зміни групи. Мутація: прибери скидання ['registries','target'].
    const targetKey = ['registries', 'target', 90];
    client.setQueryData(targetKey, { id: 90, code: 'PRMG_WATER', isActive: true });
    stub([water]);
    show('/admin/permits/groups?panel=WATER', client);

    fireEvent.click(await screen.findByRole('switch'));
    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.save' }));

    await waitFor(() => expect(client.getQueryState(pickerKey)?.isInvalidated).toBe(true));
    expect(client.getQueryState(otherKey)?.isInvalidated).toBe(false);
    await waitFor(() => expect(client.getQueryState(targetKey)?.isInvalidated).toBe(true));
  });

  it('мертвий ?panel (неіснуючий код) знімається з адреси, живий лишається', async () => {
    stub([water]);
    show('/admin/permits/groups?panel=NOPE');

    await screen.findByText('Water permits');
    await waitFor(() => expect(where()).not.toContain('panel='));
    cleanup();

    show('/admin/permits/groups?panel=WATER');
    await screen.findByRole('dialog');
    expect(where()).toContain('panel=WATER');
  });

  it('?panel=_new без права знімається з адреси, а з правом лишається', async () => {
    session.permissions = ['Registry.View'];
    stub([water]);
    show('/admin/permits/groups?panel=_new');

    await screen.findByText('Water permits');
    await waitFor(() => expect(where()).not.toContain('panel='));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('використана група: видалення недоступне, є пояснення «лише деактивувати»', async () => {
    stub([used]);
    show('/admin/permits/groups?panel=AIRUSED');

    expect(await screen.findByText('permits.groups.drawer.usedNote {"templates":"1","columns":"2"}')).not.toBeNull();
    expect(screen.queryByRole('button', { name: 'permits.groups.drawer.delete' })).toBeNull();
  });

  it('невикористана група: видалення через підтвердження, DELETE і закриття шторки', async () => {
    stub([water]);
    show('/admin/permits/groups?panel=WATER');

    fireEvent.click(await screen.findByRole('button', { name: 'permits.groups.drawer.delete' }));
    fireEvent.click(await screen.findByTestId('confirm-verb'));

    await waitFor(() => expect(backend.sent).toHaveLength(1));
    expect(backend.sent[0]).toMatchObject({ method: 'DELETE' });
    expect(backend.sent[0]?.url).toContain('/api/v1/permit-groups/WATER');
    await waitFor(() => expect(where()).not.toContain('panel='));
  });

  it('409 4098 на видаленні (групу встигли використати): повідомлення сервера, шторка лишається', async () => {
    stub([water]);
    show('/admin/permits/groups?panel=WATER');
    backend.failNext = problem(409, 'ECR-PRM-4098', 'err.ECR-PRM-4098.inUse', { code: 'WATER', templates: '2' });

    fireEvent.click(await screen.findByRole('button', { name: 'permits.groups.drawer.delete' }));
    fireEvent.click(await screen.findByTestId('confirm-verb'));

    await waitFor(() => expect(backend.sent).toHaveLength(1));
    expect(where()).toContain('panel=WATER');
  });
});

/**
 * Вихід зі шторки з незбереженим: Esc, хрестик, затемнення й Cancel питають «Discard»; чиста форма закривається без питань.
 *
 * Мутаційний доказ (вручну, відновлено): прибрати виклик `useDirtyDrawerExit` - Esc/хрестик/затемнення закривають шторку
 * без питання (валить перший, другий, третій і п'ятий тести); `onClick={close}` замість `requestClose` на Cancel - валить
 * четвертий; `closeOnEscape={!confirmingDelete}` без `&& !guarded` - Esc закриває шторку, ще й питає (валить перший).
 */
describe('Groups: вихід зі шторки з незбереженим (Discard)', () => {
  const nameField = async (): Promise<HTMLInputElement> =>
    (await screen.findByLabelText(/permits\.groups\.field\.name · English/)) as HTMLInputElement;
  const discardTitle = (): HTMLElement | null => screen.queryByText('permits.groups.drawer.discardTitle');

  async function editName(): Promise<HTMLInputElement> {
    const name = await nameField();
    fireEvent.change(name, { target: { value: 'Water 2' } });
    name.focus();

    return name;
  }

  it('Esc: питає «Discard», шторка лишається; «Keep editing» зберігає введене; Esc у підтвердженні закриває лише його', async () => {
    stub([water]);
    show('/admin/permits/groups?panel=WATER');
    const name = await editName();

    fireEvent.keyDown(name, { key: 'Escape' });
    expect(await screen.findByText('permits.groups.drawer.discardTitle')).not.toBeNull();
    expect(where()).toContain('panel=WATER');

    fireEvent.click(await screen.findByRole('button', { name: 'permits.groups.drawer.keepEditing' }));
    await waitFor(() => expect(discardTitle()).toBeNull());
    expect(where()).toContain('panel=WATER');
    expect((await nameField()).value).toBe('Water 2');

    // Esc у вкладеному підтвердженні: закривається воно, шторка лишається.
    fireEvent.keyDown(name, { key: 'Escape' });
    const cancel = await screen.findByTestId('confirm-cancel');
    fireEvent.keyDown(cancel, { key: 'Escape' });
    await waitFor(() => expect(discardTitle()).toBeNull());
    expect(where()).toContain('panel=WATER');
    expect(backend.sent).toHaveLength(0);
  });

  it('«Discard» закриває шторку без запису', async () => {
    stub([water]);
    show('/admin/permits/groups?panel=WATER');
    const name = await editName();

    fireEvent.keyDown(name, { key: 'Escape' });
    fireEvent.click(await screen.findByTestId('confirm-verb'));

    await waitFor(() => expect(where()).not.toContain('panel='));
    expect(backend.sent).toHaveLength(0);
  });

  it('хрестик у шапці питає так само', async () => {
    stub([water]);
    show('/admin/permits/groups?panel=WATER');
    await editName();

    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.close' }));

    expect(await screen.findByText('permits.groups.drawer.discardTitle')).not.toBeNull();
    expect(where()).toContain('panel=WATER');
  });

  it('Cancel у підвалі питає так само', async () => {
    stub([water]);
    show('/admin/permits/groups?panel=WATER');
    await editName();

    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.cancel' }));

    expect(await screen.findByText('permits.groups.drawer.discardTitle')).not.toBeNull();
    expect(where()).toContain('panel=WATER');
  });

  it('клік по затемненню питає так само', async () => {
    stub([water]);
    show('/admin/permits/groups?panel=WATER');
    await editName();

    const overlay = document.querySelector('.mantine-Drawer-overlay');
    expect(overlay, 'на вузькому екрані є затемнення').not.toBeNull();
    fireEvent.click(overlay as Element);

    expect(await screen.findByText('permits.groups.drawer.discardTitle')).not.toBeNull();
    expect(where()).toContain('panel=WATER');
  });

  it('без правок: Esc, хрестик і Cancel закривають одразу, без питання', async () => {
    stub([water]);
    show('/admin/permits/groups?panel=WATER');
    const name = await nameField();
    name.focus();

    fireEvent.keyDown(name, { key: 'Escape' });
    await waitFor(() => expect(where()).not.toContain('panel='));
    expect(discardTitle()).toBeNull();
    cleanup();

    show('/admin/permits/groups?panel=WATER');
    await nameField();
    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.close' }));
    await waitFor(() => expect(where()).not.toContain('panel='));
    expect(discardTitle()).toBeNull();
    cleanup();

    show('/admin/permits/groups?panel=WATER');
    await nameField();
    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.cancel' }));
    await waitFor(() => expect(where()).not.toContain('panel='));
    expect(discardTitle()).toBeNull();
  });

  it('нова група: введений код робить форму «брудною», порожня закривається без питання', async () => {
    stub([]);
    show('/admin/permits/groups?panel=_new');
    const code = (await screen.findByLabelText(/^permits\.groups\.field\.code/)) as HTMLInputElement;
    fireEvent.change(code, { target: { value: 'WATER' } });

    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.cancel' }));
    expect(await screen.findByText('permits.groups.drawer.discardTitle')).not.toBeNull();
    fireEvent.click(await screen.findByTestId('confirm-verb'));
    await waitFor(() => expect(where()).not.toContain('panel='));
    cleanup();

    show('/admin/permits/groups?panel=_new');
    await screen.findByLabelText(/^permits\.groups\.field\.code/);
    fireEvent.click(screen.getByRole('button', { name: 'permits.groups.drawer.cancel' }));
    await waitFor(() => expect(where()).not.toContain('panel='));
  });
});
