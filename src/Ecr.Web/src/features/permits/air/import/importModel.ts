import { EcrApiError } from '@/api/client';
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

export type ImportStep = 'scope' | 'file' | 'mapping' | 'preview' | 'apply';
export const ImportSteps: readonly ImportStep[] = ['scope', 'file', 'mapping', 'preview', 'apply'];

/** Регіон без ділянок: код `-` (контракт). */
export const NoArea = '-';

export type ResolutionChoice = 'unset' | 'skip' | 'create';

export interface Resolution {
  readonly choice: ResolutionChoice;
  /** Id вузла дерева (рядок); обов'язковий при `create`. */
  readonly createUnder: string;
}

export const UnsetResolution: Resolution = { choice: 'unset', createUnder: '' };

export interface ImportDraft {
  readonly file: File | null;
  /** Значення пікера REGION (`scopeModel`): Registry — id запису, List — текст вибору, Text — введений текст. */
  readonly region: string;
  /** Значення пікера AREA, так само; порожньо — без ділянки. */
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
 * Тіло `request`. Незбережена специфікація (`spec`) перемагає профіль. Регіон/ділянка сюди не входять: їх несуть
 * окремі поля форми (`scopeFormFields`, AIR-ON-AIR). `preview` — відбиток і рішення для `apply`; для `analyse` їх не передають.
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
    ...(resolutions.length > 0 ? { resolutions } : {}),
    ...(extra.ownerKeys !== undefined && extra.ownerKeys.length > 0 ? { confirmedOtherOwners: extra.ownerKeys } : {}),
    ...(extra.fingerprint === undefined ? {} : { expectedFingerprint: extra.fingerprint }),
  };
}

/**
 * Why a request with the workbook never got an answer (P1 UAT 07.11): `fileUnreadable` — the browser can no longer read the chosen
 * file (it was saved in Excel, moved or deleted after it was chosen: Chrome refuses the upload, `fetch` rejects with a
 * `TypeError` and no HTTP status); `network` — the file is readable, the server was not reached. `null` — not such a failure (an
 * answer of the server, shown by `ErrorAlert` with its own text).
 *
 * ⚠ Without this the wizard showed the generic «The request failed» for every retry and the user had no way forward.
 */
export type SendFailure = 'fileUnreadable' | 'network';

export async function sendFailureOf(error: unknown, file: File | null): Promise<SendFailure | null> {
  if (error instanceof EcrApiError || !(error instanceof TypeError)) return null;
  if (file === null) return 'network';

  return (await readable(file)) ? 'network' : 'fileUnreadable';
}

/** Reads the first byte of the file: a file changed or removed on disk after it was chosen fails (`NotReadableError`). */
function readable(file: File): Promise<boolean> {
  return new Promise((resolve) => {
    try {
      const reader = new FileReader();
      reader.onload = () => resolve(true);
      reader.onerror = () => resolve(false);
      reader.readAsArrayBuffer(file.slice(0, 1));
    } catch {
      resolve(false);
    }
  });
}

/** Крок «Файл» можна завершити: є файл `.xlsx` (область перевіряє крок «Регіон і ділянка», `scopeProblem`). */
export function fileStepProblem(draft: ImportDraft): 'file' | 'extension' | null {
  if (draft.file === null) return 'file';
  if (!/\.xlsx$/i.test(draft.file.name)) return 'extension';

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

/** Catalog key of «the referenced code is not in the target registry» (`RegistryImportKeys.ReferenceMissing`). */
export const ReferenceMissingKey = 'err.ECR-IMP-0422.regReferenceMissing';

/** Reference kind of the NAE tree (`INaeResolver.NaeKind`). */
export const NaeReferenceKind = 'nae';

/**
 * A finding «this NAE code is not in the NAE tree» (pass 2, 07.11): the server reports it as an error (the apply refuses while
 * it is undecided), but it is NOT a defect of the file — the preview resolves it with «create under a node» / «skip», so it is
 * shown in the missing-NAE warning group, not among the file errors. An unknown substance stays a file error.
 */
export function isMissingNaeFinding(issue: ImportIssue): boolean {
  return issue.severity === 'Error' && issue.messageKey === ReferenceMissingKey && issue.args?.['kind'] === NaeReferenceKind;
}

/** Errors the file itself has (the user must fix the file): every error except the missing-NAE findings. */
export function fileErrorIssues(issues: readonly ImportIssue[]): readonly ImportIssue[] {
  return issues.filter((issue) => issue.severity === 'Error' && !isMissingNaeFinding(issue));
}

/**
 * Чи можна застосувати: сервер дозволяє (`canApply`: немає помилок), усі відсутні НАЕ розв'язані, заміна чужих
 * дозволів підтверджена, а перегляд свіжий (параметри не змінились після нього).
 *
 * ⚠ Pass 2 (07.11): «the file has errors» only for errors of the FILE. Undecided missing NAE are «unresolved»; decided ones
 * make the preview stale until it is run again. If the server still refuses (`canApply` false) with nothing else to explain it,
 * the reason is «errors» again — Apply is never enabled against the server's verdict.
 */
export function applyGate(preview: AirPreview, draft: ImportDraft, stale: boolean): ApplyGate {
  const reasons: ('errors' | 'unresolved' | 'confirm' | 'stale')[] = [];
  const body = preview.preview;
  const open = unresolved(body.missing, draft.resolutions).length;

  if (fileErrorIssues(body.issues).length > 0 || (!body.canApply && open === 0 && !stale)) reasons.push('errors');
  if (open > 0) reasons.push('unresolved');
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

/**
 * The first field that points at nothing — no column letter and no header text (a field just added in the editor); `null` —
 * every field has one. Such a field may be READ (the server reports it as «column not found»), but a profile cannot be saved
 * with it (`422 regSpecInvalid`): the editor highlights it and blocks «Save as profile…».
 */
export function fieldWithoutColumn(spec: ImportSpec): string | null {
  const field = specFields(spec).find(lacksColumn);

  return field === undefined ? null : (field.name ?? '');
}

/** A field has neither a column letter nor header text. */
export function lacksColumn(field: ImportFieldSpec): boolean {
  return (field.column ?? '').trim() === '' && (field.patterns ?? []).length === 0;
}

/** A file column the editor offers for a field: letter and the header text found there. */
export interface FileColumnOption {
  readonly value: string;
  readonly label: string;
}

/**
 * Columns of the analysed file (mapped and unmapped) for the field editor's column picker, in file order: `B — Цех`. A letter
 * the specification already names but the header report does not list (a column past the header) is kept, so the picker never
 * hides the current value.
 */
export function fileColumnOptions(
  columns: readonly { readonly column: number; readonly columnLetter: string; readonly headerText: string }[],
  current: readonly (string | null | undefined)[] = [],
): FileColumnOption[] {
  const byLetter = new Map<string, { order: number; label: string }>();
  for (const column of columns) {
    if (byLetter.has(column.columnLetter)) continue;
    const text = column.headerText.replace(/\s+/g, ' ').trim();
    const shown = text.length > 60 ? `${text.slice(0, 60)}…` : text;
    byLetter.set(column.columnLetter, { order: column.column, label: shown === '' ? column.columnLetter : `${column.columnLetter} — ${shown}` });
  }
  for (const letter of current) {
    const value = (letter ?? '').trim().toUpperCase();
    if (value !== '' && !byLetter.has(value)) byLetter.set(value, { order: Number.MAX_SAFE_INTEGER, label: value });
  }

  return [...byLetter.entries()]
    .sort((a, b) => a[1].order - b[1].order || a[0].localeCompare(b[0]))
    .map(([value, { label }]) => ({ value, label }));
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

/** `409 ECR-REG-0409` keys of «Save as profile…» (`AirLimitsKeys` on the server). */
export const ProfileCodeTakenKey = 'err.ECR-REG-0409.profileCodeTaken';
export const ProfileBuiltInKey = 'err.ECR-REG-0409.profileBuiltIn';

/**
 * Why «Save as profile…» was refused for the code (pass 2, 07.11): `taken` — a profile with the code exists (the form offers
 * «Overwrite» or «Choose another code»); `builtIn` — the shipped profile, never overwritten (only «Choose another code»).
 * `null` — another refusal, shown as is.
 */
export function profileSaveConflict(error: unknown): 'taken' | 'builtIn' | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== 'ECR-REG-0409') return null;
  const key = error.problem.extensions2?.['messageKey'];
  if (key === ProfileCodeTakenKey) return 'taken';
  if (key === ProfileBuiltInKey) return 'builtIn';

  return null;
}

/** A refused «Save as profile…» the form answers with «Overwrite» / «Choose another code». */
export interface ProfileSaveConflict {
  readonly code: string;
  readonly name: string;
  readonly kind: 'taken' | 'builtIn';
}

/** Код профілю для «Save as new»: латиниця, цифри, `_`; велика літера. */
export function profileCodeProblem(code: string): boolean {
  return !/^[A-Z][A-Z0-9_]{1,63}$/.test(code);
}

/**
 * Code suggested from the profile name (the user may still change it): Latin letters and digits upper case, the rest `_`;
 * a name without Latin letters (Cyrillic) gives `''` — the user enters the code.
 */
export function profileCodeFrom(name: string): string {
  const code = name
    .toLocaleUpperCase('en-US')
    .replace(/[^A-Z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .slice(0, 62);
  if (code === '') return '';

  return /^[A-Z]/.test(code) ? code : `P_${code}`;
}
