import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { Routes } from './routes';

/**
 * Знімки всіх маршрутів під двома ролями × двома темами × двома щільностями
 * (`D-142`).
 *
 * ⛔ Сенс не в «подивитися, як виглядає». Знімок під ДВОМА ролями відповідає
 * на питання, яке інакше перевіряють вручну перед кожним релізом: чи бачить
 * оператор те, чого не має бачити. Порожній екран під оператором і повний під
 * адміністратором — доказ; повний під обома — знахідка.
 *
 * ### ✎ Знеколіреного знімка тут НЕМАЄ — і ніколи не було
 *
 * ⛔ На цьому місці стояла обіцянка «друга половина — знеколірений знімок
 * кожного маршруту (`ФВ-14.18`), артефакт, який можна відкрити через півроку».
 * Код її не виконував: `normalize(shot)` рахував сіру копію і **викидав** її,
 * записуючи у файл `*.grey.txt` рівно два числа — розміри знімка. Жодного
 * `expect` над результатом не було, дивитися в «артефакті» не було на що.
 *
 * ⚠ Ціна цієї бездіяльної половини виміряна на живому стенді, не оцінена:
 * `/admin/ui-strings` рендерить увесь каталог рядків (1314 рядків, сторінка
 * 1280×49 641 CSS-пікселів), тож `fullPage`-знімок при `deviceScaleFactor: 2`
 * — це 2560×99 282 = **254 млн пікселів**, 11.5 МБ PNG. `normalize()` на
 * ньому — **34.3 с** і ~2 ГБ `Float64Array`, поверх 19.4 с самого знімка. При
 * бюджеті прогону 90 с (`test.slow()`) один цей маршрут з'їдав більше
 * половини, і сценарії ролі `admin` падали за таймаутом ПРОГОНУ 50/50 —
 * різний сценарій щоразу.
 *
 * ⛔ Прибрано саме обчислення, не перевірку: перевіряти там не було чого.
 * Справжній гейт `ФВ-14.18` — у `cellStates.spec.ts`, і він СТВЕРДЖУЄ
 * (`structuralDifference`/`rawDifference`, `greyscale.ts`): стани комірки
 * мають лишатися відрізнюваними без кольору. Ця картка його не торкається.
 *
 * ⚠ Те, що лишилося невиправленим і названо прямо: `/admin/ui-strings` віддає
 * і малює ВЕСЬ каталог однією таблицею без сторінок — 49 641 піксель висоти.
 * Це дефект самої сторінки (і для людини, не лише для знімка), але він поза
 * межами цієї картки.
 *
 * ⚠ Прогін нічого не ПОРІВНЮЄ з еталоном. Порівняння знімків на різних
 * машинах дає різницю в шрифтах і згладжуванні, і такий гейт вимикають на
 * третьому фальшивому падінні. Тут перевіряється інше: сторінка **не
 * порожня** і не показала аварійного стану — а самі знімки лягають в артефакти
 * для людини.
 */
const Roles = [
  { name: 'operator', user: 'e2e-operator', password: 'E2E-Operator-Work-2026!' },
  { name: 'admin', user: 'e2e-admin', password: 'E2E-Admin-Work-2026!' },
];

/*
 * ⚠ Перелік маршрутів переїхав у `./routes.ts`, а сторож дрейфу — у vitest
 * (`src/app/__tests__/routes.test.ts`). Причина в кінці цього файла.
 */

const OutputDirectory = path.resolve('../../artifacts/screenshots');

/** Вхід без миші — той самий шлях, що й у справжнього користувача. */
async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await page.getByLabel(/User name|Ім'я/i).fill(user);

  // ⚠ Q-260 (змержено вже ПІСЛЯ написання цього файла) додав кнопці-тумблеру
  // видимості пароля `aria-label="Toggle password visibility"`
  // (`LoginPage.tsx`, `passwordToggleProps`) — і `getByLabel(/Password|Пароль/i)`
  // відтоді резолвиться у ДВА елементи: саме поле і цю кнопку. Дослівно:
  //   strict mode violation: getByLabel(/Password|Пароль/i) resolved to 2 elements
  // Роль розрізняє їх однозначно: поле вводу — `textbox`, кнопка — `button`.
  // Той самий рядок уже стоїть у `security.spec.ts` і `keyboardPath.spec.ts`;
  // сюди він не доїхав, і саме тому знімки падали на вході, а не на маршруті.
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/**
 * Відмова в праві на сторінці — усі три її подачі.
 *
 * ⛔ Без цього знімок відмови проходив: заголовок сторінки малюється й над
 * `403`, а цикл нижче чекає саме заголовка. Так знімок адміна на
 * `/admin/sources` був знімком `ECR-AUTH-0403` (роль стенда мала
 * `Integration.Manage`, але не `Integration.View`).
 *
 * Подачі, і всі три — `role="alert"`:
 * - `ErrorAlert` (відмова запиту всередині сторінки) — несе код
 *   `ECR-AUTH-0403` у `<Code>`;
 * - `ForbiddenState` (`AsyncBoundary`, стан `no-permission`) — теж код;
 * - `AccessDeniedPage` (`RouteGuard`, відмова маршруту) — коду НЕ несе, лише
 *   текст `err.ECR-AUTH-0403` і назву права. Тому другий шаблон — текст
 *   каталогу англійською: стенд працює мовою `en`, інших мов цього ключа в
 *   сіді немає.
 */
const AccessDenied = /ECR-AUTH-0403|You do not have permission for this action/;

async function expectNoAccessDenied(page: Page, where: string): Promise<void> {
  const denied = page.getByRole('alert').filter({ hasText: AccessDenied });

  // ⚠ `soft`: прогін іде далі по маршрутах і називає ВСІ відмови, а не першу.
  expect.soft(
    await denied.count(),
    `${where}: на сторінці відмова в праві — знімок показав би 403, а не екран`,
  ).toBe(0);
}

test.describe('Знімки маршрутів (D-142)', () => {
  // ⚠ Цей гейт більше не вирішує долю набору: без стенда ВЕСЬ набір падає в
  // `globalSetup.ts`. Він лишається робочим лише під `ECR_E2E_OPTIONAL`, коли
  // пропуск оголошений свідомо, — і саме тому в тексті названа змінна: інакше
  // рядок «немає стенда» знову читався б як норма.
  test.skip(
    (process.env['ECR_E2E_PERIOD'] ?? '') === '',
    'ECR_E2E_OPTIONAL: стенда немає, знімки пропущено. Стенд: tools/e2e-stand.ps1.',
  );

  for (const role of Roles) {
    for (const scheme of ['light', 'dark'] as const) {
      for (const density of ['compact', 'comfortable'] as const) {
        test(`${role.name} · ${scheme} · ${density}`, async ({ page }) => {
          test.slow();

          await mkdir(OutputDirectory, { recursive: true });

          // ⛔ Тема і щільність ставляться ДО входу, у сховище: після входу
          // їх міняє меню користувача, а це знову шлях через інтерфейс, який
          // цей прогін і має зняти, а не пройти.
          await page.goto('/login');
          // ⚠ Об'єкт, а не кортеж: під `noUncheckedIndexedAccess` елемент
          // масиву має тип `T | undefined`, і деструктуризація в браузері
          // мовчки поклала б `undefined` у сховище.
          await page.evaluate(
            (preferences: { colour: string; rows: string }) => {
              localStorage.setItem('mantine-color-scheme-value', preferences.colour);
              localStorage.setItem('ecr.density', preferences.rows);
            },
            { colour: scheme, rows: density },
          );

          await signIn(page, role.user, role.password);

          for (const route of Routes) {
            await page.goto(route.path);

            // ⚠ Чекаємо саме ЗАГОЛОВОК, а не мережу: `networkidle` на екрані
            // з опитуванням задач не настає ніколи, і прогін падав би за
            // таймаутом там, де сторінка давно готова.
            await expect(
              page.getByRole('heading').first(),
              `${route.name}: заголовка немає — сторінка не відрендерилася`,
            ).toBeVisible({ timeout: 30_000 });

            // ⛔ Аварійного стану бути не має. Порожній екран під роллю без
            // прав — законно (пункт меню туди й не веде); аварія — ні.
            const crashed = await page.getByText(/Unhandled|TypeError|is not a function/i).count();
            expect(crashed, `${route.name}: на сторінці слід аварії`).toBe(0);

            // ⚠ Лише під адміністратором: у ролі стенда всі права маршрутів,
            // тож відмова тут — дефект стенда або продукту. Оператор на
            // адмін-маршруті бачить відмову законно (див. шапку файла).
            if (role.name === 'admin') await expectNoAccessDenied(page, route.name);

            const shot = await page.screenshot({ fullPage: true });
            const name = `${role.name}-${scheme}-${density}-${route.name}`;

            await writeFile(path.join(OutputDirectory, `${name}.png`), shot);
          }
        });
      }
    }
  }

  /*
   * Шухляда з'єднання (`/admin/sources?panel=<code>`, вкладка Connection).
   *
   * ⚠ На стенді з'єднань немає, і сідом його не заводимо: це дані, а не
   * конфігурація. Тож з'єднання створюється через API тим самим входом, що й
   * у людини (`page.request` ділить куки з вкладкою), і прибирається за собою
   * у `finally`. Лічильники нового рядка — нулі, тож `DELETE` не впирається в
   * `409 dataSourceInUse`.
   *
   * ⚠ Лише `admin`: без `Integration.Manage` створити з'єднання нікому, а
   * порожній перелік під оператором уже знімає цикл вище.
   */
  for (const scheme of ['light', 'dark'] as const) {
    test(`admin · ${scheme} · шухляда з'єднання`, async ({ page }) => {
      test.slow();

      await mkdir(OutputDirectory, { recursive: true });

      await page.goto('/login');
      await page.evaluate(
        (colour: string) => {
          localStorage.setItem('mantine-color-scheme-value', colour);
          localStorage.setItem('ecr.density', 'compact');
        },
        scheme,
      );

      await signIn(page, 'e2e-admin', 'E2E-Admin-Work-2026!');

      // ⚠ Унікальний код: прогін, що впав до `finally`, не має ламати наступний
      // через `dataSourceCodeTaken`.
      const code = `E2E_DS_${scheme.toUpperCase()}_${Date.now()}`;
      const created = await page.request.post('/api/v1/data-sources', {
        data: {
          code,
          nameL10n: { en: 'E2E PI server' },
          transport: 'PiWebApi',
          endpoint: 'https://pi.e2e.invalid/piwebapi',
          catalog: 'E2E_AF',
          maxParallel: 4,
        },
      });

      expect(created.status(), `створення з'єднання: ${await created.text()}`).toBe(201);

      const source = (await created.json()) as { id: number; rowVersion: string };

      try {
        await page.goto(`/admin/sources?panel=${code}`);

        const drawer = page.getByRole('dialog');

        await expect(drawer, 'шухляда не відкрилася адресою').toBeVisible({ timeout: 30_000 });
        await expect(drawer.getByRole('tab', { selected: true })).toBeVisible();
        await expect(drawer.getByText('https://pi.e2e.invalid/piwebapi')).toBeVisible();

        const crashed = await page.getByText(/Unhandled|TypeError|is not a function/i).count();
        expect(crashed, 'шухляда з\'єднання: на сторінці слід аварії').toBe(0);
        await expectNoAccessDenied(page, 'шухляда з\'єднання');

        const shot = await page.screenshot();

        await writeFile(path.join(OutputDirectory, `admin-${scheme}-compact-sources-drawer.png`), shot);
      } finally {
        const removed = await page.request.delete(`/api/v1/data-sources/${source.id}`, {
          headers: { 'If-Match': `"${source.rowVersion}"` },
        });

        expect(removed.ok(), `прибирання з'єднання: ${removed.status()}`).toBe(true);
      }
    });
  }
});

/*
 * ⛔ Сторож «перелік маршрутів не відстав від роутера» ЖИВ ТУТ і не виконувався
 * ніде. Стенд йому не потрібен — він читає `src/app/router.tsx` файлом, — але
 * лежав усередині `describe`, гейтованого на `ECR_E2E_PERIOD`, і мовчки
 * пропускався разом зі знімками. Його власний коментар називав це «перевіркою,
 * яка виглядає повною і такою не є», перебуваючи рівно в цьому стані.
 *
 * ⛔ Він переїхав у vitest — `src/app/__tests__/routes.test.ts`, — а не в
 * сусідній `describe` без гейта. Причина: тут його виконував би лише
 * `npm run test:e2e`, тобто команда, яку без стенда не запускають, і в конвеєрі
 * (крок «Прогони в браузері» — `ci-exempt`) його не було б однаково. Під vitest
 * він іде в кожному `npm run test` і в кожному прогоні конвеєра — без браузера,
 * без бази, за мілісекунди.
 *
 * ⚠ Спільним лишився ЛИШЕ перелік (`./routes.ts`): якщо його розкопіювати,
 * сторож стерегтиме свою копію, а знімки зніматимуть іншу.
 */
