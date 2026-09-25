import { useEffect, useMemo, useState, type JSX } from 'react';
import { NativeSelect, Text } from '@mantine/core';
import { apiFetch } from '@/api/client';
import type { LanguageDto, UiStringCatalog } from '@/api/types';
import { language, loadCatalog, setLanguage, t, type Scope } from '@/shared/i18n';
import { useCatalog } from '@/shared/i18n/useCatalog';
import { useLanguages } from '@/shared/i18n/useLanguages';

/**
 * Мови, у яких є хоч один власний переклад (`R-16`).
 *
 * ⛔ Реєстр пропонував `ru`/`kz`, а в каталозі для них — нуль рядків: сервер
 * підміняє відсутній переклад англійським (`UiStringResolver.Compose`), тож
 * людина обирала «Русский» і отримувала той самий англійський інтерфейс, лише
 * з іншою позначкою в перемикачі. Вибір, що нічого не змінює, — не вибір.
 *
 * ⚠ Покриття — з КАТАЛОГУ, а не з `GET /ui-strings/coverage`: той вимагає
 * `System.ManageLocalization`, а перемикач бачить кожен. Мова вважається
 * перекладеною, якщо в її зрізі є хоч один рядок, що відрізняється від мови
 * за замовчуванням, — тобто рівно те, що людина побачить на екрані.
 *
 * ⚠ Деградація — у бік ПОКАЗУ: зріз не завантажився — мова лишається (як і
 * було до цієї правки), бо сховати мову через збій мережі гірше, ніж
 * запропонувати неперекладену. Поточна мова лишається завжди: інакше
 * перемикач показував би не те, що обрано.
 *
 * ⚠ Кешується надовго (як і сам реєстр): переклади з'являються раз на
 * тижні, а не між двома відкриттями меню.
 */
export function useTranslatedLanguages(
  languages: readonly LanguageDto[] | undefined,
  scope: Scope,
): LanguageDto[] {
  const all = useMemo(() => languages ?? [], [languages]);
  const fallback = all.find((item) => item.isDefault);

  // Зрізи за кодом мови: `undefined` — ще їде, `null` — відмова.
  const [catalogs, setCatalogs] = useState<Record<string, Strings | null>>({});

  useEffect(() => {
    if (fallback === undefined) return;

    let live = true;

    for (const item of all) {
      void catalogOf(item.code, scope).then((strings) => {
        if (live) setCatalogs((previous) => ({ ...previous, [item.code]: strings }));
      });
    }

    return () => {
      live = false;
    };
  }, [all, fallback, scope]);

  if (fallback === undefined) return [...all];

  const reference = catalogs[fallback.code];
  const current = language();

  return all.filter((item) => {
    if (item.isDefault || item.code === current) return true;

    const own = catalogs[item.code];

    // Відмова будь-якого з двох зрізів — не знаємо, і не ховаємо.
    if (own === null || reference === null) return true;

    // Ще їде — поки не показуємо: інакше пункт з'явився б і за мить зник.
    if (own === undefined || reference === undefined) return false;

    return Object.entries(own).some(([key, value]) => reference[key] !== value);
  });
}

type Strings = UiStringCatalog['strings'];

/**
 * Зріз каталогу мови — один запит на сеанс вкладки.
 *
 * ⚠ Власна пам'ять, а не React Query: хук потрібен і сторінці входу, яка
 * рендериться поза `QueryClientProvider` (див. `usePublicBootstrap`).
 * Відмова не кешується — наступне відкриття меню спробує знову.
 */
const catalogCache = new Map<string, Promise<Strings | null>>();

function catalogOf(code: string, scope: Scope): Promise<Strings | null> {
  const key = `${code}:${scope}`;
  const hit = catalogCache.get(key);
  if (hit !== undefined) return hit;

  const made = apiFetch<UiStringCatalog>(
    `/api/v1/ui-strings/${encodeURIComponent(code)}?scope=${scope}`,
  ).then(
    (catalog) => (catalog !== null && typeof catalog === 'object' && typeof catalog.strings === 'object' ? catalog.strings : null),
    () => null,
  );

  void made.then((strings) => {
    if (strings === null) catalogCache.delete(key);
  });

  catalogCache.set(key, made);

  return made;
}

/** Скидає пам'ять зрізів — для тестів. */
export function resetLanguageCoverage(): void {
  catalogCache.clear();
}

/**
 * Перемикач мови інтерфейсу (T9 директиви №11, `ФВ-14.9`).
 *
 * ⛔ Перелік мов — із реєстру (`useLanguages`, `GET /api/v1/languages`), а не
 * константа `['en', 'ru', 'kz']` у бандлі: саме це і забороняє вимога
 * «додавання мови — запис у реєстр, не збірка клієнта» (`shared/i18n/index.ts`).
 *
 * ✎ `R-16`: і лише ті з реєстру, для яких є переклад (`useTranslatedLanguages`
 * вище). Без жодної мови, крім однієї, перемикач ховається — вибір з одного
 * пункту не є вибором (той самий прийом, що й на сторінці входу).
 */
export function LanguageSwitcher(): JSX.Element | null {
  // Перемальовує підпис обраної мови після `setLanguage`/`loadCatalog`
  // (обидва зрештою кличуть `bumpCatalog`, `D-138`).
  useCatalog();

  const languages = useLanguages();
  // ⚠ Область — `private`: перемикач живе в меню користувача, тобто після
  // входу, і людина бачить переклад саме приватного зрізу.
  const offered = useTranslatedLanguages(languages.data, 'private');
  const data = offered.map((item) => ({
    value: item.code,
    label: item.nameNative,
  }));

  // ⚠ Ховається, а не малює порожній список: реєстр мов недоступний — це
  // рідкісний збій мережі, а не привід показувати перемикач без жодного
  // пункту вибору. Один пункт — теж не вибір (`R-16`).
  if (data.length <= 1) return null;

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
