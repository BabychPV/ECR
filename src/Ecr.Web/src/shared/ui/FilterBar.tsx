import { useEffect, useMemo, useRef, type JSX, type ReactNode } from 'react';
import { Button, Group, Select, TextInput } from '@mantine/core';
import { useSearchParams } from 'react-router-dom';
import { useFieldDraft } from './useFieldDraft';
import { useUrlParamsSetter, useUrlState } from './useUrlState';

/**
 * Рядок фільтрів набору (`KIT.md` §6.3, директива №15 §2, Шар 3).
 *
 * ⛔ **Значення живуть в АДРЕСІ, не в `useState`** (`ФВ-14.29`). Це не
 * вподобання: відфільтрований перелік — саме те, що людина надсилає колезі
 * («подивись ось це») і до чого повертається завтра. У локальному стані
 * посилання відкривається порожнім екраном із проханням обрати все наново.
 * Механізм уже є — `useUrlState`; другий поруч із ним розійшовся б із першим
 * на `replace: true` (зміна фільтра НЕ додає запису в історію) і повернув би
 * «Назад», що десять разів відкочує фільтр замість того, щоб піти зі
 * сторінки.
 *
 * ⛔ **Кожне поле кличе `useUrlState` САМЕ, окремим компонентом.** Хуки не
 * викликають у циклі за пропом: перелік фільтрів у `ListPage` залежить від
 * прав і від вкладки, тобто його довжина змінюється між рендерами — і порядок
 * хуків поїхав би.
 */

/** Варіант значення фільтра. */
export interface FilterOption {
  /** Значення, яке потрапляє в адресу. */
  readonly value: string;

  /** Підпис варіанта — готовий рядок інтерфейсу, не ключ каталогу. */
  readonly label: string;
}

/** Один фільтр-перелік. */
export interface FilterSpec {
  /** Ідентифікатор — він же ІМ'Я ПАРАМЕТРА адреси. */
  readonly id: string;

  /**
   * Підпис поля.
   *
   * ⚠ Саме підпис, а не `placeholder` (`ФВ-14.20`): placeholder зникає при
   * виборі, і людина, яка відвернулася на секунду, більше не знає, що саме
   * звужує. Лінтер вимагає його окремим правилом.
   */
  readonly label: string;

  /** Варіанти. */
  readonly options: readonly FilterOption[];
}

/** Поле вільного пошуку. */
export interface FilterBarSearch {
  /** Підпис поля. */
  readonly label: string;

  /** Ім'я параметра адреси; дефолт — `q`. */
  readonly param?: string | undefined;

  /** Підказка всередині поля — ДОДАТОК до підпису, не заміна. */
  readonly placeholder?: string | undefined;
}

export interface FilterBarProps {
  /** Поле вільного пошуку; без нього поля немає зовсім (`D15-06`). */
  readonly search?: FilterBarSearch | undefined;

  /** Фільтри-переліки. */
  readonly filters?: readonly FilterSpec[] | undefined;

  /** Що поставити праворуч (перемикач подання, експорт). */
  readonly right?: ReactNode;

  /** Підпис кнопки скидання; див. `ClearGlyph`. */
  readonly clearLabel?: string | undefined;

  /** Значення змінилися. Викликається і на читанні з адреси при відкритті. */
  readonly onChange?: ((values: Readonly<Record<string, string>>) => void) | undefined;
}

/** Ім'я параметра пошуку за замовчуванням. */
const DefaultSearchParam = 'q';

/**
 * Підпис кнопки скидання за замовчуванням.
 *
 * ⛔ Символ, а не англійське слово: ключа під «Clear filters» у каталозі
 * (`09-seed.sql`) немає, а завести його цим PR не можна — сід є спільним
 * ресурсом паралельної фази. `t()` на неіснуючий ключ показав би читалці
 * `⟦filters.clear⟧` (`D-138`), а зашитий англійський літерал лишився б
 * англійським і в казахському інтерфейсі. Гліф однаковий у всіх трьох мовах
 * продукту.
 *
 * ⚠ Названа прогалина, не рішення назавжди: викликач передає `clearLabel`,
 * щойно ключ з'явиться, і жоден рядок цього файлу не змінюється.
 */
const ClearGlyph = '×';

export function FilterBar({
  search,
  filters,
  right,
  clearLabel = ClearGlyph,
  onChange,
}: FilterBarProps): JSX.Element | null {
  const [params] = useSearchParams();
  const setParams = useUrlParamsSetter();

  const searchParam = search?.param ?? DefaultSearchParam;

  /*
   * ⚠ Перелік ІМЕН, якими володіє цей рядок фільтрів. Скидання чистить саме
   * їх, а не всю адресу: `?panel=`, `?tab=` і `?periodKey=` належать екрану, і
   * «Clear filters», що змітає відкриту шухляду разом із фільтром, — це не те,
   * про що просив користувач.
   */
  const names = useMemo(() => {
    const list = filters === undefined ? [] : filters.map((filter) => filter.id);

    return search === undefined ? list : [searchParam, ...list];
  }, [filters, search, searchParam]);

  const values = useMemo<Readonly<Record<string, string>>>(() => {
    const entries: [string, string][] = [];

    for (const name of names) {
      const value = params.get(name);
      if (value !== null && value.length > 0) entries.push([name, value]);
    }

    return Object.fromEntries(entries);
  }, [names, params]);

  /*
   * ⛔ Кнопка з'являється САМА — і саме тому вона прив'язана до вмісту адреси,
   * а не до прапорця з екрана. Прапорець довелося б не забути перемкнути в
   * кожному з чотирьох місць, де значення змінюється (поле, перелік, адреса
   * ззовні, «Назад»), і перше ж забуте місце лишало б кнопку, яка нічого не
   * чистить, або ховало б потрібну.
   */
  const hasValues = Object.keys(values).length > 0;

  /*
   * ⚠ `onChange` кличеться з ЕФЕКТУ, а не з обробника поля: значення може
   * змінитися й повз рядок фільтрів — кнопкою `StatStrip`, посиланням із
   * сусіднього екрана, «Назад». Обробник поля про ці шляхи не знає, і
   * сторінка отримувала б повідомлення лише про половину змін.
   */
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;

  const valuesRef = useRef(values);
  valuesRef.current = values;

  // ⚠ Залежність — РЯДОК, а не об'єкт: `values` збирається наново на кожному
  // рендері, і порівняння за посиланням кликало б обробник нескінченно.
  const signature = JSON.stringify(values);

  useEffect(() => {
    onChangeRef.current?.(valuesRef.current);
  }, [signature]);

  // `D15-06`: порожній рядок фільтрів не малюється — він з'їдав би відступ.
  if (search === undefined && (filters === undefined || filters.length === 0) && right == null) {
    return null;
  }

  return (
    <Group gap="sm" align="flex-end" mb="md" data-filter-bar="true">
      {search !== undefined && (
        <SearchField param={searchParam} label={search.label} placeholder={search.placeholder} />
      )}

      {filters?.map((filter) => (
        <SelectField key={filter.id} spec={filter} clearLabel={clearLabel} />
      ))}

      {hasValues && (
        <Button
          variant="default"
          size="xs"
          data-filter-clear="true"
          onClick={() => {
            /*
             * ⛔ ОДИН перехід на всі параметри (`useUrlParamsSetter`), а не
             * сеттер на кожен: два виклики `setSearchParams` синхронно в
             * одному обробнику губили ОБИДВІ зміни, не лише другу
             * (`useUrlState.ts`, UI-аудит lane 3). Скидання трьох фільтрів
             * трьома викликами не скинуло б жодного.
             */
            setParams(Object.fromEntries(names.map((name) => [name, null])));
          }}
        >
          {clearLabel}
        </Button>
      )}

      {right}
    </Group>
  );
}

/** Поле вільного пошуку: значення — в адресі. */
function SearchField({
  param,
  label,
  placeholder,
}: {
  readonly param: string;
  readonly label: string;
  readonly placeholder?: string | undefined;
}): JSX.Element {
  const [value, setValue] = useUrlState(param);
  // ⛔ Поле показує ВЛАСНЕ значення, а не адресу (`useFieldDraft`): кероване
  // адресою, воно губило символи при повільному рендері — адреса оновлюється
  // переходом і запізнюється (живий стенд, «Row key»/«Rule», 2026-09-24).
  // «Clear filters» і навігація приймаються, коли поле не у фокусі.
  const field = useFieldDraft(value ?? '');

  return (
    <TextInput
      label={label}
      placeholder={placeholder}
      value={field.value}
      onFocus={field.onFocus}
      onBlur={field.onBlur}
      onChange={(event) => {
        field.setValue(event.currentTarget.value);
        setValue(event.currentTarget.value);
      }}
    />
  );
}

/** Фільтр-перелік: значення — в адресі. */
function SelectField({
  spec,
  clearLabel,
}: {
  readonly spec: FilterSpec;
  readonly clearLabel: string;
}): JSX.Element {
  const [value, setValue] = useUrlState(spec.id);

  return (
    <Select
      label={spec.label}
      data={[...spec.options]}
      value={value}
      onChange={(next) => {
        setValue(next);
      }}
      /*
       * ⚠ `clearable` замість окремого варіанта «Усі»: варіант потребував би
       * власного рядка інтерфейсу, якого в каталозі немає, а хрестик Mantine
       * отримує ім'я з того самого `clearLabel`, що вже є в цього рядка
       * фільтрів. Кнопка без імені завалила б `button-name` у гейті `a11y`.
       */
      clearable
      clearButtonProps={{ 'aria-label': clearLabel }}
    />
  );
}
