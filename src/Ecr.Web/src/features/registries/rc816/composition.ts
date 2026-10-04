import type { components } from '@/api/schema';
import type { RegistryDefinitionDto, RegistryFieldSaveDto } from '@/api/types';
import { normalizeUserDecimal } from '@/shared/format/userDecimal';

/**
 * Композиція довідників (`ФВ-8.16`, `D-155`, FEATURE-REGISTRY-TABLES §4.8): поле `Lookup`
 * дочірнього довідника оголошує його рядок ЧАСТИНОЮ батька.
 *
 * ⛔ Усе тут — лише читання опису, який віддає сервер. Правила композиції (одне поле, інший
 * довідник, без кіл, обов'язковість, нетемпоральна дитина) перевіряє
 * `RegistryCompositionRules.Validate`; клієнт попереджає заздалегідь, а не вирішує замість нього.
 */

/** Що робити з частинами, коли видаляють батька. */
export type ParentDeletePolicy = NonNullable<components['schemas']['ParentDeletePolicy']>;

/** Політики видалення батька в порядку показу; перша — типова на сервері. */
export const ParentDeletePolicies: readonly ParentDeletePolicy[] = ['Restrict', 'Cascade'];

/** Зв'язок «дитина → батько» композиції, як його описує `relations[]` дитини. */
export interface CompositionLink {
  /** Поле композиції дитини. */
  readonly fieldCode: string;
  /** Довідник-батько. */
  readonly parentRegistryDefId: number;
  /** Код довідника-батька; `null` — сервер не знайшов назви. */
  readonly parentRegistryCode: string | null;
  /** Політика видалення батька. */
  readonly onParentDelete: ParentDeletePolicy;
}

/**
 * Поле композиції довідника — зв'язок до його батька; `null` — довідник нічия не частина.
 *
 * ⚠ Береться з `relations[]`, а не з `fields[]`: `RegistryFieldDto` ознаки композиції не несе
 * (серверу бракує `fields[].relationKind` — названо у звіті лінії), а зв'язок виду `Composition`
 * сервер будує саме з поля (`RegistryDefinitionHandlers.Relations`).
 */
export function compositionOf(definition: Pick<RegistryDefinitionDto, 'relations'>): CompositionLink | null {
  const relation = definition.relations.find(
    (item) => item.kind === 'Composition' && item.fieldCode !== null && item.targetRegistryDefId !== null,
  );
  if (relation === undefined) return null;

  return {
    fieldCode: relation.fieldCode as string,
    parentRegistryDefId: relation.targetRegistryDefId as number,
    parentRegistryCode: relation.targetRegistryCode,
    onParentDelete: relation.onParentDelete ?? 'Restrict',
  };
}

/** Ознаки композиції нового поля в чернетці конструктора. */
export interface CompositionDraft {
  /** `Composition` — поле робить запис частиною батька; інакше звичайне посилання. */
  readonly relationKind?: 'Composition' | null;
  /** Політика видалення батька; лише для композиції. */
  readonly onParentDelete?: ParentDeletePolicy | null;
}

/** Чи поле чернетки — композиція. */
export function isComposition(draft: CompositionDraft & { readonly dataType: string }): boolean {
  return draft.dataType === 'Lookup' && draft.relationKind === 'Composition';
}

/**
 * Частина тіла збереження нового поля, що стосується композиції.
 *
 * ⛔ `isRequired: true` для композиції — не вибір форми, а умова сервера
 * (`err.ECR-REG-0422.compositionNotRequired`): частина без батька не видна ніколи. Для решти полів
 * повертаються явні `null` — «звичайне посилання», і наявне значення `isRequired` не чіпається.
 */
export function compositionSave(
  draft: CompositionDraft & { readonly dataType: string },
): Pick<RegistryFieldSaveDto, 'relationKind' | 'onParentDelete'> & { isRequired?: true } {
  if (!isComposition(draft)) return { relationKind: null, onParentDelete: null };

  return {
    relationKind: 'Composition',
    onParentDelete: draft.onParentDelete ?? 'Restrict',
    isRequired: true,
  };
}

/** Ознаки композиції нового поля, прочитані з чернетки сервера. */
export function compositionFromSave(field: Pick<RegistryFieldSaveDto, 'relationKind' | 'onParentDelete'>): CompositionDraft {
  return field.relationKind === 'Composition'
    ? { relationKind: 'Composition', onParentDelete: field.onParentDelete ?? 'Restrict' }
    : {};
}

/** Причина, з якої сервер відмовить у композиції, — ключ тексту. */
export type CompositionIssue =
  | 'registries.rc816.issueMoreThanOne'
  | 'registries.rc816.issueTargetSelf'
  | 'registries.rc816.issueTemporal'
  | 'registries.rc816.issueNoTarget';

/**
 * Що не так із композицією нового поля — до збереження, словами, а не відмовою `422`.
 *
 * @param definition Опис довідника, куди додається поле.
 * @param draft Нове поле.
 * @param otherNew Решта нових полів цього сеансу (друге поле композиції серед них — теж порушення).
 */
export function compositionIssues(
  definition: Pick<RegistryDefinitionDto, 'id' | 'isTemporal' | 'relations'>,
  draft: CompositionDraft & { readonly dataType: string; readonly lookupRegistryDefId: number | null },
  otherNew: readonly (CompositionDraft & { readonly dataType: string })[],
): CompositionIssue[] {
  if (!isComposition(draft)) return [];

  const issues: CompositionIssue[] = [];
  if (compositionOf(definition) !== null || otherNew.some((field) => field !== draft && isComposition(field))) {
    issues.push('registries.rc816.issueMoreThanOne');
  }
  if (draft.lookupRegistryDefId === null) issues.push('registries.rc816.issueNoTarget');
  else if (draft.lookupRegistryDefId === definition.id) issues.push('registries.rc816.issueTargetSelf');
  if (definition.isTemporal) issues.push('registries.rc816.issueTemporal');

  return issues;
}

/** Шаблон правила «Сума дочірніх» (`childSum`, FEATURE-REGISTRY-TABLES §6). */
export interface ChildSumRule {
  /** Код правила. */
  readonly code: string;
  /** Дочірній довідник. */
  readonly child: string;
  /** Поле дитини, яке сумується. */
  readonly field: string;
  /** Ціль суми — рядком, без втрати знаків. */
  readonly target: string;
  /** Допуск — рядком. */
  readonly tolerance: string;
  /** Рівень порушення. */
  readonly severity: string;
}

function numberParameter(value: unknown): string | null {
  if (typeof value === 'number' && Number.isFinite(value)) return String(value);
  if (typeof value === 'string' && parseDecimal(value) !== null) return value.trim();
  return null;
}

/**
 * Активні правила «Сума дочірніх» батька — параметри живого індикатора Σ.
 *
 * ⛔ Вираз правила НЕ розбирається (§6: «вираз правила не розбирається на клієнті»): індикатор
 * читає рівно ті параметри, з яких сервер вираз згенерував (`RegistryRuleTemplates`). Правило з
 * неповними параметрами пропускається — вигадана ціль показала б «✓» там, де сервер скаже інше.
 */
export function childSumRules(definition: Pick<RegistryDefinitionDto, 'rules'>): ChildSumRule[] {
  const rules: ChildSumRule[] = [];

  for (const rule of definition.rules) {
    if (!rule.isActive || rule.parametersJson === null) continue;

    let parameters: unknown;
    try {
      parameters = JSON.parse(rule.parametersJson);
    } catch {
      continue;
    }
    if (parameters === null || typeof parameters !== 'object') continue;

    const p = parameters as Record<string, unknown>;
    const target = numberParameter(p['target']);
    const tolerance = numberParameter(p['tolerance'] ?? 0);
    if (
      p['template'] !== 'childSum'
      || typeof p['child'] !== 'string'
      || typeof p['field'] !== 'string'
      || target === null
      || tolerance === null
    ) {
      continue;
    }

    rules.push({
      code: rule.code,
      child: p['child'],
      field: p['field'],
      target,
      tolerance,
      severity: rule.severity,
    });
  }

  return rules;
}

/** Десяткове число без втрати знаків: `units × 10^-scale`. */
export interface Decimal {
  readonly units: bigint;
  readonly scale: number;
}

/** Найбільша експонента, яку ще має сенс читати: масштаб `decimal` .NET — 28. */
const MaxDecimalExponent = 28;

const DecimalPattern =/^([+-]?)(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$/;

/**
 * Читає число: інваріантний запис сервера (`12.5`, `1E-05`) або введене людиною за правилами
 * сервера (`normalizeUserDecimal`: `12,5`, `1 234,5`). `null` — не число або неоднозначне
 * (`1,234`): Σ не вгадує те, що сервер однаково відхилить.
 *
 * ⚠ `BigInt`, а не `Number`: склад газу — це десятки значень із шістьма знаками, і Σ у подвійній
 * точності давала б `99.99999999999999` там, де сервер (decimal) рахує рівно `100`.
 */
export function parseDecimal(raw: string): Decimal | null {
  const read = normalizeUserDecimal(raw);
  if (read.kind !== 'number') return null;

  const match = DecimalPattern.exec(read.text);
  if (match === null) return null;

  const [, sign = '', whole = '', fraction = '', exponent = '0'] = match;
  if (whole.length === 0 && fraction.length === 0) return null;
  // ⛔ Експонента лише в межах `decimal` .NET (28 знаків): `1e-100000000` інакше змушувало б на
  // КОЖЕН рендер Σ рахувати `10n ** 100000000n` (вкладка висне), а ще цифра — `RangeError` у рендері
  // і межа маршруту розмонтовує редактор з усіма незбереженими правками. Сервер такого не прийме.
  if (Math.abs(Number(exponent)) > MaxDecimalExponent) return null;

  let units = BigInt(`${whole}${fraction}` || '0');
  let scale = fraction.length - Number(exponent);
  if (scale < 0) {
    units *= 10n ** BigInt(-scale);
    scale = 0;
  }

  return { units: sign === '-' ? -units : units, scale };
}

function aligned(a: Decimal, b: Decimal): [bigint, bigint, number] {
  const scale = Math.max(a.scale, b.scale);
  return [a.units * 10n ** BigInt(scale - a.scale), b.units * 10n ** BigInt(scale - b.scale), scale];
}

/** Сума чисел; нечислові й порожні значення не додаються, а рахуються окремо. */
export function sumDecimals(values: readonly (string | null | undefined)[]): { sum: Decimal; skipped: number } {
  let sum: Decimal = { units: 0n, scale: 0 };
  let skipped = 0;

  for (const value of values) {
    if (value === null || value === undefined || value.trim() === '') continue;
    const parsed = parseDecimal(value);
    if (parsed === null) {
      skipped += 1;
      continue;
    }
    const [x, y, scale] = aligned(sum, parsed);
    sum = { units: x + y, scale };
  }

  return { sum, skipped };
}

/** Інваріантний запис числа без зайвих нулів дробу (`100.0002`, `-0.5`, `0`). */
export function formatDecimal(value: Decimal): string {
  const negative = value.units < 0n;
  const digits = (negative ? -value.units : value.units).toString().padStart(value.scale + 1, '0');
  const whole = digits.slice(0, digits.length - value.scale);
  const fraction = digits.slice(digits.length - value.scale).replace(/0+$/, '');
  const text = fraction.length > 0 ? `${whole}.${fraction}` : whole;
  return negative && text !== '0' ? `-${text}` : text;
}

/** Чи `|sum − target| ≤ tolerance` — та сама межа, що в згенерованому виразі (`<=`). */
export function withinTolerance(sum: Decimal, target: string, tolerance: string): boolean | null {
  const goal = parseDecimal(target);
  const slack = parseDecimal(tolerance);
  if (goal === null || slack === null) return null;

  const [s, g, scale] = aligned(sum, goal);
  const diff: Decimal = { units: s > g ? s - g : g - s, scale };
  const [d, t] = aligned(diff, { units: slack.units < 0n ? -slack.units : slack.units, scale: slack.scale });
  return d <= t;
}
