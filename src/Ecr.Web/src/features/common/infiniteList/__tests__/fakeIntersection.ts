import { act } from '@testing-library/react';
import { vi } from 'vitest';

/**
 * Керована підміна `IntersectionObserver` для тестів нескінченної прокрутки (W-3).
 *
 * `observe()` через `queueMicrotask` повідомляє ПОТОЧНИЙ стан `visible` — як браузер, що одразу віддає новому
 * спостерігачу поточний перетин; `setVisible(v)` повідомляє всіх живих; `disconnect()` прибирає зі списку.
 */
export interface IntersectionProbe {
  readonly setVisible: (visible: boolean) => void;
  /** Кількість живих (не від'єднаних) спостерігачів. */
  readonly live: () => number;
}

interface Entry {
  target: Element | null;
  readonly callback: IntersectionObserverCallback;
  readonly self: IntersectionObserver;
}

export function installIntersection(initiallyVisible: boolean): IntersectionProbe {
  let visible = initiallyVisible;
  const live = new Set<Entry>();

  class FakeObserver {
    private readonly entry: Entry;

    constructor(callback: IntersectionObserverCallback) {
      this.entry = { target: null, callback, self: this as unknown as IntersectionObserver };
      live.add(this.entry);
    }

    observe(target: Element): void {
      const entry = this.entry;
      entry.target = target;
      queueMicrotask(() => {
        if (!live.has(entry) || entry.target === null) return;
        entry.callback([{ target, isIntersecting: visible } as unknown as IntersectionObserverEntry], entry.self);
      });
    }

    unobserve(): void {
      this.entry.target = null;
    }

    disconnect(): void {
      live.delete(this.entry);
    }

    takeRecords(): IntersectionObserverEntry[] {
      return [];
    }
  }

  vi.stubGlobal('IntersectionObserver', FakeObserver);

  return {
    setVisible: (next) => {
      visible = next;
      act(() => {
        for (const entry of [...live]) {
          if (entry.target === null) continue;
          entry.callback(
            [{ target: entry.target, isIntersecting: next } as unknown as IntersectionObserverEntry],
            entry.self,
          );
        }
      });
    },
    live: () => live.size,
  };
}
