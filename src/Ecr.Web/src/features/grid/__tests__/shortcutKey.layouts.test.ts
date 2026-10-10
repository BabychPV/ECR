import { describe, expect, it } from 'vitest';
import { gridShortcut } from '../shortcutKey';

/**
 * N3-11: літера ярлика - це латинська `key`, а `code` (фізична клавіша) - запасний варіант.
 * QWERTZ: Z і Y поміняні місцями; AZERTY: «z» лежить на фізичній `KeyW`.
 */
const ev = (code: string, key: string, shiftKey = false) => ({ code, key, shiftKey, altKey: false });

describe('gridShortcut: розкладки', () => {
  it('QWERTZ: Ctrl+Z (key z на KeyY) - undo, Ctrl+Y (key y на KeyZ) - redo', () => {
    expect(gridShortcut(ev('KeyY', 'z'))).toBe('undo');
    expect(gridShortcut(ev('KeyZ', 'y'))).toBe('redo');
  });

  it('AZERTY: Ctrl+Z (key z на KeyW) - undo; Ctrl+W (key w на KeyZ) - не ярлик', () => {
    expect(gridShortcut(ev('KeyW', 'z'))).toBe('undo');
    expect(gridShortcut(ev('KeyZ', 'w'))).toBeNull();
  });

  it('QWERTY і CapsLock/Shift: регістр не важить', () => {
    expect(gridShortcut(ev('KeyZ', 'z'))).toBe('undo');
    expect(gridShortcut(ev('KeyZ', 'Z', true))).toBe('redo');
    expect(gridShortcut(ev('KeyS', 'S'))).toBe('save');
  });

  it('кирилиця: key нелатинська - працює code', () => {
    expect(gridShortcut(ev('KeyZ', 'я'))).toBe('undo');
    expect(gridShortcut(ev('KeyY', 'н'))).toBe('redo');
    expect(gridShortcut(ev('KeyS', 'і'))).toBe('save');
  });

  it('порожній code - відкат до key; AltGr/Alt - не ярлик', () => {
    expect(gridShortcut(ev('', 'Z'))).toBe('undo');
    expect(gridShortcut({ code: 'KeyZ', key: 'ż', shiftKey: false, altKey: true })).toBeNull();
  });
});
