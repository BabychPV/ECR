import { expect, test, type Browser, type Page } from '@playwright/test';

/**
 * ФВ-1.2 (рішення людини 12.10) проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`,
 * `-Grep projectDefaultVersion`): форма «New project» за замовчуванням пропонує ОСТАННЮ ОПУБЛІКОВАНУ версію
 * шаблону.
 *
 * Сценарій:
 *  1. адмін клонує наявну опубліковану версію -> нова чернетка -> публікує її (v2 того самого шаблону);
 *  2. `/admin/periods` -> «New project»: у полі «Template version» вже стоїть v2 (без жодного кліку);
 *  3. v1 лишається доступною для вибору: в розкритому списку є обидві;
 *  4. адмін обирає v1 свідомо -> проєкт створюється саме на v1 (пропозиція не перекриває вибір людини);
 *  5. другий проєкт створюється без вибору версії -> прив'язаний до v2;
 *  6. зміна шаблону НЕ переносить наявні проєкти (ФВ-1.2): проєкт із кроку 4 лишився на v1.
 *
 * ⚠ Стан стенда: тест створює дві опубліковані версії (`99.<n>.0.1/2`) і ДВА проєкти (`E2E_FV12_<мітка>_A/B`).
 * `beforeAll`/`afterAll` (`sweep`) архівують проєкти, виводять версії з обігу (`Deprecated`), щоб наступні файли
 * не бачили «найновішою» чужу версію, і ПЕРЕВІРЯЮТЬ підсумок. Проєкт видалити нічим - лише `Archived`.
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
const created: { versionId: number | null; baseVersionId: number | null; projectIds: number[] } = {
  versionId: null,
  baseVersionId: null,
  projectIds: [],
};
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
    created.projectIds.push(first.projectId);
    expect(first.templateVersionIdSent, 'проєкт мав піти з обраною v1').toBe(v1Id);

    // ── 5. Без вибору — пропозиція (v2) ────────────────────────────────────
    await openCreateForm(page);
    await expect(versionField).toHaveValue(new RegExp(`${reEscape(v2Label())}$`), { timeout: 60_000 });
    const second = await submitForm(page, `E2E_FV12_${stamp}_B`);
    created.projectIds.push(second.projectId);
    expect(second.templateVersionIdSent, 'без вибору проєкт мав піти з пропонованою v2').toBe(v2Id);

    // ── 6. Сервер: прив'язка збережена як створена; публікація v2 нічого не перенесла ──
    expect(await boundVersion(page, first.projectId), 'проєкт A мав лишитися на v1').toBe(v1Id);
    expect(await boundVersion(page, second.projectId), 'проєкт B мав бути на v2').toBe(v2Id);
  });

  // ⛔ Прибирання - НАДІЙНЕ, а не best effort: зайвий проєкт/опублікована версія видні наступним файлам
  // (`periodsOverview`, `zz-walkthrough`, форма проєкту іншого spec). Тому і ДО тесту (залишки аварійного
  // прогону), і ПІСЛЯ нього: усе, що носить префікс/номер цього тесту, архівується/виводиться з обігу, потім
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

const ProjectPrefix = 'E2E_FV12_';
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

    // Проєкти (з курсорною пагінацією).
    const projects = await listAllProjects(page);
    for (const project of projects.filter((p) => p.code.startsWith(ProjectPrefix) && p.status !== 'Archived')) {
      const response = await page.request.post(`/api/v1/projects/${String(project.id)}/archive`);
      if (!response.ok()) {
        problems.push(`проєкт ${project.code} не архівовано: ${String(response.status())} ${await response.text()}`);
      }
    }

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
    for (const project of await listAllProjects(page)) {
      if (project.code.startsWith(ProjectPrefix) && project.status !== 'Archived') {
        problems.push(`проєкт ${project.code} лишився зі станом ${project.status}`);
      }
    }
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

async function listAllProjects(page: Page): Promise<{ id: number; code: string; status: string }[]> {
  const all: { id: number; code: string; status: string }[] = [];
  let cursor: string | null = null;

  for (let guard = 0; guard < 50; guard += 1) {
    const url: string =
      cursor === null ? '/api/v1/projects?limit=100' : `/api/v1/projects?limit=100&cursor=${encodeURIComponent(cursor)}`;
    const response = await page.request.get(url);
    expect(response.ok(), `GET projects: ${String(response.status())}`).toBe(true);

    const body = (await response.json()) as {
      items: { id: number; code: string; status: string }[];
      nextCursor: string | null;
    };
    all.push(...body.items);
    if (body.nextCursor === null) break;
    cursor = body.nextCursor;
  }

  return all;
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

/** Заповнює решту полів і натискає «Save»; повертає id проєкту і версію, яку надіслав клієнт. */
async function submitForm(
  page: Page,
  code: string,
): Promise<{ projectId: number; templateVersionIdSent: number }> {
  const dialog = page.getByRole('dialog');

  await dialog.getByRole('textbox', { name: /^Code|^Код/i }).fill(code);
  await dialog.getByRole('textbox', { name: /^Name|^Назва|^Название/i }).first().fill(`E2E FV-1.2 ${code}`);

  const zone = dialog.getByRole('textbox', { name: /time zone|Часов|Часов/i });
  await zone.click();
  await zone.fill('Europe/');
  await page.getByRole('option', { name: /^Europe\// }).first().click();

  const policy = dialog.getByRole('textbox', { name: /Period policy|Політика|Политика/i });
  await policy.click();
  await page.getByRole('listbox').last().getByRole('option').first().click();

  const request = page.waitForResponse(
    (response) => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/v1/projects',
    { timeout: 60_000 },
  );
  await dialog.getByRole('button', { name: /^Save$|^Зберегти$|^Сохранить$/i }).click();

  const response = await request;
  expect(response.status(), `створення проєкту: ${await response.text()}`).toBe(201);

  const sent = JSON.parse(response.request().postData() ?? '{}') as { templateVersionId?: number };
  const body = (await response.json()) as { projectId: number };

  return { projectId: body.projectId, templateVersionIdSent: sent.templateVersionId ?? 0 };
}

async function boundVersion(page: Page, projectId: number): Promise<number> {
  const response = await page.request.get(`/api/v1/projects/${String(projectId)}/document-template`);
  expect(response.ok(), `GET document-template: ${String(response.status())} ${await response.text()}`).toBe(true);

  return ((await response.json()) as { templateVersionId: number }).templateVersionId;
}
