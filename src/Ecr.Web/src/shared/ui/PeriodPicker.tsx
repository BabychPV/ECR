import type { JSX } from 'react';
import { ActionIcon, Group, NumberInput, type MantineSize } from '@mantine/core';
import { formatDate } from '@/shared/format';
import { t } from '@/shared/i18n';
import { useFieldDraft } from './useFieldDraft';

/**
 * `PeriodPicker` (директива №15 §2, Шар 3, UI-06; те саме завдання, що
 * `DIRECTIVE-14-UIUX.md` U.3, DoD: «стрілка з грудня веде в січень наступного
 * року за календарем, не `+1`»).
 *
 * ⛔ Замінює голий `NumberInput` із написом `Period: 202609`
 * (`DIRECTIVE-14-UIUX.md:110-112`): користувач мав знати КОДУВАННЯ
 * `periodKey` (`YYYYMM`), а стрілка з `…12` арифметикою `+1` вела в `…13` —
 * невалідний період. `R-A6` прямо забороняє виводити місяць із `PeriodKey`
 * арифметикою, а старе поле саме до цього й запрошувало.
 *
 * ⚠ Прогалина факту (директива, а не судження): `docs/build/DIRECTIVE-15-FRONTEND.md:129`
 * вимагає «місяць/квартал/рік за типом періоду шаблону», але типу періоду
 * (`PeriodType`) немає НІДЕ в системі — ані в `api/schema.d.ts`, ані на
 * бекенді (перевірено `git grep -i periodtype` по всьому репозиторію, нуль
 * збігів), ані ендпоінта переліку періодів проєкту (`GET /projects/{id}/periods`
 * з того самого U.3 теж не існує). Тому ця версія підтримує лише МІСЯЧНУ
 * гранулярність — саме ту, яку кодує чинний `periodKey` (`YYYYMM`) у
 * `DocumentsPage`/`DocumentPage` вже сьогодні. Перемикання місяць/квартал/рік
 * — окремий крок, що чекає на дані з бекенда; зафіксовано в звіті PR.
 *
 * ⚠ Формат `periodKey` НЕ змінюється: `value`/`onChange` лишаються
 * `number | null`, як і в замінюваному `NumberInput` — виклики цього
 * компонента підставляються в ті самі `useUrlNumber('periodKey')`.
 */

const YearMultiplier = 100;
const FirstMonth = 1;
const LastMonth = 12;
/** Межі року — ті самі, що в `PeriodKey.IsValid` на сервері (`Ecr.Domain/ValueObjects/PeriodKey.cs`). */
const FirstYear = 1900;
const LastYear = 9999;

interface ParsedPeriod {
  readonly year: number;
  readonly month: number;
}

/** Розбирає `periodKey` (`YYYYMM`) на рік і місяць; `null` — значення не період. */
function parsePeriodKey(value: number): ParsedPeriod | null {
  if (!Number.isFinite(value)) return null;

  const year = Math.trunc(value / YearMultiplier);
  const month = value - year * YearMultiplier;

  if (month < FirstMonth || month > LastMonth) return null;

  return { year, month };
}

/**
 * Чи є набране значення ПОВНИМ дійсним `periodKey`: ціле, рік `1900..9999`
 * (тобто рівно шість цифр), місяць `01..12`.
 *
 * ⛔ Лише таке значення йде в `onChange`. Раніше туди йшла кожна проміжна
 * цифра: набір `202608` давав `2`, `20`, `202`, `2026`, `20260` — і кожна
 * ставала `?periodKey=` в адресі й запитом до сервера, на який той чесно
 * відповідав `422` (`PeriodKey.Parse`). Живий стенд: 5 × `422` на `/` і
 * 5 × `422` на `/admin/campaign` на один набір.
 */
export function isCompletePeriodKey(value: number): boolean {
  if (!Number.isInteger(value)) return false;

  const parsed = parsePeriodKey(value);

  return parsed !== null && parsed.year >= FirstYear && parsed.year <= LastYear;
}

function toPeriodKey(period: ParsedPeriod): number {
  return period.year * YearMultiplier + period.month;
}

/**
 * Сусідній період КАЛЕНДАРЕМ, а не `periodKey ± 1`.
 *
 * ⛔ Доказ через мутацію: заміна тіла на `value + delta` лишає `202512 → 1`
 * крок «наступний» рівним `202513` — невалідному periodKey, і
 * `PeriodPicker.test.tsx` це ловить (`shiftPeriod` тестується прямо і через
 * клік по стрілці).
 */
function shiftPeriod(value: number, delta: -1 | 1): number | null {
  const parsed = parsePeriodKey(value);
  if (parsed === null) return null;

  let { year, month } = parsed;
  month += delta;

  if (month > LastMonth) {
    month = FirstMonth;
    year += 1;
  } else if (month < FirstMonth) {
    month = LastMonth;
    year -= 1;
  }

  return toPeriodKey({ year, month });
}

/** Підпис періоду мовою інтерфейсу («Вересень 2026»), чи `undefined` для невалідного значення. */
function periodCaption(value: number): string | undefined {
  const parsed = parsePeriodKey(value);
  if (parsed === null) return undefined;

  const formatted = formatDate(new Date(parsed.year, parsed.month - 1, 1), {
    year: 'numeric',
    month: 'long',
  });

  return formatted.length > 0 ? formatted : undefined;
}

export interface PeriodPickerProps {
  /** `periodKey` (`YYYYMM`), як в адресі (`ФВ-14.29`); `null` — період не обрано. */
  readonly value: number | null;
  /**
   * Кличеться лише з ПОВНИМ дійсним `periodKey` (`isCompletePeriodKey`) або
   * з `null`, коли поле очищено; проміжні цифри набору сюди не доходять.
   * Кожен виклик сам вирішує, що
   * робити з `null` (звузити фільтр до «без періоду», чи лишити попередній
   * `periodKey`) — `PeriodPicker` цього рішення не нав'язує.
   */
  readonly onChange: (value: number | null) => void;
  /** За замовчуванням — `documents.period`, той самий ключ, що й у заміненого поля. */
  readonly label?: string;
  readonly size?: MantineSize;
  readonly miw?: number | string;
  readonly disabled?: boolean;
  readonly id?: string;
}

/**
 * Вибір звітного періоду: стрілки ‹ › (календарний крок) + пряме введення
 * `periodKey` (те саме поле, що й раніше, — набір цифр так само працює) +
 * підпис мовою інтерфейсу під полем.
 */
export function PeriodPicker({
  value,
  onChange,
  label,
  size = 'xs',
  miw = 130,
  disabled = false,
  id,
}: PeriodPickerProps): JSX.Element {
  /*
   * ⛔ Незавершений набір живе ЛИШЕ тут, у полі, і не йде в `onChange`: див.
   * `isCompletePeriodKey`.
   *
   * ⛔ Поки поле у фокусі, воно показує РІВНО набране — зовнішній `value` у
   * нього не пише (`useFieldDraft`). Раніше чернетка «застарівала», щойно
   * `value` змінювався ззовні, і поле знову показувало `value`: автовибір
   * періоду на `/` дописував `202609` у щойно очищене поле, і набір `202608`
   * давав `202608202609` (живий стенд, 5 з 5). Те саме робило б запізніле
   * відлуння адреси при повільному рендері. Ззовні (стрілка, «Назад»,
   * навігація) `value` приймається, коли поле не у фокусі.
   */
  const field = useFieldDraft<string | number>(value ?? '');
  const local = field.value;
  const complete = typeof local === 'number' && isCompletePeriodKey(local);

  // ⚠ Стрілки крокують від набраного, лише коли воно повне; від неповного —
  // від ЧИННОГО періоду, як і раніше.
  const current = complete ? local : value;
  const prevValue = current === null ? null : shiftPeriod(current, -1);
  const nextValue = current === null ? null : shiftPeriod(current, 1);
  // ⚠ Поки набір неповний, підпис попереднього періоду під полем брехав би
  // («2026» над «September 2026») — тому підпису немає, як і для порожнього.
  const caption = complete ? periodCaption(local) : undefined;

  const commit = (next: number | null): void => {
    field.setValue(next ?? '');
    onChange(next);
  };

  const handleInput = (next: string | number): void => {
    field.setValue(next);

    if (next === '') {
      onChange(null);
    } else if (typeof next === 'number' && isCompletePeriodKey(next)) {
      onChange(next);
    }
  };

  return (
    <Group gap="xs" align="end" wrap="nowrap">
      <ActionIcon
        variant="default"
        size={size}
        aria-label={t('period.previous')}
        disabled={disabled || prevValue === null}
        onClick={() => commit(prevValue)}
      >
        ‹
      </ActionIcon>

      <NumberInput
        id={id}
        size={size}
        miw={miw}
        label={label ?? t('documents.period')}
        description={caption}
        disabled={disabled}
        value={local}
        onChange={handleInput}
        onFocus={field.onFocus}
        // ⚠ Незавершений набір, покинутий фокусом, повертає поле до чинного
        // періоду: інакше поле показувало б «2026», а список — інший період.
        // Повний набір лишається: `value` у цю мить може ще нести запізніле
        // відлуння адреси, і підтягнути його означало б стерти набране.
        onBlur={() => {
          field.onBlur();
          if (!complete) field.setValue(value ?? '');
        }}
      />

      <ActionIcon
        variant="default"
        size={size}
        aria-label={t('period.next')}
        disabled={disabled || nextValue === null}
        onClick={() => commit(nextValue)}
      >
        ›
      </ActionIcon>
    </Group>
  );
}
