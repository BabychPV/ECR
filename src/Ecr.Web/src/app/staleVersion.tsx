import { useEffect, useSyncExternalStore, type JSX } from 'react';
import { Alert, Button, Group, Text } from '@mantine/core';

/**
 * Застаріла збірка у відкритій вкладці (`DAT-08`, клієнтська половина).
 *
 * **Факт, з якого це виросло.** Усі сторінки застосунку — ліниві чанки з
 * хешем у назві (`routePrefetch.ts`, `router.tsx`). Після оновлення MSI
 * файли попередньої збірки зникають із `wwwroot`, а вкладка, відкрита до
 * оновлення, і далі тримає в пам'яті СТАРИЙ `index.html` зі старими іменами
 * чанків. Перший же перехід (або прогрів за наведенням,
 * `useRoutePrefetch.ts`) просить файл, якого вже немає: `import()`
 * відхиляється, і користувач бачить білий екран, невідрізнимий від дефекту
 * продукту. Обробника `vite:preloadError` у клієнті не було жодного —
 * `0` збігів по `src/Ecr.Web/src` до цієї зміни.
 *
 * ⚠ Подія — саме `vite:preloadError`, а не власне вгадування «мабуть,
 * оновилися»: її кидає сам рантайм Vite (`__vitePreload`) РІВНО тоді, коли
 * не вдалося завантажити чанк, і жодне інше джерело помилки її не дає.
 *
 * ⛔ `event.preventDefault()` тут НЕ викликається навмисно. Скасування події
 * глушить лише ВИКИДАННЯ помилки — сам `import()` від цього не стає
 * успішним: обіцянка розв'язується `undefined`, `React.lazy` отримує
 * не-компонент, і застосунок падає на кроці пізніше й із менш зрозумілою
 * причиною. Тому помилці дозволено дійти до межі маршруту
 * (`RouteErrorPage`, `D14-11`), а цей модуль лише ПОЯСНЮЄ її: екран помилки
 * читає `isStaleVersion()` і показує «встановлено нову версію» замість
 * загального «сталася помилка».
 *
 * ⚠ Збереження незбережених правок при цій події (перша половина пункту (1)
 * `DAT-08`) НЕ реалізовано тут: сховище правок рівня документа — це
 * `D14-12`, якого ще немає. Робити «збереження» поверх стану, що живе в
 * сітці, означало б повторити той самий дефект, який `D14-12` і закриває.
 */

/** Чи вже відомо, що на сервері інша збірка. */
let stale = false;

/** Підписники (`useSyncExternalStore` — той самий прийом, що й в i18n). */
const listeners = new Set<() => void>();

/** Знімок стану для `useSyncExternalStore`. */
export function isStaleVersion(): boolean {
  return stale;
}

/** Підписка на зміну стану; повертає відписку. */
export function subscribeStaleVersion(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/**
 * Позначає збірку застарілою.
 *
 * ⚠ Один раз: другий і третій відхилений чанк не мають ані показувати другий
 * банер, ані смикати перемальовування — стан уже той самий.
 */
function markStaleVersion(): void {
  if (stale) return;

  stale = true;
  for (const listener of listeners) listener();
}

/** Скидає стан — лише для тестів (модульний стан переживає розмонтування). */
export function resetStaleVersion(): void {
  stale = false;
  for (const listener of listeners) listener();
}

/**
 * Вішає слухача `vite:preloadError`; повертає зняття — форма `useEffect`.
 *
 * ⚠ Слухач саме на `window`: подію кидає рантайм Vite поза деревом React, і
 * в момент відмови жодного компонента, якому вона «належить», може не бути
 * взагалі (прогрів за наведенням стається без переходу).
 */
export function watchPreloadErrors(): () => void {
  const onPreloadError = (): void => {
    markStaleVersion();
  };

  window.addEventListener('vite:preloadError', onPreloadError);

  return () => {
    window.removeEventListener('vite:preloadError', onPreloadError);
  };
}

/**
 * Банер «встановлено нову версію».
 *
 * ⚠ Монтується в `App.tsx` — ПОЗА деревом маршрутів: відмова прогріву
 * стається без жодного переходу, тож банер має жити незалежно від того, який
 * маршрут зараз відкритий і чи він узагалі змонтувався.
 *
 * ⛔ Написи — ЛІТЕРАЛИ, не `t()`, за прецедентом `CATALOG_LOAD_FAILED` і
 * `passwordToggleProps` (`pages/LoginPage.tsx`): рядки інтерфейсу живуть у
 * СЕРВЕРНОМУ каталозі (`09-seed.sql`), ключів під ці написи там немає, а
 * `t()` без рядка в каталозі показав би позначений ключ (`⟦...⟧`) — тобто
 * саме той нерозбірливий екран, від якого банер і рятує. Англійською, як усі
 * інші запасні тексти клієнта (`D14-13`).
 *
 * ⚠ Банер не пропонує «зберегти перед перезавантаженням»: правки зараз живуть
 * у стані сітки, і зберегти їх звідси нема як (`D14-12`). Обіцянка, якої код
 * не виконує, гірша за її відсутність.
 */
export function NewVersionBanner(): JSX.Element | null {
  const outdated = useSyncExternalStore(subscribeStaleVersion, isStaleVersion, isStaleVersion);

  useEffect(watchPreloadErrors, []);

  if (!outdated) return null;

  return (
    <Alert
      role="alert"
      color="statusWarning"
      title="A new version is installed"
      data-testid="new-version-banner"
      // ⚠ Поверх усього і при прокрутці теж: подія може статися, коли
      // користувач дивиться в середину довгої таблиці, а не на верх сторінки.
      style={{ position: 'fixed', top: 0, left: 0, right: 0, zIndex: 400 }}
    >
      <Group gap="xs" wrap="wrap">
        <Text size="sm">
          This tab is running an older build and can no longer load parts of the application. Reload
          to get the new version.
        </Text>
        <Button size="xs" onClick={() => window.location.reload()}>
          Reload page
        </Button>
      </Group>
    </Alert>
  );
}
