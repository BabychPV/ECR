import type { AirScopeCodes, AirScopeEntry, AirScopeField, AirScopeFields } from '../airTypes';
import { NoArea } from './importModel';

/*
 * Область імпорту параметрів (AIR-ON-AIR, людина 02.11: «таблиця параметрів відноситься до пермиту локації та регіону»):
 * REGION і AREA — звичайні колонки AIR_SOURCES типу AIR, кожна одного з трьох видів (`GET /air-limits/scope-fields`):
 * Registry — запис спільного довідника (значення поля = id запису), List — значення типу (значення = текст вибору),
 * Text — вільний текст. Чиста модель, без React.
 */

export const ScopeKind = { Text: 'Text', Registry: 'Registry', List: 'List' } as const;

/** Значення пікера для варіанта: Registry — id запису, List — текст вибору. */
export function optionValue(field: AirScopeField, option: AirScopeEntry): string {
  return field.kind === ScopeKind.Registry && option.entryId !== null ? String(option.entryId) : option.code;
}

/** Варіант, обраний у полі; для Text — `undefined`. */
export function chosenOption(field: AirScopeField, value: string): AirScopeEntry | undefined {
  if (field.kind === ScopeKind.Text || value === '') return undefined;

  return field.options.find((option) => optionValue(field, option) === value);
}

export interface AreaChoice {
  /** Показувати поле Area: лише коли регіон має ділянки (або регіон — вільний текст, і цього не знати). */
  readonly show: boolean;
  /** Варіанти Area для обраного регіону (Registry/List); для Text — порожньо. */
  readonly options: readonly AirScopeEntry[];
}

/**
 * Area за обраним Region: регіон зіставлено з деревом НАЕ (`naeRegion`), у дереві в нього є справжні ділянки —
 * показуємо лише ті значення Area, що зіставлені (`naeArea`) з ділянками САМЕ цього регіону. Регіон без ділянок чи поза
 * деревом — поля Area немає. Регіон-текст: зіставлення невідоме — Area показується без фільтра.
 */
export function areaChoice(fields: AirScopeFields, codes: AirScopeCodes, region: string): AreaChoice {
  const all = fields.area.kind === ScopeKind.Text ? [] : fields.area.options;
  if (fields.region.kind === ScopeKind.Text) return { show: region.trim() !== '', options: all };

  const naeRegion = chosenOption(fields.region, region)?.naeRegion ?? null;
  if (naeRegion === null) return { show: false, options: [] };
  const areas = (codes.regions.find((item) => item.code === naeRegion)?.areas ?? [])
    .map((item) => item.code)
    .filter((code) => code !== NoArea);
  if (areas.length === 0) return { show: false, options: [] };

  return { show: true, options: all.filter((option) => option.naeArea !== null && areas.includes(option.naeArea)) };
}

/** Чого бракує, щоб іти далі: обов'язковий Region порожній, або Area обов'язкова й показана, але порожня. */
export function scopeProblem(fields: AirScopeFields, codes: AirScopeCodes, region: string, area: string): 'region' | 'area' | null {
  if (fields.region.required && region.trim() === '') return 'region';
  if (fields.area.required && areaChoice(fields, codes, region).show && area.trim() === '') return 'area';

  return null;
}

/** Обраний регіон відомий, але поза деревом НАЕ: коди НАЕ зіставлятимуться без області (попередження, не блок). */
export function regionOutsideTree(fields: AirScopeFields, region: string): boolean {
  const option = chosenOption(fields.region, region);

  return option !== undefined && option.naeRegion === null;
}

/**
 * Поля форми `preview`/`apply` (контракт 1fa4642cc): Registry — `regionEntryId`/`areaEntryId`, Text/List —
 * `region`/`area`. Прихована чи порожня Area не передається.
 */
export function scopeFormFields(
  fields: AirScopeFields,
  codes: AirScopeCodes,
  region: string,
  area: string,
): Readonly<Record<string, string>> {
  const out: Record<string, string> = {};
  const put = (field: AirScopeField, value: string, entryName: string, textName: string): void => {
    const trimmed = value.trim();
    if (trimmed === '') return;
    out[field.kind === ScopeKind.Registry ? entryName : textName] = trimmed;
  };

  put(fields.region, region, 'regionEntryId', 'region');
  if (areaChoice(fields, codes, region).show) put(fields.area, area, 'areaEntryId', 'area');

  return out;
}
