import type { JSX } from 'react';
import { Skeleton, Stack, Text, TextInput } from '@mantine/core';
import { useLanguages } from '@/shared/i18n/useLanguages';
import { t } from '@/shared/i18n';
import { ErrorAlert } from './ErrorAlert';

/** Назва мовами каталогу: код мови → текст. */
export type LocalizedValue = Record<string, string>;

/**
 * Поле локалізованої назви — по рядку на кожну мову реєстру.
 *
 * ⛔ Мови беруться з `GET /api/v1/languages`, а не з константи. Колонка
 * `…L10n` — це об'єкт саме тому, що «додати мову = запис у реєстр, а не
 * колонка в двадцяти таблицях» (`ФВ-2.2`); зашитий список у формі повернув би
 * ту саму жорсткість на крок раніше.
 *
 * ⚠ Порожні мови **не потрапляють у запит**: `{"en":"Water","ru":""}` — це не
 * «російської немає», а «російською називається порожньо», і `localized()`
 * поверне за такою назвою англійську лише завдяки тому, що перевіряє довжину.
 * Не надсилати порожнє чесніше, ніж покладатися на це.
 */
export function LocalizedInput({
  label,
  description,
  value,
  onChange,
  required = false,
}: {
  label: string;
  description?: string | undefined;
  value: LocalizedValue;
  onChange: (next: LocalizedValue) => void;
  /**
   * Позначити назву обов'язковою (`U-13`).
   *
   * ⚠ Зірочку отримує лише поле мови за замовчуванням, а не кожне: форма
   * вимагає назву ХОЧА Б ОДНІЄЮ мовою (`hasAnyText`), і три зірочки поспіль
   * читалися б як «заповни всі три». Поле мови за замовчуванням — те саме,
   * що вже несе `description`, тобто головне поле групи.
   */
  required?: boolean;
}): JSX.Element {
  const languages = useLanguages();

  /*
   * ⛔ Тут стояло `(languages.data ?? []).map(…)` — і це рівно той взірець,
   * проти якого існує `AsyncBoundary`: «невдалий запит перетворюється на
   * „даних немає“». Наслідок тут гірший, ніж порожній перелік. При відмові
   * `GET /api/v1/languages` компонент малював НУЛЬ полів — а форма поруч
   * лишалася з заблокованою кнопкою і підказкою «ще потрібно: Назва»
   * (`CreateProjectModal.tsx`). Тобто застосунок вимагав заповнити поле,
   * якого на екрані не існує, і людина робила висновок «форма зламана», а не
   * «сервер не відповів».
   *
   * ⚠ Показ помилки — `ErrorAlert`, а не `AsyncBoundary`. Різниця не
   * стилістична: `AsyncBoundary` малює порожній і помилковий стан через
   * `<Title order={4}>`, а цей компонент живе ВСЕРЕДИНІ діалогу, у якого вже
   * є власний заголовок — і вкладений `h4` після нього дав би `heading-order`
   * в `axe`. Сусідній `CreateDocumentModal` із тієї самої причини показує
   * відмову структури теж через `ErrorAlert`.
   */
  if (languages.error !== null) {
    return <ErrorAlert error={languages.error} onRetry={() => void languages.refetch()} />;
  }

  /*
   * ⚠ Скелет, а не порожнеча: доки реєстр мов їде, форма має показувати, що
   * саме тут з'явиться. Один рядок — бо мов зазвичай три, і смуга на три
   * поля брехала б про висоту так само, як нуль полів.
   */
  if (languages.isPending) {
    return <Skeleton height={60} radius="sm" data-localized-input="pending" />;
  }

  /*
   * ⛔ Реєстр мов ПОРОЖНІЙ — це не те саме, що відмова, і мовчати про нього
   * теж не можна: форма без жодного поля назви незаповнювана, і причина
   * (порожній реєстр `ФВ-2.2`) лежить в адмініструванні, а не в цій формі.
   *
   * ⚠ `?? []` тут ЛИШИВСЯ — і це не недогляд. Вада була не у фолбеку, а в
   * ПОРЯДКУ: доки він стояв першим, він ковтав і відмову, і очікування. Після
   * двох перевірок вище порожнеча означає рівно порожнечу — той самий порядок,
   * що й в `AsyncBoundary` («помилка йде ПЕРШОЮ: невдалий запит теж лишає
   * `data` порожнім»).
   *
   * ⛔ Перша редакція цієї правки читала `languages.data.length` напряму — і
   * впала на двох сусідніх наборах: їхні заглушки віддають `null` на
   * незнайому адресу, тобто `data` не `undefined`, а `null`, і `.length` кидав
   * `TypeError`, валячи весь діалог. Саме те, від чого правка мала берегти,
   * вона й робила — лише гучніше.
   */
  const list = languages.data ?? [];

  if (list.length === 0) {
    return (
      <Text size="sm" c="dimmed" data-localized-input="empty">
        {t('state.emptyTitle')}
      </Text>
    );
  }

  return (
    <Stack gap="xs">
      {list.map((language) => (
        <TextInput
          key={language.code}
          label={`${label} · ${language.nameNative}`}
          description={language.isDefault ? description : undefined}
          required={required && language.isDefault}
          value={value[language.code] ?? ''}
          onChange={(event) => {
            const next = { ...value };
            const text = event.currentTarget.value;

            if (text.length === 0) {
              delete next[language.code];
            } else {
              next[language.code] = text;
            }

            onChange(next);
          }}
        />
      ))}
    </Stack>
  );
}

/** Чи заповнена назва хоча б однією мовою. */
export function hasAnyText(value: LocalizedValue): boolean {
  return Object.values(value).some((text) => text.trim().length > 0);
}
