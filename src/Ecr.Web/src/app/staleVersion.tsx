import { useEffect, useSyncExternalStore, type JSX } from 'react';
import { Alert, Button, Group, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import {
  flushUnsaved,
  hasUnsavedChanges,
  UnsavedSettleMs,
  unsavedCount,
} from '@/shared/ui/unsavedSources';

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
 * ✎ `DIRECTIVE-16` §1, «DAT-08 залишок»: при цій події незбережене СПЕРШУ
 * зберігається — тим самим реєстром джерел (`shared/ui/unsavedSources.ts`,
 * `flushUnsaved`), що й при виході з документа (`UnsavedGuard`). Раніше тут
 * стояло «неможливо, бо `D14-12` ще немає»; сховище рівня документа давно є,
 * а банер пропонував «Reload» поверх незбережених правок — тобто перезавантаження
 * мовчки їх викидало.
 *
 * ⚠ Збереження не потребує жодного нового чанка: `PATCH` — це `fetch` до API,
 * а зберігачі (сітка, безхазяйний зріз) уже в пам'яті. Тому воно можливе саме
 * тоді, коли підвантажити частину застосунку вже не можна.
 */

/** Чи вже відомо, що на сервері інша збірка. */
let stale = false;

/**
 * Що сталося з незбереженим, коли збірка виявилась застарілою.
 *
 * - `none` — зберігати не було чого (банер про це мовчить);
 * - `saving` — збереження йде; кнопки перезавантаження ще НЕМАЄ;
 * - `saved` — усе незбережене прийнято сервером;
 * - `failed` — щось лишилось (відмова або таймаут): перезавантаження його
 *   викине, і банер каже це прямо.
 */
type StaleSaveState = 'none' | 'saving' | 'saved' | 'failed';

let saveState: StaleSaveState = 'none';

/** Скільки правок не вдалося зберегти — знімок на момент відмови. */
let unsavedLeft = 0;

/** Підписники (`useSyncExternalStore` — той самий прийом, що й в i18n). */
const listeners = new Set<() => void>();

/** Знімок стану для `useSyncExternalStore`. */
export function isStaleVersion(): boolean {
  return stale;
}

/** Підписка на зміну стану; повертає відписку. */
function subscribeStaleVersion(listener: () => void): () => void {
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

/** Знімок стану збереження для `useSyncExternalStore`. */
function staleSaveState(): StaleSaveState {
  return saveState;
}

function setSaveState(next: StaleSaveState): void {
  saveState = next;
  for (const listener of listeners) listener();
}

/** Скидає стан — лише для тестів (модульний стан переживає розмонтування). */
export function resetStaleVersion(): void {
  stale = false;
  saveState = 'none';
  unsavedLeft = 0;
  for (const listener of listeners) listener();
}

/**
 * Вішає слухача `vite:preloadError`; повертає зняття — форма `useEffect`.
 *
 * ⚠ Слухач саме на `window`: подію кидає рантайм Vite поза деревом React, і
 * в момент відмови жодного компонента, якому вона «належить», може не бути
 * взагалі (прогрів за наведенням стається без переходу).
 *
 * ⛔ Збереження запускається СИНХРОННО в обробнику, до будь-якого рендера:
 * `__vitePreload` кидає подію раніше, ніж відхиляє `import()`, тобто раніше,
 * ніж межа маршруту встигне щось розмонтувати. `flushUnsaved` при цьому
 * відразу кличе зберігачів (`flushAutosave`) — запити вже летять.
 *
 * ⚠ Лише ОДИН раз на застарілу збірку: повторні відмови чанків не мають
 * запускати друге збереження тих самих комірок (два паралельні `PATCH` — той
 * самий аргумент, що в `UnsavedGuard`).
 *
 * @param settleTimeoutMs Скільки чекати на результат збереження — див.
 *   `UnsavedSettleMs`; проп існує заради тестів.
 */
function watchPreloadErrors(settleTimeoutMs: number = UnsavedSettleMs): () => void {
  const onPreloadError = (): void => {
    if (stale) return;

    const dirty = hasUnsavedChanges();

    // ⚠ Стан збереження ставиться ДО `markStaleVersion`: перший же кадр банера
    // має бути «зберігаємо» без кнопки, а не «перезавантажте» на мить.
    saveState = dirty ? 'saving' : 'none';
    markStaleVersion();

    if (!dirty) return;

    void flushUnsaved(settleTimeoutMs).then(
      (saved) => {
        unsavedLeft = saved ? 0 : unsavedCount();
        setSaveState(saved ? 'saved' : 'failed');
      },
      () => {
        // ⚠ Безпечний бік помилки той самий, що й у таймауту: «не збережено».
        unsavedLeft = unsavedCount();
        setSaveState('failed');
      },
    );
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
 * ⛔ Заголовок, основний текст і кнопка — ЛІТЕРАЛИ, не `t()`, за прецедентом
 * `CATALOG_LOAD_FAILED` і `passwordToggleProps` (`pages/LoginPage.tsx`): подія
 * може статися й до того, як каталог доїхав (відмова чанка сторінки входу), а
 * `t()` без каталогу показав би позначений ключ (`⟦...⟧`) — тобто саме той
 * нерозбірливий екран, від якого банер і рятує. Англійською, як усі інші
 * запасні тексти клієнта (`D14-13`).
 *
 * ⚠ Рядок про збереження — навпаки, `t()` з ключами `unsaved.stale*`
 * (`09-seed.sql`, приватна область): він з'являється ЛИШЕ тоді, коли
 * незбережені правки були, а правок без входу й завантаженого каталогу не
 * буває.
 *
 * ⛔ Кнопка перезавантаження — лише ПІСЛЯ спроби збереження: натиснута під час
 * `saving`, вона обірвала б запит, заради якого спроба й робилась.
 */
export function NewVersionBanner({
  settleTimeoutMs = UnsavedSettleMs,
}: { readonly settleTimeoutMs?: number } = {}): JSX.Element | null {
  const outdated = useSyncExternalStore(subscribeStaleVersion, isStaleVersion, isStaleVersion);
  const save = useSyncExternalStore(subscribeStaleVersion, staleSaveState, staleSaveState);

  useEffect(() => watchPreloadErrors(settleTimeoutMs), [settleTimeoutMs]);

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
        {save !== 'none' && (
          <Text size="sm" fw={600} data-testid="new-version-save-state" data-state={save}>
            {save === 'saving' && t('unsaved.staleSaving')}
            {save === 'saved' && t('unsaved.staleSaved')}
            {save === 'failed' && t('unsaved.staleFailed', { count: unsavedLeft })}
          </Text>
        )}
        {save !== 'saving' && (
          <Button onClick={() => window.location.reload()}>
            Reload page
          </Button>
        )}
      </Group>
    </Alert>
  );
}
