import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, within, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { GrantsPanel } from '@/pages/admin/GrantsPanel';
import { hasUnsavedChanges } from '@/shared/ui/unsavedSources';
import type { ResourceGrantDto, RoleView } from '@/api/types';
import { withTestDefaults } from '@/test/render';

/**
 * Аудит U6 (клієнт): ресурс гранта обирається за НАЗВОЮ, чернетка не
 * губиться мовчки, підписи — не сирі коди переліків.
 *
 * ⚠ `fireEvent`, не `userEvent`: панель у продукті вантажиться ліниво, а
 * `userEvent` із `vi.mock`/лінивими імпортами тут уже давав хибні результати.
 */

// ⚠ Підписи переліків навмисно ВІДРІЗНЯЮТЬСЯ від значень: інакше тест не
// відрізнив би `t('enum.resourceKind.Sheet')` від сирого `Sheet`.
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
  'grants.pickerRegistry': 'Registry',
  'grants.pickerTemplate': 'Template',
  'grants.pickerVersion': 'Version',
  'grants.pickerSheet': 'Sheet',
  'grants.pickerTable': 'Table',
  'grants.pickerColumn': 'Column',
  'grants.pickerNothingFound': 'Nothing found',
  'grants.pickerLoadFailed': 'Load failed',
  'grants.projectsScopeHint': 'Scope hint',
  'grants.unsaved': 'Unsaved changes',
  'grants.pickResourceFirst': 'Pick a resource first',
  'grants.discardTitle': 'Discard changes of {role}?',
  'grants.discardText': 'Discard text',
  'grants.discardVerb': 'Discard changes',
  'enum.resourceKind.Project': 'Проєкт',
  'enum.resourceKind.Sheet': 'Аркуш',
  'enum.resourceKind.Table': 'Таблиця',
  'enum.resourceKind.Column': 'Колонка',
  'enum.resourceKind.Registry': 'Довідник',
  'enum.grantLevel.Read': 'Читання',
  'enum.grantLevel.Write': 'Запис',
  'enum.grantLevel.Submit': 'Подання',
  'enum.grantLevel.Approve': 'Погодження',
  'enum.grantLevel.Manage': 'Керування',
  'common.save': 'Save',
  'common.cancel': 'Cancel',
  'common.loading': 'Loading',
};

const Roles: RoleView[] = [
  { id: 10, code: 'Auditor', isActive: true, isBuiltIn: false, permissions: [], dangerousPermissions: [] },
  { id: 11, code: 'Operator', isActive: true, isBuiltIn: false, permissions: [], dangerousPermissions: [] },
];

const Projects = {
  items: [
    { id: 41, code: 'PRJ-A', status: 'Active', timeZoneId: 'UTC', periodKind: 'Month', currentPeriodId: null, periodCount: 0 },
    { id: 42, code: 'PRJ-B', status: 'Active', timeZoneId: 'UTC', periodKind: 'Month', currentPeriodId: null, periodCount: 0 },
  ],
  nextCursor: null,
  totalCount: 2,
};

const Structure = {
  templateVersionId: 70,
  isEditable: false,
  presentationRevision: 1,
  groupRules: [],
  sheets: [
    {
      id: 500, code: 'BS', nameL10n: { values: { en: 'Balance' } }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null,
      tables: [
        {
          id: 600, code: 'T1', nameL10n: { values: { en: 'Emissions' } }, ordinal: 1, layoutKind: 'Grid', rowMode: 'Fixed', maxDynamicRows: null, rows: [],
          columns: [
            { id: 701, code: 'C1', headerL10n: { values: { en: 'Volume' } } },
            { id: 702, code: 'C2', headerL10n: { values: { en: 'Mass' } } },
          ],
        },
      ],
    },
  ],
};

let serverGrants: Record<number, ResourceGrantDto[]>;
let puts: { url: string; body: { grants: ResourceGrantDto[] } }[];
let grantGets: number;

function json(data: unknown): Response {
  return new Response(JSON.stringify(data), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(() => {
  serverGrants = { 10: [], 11: [] };
  puts = [];
  grantGets = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });

      const grants = /\/roles\/(\d+)\/grants/.exec(url);
      if (grants) {
        const roleId = Number(grants[1]);
        if (init?.method === 'PUT') {
          const body = JSON.parse(String(init.body)) as { grants: ResourceGrantDto[] };
          puts.push({ url, body });
          serverGrants[roleId] = body.grants;
          return new Response(null, { status: 204 });
        }
        grantGets += 1;
        return json(serverGrants[roleId] ?? []);
      }
      if (url.includes('/api/v1/projects')) return json(Projects);
      if (url.includes('/api/v1/templates/7/versions')) {
        return json({ items: [{ id: 70, version: '1.0', status: 'Published', publishedAt: null, clonedFromVersionId: null, presentationRevision: 1 }], nextCursor: null, totalCount: 1 });
      }
      if (url.includes('/api/v1/templates')) return json({ items: [{ id: 7, code: 'TPL-HSE', versionCount: 1 }], nextCursor: null, totalCount: 1 });
      if (url.includes('/template-versions/70/structure')) return json(Structure);

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

/** Відкриває `Select` за доступним ім'ям і клікає опцію (опції — у порталі). */
async function choose(field: string, option: string): Promise<void> {
  fireEvent.click(await screen.findByRole('textbox', { name: field }));
  fireEvent.click(await screen.findByRole('option', { name: option }));
}

async function openRole(code: string): Promise<void> {
  await choose('Role', code);
  await screen.findByText('Empty');
}

describe('GrantsPanel: вибір ресурсу за назвою (U6)', () => {
  it('обраний за кодом проєкт іде в PUT своїм id, а не сусіднім', async () => {
    await renderPanel();
    await openRole('Auditor');

    fireEvent.click(screen.getByRole('button', { name: 'Add grant' }));
    await choose('Project 1', 'PRJ-B');

    // Назва видна одразу, до збереження.
    expect(await screen.findByText('PRJ-B')).not.toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]?.url).toContain('/roles/10/grants');
    expect(puts[0]?.body.grants).toEqual([
      expect.objectContaining({ resourceKind: 'Project', resourceId: 42, level: 'Read', isDeny: false }),
    ]);
  });

  it('колонка обирається каскадом шаблон → версія → аркуш → таблиця → колонка', async () => {
    await renderPanel();
    await openRole('Auditor');

    fireEvent.click(screen.getByRole('button', { name: 'Add grant' }));
    await choose('Resource kind 1', 'Колонка');
    await choose('Template 1', 'TPL-HSE');
    await choose('Version 1', '1.0');
    await choose('Sheet 1', 'Balance (BS)');
    await choose('Table 1', 'Emissions (T1)');
    await choose('Column 1', 'Mass (C2)');

    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]?.body.grants).toEqual([
      expect.objectContaining({ resourceKind: 'Column', resourceId: 702 }),
    ]);
  });

  it('рядок без обраного ресурсу не зберігається (грант «ні на що»)', async () => {
    await renderPanel();
    await openRole('Auditor');

    fireEvent.click(screen.getByRole('button', { name: 'Add grant' }));

    expect(await screen.findByText('Pick a resource first')).not.toBeNull();
    expect((screen.getByRole('button', { name: 'Save' }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('GrantsPanel: незбережена чернетка (U6)', () => {
  it('перемикання ролі зі зміненою чернеткою питає; «Cancel» лишає чернетку й роль', async () => {
    await renderPanel();
    await openRole('Auditor');

    fireEvent.click(screen.getByRole('button', { name: 'Add grant' }));
    await choose('Project 1', 'PRJ-A');
    expect(hasUnsavedChanges()).toBe(true);

    await choose('Role', 'Operator');

    const dialog = await screen.findByRole('dialog', { name: 'Discard changes of Auditor?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(screen.getByText('PRJ-A')).not.toBeNull();
    expect((screen.getByRole('textbox', { name: 'Role' }) as HTMLInputElement).value).toBe('Auditor');
    expect(hasUnsavedChanges()).toBe(true);
  });

  it('підтвердження скидає чернетку й відкриває іншу роль', async () => {
    serverGrants[11] = [{ resourceKind: 'Project', resourceId: 41, level: 'Write', isDeny: false, resourceName: 'PRJ-A' }];
    await renderPanel();
    await openRole('Auditor');

    fireEvent.click(screen.getByRole('button', { name: 'Add grant' }));
    await choose('Project 1', 'PRJ-B');
    await choose('Role', 'Operator');

    const dialog = await screen.findByRole('dialog', { name: 'Discard changes of Auditor?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Discard changes' }));

    expect(await screen.findByText('PRJ-A')).not.toBeNull();
    expect(screen.queryByText('PRJ-B')).toBeNull();
    expect(hasUnsavedChanges()).toBe(false);
  });

  it('перезапит із сервера не затирає змінену чернетку', async () => {
    const client = await renderPanel();
    await openRole('Auditor');

    fireEvent.click(screen.getByRole('button', { name: 'Add grant' }));
    await choose('Project 1', 'PRJ-B');

    // Хтось інший тим часом змінив гранти ролі; фоновий перезапит їх приносить.
    serverGrants[10] = [{ resourceKind: 'Project', resourceId: 41, level: 'Manage', isDeny: false, resourceName: 'PRJ-A' }];
    const before = grantGets;
    await client.invalidateQueries({ queryKey: ['grants', 10] });
    await waitFor(() => expect(grantGets).toBeGreaterThan(before));
    await waitFor(() => expect(client.getQueryData(['grants', 10])).toEqual(serverGrants[10]));

    expect(screen.getByText('PRJ-B')).not.toBeNull();
    expect(screen.queryByText('PRJ-A')).toBeNull();
    expect(screen.getByTestId('grants-unsaved')).not.toBeNull();
  });

  it('незмінену чернетку перезапит оновлює', async () => {
    const client = await renderPanel();
    await openRole('Auditor');

    serverGrants[10] = [{ resourceKind: 'Project', resourceId: 41, level: 'Manage', isDeny: false, resourceName: 'PRJ-A' }];
    await client.invalidateQueries({ queryKey: ['grants', 10] });

    expect(await screen.findByText('PRJ-A')).not.toBeNull();
    expect(hasUnsavedChanges()).toBe(false);
  });
});

describe('GrantsPanel: підписи переліків (U6)', () => {
  it('вид ресурсу й рівень доступу — з каталогу, а не сирі коди', async () => {
    serverGrants[10] = [{ resourceKind: 'Sheet', resourceId: 500, level: 'Read', isDeny: false, resourceName: 'BS' }];
    await renderPanel();
    await choose('Role', 'Auditor');
    await screen.findByText('BS');

    expect((screen.getByRole('textbox', { name: 'Resource kind 1' }) as HTMLInputElement).value).toBe('Аркуш');
    expect((screen.getByRole('textbox', { name: 'Level 1' }) as HTMLInputElement).value).toBe('Читання');

    fireEvent.click(screen.getByRole('textbox', { name: 'Level 1' }));
    const levels = (await screen.findAllByRole('option')).map((o) => o.textContent);
    expect(levels).toEqual(['Читання', 'Запис', 'Подання', 'Погодження', 'Керування']);
  });
});
