import { useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { UserPage } from '@/api/types';
import { t } from '@/shared/i18n';
import { isHumanOrigin } from '@/features/audit/api';
import { can, useSession } from '@/shared/session/useSession';

/** Рядок журналу з автором — спільна форма журналу комірок і структури. */
interface AuthoredRow {
  readonly changedByUserId: number;
  readonly changedByDisplayName?: string | null | undefined;
}

/** Ім'я автора рядка; зниклий запис — чесне «користувач #id» (`R-18`). */
export function authorName(row: AuthoredRow): string {
  return row.changedByDisplayName ?? t('audit.userGone', { id: row.changedByUserId });
}

/**
 * Автор зміни КОМІРКИ: ім'я людини для правки й імпорту, «Система» — для перерахунку, інтеграції,
 * міграції (`isHumanOrigin`, AN-115). Того, хто запустив системну зміну, видно в підказці
 * (`cellAuthorHint`) — журнал не губить, чия це була дія.
 */
export function cellAuthorName(row: AuthoredRow & { readonly origin: string }): string {
  return isHumanOrigin(row.origin) ? authorName(row) : t('audit.systemAuthor');
}

/** Підказка до автора зміни комірки: номер, а для системної зміни — ще й хто її запустив. */
export function cellAuthorHint(row: AuthoredRow & { readonly origin: string }): string {
  const id = `#${String(row.changedByUserId)}`;
  return isHumanOrigin(row.origin) ? id : `${authorName(row)} (${id})`;
}

/**
 * Автори для фільтра журналу — вибором, а не номером (`R-18`).
 *
 * ⛔ Поле «Author» було `NumberInput`: щоб звузити журнал до людини, треба було
 * знати її внутрішній `UserId`. Перелік — з двох джерел: `GET /users` (лише з
 * `Security.ManageUsers` — саме це право його відкриває) і автори, яких
 * журнал уже показав (ім'я прийшло в самому рядку). Обраний автор лишається в
 * переліку завжди — інакше поле показувало б порожнечу при чинному фільтрі.
 */
export function useAuthorOptions(
  items: readonly AuthoredRow[] | undefined,
  selected: number | null,
): { value: string; label: string }[] {
  const session = useSession();
  const users = useQuery({
    queryKey: ['users', 'audit-authors'],
    queryFn: () => apiFetch<UserPage>('/api/v1/users?limit=200'),
    enabled: can(session.data, 'Security.ManageUsers'),
    staleTime: 5 * 60 * 1000,
  });

  const [seen, setSeen] = useState<ReadonlyMap<number, string>>(new Map());

  useEffect(() => {
    if (items === undefined) return;

    setSeen((previous) => {
      let next: Map<number, string> | null = null;

      for (const item of items) {
        const name = item.changedByDisplayName;
        if (name !== null && name !== undefined && previous.get(item.changedByUserId) !== name) {
          next ??= new Map(previous);
          next.set(item.changedByUserId, name);
        }
      }

      return next ?? previous;
    });
  }, [items]);

  return useMemo(() => {
    const names = new Map<number, string>(seen);

    for (const user of users.data?.items ?? []) {
      names.set(user.id, user.displayName.length > 0 ? user.displayName : user.userName);
    }

    if (selected !== null && !names.has(selected)) {
      names.set(selected, t('audit.userGone', { id: selected }));
    }

    return [...names.entries()]
      .sort((a, b) => a[1].localeCompare(b[1]))
      .map(([id, label]) => ({ value: String(id), label }));
  }, [seen, users.data, selected]);
}
