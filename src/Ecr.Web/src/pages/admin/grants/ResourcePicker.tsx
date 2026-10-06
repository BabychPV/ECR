import type { JSX } from 'react';
import { Group, Select } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch, EcrApiError } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type {
  GrantableProject,
  RegistryDefDto,
  TemplatePage,
  TemplateStructureDto,
  TemplateVersionPage,
} from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import type { ResourceKind } from './grantLabels';

/**
 * Вибір ресурсу гранта за НАЗВОЮ (аудит U6) замість голого `NumberInput`.
 *
 * ⛔ Доти адміністратор вписував внутрішній id, і помилка на одну цифру
 * відкривала доступ до ЧУЖОГО ресурсу — мовчки: назва з'являлась лише після
 * збереження.
 *
 * ⚠ Аркуш/таблиця/колонка обираються каскадом «шаблон → версія → аркуш →
 * таблиця → колонка», а не «проєкт → аркуш»: `SheetDef`/`TableDef`/`ColumnDef`
 * належать ВЕРСІЇ ШАБЛОНУ (`ListResourceGrantsHandler`, Q-299), а шлях через
 * проєкт (`/projects/{id}/document-template`) вимагає `Document.Create` і
 * гранта `Write` на сам проєкт — адміністратор безпеки його зазвичай не має.
 *
 * ⚠ Ключі кешу — ті самі, що й на інших екранах, і з тією самою формою
 * відповіді (`queryKeys.templates.*`, `queryKeys.registries.list()`):
 * розбіжні ключі кешу для тих самих даних — відомий клас дефекту. Проєкти —
 * окремий довідник зі своїм ключем (див. нижче, D-207 п.2).
 */

/** Проміжні рівні каскаду — стан рядка чернетки, на сервер не йде. */
export interface PickPath {
  readonly templateId: number | null;
  readonly versionId: number | null;
  readonly sheetId: number | null;
  readonly tableId: number | null;
}

export const EmptyPath: PickPath = { templateId: null, versionId: null, sheetId: null, tableId: null };

interface PickResult {
  /** `0` — ресурс ще не обрано до кінця. */
  readonly resourceId: number;
  /** Код обраного ресурсу — та сама форма, що й `resourceName` від сервера. */
  readonly code: string | null;
  readonly path: PickPath;
}

interface ResourcePickerProps {
  readonly index: number;
  readonly kind: ResourceKind;
  readonly resourceId: number;
  readonly path: PickPath;
  readonly onChange: (next: PickResult) => void;
  /** Лише перегляд (симуляція, `L9-18`): поля показують вибір, але не змінюються. */
  readonly readOnly?: boolean;
}

/** Глибина каскаду всередині структури версії: аркуш 1, таблиця 2, колонка 3. */
function structureDepth(kind: ResourceKind): number {
  if (kind === 'Sheet') return 1;
  if (kind === 'Table') return 2;
  if (kind === 'Column') return 3;
  return 0;
}

function nameWithCode(name: string, code: string): string {
  return name.length > 0 && name !== code ? `${name} (${code})` : code;
}

function idOrNull(value: string | null): number | null {
  return value === null ? null : Number(value);
}

/**
 * ⛔ A1-05: 403 на каскаді шаблону — не «не вдалося завантажити». Список
 * шаблонів (`GET /templates`) вимагає `Template.View`, тобто роль
 * TemplateAdministrator; адміністратор безпеки без неї (напр. `bootstrap`)
 * бачив загальну помилку й не знав, чого бракує.
 */
function failureText(error: Error | null, forbiddenKey?: string): string | null {
  if (error === null) return null;
  if (forbiddenKey !== undefined && error instanceof EcrApiError && error.problem.status === 403) return t(forbiddenKey);

  return t('grants.pickerLoadFailed');
}

function selected(id: number): string | null {
  return id > 0 ? String(id) : null;
}

export function ResourcePicker({
  index,
  kind,
  resourceId,
  path,
  onChange,
  readOnly = false,
}: ResourcePickerProps): JSX.Element {
  const depth = structureDepth(kind);
  const inTemplate = depth > 0;
  const n = index + 1;

  /*
   * ⛔ D-207 п.2 (рішення людини 2026-09-29, варіант B): довідник проєктів для
   * гранта — `GET /security/projects` (код і назва ВСІХ проєктів, право
   * `Security.ManageRoles`), а не `GET /projects`. Той фільтрує за грантами й
   * вимагає `Document.View`, тож адміністратор безпеки без грантів бачив тут
   * порожній вибір і не міг видати грант на проєкт нікому. Панель грантів
   * існує лише під `Security.ManageRoles` (без нього сервер не віддає й самих
   * грантів), тому окремої гілки «без права» тут немає.
   *
   * ⚠ Ключ кешу — власний, не `['projects']`: інша форма відповіді (масив
   * `{id, code, nameL10n}`, а не сторінка `ProjectSummary`), і спільний ключ
   * для різних форм — відомий клас дефекту.
   */
  const projects = useQuery({
    queryKey: ['security', 'projects'],
    queryFn: () => apiFetch<GrantableProject[]>('/api/v1/security/projects'),
    enabled: kind === 'Project',
  });

  const registries = useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
    enabled: kind === 'Registry',
  });

  const templates = useQuery({
    queryKey: queryKeys.templates.list(),
    queryFn: () => apiFetch<TemplatePage>('/api/v1/templates?limit=100'),
    enabled: inTemplate,
  });

  const versions = useQuery({
    queryKey: queryKeys.templates.versionsOf(path.templateId ?? undefined),
    queryFn: () =>
      apiFetch<TemplateVersionPage>(`/api/v1/templates/${String(path.templateId)}/versions?limit=100`),
    enabled: inTemplate && path.templateId !== null,
  });

  const structure = useQuery({
    queryKey: queryKeys.templates.version(path.versionId ?? 0),
    queryFn: () =>
      apiFetch<TemplateStructureDto>(`/api/v1/template-versions/${String(path.versionId)}/structure`),
    enabled: inTemplate && path.versionId !== null,
  });

  // ⚠ `aria-label` стоїть на кожному полі явно, а не в спільних пропах: так
  // його бачить лінт ФВ-14.20.
  const aria = (label: string): string => `${label} ${n}`;
  const common = (label: string, failure: string | null) => ({
    size: 'xs' as const,
    miw: 150,
    searchable: !readOnly,
    clearable: !readOnly,
    readOnly,
    placeholder: label,
    nothingFoundMessage: t('grants.pickerNothingFound'),
    ...(failure !== null ? { error: failure } : {}),
  });

  if (kind === 'Project') {
    const items = projects.data ?? [];

    return (
      <Select
        {...common(t('grants.pickerProject'), failureText(projects.error))}
        aria-label={aria(t('grants.pickerProject'))}
        data={items.map((p) => ({ value: String(p.id), label: nameWithCode(localized(p.nameL10n), p.code) }))}
        value={selected(resourceId)}
        onChange={(value) => {
          const id = idOrNull(value);
          onChange({ resourceId: id ?? 0, code: items.find((p) => p.id === id)?.code ?? null, path });
        }}
      />
    );
  }

  if (kind === 'Registry') {
    const items = registries.data ?? [];

    return (
      <Select
        {...common(t('grants.pickerRegistry'), failureText(registries.error))}
        aria-label={aria(t('grants.pickerRegistry'))}
        data={items.map((r) => ({ value: String(r.id), label: nameWithCode(localized(r.nameL10n), r.code) }))}
        value={selected(resourceId)}
        onChange={(value) => {
          const id = idOrNull(value);
          onChange({ resourceId: id ?? 0, code: items.find((r) => r.id === id)?.code ?? null, path });
        }}
      />
    );
  }

  const sheets = structure.data?.sheets ?? [];
  const sheet = sheets.find((s) => s.id === path.sheetId);
  const table = sheet?.tables.find((tb) => tb.id === path.tableId);

  // Проміжний рівень лише зберігає шлях; кінцевий — ставить id і код ресурсу.
  const pick = (level: number, id: number | null, code: string | undefined, next: PickPath): void => {
    const final = level === depth;
    onChange({ resourceId: final ? (id ?? 0) : 0, code: final && id !== null ? (code ?? null) : null, path: next });
  };

  return (
    <Group gap="xs" wrap="wrap">
      <Select
        {...common(t('grants.pickerTemplate'), failureText(templates.error, 'grants.pickerForbidden'))}
        aria-label={aria(t('grants.pickerTemplate'))}
        data={(templates.data?.items ?? []).map((tp) => ({ value: String(tp.id), label: tp.code }))}
        value={path.templateId === null ? null : String(path.templateId)}
        onChange={(value) => pick(-1, null, undefined, { ...EmptyPath, templateId: idOrNull(value) })}
      />
      <Select
        {...common(t('grants.pickerVersion'), failureText(versions.error, 'grants.pickerForbidden'))}
        aria-label={aria(t('grants.pickerVersion'))}
        disabled={path.templateId === null}
        data={(versions.data?.items ?? []).map((v) => ({ value: String(v.id), label: v.version }))}
        value={path.versionId === null ? null : String(path.versionId)}
        onChange={(value) =>
          pick(0, null, undefined, { ...EmptyPath, templateId: path.templateId, versionId: idOrNull(value) })
        }
      />
      <Select
        {...common(t('grants.pickerSheet'), failureText(structure.error, 'grants.pickerForbidden'))}
        aria-label={aria(t('grants.pickerSheet'))}
        disabled={path.versionId === null}
        data={sheets.map((s) => ({ value: String(s.id), label: nameWithCode(localized(s.nameL10n), s.code) }))}
        value={depth === 1 ? selected(resourceId) : path.sheetId === null ? null : String(path.sheetId)}
        onChange={(value) => {
          const id = idOrNull(value);
          pick(1, id, sheets.find((s) => s.id === id)?.code, { ...path, sheetId: id, tableId: null });
        }}
      />
      {depth >= 2 && (
        <Select
          {...common(t('grants.pickerTable'), null)}
          aria-label={aria(t('grants.pickerTable'))}
          disabled={sheet === undefined}
          data={(sheet?.tables ?? []).map((tb) => ({
            value: String(tb.id),
            label: nameWithCode(localized(tb.nameL10n), tb.code),
          }))}
          value={depth === 2 ? selected(resourceId) : path.tableId === null ? null : String(path.tableId)}
          onChange={(value) => {
            const id = idOrNull(value);
            pick(2, id, sheet?.tables.find((tb) => tb.id === id)?.code, { ...path, tableId: id });
          }}
        />
      )}
      {depth >= 3 && (
        <Select
          {...common(t('grants.pickerColumn'), null)}
          aria-label={aria(t('grants.pickerColumn'))}
          disabled={table === undefined}
          data={(table?.columns ?? []).map((c) => ({
            value: String(c.id),
            label: nameWithCode(localized(c.headerL10n), c.code),
          }))}
          value={selected(resourceId)}
          onChange={(value) => {
            const id = idOrNull(value);
            pick(3, id, table?.columns.find((c) => c.id === id)?.code, path);
          }}
        />
      )}
    </Group>
  );
}
