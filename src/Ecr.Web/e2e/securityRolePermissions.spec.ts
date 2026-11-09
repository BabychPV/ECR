import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test';

/**
 * (v) human 08.11 («чому я не можу редагувати роль?» → «так», D-470): custom role permissions are editable — impact
 * preview first, a separate confirmation for dangerous permissions, audit, 409 on a concurrent edit; built-in roles
 * stay locked.
 *
 * On the REAL stand (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep "Role permissions edit"`): a temporary
 * custom role `E2ERP<stamp>` with one permission and a temporary user holding it. `afterAll` strips the user's
 * roles (users cannot be deleted by API) and deletes the role.
 * ⛔ Without a stand the file FAILS (`beforeAll`), it is not skipped (honesty-guard).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const Role = `E2ERP${Stamp}`;
const Holder = { user: `e2e-rp-${Stamp.toLowerCase()}`, display: `E2E role holder ${Stamp}`, issued: 'E2E-RolePerm-2026!' };

interface Permission {
  readonly code: string;
  readonly group: string;
  readonly isDangerous: boolean;
}

const groupOf = new Map<string, string>();

let roleId = 0;
let userId = 0;
let base = '';
let extra = '';
let dangerous = '';

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function login(api: APIRequestContext, user: { user: string; password: string }): Promise<void> {
  const response = await api.post('/api/v1/login/local', { data: { userName: user.user, password: user.password } });
  expect(response.ok(), `вхід ${user.user}: ${String(response.status())}`).toBe(true);
}

async function signIn(page: Page, user: { user: string; password: string }): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(user.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function permissionsOf(api: APIRequestContext): Promise<string[]> {
  const roles = await ok<{ id: number; permissions: string[]; dangerousPermissions: string[] }[]>(await api.get('/api/v1/roles'), 'ролі');
  const mine = roles.find((r) => r.id === roleId);

  return [...new Set([...(mine?.permissions ?? []), ...(mine?.dangerousPermissions ?? [])])].sort();
}

test.describe('Role permissions edit', () => {
  test.describe.configure({ mode: 'serial', timeout: 180_000 });
  let admin: APIRequestContext;

  test.beforeAll(async ({ playwright }, info) => {
    const baseURL = info.project.use.baseURL;
    admin = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    await login(admin, Admin);

    const catalog = await ok<Permission[]>(await admin.get('/api/v1/permissions'), 'каталог прав');
    for (const p of catalog) groupOf.set(p.code, p.group);
    const safe = catalog.filter((p) => !p.isDangerous && p.code.startsWith('Document.'));
    base = safe[0]?.code ?? '';
    extra = safe[1]?.code ?? '';
    dangerous = catalog.find((p) => p.isDangerous && !p.code.startsWith('Security.'))?.code ?? '';
    expect([base, extra, dangerous].every((code) => code !== ''), 'каталог прав стенда').toBe(true);

    const role = await ok<{ roleId?: number; id?: number }>(
      await admin.post('/api/v1/roles', { data: { code: Role, nameL10n: { en: `E2E role permissions ${Stamp}` }, permissionCodes: [base] } }),
      'тимчасова роль',
    );
    roleId = role.roleId ?? role.id ?? 0;
    expect(roleId, 'id тимчасової ролі').toBeGreaterThan(0);

    const created = await ok<{ userId?: number; id?: number }>(
      await admin.post('/api/v1/users', {
        data: { userName: Holder.user, provider: 'Local', sid: null, displayName: Holder.display, initialPassword: Holder.issued, roleCodes: [Role] },
      }),
      `користувач ${Holder.user}`,
    );
    userId = created.userId ?? created.id ?? 0;
  });

  test.afterAll(async () => {
    const problems: string[] = [];
    if (userId !== 0) {
      const response = await admin.put(`/api/v1/users/${String(userId)}/roles`, { data: { roleCodes: [] } });
      if (response.status() >= 300) problems.push(`user ${String(userId)}: ${String(response.status())}`);
    }
    if (roleId !== 0) {
      const response = await admin.delete(`/api/v1/roles/${String(roleId)}`);
      if (response.status() >= 300 && response.status() !== 404) problems.push(`role: ${String(response.status())} ${(await response.text()).slice(0, 300)}`);
    }
    await admin.dispose();
    expect(problems, 'прибирання стенда').toEqual([]);
  });

  test('Role permissions edit: impact preview, dangerous confirmation, save, 409 on a concurrent edit', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, Admin);
    await page.goto(`/admin/security?role=${Role}`);

    const detail = page.getByTestId('role-detail');
    await expect(detail.getByRole('heading', { level: 2 })).toBeVisible({ timeout: 30_000 });

    // v1: custom role — «Edit permissions»; no built-in lock banner.
    await expect(page.getByTestId('role-builtin-banner')).toHaveCount(0);
    await page.getByTestId('role-edit-permissions').click();

    // v2/v3: add a safe and a dangerous permission, review the impact.
    for (const code of [extra, dangerous]) {
      const row = detail.locator(`[data-permission="${code}"]`);
      if (!(await row.isVisible())) await detail.locator(`[data-permission-group="${groupOf.get(code) ?? ''}"] button`).first().click();
      await row.getByRole('checkbox').check();
    }
    const dirty = page.getByTestId('role-permissions-dirty');
    await expect(dirty).toBeVisible();
    await dirty.getByRole('button').last().click();

    const impact = page.getByTestId('role-impact');
    await expect(impact).toBeVisible({ timeout: 30_000 });
    await expect(impact.locator(`[data-impact-user="${Holder.user}"]`), 'носій ролі у перегляді впливу').toBeVisible();
    await expect(impact.getByTestId('role-impact-changes')).toContainText(extra);

    const save = impact.getByRole('button', { name: /Save permissions|Сохранить права|Құқықтарды сақтау/i });
    await expect(save, 'небезпечне право без окремого підтвердження').toBeDisabled();

    // v6: someone else edits the role meanwhile — the save is a clear 409, never a 500.
    const preview = await ok<{ version: string }>(
      await admin.post(`/api/v1/roles/${String(roleId)}/permissions/preview`, { data: { permissionCodes: [base] } }),
      'перегляд з API',
    );
    await ok(
      await admin.put(`/api/v1/roles/${String(roleId)}/permissions`, {
        headers: { 'If-Match': `"${preview.version}"` },
        data: { permissionCodes: [base, extra], confirmDangerous: false },
      }),
      'чужа правка',
    );

    await impact.getByTestId('role-dangerous-confirm').getByRole('checkbox').check();
    await expect(save).toBeEnabled();
    const conflict = page.waitForResponse((r) => r.request().method() === 'PUT' && r.url().endsWith(`/roles/${String(roleId)}/permissions`));
    await save.click();
    expect((await conflict).status(), 'друга правка з тієї самої версії').toBe(409);
    await expect(impact.getByTestId('role-permissions-conflict')).toBeVisible();

    // Reload and redo: the save now passes.
    await impact.getByTestId('role-permissions-conflict').getByRole('button').click();
    await expect(impact).toBeHidden();
    await page.reload();
    await page.getByTestId('role-edit-permissions').click();
    const row = detail.locator(`[data-permission="${dangerous}"]`);
    if (!(await row.isVisible())) await detail.locator(`[data-permission-group="${groupOf.get(dangerous) ?? ''}"] button`).first().click();
    await row.getByRole('checkbox').check();
    await page.getByTestId('role-permissions-dirty').getByRole('button').last().click();
    await expect(impact).toBeVisible({ timeout: 30_000 });
    await impact.getByTestId('role-dangerous-confirm').getByRole('checkbox').check();
    const saved = page.waitForResponse((r) => r.request().method() === 'PUT' && r.url().endsWith(`/roles/${String(roleId)}/permissions`));
    await save.click();
    expect((await saved).status()).toBe(204);
    await expect(impact).toBeHidden({ timeout: 30_000 });

    expect(await permissionsOf(admin)).toEqual([base, dangerous, extra].sort());
  });
});
