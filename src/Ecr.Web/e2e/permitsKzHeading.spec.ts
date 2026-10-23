import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { axeViolations } from './axeRule';
import { CreatedPermitTypes } from './permitTypeCleanup';

/**
 * P3-N8 / P3-N9 у розділі Permits на живому стенді:
 * - kz: на жодному екрані й у календарі діалогу немає сирого «M10» / «2026 M10 2» (ICU без назв місяців `kk`);
 * - heading-order: справжній axe-core у браузері не знаходить пропущених рівнів заголовків на екранах розділу.
 *
 * Дані: тип/дозвіл через API (`e2e-admin`) з префіксом `E2E_`; чинний дозвіл сервер не віддає на видалення,
 * тут лишається лише чернетка, яку прибирає `afterEach`. Мова профілю вмикається налаштуванням і знімається в `finally`.
 *
 * Запуск (стенд - `tools/e2e-stand.ps1`): `npx playwright test -g "Permits kz heading"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилась').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function body<T>(response: APIResponse, what: string): Promise<T> {
  expect(response.status(), `${what}: ${await response.text()}`).toBeLessThan(300);

  return (await response.json()) as T;
}

const pad = (value: number): string => String(value).padStart(2, '0');
const isoDate = (date: Date): string => `${String(date.getFullYear())}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;

const column = (code: string, over: Record<string, unknown>): Record<string, unknown> => ({
  code,
  nameL10n: { en: code },
  dataType: 'String',
  unitId: null,
  required: false,
  key: false,
  group: null,
  precision: null,
  scale: null,
  listScope: null,
  listRegistryCode: null,
  siblingTableCode: null,
  choices: null,
  order: 0,
  ...over,
});

async function publishType(page: Page, tracked: CreatedPermitTypes, name: string): Promise<string> {
  const code = tracked.track(`E2E_KZH_${String(Date.now())}${String(Math.floor(Math.random() * 1000))}`);
  const made = await body<{ rowVersion: string }>(
    await page.request.post('/api/v1/permit-types', { data: { code, category: 'Other', nameL10n: { en: name } } }),
    'створення типу',
  );
  const saved = await body<{ rowVersion: string }>(
    await page.request.put(`/api/v1/permit-types/${code}`, {
      data: {
        category: 'Other',
        nameL10n: { en: name },
        rowVersion: made.rowVersion,
        rules: [],
        tables: [
          {
            code: 'Items',
            nameL10n: { en: 'Items' },
            order: 1,
            rules: [],
            columns: [column('Name', { key: true, required: true, order: 1 })],
          },
        ],
      },
    }),
    'заміна чернетки типу',
  );
  await body(
    await page.request.post(`/api/v1/permit-types/${code}/publish`, {
      data: { reason: 'E2E: kz heading permit type', rowVersion: saved.rowVersion },
    }),
    'публікація типу',
  );

  return code;
}

async function createPermit(page: Page, typeCode: string, number: string): Promise<number> {
  const year = new Date().getFullYear();
  const permit = await body<{ id: number }>(
    await page.request.post('/api/v1/permits', {
      data: {
        typeCode,
        number,
        location: 'E2E kz heading site',
        issueDate: null,
        validFrom: isoDate(new Date(year + 1, 9, 10)),
        validTo: isoDate(new Date(year + 2, 9, 10)),
        reportingFlags: null,
      },
    }),
    'створення дозволу',
  );

  return permit.id;
}

/** Ураження ICU: `kk` «підтримується», але назв місяців немає - Chrome на деяких збірках дає «M10» / «2026 M10 2». */
const BrokenKkIcu = (): void => {
  const Native = Intl.DateTimeFormat;
  const Patched = function (this: unknown, locales?: string | string[], options?: Intl.DateTimeFormatOptions) {
    const real = new Native(locales, options);
    const first = Array.isArray(locales) ? locales[0] : locales;
    if (first === undefined || !first.toLowerCase().startsWith('kk')) return real;
    const originalFormat = real.format.bind(real);

    const fake = Object.create(real) as Intl.DateTimeFormat;
    Object.defineProperty(fake, 'format', {
      value: (value?: Date | number): string => {
        const at = new Date(value ?? Date.now());
        if (options?.dateStyle === 'medium') return `${String(at.getFullYear())} M${String(at.getMonth() + 1)} ${String(at.getDate())}`;
        if (options?.month === 'long' && options.year === undefined) return `M${String(at.getUTCMonth() + 1)}`;

        return originalFormat(value);
      },
    });

    return fake;
  } as unknown as typeof Intl.DateTimeFormat;
  // «kk» оголошується підтримуваною, хоча даних про місяці немає.
  Patched.supportedLocalesOf = (locales, options): string[] => {
    const asked = ([] as unknown[]).concat(locales ?? []).map(String);
    const kk = asked.filter((item) => item.toLowerCase().startsWith('kk'));

    return [...new Set([...Native.supportedLocalesOf(locales, options), ...kk])];
  };
  Object.defineProperty(Intl, 'DateTimeFormat', { value: Patched, configurable: true, writable: true });
  // `formatLocale()` перевіряє підтримку саме через `Intl.NumberFormat.supportedLocalesOf`.
  const nativeNumbers = Intl.NumberFormat.supportedLocalesOf.bind(Intl.NumberFormat);
  Intl.NumberFormat.supportedLocalesOf = (locales, options): string[] => {
    const kk = ([] as unknown[]).concat(locales ?? []).map(String).filter((item) => item.toLowerCase().startsWith('kk'));

    return [...new Set([...nativeNumbers(locales, options), ...kk])];
  };
};

const RawMonth = /\bM\d{1,2}\b/;

test.describe('Permits kz heading', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  const types = new CreatedPermitTypes();
  const drafts: number[] = [];

  test.afterEach(async ({ page }) => {
    for (const id of drafts.splice(0)) {
      const removed = await page.request.delete(`/api/v1/permits/${String(id)}`);
      expect([204, 404, 409], `прибирання дозволу ${String(id)}: ${removed.status()}`).toContain(removed.status());
    }
    await types.cleanup(page);
  });

  for (const broken of [false, true]) {
    test(`kz${broken ? ' (ICU без назв місяців)' : ''}: екрани дозволів і календар без сирого «M10»`, async ({ page }) => {
      test.setTimeout(240_000);
      await page.setViewportSize({ width: 1440, height: 900 });
      if (broken) await page.addInitScript(BrokenKkIcu);
      await signIn(page);

      const typeCode = await publishType(page, types, `E2E_ kz heading ${String(Date.now())}`);
      const id = await createPermit(page, typeCode, `E2E-KZH-${String(Date.now())}`);
      drafts.push(id);

      const setLanguage = async (value: string | null): Promise<void> => {
        const response =
          value === null
            ? await page.request.delete('/api/v1/me/preferences/language')
            : await page.request.put('/api/v1/me/preferences/language', {
                data: JSON.stringify(value),
                headers: { 'Content-Type': 'application/json' },
              });
        expect([200, 204, 404], `мова профілю: ${String(response.status())}`).toContain(response.status());
      };

      await setLanguage('kz');
      try {
        await page.goto(`/admin/permits/${String(id)}?dialog=activate`);
        const dialog = page.getByRole('dialog');
        await expect(dialog).toBeVisible({ timeout: 30_000 });
        await expect(page.locator('html')).toHaveAttribute('lang', /kk|kz/i, { timeout: 15_000 });
        const text = await dialog.innerText();
        if (broken) {
          const probe = await page.evaluate(() => new Intl.DateTimeFormat('kk', { dateStyle: 'medium' }).format(new Date(2026, 9, 2)));
          expect(probe, 'емуляція зламаного ICU не діє').toMatch(RawMonth);
        }
        expect(text, `діалог Activate: ${text}`).not.toMatch(RawMonth);
        expect(text, text).not.toMatch(/\bOct(ober)?\b/);
        await page.keyboard.press('Escape');
        await expect(dialog).toBeHidden({ timeout: 15_000 });

        for (const path of [`/admin/permits/${String(id)}`, '/admin/permits']) {
          await page.goto(path);
          await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 30_000 });
          const main = await page.locator('main, body').first().innerText();
          expect(main, `${path}: ${main.slice(0, 400)}`).not.toMatch(RawMonth);
        }

        // Календар поля «Valid from» у майстрі New permit: шапка місяця й список місяців - словами kk.
        await page.goto('/admin/permits');
        await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 30_000 });
        await page.getByRole('button').filter({ hasText: /^(New permit|Жаңа рұқсат)/ }).first().click();
        const wizard = page.getByRole('dialog');
        await expect(wizard).toBeVisible({ timeout: 15_000 });
        await wizard.getByRole('radio').first().check();
        await wizard.getByTestId('wizard-next').click();
        // Назви полів - казахські, тож поле дати знаходимо за календарем: клікаємо текстові поля, доки не відкриється.
        const calendar = page.locator('[data-dates-dropdown]');
        const fields = wizard.getByRole('textbox');
        await expect(fields.first()).toBeVisible({ timeout: 10_000 });
        for (let index = 0; index < (await fields.count()); index += 1) {
          await fields.nth(index).click();
          if (await calendar.isVisible().catch(() => false)) break;
          await page.waitForTimeout(300);
        }
        await expect(calendar).toBeVisible({ timeout: 10_000 });
        const header = await calendar.innerText();
        expect(header, `календар: ${header}`).not.toMatch(RawMonth);
      } finally {
        await setLanguage(null);
      }
    });
  }

  test('heading-order: справжній axe не знаходить пропущених рівнів на екранах розділу', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const typeCode = await publishType(page, types, `E2E_ heading ${String(Date.now())}`);
    const id = await createPermit(page, typeCode, `E2E-HDG-${String(Date.now())}`);
    drafts.push(id);

    const failures: string[] = [];
    const sweep = async (label: string): Promise<void> => {
      const found = await axeViolations(page, 'heading-order');
      if (found.length > 0) failures.push(`${label}: ${found.join(' | ')}`);
    };

    const screens = [
      '/admin/permits',
      `/admin/permits/${String(id)}`,
      '/admin/permits/types',
      `/admin/permits/types/${typeCode}`,
      '/admin/permits/lists',
      '/admin/permits/groups',
      '/admin/permits/groups?panel=_new',
      '/admin/permits/bindings',
    ];

    for (const path of screens) {
      await page.goto(path);
      await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible({ timeout: 30_000 });
      await sweep(path);

      const tabs = page.getByRole('tab');
      const count = await tabs.count();
      for (let index = 0; index < count; index += 1) {
        const tab = tabs.nth(index);
        const name = (await tab.innerText()).trim();
        await tab.click();
        await page.waitForTimeout(400);
        await sweep(`${path} [вкладка ${name}]`);
      }
    }

    expect(failures, failures.join('\n')).toEqual([]);
  });
});
