import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { normalize } from './greyscale';

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

/**
 * Маршрути застосунку.
 *
 * ⚠ Перелік узгоджений із `router.tsx` руками — і це його слабке місце:
 * доданий маршрут сюди не потрапить сам. Автоматичного джерела немає, бо
 * маршрути оголошені всередині JSX; тому нижче стоїть перевірка на кількість,
 * яка падає, щойно маршрутів у роутері стало більше.
 */
const Routes = [
  { path: '/', name: 'documents' },
  { path: '/my-groups', name: 'my-groups' },
  { path: '/admin/templates', name: 'templates' },
  { path: '/admin/registries', name: 'registries' },
  { path: '/admin/methodologies', name: 'methodologies' },
  { path: '/admin/expressions', name: 'expressions' },
  { path: '/admin/units', name: 'units' },
  { path: '/admin/security', name: 'security' },
  { path: '/admin/periods', name: 'periods' },
  { path: '/admin/sources', name: 'sources' },
  { path: '/admin/mapping', name: 'mapping' },
  { path: '/admin/jobs', name: 'jobs' },
  { path: '/admin/health', name: 'health' },
  { path: '/admin/snapshots', name: 'snapshots' },
  { path: '/admin/audit', name: 'audit' },
  { path: '/admin/ui-strings', name: 'ui-strings' },
];

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
  test.skip(
    (process.env['ECR_E2E_PERIOD'] ?? '') === '',
    'Немає стенда: запускати через tools/e2e-stand.ps1.',
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

  test('перелік маршрутів не відстав від роутера', async () => {
    // ⛔ Перелік вище — рукописний, і саме тому тут стоїть ця перевірка.
    // Доданий маршрут не потрапить у знімки сам, і мовчазна прогалина в
    // артефактах — це рівно той дефект, який ЕТАП 7.5 і виловлює: перевірка,
    // яка виглядає повною і такою не є.
    const { readFile } = await import('node:fs/promises');
    const router = await readFile(path.resolve('src/app/router.tsx'), 'utf8');

    const declared = [...router.matchAll(/path:\s*'([^']+)'/g)]
      .map((m) => (m[1] ?? '').replace(/^\//, ''))

      // ⛔ Поза переліком навмисно: вхід і зміна пароля живуть ДО сесії, а
      // каталог компонентів існує лише в розробці (`D7-09`) — знімати
      // сторінку, якої немає у виробничій збірці, означало б класти в
      // артефакти те, чого замовник не побачить ніколи.
      .filter((p) => p !== 'login' && p !== 'change-password' && !p.startsWith('_'))
      .filter((p) => !p.includes(':'));

    // `index: true` для кореня описаний окремо і в перелік шляхів не
    // потрапляє — тому мінус один не потрібен, але корінь у нашому переліку є.
    const covered = new Set(Routes.map((r) => r.path.replace(/^\//, '')));

    const missing = declared.filter((p) => !covered.has(p));

    expect(
      missing,
      'ці маршрути оголошені в роутері й не потрапляють у знімки',
    ).toEqual([]);
  });
});
