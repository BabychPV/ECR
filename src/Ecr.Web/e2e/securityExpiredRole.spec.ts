import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test';

/**
 * P1 (human, 08.11, correlation e16184de): Security → user → add a role whose dated assignment has EXPIRED → Save gave
 * «Internal error». `PUT /users/{id}/roles` inserted a second (user, role) row next to the expired one and
 * `UQ_RoleAssignment_User` surfaced as a bare 500; the dialog showed the expired role as absent.
 *
 * On the REAL stand (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep "expired role"`): a temporary role and user
 * (`E2ERX<stamp>`), the role assigned through the API with a past `validTo`; the access dialog shows
 * «expired …, re-assign?», re-assign + Save answers 200, no error toast, and the user ends with ONE open-ended row.
 * `afterAll` strips the user's roles (users cannot be deleted by API) and deletes the role.
 * ⛔ Without a stand the file FAILS (`beforeAll`), it is not skipped (honesty-guard).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const Role = `E2ERX${Stamp}`;
const Target = { user: `e2e-rx-${Stamp.toLowerCase()}`, display: `E2E expired role ${Stamp}`, issued: 'E2E-RoleRx-2026!' };

let roleId = 0;
let userId = 0;

function isoDaysAgo(days: number): string {
  const at = new Date();
  at.setDate(at.getDate() - days);
  const pad = (part: number): string => String(part).padStart(2, '0');

  return `${String(at.getFullYear())}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`;
}

const ExpiredTo = isoDaysAgo(13);

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

test.describe('Security: expired role re-assign', () => {
  test.describe.configure({ mode: 'serial', timeout: 180_000 });
  let admin: APIRequestContext;

  test.beforeAll(async ({ playwright }, info) => {
    const baseURL = info.project.use.baseURL;
    admin = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    await login(admin, Admin);

    const role = await ok<{ roleId?: number; id?: number }>(
      await admin.post('/api/v1/roles', {
        data: { code: Role, nameL10n: { en: `E2E expired role ${Stamp}` }, permissionCodes: ['Document.View'] },
      }),
      'тимчасова роль',
    );
    roleId = role.roleId ?? role.id ?? 0;
    expect(roleId, 'id тимчасової ролі').toBeGreaterThan(0);

    const created = await ok<{ userId?: number; id?: number }>(
      await admin.post('/api/v1/users', {
        data: { userName: Target.user, provider: 'Local', sid: null, displayName: Target.display, initialPassword: Target.issued, roleCodes: [] },
      }),
      `користувач ${Target.user}`,
    );
    userId = created.userId ?? created.id ?? 0;
    expect(userId, 'id тимчасового користувача').toBeGreaterThan(0);

    // The dated assignment that has already expired — exactly the live state (LandRestricted, ValidTo 26.10).
    await ok(
      await admin.put(`/api/v1/users/${String(userId)}/roles`, {
        data: { roleCodes: [Role], validity: { [Role]: { validFrom: null, validTo: ExpiredTo } } },
      }),
      'строкова роль, що сплила',
    );
  });

  test.afterAll(async () => {
    const problems: string[] = [];
    if (userId !== 0) {
      // A dated row is not removed by an empty set: normalise it to open-ended first, then strip.
      await admin.put(`/api/v1/users/${String(userId)}/roles`, { data: { roleCodes: [Role] } });
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

  test('Security: expired role is shown as expired and re-assign saves without an error', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, Admin);
    await page.goto('/admin/security?tab=users');

    const row = page.getByRole('row').filter({ hasText: Target.user });
    // The list is cursor-paged (200 per page): load more until the temporary user shows up.
    for (let attempt = 0; attempt < 10 && (await row.count()) === 0; attempt += 1) {
      const more = page.locator('[data-users-more] button');
      if ((await more.count()) === 0) {
        await page.waitForTimeout(1_000);
        continue;
      }
      await more.click();
    }
    await expect(row, `користувача ${Target.user} немає в переліку`).toHaveCount(1, { timeout: 30_000 });

    // «Access» — the only button of the 4th cell.
    await row.getByRole('cell').nth(3).getByRole('button').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 30_000 });

    const notice = dialog.locator(`[data-role-validity="expired"][data-role-code="${Role}"]`);
    await expect(notice, 'сплила роль має бути видна, а не відсутня').toBeVisible({ timeout: 30_000 });
    await expect(notice).toContainText(/expired|сплил|истекл|аяқталды/i);
    await expect(notice).toContainText(/re-assign|призначити|назначить|тағайындау/i);

    await notice.getByRole('button').click();
    await expect(notice.getByRole('button'), 'після re-assign кнопки вже немає').toHaveCount(0);

    const saved = page.waitForResponse(
      (r) => r.request().method() === 'PUT' && r.url().endsWith(`/api/v1/users/${String(userId)}/roles`),
      { timeout: 30_000 },
    );
    await dialog.getByRole('button', { name: /^(Save|Зберегти|Сохранить|Сақтау)$/i }).click();
    const response = await saved;
    expect(response.status(), `PUT roles: ${await response.text()}`).toBe(200);

    await expect(dialog, 'діалог закривається після успішного збереження').toBeHidden({ timeout: 30_000 });
    await expect(page.getByText(/Internal error|Внутрішня помилка|Внутренняя ошибка/i)).toHaveCount(0);

    const rows = await ok<{ roleCode: string; validFrom: string | null; validTo: string | null }[]>(
      await admin.get(`/api/v1/users/${String(userId)}/role-assignments`),
      'призначення після збереження',
    );
    const mine = rows.filter((r) => r.roleCode === Role);
    expect(mine, 'рівно одне призначення ролі').toHaveLength(1);
    expect(mine[0]?.validTo ?? null, 'повторне призначення — безстрокове').toBeNull();
  });
});
