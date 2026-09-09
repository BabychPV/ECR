import type { JSX } from 'react';
import { NativeSelect, Text } from '@mantine/core';
import { language, loadCatalog, setLanguage, t } from '@/shared/i18n';
import { useCatalog } from '@/shared/i18n/useCatalog';
import { useLanguages } from '@/shared/i18n/useLanguages';

/**
 * Перемикач мови інтерфейсу (T9 директиви №11, `ФВ-14.9`).
 *
 * ⛔ Перелік мов — із реєстру (`useLanguages`, `GET /api/v1/languages`), а не
 * константа `['en', 'ru', 'kz']` у бандлі: саме це і забороняє вимога
 * «додавання мови — запис у реєстр, не збірка клієнта» (`shared/i18n/index.ts`).
 *
 * ⚠ Перекладів `ru`/`kz` у каталозі сьогодні немає — їх заводить термінолог
 * (`C-7`), не цей компонент. Каталог сам підміняє відсутній переклад
 * англійським на сервері (`UiStringResolver.Compose`), тому перемикання мови
 * без жодного перекладеного рядка не показує ні порожнечі, ні голого ключа —
 * лише англійський текст під новою мовою, і це очікувана поведінка, не дефект.
 */
export function LanguageSwitcher(): JSX.Element | null {
  // Перемальовує підпис обраної мови після `setLanguage`/`loadCatalog`
  // (обидва зрештою кличуть `bumpCatalog`, `D-138`).
  useCatalog();

  const languages = useLanguages();
  const data = (languages.data ?? []).map((item) => ({
    value: item.code,
    label: item.nameNative,
  }));

  // ⚠ Ховається, а не малює порожній список: реєстр мов недоступний — це
  // рідкісний збій мережі, а не привід показувати перемикач без жодного
  // пункту вибору.
  if (data.length === 0) return null;

  return (
    <div>
      <Text size="xs" c="dimmed" mb="xs" id="ecr-language-label">
        {t('profile.language')}
      </Text>

      <NativeSelect
        size="xs"
        aria-labelledby="ecr-language-label"
        data={data}
        value={language()}
        onChange={(event) => {
          const value = event.currentTarget.value;
          if (value === language()) return;

          setLanguage(value);

          // ⛔ Без цього виклику перемикач лише запам'ятовує вибір у
          // `localStorage`: `t()` і далі читає каталог, завантажений під
          // СТАРУ мову (`loaded` у `shared/i18n/index.ts` наповнюється лише
          // тим, що явно запитали), і видимий текст не зміниться до
          // наступного відкриття сторінки.
          //
          // ⚠ Область — `private`: перемикач стоїть у `UserMenu`, а це
          // частина застосунку, показана лише після входу; приватний зріз
          // містить усе, включно зі спільними ключами (`common.*`, `app.*`).
          void loadCatalog(value, 'private');
        }}
      />
    </div>
  );
}
