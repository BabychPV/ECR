import { createTheme, mergeThemeOverrides } from '@mantine/core';
import { testTheme } from '@/test/render';

/**
 * B145: тема, з якою СПРАВЖНІЙ `Modal` (і все, що на ньому: `ConfirmModal`, `ReasonModal`, `Wizard`, власні
 * діалоги екранів) після `opened = false` лишається в DOM назавжди (`keepMounted`), а не ~200 мс.
 *
 * ⚠ Навіщо поруч із заглушками `@/test/alwaysMountedDialogs`: заглушка підміняє сам діалог, тож захист
 * УСЕРЕДИНІ нього (`shared/ui/ConfirmModal`, `ReasonModal`) чи діалог, зібраний на `Modal` напряму, нею не
 * перевірити. Тут компонент справжній, а з доказу прибрано лише час: кнопка діалогу, що закрився, на місці,
 * і клік по ній (`fireEvent.click`) доходить до обробника так само, як у вікні анімації закриття.
 *
 * ⚠ Закритий діалог схований (`display: none`): його кнопки шукати за `data-testid` або за роллю з
 * `{ hidden: true }`, а перед кліком звіряти `isConnected` — клік по від'єднаному вузлу до React не дійде, і
 * тест був би зеленим ні про що.
 *
 * ⛔ Лише для тестів: у продукті `keepMounted` у діалогів немає.
 */
export const keepMountedModalsTheme = mergeThemeOverrides(
  testTheme,
  // ⚠ B149: `ModalRoot` окремо — `shared/ui/ResizableModal` збирає діалог із `Modal.Root`, а той бере
  // типові пропси під власним іменем, не `Modal`. Без цього закритий `ResizableModal` зникав із DOM за кадр
  // переходу, і зонд бачив або не бачив кнопку залежно від таймінгу.
  createTheme({
    components: {
      Modal: { defaultProps: { keepMounted: true } },
      ModalRoot: { defaultProps: { keepMounted: true } },
    },
  }),
);
