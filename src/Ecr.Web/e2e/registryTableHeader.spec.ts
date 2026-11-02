import { expect, test, type APIRequestContext, type APIResponse, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Таблиця вкладки дозволу (`PermitDefTable`): шапка над кожним стовпцем — дефект 02.11 (дозвіл KZ06VCZ14825472,
 * «Emission limits (44)», ~1670 px): між «Substance Code *» і «Substance (En)» шапка порожня, а комірка тіла показує
 * хвіст тексту («…Day at the Offshore Complex»). Причина: при горизонтальній прокрутці другий стовпець частково
 * заїжджає під липкий перший; його підпис (зліва в комірці) повністю ховається під першим стовпцем, а довгий текст
 * тіла видно хвостом. Фікс: вміст решти стовпців липне до правого краю першого (`--pm-first-w`).
 *
 * Що доводиться на справжньому стенді (vitest не має розкладки): на 1440 і 500 px, без прокрутки, з прокруткою «другий
 * стовпець наполовину під першим» і в кінець — кожен стовпець тіла має комірку шапки з тим самим діапазоном x (±2 px),
 * видима частина шапки належить саме їй (не рядку груп), а підпис і початок тексту тіла не сховані під першим
 * стовпцем. axe (wcag2a/aa) на таблиці у світлій і темній темі.
 *
 * Дані заводить сам спек (тип з групою стовпців, дозвіл, рядки через `entries/batch`) і прибирає в `afterAll`.
 * Запуск: `tools/e2e-stand.ps1 … -Grep "Registry table header"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Stamp = String(Date.now());
const TypeCode = `E2E_RTH_${Stamp}`;
const TabName = 'Emission limits';
const ShotsDir = process.env['ECR_E2E_SHOTS'] ?? '';
const LongName = 'Nitrogen dioxide emitted during the Working Day at the Offshore Complex';

let admin: APIRequestContext;
let permitId = 0;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${response.status()} ${body.slice(0, 1500)}`).toBeLessThan(300);
  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

function column(code: string, label: string, order: number, over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    code,
    nameL10n: { en: label },
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
    order,
    ...over,
  };
}

const Decimal = { dataType: 'Decimal', precision: 18, scale: 3, group: 'Limits by year' };

async function arrange(api: APIRequestContext): Promise<number> {
  const created = await ok<{ rowVersion: string; nameL10n: Record<string, string> }>(
    await api.post('/api/v1/permit-types', { data: { code: TypeCode, category: 'Water', nameL10n: { en: `E2E registry header ${Stamp}` } } }),
    'тип дозволу',
  );
  const replaced = await ok<{ rowVersion: string }>(
    await api.put(`/api/v1/permit-types/${TypeCode}`, {
      data: {
        category: 'Water',
        nameL10n: created.nameL10n,
        rowVersion: created.rowVersion,
        rules: [],
        tables: [
          {
            code: 'Limits',
            nameL10n: { en: TabName },
            order: 1,
            columns: [
              column('SubstanceCode', 'Substance Code', 1, { key: true, required: true }),
              column('SubstanceName', 'Name', 2),
              column('SubstanceEn', 'Substance (En)', 3),
              column('Source', 'Emission source', 4),
              column('Gs', 'Limit g/s', 5, Decimal),
              column('Ty', 'Limit t/year', 6, Decimal),
              column('Y2026', 'Limit 2026', 7, Decimal),
              column('Y2027', 'Limit 2027', 8, Decimal),
              column('Y2028', 'Limit 2028', 9, Decimal),
            ],
            rules: [],
          },
        ],
      },
    }),
    'чернетка типу',
  );
  await ok(await api.post(`/api/v1/permit-types/${TypeCode}/publish`, { data: { reason: 'E2E registry header', rowVersion: replaced.rowVersion } }), 'публікація типу');

  const permit = await ok<{ id: number }>(
    await api.post('/api/v1/permits', {
      data: {
        typeCode: TypeCode,
        number: `E2E-RTH-${Stamp}`,
        location: 'E2E facility',
        issueDate: '2026-01-05',
        validFrom: '2026-01-10',
        validTo: '2027-01-10',
        reportingFlags: { form300: false, form301: false, form302: false, tox: false },
      },
    }),
    'дозвіл',
  );
  const detail = await ok<{ tables: { code: string; registryCode: string }[] }>(await api.get(`/api/v1/permits/${String(permit.id)}`), 'картка дозволу');
  const registry = detail.tables.find((table) => table.code === 'Limits')?.registryCode;
  expect(registry, 'у дозволу немає таблиці Limits').toBeDefined();

  const items = Array.from({ length: 12 }, (_, i) => ({
    clientRowId: `r${String(i)}`,
    op: 'upsert',
    id: null,
    code: null,
    baseVersion: null,
    values: {
      PERMIT: String(permit.id),
      SubstanceCode: String(301 + i).padStart(4, '0'),
      SubstanceName: `${LongName} #${String(i + 1)}`,
      SubstanceEn: `Nitrogen dioxide ${String(i + 1)}`,
      Source: `Flare stack ${String(i + 1)} of the gas treatment unit`,
      Gs: String(0.125 + i),
      Ty: String(12.5 + i),
      Y2026: String(100 + i),
      Y2027: String(110 + i),
      Y2028: String(120 + i),
    },
  }));
  const batch = await ok<{ applied: boolean }>(
    await api.post(`/api/v1/registries/${registry ?? ''}/entries/batch?dryRun=false`, { data: { items } }),
    'рядки таблиці',
  );
  expect(batch.applied, 'рядки не записано').toBe(true);
  return permit.id;
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Тема через меню користувача: сервер зберігає вподобання й перекриває localStorage. */
async function ensureScheme(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  const html = page.locator('html');
  const menu = page.getByRole('button', { name: new RegExp(Admin.user, 'i') }).first();
  await expect(menu).toBeVisible({ timeout: 30_000 });
  if ((await html.getAttribute('data-mantine-color-scheme')) !== scheme) {
    await menu.click();
    await page.getByText(scheme === 'dark' ? 'Dark' : 'Light', { exact: true }).first().click();
    await page.keyboard.press('Escape');
  }
  await expect(html, `тема ${scheme} не застосувалась`).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
}

async function openTable(browser: Browser, width: number, scheme: 'light' | 'dark'): Promise<{ page: Page; context: BrowserContext }> {
  const context = await browser.newContext({ viewport: { width, height: 900 } });
  const page = await context.newPage();
  await page.emulateMedia({ colorScheme: scheme });
  await signIn(page);
  await ensureScheme(page, scheme);
  await page.goto(`/admin/permits/${String(permitId)}`);
  await ensureScheme(page, scheme);
  await page.getByRole('tab', { name: new RegExp(`^${TabName}`) }).click();
  await expect(page.locator('.pm-t tbody tr')).toHaveCount(12, { timeout: 30_000 });
  return { page, context };
}

/**
 * Геометрія шапки проти тіла в поточній прокрутці. Повертає список порушень (порожній = добре):
 * діапазон x шапки = діапазону x тіла; видима частина шапки (правіше за перший стовпець) — саме ця шапка;
 * підпис шапки й текст тіла, якщо стовпець виступає з-під першого ширше за них, не сховані під першим стовпцем.
 */
async function headerIssues(page: Page, where: string): Promise<string[]> {
  const issues = await page.evaluate(() => {
    const out: string[] = [];
    const region = document.querySelector<HTMLElement>('.pm-tw')!;
    const view = region.getBoundingClientRect();
    const heads = Array.from(region.querySelectorAll<HTMLElement>('thead tr.pm-cols > *'));
    const rows = Array.from(region.querySelectorAll<HTMLElement>('tbody tr'));
    const firstRight = heads[0]!.getBoundingClientRect().right;
    const owns = (x: number, y: number, cell: HTMLElement): boolean => {
      const hit = document.elementFromPoint(x, y);
      return hit !== null && cell.contains(hit);
    };
    const body = rows[0]!;
    const cells = Array.from(body.children) as HTMLElement[];
    if (cells.length !== heads.length) out.push(`шапок ${String(heads.length)}, комірок тіла ${String(cells.length)}`);
    for (let i = 1; i < Math.min(cells.length, heads.length); i += 1) {
      const th = heads[i]!.getBoundingClientRect();
      const td = cells[i]!.getBoundingClientRect();
      const name = heads[i]!.textContent ?? String(i);
      if (Math.abs(th.left - td.left) > 2 || Math.abs(th.right - td.right) > 2) {
        out.push(`«${name}»: шапка x ${th.left.toFixed(0)}–${th.right.toFixed(0)}, тіло ${td.left.toFixed(0)}–${td.right.toFixed(0)}`);
      }
      const left = Math.max(th.left, firstRight) + 1;
      const right = Math.min(th.right, view.right - 16) - 1;
      if (right - left < 6) continue; // стовпець цілком поза видимою областю
      const y = th.top + th.height / 2;
      if (!owns((left + right) / 2, y, heads[i]!)) out.push(`«${name}»: видима частина шапки не належить шапці стовпця`);
      const label = heads[i]!.querySelector<HTMLElement>('.pm-stick');
      if (label !== null && th.right - firstRight > label.getBoundingClientRect().width + 4) {
        const l = label.getBoundingClientRect();
        if (l.left < firstRight - 1 || !owns(l.left + 3, l.top + l.height / 2, heads[i]!)) {
          out.push(`«${name}»: підпис шапки схований під першим стовпцем (підпис x ${l.left.toFixed(0)}, перший до ${firstRight.toFixed(0)})`);
        }
      }
      const text = cells[i]!.querySelector<HTMLElement>('.pm-stick');
      if (text !== null && td.right - firstRight > text.getBoundingClientRect().width + 4 && text.getBoundingClientRect().left < firstRight - 1) {
        out.push(`«${name}»: початок тексту тіла схований під першим стовпцем`);
      }
    }
    return out;
  });
  return issues.map((issue) => `${where}: ${issue}`);
}

async function scrollTo(page: Page, to: 'start' | 'half' | 'end'): Promise<void> {
  await page.evaluate((target) => {
    const region = document.querySelector<HTMLElement>('.pm-tw')!;
    region.scrollIntoView({ block: 'center' });
    const heads = region.querySelectorAll<HTMLElement>('thead tr.pm-cols > *');
    const first = heads[0]!;
    const second = heads[1]!;
    region.scrollLeft =
      target === 'start' ? 0 : target === 'end' ? region.scrollWidth : second.offsetLeft - first.offsetWidth + second.offsetWidth / 2;
    if (target === 'end') region.scrollTop = region.scrollHeight;
  }, to);
  await page.waitForTimeout(150);
}

async function axeTable(page: Page): Promise<string[]> {
  await page.addScriptTag({ path: 'node_modules/axe-core/axe.min.js' });
  return page.evaluate(async () => {
    const axe = (window as unknown as {
      axe: { run: (c: object, o: object) => Promise<{ violations: { id: string; nodes: { target: unknown[] }[] }[] }> };
    }).axe;
    const result = await axe.run({ include: [['.pm-tw']] }, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa'] } });
    return result.violations.flatMap((v) => v.nodes.map((n) => `${v.id}: ${String(n.target.join(' '))}`));
  });
}

async function shot(page: Page, label: string): Promise<void> {
  if (ShotsDir === '') return;
  mkdirSync(ShotsDir, { recursive: true });
  await page.locator('.pm-tw').screenshot({ path: join(ShotsDir, `registry-table-header-${label}.png`) });
}

test.describe('Registry table header', () => {
  test.describe.configure({ mode: 'serial', timeout: 240_000 });

  test.beforeAll(async ({ playwright }, info) => {
    test.setTimeout(180_000);
    const baseURL = info.project.use.baseURL;
    admin = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    const login = await admin.post('/api/v1/login/local', { data: { userName: Admin.user, password: Admin.password } });
    expect(login.ok(), `вхід ${Admin.user}: ${String(login.status())}`).toBe(true);
    permitId = await arrange(admin);
  });

  test.afterAll(async ({ browser }) => {
    test.setTimeout(120_000);
    // Тема адміністратора зберігається на сервері: повернути світлу, щоб не вплинути на інші спеки.
    const { page, context } = await openTable(browser, 1440, 'light');
    await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', 'light');
    await context.close();
    const problems: string[] = [];
    if (permitId !== 0) {
      const removed = await admin.delete(`/api/v1/permits/${String(permitId)}`);
      if (removed.status() >= 300 && removed.status() !== 404) problems.push(`permit: ${String(removed.status())} ${(await removed.text()).slice(0, 300)}`);
    }
    const type = await admin.delete(`/api/v1/permit-types/${TypeCode}`);
    if (type.status() >= 300 && ![404, 409].includes(type.status())) problems.push(`type: ${String(type.status())} ${(await type.text()).slice(0, 300)}`);
    await admin.dispose();
    expect(problems, 'прибирання').toEqual([]);
  });

  for (const width of [1440, 500]) {
    test(`Registry table header: every body column has its own visible header cell - ${String(width)} px`, async ({ browser }) => {
      const { page, context } = await openTable(browser, width, 'light');
      await expect(page.locator('.pm-t thead tr.pm-grp'), 'має бути рядок груп').toHaveCount(1);
      const overflow = await page.locator('.pm-tw').evaluate((el) => el.scrollWidth - el.clientWidth);
      expect(overflow, 'таблиця має прокручуватись по горизонталі, інакше перевірка нічого не доводить').toBeGreaterThan(100);

      const issues: string[] = [];
      for (const position of ['start', 'half', 'end'] as const) {
        await scrollTo(page, position);
        issues.push(...(await headerIssues(page, `${String(width)} px, ${position}`)));
        await shot(page, `${String(width)}-${position}`);
      }
      expect(issues).toEqual([]);
      await context.close();
    });
  }

  for (const scheme of ['light', 'dark'] as const) {
    test(`Registry table header: axe ${scheme}`, async ({ browser }) => {
      const { page, context } = await openTable(browser, 1440, scheme);
      await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
      await scrollTo(page, 'half');
      expect(await axeTable(page), `axe ${scheme}`).toEqual([]);
      await context.close();
    });
  }
});
