import type { MeDto } from '@/shared/session/useSession';
import { meetsGrant, type GrantLevelName } from '@/features/workflow/grantLevels';

/*
 * Перевірка гранта на проєкт — окремо від `BusinessKeyChangeAction.tsx` (D-132): адміністративні сторінки
 * (`PeriodsPage`, погодження перерахунку) беруть лише її, а модуль дії тягнув за собою весь `SheetActions`.
 * `BusinessKeyChangeAction.tsx` реекспортує функцію — наявні імпорти не змінюються.
 */

/**
 * Чи має користувач на проєкт грант щонайменше `required` — дзеркало
 * `profile.LevelFor(ResourceKind.Project, id) < required` на сервері.
 * Заборона виграє на будь-якому рівні (ФВ-6.6).
 */
export function hasProjectGrant(
  me: MeDto | undefined,
  projectId: number,
  required: GrantLevelName,
): boolean {
  if (me === undefined) return false;

  const key = `Project:${String(projectId)}`;
  if ((me.denies ?? []).includes(key)) return false;

  const level = (me.grants ?? {})[key] as GrantLevelName | undefined;
  return level !== undefined && meetsGrant(level, required);
}

