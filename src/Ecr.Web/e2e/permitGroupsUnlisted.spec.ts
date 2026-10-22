import { expect, test, type APIResponse, type Page } from '@playwright/test';

/**
 * Permits (групи типів): поточна ціль Lookup, якої немає в переліку цілей («unlisted»), показується НАЗВОЮ з
 * `GET /registries/by-id/{id}`, а не порожнім полем. РЕАЛЬНИЙ сервер, без підміни відповідей.
 *
 * Сценарій: група (API) -> поле шапки Lookup на неї (API) -> група деактивується (API) -> поле в чернетці версії
 * відкривається в UI: поточна ціль - «Назва (PRMG_КОД)», неактивна - з позначкою «inactive»; якщо сервер ще віддає
 * неактивну групу в переліку цілей - вона показана звичайною опцією (тоді «unlisted» не потрібен, і це теж чесний результат).
 * N/5. Запуск (стенд - `tools/e2e-stand.ps1`): `-Grep "Permit groups unlisted"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${response.status()} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

test.describe('Permit groups unlisted', () => {
  test('неактивна група як поточна ціль поля шапки: назва з by-id, а не порожнє поле (N/5)', async ({ page }) => {
    test.setTimeout(600_000);
    await signIn(page);

    for (let run = 1; run <= 5; run += 1) {
      const stamp = `${String(Date.now())}${String(run)}`;
      const code = `E2E_PGU_${stamp}`;
      const name = `E2E retired group ${stamp}`;
      const description = `Retired e2e list ${stamp}`;
      const created = await ok<{ registryDefId: number; rowVersion: string; registryCode: string }>(
        await page.request.post('/api/v1/permit-groups', {
          data: { code, nameL10n: { en: name }, descriptionL10n: { en: description }, typeCodes: null },
        }),
        'група',
      );

      const template = await ok<{ templateId: number }>(
        await page.request.post('/api/v1/templates', { data: { code: `E2EPgu${stamp}`, nameL10n: { en: `E2E unlisted ${stamp}` } } }),
        'шаблон',
      );
      const version = await ok<{ versionId: number }>(
        await page.request.post(`/api/v1/templates/${template.templateId}/versions`, { data: { versionNumber: '1.0.0.0', cloneFromVersionId: null } }),
        'версія',
      );
      await ok(
        await page.request.put(`/api/v1/template-versions/${version.versionId}/header-fields/PermitNo`, {
          data: { dataType: 'Lookup', isRequired: false, labelL10n: { en: 'Permit number' }, lookupRegistryDefId: created.registryDefId, ordinal: 0 },
        }),
        'поле шапки на групу',
      );
      await ok(
        await page.request.put(`/api/v1/permit-groups/${code}`, {
          data: { nameL10n: { en: name }, descriptionL10n: { en: description }, isActive: false, rowVersion: created.rowVersion },
        }),
        'деактивація групи',
      );

      // by-id віддає неактивну групу з назвою, описом і kind (реальний сервер).
      const target = await ok<{ code: string; kind: string; isActive: boolean; nameL10n: { values?: Record<string, string> } }>(
        await page.request.get(`/api/v1/registries/by-id/${String(created.registryDefId)}`),
        'by-id',
      );
      expect(target.code).toBe(`PRMG_${code}`);
      expect(target.kind).toBe('PermitGroup');
      expect(target.isActive, `прогін ${run}: by-id isActive`).toBe(false);

      await page.goto(`/admin/templates/${String(template.templateId)}/versions/${String(version.versionId)}`);
      await page.getByRole('row', { name: /Permit number \(PermitNo\)/ }).getByRole('button', { name: 'Edit' }).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible({ timeout: 30_000 });
      const registry = dialog.getByRole('textbox', { name: 'Registry' });
      // Назва групи у полі (а не порожньо): або як звичайна опція, або як «unlisted» з позначкою неактивності.
      await expect(registry, `прогін ${run}: поточна ціль у полі`).toHaveValue(new RegExp(`^${name} \\(PRMG_${code}\\)`), { timeout: 30_000 });
      const value = await registry.inputValue();
      console.log(`UNLISTED run ${String(run)}: "${value}"`);
      await page.keyboard.press('Escape');
    }
  });
});
