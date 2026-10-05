import type { JSX } from 'react';
import { Badge, Group, MultiSelect, Text, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import type { GrantableProject, GrantableSheet, RoleScopeDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';

/**
 * Область дії призначення ролі за проєктами (ФВ-6.14) — спільне для форми
 * ролей користувача й групових призначень.
 *
 * ⚠ Порожній перелік тут — «усі проєкти» (на сервер області тоді не шлемо).
 * Сервер порожньої області не приймає (`422`): роль, що не діє ніде, —
 * не те, що адміністратор мав на увазі, лишивши поле порожнім.
 */

/** Довідник проєктів для вибору області — той самий, що й у гранті (D-207 п.2). */
export function useGrantableProjects(enabled: boolean): {
  readonly projects: readonly GrantableProject[];
  readonly failed: boolean;
} {
  // ⚠ Ключ і форма відповіді — ті самі, що в `ResourcePicker`: розбіжні ключі
  // для тих самих даних — відомий клас дефекту.
  const query = useQuery({
    queryKey: ['security', 'projects'],
    queryFn: () => apiFetch<GrantableProject[]>('/api/v1/security/projects'),
    enabled,
  });

  const projects = Array.isArray(query.data)
    ? query.data.filter(
        (p): p is GrantableProject =>
          typeof p === 'object' && p !== null && typeof p.id === 'number' && typeof p.code === 'string',
      )
    : [];

  return { projects, failed: query.isError };
}

function projectLabel(project: GrantableProject): string {
  const name = localized(project.nameL10n);
  return name.length > 0 && name !== project.code ? `${name} (${project.code})` : project.code;
}

/**
 * Варіанти мультивибору проєктів.
 *
 * ⚠ Проєкт області, якого немає в довіднику (довідник не завантажився або
 * права на нього немає), лишається обраним під своїм номером: зникнути з поля
 * він не має права — збереження тоді мовчки звузило б область.
 */
function projectOptions(
  projects: readonly GrantableProject[],
  selectedIds: readonly number[],
): { value: string; label: string }[] {
  const known = projects.map((p) => ({ value: String(p.id), label: projectLabel(p) }));
  const missing = selectedIds
    .filter((id) => !projects.some((p) => p.id === id))
    .map((id) => ({ value: String(id), label: `#${String(id)}` }));

  return [...known, ...missing];
}

/**
 * Область однієї ролі у формі (D-214): проєкти, коди аркушів і межі періодів
 * ТЕКСТОМ, як їх набрано (`2026-01`), — щоб незавершене введення не губилося.
 * Порожнє — «усі».
 */
export interface ScopeEntry {
  readonly projects: readonly number[];
  readonly sheets: readonly string[];
  readonly from: string;
  readonly to: string;
}

export const EmptyScope: ScopeEntry = { projects: [], sheets: [], from: '', to: '' };

/** Ключ періоду (`202601`) у вигляді для людини (`2026-01`). */
export function formatPeriod(key: number | null | undefined): string {
  if (key === null || key === undefined) return '';

  return `${String(Math.floor(key / 100))}-${String(key % 100).padStart(2, '0')}`;
}

/**
 * Межа періоду з тексту: `2026-01`, `2026/1` чи `202601` → `202601`;
 * порожньо — `null` (межа відкрита); не період — `undefined`.
 */
export function parsePeriod(text: string): number | null | undefined {
  const trimmed = text.trim();
  if (trimmed.length === 0) return null;

  const match = /^(\d{4})\s*[-./]?\s*(\d{1,2})$/.exec(trimmed);
  if (match === null) return undefined;

  const year = Number(match[1]);
  const sequence = Number(match[2]);

  return year >= 1900 && sequence >= 1 && sequence <= 99 ? year * 100 + sequence : undefined;
}

/**
 * Чи можна надіслати область: межі — періоди, «з» не пізніше «по».
 *
 * ⛔ L9-11: без проєктів область не звужується зовсім (`scopeToDto` → `null`), а поля періодів
 * вимкнені — тож лишений у них текст не валідується. Доти `2026-1x` у вимкненому полі після зняття
 * всіх проєктів блокував «Save»/«Assign», і виправити поле було неможливо.
 */
export function scopeValid(entry: ScopeEntry): boolean {
  if (entry.projects.length === 0) return true;

  const from = parsePeriod(entry.from);
  const to = parsePeriod(entry.to);

  return from !== undefined && to !== undefined && (from === null || to === null || from <= to);
}

/** Область із відповіді сервера у форму; `null` — роль діє скрізь. */
export function scopeFromDto(dto: RoleScopeDto | null | undefined): ScopeEntry {
  if (dto === null || dto === undefined) return EmptyScope;

  return {
    projects: dto.projects,
    sheets: dto.sheets ?? [],
    from: formatPeriod(dto.periods?.from),
    to: formatPeriod(dto.periods?.to),
  };
}

/**
 * Область для сервера; `null` — без області (порожні проєкти — усі проєкти).
 *
 * ⚠ Незадані аркуші й періоди не шлються зовсім: область лише з проєктами —
 * дослівно та сама, що до D-214. Аркуші й періоди без проєктів сенсу не мають —
 * без проєктів область не шлеться ніяк.
 */
export function scopeToDto(entry: ScopeEntry): RoleScopeDto | null {
  if (entry.projects.length === 0) return null;

  const from = parsePeriod(entry.from) ?? null;
  const to = parsePeriod(entry.to) ?? null;

  return {
    projects: [...entry.projects].sort((a, b) => a - b),
    ...(entry.sheets.length > 0 && { sheets: [...entry.sheets].sort() }),
    ...((from !== null || to !== null) && { periods: { from, to } }),
  };
}

/** Аркуші чинних версій шаблонів проєктів — довідник області за аркушами (D-214). */
export function useProjectSheets(enabled: boolean): { readonly sheets: readonly GrantableSheet[] } {
  const query = useQuery({
    queryKey: ['security', 'project-sheets'],
    queryFn: () => apiFetch<GrantableSheet[]>('/api/v1/security/project-sheets'),
    enabled,
  });

  const sheets = Array.isArray(query.data)
    ? query.data.filter(
        (s): s is GrantableSheet =>
          typeof s === 'object' && s !== null && typeof s.projectId === 'number' && typeof s.code === 'string',
      )
    : [];

  return { sheets };
}

/**
 * Варіанти аркушів: коди аркушів ОБРАНИХ проєктів (однаковий код у кількох
 * проєктах — один варіант). Обраний код, якого в довіднику немає, лишається
 * під своїм кодом — з тієї ж причини, що й проєкт у `projectOptions`.
 */
function sheetOptions(
  sheets: readonly GrantableSheet[],
  projectIds: readonly number[],
  selected: readonly string[],
): { value: string; label: string }[] {
  const result = new Map<string, string>();

  for (const sheet of sheets) {
    if (!projectIds.includes(sheet.projectId) || result.has(sheet.code)) continue;

    const name = localized(sheet.nameL10n);
    result.set(sheet.code, name.length > 0 && name !== sheet.code ? `${name} (${sheet.code})` : sheet.code);
  }

  for (const code of selected) {
    if (!result.has(code)) result.set(code, code);
  }

  return [...result].map(([value, label]) => ({ value, label }));
}

/**
 * Три прості поля області однієї ролі: «Проєкти», «Аркуші», «Періоди з … по …».
 * Порожнє поле — «усі»; аркуші й періоди звужують роль усередині обраних
 * проєктів, тож без проєктів вони вимкнені.
 */
export function ScopeFields({
  value,
  onChange,
  projects,
  sheets,
  suffix,
  hint,
  error,
}: {
  value: ScopeEntry;
  onChange: (next: ScopeEntry) => void;
  projects: readonly GrantableProject[];
  sheets: readonly GrantableSheet[];
  /** Додаток до підпису поля (код ролі), коли ролей у формі кілька. */
  suffix?: string | undefined;
  /** Підказка під полем проєктів. */
  hint?: string | undefined;
  error?: string | undefined;
}): JSX.Element {
  // ⚠ Ключ — літералом у кожному виклику `t` (сторож `EndpointCoverageTests`).
  const label = (text: string): string => (suffix === undefined ? text : `${text} · ${suffix}`);
  const narrowing = value.projects.length > 0;
  const from = parsePeriod(value.from);
  const to = parsePeriod(value.to);
  const reversed = typeof from === 'number' && typeof to === 'number' && from > to;

  return (
    <Group align="flex-start" gap="xs" wrap="wrap">
      <MultiSelect
        label={label(t('security.scopeProjects'))}
        placeholder={narrowing ? undefined : t('security.scopeAllProjects')}
        description={hint}
        data={projectOptions(projects, value.projects)}
        value={value.projects.map(String)}
        onChange={(values) => onChange({ ...value, projects: values.map(Number) })}
        searchable
        clearable
        miw={200}
        error={error}
      />
      <MultiSelect
        label={label(t('security.scopeSheets'))}
        placeholder={value.sheets.length === 0 ? t('security.scopeAllSheets') : undefined}
        description={narrowing ? undefined : t('security.scopeNarrowingNeedsProjects')}
        data={sheetOptions(sheets, value.projects, value.sheets)}
        value={[...value.sheets]}
        onChange={(values) => onChange({ ...value, sheets: values })}
        disabled={!narrowing}
        searchable
        clearable
        miw={180}
      />
      <TextInput
        label={label(t('security.scopePeriodFrom'))}
        description={t('security.scopePeriodHint')}
        placeholder={t('security.scopeAllPeriods')}
        value={value.from}
        onChange={(event) => onChange({ ...value, from: event.currentTarget.value })}
        disabled={!narrowing}
        // ⚠ L9-11: вимкнене поле не валідується (див. `scopeValid`) — і помилки біля нього немає.
        error={narrowing && from === undefined ? t('security.scopePeriodInvalid') : undefined}
        miw={140}
      />
      <TextInput
        label={label(t('security.scopePeriodTo'))}
        description={t('security.scopePeriodHint')}
        placeholder={t('security.scopeAllPeriods')}
        value={value.to}
        onChange={(event) => onChange({ ...value, to: event.currentTarget.value })}
        disabled={!narrowing}
        error={
          !narrowing
            ? undefined
            : to === undefined
              ? t('security.scopePeriodInvalid')
              : reversed
                ? t('security.scopePeriodOrder')
                : undefined
        }
        miw={140}
      />
    </Group>
  );
}

/** Область у рядку таблиці: назви проєктів або «усі проєкти»; аркуші й періоди — коли звужено. */
export function ScopeSummary({
  projectIds,
  projects,
  sheets,
  periods,
}: {
  projectIds: readonly number[] | null | undefined;
  projects: readonly GrantableProject[];
  sheets?: readonly string[] | null | undefined;
  periods?: RoleScopeDto['periods'] | undefined;
}): JSX.Element {
  if (projectIds === null || projectIds === undefined) {
    return (
      <Badge variant="light" color="gray" data-scope="all">
        {t('security.scopeAllProjects')}
      </Badge>
    );
  }

  // ⛔ Порожній перелік від сервера — область не розібралася, роль не діє
  // НІДЕ. «Усі проєкти» тут було б неправдою в бік ширших прав.
  if (projectIds.length === 0) {
    return (
      <Text size="sm" c="statusWarning" data-scope="nowhere">
        {t('security.scopeNowhere')}
      </Text>
    );
  }

  const hasSheets = sheets !== null && sheets !== undefined && sheets.length > 0;
  const hasPeriods =
    periods !== null && periods !== undefined && (periods.from != null || periods.to != null);

  return (
    <>
      <Text size="sm" data-scope="projects">
        {projectOptions(projects, projectIds)
          .filter((option) => projectIds.includes(Number(option.value)))
          .map((option) => option.label)
          .join(', ')}
      </Text>
      {hasSheets && (
        <Text size="xs" c="dimmed" data-scope="sheets">
          {t('security.scopeSheetsSummary', { sheets: sheets.join(', ') })}
        </Text>
      )}
      {hasPeriods && (
        <Text size="xs" c="dimmed" data-scope="periods">
          {t('security.scopePeriodsSummary', {
            from: formatPeriod(periods.from) || '…',
            to: formatPeriod(periods.to) || '…',
          })}
        </Text>
      )}
    </>
  );
}

/**
 * ⛔ Роль, звужена аркушами чи періодами, відкриває документи своїх проєктів,
 * але прав на весь проєкт (розрахунки, звіти, створення й видалення
 * документа, керування) не дає ніде — сервер їх із такої ролі не бере.
 */
export function ScopeNarrowedWarning(): JSX.Element {
  return (
    <Text size="sm" c="statusWarning" mt="xs" data-scope-narrowed-warning>
      {t('security.scopeNarrowedWarning')}
    </Text>
  );
}

/**
 * ⛔ Роль з областю не дає ГЛОБАЛЬНИХ прав (`Security.*`, `Template.*`,
 * `Registry.*` тощо) — вони не прив'язані до проєкту, і сервер їх із такої
 * ролі не бере. Без попередження адміністратор обмежував би роль проєктом і
 * дивувався, куди зникло керування шаблонами.
 */
export function ScopeGlobalWarning(): JSX.Element {
  return (
    <Text size="sm" c="statusWarning" mt="xs" data-scope-warning>
      {t('security.scopeGlobalWarning')}
    </Text>
  );
}

/**
 * Відмова сервера про область: `403 noProjectManageGrant` (немає `Manage` на
 * проєкт) або `422` з кодом ролі. `null` — відмова про інше.
 */
export function scopeProblem(error: unknown): { roleCode: string | null; projectId: number | null } | null {
  if (!(error instanceof EcrApiError)) return null;

  const ext = error.problem.extensions2 ?? {};
  const projectId = ext['projectId'] === undefined ? null : Number(ext['projectId']);
  const roleCode = typeof ext['code'] === 'string' ? ext['code'] : null;

  if (error.problem.status === 403 && ext['messageKey'] === 'err.ECR-AUTH-0403.noProjectManageGrant') {
    return { roleCode, projectId };
  }

  if (error.problem.status === 422 && roleCode !== null) {
    return { roleCode, projectId };
  }

  return null;
}
