import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { hasUnsavedChanges } from '@/shared/ui/unsavedSources';
import { withTestDefaults } from '@/test/render';

/**
 * Аудит U6: незбережена чернетка грантів не губиться при зміні вкладки.
 *
 * ⛔ Вкладка — у `?tab=`, а `UnsavedGuard` блокує лише зміну шляху. Доти
 * перехід «Гранти → Ролі» розмонтовував `GrantsPanel`, і доданий грант зникав
 * мовчки; повернувшись, людина бачила збережений стан без жодного слова.
 *
 * ⛔ Мутаційний доказ: повернути `{tab === 'grants' && …}` замість
 * `grantsMounted` у `SecurityPage.tsx` — тест червоний (рядка гранта немає,
 * `hasUnsavedChanges()` — `false`).
 *
 * ⚠ `fireEvent`, не `userEvent`: `GrantsPanel` вантажиться лінивим імпортом.
 */

const Strings: Record<string, string> = {
  'security.roles': 'Roles',
  'security.grants': 'Grants',
  'security.users': 'Users',
  'security.role': 'Role',
  'grants.pickRole': 'Pick a role',
  'grants.pickRoleHint': 'Pick a role hint',
  'grants.add': 'Add grant',
  'grants.kind': 'Resource kind',
  'grants.target': 'Resource',
  'grants.resourceName': 'Resolved name',
  'grants.level': 'Level',
  'grants.deny': 'Deny',
  'grants.remove': 'Remove',
  'grants.empty': 'Empty',
  'grants.emptyHint': 'Empty hint',
  'grants.pickerProject': 'Project',
  'grants.unsaved': 'Unsaved changes',
  'grants.pickResourceFirst': 'Pick a resource first',
  'common.save': 'Save',
  'common.cancel': 'Cancel',
  'common.loading': 'Loading',
};

const Roles = [
  { id: 1, code: 'Viewer', isActive: true, isBuiltIn: false, dangerousPermissions: [], permissions: ['Document.View'] },
];

const Projects = {
  items: [{ id: 41, code: 'PRJ-A', status: 'Active', timeZoneId: 'UTC', periodKind: 'Month', currentPeriodId: null, periodCount: 0 }],
  nextCursor: null,
  totalCount: 1,
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (url.includes('/me')) {
        return json({ userId: 0, userName: 'test', language: 'en', permissions: ['Security.ManageRoles'], isSimulation: false });
      }
      if (url.includes('/grants')) return json([]);
      if (url.includes('/permissions')) return json([{ code: 'Document.View', isDangerous: false }]);
      if (url.includes('/roles')) return json(Roles);
      if (url.includes('/projects')) return json(Projects);

      return json([]);
    }),
  );
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

async function choose(field: string, option: string): Promise<void> {
  fireEvent.click(await screen.findByRole('textbox', { name: field }));
  fireEvent.click(await screen.findByRole('option', { name: option }));
}

describe('SecurityPage — чернетка грантів переживає зміну вкладки (U6)', () => {
  it('додане на «Гранти» лишається після «Ролі» і назад', async () => {
    render(
      <MantineProvider theme={withTestDefaults(theme)}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <MemoryRouter initialEntries={['/admin/security?tab=grants']}>
            <SecurityPage />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    await choose('Role', 'Viewer');
    await screen.findByText('Empty');
    fireEvent.click(screen.getByRole('button', { name: 'Add grant' }));
    await choose('Project 1', 'PRJ-A');
    expect(hasUnsavedChanges()).toBe(true);

    fireEvent.click(screen.getByRole('radio', { name: 'Roles' }));
    // Вкладка справді змінилась: панелі грантів на екрані немає (`getByRole`
    // не бачить схованого).
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Add grant' })).toBeNull());
    // ⛔ Ховання — не розмонтування: сторож виходу й далі знає про чернетку.
    expect(hasUnsavedChanges()).toBe(true);

    fireEvent.click(screen.getByRole('radio', { name: 'Grants' }));
    await screen.findByRole('button', { name: 'Add grant' });

    expect((screen.getByRole('textbox', { name: 'Project 1' }) as HTMLInputElement).value).toBe('PRJ-A');
    expect(screen.getByTestId('grants-unsaved')).not.toBeNull();
  });
});
