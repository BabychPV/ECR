import { describe, expect, it } from 'vitest';
import { holdScrollPosition, type HoldScrollEnvironment } from '@/features/documents/holdScrollPosition';

/**
 * ✎ Борги 27.11 (п. 8, флейк `periodNavBounds.spec.ts:89` «scrollY 120 -> 0»): прокрутка після перемикання періоду
 * тримається за ЧАСОМ — скелет сітки, що притиснув прокрутку до 0 ПІСЛЯ ~40 кадрів (зрізи таблиць ідуть довше), не
 * лишає людину нагорі: щойно сторінка знову виросла, позиція повертається. Рух людини утримання зупиняє.
 *
 * Мутація (локально): повернути ліміт «40 кадрів» замість бюджету часу — червоний «повільна сітка».
 */
function stand(): HoldScrollEnvironment & {
  y: number;
  maxY: number;
  time: number;
  tick: (ms: number) => void;
  fire: (type: string) => void;
} {
  let queue: (() => void)[] = [];
  const listeners = new Map<string, () => void>();
  const env = {
    y: 0,
    maxY: 1_000,
    time: 0,
    scrollY: () => env.y,
    scrollTo: (target: number) => {
      env.y = Math.max(0, Math.min(target, env.maxY));
    },
    requestFrame: (callback: () => void) => {
      queue.push(callback);
      return queue.length;
    },
    cancelFrame: () => {
      queue = [];
    },
    now: () => env.time,
    addEventListener: (type: string, listener: () => void) => {
      listeners.set(type, listener);
    },
    removeEventListener: (type: string) => {
      listeners.delete(type);
    },
    /** Кадри по 16 мс протягом `ms`. */
    tick: (ms: number) => {
      for (let passed = 0; passed < ms; passed += 16) {
        env.time += 16;
        const run = queue;
        queue = [];
        for (const callback of run) callback();
      }
    },
    fire: (type: string) => listeners.get(type)?.(),
  };

  return env;
}

describe('holdScrollPosition', () => {
  it('повільна сітка: скелет притиснув прокрутку до 0 після 40 кадрів — позиція повертається, щойно сторінка виросла', () => {
    const env = stand();
    env.y = 120;
    holdScrollPosition(120, 5_000, env);
    env.tick(1_000); // > 40 кадрів

    // Скелет сітки: сторінка не вища за вікно — браузер притискає прокрутку.
    env.maxY = 0;
    env.scrollTo(env.y);
    env.tick(500);
    expect(env.y).toBe(0);

    // Зрізи прийшли — сторінка знову висока: утримання ще живе й повертає 120.
    env.maxY = 1_000;
    env.tick(100);
    expect(env.y).toBe(120);
  });

  it('рух людини зупиняє утримання; бюджет часу — межа', () => {
    const env = stand();
    holdScrollPosition(120, 5_000, env);
    env.tick(100);
    expect(env.y).toBe(120);

    env.fire('wheel');
    env.y = 400;
    env.tick(500);
    expect(env.y).toBe(400);

    const late = stand();
    holdScrollPosition(120, 1_000, late);
    late.tick(1_200);
    late.y = 0;
    late.tick(500);
    expect(late.y).toBe(0);
  });
});
