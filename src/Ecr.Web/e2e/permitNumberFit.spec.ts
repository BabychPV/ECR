import { expect, test, type Page } from '@playwright/test';

/**
 * Числа в таблиці дозволу видно цілими (людина 25.11, LIVE: «числа в пермітах не вистачає місця»): на 1440 px
 * таблиця лімітів у формі «Emission limits» — код, чотири довгі текстові стовпці (назви майданчика ~140 знаків, як у
 * KZ06VCZ14825472) і три десяткові. Кожна числова комірка тіла має бути (1) не обрізана всередині
 * (`scrollWidth ≤ clientWidth`) і (2) цілком у видимій частині прокручуваної області — текстові стовпці вужчі, а хвіст
 * числових закріплено справа.
 *
 * Тип і дозвіл заводяться через API входом `e2e-admin`; рядки — вставкою з Excel, як це робить людина.
 * Запуск: `npx playwright test permitNumberFit.spec.ts` (стенд — `tools/e2e-stand.ps1`).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

function column(code: string, name: string, order: number, over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    code,
    nameL10n: { en: name },
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

const Decimal = { dataType: 'Decimal', precision: 28, scale: 16 };

const SiteEn =
  'Environmental Protection Chapter to the "Development of the Kashagan Field. Capacity Expansion to 450 Thousand Barrels per Day at the Offshore Complex"';
const SiteRu =
  'Раздел «Охрана окружающей среды» к Рабочему Проекту «Разработка месторождения Кашаган. Наращивание производительности до 450 тыс. баррелей/сутки на Морском комплексе»';

/** Тип з таблицею «Emission limits» (як на LIVE) і дозвіл-чернетка цього типу. */
async function seedPermit(page: Page, stamp: string): Promise<number> {
  const code = `E2E_NF_${stamp}`;
  const created = await page.request.post('/api/v1/permit-types', {
    data: { code, category: 'Air', nameL10n: { en: `E2E number fit ${stamp}` } },
  });
  expect(created.status(), `створення типу: ${await created.text()}`).toBeLessThan(300);
  const fresh = (await created.json()) as { rowVersion: string; nameL10n: Record<string, string> };

  const replaced = await page.request.put(`/api/v1/permit-types/${code}`, {
    data: {
      category: 'Air',
      nameL10n: fresh.nameL10n,
      rowVersion: fresh.rowVersion,
      rules: [],
      tables: [
        {
          code: 'Limits',
          nameL10n: { en: 'Emission limits' },
          order: 1,
          columns: [
            column('Code', 'Substance Code', 1, { key: true, required: true }),
            column('SiteEn', 'Site (En)', 2, { required: true }),
            column('SubstanceEn', 'Substance (En)', 3),
            column('SubstanceRu', 'Substance (Ru)', 4),
            column('SiteRu', 'Site (Ru)', 5),
            column('Tpy', 'Emission (t/year)', 6, { ...Decimal, required: true }),
            column('Mg', 'Emission (mg/nm3)', 7, Decimal),
            column('Gs', 'Emission (g/sec)', 8, Decimal),
          ],
          rules: [],
        },
      ],
    },
  });
  expect(replaced.status(), `заміна чернетки: ${await replaced.text()}`).toBe(200);
  const draft = (await replaced.json()) as { rowVersion: string };

  const published = await page.request.post(`/api/v1/permit-types/${code}/publish`, {
    data: { reason: 'E2E number fit type', rowVersion: draft.rowVersion },
  });
  expect(published.status(), `публікація типу: ${await published.text()}`).toBeLessThan(300);

  const permit = await page.request.post('/api/v1/permits', {
    data: {
      typeCode: code,
      number: `E2E-NF-${stamp}`,
      location: 'E2E facility',
      issueDate: '2026-01-05',
      validFrom: '2026-01-10',
      validTo: '2027-01-10',
      reportingFlags: { form300: false, form301: false, form302: false, tox: false },
    },
  });
  expect(permit.status(), `створення дозволу: ${await permit.text()}`).toBe(201);
  return ((await permit.json()) as { id: number }).id;
}

test.describe('Permit numbers fit', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  test('1440: кожна числова комірка ціла й видима поруч із довгим текстом', async ({ page }) => {
    test.setTimeout(240_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    const id = await seedPermit(page, String(Date.now()));

    await page.goto(`/admin/permits/${String(id)}`);
    await page.getByRole('tab', { name: /^Emission limits/ }).click();
    await page.getByRole('button', { name: 'Paste from Excel…' }).click();
    const paste = page.getByRole('dialog', { name: 'Paste rows into Emission limits' });
    const rows = [
      ['827', SiteEn, 'Vinyl chloride, chloroethene, chloroethylene, ethylene monochloride', 'Винилхлорид, хлорэтилен', SiteRu, '0.0000176', '0', '0.0002'],
      ['301', SiteEn, 'Nitrogen dioxide', 'Азота диоксид', SiteRu, '0.6669955', '75435.958', '0.4566289'],
      ['2735', SiteEn, 'Mineral petroleum oil', 'Масло минеральное нефтяное', SiteRu, '0.00000087', '162121.138', '0.00021'],
    ];
    await paste.getByRole('textbox', { name: 'Rows' }).fill(rows.map((row) => row.join('\t')).join('\n'));
    await paste.getByRole('button', { name: 'Check rows' }).click();
    await paste.getByRole('button', { name: 'Add 3 rows' }).click();
    await expect(page.getByRole('row', { name: /Mineral petroleum oil/ })).toBeVisible({ timeout: 15_000 });

    const region = page.getByRole('region', { name: /Emission limits/ });
    const report = await region.evaluate((el) => {
      const box = el.getBoundingClientRect();
      return Array.from(el.querySelectorAll<HTMLElement>('tbody td.num')).map((cell) => {
        const rect = cell.getBoundingClientRect();
        return {
          text: (cell.textContent ?? '').trim(),
          clipped: cell.scrollWidth > cell.clientWidth,
          outside: rect.left < box.left - 0.5 || rect.right > box.right + 0.5,
        };
      });
    });
    expect(report.length, 'числових комірок: 3 рядки × 3 стовпці').toBe(9);
    expect(report.filter((cell) => cell.clipped || cell.outside), JSON.stringify(report)).toEqual([]);
    await page.screenshot({ path: test.info().outputPath('permit-number-fit-1440.png') });
  });
});
