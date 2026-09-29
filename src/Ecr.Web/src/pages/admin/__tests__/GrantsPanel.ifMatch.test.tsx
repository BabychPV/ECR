import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { GrantsPanel } from '@/pages/admin/GrantsPanel';
import type { ResourceGrantDto, RoleView } from '@/api/types';
import { withTestDefaults } from '@/test/render';

/**
 * Гранти ролі: версія набору — `ETag` на `GET`, `If-Match` на `PUT`.
 *
 * ⛔ Доти два адміністратори однієї ролі затирали набори один одного мовчки:
 * `PUT` замінює набір ЦІЛКОМ, і від чужої правки не лишалося нічого. Сервер
 * уміє звіряти версію (`409 ECR-SEC-0409`), але екран її не слав.
 *
 * ⚠ Сервер тут — справжня модель звірки (версія лічильником), а не підставна
 * відмова: `409` виникає лише тоді, коли `If-Match` справді застарів, тож
 * тест не може позеленіти від екрана, який заголовка не шле.
 *
 * ⚠ `fireEvent`, не `userEvent` (лінива панель, див. `GrantsPanel.picker`).
 */

const Strings: Record<string, string> = {
  'security.role': 'Role',
  'grants.pickRole': 'Pick a role',
  'grants.pickRoleHint': 'Pick a role hint',
  'grants.add': 'Add grant',
  'grants.saved': 'Saved',
  'grants.kind': 'Resource kind',
  'grants.target': 'Resource',
  'grants.resourceName': 'Resolved name',
  'grants.resourceNameUnknown': 'Not found',
  'grants.level': 'Level',
  'grants.deny': 'Deny',
  'grants.remove': 'Remove',
  'grants.empty': 'Empty',
  'grants.emptyHint': 'Empty hint',
  'grants.pickerProject': 'Project',
  'grants.pickerNothingFound': 'Nothing found',
  'grants.pickerLoadFailed': 'Load failed',
  'grants.projectsCatalogHint': 'All projects hint',
  'grants.unsaved': 'Unsaved changes',
  'grants.pickResourceFirst': 'Pick a resource first',
  'grants.discardTitle': 'Discard changes of {role}?',
  'grants.discardText': 'Discard text',
  'grants.discardVerb': 'Discard changes',
  'grants.conflict': 'Someone else changed these grants',
  'err.ECR-SEC-0409': 'Security conflict',
  'common.save': 'Save',
  'common.cancel': 'Cancel',
  'common.loading': 'Loading',
};

const Roles: RoleView[] = [
  { id: 10, code: 'Auditor', isActive: true, isBuiltIn: false, permissions: [], dangerousPermissions: [] },
];

const GrantableProjects = [
  { id: 41, code: 'PRJ-A', nameL10n: { values: { en: 'Alpha field' } } },
  { id: 42, code: 'PRJ-B', nameL10n: { values: { en: 'Bravo field' } } },
];

interface Put {
  readonly ifMatch: string | null;
  readonly grants: ResourceGrantDto[];
  readonly status: number;
}

let server: { grants: ResourceGrantDto[]; version: number };
let puts: Put[];
let grantGets: number;

function tag(): string {
  return `"v${server.version}"`;
}

function json(data: unknown, init: ResponseInit = {}): Response {
  return new Response(JSON.stringify(data), {
    status: 200,
    ...init,
    headers: { 'Content-Type': 'application/json', ...(init.headers as Record<string, string> | undefined) },
  });
}

/** Модель звірки сервера: `If-Match` мусить дорівнювати поточній версії. */
function put(init: RequestInit): Response {
  const ifMatch = new Headers(init.headers).get('If-Match');
  const body = JSON.parse(String(init.body)) as { grants: ResourceGrantDto[] };

  if (ifMatch !== tag()) {
    const status = ifMatch === null ? 422 : 409;
    puts.push({ ifMatch, grants: body.grants, status });

    return json(
      ifMatch === null
        ? { title: 'x', status, errorCode: 'ECR-REQ-0422', correlationId: 'c', messageKey: 'err.ECR-REQ-0422.roleGrantsIfMatch' }
        : { title: 'x', status, errorCode: 'ECR-SEC-0409', correlationId: 'c', messageKey: 'err.ECR-SEC-0409', version: `v${server.version}` },
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    );
  }

  puts.push({ ifMatch, grants: body.grants, status: 204 });
  server = { grants: body.grants, version: server.version + 1 };

  return new Response(null, { status: 204, headers: { ETag: tag() } });
}

beforeEach(() => {
  server = { grants: [], version: 1 };
  puts = [];
  grantGets = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (url.includes('/roles/10/grants')) {
        if (init?.method === 'PUT') return put(init);
        grantGets += 1;
        return json(server.grants, { headers: { ETag: tag() } });
      }
      if (url.includes('/api/v1/security/projects')) return json(GrantableProjects);

      return json([]);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

async function renderPanel(): Promise<QueryClient> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={client}>
        <GrantsPanel roles={Roles} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

async function choose(field: string, option: string): Promise<void> {
  fireEvent.click(await screen.findByRole('textbox', { name: field }));
  fireEvent.click(await screen.findByRole('option', { name: option }));
}

async function addProject(index: number, option: string): Promise<void> {
  fireEvent.click(screen.getByRole('button', { name: 'Add grant' }));
  await choose(`Project ${index}`, option);
}

function save(): void {
  fireEvent.click(screen.getByRole('button', { name: 'Save' }));
}

/** Інший адміністратор зберіг свій набір тієї самої ролі. */
function someoneElseSaves(grants: ResourceGrantDto[]): void {
  server = { grants, version: server.version + 1 };
}

describe('GrantsPanel: If-Match на заміні грантів ролі', () => {
  it('PUT несе ETag прочитаного набору, наступний — ETag відповіді PUT', async () => {
    await renderPanel();
    await choose('Role', 'Auditor');
    await screen.findByText('Empty');

    await addProject(1, 'Alpha field (PRJ-A)');
    save();
    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]).toMatchObject({ ifMatch: '"v1"', status: 204 });

    await waitFor(() => expect(screen.queryByTestId('grants-unsaved')).toBeNull());
    await addProject(2, 'Bravo field (PRJ-B)');
    save();

    await waitFor(() => expect(puts).toHaveLength(2));
    expect(puts[1]).toMatchObject({ ifMatch: '"v2"', status: 204 });
  });

  it('409: гранти перечитано, конфлікт показано, чернетку збережено; повтор іде зі свіжою версією', async () => {
    await renderPanel();
    await choose('Role', 'Auditor');
    await screen.findByText('Empty');
    await addProject(1, 'Bravo field (PRJ-B)');

    someoneElseSaves([{ resourceKind: 'Project', resourceId: 41, level: 'Manage', isDeny: false, resourceName: 'PRJ-A' }]);
    const before = grantGets;
    save();

    expect(await screen.findByText('Someone else changed these grants')).not.toBeNull();
    expect(puts).toEqual([expect.objectContaining({ ifMatch: '"v1"', status: 409 })]);
    expect(grantGets).toBeGreaterThan(before);

    // Чернетка — робота людини: її не замінено набором іншого адміністратора.
    expect(screen.getByText('PRJ-B')).not.toBeNull();
    expect(screen.queryByText('PRJ-A')).toBeNull();
    expect(screen.getByTestId('grants-unsaved')).not.toBeNull();

    // Усвідомлене повторне збереження — вже від версії, яку перечитали.
    save();
    await waitFor(() => expect(puts).toHaveLength(2));
    expect(puts[1]).toMatchObject({ ifMatch: '"v2"', status: 204 });
    await waitFor(() => expect(screen.queryByTestId('grants-conflict')).toBeNull());
  });

  it('фоновий перезапит не підміняє версію під зміненою чернеткою — збереження дає 409, а не мовчазне затирання', async () => {
    // ⛔ Мутаційний доказ: брати `If-Match` з останньої відповіді `GET`
    // (`grants.data.etag`), а не з точки відліку чернетки, — тут `204`.
    const client = await renderPanel();
    await choose('Role', 'Auditor');
    await screen.findByText('Empty');
    await addProject(1, 'Bravo field (PRJ-B)');

    someoneElseSaves([{ resourceKind: 'Project', resourceId: 41, level: 'Manage', isDeny: false, resourceName: 'PRJ-A' }]);
    const before = grantGets;
    await client.invalidateQueries({ queryKey: ['grants', 10] });
    await waitFor(() => expect(grantGets).toBeGreaterThan(before));

    save();

    await screen.findByText('Someone else changed these grants');
    expect(puts[0]).toMatchObject({ ifMatch: '"v1"', status: 409 });
    expect(server.grants).toEqual([expect.objectContaining({ resourceId: 41, level: 'Manage' })]);
  });

  it('409 → «Discard changes» показує набір, який тепер на сервері', async () => {
    await renderPanel();
    await choose('Role', 'Auditor');
    await screen.findByText('Empty');
    await addProject(1, 'Bravo field (PRJ-B)');

    someoneElseSaves([{ resourceKind: 'Project', resourceId: 41, level: 'Manage', isDeny: false, resourceName: 'PRJ-A' }]);
    save();

    await screen.findByText('Someone else changed these grants');
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));

    expect(await screen.findByText('PRJ-A')).not.toBeNull();
    expect(screen.queryByText('PRJ-B')).toBeNull();
    expect(screen.queryByTestId('grants-unsaved')).toBeNull();
    expect(screen.queryByTestId('grants-conflict')).toBeNull();
  });
});
