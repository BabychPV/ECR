import type { JSX } from 'react';
import { Box, Group, Text, UnstyledButton } from '@mantine/core';
import { formatNumber } from '@/shared/format';
import { toneFills, type StatusTone } from '@/shared/ui/StatusBadge';

/**
 * `StatStrip` — ОДНА приглушена смуга показників над переліком
 * (`KIT.md` §1.6 і §6 `E.StatStrip`, директива №15 §2 «Шар 3», правила `L3`
 * і `L4`).
 *
 * Дослівна вимога макета (`KIT.md:30`): «ОДНА смуга, ≤ 4 показники, дрібний
 * підпис + число моноширинним, **без плиток, рамок і кольорових фонів**;
 * показник клікабельний і фільтрує перелік під ним; колір лише в
 * показника-проблеми (напр. «7 validation errors»)».
 *
 * ⛔ Що з макета НЕ перенесено, і чому:
 *
 *  1. **Рамки смуги** (`index.html:220-223`: `border-top`/`border-bottom` на
 *     `.statstrip`, `border-right` між показниками). `KIT.md` §1.6 у тому
 *     самому реченні вимагає «без плиток, **рамок** і кольорових фонів» — тобто
 *     CSS прототипу суперечить його ж власній специфікації. Узято текст
 *     специфікації: роздільники — це і є та сама «плитка», лише тонша, і саме
 *     вони робили смугу з B «Control Room» гучною. Розділяє відступ шкали.
 *  2. **Підкреслення активного фірмовим синім**
 *     (`.stat[aria-pressed="true"]{box-shadow:inset 0 -2px 0 var(--accent)}`).
 *     Активність несуть `aria-pressed`, напівжирний підпис і підкреслення —
 *     три канали (`ФВ-14.18`), з яких жоден не кольоровий. Додати сюди ще й
 *     колір означало б завести в компоненті ДРУГИЙ привід пофарбувати щось,
 *     окрім проблеми, — і правило `L3` перестало б перевірятися однією
 *     умовою.
 *  3. **`console.warn` про зайві показники** (`kit.js:311`) і мовчазне
 *     відкидання п'ятого. Попередження в консолі ніхто не побачить — це
 *     дослівно та сама вада, яку описує `L5` («не `console.error`:
 *     `src/test/setup.ts` консоль не стереже»). Межа стоїть у ТИПІ
 *     (`StatStripItems`), а рантайм на переповненні падає в режимі розробки.
 *  4. **`fmt.int` прототипу** — замінено на `formatNumber` із
 *     `shared/format`: число в інтерфейсі береться локаллю ПРОДУКТУ
 *     (`D15-09`), а не власним форматувальником набору.
 */

/**
 * Тон показника-ПРОБЛЕМИ.
 *
 * ⛔ Звужений `StatusTone`, а не власний союз рядків. `L3` каже «колір — лише
 * для проблеми або очікуваної дії», і єдиним джерелом кольору статусу в
 * застосунку є `StatusBadge` (директива №15 §2, шар 1: «іншого способу
 * намалювати статус у застосунку не лишається»). Власний союз завів би другу
 * таблицю тонів, яка розійшлася б із першою мовчки — рівно те, що вже сталося
 * з п'ятьма `stateColor` по сторінках.
 *
 * ⚠ `neutral`/`info`/`muted` сюди НЕ входять навмисно: показник, якому нема
 * на що скаржитися, не отримує тону взагалі — він нейтральний за
 * замовчуванням. Тон, що означає «все гаразд», був би способом обійти `L3`
 * пропом.
 */
export type StatTone = Extract<StatusTone, 'warning' | 'danger'>;

/** Один показник смуги. */
export interface StatItem {
  /** Стабільний ідентифікатор — він же значення фільтра. */
  readonly id: string;

  /**
   * Дрібний підпис під/поруч із числом.
   *
   * ⛔ Приходить ПРОПОМ, а не з каталогу за ключем (на відміну від
   * `StatusBadge`). Причина та сама, що в `DetailDrawer.closeLabel` і
   * `PageHeader.back.label`: набір не знає, який саме рядок потрібен цьому
   * переліку («running», «failed today», «validation errors»), а нових ключів
   * каталогу цей PR не заводить — `09-seed.sql` належить сусідній роботі.
   * Сторінка передає сюди вже готовий `t('…')`.
   */
  readonly label: string;

  /** Число показника. Нуль — це ДАНІ, а не порожнеча (пор. `KeyValue`). */
  readonly value: number;

  /** Знаменник: `48 / 60`. */
  readonly of?: number | undefined;

  /**
   * Тон ПРОБЛЕМИ. Діє лише за ненульового значення — див. `problemTone`.
   */
  readonly tone?: StatTone | undefined;

  /** Підказка при наведенні. */
  readonly hint?: string | undefined;

  /** `false` — показник не фільтрує (просто цифра). За замовчуванням фільтрує. */
  readonly filter?: boolean | undefined;
}

/** Межа `L4`. Одне число на компонент, тип і рантайм. */
export const StatStripMaxItems = 4;

/**
 * ⛔ `L4` у ТИПІ: «`StatStripProps.items` — кортеж довжини ≤ 4» (директива
 * №15 §0, рядок `L4`).
 *
 * ⚠ Саме союз кортежів, а не `readonly StatItem[]` із перевіркою в рантаймі.
 * Масив будь-якої довжини присвоїти союзу кортежів не можна, а кортеж із
 * п'яти елементів не підходить до жодного з чотирьох варіантів — тобто
 * п'ятий показник не компілюється, і про це відомо ДО запуску. Перевірка в
 * рантаймі лишається (`capItems`), але вона стереже лише те, що прийшло повз
 * типи: `as`, `JSON.parse`, дані сервера.
 *
 * ⚠ Порожнього кортежа в союзі немає: смуга без жодної цифри — це не смуга.
 * Сторінка, якій немає що показати, просто не передає `stats` у `ListPage`
 * (`D15-06`).
 */
export type StatStripItems =
  | readonly [StatItem]
  | readonly [StatItem, StatItem]
  | readonly [StatItem, StatItem, StatItem]
  | readonly [StatItem, StatItem, StatItem, StatItem];

export interface StatStripProps {
  /**
   * Ім'я смуги для читалки (`role="group"`).
   *
   * ⛔ Обов'язковий проп, а не дефолт `'Summary'` із прототипу: група без
   * імені оголошується як безіменна, і читалка не каже, ЧОГО ці чотири числа
   * стосуються. Рядок — з каталогу на боці сторінки (`t('…')`), див. `label`
   * показника.
   */
  readonly label: string;

  readonly items: StatStripItems;

  /**
   * Обраний показник — або `null`.
   *
   * ⛔ Компонент КЕРОВАНИЙ, на відміну від прототипу (`kit.js` тримає `active`
   * усередині вузла). Обраний фільтр — це стан подання, а він у цьому
   * застосунку живе в адресі (`ФВ-14.29`, `useUrlState`): смуга з власним
   * `useState` показувала б «running» підсвіченим після «Назад», коли перелік
   * уже нефільтрований, і навпаки — посилання з `?stat=failed` відкривало б
   * відфільтрований перелік із непідсвіченою цифрою.
   */
  readonly active?: string | null | undefined;

  /**
   * Клац по показнику. `null` — показник знято (повторний клац по активному).
   *
   * ⚠ Немає обробника — смуга лишається цифрами: кнопки без дії тут не
   * малюються, бо кнопка, яка нічого не робить, гірша за текст.
   */
  readonly onSelect?: ((id: string | null) => void) | undefined;
}

/**
 * Тон, який показник справді отримує.
 *
 * ⛔ `L3` дослівно: «колір — лише для проблеми або очікуваної дії; „все
 * гаразд“ нейтральне». Нуль проблем — це «все гаразд», тож `tone: 'danger'`
 * при `value === 0` НЕ фарбує нічого. Інакше перелік без жодної помилки
 * світився б червоним «0 validation errors» — і червоний перестав би щось
 * означати рівно там, де він потрібен.
 *
 * ⚠ Від'ємне значення тону теж не отримує: проблеми «−3» не буває, це
 * зіпсовані дані.
 */
export function problemTone(item: StatItem): StatTone | null {
  if (item.tone === undefined) return null;

  return item.value > 0 ? item.tone : null;
}

/**
 * Перевірка межі `L4` в рантаймі — для того, що прийшло повз типи.
 *
 * ⛔ У режимі розробки — ВИНЯТОК, а не `console.warn` прототипу. Причина
 * названа в `L5` для `DataTable` і тут та сама: попередження в консолі не
 * стереже ніхто (`src/test/setup.ts` консоль не перевіряє), тож п'ятий
 * показник доїхав би до користувача, тихо зникнувши з екрана.
 *
 * ⚠ У зібраному застосунку — обрізання до чотирьох, а не падіння екрана.
 * Перелік документів не має гаснути через те, що сервер віддав п'ятий
 * лічильник; межа при цьому лишається дотриманою.
 */
export function capItems(items: readonly StatItem[]): readonly StatItem[] {
  if (items.length <= StatStripMaxItems) return items;

  if (import.meta.env.DEV) {
    throw new Error(
      `[StatStrip] L4: показників має бути ≤ ${StatStripMaxItems}, передано ${items.length} ` +
        `(${items.map((item) => item.id).join(', ')}). Смуга — не панель метрик: ` +
        'зайве число прибирає увагу від потрібного.',
    );
  }

  return items.slice(0, StatStripMaxItems);
}

/**
 * Смуга показників.
 *
 * ⛔ `D15-06`: порожній перелік (він може прийти лише повз типи) дає `null`, а
 * не порожню групу. Порожня `role="group"` з іменем оголошується читалці як
 * існуюча — привид, якого не видно, але чути.
 */
export function StatStrip({
  label,
  items,
  active = null,
  onSelect,
}: StatStripProps): JSX.Element | null {
  const shown = capItems(items);

  if (shown.length === 0) return null;

  return (
    /*
     * ⚠ `gap="lg"` і жодної рамки — це і є «приглушено» (`KIT.md` §1.6).
     * Показники розділяє відстань, а не лінія.
     */
    <Group role="group" aria-label={label} gap="lg" wrap="wrap" data-stat-strip="">
      {shown.map((item) => (
        <Stat key={item.id} item={item} active={active} onSelect={onSelect} />
      ))}
    </Group>
  );
}

interface StatProps {
  readonly item: StatItem;
  readonly active: string | null;
  readonly onSelect: ((id: string | null) => void) | undefined;
}

function Stat({ item, active, onSelect }: StatProps): JSX.Element {
  const isActive = active === item.id;
  const tone = problemTone(item);

  /*
   * ⛔ Колір береться з `toneFills` — тієї самої таблиці, якою малює
   * `StatusBadge`. Власного літерала (ні `#hex`, ні `var(--…)`) тут немає
   * навмисно: `ФВ-14.11` забороняє літерал, а другий перелік токенів
   * розійшовся б із першим. Нейтральний показник бере `toneFills.neutral` —
   * тобто «немає тону» теж названо через те саме джерело, а не відсутністю
   * пропа.
   */
  const fill = toneFills[tone ?? 'neutral'];

  const marks = {
    'data-stat': item.id,
    'data-stat-tone': tone ?? 'neutral',
    'data-stat-active': String(isActive),
  } as const;

  const hint = item.hint !== undefined && item.hint !== '' ? { title: item.hint } : {};

  const content = (
    <Group gap="xs" align="baseline" wrap="nowrap">
      {/* Число — моноширинне (`KIT.md` §1.6), щоб цифри не стрибали при оновленні. */}
      <Text span ff="monospace" fw={500} c={fill.text} data-stat-value="">
        {formatNumber(item.value)}
        {item.of !== undefined && (
          <Text span ff="monospace" size="xs" c="dimmed" data-stat-of="">
            {` / ${formatNumber(item.of)}`}
          </Text>
        )}
      </Text>

      {/*
       * Підпис — дрібний і приглушений. Активність позначена напівжирним і
       * підкресленням: два НЕкольорові канали поверх `aria-pressed`
       * (`ФВ-14.18`).
       */}
      <Text
        span
        size="xs"
        c="dimmed"
        data-stat-label=""
        {...(isActive ? ({ fw: 700, td: 'underline' } as const) : {})}
      >
        {item.label}
      </Text>
    </Group>
  );

  const select = item.filter === false ? undefined : onSelect;

  if (select === undefined) {
    return (
      <Box {...marks} {...hint}>
        {content}
      </Box>
    );
  }

  return (
    <UnstyledButton
      type="button"
      aria-pressed={isActive}
      onClick={() => {
        // Повторний клац по активному ЗНІМАЄ фільтр — інакше зняти його з
        // самої смуги було б нічим (`kit.js:318`).
        select(isActive ? null : item.id);
      }}
      {...marks}
      {...hint}
    >
      {content}
    </UnstyledButton>
  );
}
