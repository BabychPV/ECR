import '@testing-library/react';

/*
 * ⚠ jsdom не реалізує ані `matchMedia`, ані `ResizeObserver`, а Mantine і
 * RevoGrid звертаються до них при монтуванні. Без цих заглушок компонентний
 * тест падає не на своїй причині, а на відсутньому браузерному API — і
 * причину шукають у власному коді.
 */
if (typeof window !== 'undefined') {
  if (window.matchMedia === undefined) {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: (query: string) => ({
        matches: false,
        media: query,
        onchange: null,
        addEventListener: () => {},
        removeEventListener: () => {},
        addListener: () => {},
        removeListener: () => {},
        dispatchEvent: () => false,
      }),
    });
  }

  if (globalThis.ResizeObserver === undefined) {
    globalThis.ResizeObserver = class {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    } as unknown as typeof ResizeObserver;
  }
}
