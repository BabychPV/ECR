import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  KeyStep,
  KeyStepLarge,
  MinHeight,
  MinWidth,
  clampBox,
  dragResize,
  keyResize,
  parseSize,
  readSize,
  storageKey,
  writeSize,
} from '@/shared/ui/modalSize';

/**
 * Чиста логіка розміру вікна формул і методологій (P44): межі, крок клавіш, перетягування,
 * сховище. Без рендера — саме тут ламається мовчки (вікно за екраном, забутий розмір).
 */

const Screen = { width: 1280, height: 800 };

afterEach(() => {
  vi.restoreAllMocks();
  try {
    globalThis.localStorage.clear();
  } catch {
    // Сховища може не бути — тоді й чистити нічого.
  }
});

describe('modalSize: межі', () => {
  it('не менше мінімуму і не більше 90 % екрана', () => {
    expect(clampBox({ width: 10, height: 10 }, Screen)).toEqual({ width: MinWidth, height: MinHeight });
    expect(clampBox({ width: 5000, height: 5000 }, Screen)).toEqual({ width: 1152, height: 720 });
  });

  it('на малому екрані мінімум поступається максимуму', () => {
    expect(clampBox({ width: 1000, height: 1000 }, { width: 400, height: 300 })).toEqual({ width: 360, height: 270 });
  });
});

describe('modalSize: клавіші ручки', () => {
  const box = { width: 600, height: 400 };

  it('стрілки змінюють ширину і висоту на крок, Shift — на великий крок', () => {
    expect(keyResize(box, 'ArrowRight', false, Screen)).toEqual({ width: 600 + KeyStep, height: 400 });
    expect(keyResize(box, 'ArrowLeft', false, Screen)).toEqual({ width: 600 - KeyStep, height: 400 });
    expect(keyResize(box, 'ArrowDown', true, Screen)).toEqual({ width: 600, height: 400 + KeyStepLarge });
    expect(keyResize(box, 'ArrowUp', true, Screen)).toEqual({ width: 600, height: 400 - KeyStepLarge });
  });

  it('у межах: менше мінімуму не стискається', () => {
    expect(keyResize({ width: MinWidth, height: MinHeight }, 'ArrowLeft', true, Screen)).toEqual({
      width: MinWidth,
      height: MinHeight,
    });
  });

  it('інша клавіша — не про розмір (Esc і Tab ідуть далі)', () => {
    expect(keyResize(box, 'Escape', false, Screen)).toBeNull();
    expect(keyResize(box, 'Tab', false, Screen)).toBeNull();
  });
});

describe('modalSize: перетягування кута', () => {
  it('ширина росте на подвійний зсув (вікно по центру), висота — на зсув, якщо не centered', () => {
    expect(dragResize({ width: 600, height: 400 }, 50, 30, false, Screen)).toEqual({ width: 700, height: 430 });
    expect(dragResize({ width: 600, height: 400 }, 50, 30, true, Screen)).toEqual({ width: 700, height: 460 });
  });
});

describe('modalSize: сховище', () => {
  it('запам’ятовує розмір і стирає його на звичайному', () => {
    writeSize('methodology.formula', { mode: 'custom', width: 700, height: 500 });
    expect(readSize('methodology.formula')).toEqual({ mode: 'custom', width: 700, height: 500 });

    writeSize('methodology.formula', { mode: 'full' });
    expect(readSize('methodology.formula')).toEqual({ mode: 'full' });

    writeSize('methodology.formula', { mode: 'normal' });
    expect(globalThis.localStorage.getItem(storageKey('methodology.formula'))).toBeNull();
    expect(readSize('methodology.formula')).toEqual({ mode: 'normal' });
  });

  it('пошкоджений чи чужий запис — звичайний розмір', () => {
    expect(parseSize('{oops')).toEqual({ mode: 'normal' });
    expect(parseSize('{"mode":"custom","width":-1,"height":5}')).toEqual({ mode: 'normal' });
    expect(parseSize('"full"')).toEqual({ mode: 'normal' });
  });

  it('сховище, що кидає, не ламає ні читання, ні запис', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('full', 'QuotaExceededError');
    });

    expect(() => writeSize('methodology.formula', { mode: 'full' })).not.toThrow();
    expect(readSize('methodology.formula')).toEqual({ mode: 'normal' });
  });
});
