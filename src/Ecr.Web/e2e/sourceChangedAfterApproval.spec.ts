import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test';

/**
 * ✎ F5-R (D-570, рішення людини P-1122-4: «так познач щоб користувач бачив це»): на справжньому стенді й у справжньому
 * Chromium — (а) рядок переліку документів, чий затверджений місяць має змінене джерело, показує позначку «Source changed»,
 * а підказка (фокус клавіатурою) називає місяць і дію; (б) панель «Проблеми розрахунку» документа показує причину «дані
 * HSE400 змінено після затвердження» з датою зміни й підказкою дії. Тексти — з сіду стенда (каталог), не ключі.
 *
 * ⚠ Відповіді переліку й `calculation-issues` підміняються в браузері (поле `sourceChangedPeriods` / рядок `sourceIssues`):
 * зв'язки HSE400 → Land у стенді немає, а перевіряється рендер, каталог і доступність позначки. Правило сервера тримають
 * інтеграційні тести `DocumentSourceIssuesAnyTests`.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

test.describe('Джерело змінено після затвердження (F5-R)', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('перелік: позначка «Source changed» з підказкою; документ: причина з датою зміни й дією', async ({ page }) => {
    let firstId: number | null = null;
    await page.route(/\/api\/v1\/documents\?/, async (route) => {
      const response = await route.fetch();
      const body = (await response.json()) as { items?: Array<Record<string, unknown>> };
      const first = body.items?.[0];
      if (first !== undefined) {
        firstId = Number(first['id']);
        first['sourceChangedPeriods'] = [Number(PeriodKey)];
      }
      await route.fulfill({ response, json: body });
    });

    await signIn(page, Operator.user, Operator.password);
    await page.goto(`/?periodKey=${PeriodKey}`);

    const mark = page.locator('[data-source-changed]').first();
    await expect(mark, 'позначки «Source changed» у рядку немає').toBeVisible({ timeout: 30_000 });
    await expect(mark).toHaveText(/Source changed|Источник изменён|Дереккөз өзгерді/);
    await expect(mark).toHaveAttribute('data-periods', PeriodKey);

    // Підказка досяжна з клавіатури (`<Hint focusable>`), текст — з каталогу стенда, не ключ.
    await mark.focus();
    await expect(page.getByRole('tooltip')).toContainText(/HSE400/);
    await expect(page.getByRole('tooltip')).not.toContainText('documents.sourceChangedHint');

    expect(firstId, 'у переліку стенда немає документа').not.toBeNull();
    await page.route(/\/api\/v1\/documents\/\d+\/calculation-issues/, async (route) => {
      const response = await route.fetch();
      const body = (await response.json()) as Record<string, unknown>;
      body['sourceIssues'] = [
        {
          kind: 'SourceChangedAfterApproval',
          monthNumber: Number(PeriodKey) % 100,
          sourcePeriodKey: Number(PeriodKey),
          sourceDocumentId: null,
          sourceProjectCode: null,
          sourceDocumentKey: null,
          sourceApprovedAt: null,
          approvedAt: '2026-04-02T08:00:00Z',
          sourceChangedAt: '2026-04-05T11:00:00Z',
        },
      ];
      await route.fulfill({ response, json: body });
    });

    await page.goto(`/documents/${String(firstId)}?periodKey=${PeriodKey}`);
    const cause = page.getByTestId('document-calc-issue').filter({ has: page.getByTestId('document-calc-issue-source-hint') });
    await expect(cause, 'причини «джерело змінено» в панелі немає').toBeVisible({ timeout: 30_000 });
    await expect(cause).toHaveAttribute('data-kind', 'sourceChanged');
    await expect(cause.getByTestId('document-calc-issue-title')).toContainText(/HSE400/);
    await expect(cause.getByTestId('document-calc-issue-source-dates').locator('time')).toHaveCount(2);
    await expect(cause.getByTestId('document-calc-issue-source-hint')).not.toContainText('document.calcIssues');
    await expect(page.getByTestId('document-calc-issues')).toHaveAttribute('data-blocking', 'false');
  });

  /**
   * ✎ W15z M4: документ «рік» (`YearWithMonthColumns`, як Land doc 96) відкривається на ЯКОРІ року, а F5-R живе в
   * місяці (жовтень). Доти сторінка читала `calculation-issues` лише якоря — причини жовтня не було видно ніде. Тепер
   * панель жовтня (`data-period`) з причиною й джерелом — з відповіді `year=true` (підміна — лише її: без `year=true`
   * панелі жовтня не буде, тест червоний).
   */
  test('документ «рік»: на якорі видно панель жовтня з причиною «джерело змінено» і джерелом', async ({ page }) => {
    // ⚠ Фікстура — ~35 запитів (шаблон, 13 колонок, проєкт, екземпляри 12 місяців, рядок): типових 30 с не вистачає.
    test.setTimeout(300_000);
    const request = page.request;
    await signIn(page, Operator.user, Operator.password);
    const arranged = await arrangeYearDocument(request, 'Monthly');
    // ✎ Репетиція W15z: джерело — як справжній HSE400: річний проєкт (PeriodKind.Yearly), документ «рік».
    const hse = await arrangeYearDocument(request, 'Yearly');
    // Фікстура як справжній HSE400: статусів місячних колонок немає, коди місячних колонок — є.
    const target = (await (
      await request.get(`/api/v1/documents/${String(hse.documentId)}/submission-target?sheetDefId=${String(hse.sheetDefId)}&periodKey=${String(hse.anchor)}`)
    ).json()) as { monthColumns?: unknown[]; monthColumnCodes?: unknown[] };
    expect(target.monthColumns ?? [], 'річний проєкт дав статуси місячних колонок — фікстура не як HSE400').toHaveLength(0);
    expect(target.monthColumnCodes ?? []).toHaveLength(12);

    let yearRead = false;
    await page.route(/\/api\/v1\/documents\/\d+\/calculation-issues/, async (route) => {
      const response = await route.fetch();
      const body = (await response.json()) as Record<string, unknown>;
      if (route.request().url().includes('year=true')) {
        yearRead = true;
        // ✎ Огляд b11 (F): StepError рядка R40 жовтня — «Show rows» веде до рядка за межами вікна сітки (вертикаль).
        body['items'] = [
          ...((body['items'] as unknown[] | undefined) ?? []),
          {
            tableCode: 'T1', sheetCopyId: null, rowNumber: 40, rowKey: 'R40', kind: 'StepError', methodologyCode: 'E2E',
            detectedAt: '2026-04-03T08:00:00Z', monthNumber: 10, stepCode: 'S1', errorCode: '#VALUE', affectedCount: 1,
            methodologyId: null,
          },
        ];
        body['sourceIssues'] = [
          {
            kind: 'SourceChangedAfterApproval',
            monthNumber: 10,
            sourcePeriodKey: arranged.october,
            sourceDocumentId: hse.documentId,
            sourceProjectCode: 'HSE',
            sourceDocumentKey: 'HSE400-E2E',
            sourceApprovedAt: '2026-04-01T08:00:00Z',
            approvedAt: '2026-04-02T08:00:00Z',
            sourceChangedAt: null,
          },
        ];
      }
      await route.fulfill({ response, json: body });
    });

    await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.october)}`);
    await expect(page).toHaveURL(new RegExp(`periodKey=${String(arranged.anchor)}`), { timeout: 90_000 });

    const panel = page.locator(`[data-testid="document-calc-issues"][data-period="${String(arranged.october)}"]`);
    await expect(panel, 'панелі проблем жовтня на якорі року немає').toBeVisible({ timeout: 60_000 });
    expect(yearRead, 'сторінка «рік» не читала проблеми року (year=true)').toBe(true);
    await expect(panel.getByTestId('document-calc-issues-title')).toContainText(/October|Жовт|Октябр|Қазан/i);
    const cause = panel.getByTestId('document-calc-issue').first();
    await expect(cause).toHaveAttribute('data-kind', 'sourceChanged');
    await expect(cause.getByTestId('document-calc-issue-detail')).toHaveText('HSE · HSE400-E2E');
    await expect(cause.getByTestId('document-calc-issue-source-hint')).not.toContainText('document.calcIssues');
    await expect(cause.getByTestId('document-calc-issue-open-source')).toBeVisible();

    // ✎ Огляд b11 (F): `scrollToCoordinate({ y })` RevoGrid — пікселі; рядок R40 за межами вікна сітки має стати видимим.
    await panel.locator('[data-kind="stepError"]').getByTestId('document-calc-issue-show').click();
    await expect(page.locator('.ecr-cell-nav-target'), 'сітка не прокрутилась до рядка R40').toBeInViewport({ timeout: 30_000 });

    // ✎ Огляд M4 (4): «Open HSE400» веде на МІСЯЦЬ річного документа (`?periodKey=жовтень`) — сторінка лишається на якорі
    // для даних, але сітка переходить до колонки жовтня (підсвічена комірка, заголовок «Month M10» у вікні).
    await cause.getByTestId('document-calc-issue-open-source').click();
    await expect(page).toHaveURL(new RegExp(`/documents/${String(hse.documentId)}\\?periodKey=${String(hse.anchor)}`), { timeout: 90_000 });
    await expect(page.locator('.ecr-cell-nav-target'), 'сітка не перейшла до колонки місяця').toHaveCount(1, { timeout: 60_000 });
    await expect(page.getByText('Month M10', { exact: true }).first()).toBeInViewport();
  });
});

interface YearDocument {
  readonly documentId: number;
  readonly anchor: number;
  readonly october: number;
  readonly sheetDefId: number;
}

/**
 * Документ «рік» на одноразовій базі стенда: шаблон `YearWithMonthColumns` (аркуш S1, таблиця `MonthsInColumns`, 12
 * місячних колонок). `Monthly` — як Land (споживач), `Yearly` — як HSE400 (джерело: річний проєкт, `monthColumns`
 * у `submission-target` порожні, місяць колонки — лише в `monthColumnCodes`).
 */
async function arrangeYearDocument(request: APIRequestContext, periodKind: 'Monthly' | 'Yearly'): Promise<YearDocument> {
  const ok = async <T,>(response: APIResponse, what: string): Promise<T> => {
    const body = await response.text();
    expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1000)}`).toBeLessThan(300);
    return (body.length > 0 ? JSON.parse(body) : {}) as T;
  };
  const stamp = `${String(Date.now())}${periodKind === 'Yearly' ? 'Y' : 'M'}`;
  const year = new Date().getUTCFullYear() - 1;

  const template = await ok<{ templateId: number }>(
    await request.post('/api/v1/templates', { data: { code: `E2EF5R${stamp}`, nameL10n: { en: `E2E F5-R year ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await request.post(`/api/v1/templates/${String(template.templateId)}/versions`, { data: { versionNumber: '1.0.0.0', cloneFromVersionId: null } }),
    'версія',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;
  await ok(await request.put(`${base}/layout`, { data: { layout: 'YearWithMonthColumns' } }), 'розкладка «рік»');
  await ok(
    await request.put(`${base}/sheets/S1`, { data: { nameL10n: { en: 'Year sheet' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null } }),
    'аркуш',
  );
  const table = await ok<{ id: number }>(
    await request.put(`${base}/sheets/S1/tables/T1`, {
      data: { nameL10n: { en: 'Year table' }, ordinal: 0, layoutKind: 'MonthsInColumns', rowMode: 'Dynamic', maxDynamicRows: 60 },
    }),
    'таблиця',
  );
  const column = (ordinal: number, header: string, dataType: 'String' | 'Decimal', month: number | null): Record<string, unknown> => ({
    headerL10n: { en: header }, ordinal, dataType, isRequired: false, isReadOnly: false, isHidden: false,
    precision: dataType === 'Decimal' ? 18 : null, scale: dataType === 'Decimal' ? 2 : null, defaultValue: null,
    displayFormat: null, lookupFilter: null, lookupRegistryDefId: null, styleId: null, unitId: null,
    ...(month === null ? {} : { isMonthColumn: true, monthNumber: month }),
  });
  await ok(await request.put(`${base}/tables/${String(table.id)}/columns/NAME`, { data: column(0, 'Name', 'String', null) }), 'стовпець NAME');
  for (let m = 1; m <= 12; m += 1) {
    const code = `M${String(m).padStart(2, '0')}`;
    await ok(await request.put(`${base}/tables/${String(table.id)}/columns/${code}`, { data: column(m, `Month ${code}`, 'Decimal', m) }), `стовпець ${code}`);
  }
  await ok(await request.post(`${base}/publish`, { data: { reason: 'e2e: F5-R документа «рік» (W15z M4)' } }), 'публікація');

  const policy = await ok<{ id: number }>(
    await request.post('/api/v1/projects/period-policies', {
      data: { code: `E2EF5R${stamp}`, openOffsetDays: 0, graceOffsetDays: 3000, hardCloseOffsetDays: 3600, yearGraceOffsetDays: 3600 },
    }),
    'політика періодів',
  );
  const project = await ok<{ projectId: number }>(
    await request.post('/api/v1/projects', {
      data: {
        code: `E2EF5RPrj${stamp}`, nameL10n: { en: `E2E F5-R year ${stamp}` }, periodKind, timeZoneId: 'UTC',
        templateVersionId: version.versionId, periodPolicyId: policy.id, year,
      },
    }),
    'проєкт',
  );
  await ok(await request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація');
  const structure = await ok<{ sheets: { id: number }[] }>(await request.get(`${base}/structure`), 'структура');
  const document = await ok<{ documentId: number }>(
    await request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((s) => s.id), templateVersionId: version.versionId },
    }),
    'документ',
  );

  // Екземпляри всіх місяців (власники місячних колонок) і рядок R1 — щоб було куди перейти в колонці жовтня.
  // ⚠ Річний проєкт (як HSE400, PeriodKind.Yearly) має лише період якоря — місяців як періодів немає.
  let anchorTables: { tableInstanceId: number; tableCode: string; sheetDefId: number }[] = [];
  for (let m = 1; m <= (periodKind === 'Yearly' ? 1 : 12); m += 1) {
    const rows = await ok<{ tableInstanceId: number; tableCode: string; sheetDefId: number }[]>(
      await request.get(`/api/v1/documents/${String(document.documentId)}/tables?periodKey=${String(year * 100 + m)}`),
      `таблиці ${String(m)}`,
    );
    if (m === 1) anchorTables = rows;
  }
  const t1 = anchorTables.find((t) => t.tableCode === 'T1');
  if (t1 === undefined) throw new Error('у якоря немає таблиці T1');
  // ✎ Огляд b11 (F): у споживача — 40 рядків, щоб перехід до R40 вимагав вертикальної прокрутки сітки (70vh).
  for (let r = 1; r <= (periodKind === 'Yearly' ? 1 : 40); r += 1) {
    await ok(
      await request.post(`/api/v1/documents/${String(document.documentId)}/rows`, { data: { tableInstanceId: t1.tableInstanceId, rowKey: `R${String(r)}` } }),
      `рядок R${String(r)}`,
    );
  }

  return { documentId: document.documentId, anchor: year * 100 + 1, october: year * 100 + 10, sheetDefId: t1.sheetDefId };
}

async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
