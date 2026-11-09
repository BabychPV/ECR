import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ACTIVITY_PATH,
  BACKGROUND_HEADER,
  apiFetch,
  loginUrl,
  resetSignOutForTests,
  setLoginRedirect,
  setUserActivityListener,
  signOutForIdle,
} from '@/api/client';
import { IdleLastActivityKey, resetIdleChannelForTests } from '../idleChannel';
import { IdleClock, TouchIntervalMs, warningLeadMs, type IdlePhase } from '../idleClock';

/**
 * D-471: годинник бездіяльності вкладки — попередження за хвилину, вихід на межі, спільний для вкладок,
 * опитування (GET) не рахуються.
 *
 * ⛔ Червоні без фіксу (перевірено тимчасовим відкатом у робочому дереві): прибери `!force && phase === 'warning'`
 * у `recordActivity` — падає «дія під попередженням не знімає його»; прибери хвіст у `scheduleTouch` — падає
 * «хвіст дії доїжджає»; прибери `isUserAction` в `apiFetchRaw` (виклик слухача на будь-який запит) — падає
 * «GET-опитування не рахується»; прибери гілку `idle` у `loginCauseOf` — падає «401 idle веде на вхід з причиною».
 */

/** Підробка `BroadcastChannel`: спільна шина між екземплярами, як між вкладками. */
class FakeChannel extends EventTarget {
  static readonly all = new Set<FakeChannel>();

  constructor(readonly name: string) {
    super();
    FakeChannel.all.add(this);
  }

  postMessage(data: unknown): void {
    for (const other of FakeChannel.all) {
      if (other !== this && other.name === this.name) other.dispatchEvent(new MessageEvent('message', { data }));
    }
  }

  close(): void {
    FakeChannel.all.delete(this);
  }
}

const Minute = 60_000;

function makeClock(minutes = 30) {
  const phases: IdlePhase[] = [];
  const touch = vi.fn(async () => Promise.resolve());
  const onExpire = vi.fn();
  const clock = new IdleClock(minutes * Minute, {
    now: () => Date.now(),
    touch,
    onPhase: (phase) => phases.push(phase),
    onExpire,
  });
  return { clock, phases, touch, onExpire };
}

/** «Інша вкладка» — окремий екземпляр каналу з тим самим іменем. */
function otherTab(): { channel: FakeChannel; received: unknown[] } {
  const channel = new FakeChannel('ecr.idle');
  const received: unknown[] = [];
  channel.addEventListener('message', (event) => received.push((event as MessageEvent).data));
  return { channel, received };
}

beforeEach(() => {
  vi.useFakeTimers({ now: new Date('2026-11-09T10:00:00Z') });
  vi.stubGlobal('BroadcastChannel', FakeChannel);
  window.localStorage.clear();
});

afterEach(() => {
  resetIdleChannelForTests();
  FakeChannel.all.clear();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  setUserActivityListener(null);
  setLoginRedirect(() => {});
  resetSignOutForTests();
});

describe('IdleClock: попередження й вихід', () => {
  it('попередження — за хвилину до межі, вихід — на межі, інші вкладки повідомлено', () => {
    const tab = otherTab();
    const { clock, phases, onExpire } = makeClock(30);
    clock.start();

    vi.advanceTimersByTime(29 * Minute - 1);
    expect(clock.currentPhase).toBe('active');

    vi.advanceTimersByTime(1);
    expect(clock.currentPhase).toBe('warning');
    expect(phases).toContain('warning');
    expect(onExpire).not.toHaveBeenCalled();

    vi.advanceTimersByTime(Minute);
    expect(onExpire).toHaveBeenCalledWith(false);
    expect(clock.currentPhase).toBe('expired');
    expect(tab.received).toContainEqual({ type: 'expired', minutes: 30 });
  });

  it('клацання скидає відлік: межа рахується від останньої дії', () => {
    const { clock, onExpire } = makeClock(30);
    clock.start();

    vi.advanceTimersByTime(20 * Minute);
    clock.recordActivity();

    vi.advanceTimersByTime(25 * Minute);
    expect(clock.currentPhase).toBe('active');
    expect(onExpire).not.toHaveBeenCalled();

    vi.advanceTimersByTime(5 * Minute);
    expect(onExpire).toHaveBeenCalledTimes(1);
  });

  it('дія під попередженням не знімає його; «Stay signed in» знімає й одразу повідомляє сервер', () => {
    const { clock, touch, onExpire } = makeClock(30);
    clock.start();
    touch.mockClear();

    vi.advanceTimersByTime(29 * Minute + 10_000);
    expect(clock.currentPhase).toBe('warning');

    // Tab до кнопки — не відповідь на попередження.
    clock.recordActivity();
    expect(clock.currentPhase).toBe('warning');
    expect(touch).not.toHaveBeenCalled();

    clock.stayActive();
    expect(clock.currentPhase).toBe('active');
    expect(touch).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(29 * Minute);
    expect(onExpire).not.toHaveBeenCalled();
  });

  it('на межі коротшій за 2 хв попередження — посередині (лише стенд)', () => {
    expect(warningLeadMs(30 * Minute)).toBe(Minute);
    expect(warningLeadMs(Minute)).toBe(30_000);
  });
});

describe('IdleClock: спільний годинник вкладок', () => {
  it('дія в іншій вкладці тримає цю живою і знімає попередження', () => {
    const tab = otherTab();
    const { clock, onExpire } = makeClock(30);
    clock.start();

    vi.advanceTimersByTime(29 * Minute + 30_000);
    expect(clock.currentPhase).toBe('warning');

    tab.channel.postMessage({ type: 'activity', at: Date.now() });
    expect(clock.currentPhase).toBe('active');

    vi.advanceTimersByTime(29 * Minute);
    expect(onExpire).not.toHaveBeenCalled();
  });

  it('запасний транспорт: момент дії зі сховища теж відсуває межу', () => {
    const { clock, onExpire } = makeClock(30);
    clock.start();

    vi.advanceTimersByTime(20 * Minute);
    window.localStorage.setItem(IdleLastActivityKey, String(Date.now()));

    vi.advanceTimersByTime(10 * Minute);
    expect(onExpire).not.toHaveBeenCalled();
    expect(clock.currentPhase).toBe('active');
  });

  it('дія цієї вкладки доходить до інших', () => {
    const tab = otherTab();
    const { clock } = makeClock(30);
    clock.start();
    tab.received.length = 0;

    vi.advanceTimersByTime(5_000);
    clock.recordActivity();
    expect(tab.received).toContainEqual({ type: 'activity', at: Date.now() });
  });

  it('вихід в іншій вкладці — вихід і тут, без другого запиту на сервер', () => {
    const tab = otherTab();
    const { clock, onExpire } = makeClock(30);
    clock.start();

    tab.channel.postMessage({ type: 'expired', minutes: 30 });
    expect(onExpire).toHaveBeenCalledWith(true);
    expect(clock.currentPhase).toBe('expired');
  });
});

describe('IdleClock: сигнал активності на сервер', () => {
  it('не частіше за інтервал, але з хвостом — дія після сигналу таки доїжджає', () => {
    const { clock, touch } = makeClock(30);
    clock.start();
    expect(touch).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(5_000);
    clock.recordActivity();
    expect(touch).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(TouchIntervalMs - 5_000);
    expect(touch).toHaveBeenCalledTimes(2);

    // Без нових дій — тиша.
    vi.advanceTimersByTime(5 * TouchIntervalMs);
    expect(touch).toHaveBeenCalledTimes(2);
  });
});

describe('транспорт: що вважається дією людини', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Promise.resolve(new Response(null, { status: 204 }))),
    );
  });

  it('GET-опитування не рахується; мутація рахується; фонова мутація й сам сигнал — ні', async () => {
    const listener = vi.fn();
    setUserActivityListener(listener);

    await apiFetch('/api/v1/jobs/summary');
    await apiFetch('/api/v1/me/tasks');
    expect(listener).not.toHaveBeenCalled();

    await apiFetch('/api/v1/documents/1/cells', { method: 'PATCH', body: '{}' });
    expect(listener).toHaveBeenCalledTimes(1);

    await apiFetch('/api/v1/documents/1/tables/T/read', {
      method: 'PUT',
      body: '{}',
      headers: { [BACKGROUND_HEADER]: '1' },
    });
    await apiFetch(ACTIVITY_PATH, { method: 'POST' });
    expect(listener).toHaveBeenCalledTimes(1);
  });

  it('401 idle від сервера веде на вхід із причиною й межею і виводить інші вкладки', async () => {
    const tab = otherTab();
    const redirect = vi.fn();
    setLoginRedirect(redirect);
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        Promise.resolve(
          new Response(
            JSON.stringify({
              title: 'Signed out',
              status: 401,
              errorCode: 'ECR-AUTH-0401',
              correlationId: 'c',
              messageKey: 'err.ECR-AUTH-0401.idleTimeout',
              idleMinutes: 30,
            }),
            { status: 401, headers: { 'Content-Type': 'application/problem+json' } },
          ),
        ),
      ),
    );

    await apiFetch('/api/v1/jobs/summary').catch(() => undefined);

    expect(redirect).toHaveBeenCalledWith(expect.any(String), 'idle', 30);
    expect(tab.received).toContainEqual({ type: 'expired', minutes: 30 });
  });

  it('вихід таймером закриває мережу й веде на вхід із причиною idle', async () => {
    const redirect = vi.fn();
    setLoginRedirect(redirect);

    signOutForIdle(30);
    expect(redirect).toHaveBeenCalledWith(expect.any(String), 'idle', 30);

    const error = await apiFetch('/api/v1/jobs/summary').catch((e: unknown) => e);
    expect(error).toMatchObject({ problem: { status: 401 } });
  });

  it('адреса входу несе причину й межу', () => {
    expect(loginUrl('/documents/1', 'idle', 30)).toBe('/login?from=%2Fdocuments%2F1&reason=idle&minutes=30');
    expect(loginUrl('/x', 'session-invalidated')).toBe('/login?from=%2Fx&reason=session-invalidated');
  });
});
