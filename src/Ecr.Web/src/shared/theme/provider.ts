import { cssVariablesResolver } from './cssVariables';
import { theme } from './theme';

/**
 * Налаштування `MantineProvider`, спільні для застосунку й для приладь тестів.
 *
 * ⛔ **Чому це окремий експорт, а не два однакові виклики.** До `UI-01` їх і
 * було два: `App.tsx` і `src/test/__tests__/a11yFixtures.tsx` кожен будував
 * власний `<MantineProvider theme={theme}>`. Коли `UI-01` додав
 * `cssVariablesResolver`, він дійшов лише до `App.tsx` — і гейт `a11y`,
 * два з семи, почав проганяти застосунок на дефолтних змінних Mantine, тобто
 * перевіряв те, чого на екрані немає.
 *
 * ⚠ Дублікат не «розійшовся через неуважність»: він розійдеться знову при
 * наступному пропі провайдера, хоч би скільки разів про нього нагадували.
 * Тому розбіжність прибрано **за побудовою** — обидва місця розгортають один
 * об'єкт, і новий проп фізично потрапляє в обидва.
 *
 * ⚠ Схема кольорів сюди НЕ входить: застосунок бере системну
 * (`defaultColorScheme="auto"`), а набір a11y примусово ганяє кожну
 * (`forceColorScheme`) — це різні наміри, і зводити їх в один було б гірше за
 * дублікат.
 *
 * ⚠ Тип НЕ звужено до `Pick<MantineProviderProps, …>`: у проєкті ввімкнено
 * `exactOptionalPropertyTypes`, і `Pick` дав би `theme?: … | undefined`, тобто
 * передати цей `theme` назад у `MantineProvider` стало б помилкою типу. Хай
 * тип виводиться — він і так точніший за `Pick`.
 */
export const mantineProviderProps = {
  theme,
  cssVariablesResolver,
} as const;
