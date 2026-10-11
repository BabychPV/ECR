/**
 * Ширина, від якої меню — колонка поруч зі сторінкою, а не шухляда за бургером.
 *
 * ⚠ Макет `docs/design/hybrid/index.html`: `@media (max-width:900px)` — `.side` стає шаром поверх.
 * Одна константа для `AppShell.navbar.breakpoint`, запиту хука, бургера, кнопки згортання й крихт:
 * `hiddenFrom`/`visibleFrom` Mantine знають лише іменовані межі (`sm` = 768 px), тож на 768–899 px
 * бургер зникав, а меню лишалось згорнутим — тому видимість рахується тим самим запитом.
 */
export const NavbarDesktopBreakpoint = 900;
export const NavbarDesktopQuery = `(min-width: ${NavbarDesktopBreakpoint}px)`;
