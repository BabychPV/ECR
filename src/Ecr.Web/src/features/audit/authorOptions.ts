import { useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { allUsersQuery } from '@/features/security/allUsers';
import { t } from '@/shared/i18n';
import { isHumanCellChange } from '@/features/audit/api';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import { can, useSession } from '@/shared/session/useSession';
import { disambiguateNames, pickedLabel, type FilterOption } from '@/features/audit/filterOptions';

/** Автор для фільтра «By user» (`GET /api/v1/audit/authors`, ✎ P-1124-4): номер і ім'я, без логіна й SID. */
type AuditAuthor = components['schemas']['AuditAuthorView'];

/** Рядок журналу з автором — спільна форма журналу комірок і структури. */
interface AuthoredRow {
  readonly changedByUserId: number;
  readonly changedByDisplayName?: string | null | undefined;
}

/**
 * Умовний Id системи в журналах — задачі (`PeriodStateJob.SystemUserId`, перерахунок, дозволи) пишуть `0`;
 * користувача з таким Id не буває.
 */
export const SystemUserId = 0;

/**
 * PSS-24: спроба входу під ІМЕНЕМ, якого в системі немає (`LoginFailed`), не має автора, а колонка автора в журналі
 * `NOT NULL` — сервер пише `-1` (`LoginAuditEventTypes.AnonymousActorId`). Ім'я, яке ввели, — у подробицях події.
 */
export const AnonymousUserId = -1;

/**
 * Ім'я автора рядка; зниклий запис — чесне «користувач #id» (`R-18`); системний запис (`0`) — «Система»
 * (F13-p213 i5: автозакриття показувало «User #0 (no longer exists)»).
 */
export function authorName(row: AuthoredRow): string {
  if (row.changedByUserId === SystemUserId) return t('audit.systemActor');
  if (row.changedByUserId === AnonymousUserId) return t('audit.anonymousActor');
  return row.changedByDisplayName ?? t('audit.userGone', { id: row.changedByUserId });
}

/**
 * Автор зміни КОМІРКИ: ім'я людини для правки й імпорту, «Система» — для перерахунку, інтеграції,
 * міграції (`isHumanOrigin`, AN-115). Того, хто запустив системну зміну, видно в підказці
 * (`cellAuthorHint`) — журнал не губить, чия це була дія.
 */
export function cellAuthorName(row: AuthoredRow & { readonly origin: string; readonly reason?: string | null | undefined }): string {
  return isHumanCellChange(row) ? authorName(row) : t('audit.systemAuthor');
}

/** Підказка до автора зміни комірки: номер, а для системної зміни — ще й хто її запустив. */
export function cellAuthorHint(row: AuthoredRow & { readonly origin: string; readonly reason?: string | null | undefined }): string {
  const id = `#${String(row.changedByUserId)}`;
  return isHumanCellChange(row) ? id : `${authorName(row)} (${id})`;
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
  picked: FilterOption | null = null,
): { value: string; label: string; disabled?: boolean }[] {
  const session = useSession();
  // ⛔ PSS-19c: усі користувачі (`allUsersQuery`), а не перші 200 мовчки — 201-й автор не мав імені у фільтрі.
  const users = useQuery({
    ...allUsersQuery,
    enabled: can(session.data, 'Security.ManageUsers'),
    staleTime: 5 * 60 * 1000,
  });
  /*
   * ✎ P-1124-4: автори за правом САМОГО журналу (`Security.ViewAudit`). `GET /users` вимагає `Security.ManageUsers`,
   * якого вбудовані ролі не мають, — і в аудитора «By user» стояв порожнім «Anyone» без жодного варіанта (живий екран
   * 24.11): у журналі безпеки більшість рядків — «Система» чи анонімна спроба входу, імені в рядку немає.
   */
  const authors = useQuery({
    queryKey: ['audit-authors'],
    queryFn: () => apiFetch<AuditAuthor[]>('/api/v1/audit/authors'),
    enabled: can(session.data, 'Security.ViewAudit'),
    staleTime: 5 * 60 * 1000,
  });

  const [seen, setSeen] = useState<ReadonlyMap<number, string>>(new Map());

  useEffect(() => {
    if (items === undefined) return;

    setSeen((previous) => {
      let next: Map<number, string> | null = null;

      for (const item of items) {
        // ✎ P-1124-4: «Система» (0) і анонімна спроба входу (-1) — теж варіанти фільтра: імені в рядку в них немає.
        const name =
          item.changedByUserId === SystemUserId || item.changedByUserId === AnonymousUserId
            ? authorName(item)
            : item.changedByDisplayName;
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

    for (const author of Array.isArray(authors.data) ? authors.data : []) {
      names.set(author.userId, authorName({ changedByUserId: author.userId, changedByDisplayName: author.displayName }));
    }

    for (const user of users.data?.items ?? []) {
      names.set(user.id, user.displayName.length > 0 ? user.displayName : user.userName);
    }

    if (selected !== null && !names.has(selected)) {
      // ✎ AC-11: щойно обраного автора не показуємо як «User #id», поки перелік/журнал перезавантажуються.
      names.set(selected, pickedLabel(picked, selected) ?? authorName({ changedByUserId: selected }));
    }

    // audit-filters-live D4: однакові імена (тимчасові акаунти вікон) розрізняються логіном, без нього — номером.
    const logins = new Map<number, string>((users.data?.items ?? []).map((user) => [user.id, user.userName]));

    return [
      ...disambiguateNames(
        [...names.entries()]
          .sort((a, b) => a[1].localeCompare(b[1]))
          .map(([id, label]) => ({ value: String(id), label })),
        logins,
      ),
      // PSS-19c / L4: користувачів дочитано лише до запобіжника — останнім неактивним пунктом сказано прямо.
      // ⚠ Те саме, що `truncatedOption` (`@/shared/ui/truncatedOption`), але рядком тут: `AuditPage` — біля межі
      // бюджету `D-132`, а окремий спільний чанк ради трьох рядків коштував би маршруту ~0,2 КБ (рецензія L10, M3).
      ...(users.data === undefined || users.data.nextCursor === null
        ? []
        : [{ value: '__list-truncated__', label: t('common.shownSoFar', { shown: users.data.items.length }), disabled: true }]),
    ];
  }, [seen, authors.data, users.data, selected, picked]);
}
