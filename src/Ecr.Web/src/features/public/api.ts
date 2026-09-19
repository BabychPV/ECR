import { useEffect, useState } from 'react';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * Публічні дані екрана входу (`BE-07`, `GET /api/v1/public/bootstrap`).
 *
 * ⚠ Тип береться зі ЗГЕНЕРОВАНОЇ схеми, а не пишеться руками. Саме рукописні
 * типи відповідей і дали `A7-34`/`A7-35`/`A7-36`: поле, назване інакше, ніж на
 * сервері, нічого не ламає — воно просто `undefined`, а компілятор обіцяв
 * рядок.
 */
export type PublicBootstrap = components['schemas']['PublicBootstrapResponse'];

/**
 * Що показувати, доки (або якщо) сервер не відповів.
 *
 * ⛔ Обидва способи входу УВІМКНЕНІ, а не вимкнені. Це не оптимізм: єдиний
 * наслідок помилки в цей бік — кнопка, яка дасть 401 з поясненням; наслідок
 * протилежного — форма входу БЕЗ ЖОДНОГО способу увійти через збій мережі на
 * допоміжному запиті. Друге не має способу виправити себе з боку користувача.
 *
 * ⚠ Версія порожня, і через це підвал із нею просто не малюється — вигадувати
 * «1.0.0» тут означало б показувати число, якого ніхто не питав.
 */
export const SIGN_IN_FALLBACK: PublicBootstrap = {
  productVersion: '',
  languages: [],
  windowsSignInEnabled: true,
  localSignInEnabled: true,
};

/** Один анонімний запит; викликається до будь-якої автентифікації. */
export function fetchPublicBootstrap(): Promise<PublicBootstrap> {
  return apiFetch<PublicBootstrap>('/api/v1/public/bootstrap');
}

/**
 * Публічні дані для екрана входу.
 *
 * ⛔ `useState`/`useEffect`, а НЕ `useQuery`, і це не стильовий вибір.
 * `LoginPage` — єдиний екран, який рендериться і поза `QueryClientProvider`
 * (компонентні тести піднімають його під самими `MantineProvider` +
 * `MemoryRouter`), а `useQuery` без провайдера кидає на рендері. Хук із
 * React Query перетворив би допоміжний запит на умову працездатності форми
 * входу — рівно навпаки до того, чого він має досягти.
 *
 * ⚠ Відмова НЕ повертається викликачеві й ніде не показується: це допоміжні
 * дані, а не вміст екрана. Червона смуга «не вдалося завантажити bootstrap»
 * над робочою формою входу лякала б без жодної дії, яку можна вчинити.
 */
export function usePublicBootstrap(): PublicBootstrap {
  const [value, setValue] = useState<PublicBootstrap>(SIGN_IN_FALLBACK);

  useEffect(() => {
    let live = true;

    fetchPublicBootstrap()
      .then((data) => {
        // ⚠ Перевірка форми, а не довіра типу: `apiFetch` типізований
        // дженериком, тобто на рантаймі не перевіряє НІЧОГО. Відповідь іншої
        // форми (проксі, сторінка входу оператора зв'язку, підмінений у тесті
        // `fetch`) інакше дала б `languages.map` по `undefined` і білий екран
        // замість форми входу.
        if (!live || data === null || typeof data !== 'object') return;

        setValue({
          productVersion: typeof data.productVersion === 'string' ? data.productVersion : '',
          languages: Array.isArray(data.languages) ? data.languages : [],
          windowsSignInEnabled: data.windowsSignInEnabled !== false,
          localSignInEnabled: data.localSignInEnabled !== false,
        });
      })
      .catch(() => {
        // Мовчки: див. ⚠ вище.
      });

    return () => {
      live = false;
    };
  }, []);

  return value;
}
