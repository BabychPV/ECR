import { expect, test, type Page } from '@playwright/test';

/**
 * Q-2 (людина 06.11: «це має бути параметр налаштування в UI») на живому стенді: частота синку подій PI сутності-шаблону
 * факела — «Джерела» → шухляда з'єднання `E2E_PI_EF` → «Events from PI» → частота → зберегти → перезавантаження.
 *
 * ⚠ З'єднання і сутність кладе стенд (`tools/e2e/pi-events-seed.sql`, `ECR_E2E_PI_EVENTS_ENTITY`): сутність ВИМКНЕНА, тож
 * поставлений розклад синку не запускає (збір вимкненої сутності нічого не робить). Сценарій сам прибирає за собою
 * розклад через API — повторний прогін на тому самому стенді починає з «розкладу немає».
 * Запуск: `tools/e2e-stand.ps1 … -Grep "частота синку подій PI"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const EntityId = Number(process.env['ECR_E2E_PI_EVENTS_ENTITY'] ?? '0');
const Connection = 'E2E_PI_EF';

test.describe('частота синку подій PI', () => {
  test.skip(EntityId === 0, 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('частота синку подій PI: вибрати, зберегти, після перезавантаження збережена', async ({ page }) => {
    test.slow();
    await signIn(page);
    await removeSchedule(page);

    try {
      await openEventsTab(page);
      const block = page.locator(`[data-source-event-frequency="${EntityId}"]`);
      await expect(block).toBeVisible({ timeout: 30_000 });

      // Розкладу ще немає: типово «щогодини» з підказкою «збережіть».
      await expect(block.locator('[data-source-event-frequency-none]')).toBeVisible();

      await block.getByRole('textbox', { name: 'Event sync frequency' }).click();
      await page.getByRole('option', { name: 'Every 15 minutes' }).click();
      await block.locator('[data-source-event-frequency-save]').click();
      await expect(block).toHaveAttribute('data-frequency-stored', 'PT15M', { timeout: 30_000 });

      // Після перезавантаження частота читається з розкладу на сервері.
      await page.reload();
      await openEventsTab(page);
      const reloaded = page.locator(`[data-source-event-frequency="${EntityId}"]`);
      await expect(reloaded).toHaveAttribute('data-frequency-stored', 'PT15M', { timeout: 30_000 });
      await expect(reloaded.getByRole('textbox', { name: 'Event sync frequency' })).toHaveValue('Every 15 minutes');
      await expect(reloaded.locator('[data-source-event-frequency-none]')).toHaveCount(0);

      // Сервер зберіг саме cron вибраної частоти.
      const stored = await schedules(page);
      expect(stored.find((row) => row.sourceEntityId === EntityId)?.cron).toBe('0 0/15 * * * ?');
    } finally {
      await removeSchedule(page);
    }
  });
});

interface ScheduleRow {
  readonly id: number;
  readonly sourceEntityId: number;
  readonly cron: string;
  readonly rowVersion: string;
}

async function openEventsTab(page: Page): Promise<void> {
  await page.goto(`/admin/sources?panel=${Connection}`);
  await page.getByRole('tab', { name: 'Events from PI' }).click({ timeout: 30_000 });
  // Сутність стенда — єдина в з'єднанні; вибір лише підтверджує, що вкладка показує саме її.
  await expect(page.locator('[data-source-events-entity]')).toBeVisible({ timeout: 30_000 });
}

async function schedules(page: Page): Promise<ScheduleRow[]> {
  // Перелік вимагає `Integration.EditSchedule` (роль `E2EAdmin` у `tools/e2e-stand.ps1`); відмова — зі статусом і тілом.
  const response = await page.request.get(`/api/v1/collection-schedules?dataSource=${encodeURIComponent(Connection)}`);
  expect(response.ok(), `GET collection-schedules?dataSource=${Connection}: ${String(response.status())} ${await response.text()}`).toBe(true);
  return (await response.json()) as ScheduleRow[];
}

/** Прибирання — терпиме: розкладу немає (чи його вже прибрали — `404`) означає «нічого прибирати». */
async function removeSchedule(page: Page): Promise<void> {
  const row = (await schedules(page)).find((item) => item.sourceEntityId === EntityId);
  if (row === undefined) return;
  const response = await page.request.delete(`/api/v1/collection-schedules/${String(row.id)}`, {
    headers: { 'If-Match': `"${row.rowVersion}"` },
  });
  expect(
    response.ok() || response.status() === 404,
    `DELETE collection-schedules/${String(row.id)}: ${String(response.status())} ${await response.text()}`,
  ).toBe(true);
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
