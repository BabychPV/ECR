import { expect, test, type Browser, type Page } from '@playwright/test';

/**
 * P2 10.11 (живий знімок людини, ru): у діалозі «Добавить участника документа» рівні доступу стояли в
 * `SegmentedControl` — чотири рівні сегменти на ширину діалогу; четвертий підпис «Заполнение, подача и согласование»
 * виходив за діалог / обрізався, і видима частина читалась «Заполнение» — дубль Write.
 *
 * DoD на РЕАЛЬНОМУ стенді (`tools/e2e-stand.ps1`, workflow `e2e-stand`): мовою профілю ru і kz, у світлій і темній темі,
 * типова ширина діалогу — КОЖЕН із чотирьох рівнів: (1) підпис — повний текст сіду, (2) прямокутник підпису й радіо
 * цілком усередині `.mantine-Modal-content`, (3) підпис не обрізаний (`scrollWidth ≤ clientWidth` у нього й нащадків),
 * (4) ні вміст діалогу, ні сторінка не мають горизонтальної прокрутки. Знімки діалогу — у `test-results` (артефакт CI).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

/** Тексти сіду `D443:participants` (Read, Write, Submit, Approve). */
const Labels: Record<'ru' | 'kz', readonly string[]> = {
  ru: ['Просмотр', 'Заполнение', 'Заполнение и подача', 'Заполнение, подача и согласование'],
  kz: ['Қарау', 'Толтыру', 'Толтыру және жіберу', 'Толтыру, жіберу және келісу'],
};

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я|Имя|Пайдаланушы/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль|Құпиясөз/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Вподобання профілю (`/me/preferences/{key}`, BE-20): `null` — зняти. Сервер перекриває localStorage. */
async function setPreference(page: Page, key: 'language' | 'theme', value: unknown): Promise<void> {
  const url = `/api/v1/me/preferences/${key}`;
  const response =
    value === null
      ? await page.request.delete(url)
      : await page.request.put(url, { data: JSON.stringify(value), headers: { 'Content-Type': 'application/json' } });
  expect([200, 204, 404], `${key}: ${String(response.status())}`).toContain(response.status());
}

/** Збережене значення вподобання до тесту (`null` — не було), щоб повернути стенд як був. */
async function preference(page: Page, key: 'language' | 'theme'): Promise<unknown> {
  const response = await page.request.get('/api/v1/me/preferences');
  expect(response.ok(), `GET preferences: ${String(response.status())}`).toBe(true);
  const all = (await response.json()) as { key: string; value?: unknown }[];

  return all.find((p) => p.key === key)?.value ?? null;
}

async function ensureScheme(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  await expect(page.locator('html'), `тема ${scheme} не застосувалась`).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
}

interface FitReport {
  readonly labels: string[];
  readonly problems: string[];
}

async function openParticipantDialog(page: Page): Promise<void> {
  await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}&panel=edit-assignments`);
  await page.getByTestId('add-participant').click({ timeout: 60_000 });
  await expect(page.getByTestId('participant-modal')).toBeVisible({ timeout: 15_000 });
}

async function measure(page: Page): Promise<FitReport> {
  return page.getByTestId('participant-modal').evaluate((stack) => {
    const problems: string[] = [];
    const content = stack.closest('.mantine-Modal-content');
    if (!(content instanceof HTMLElement)) return { labels: [], problems: ['немає .mantine-Modal-content'] };
    const box = content.getBoundingClientRect();
    const inside = (r: DOMRect): boolean => r.left >= box.left - 0.5 && r.right <= box.right + 0.5 && r.top >= box.top - 0.5 && r.bottom <= box.bottom + 0.5;
    const radios = Array.from(stack.querySelectorAll<HTMLInputElement>('input[type="radio"]'));
    if (radios.length !== 4) problems.push(`радіо рівнів: ${String(radios.length)}, очікувано 4`);
    const labels: string[] = [];
    for (const radio of radios) {
      const label = stack.querySelector<HTMLElement>(`label[for="${CSS.escape(radio.id)}"]`);
      if (label === null) {
        problems.push(`радіо ${radio.value}: немає label[for]`);
        continue;
      }
      const text = (label.textContent ?? '').trim();
      labels.push(text);
      const r = label.getBoundingClientRect();
      if (!inside(r)) {
        problems.push(`«${text}»: підпис [${r.left.toFixed(0)}..${r.right.toFixed(0)}] поза діалогом [${box.left.toFixed(0)}..${box.right.toFixed(0)}]`);
      }
      for (const el of [label, ...Array.from(label.querySelectorAll<HTMLElement>('*'))]) {
        if (el.scrollWidth > el.clientWidth + 1 && el.clientWidth > 0) {
          problems.push(`«${text}»: обрізано (${el.tagName}.${el.className}: scrollWidth ${String(el.scrollWidth)} > clientWidth ${String(el.clientWidth)})`);
          break;
        }
      }
    }
    for (const el of [content, ...Array.from(content.querySelectorAll<HTMLElement>('.mantine-Modal-body'))]) {
      if (el.scrollWidth > el.clientWidth + 1) problems.push(`горизонтальна прокрутка: ${el.className} ${String(el.scrollWidth)} > ${String(el.clientWidth)}`);
    }
    if (document.documentElement.scrollWidth > document.documentElement.clientWidth + 1) problems.push('горизонтальна прокрутка сторінки');

    return { labels, problems };
  });
}

async function adminPage(browser: Browser): Promise<Page> {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  await signIn(page);

  return page;
}

test.describe('Рівні учасника документа влазять у діалог (P2 10.11)', () => {
  test.skip(PeriodKey === '' || DocumentId === '', 'ECR_E2E_OPTIONAL: стенда немає (tools/e2e-stand.ps1).');
  test.describe.configure({ mode: 'serial', timeout: 240_000 });

  for (const lang of ['ru', 'kz'] as const) {
    test(`рівні учасника влазять у діалог: ${lang}, світла і темна тема`, async ({ browser }, testInfo) => {
      const page = await adminPage(browser);
      const before = { language: await preference(page, 'language'), theme: await preference(page, 'theme') };
      await setPreference(page, 'language', lang);
      try {
        for (const scheme of ['light', 'dark'] as const) {
          await setPreference(page, 'theme', scheme);
          await page.emulateMedia({ colorScheme: scheme });
          await openParticipantDialog(page);
          await ensureScheme(page, scheme);
          const content = page.locator('.mantine-Modal-content').filter({ has: page.getByTestId('participant-modal') });
          await content.screenshot({ path: testInfo.outputPath(`participant-levels-${lang}-${scheme}.png`), animations: 'disabled' });

          const report = await measure(page);
          expect(report.labels, `${lang}/${scheme}: повні підписи рівнів`).toEqual([...Labels[lang]]);
          expect(report.problems, `${lang}/${scheme}:\n${report.problems.join('\n')}`).toEqual([]);
          await page.keyboard.press('Escape');
        }
      } finally {
        await setPreference(page, 'language', before.language);
        await setPreference(page, 'theme', before.theme);
        await page.context().close();
      }
    });
  }
});
