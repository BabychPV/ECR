import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { openDocumentMore } from './documentMore';

/**
 * D-443 (AN-130, BRIEF-1021 П.2) на РЕАЛЬНОМУ стенді (`tools/e2e-stand.ps1`, workflow `e2e-stand`): учасник документа.
 *
 * DoD: користувач БЕЗ жодної ролі на проєкт, якому надано одну таблицю, бачить документ у переліку, відкриває лише
 * цю таблицю (один аркуш), таблиця іншого аркуша через API — недоступна; `Approve`, виданий тим, хто сам `Approve`
 * не має, відхиляється; передача власника працює (через шторку) і повертається назад.
 *
 * Учасника заводить сам тест (`e2e-admin` має `Security.ManageUsers`): стенд свіжий на кожен прогін, а тестової ролі
 * в нього навмисно немає. Наприкінці надання знімаються, власник повертається — інші специфікації бачать стенд як був.
 *
 * ⚠ Написано без локального прогону (стенда на машині автора немає): перший живий прогін — CI `e2e-stand`.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Operator = { userName: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!', displayName: 'E2E operator' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const DocumentUrl = `/documents/${DocumentId}?periodKey=${PeriodKey}`;

// Ім'я без «approve»/«admin»: локатори кнопок звіряють підрядок доступного імені (див. e2e-stand.ps1, e2e-reviewer).
const Stamp = Date.now().toString(36);
const Participant = {
  userName: `e2e-part-${Stamp}`,
  displayName: `E2E participant ${Stamp}`,
  issued: 'E2E-Part1cip-2026!',
  password: 'E2E-Part1cip-Work-2026!',
};

interface DocumentTable {
  sheetCode: string;
  tableCode: string;
  tableInstanceId: number;
}

async function signedIn(browser: Browser, user: { userName: string; password: string }): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', {
    data: { userName: user.userName, password: user.password },
  });
  expect(login.ok(), `вхід ${user.userName}: ${String(login.status())} ${await login.text()}`).toBe(true);

  return context;
}

async function tablesOf(context: BrowserContext): Promise<DocumentTable[]> {
  const response = await context.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`);
  expect(response.ok(), `GET tables: ${String(response.status())} ${await response.text()}`).toBe(true);

  return (await response.json()) as DocumentTable[];
}

async function openDrawer(page: Page): Promise<void> {
  await page.goto(DocumentUrl);
  await openDocumentMore(page);
  await page.getByRole('menuitem', { name: /Edit access|Доступ к редактированию/ }).click();
  await expect(page.getByTestId('add-participant')).toBeVisible({ timeout: 20_000 });
}

test.describe('Учасники документа (D-443) на реальному стенді', () => {
  test.describe.configure({ mode: 'serial', timeout: 240_000 });

  let granted: DocumentTable | undefined;
  let other: DocumentTable | undefined;
  let participantId = 0;
  /** Власник документа ДО файла — до нього `afterAll` повертає документ, хоч би на якому кроці файл зупинився. */
  let originalOwner: number | null = null;

  test.beforeAll(async ({ browser }) => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }

    const admin = await signedIn(browser, Admin);
    try {
      const document = await admin.request.get(`/api/v1/documents/${DocumentId}?periodKey=${PeriodKey}`);
      expect(document.ok(), `GET document: ${String(document.status())}`).toBe(true);
      originalOwner = ((await document.json()) as { ownerUserId: number | null }).ownerUserId;
    } finally {
      await admin.close();
    }
  });

  test('учасник без ролі отримує одну таблицю через шторку: бачить документ і лише цю таблицю', async ({ browser }) => {
    const admin = await signedIn(browser, Admin);
    try {
      const tables = await tablesOf(admin);
      granted = tables[0];
      other = tables.find((t) => t.sheetCode !== granted?.sheetCode) ?? tables.find((t) => t.tableCode !== granted?.tableCode);
      expect(granted, 'у документі стенда немає таблиць').toBeDefined();
      expect(other, 'у документі стенда лише одна таблиця — нема чим перевірити сховане').toBeDefined();

      // Обліковий запис без жодної ролі (без роль-кодів) і зміна разового пароля.
      const created = await admin.request.post('/api/v1/users', {
        data: {
          userName: Participant.userName,
          provider: 'Local',
          sid: null,
          displayName: Participant.displayName,
          initialPassword: Participant.issued,
          roleCodes: [],
        },
      });
      expect(created.ok(), `POST users: ${String(created.status())} ${await created.text()}`).toBe(true);
      participantId = ((await created.json()) as { userId: number }).userId;

      const first = await signedIn(browser, { userName: Participant.userName, password: Participant.issued });
      const changed = await first.request.post('/api/v1/auth/change-password', {
        data: { currentPassword: Participant.issued, newPassword: Participant.password },
      });
      expect(changed.ok(), `change-password: ${String(changed.status())} ${await changed.text()}`).toBe(true);
      await first.close();

      // Власник/менеджер видає «Перегляд» на одну таблицю через шторку «Edit access → Add participant».
      const page = await admin.newPage();
      await openDrawer(page);
      await page.getByTestId('add-participant').click();
      const modal = page.getByTestId('participant-modal');
      await expect(modal).toBeVisible();
      await modal.getByLabel(/^Who|^Кто/).fill(Participant.displayName.slice(0, 18));
      await modal.getByRole('button', { name: Participant.displayName }).click({ timeout: 20_000 });
      // Mantine SegmentedControl ховає radio-input: клік по підпису сегмента.
      await modal.locator('label').filter({ hasText: /^(View|Просмотр)$/ }).click();
      await expect(modal.getByRole('radio', { name: /^(View|Просмотр)$/ })).toBeChecked();
      await modal.getByLabel(/^(Sheet|Лист)$/).click();
      await page.getByRole('option', { name: new RegExp(`\\(${granted?.sheetCode ?? ''}\\)$`) }).click();
      await modal.getByLabel(/^(Table|Таблица)$/).click();
      await page.getByRole('option', { name: new RegExp(`\\(${granted?.tableCode ?? ''}\\)$`) }).click();

      const grant = page.waitForResponse(
        (r) => r.request().method() === 'POST' && new URL(r.url()).pathname === `/api/v1/documents/${DocumentId}/edit-assignments`,
      );
      await modal.getByRole('button', { name: /Grant access|Предоставить доступ/ }).click();
      expect((await grant).status()).toBe(200);
      await expect(page.getByTestId('edit-assignments-months').getByTestId('assignment-level').filter({ hasText: /View|Просмотр/ }))
        .toHaveCount(1, { timeout: 20_000 });
    } finally {
      await admin.close();
    }

    // Учасник: документ є в переліку, відкривається з ОДНИМ аркушем, API віддає лише надану таблицю.
    const participant = await signedIn(browser, Participant);
    try {
      const list = await participant.request.get('/api/v1/documents?limit=200');
      expect(list.ok(), `GET documents: ${String(list.status())}`).toBe(true);
      const ids = ((await list.json()) as { items: { id: number }[] }).items.map((d) => String(d.id));
      expect(ids).toContain(DocumentId);

      const visible = await tablesOf(participant);
      expect(visible.map((t) => t.tableInstanceId)).toEqual([granted?.tableInstanceId]);

      const hidden = await participant.request.get(
        `/api/v1/documents/${DocumentId}/tables/${String(other?.tableInstanceId ?? 0)}?periodKey=${PeriodKey}`,
      );
      expect([403, 404], `чужа таблиця: ${String(hidden.status())}`).toContain(hidden.status());

      const page = await participant.newPage();
      await page.goto(DocumentUrl);
      await expect(page.locator('.doc-sheet-tab')).toHaveCount(1, { timeout: 60_000 });
    } finally {
      await participant.close();
    }
  });

  test('Approve не видає той, хто сам Approve не має; власника передано й повернуто через шторку', async ({ browser }) => {
    const admin = await signedIn(browser, Admin);
    expect(originalOwner, 'власник документа стенда невідомий').not.toBeNull();

    const operator = await signedIn(browser, Operator);
    try {
      // Менеджер передає власність `e2e-operator` (Write на проєкт) — через шторку.
      const page = await admin.newPage();
      await openDrawer(page);
      await page.getByTestId('transfer-owner').click();
      const modal = page.getByTestId('owner-transfer-modal');
      await modal.getByLabel(/^Who|^Кто/).fill(Operator.displayName);
      await modal.getByRole('button', { name: Operator.displayName }).click({ timeout: 20_000 });
      const transferred = page.waitForResponse(
        (r) => r.request().method() === 'POST' && new URL(r.url()).pathname === `/api/v1/documents/${DocumentId}/owner`,
      );
      await modal.getByRole('button', { name: /^Transfer$|^Передать$/ }).click();
      expect((await transferred).status()).toBe(200);

      // Власник із Write на проєкт: Approve — 403 «не вище власного рівня на області».
      const sheetCode = granted?.sheetCode ?? null;
      expect(participantId, 'учасника не створено попереднім кроком').toBeGreaterThan(0);
      const refused = await operator.request.post(`/api/v1/documents/${DocumentId}/edit-assignments`, {
        data: { slice: { sheetCode, tableCode: null }, recipientUserIds: [participantId], isExclusive: false, level: 'Approve' },
      });
      expect(refused.status(), await refused.text()).toBe(403);
      expect(((await refused.json()) as { messageKey?: string }).messageKey).toBe('err.ECR-ACCS-0403.editAssignmentLevelAboveOwn');
    } finally {
      // Повернення власника й зняття надань — у `afterAll`: воно мусить відбутися й тоді, коли цей тест не стартував.
      await operator.close();
      await admin.close();
    }
  });

  /*
   * ⛔ Прибирання — НЕ в `finally` другого тесту, як було: режим `serial` пропускає решту файла після першого падіння,
   * тож упалий перший тест (надання «View» уже видано) лишав учасника в документі стенда для всіх наступних
   * специфікацій (`editAssignments` іде одразу за цим файлом і рахує рядки шторки). `afterAll` виконується завжди.
   * Власника повертаємо (менеджер лишається менеджером); якщо попередній власник сам документом керувати не може
   * (сервер 422 — без права запису в проєкті), власником стає `e2e-admin`.
   */
  test.afterAll(async ({ browser }, testInfo) => {
    if (PeriodKey === '' || DocumentId === '') return;
    testInfo.setTimeout(60_000);

    const admin = await signedIn(browser, Admin);
    try {
      const current = ((await (await admin.request.get(`/api/v1/documents/${DocumentId}?periodKey=${PeriodKey}`)).json()) as {
        ownerUserId: number | null;
      }).ownerUserId;
      if (originalOwner !== null && current !== originalOwner) {
        let back = await admin.request.post(`/api/v1/documents/${DocumentId}/owner`, { data: { newOwnerUserId: originalOwner } });
        if (back.status() === 422) {
          const me = (await (await admin.request.get('/api/v1/me')).json()) as { userId: number };
          back = await admin.request.post(`/api/v1/documents/${DocumentId}/owner`, { data: { newOwnerUserId: me.userId } });
        }
        expect(back.ok(), `повернення власника: ${String(back.status())} ${await back.text()}`).toBe(true);
      }

      const list = (await (await admin.request.get(`/api/v1/documents/${DocumentId}/edit-assignments`)).json()) as {
        assignments: { id: number; level?: string }[];
      };
      for (const a of list.assignments.filter((x) => (x.level ?? 'Write') !== 'Write')) {
        const removed = await admin.request.delete(`/api/v1/documents/${DocumentId}/edit-assignments/${String(a.id)}`);
        expect(removed.ok(), `зняття надання ${String(a.id)}: ${String(removed.status())}`).toBe(true);
      }
    } finally {
      await admin.close();
    }
  });
});
