/**
 * Сповіщення між вкладками про зміну сеансу (AN-108 / S2-05).
 *
 * ⛔ Cookie сеансу спільна для всіх вкладок браузера. Сценарій спільного ПК: A працює у вкладці 2 (є незбережені
 * правки), у вкладці 1 виходить і входить B. Без сповіщення вкладка 2 показувала б дані A, а її автозбереження чи
 * маячок `beforeunload` віз би правки A вже з cookie B — і журнал правок приписав би B чужі значення.
 *
 * ⚠ Два транспорти: `BroadcastChannel` (основний) і подія `storage` (запасний — без `BroadcastChannel`). Обидва
 * доставляють лише ІНШИМ вкладкам; повторна доставка безпечна — отримувач ідемпотентний (`abandonSwitchedSession`).
 *
 * ⚠ Один екземпляр каналу на вкладку: повідомлення не повертається до того самого об'єкта, що його надіслав, а
 * другий екземпляр у тій самій вкладці отримав би власне сповіщення як чуже.
 */

const ChannelName = 'ecr.session';

/** Ключ `localStorage` запасного транспорту: значення — лише позначка часу, жодних даних сеансу. */
export const SessionChangedStorageKey = 'ecr.session.changed';

let channel: BroadcastChannel | null | undefined;

function sessionChannel(): BroadcastChannel | null {
  if (channel !== undefined) return channel;
  channel = typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel(ChannelName);
  // ⚠ У Node (тести) відкритий канал тримав би цикл подій живим.
  (channel as unknown as { unref?: () => void } | null)?.unref?.();
  return channel;
}

/** Сповіщає інші вкладки: сеанс змінився (вихід, вхід). */
export function announceSessionChange(): void {
  try {
    sessionChannel()?.postMessage('changed');
  } catch {
    // Канал закрито — лишається запасний транспорт.
  }
  try {
    window.localStorage.setItem(SessionChangedStorageKey, `${String(Date.now())}:${String(Math.random())}`);
  } catch {
    // Приватний режим / заблоковане сховище: лишається основний транспорт.
  }
}

/** Підписується на зміну сеансу в ІНШІЙ вкладці; повертає відписку. */
export function listenSessionChange(onChange: () => void): () => void {
  const bus = sessionChannel();
  const onMessage = (): void => onChange();
  const onStorage = (event: StorageEvent): void => {
    if (event.key === SessionChangedStorageKey) onChange();
  };

  bus?.addEventListener('message', onMessage);
  window.addEventListener('storage', onStorage);

  return () => {
    bus?.removeEventListener('message', onMessage);
    window.removeEventListener('storage', onStorage);
  };
}

/** Скидає канал — лише для тестів. */
export function resetSessionChannelForTests(): void {
  channel?.close();
  channel = undefined;
}
