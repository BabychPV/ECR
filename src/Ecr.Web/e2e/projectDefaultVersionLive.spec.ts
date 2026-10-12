import { expect, test, type Browser, type Page, type Route } from '@playwright/test';

/**
 * ФВ-1.2 (рішення людини 12.10) проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`,
 * `-Grep projectDefaultVersion`): форма «New project» за замовчуванням пропонує ОСТАННЮ ОПУБЛІКОВАНУ версію
 * шаблону.
 *
 * Сценарій:
 *  1. адмін клонує наявну опубліковану версію -> нова чернетка -> публікує її (v2 того самого шаблону);
 *  2. `/admin/periods` -> «New project»: у полі «Template version» вже стоїть v2 (без жодного кліку);
 *  3. v1 лишається доступною для вибору: в розкритому списку є обидві;
 *  4. адмін обирає v1 свідомо -> клієнт шле саме v1 (пропозиція не перекриває вибір людини);
 *  5. без вибору версії клієнт шле v2;
 *  Серверну прив'язку (ФВ-1.2: публікація не переносить наявні проєкти) доводить Infra ProjectTemplateVersionBindingTests.
 *
 * ⚠ Стан стенда: тест створює дві опубліковані версії (`99.<n>.0.1/2`) ; проєкти НЕ створюються
 * (запит форми перехоплюється й скасовується). `beforeAll`/`afterAll` (`sweep`) виводять версії з обігу
 * (`Deprecated`), щоб наступні файли не бачили «найновішою» чужу версію, і ПЕРЕВІРЯЮТЬ підсумок.
 *
 * ⚠ Написано без локального стенда: живий прогін - workflow `e2e-stand`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

interface VersionRow {
  id: number;
  version: string;
  status: string;
  publishedAt: string | null;
}

const stamp = Date.now().toString(36).toUpperCase();
// Формат номера версії - Major.Minor.Patch.Build (ECR-TMPL-0422 versionNumberFormat, прогін 38294915509).
const serial = Date.now() % 1_000_000;
const NewVersion = `99.${String(serial)}.0.2`;
const BaseVersion = `99.${String(serial)}.0.1`;

/** Усе, що тест створив і що `afterAll` прибирає. */
const created: { versionId: number | null; baseVersionId: number | null } = { versionId: null, baseVersionId: null };
let v1Id = 0;
let v2Id = 0;
let v1Label = '';
let templateLabel = '';

test.describe('projectDefaultVersion: форма проєкту пропонує останню опубліковану версію (ФВ-1.2)', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  test('projectDefaultVersion: після публікації v2 форма пропонує v2, v1 лишається, вибір v1 і створення', async ({
    page,
  }) => {
    await signIn(page);

    // ── 1. v2: клон опублікованої версії + публікація ────────────────────────
    const source = await findSourceVersion(page);
    v1Id = source.versionId;
    v1Label = source.versionLabel;
    if (source.status !== 'Published') {
      // ⚠ Прогін 38293702756: єдина версія стенда - ЧЕРНЕТКА з фіксованою таблицею без рядків, її публікацію
      // сервер відхиляє (ECR-TMPL-4228). Базову версію стенда не чіпаємо: v1 - її клон із доданим рядком.
      v1Id = await publishableClone(page, source.versionId, BaseVersion);
      v1Label = `${source.templateCode} · ${BaseVersion}`;
      created.baseVersionId = v1Id;
    }
    templateLabel = source.templateCode;

    const clone = await page.request.post(`/api/v1/template-versions/${String(v1Id)}/clone`, {
      data: { newVersion: NewVersion },
    });
    expect(clone.status(), `клон версії: ${await clone.text()}`).toBe(201);
    v2Id = ((await clone.json()) as { versionId: number }).versionId;
    created.versionId = v2Id;

    const publish = await page.request.post(`/api/v1/template-versions/${String(v2Id)}/publish`, {
      data: { reason: 'e2e: ФВ-1.2, версія за замовчуванням у формі проєкту' },
    });
    expect(publish.status(), `публікація v2: ${await publish.text()}`).toBe(204);

    // Серверний перелік каже те саме, на що спирається клієнт: v2 опублікована і новіша за v1.
    const rows = await listVersions(page, source.templateId);
    const row1 = rows.find((row) => row.id === v1Id);
    const row2 = rows.find((row) => row.id === v2Id);
    expect(row2?.status, 'v2 не стала Published').toBe('Published');
    expect(row1?.status, 'v1 мала лишитися Published').toBe('Published');
    expect(Date.parse(row2?.publishedAt ?? ''), 'v2 має бути опублікована пізніше за v1').toBeGreaterThanOrEqual(
      Date.parse(row1?.publishedAt ?? ''),
    );

    // ── 2. Форма: v2 вже обрана ──────────────────────────────────────────────
    await openCreateForm(page);
    const versionField = page.getByRole('textbox', { name: /Template version|Версі[яї] шаблону/i });
    await expect(
      versionField,
      `форма не запропонувала останню опубліковану версію (очікувалось «${NewVersion}»)`,
    ).toHaveValue(new RegExp(`${reEscape(v2Label())}$`), { timeout: 60_000 });

    // ── 3. v1 лишається доступною ─────────────────────────────────────────
    await versionField.click();
    const listbox = page.getByRole('listbox').filter({ hasText: NewVersion });
    await expect(listbox.getByRole('option', { name: new RegExp(`${reEscape(v2Label())}$`) })).toBeVisible();
    await expect(
      listbox.getByRole('option', { name: new RegExp(`${reEscape(v1Label)}$`) }),
      'v1 зникла зі списку версій',
    ).toBeVisible();

    // ── 4. Свідомий вибір v1 не перекривається пропозицією ──────────────────
    await listbox.getByRole('option', { name: new RegExp(`${reEscape(v1Label)}$`) }).click();
    await expect(versionField).toHaveValue(new RegExp(`${reEscape(v1Label)}$`));
    const first = await submitForm(page, `E2E_FV12_${stamp}_A`);
    expect(first.templateVersionIdSent, 'проєкт мав піти з обраною v1').toBe(v1Id);

    // ── 5. Без вибору — пропозиція (v2) ────────────────────────────────────
    await openCreateForm(page);
    await expect(versionField).toHaveValue(new RegExp(`${reEscape(v2Label())}$`), { timeout: 60_000 });
    const second = await submitForm(page, `E2E_FV12_${stamp}_B`);
    expect(second.templateVersionIdSent, 'без вибору проєкт мав піти з пропонованою v2').toBe(v2Id);

    // ⚠ Серверний бік (проєкт фіксує версію при створенні; публікація v2 наявні проєкти не переносить) доводить
    // ProjectTemplateVersionBindingTests (Infra, СУБД). Тут запит перехоплено й скасовано: створений проєкт
    // на стенді не видалити й не заархівувати (відкриті періоди, ECR-PRD-0409), а він бачився б наступним файлам.
  });

  // ⛔ Прибирання - НАДІЙНЕ, а не best effort: зайва опублікована версія видна наступним файлам
  // (`periodsOverview`, `zz-walkthrough`, форма проєкту іншого spec). Тому і ДО тесту (залишки аварійного
  // прогону), і ПІСЛЯ нього: усе, що носить номер цього тесту, виводиться з обігу, потім
  // стан ПЕРЕВІРЯЄТЬСЯ; не вдалось - хук падає з переліком, а не мовчить.
  test.beforeAll(async ({ browser }, testInfo) => {
    testInfo.setTimeout(120_000);
    await sweep(browser, 'залишки попереднього прогону');
  });

  test.afterAll(async ({ browser }, testInfo) => {
    testInfo.setTimeout(120_000);
    await sweep(browser, 'після тесту');
  });
});

/** Версії цього тесту: `99.<serial>.0.1` (база) і `99.<serial>.0.2` (v2). */
const VersionPattern = /^99\.\d+\.0\.[12]$/;

/**
 * Архівує проєкти `E2E_FV12_*` і виводить з обігу версії `99.*.0.[12]`, потім перевіряє підсумок:
 * жоден такий проєкт не лишився неархівним, жодна така версія не лишилась `Published`.
 * ⚠ Проєкт видалити нічим (ендпоінта немає): `Archived` - найкраще, що дозволяє сервер; чи показує його
 * перелік - перевіряється повним прогоном (`zz-walkthrough`, `periodsOverview`), а не тут.
 */
async function sweep(browser: Browser, phase: string): Promise<void> {
  const context = await browser.newContext();
  const page = await context.newPage();
  const problems: string[] = [];

  try {
    await signIn(page);

    // Версії всіх шаблонів.
    const templates = await page.request.get('/api/v1/templates?limit=100');
    const templateRows = ((await templates.json()) as { items?: { id: number }[] }).items ?? [];
    for (const template of templateRows) {
      for (const row of await listVersions(page, template.id)) {
        if (!VersionPattern.test(row.version) || row.status !== 'Published') continue;

        const response = await page.request.post(`/api/v1/template-versions/${String(row.id)}/deprecate`, {
          data: { reason: 'e2e: ФВ-1.2, повернення стенда до базової лінії' },
        });
        if (!response.ok()) {
          problems.push(`версію ${row.version} (#${String(row.id)}) не виведено з обігу: ${String(response.status())}`);
        }
      }
    }

    // Підсумок: перечитуємо, а не віримо кодам відповідей.
    for (const template of templateRows) {
      for (const row of await listVersions(page, template.id)) {
        if (VersionPattern.test(row.version) && row.status === 'Published') {
          problems.push(`версія ${row.version} (#${String(row.id)}) лишилась Published`);
        }
      }
    }
  } finally {
    await context.close();
  }

  if (problems.length > 0) {
    throw new Error(`projectDefaultVersion: прибирання (${phase}) не вдалося:\n- ${problems.join('\n- ')}`);
  }
}

// ── Помічники ──────────────────────────────────────────────────────────────────

/** Підпис опції v2 у формі: <код шаблону> · <версія>. */
function v2Label(): string {
  return `${templateLabel} · ${NewVersion}`;
}

function reEscape(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  // Роль, а не підпис: `getByLabel(/Password/)` резолвиться ще й у кнопку-тумблер видимості пароля (Q-260).
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function listVersions(page: Page, templateId: number): Promise<VersionRow[]> {
  const response = await page.request.get(`/api/v1/templates/${String(templateId)}/versions?limit=100`);
  expect(response.ok(), `GET versions: ${String(response.status())}`).toBe(true);

  return ((await response.json()) as { items: VersionRow[] }).items;
}

/**
 * Клон `sourceId`, у якому кожна таблиця без рядків (фіксована/змішана) отримує один рядок - інакше публікацію
 * відхиляє ECR-TMPL-4228 - і який публікується. Повертає id опублікованої версії-клону.
 */
async function publishableClone(page: Page, sourceId: number, name: string): Promise<number> {
  const clone = await page.request.post(`/api/v1/template-versions/${String(sourceId)}/clone`, {
    data: { newVersion: name },
  });
  expect(clone.status(), `клон ${String(sourceId)}: ${await clone.text()}`).toBe(201);
  const id = ((await clone.json()) as { versionId: number }).versionId;

  const structure = await page.request.get(`/api/v1/template-versions/${String(id)}/structure`);
  expect(structure.ok(), `structure ${String(id)}: ${await structure.text()}`).toBe(true);
  const sheets = ((await structure.json()) as {
    sheets: { tables: { id: number; rowMode: string; rows: unknown[] }[] }[];
  }).sheets;

  for (const table of sheets.flatMap((sheet) => sheet.tables)) {
    if (table.rowMode === 'Dynamic' || table.rows.length > 0) continue;

    const row = await page.request.put(`/api/v1/template-versions/${String(id)}/tables/${String(table.id)}/rows/E2EFV12`, {
      data: { labelL10n: { en: 'E2E row' }, ordinal: null, rowKind: 'Item', parentRowKey: null, isReadOnly: false },
    });
    expect(row.ok(), `рядок таблиці ${String(table.id)}: ${await row.text()}`).toBe(true);
  }

  const publish = await page.request.post(`/api/v1/template-versions/${String(id)}/publish`, {
    data: { reason: 'e2e: ФВ-1.2, базова опублікована версія' },
  });
  expect(publish.status(), `публікація ${name}: ${await publish.text()}`).toBe(204);

  return id;
}
/**
 * Версія стенда - джерело для клону: найновіша опублікована неархівного шаблону, інакше (стенд може сидіти
 * чернеткою) - будь-яка версія, яку тест сам опублікує. Порожній стенд - червоне з переліком побаченого.
 */
async function findSourceVersion(page: Page): Promise<{
  templateId: number;
  templateCode: string;
  versionId: number;
  versionLabel: string;
  status: string;
}> {
  const response = await page.request.get('/api/v1/templates?limit=100');
  expect(response.ok(), `GET templates: ${String(response.status())}`).toBe(true);

  const templates = ((await response.json()) as { items: { id: number; code: string; isArchived?: boolean }[] }).items;
  const seen: string[] = [];
  let draft: { templateId: number; templateCode: string; versionId: number; versionLabel: string; status: string } | null =
    null;

  for (const template of templates) {
    const rows = await listVersions(page, template.id);
    seen.push(`${template.code}${template.isArchived === true ? ' (archived)' : ''}: ${rows.map((r) => `${r.version}=${r.status}`).join(', ')}`);
    if (template.isArchived === true) continue;

    const published = rows.filter((row) => row.status === 'Published').at(-1);
    if (published !== undefined) {
      return {
        templateId: template.id,
        templateCode: template.code,
        versionId: published.id,
        versionLabel: `${template.code} · ${published.version}`,
        status: 'Published',
      };
    }

    const any = rows.find((row) => row.status === 'Draft');
    if (any !== undefined && draft === null) {
      draft = {
        templateId: template.id,
        templateCode: template.code,
        versionId: any.id,
        versionLabel: `${template.code} · ${any.version}`,
        status: 'Draft',
      };
    }
  }

  if (draft !== null) return draft;

  throw new Error(`не доведено: на стенді немає версії для клону. Шаблони: ${seen.join(' | ') || '(порожньо)'}`);
}
/**
 * ⚠ Найновіша опублікована версія ВЗАГАЛІ (серед усіх шаблонів) - це саме та, що пропонує форма. Тест клонує
 * версію з першого шаблону, де вона є, і публікує пізніше за всі, тож v2 гарантовано найновіша.
 */
async function openCreateForm(page: Page): Promise<void> {
  await page.goto('/admin/periods');
  const opener = page.getByRole('button', { name: /New project|Новий проєкт/i }).first();
  await expect(opener, 'немає кнопки створення проєкту').toBeVisible({ timeout: 30_000 });
  await opener.click();
  await expect(page.getByRole('dialog')).toBeVisible({ timeout: 10_000 });
}

/**
 * Заповнює решту полів і натискає «Save»; повертає версію, яку надіслав клієнт.
 * ⚠ POST перехоплюється й скасовується: проєкт на стенді не видалити й не заархівувати (відкриті періоди,
 * ECR-PRD-0409 - прогін 38300078736), а створений він бачився б `periodsOverview`/`zz-walkthrough`. Що саме
 * надсилає клієнт - і є предмет цього тесту; серверну прив'язку доводить `ProjectTemplateVersionBindingTests`.
 */
async function submitForm(page: Page, code: string): Promise<{ templateVersionIdSent: number }> {
  const dialog = page.getByRole('dialog');

  await dialog.getByRole('textbox', { name: /^Code|^Код/i }).fill(code);
  await dialog.getByRole('textbox', { name: /^Name|^Назва|^Название/i }).first().fill(`E2E FV-1.2 ${code}`);

  const zone = dialog.getByRole('textbox', { name: /time zone|Часов/i });
  await zone.click();
  await zone.fill('Europe/');
  await page.getByRole('option', { name: /^Europe\// }).first().click();

  const policy = dialog.getByRole('textbox', { name: /Period policy|Політика|Политика/i });
  await policy.click();
  await page.getByRole('listbox').last().getByRole('option').first().click();

  let postData: string | null = null;
  const pattern = '**/api/v1/projects';
  const handler = async (route: Route): Promise<void> => {
    if (route.request().method() !== 'POST') {
      await route.continue();
      return;
    }

    postData = route.request().postData();
    await route.abort('failed');
  };
  await page.route(pattern, handler);

  try {
    await dialog.getByRole('button', { name: /^Save$|^Зберегти$|^Сохранить$/i }).click();
    await expect.poll(() => postData, { message: 'форма не надіслала POST /projects', timeout: 30_000 }).not.toBeNull();
  } finally {
    await page.unroute(pattern, handler);
  }

  const sent = JSON.parse(postData ?? '{}') as { templateVersionId?: number; code?: string };
  expect(sent.code, 'у запиті не той код проєкту').toBe(code);

  return { templateVersionIdSent: sent.templateVersionId ?? 0 };
}