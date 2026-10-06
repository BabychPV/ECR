import { lazy, Suspense, useCallback, useId, useRef, useState, type JSX, type KeyboardEvent } from 'react';
import { Text, TextInput, type MantineSize } from '@mantine/core';
import { formatPeriodKey } from '@/shared/format';
import { t } from '@/shared/i18n';
import type { PeriodStateSummary } from './periodStates';
import { useFieldDraft } from './useFieldDraft';

import './PeriodPicker.css';

/**
 * ⛔ Сітка періодів і все про стан періоду (запит календарів, чип, «closes in»)
 * — лінивими чанками (UI-13, бюджет `D-132`): вибір періоду стоїть статично на
 * шести сторінках, а стан потрібен лише переліку документів. Статичний
 * імпорт `formatDate`/`formatCount`/`useQueries` звідси переносив спільні
 * модулі в чанки цих сторінок (+3…5 КБ gzip).
 */
const PeriodMonthGrid = lazy(() => import('./PeriodMonthGrid'));
const PeriodStatesLoader = lazy(() => import('./periodStates').then((m) => ({ default: m.PeriodStatesLoader })));
const PeriodStateChip = lazy(() => import('./periodStates').then((m) => ({ default: m.PeriodStateChip })));
const PeriodDeadline = lazy(() => import('./periodStates').then((m) => ({ default: m.PeriodDeadline })));

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

/**
 * Скільки періодів у році за періодичністю проєкту (`X-34`).
 *
 * ⛔ `PeriodKey = Year*100 + Sequence` (`R-A6`): у квартальному проєкті 202504 —
 * ЧЕТВЕРТИЙ КВАРТАЛ, а підпис «April 2025» і крок ›  з 202504 на 202505
 * (неіснуючий період) брехали б рівно про те, що людина обирає.
 */
function periodsPerYear(kind: string | undefined): number {
  if (kind === 'Quarterly') return 4;
  if (kind === 'Yearly') return 1;

  return LastMonth;
}

/** Сусідній період для НЕмісячної періодичності: номер крутиться в межах року. */
function shiftSequence(value: number, delta: -1 | 1, perYear: number): number | null {
  const parsed = parsePeriodKey(value);
  if (parsed === null || parsed.month > perYear) return null;

  let { year, month } = parsed;
  month += delta;

  if (month > perYear) {
    month = 1;
    year += 1;
  } else if (month < 1) {
    month = perYear;
    year -= 1;
  }

  return toPeriodKey({ year, month });
}

/** Підпис періоду мовою інтерфейсу («Вересень 2026»), чи `undefined` для невалідного значення. */
function periodCaption(value: number, kind?: string): string | undefined {
  // ⚠ `X-34`/`A2-10`: квартал і рік — за періодичністю; місяць — із каталогу (форматер `A2-10`).
  const formatted = formatPeriodKey(value, kind);

  return formatted.length > 0 ? formatted : undefined;
}

interface PeriodPickerProps {
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
  /**
   * Видимий підпис над контролом. ✎ UI-13: без пропа підпису над контролом
   * немає (макет: сегментований контрол сам каже, що він — період), а
   * доступне ім'я поля — `documents.period`, як і було.
   */
  readonly label?: string;
  readonly size?: MantineSize;
  readonly miw?: number | string;
  readonly disabled?: boolean;
  readonly id?: string;
  /**
   * Періодичність проєкту (`PeriodKind`: `Monthly`/`Quarterly`/`Yearly`/`Custom`).
   *
   * ⚠ Необов'язкова й додана, а не змінена (`X-34`): без неї поведінка — та
   * сама, що й була (місячна). Виклик, який знає проєкт, передає її — і
   * підпис та крок стрілок ідуть за кварталами чи роками.
   */
  readonly periodKind?: string | undefined;
  /**
   * Проєкти, чиї календарі дають стан періоду (UI-13): чип «Open», значки в
   * сітці й підпис «closes in N days · дата». Без пропа — вибір періоду без
   * стану, як у формах.
   */
  readonly projectIds?: readonly number[] | undefined;
}

/**
 * Вибір звітного періоду за макетом (UI-13, `KIT.md` §6.7, `kit.js`
 * `PeriodPicker`): один сегментований контрол `‹ [календар] September 2026 ○ Open ›`,
 * клік або ↓ відкриває сітку періодів року зі станом, поруч — «Open · closes in
 * 12 days · 30 Sep 2026».
 *
 * ⛔ Людина бачить назву періоду і поза фокусом, і У ФОКУСІ (раніше у фокусі
 * поле показувало технічний `202610`). Набір ключа лишився: фокус виділяє
 * назву, і перша ж цифра замінює її набором `YYYYMM`; лише поки людина
 * друкує, поле показує набране.
 */
export function PeriodPicker({
  value,
  onChange,
  label,
  size = 'xs',
  miw = 130,
  disabled = false,
  id,
  periodKind,
  projectIds,
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
  const external = value === null ? '' : String(value);
  const field = useFieldDraft<string>(external);
  const draft = field.value;
  const local = /^\d{6}$/.test(draft) ? Number(draft) : null;
  const complete = local !== null && isCompletePeriodKey(local);
  // ⚠ Людина друкує ключ: поле показує набране, а не назву (див. опис компонента).
  const [typing, setTyping] = useState(false);
  const [open, setOpen] = useState<false | 'field' | 'grid'>(false);
  const [states, setStates] = useState<ReadonlyMap<number, PeriodStateSummary> | undefined>(undefined);
  const anchor = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);
  const generatedId = useId();
  const inputId = id ?? generatedId;

  // ⚠ Стрілки крокують від набраного, лише коли воно повне; від неповного —
  // від ЧИННОГО періоду, як і раніше.
  const current = complete ? local : value;
  const perYear = periodsPerYear(periodKind);
  // ⚠ Місячна (і невідома) періодичність — той самий календарний крок, що й
  // був; решта — номер у межах року (`X-34`).
  const step = (from: number, delta: -1 | 1): number | null =>
    perYear === LastMonth ? shiftPeriod(from, delta) : shiftSequence(from, delta, perYear);
  const prevValue = current === null ? null : step(current, -1);
  const nextValue = current === null ? null : step(current, 1);
  // ⚠ Поки набір неповний, назва попереднього періоду брехала б — тоді поле показує набране.
  const caption = complete ? periodCaption(local, periodKind) : undefined;
  const shown = typing && field.focused ? draft : (caption ?? draft);

  const summary = complete ? states?.get(local) : undefined;
  const gridYear = current !== null ? Math.trunc(current / YearMultiplier) : new Date().getFullYear();

  const commit = (next: number | null): void => {
    setTyping(false);
    field.setValue(next === null ? '' : String(next));
    onChange(next);
  };

  const handleInput = (raw: string): void => {
    const next = raw.replace(/\D/g, '');
    setTyping(true);
    setOpen(false);
    field.setValue(next);

    if (next === '') {
      onChange(null);
    } else if (/^\d{6}$/.test(next) && isCompletePeriodKey(Number(next))) {
      onChange(Number(next));
    }
  };

  const close = useCallback((returnFocus: boolean): void => {
    setOpen(false);
    if (returnFocus) input.current?.focus();
  }, []);

  const handleFieldKey = (event: KeyboardEvent<HTMLInputElement>): void => {
    if (event.key === 'ArrowDown' && !disabled) {
      event.preventDefault();
      setOpen('grid');
    } else if (event.key === 'Escape' && open !== false) {
      event.preventDefault();
      setOpen(false);
    } else if (event.key === 'Enter' && !event.nativeEvent.isComposing && complete) {
      /*
       * A4-02: Enter на ПОВНОМУ періоді — підтвердження: поле віддає фокус і показує назву («September 2026»),
       * однаково на всіх екранах. Неповний набір Enter не чіпає: фокус лишається, продовжуй набір.
       */
      setOpen(false);
      event.currentTarget.blur();
    }
  };

  const fieldLabel = label ?? t('documents.period');

  return (
    <div className="ecr-period-anchor" ref={anchor}>
      {label !== undefined && (
        <Text component="label" htmlFor={inputId} size="sm" fw={500}>
          {label}
        </Text>
      )}
      <div className="ecr-period-row">
        <div className="ecr-period" role="group" aria-label={t('period.group')}>
          <button
            type="button"
            aria-label={t('period.previous')}
            disabled={disabled || prevValue === null}
            onClick={() => commit(prevValue)}
          >
            <Chevron direction="left" />
          </button>

          <div className="ecr-period-field">
            <button
              type="button"
              className="ecr-period-calendar"
              aria-label={t('period.choose')}
              aria-haspopup="dialog"
              aria-expanded={open !== false}
              disabled={disabled}
              onClick={() => setOpen(open === false ? 'grid' : false)}
            >
              <CalendarIcon />
            </button>
            <TextInput
              id={inputId}
              ref={input}
              variant="unstyled"
              size={size}
              miw={miw}
              aria-label={label === undefined ? fieldLabel : undefined}
              inputMode="numeric"
              autoComplete="off"
              disabled={disabled}
              value={shown}
              onChange={(event) => handleInput(event.currentTarget.value)}
              onClick={() => {
                if (!disabled && open === false) setOpen('field');
              }}
              onKeyDown={handleFieldKey}
              onFocus={(event) => {
                field.onFocus();
                // Назва виділена: перша ж цифра замінює її набором ключа `YYYYMM`.
                const target = event.currentTarget;
                queueMicrotask(() => target.select());
              }}
              // ⚠ Незавершений набір, покинутий фокусом, повертає поле до чинного
              // періоду: інакше поле показувало б «2026», а список — інший період.
              // Повний набір лишається: `value` у цю мить може ще нести запізніле
              // відлуння адреси, і підтягнути його означало б стерти набране.
              onBlur={() => {
                field.onBlur();
                setTyping(false);
                if (!complete) field.setValue(external);
              }}
            />
            {summary?.uniform === true && (
              <Suspense fallback={null}>
                <PeriodStateChip summary={summary} />
              </Suspense>
            )}
          </div>

          <button
            type="button"
            aria-label={t('period.next')}
            disabled={disabled || nextValue === null}
            onClick={() => commit(nextValue)}
          >
            <Chevron direction="right" />
          </button>
        </div>

        {summary !== undefined && (
          <Suspense fallback={null}>
            <PeriodDeadline summary={summary} />
          </Suspense>
        )}
      </div>

      {open !== false && (
        <Suspense fallback={null}>
          <PeriodMonthGrid
            year={gridYear}
            perYear={perYear}
            periodKind={periodKind}
            value={current}
            states={states}
            anchor={anchor}
            focusOnOpen={open === 'grid'}
            onPick={(key) => {
              commit(key);
              close(true);
            }}
            onClose={close}
          />
        </Suspense>
      )}

      {projectIds !== undefined && projectIds.length > 0 && (
        <Suspense fallback={null}>
          <PeriodStatesLoader projectIds={projectIds} onChange={setStates} />
        </Suspense>
      )}
    </div>
  );
}

function Chevron({ direction }: { direction: 'left' | 'right' }): JSX.Element {
  return (
    <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
      <path d={direction === 'left' ? 'M15 6l-6 6 6 6' : 'M9 6l6 6-6 6'} />
    </svg>
  );
}

function CalendarIcon(): JSX.Element {
  return (
    <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
      <rect x={3} y={5} width={18} height={16} rx={2} />
      <path d="M3 10h18M8 3v4M16 3v4" />
    </svg>
  );
}
