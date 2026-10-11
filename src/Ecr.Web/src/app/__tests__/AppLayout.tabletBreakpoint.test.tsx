import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * RC19: меню на ширині 768 px.
 *
 * ⛔ Дефект: межа «меню — колонка» була 48em (768 px, `breakpoint: 'sm'`). На планшеті колонка в
 * 260 px з'їдала третину ширини, і таблиця документів стискалась до переносів у кожній клітинці.
 * Макет `docs/design/hybrid/index.html` (`@media (max-width:900px)`) на цій ширині ховає меню за
 * бургер. Тепер межа — 900 px, і вона ОДНА для хука (`useMediaQuery`) і для `AppShell`.
 *
 * ⚠ Перевірка читає сам вихідний файл: рендер у jsdom не рахує медіа-запитів, а розбіжність двох
 * значень (хук каже «десктоп», `AppShell` — «мобільне») ламала б відступ контенту.
 */
const source = readFileSync(path.resolve(__dirname, '../AppLayout.tsx'), 'utf8');

describe('AppLayout: межа меню-колонки', () => {
  it('одна константа — 900 px — і для запиту хука, і для breakpoint AppShell', () => {
    expect(source).toContain('const NavbarDesktopBreakpoint = 900;');
    expect(source).toContain('`(min-width: ${NavbarDesktopBreakpoint}px)`');
    expect(source).toContain('breakpoint: NavbarDesktopBreakpoint,');
  });

  it("старі межі 48em / 'sm' для меню не лишилися", () => {
    expect(source).not.toContain("breakpoint: 'sm'");
    expect(source).not.toContain("'(min-width: 48em)'");
  });
});
