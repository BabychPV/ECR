import type { JSX } from 'react';
import { MultiSelect, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { RoleScopeDto, UserRoleAssignmentView } from '@/api/types';
import { t } from '@/shared/i18n';
import { ScopeGlobalWarning, ScopeSummary, projectOptions, useGrantableProjects } from './roleScope';

/**
 * Області дії ролей користувача у формі доступу (ФВ-6.14).
 *
 * ⛔ `PUT /users/{id}/roles` зі словником `scopes` — ПОВНА відповідь: роль без
 * запису в ньому діє в усіх проєктах. Слати його, не прочитавши збережених
 * областей, означало б мовчки знімати їх. Тому:
 * - області читаються з `GET /users/{id}/role-assignments`;
 * - не прочиталися (або збережена область зіпсована) — поля немає, і словник
 *   НЕ шлеться: сервер тоді зберігає області як є;
 * - словник шлеться лише коли область справді змінили. Незмінений словник
 *   дорівнює відсутньому, а зайва перевірка `Manage` на кожен проєкт
 *   області відмовила б адміністраторові, що міняв лише пошту.
 */

/** Код ролі → проєкти області; порожньо або немає запису — усі проєкти. */
export type ScopeDraft = Readonly<Record<string, readonly number[]>>;

export type ScopeState = 'pending' | 'ready' | 'unavailable' | 'unreadable';

/** Збережені області БЕЗСТРОКОВИХ призначень — тих, якими керує форма. */
export function useUserRoleScopes(userId: number | null): { baseline: ScopeDraft; state: ScopeState } {
  const query = useQuery({
    queryKey: ['user-role-assignments', userId],
    // ⚠ Шлях літералом: сторож `EndpointCoverageTests` шукає споживача за текстом.
    queryFn: () => apiFetch<UserRoleAssignmentView[]>(`/api/v1/users/${userId ?? 0}/role-assignments`),
    enabled: userId !== null,
  });

  if (query.isPending) return { baseline: {}, state: 'pending' };
  if (query.isError || !Array.isArray(query.data)) return { baseline: {}, state: 'unavailable' };

  const baseline: Record<string, readonly number[]> = {};
  let unreadable = false;

  for (const row of query.data) {
    if (typeof row !== 'object' || row === null || typeof row.roleCode !== 'string') continue;

    // Строкові підміни форма не редагує (`ListUserRolesAsync`) — їхні області теж.
    if (row.validFrom != null || row.validTo != null || row.scope == null) continue;

    // ⛔ Порожній перелік від сервера — зіпсована область, роль не діє ніде.
    // Показати її «усіма проєктами» і зберегти так — розширити права мовчки.
    if (row.scope.projects.length === 0) unreadable = true;

    baseline[row.roleCode] = row.scope.projects;
  }

  return { baseline, state: unreadable ? 'unreadable' : 'ready' };
}

function pick(selected: readonly string[], draft: ScopeDraft): Record<string, RoleScopeDto> {
  const result: Record<string, RoleScopeDto> = {};
  for (const code of [...selected].sort()) {
    const projects = draft[code] ?? [];
    if (projects.length > 0) result[code] = { projects: [...projects].sort((a, b) => a - b) };
  }

  return result;
}

/**
 * Словник `scopes` для `PUT …/roles`; `undefined` — поле не шлеться (області
 * зберігаються сервером як є).
 */
export function scopesToSend(
  selected: readonly string[],
  draft: ScopeDraft,
  baseline: ScopeDraft,
  state: ScopeState,
): Record<string, RoleScopeDto> | undefined {
  if (state !== 'ready') return undefined;

  const next = pick(selected, draft);

  return JSON.stringify(next) === JSON.stringify(pick(selected, baseline)) ? undefined : next;
}

export function RoleScopeFields({
  selected,
  draft,
  state,
  error,
  onChange,
}: {
  selected: readonly string[];
  draft: ScopeDraft;
  state: ScopeState;
  /** Відмова сервера, прив'язана до ролі: код → текст. */
  error: { readonly code: string; readonly text: string } | null;
  onChange: (code: string, projects: number[]) => void;
}): JSX.Element | null {
  const { projects } = useGrantableProjects(state === 'ready' && selected.length > 0);

  if (state === 'pending' || selected.length === 0) return null;

  if (state !== 'ready') {
    return (
      <Text size="sm" c="dimmed" mt="xs" data-scope-state={state}>
        {t(state === 'unreadable' ? 'security.scopeUnreadable' : 'security.scopeUnavailable')}
      </Text>
    );
  }

  const scoped = selected.some((code) => (draft[code] ?? []).length > 0);

  return (
    <Stack gap="xs" mt="sm">
      {selected.map((code) => {
        const ids = draft[code] ?? [];

        return (
          <Stack key={code} gap="xs">
            <MultiSelect
              label={`${t('security.scopeProjects')} · ${code}`}
              data={projectOptions(projects, ids)}
              value={ids.map(String)}
              onChange={(values) => onChange(code, values.map(Number))}
              searchable
              clearable
              error={error?.code === code ? error.text : undefined}
            />
            {ids.length === 0 && (
              <div>
                <ScopeSummary projectIds={null} projects={projects} />
              </div>
            )}
          </Stack>
        );
      })}
      {scoped && <ScopeGlobalWarning />}
    </Stack>
  );
}
