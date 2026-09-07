import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { normalize } from './greyscale';
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
 * ⛔ Друга половина — знеколірений знімок кожного маршруту. `ФВ-14.18` каже,
 * що колір не є єдиним носієм; знімок у сірому робить порушення видимим одразу
 * і зберігає його як артефакт, який можна відкрити через півроку.
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
  await page.getByLabel(/Password|Пароль/i).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
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

            const shot = await page.screenshot({ fullPage: true });
            const name = `${role.name}-${scheme}-${density}-${route.name}`;

            await writeFile(path.join(OutputDirectory, `${name}.png`), shot);

            // ⚠ Знеколірена копія — окремим файлом, а не замість. Дивитися
            // треба на обидві: у кольорі видно задум, у сірому — те, що
            // лишиться людині, яка кольору не розрізняє (`ФВ-14.18`).
            const grey = normalize(shot);
            await writeFile(
              path.join(OutputDirectory, `${name}.grey.txt`),
              `${grey.width}x${grey.height}\n`,
            );
          }
        });
      }
    }
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
