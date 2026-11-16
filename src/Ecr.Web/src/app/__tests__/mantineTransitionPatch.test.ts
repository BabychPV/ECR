import { describe, expect, it } from 'vitest';
import { MantineUseTransitionModule, patchMantineUseTransition } from '../../../scripts/patch-mantine-transition.mjs';
import {
  InstalledMantineUseTransition as installed,
  InstalledMantineUseTransitionPath as modulePath,
  MantinePatchMissingHint,
} from '@/test/mantineTransitionPatch';

/** Тіло `handleStateChange` до першого `requestAnimationFrame` — те, що виконується синхронно. */
function syncPrologue(code: string): string {
  const start = code.indexOf('const handleStateChange');
  return code.slice(start, code.indexOf('requestAnimationFrame', start));
}

describe('patchMantineUseTransition', () => {
  // ⛔ Червоний, якщо `postinstall` не відпрацював: тоді і `vite dev`, і всі тести бачать
  // незалатаний Mantine, а `modalReopenRemount.test.tsx` падав би без пояснення причини.
  // ✎ `B137`: падіння саме каже, що це середовище, і називає команду (`@/test/mantineTransitionPatch`).
  it('встановлений Mantine залатаний: новий перехід скасовує кадр попереднього', () => {
    expect(syncPrologue(installed), MantinePatchMissingHint).toContain('cancelAnimationFrame(rafRef.current)');
  });

  it('латка ставить скасування кадру саме в синхронний пролог `handleStateChange`', () => {
    const original = installed.replace(' cancelAnimationFrame(rafRef.current);', '');
    expect(syncPrologue(original)).not.toContain('cancelAnimationFrame');
    expect(syncPrologue(patchMantineUseTransition(original))).toContain('cancelAnimationFrame(rafRef.current)');
  });

  it('повторне застосування нічого не змінює', () => {
    const once = patchMantineUseTransition(installed);
    expect(patchMantineUseTransition(once)).toBe(once);
  });

  it('модуль з апстрімним фіксом (7.17.8, `clearAllTimeouts`) лишається як є', () => {
    const upstream = 'function clearAllTimeouts() { cancelAnimationFrame(rafRef.current); }';
    expect(patchMantineUseTransition(upstream)).toBe(upstream);
  });

  it('незнайомий текст модуля — помилка збірки, а не тихе повернення дефекту', () => {
    expect(() => patchMantineUseTransition('const handleStateChange = () => {};')).toThrow(
      /patch-mantine-transition/,
    );
  });

  it('шаблон модуля впізнає шлях встановленого файла на обох роздільниках', () => {
    expect(MantineUseTransitionModule.test(modulePath)).toBe(true);
    expect(MantineUseTransitionModule.test(modulePath.replaceAll('/', '\\'))).toBe(true);
    expect(MantineUseTransitionModule.test(modulePath.replaceAll('\\', '/').replace('/esm/', '/cjs/'))).toBe(false);
  });
});
