import type {
  AirImportOptions,
  AirPreview,
  ImportFieldSpec,
  ImportIssue,
  ImportSpec,
  MissingReference,
  MissingResolutionRequest,
} from '../airTypes';

/*
 * Модель майстра імпорту лімітів (NAE-план N5): чиста, без React. Тут живе все, що вирішує, чи можна рухатись далі:
 * побудова `request`, розв'язання відсутніх НАЕ, підтвердження заміни чужих дозволів, «застарілий» перегляд.
 */

export type ImportStep = 'file' | 'mapping' | 'preview' | 'apply';
export const ImportSteps: readonly ImportStep[] = ['file', 'mapping', 'preview', 'apply'];

/** Регіон без ділянок: код `-` (контракт). */
export const NoArea = '-';

/** Типові коди регіонів і ділянок (підказки; сервер звіряє з деревом НАЕ). */
export const RegionHints: readonly string[] = ['ATYRAU', 'MANGISTAU'];
export const AreaHints: readonly string[] = ['ONSHORE', 'OFFSHORE', NoArea];

export type ResolutionChoice = 'unset' | 'skip' | 'create';

export interface Resolution {
  readonly choice: ResolutionChoice;
  /** Id вузла дерева (рядок); обов'язковий при `create`. */
  readonly createUnder: string;
}

export const UnsetResolution: Resolution = { choice: 'unset', createUnder: '' };

export interface ImportDraft {
  readonly file: File | null;
  readonly region: string;
  readonly area: string;
  readonly profileCode: string;
  /** Редагована специфікація; `null` — працює збережений профіль як є. */
  readonly spec: ImportSpec | null;
  readonly resolutions: Readonly<Record<string, Resolution>>;
  /** Людина підтвердила заміну рядків ІНШИХ дозволів. */
  readonly confirmedReplace: boolean;
}

export const EmptyDraft: ImportDraft = {
  file: null,
  region: '',
  area: '',
  profileCode: '',
  spec: null,
  resolutions: {},
  confirmedReplace: false,
};

/** Код регіону/ділянки: без пробілів по краях, великими літерами (так їх зберігає дерево НАЕ). */
export function normalizeCode(value: string): string {
  return value.trim().toLocaleUpperCase('en-US');
}

/** Порожня ділянка — «регіон без ділянок» (`-`). */
export function normalizeArea(value: string): string {
  const code = normalizeCode(value);

  return code === '' ? NoArea : code;
}

export function resolutionKey(missing: Pick<MissingReference, 'kind' | 'code'>): string {
  return `${missing.kind}|${missing.code}`;
}

/** Рішення людини → частина `resolutions` запиту; без вибору рядка немає. */
export function toRequestResolutions(
  missing: readonly MissingReference[],
  resolutions: Readonly<Record<string, Resolution>>,
): MissingResolutionRequest[] {
  const out: MissingResolutionRequest[] = [];

  for (const item of missing) {
    const resolution = resolutions[resolutionKey(item)];
    if (resolution === undefined || resolution.choice === 'unset') continue;
    if (resolution.choice === 'skip') {
      out.push({ kind: item.kind, code: item.code, action: 'skip' });
      continue;
    }
    if (resolution.createUnder !== '') {
      out.push({ kind: item.kind, code: item.code, action: 'create', createUnder: resolution.createUnder });
    }
  }

  return out;
}

/** Відсутні, для яких рішення немає або воно неповне (`create` без вузла). */
export function unresolved(
  missing: readonly MissingReference[],
  resolutions: Readonly<Record<string, Resolution>>,
): readonly MissingReference[] {
  return missing.filter((item) => {
    const resolution = resolutions[resolutionKey(item)];
    if (resolution === undefined || resolution.choice === 'unset') return true;

    return resolution.choice === 'create' && resolution.createUnder === '';
  });
}

/**
 * Тіло `request`. Незбережена специфікація (`spec`) перемагає профіль; регіон/ділянка нормалізуються.
 * `preview` — відбиток і рішення для `apply`; для `analyse` їх не передають.
 */
export function buildOptions(
  draft: ImportDraft,
  extra: {
    readonly missing?: readonly MissingReference[];
    readonly ownerKeys?: readonly string[];
    readonly fingerprint?: string;
  } = {},
): AirImportOptions {
  const resolutions = toRequestResolutions(extra.missing ?? [], draft.resolutions);

  return {
    ...(draft.spec === null
      ? draft.profileCode === ''
        ? {}
        : { profileCode: draft.profileCode }
      : { spec: draft.spec }),
    region: normalizeCode(draft.region),
    area: normalizeArea(draft.area),
    ...(resolutions.length > 0 ? { resolutions } : {}),
    ...(extra.ownerKeys !== undefined && extra.ownerKeys.length > 0 ? { confirmedOtherOwners: extra.ownerKeys } : {}),
    ...(extra.fingerprint === undefined ? {} : { expectedFingerprint: extra.fingerprint }),
  };
}

/** Перший крок можна завершити: є файл `.xlsx` і регіон. */
export function fileStepProblem(draft: ImportDraft): 'file' | 'region' | 'extension' | null {
  if (draft.file === null) return 'file';
  if (!/\.xlsx$/i.test(draft.file.name)) return 'extension';
  if (normalizeCode(draft.region) === '') return 'region';

  return null;
}

/** Власники, чиї рядки буде замінено, — `ownerKey` для `confirmedOtherOwners` (лише після підтвердження людини). */
export function confirmedOwnerKeys(preview: AirPreview, confirmed: boolean): readonly string[] {
  const { replace } = preview.preview;

  return confirmed && replace.confirmationRequired ? replace.otherOwners.map((owner) => owner.ownerKey) : [];
}

export interface ApplyGate {
  readonly allowed: boolean;
  /** Чого бракує (для підказки біля кнопки); порожньо, коли можна. */
  readonly reasons: readonly ('errors' | 'unresolved' | 'confirm' | 'stale')[];
}

/**
 * Чи можна застосувати: сервер дозволяє (`canApply`: немає помилок), усі відсутні НАЕ розв'язані, заміна чужих
 * дозволів підтверджена, а перегляд свіжий (параметри не змінились після нього).
 */
export function applyGate(preview: AirPreview, draft: ImportDraft, stale: boolean): ApplyGate {
  const reasons: ('errors' | 'unresolved' | 'confirm' | 'stale')[] = [];
  const body = preview.preview;

  if (!body.canApply) reasons.push('errors');
  if (unresolved(body.missing, draft.resolutions).length > 0) reasons.push('unresolved');
  if (body.replace.confirmationRequired && !draft.confirmedReplace) reasons.push('confirm');
  if (stale) reasons.push('stale');

  return { allowed: reasons.length === 0, reasons };
}

/**
 * Підпис того, від чого залежить перегляд: файл, профіль/специфікація, регіон, ділянка, рішення. Інший підпис =
 * перегляд застарів і `apply` без нового перегляду відхилить сервер (`regPlanChanged`).
 */
export function previewSignature(draft: ImportDraft, missing: readonly MissingReference[]): string {
  const file = draft.file === null ? '' : `${draft.file.name}:${String(draft.file.size)}:${String(draft.file.lastModified)}`;

  return JSON.stringify([
    file,
    draft.profileCode,
    draft.spec,
    normalizeCode(draft.region),
    normalizeArea(draft.area),
    toRequestResolutions(missing, draft.resolutions),
  ]);
}

export function errorIssues(issues: readonly ImportIssue[]): readonly ImportIssue[] {
  return issues.filter((issue) => issue.severity === 'Error');
}

export function warningIssues(issues: readonly ImportIssue[]): readonly ImportIssue[] {
  return issues.filter((issue) => issue.severity === 'Warning');
}

/** Помилки першими, далі за рядком файлу: людина спершу бачить те, що блокує. */
export function sortIssues(issues: readonly ImportIssue[]): readonly ImportIssue[] {
  const rank = (issue: ImportIssue): number => (issue.severity === 'Error' ? 0 : 1);

  return [...issues].sort((a, b) => rank(a) - rank(b) || (a.row ?? 0) - (b.row ?? 0));
}

/* ───────────────────────── редактор відповідності (RegistryImportSpec) ───────────────────────── */

/** Варіанти заголовка розділяються `|`: так їх бачить і пише людина (макет). */
export function patternsToText(patterns: readonly string[] | undefined): string {
  return (patterns ?? []).join(' | ');
}

export function textToPatterns(text: string): string[] {
  return text
    .split('|')
    .map((part) => part.trim())
    .filter((part) => part !== '');
}

export function specFields(spec: ImportSpec): readonly ImportFieldSpec[] {
  return spec.fields ?? [];
}

/** Замінює поле за індексом; решта специфікації не змінюється. */
export function withField(spec: ImportSpec, index: number, patch: Partial<ImportFieldSpec>): ImportSpec {
  const fields = specFields(spec).map((field, at) => (at === index ? { ...field, ...patch } : field));

  return { ...spec, fields };
}

export function withoutField(spec: ImportSpec, index: number): ImportSpec {
  return { ...spec, fields: specFields(spec).filter((_, at) => at !== index) };
}

export function withNewField(spec: ImportSpec, name: string): ImportSpec {
  return { ...spec, fields: [...specFields(spec), { name, patterns: [], level: 'Child', parser: 'Text' }] };
}

/** Специфікацію можна зберегти/аналізувати: у кожного поля є ім'я, імена унікальні. */
export function specProblem(spec: ImportSpec): 'nameRequired' | 'nameDuplicate' | null {
  const seen = new Set<string>();

  for (const field of specFields(spec)) {
    const name = (field.name ?? '').trim();
    if (name === '') return 'nameRequired';
    if (seen.has(name.toLocaleLowerCase())) return 'nameDuplicate';
    seen.add(name.toLocaleLowerCase());
  }

  return null;
}

/** Код профілю для «Save as new»: латиниця, цифри, `_`; велика літера. */
export function profileCodeProblem(code: string): boolean {
  return !/^[A-Z][A-Z0-9_]{1,63}$/.test(code);
}
