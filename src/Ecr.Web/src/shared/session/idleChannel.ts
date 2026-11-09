/**
 * Спільний годинник бездіяльності між вкладками (D-471).
 *
 * ⛔ Cookie сеансу одна на всі вкладки браузера, і сервер відраховує бездіяльність саме для неї. Тому й
 * клієнтський годинник один: клацання у вкладці A тримає живою вкладку B, а вихід в одній — вихід у всіх.
 *
 * ⚠ Два транспорти, як і в `sessionChannel`: `BroadcastChannel` (основний) і подія `storage` (запасний). Обидва
 * доставляють лише ІНШИМ вкладкам; повторна доставка безпечна — отримувачі ідемпотентні (беруть більший момент,
 * вихід — один раз).
 *
 * ⚠ Модуль без імпортів: його читає і транспорт (`api/client.ts`), і екран — цикл модулів неможливий.
 */

/** Повідомлення між вкладками. */
export type IdleMessage = { type: 'activity'; at: number } | { type: 'expired'; minutes?: number };

const ChannelName = 'ecr.idle';

/** Останній момент дії людини в будь-якій вкладці (мс від епохи). */
export const IdleLastActivityKey = 'ecr.idle.last';

/** Останній сигнал активності, надісланий на сервер будь-якою вкладкою (мс від епохи). */
export const IdleLastTouchKey = 'ecr.idle.touch';

/** Позначка «сеанс завершено через бездіяльність» (запасний транспорт). */
export const IdleExpiredKey = 'ecr.idle.expired';

let channel: BroadcastChannel | null | undefined;

function idleChannel(): BroadcastChannel | null {
  if (channel !== undefined) return channel;
  channel = typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel(ChannelName);
  // ⚠ У Node (тести) відкритий канал тримав би цикл подій живим.
  (channel as unknown as { unref?: () => void } | null)?.unref?.();
  return channel;
}

function readNumber(key: string): number | null {
  try {
    const raw = window.localStorage.getItem(key);
    const value = raw === null ? Number.NaN : Number(raw.split(':')[0]);
    return Number.isFinite(value) ? value : null;
  } catch {
    return null;
  }
}

function writeValue(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // Приватний режим / заблоковане сховище: лишається основний транспорт.
  }
}

function post(message: IdleMessage): void {
  try {
    idleChannel()?.postMessage(message);
  } catch {
    // Канал закрито — лишається запасний транспорт.
  }
}

/** Спільний момент останньої дії; `null` — ще не було. */
export function readSharedLastActivity(): number | null {
  return readNumber(IdleLastActivityKey);
}

/** Спільний момент останнього сигналу активності на сервер; `null` — ще не було. */
export function readSharedLastTouch(): number | null {
  return readNumber(IdleLastTouchKey);
}

/** Записує момент сигналу активності на сервер (лише сховище — іншим вкладкам повідомляти нічого). */
export function writeSharedLastTouch(at: number): void {
  writeValue(IdleLastTouchKey, String(at));
}

/** Повідомляє інші вкладки про дію людини в цій. */
export function announceActivity(at: number): void {
  writeValue(IdleLastActivityKey, String(at));
  post({ type: 'activity', at });
}

/** Повідомляє інші вкладки: сеанс завершено через бездіяльність. */
export function announceIdleExpired(minutes?: number): void {
  writeValue(IdleExpiredKey, `${String(Date.now())}:${String(minutes ?? '')}`);
  post(minutes === undefined ? { type: 'expired' } : { type: 'expired', minutes });
}

/** Підписується на повідомлення ІНШИХ вкладок; повертає відписку. */
export function listenIdleMessages(onMessage: (message: IdleMessage) => void): () => void {
  const bus = idleChannel();

  const onBus = (event: MessageEvent<unknown>): void => {
    const data = event.data as Partial<IdleMessage> | null;
    if (data?.type === 'activity' && typeof data.at === 'number') onMessage({ type: 'activity', at: data.at });
    else if (data?.type === 'expired') {
      onMessage(typeof data.minutes === 'number' ? { type: 'expired', minutes: data.minutes } : { type: 'expired' });
    }
  };

  const onStorage = (event: StorageEvent): void => {
    if (event.newValue === null) return;
    if (event.key === IdleLastActivityKey) {
      const at = Number(event.newValue);
      if (Number.isFinite(at)) onMessage({ type: 'activity', at });
    } else if (event.key === IdleExpiredKey) {
      const minutes = Number(event.newValue.split(':')[1]);
      onMessage(Number.isSafeInteger(minutes) && minutes > 0 ? { type: 'expired', minutes } : { type: 'expired' });
    }
  };

  bus?.addEventListener('message', onBus);
  window.addEventListener('storage', onStorage);

  return () => {
    bus?.removeEventListener('message', onBus);
    window.removeEventListener('storage', onStorage);
  };
}

/** Скидає канал — лише для тестів. */
export function resetIdleChannelForTests(): void {
  channel?.close();
  channel = undefined;
}
