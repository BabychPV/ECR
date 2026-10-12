import { expect, test, type Page } from '@playwright/test';

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
 * ⚠ Стан стенда: тест створює дві версії-чернетки/публікацію й ДВА проєкти (код `E2E-FV12-<мітка>`). Стенд
 * одноразовий; `afterAll` виводить опубліковану тестом версію з обігу (`Deprecated`), щоб наступні файли не
 * бачили «найновішою» чужу версію. Проєкти видалити нічим (архівація — `POST /projects/{id}/archive`, її й
 * викликає `afterAll`, best effort).
 *
 * ⚠ Написано без локального прогону (на машині автора немає стенда): перший живий прогін - CI.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

interface VersionRow {
  id: number;
  version: string;
  status: string;
  publishedAt: string | null;
}

const stamp = Date.now().toString(36).toUpperCase();
const NewVersion = `FV12-${stamp}`;

/** Усе, що тест створив і що `afterAll` прибирає. */
const created: { versionId: number | null; projectIds: number[] } = { versionId: null, projectIds: [] };
let v1Id = 0;
let v2Id = 0;
let templateLabel = '';

test.describe('projectDefaultVersion: форма проєкту пропонує останню опубліковану версію (ФВ-1.2)', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  test('projectDefaultVersion: після публікації v2 форма пропонує v2, v1 лишається, вибір v1 і створення', async ({
    page,
  }) => {
    await signIn(page);

    // ── 1. v2: клон опублікованої версії + публікація ────────────────────────
    const source = await findPublishedVersion(page);
    v1Id = source.versionId;
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
      listbox.getByRole('option', { name: new RegExp(`${reEscape(source.versionLabel)}$`) }),
      'v1 зникла зі списку версій',
    ).toBeVisible();

    // ── 4. Свідомий вибір v1 не перекривається пропозицією ──────────────────
    await listbox.getByRole('option', { name: new RegExp(`${reEscape(source.versionLabel)}$`) }).click();
    await expect(versionField).toHaveValue(new RegExp(`${reEscape(source.versionLabel)}$`));
    const first = await submitForm(page, `E2E-FV12-${stamp}-A`);
    created.projectIds.push(first.projectId);
    expect(first.templateVersionIdSent, 'проєкт мав піти з обраною v1').toBe(v1Id);

    // ── 5. Без вибору — пропозиція (v2) ────────────────────────────────────
    await openCreateForm(page);
    await expect(versionField).toHaveValue(new RegExp(`${reEscape(v2Label())}$`), { timeout: 60_000 });
    const second = await submitForm(page, `E2E-FV12-${stamp}-B`);
    created.projectIds.push(second.projectId);
    expect(second.templateVersionIdSent, 'без вибору проєкт мав піти з пропонованою v2').toBe(v2Id);

    // ── 6. Сервер: прив'язка збережена як створена; публікація v2 нічого не перенесла ──
    expect(await boundVersion(page, first.projectId), 'проєкт A мав лишитися на v1').toBe(v1Id);
    expect(await boundVersion(page, second.projectId), 'проєкт B мав бути на v2').toBe(v2Id);
  });

  test.afterAll(async ({ browser }, testInfo) => {
    testInfo.setTimeout(120_000);
    if (created.versionId === null && created.projectIds.length === 0) return;

    const context = await browser.newContext();
    const page = await context.newPage();
    try {
      await signIn(page);
      for (const projectId of created.projectIds) {
        await page.request.post(`/api/v1/projects/${String(projectId)}/archive`).catch(() => undefined);
      }
      if (created.versionId !== null) {
        await page.request
          .post(`/api/v1/template-versions/${String(created.versionId)}/deprecate`, {
            data: { reason: 'e2e: ФВ-1.2, повернення стенда до базової лінії' },
          })
          .catch(() => undefined);
      }
    } finally {
      await context.close();
    }
  });
});

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

/** Будь-яка опублікована версія будь-якого неархівного шаблону стенда - джерело для клону. */
async function findPublishedVersion(
  page: Page,
): Promise<{ templateId: number; templateCode: string; versionId: number; versionLabel: string }> {
  const response = await page.request.get('/api/v1/templates?limit=100');
  expect(response.ok(), `GET templates: ${String(response.status())}`).toBe(true);

  const templates = ((await response.json()) as { items: { id: number; code: string; isArchived?: boolean }[] }).items;
  for (const template of templates) {
    if (template.isArchived === true) continue;

    const published = (await listVersions(page, template.id)).filter((row) => row.status === 'Published');
    const latest = published.at(-1);
    if (latest !== undefined) {
      return { templateId: template.id, templateCode: template.code, versionId: latest.id, versionLabel: `${template.code} · ${latest.version}` };
    }
  }

  throw new Error('не доведено: на стенді немає жодної опублікованої версії неархівного шаблону');
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
  await zone.fill('Europe/Kyiv');
  await page.getByRole('option', { name: /Europe\/Kyiv/ }).first().click();

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
