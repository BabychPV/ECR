/**
 * ✎ Борги 27.11 (п. 8, флейк `periodNavBounds.spec.ts:89` «scrollY 120 -> 0»): після перемикання періоду сторінка тримає
 * збережену прокрутку, доки розкладка добирається, — за ЧАСОМ, а не за кількістю кадрів.
 *
 * ⛔ Що було: повтор `scrollTo` 40 кадрів (~0,7 с) від моменту, коли картка й перелік таблиць періоду вже є. Зрізи таблиць
 * (сітка) вантажаться ПІСЛЯ цього — і коли вони йшли довше за 40 кадрів (стенд під навантаженням), скелет сітки, нижчий за
 * вікно, притискав прокрутку до 0 уже після кінця повтору, і людина опинялась нагорі сторінки. Тепер повтор живе
 * {@link HoldScrollBudgetMs}: сторінка знову виросла — позиція повертається. Рух людини (колесо, дотик, клавіша, клік)
 * зупиняє утримання одразу — її рух важливіший.
 */
export const HoldScrollBudgetMs = 5_000;

const UserScroll = ['wheel', 'touchstart', 'keydown', 'mousedown'] as const;

export interface HoldScrollEnvironment {
  readonly scrollY: () => number;
  readonly scrollTo: (y: number) => void;
  readonly requestFrame: (callback: () => void) => number;
  readonly cancelFrame: (handle: number) => void;
  readonly now: () => number;
  readonly addEventListener: (type: string, listener: () => void) => void;
  readonly removeEventListener: (type: string, listener: () => void) => void;
}

const browser: HoldScrollEnvironment = {
  scrollY: () => window.scrollY,
  scrollTo: (y) => window.scrollTo({ top: y }),
  requestFrame: (callback) => window.requestAnimationFrame(callback),
  cancelFrame: (handle) => window.cancelAnimationFrame(handle),
  now: () => performance.now(),
  addEventListener: (type, listener) => window.addEventListener(type, listener, { passive: true, once: true }),
  removeEventListener: (type, listener) => window.removeEventListener(type, listener),
};

/** Утримує прокрутку на `y`; повертає «зупинити» (розмонтування, новий перемикач). */
export function holdScrollPosition(
  y: number,
  budgetMs: number = HoldScrollBudgetMs,
  env: HoldScrollEnvironment = browser,
): () => void {
  const started = env.now();
  let frame = 0;
  let stopped = false;
  const stop = (): void => {
    if (stopped) return;
    stopped = true;
    env.cancelFrame(frame);
    for (const type of UserScroll) env.removeEventListener(type, stop);
  };
  for (const type of UserScroll) env.addEventListener(type, stop);

  const retry = (): void => {
    if (stopped) return;
    if (Math.abs(env.scrollY() - y) > 1) env.scrollTo(y);
    if (env.now() - started < budgetMs) frame = env.requestFrame(retry);
    else stop();
  };
  env.scrollTo(y);
  frame = env.requestFrame(retry);

  return stop;
}
