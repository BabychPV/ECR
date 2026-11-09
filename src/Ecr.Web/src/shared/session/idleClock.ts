import {
  announceActivity,
  announceIdleExpired,
  listenIdleMessages,
  readSharedLastActivity,
  readSharedLastTouch,
  writeSharedLastTouch,
  type IdleMessage,
} from './idleChannel';

/**
 * Годинник бездіяльності вкладки (D-471): коли попередити, коли вийти, коли повідомити сервер.
 *
 * ⚠ Рахує ЛИШЕ дії людини: клацання, клавіші, коліщатко, дотик (`IdleSignOut`) і запити, що змінюють стан
 * (`setUserActivityListener` у транспорті). Опитування (`refetchInterval`, `usePollWhileVisible`) сюди не доходять
 * узагалі — вони GET, а GET транспорт активністю не вважає, як і сервер.
 *
 * ⚠ Сервер звіряє межу сам (`IdleSession`), тож вкладка мусить повідомляти йому про дії, що до нього не дійшли:
 * `POST /api/v1/session/activity` не частіше за {@link TouchIntervalMs}, з «хвостом» — дія після останнього
 * сигналу таки доїде, щойно мине інтервал. Без хвоста сервер відставав би від вкладки й виводив людину раніше,
 * ніж вона побачила попередження.
 */

/** Як часто вкладки разом повідомляють сервер про дії людини. */
export const TouchIntervalMs = 30_000;

/** Дії частіше за цей проміжок зливаються в одну. */
export const ActivityDebounceMs = 1_000;

/** Попередження — за хвилину до виходу; на межі коротшій за 2 хв — посередині (лише стенд, D-471). */
export function warningLeadMs(timeoutMs: number): number {
  return Math.min(60_000, Math.floor(timeoutMs / 2));
}

/** Фаза годинника. */
export type IdlePhase = 'active' | 'warning' | 'expired';

/** Залежності — щоб годинник жив без DOM, мережі й справжнього часу в тестах. */
export interface IdleClockDeps {
  /** Поточний час, мс від епохи. */
  now: () => number;
  /** Сигнал активності на сервер. */
  touch: () => Promise<unknown>;
  /** Зміна фази; `deadline` — момент виходу (мс від епохи). */
  onPhase: (phase: IdlePhase, deadline: number) => void;
  /** Вихід: `remote` — його вже зробила інша вкладка (або сервер). */
  onExpire: (remote: boolean) => void;
}

/** Годинник бездіяльності однієї вкладки. */
export class IdleClock {
  private last = 0;
  private lastTouch = 0;
  private phase: IdlePhase = 'active';
  private timer: ReturnType<typeof setTimeout> | null = null;
  private touchTimer: ReturnType<typeof setTimeout> | null = null;
  private unsubscribe: (() => void) | null = null;

  constructor(
    private timeoutMs: number,
    private readonly deps: IdleClockDeps,
  ) {}

  /** Момент виходу (мс від епохи). */
  get deadline(): number {
    return this.last + this.timeoutMs;
  }

  /** Поточна фаза. */
  get currentPhase(): IdlePhase {
    return this.phase;
  }

  /**
   * Старт: відкриття (перезавантаження) вкладки — теж дія людини.
   *
   * ⚠ Саме так, а не «продовжити зі сховища»: момент у сховищі міг лишитися від учорашнього сеансу, і вкладка
   * вийшла б одразу після входу. Сервер уже підтвердив сеанс (`/me`), отже людина щойно була тут.
   */
  start(): void {
    this.unsubscribe = listenIdleMessages((message) => this.onMessage(message));
    this.lastTouch = readSharedLastTouch() ?? 0;
    this.recordActivity(true);
  }

  /** Зупинка: таймери й підписки знято. */
  stop(): void {
    this.unsubscribe?.();
    this.unsubscribe = null;
    if (this.timer !== null) clearTimeout(this.timer);
    if (this.touchTimer !== null) clearTimeout(this.touchTimer);
    this.timer = null;
    this.touchTimer = null;
  }

  /** Нова межа (адміністратор змінив налаштування) — діє одразу, від останньої дії. */
  setTimeoutMs(timeoutMs: number): void {
    if (timeoutMs === this.timeoutMs) return;
    this.timeoutMs = timeoutMs;
    this.reschedule();
  }

  /**
   * Дія людини в цій вкладці.
   *
   * ⚠ Поки показано попередження, звичайні дії його НЕ знімають: людина, що з клавіатури дійшла табом до кнопки,
   * інакше закрила б діалог першим же натисканням Tab. Зняти його — «Stay signed in» ({@link stayActive}) або дія
   * в іншій вкладці.
   */
  recordActivity(force = false): void {
    if (this.phase === 'expired') return;
    if (!force && this.phase === 'warning') return;

    const now = this.deps.now();
    if (!force && now - this.last < ActivityDebounceMs) return;

    this.last = now;
    announceActivity(now);
    this.setPhase('active');
    this.reschedule();
    this.scheduleTouch(force);
  }

  /** «Stay signed in»: дія людини, і сервер дізнається про неї одразу. */
  stayActive(): void {
    this.recordActivity(true);
  }

  private onMessage(message: IdleMessage): void {
    if (message.type === 'expired') {
      this.expire(true);
      return;
    }

    if (this.phase === 'expired' || message.at <= this.last) return;
    this.last = message.at;
    this.setPhase('active');
    this.reschedule();
  }

  private scheduleTouch(force: boolean): void {
    const now = this.deps.now();
    const lastTouch = Math.max(this.lastTouch, readSharedLastTouch() ?? 0);

    if (force || now - lastTouch >= TouchIntervalMs) {
      this.sendTouch(now);
      return;
    }

    if (this.touchTimer !== null) return;
    this.touchTimer = setTimeout(() => {
      this.touchTimer = null;
      const shared = Math.max(this.lastTouch, readSharedLastTouch() ?? 0);
      if (this.phase !== 'expired' && this.last > shared) this.sendTouch(this.deps.now());
    }, lastTouch + TouchIntervalMs - now);
  }

  private sendTouch(at: number): void {
    if (this.touchTimer !== null) {
      clearTimeout(this.touchTimer);
      this.touchTimer = null;
    }
    this.lastTouch = at;
    writeSharedLastTouch(at);
    this.deps.touch().catch(() => {
      // Сигнал не дійшов (мережа): наступна дія спробує знову; відмову сеансу обробляє транспорт (`401`).
    });
  }

  private reschedule(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
    if (this.phase === 'expired') return;

    // ⚠ Сховище — на випадок, коли повідомлення сусідньої вкладки не дійшло (заснула, канал закрито).
    const shared = readSharedLastActivity();
    if (shared !== null && shared > this.last) this.last = shared;

    const now = this.deps.now();
    const expireAt = this.last + this.timeoutMs;
    const warnAt = expireAt - warningLeadMs(this.timeoutMs);

    if (now >= expireAt) {
      this.expire(false);
      return;
    }

    if (now >= warnAt) {
      this.setPhase('warning');
      this.timer = setTimeout(() => this.reschedule(), expireAt - now);
      return;
    }

    this.setPhase('active');
    this.timer = setTimeout(() => this.reschedule(), warnAt - now);
  }

  private expire(remote: boolean): void {
    if (this.phase === 'expired') return;
    this.phase = 'expired';
    this.stop();
    if (!remote) announceIdleExpired(Math.round(this.timeoutMs / 60_000));
    this.deps.onPhase('expired', this.deadline);
    this.deps.onExpire(remote);
  }

  private setPhase(phase: IdlePhase): void {
    if (this.phase === phase) return;
    this.phase = phase;
    this.deps.onPhase(phase, this.deadline);
  }
}
