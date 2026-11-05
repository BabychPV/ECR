import { EcrApiError } from '@/api/client';

/** Ключ відмови «вузол під закритими предками» (`POST …/unretire` без `withAncestors`). */
export const ParentRetiredKey = 'err.ECR-REG-0409.parentRetired';

/** Закриті предки з відмови `parentRetired`: усі закриті й ті з них, що видалені (їх разом не повернути). */
export interface ParentRetired {
  readonly ancestorIds: readonly number[];
  readonly deletedAncestorIds: readonly number[];
}

function ids(value: unknown): number[] {
  return Array.isArray(value) ? value.filter((id): id is number => typeof id === 'number') : [];
}

/**
 * Відмова повернення через закритих предків, або `null`, якщо відмова інша.
 *
 * ⚠ Видалений предок блокує й `withAncestors=true` (сервер повертає лише закриті), тому «повернути разом із предками»
 * пропонується лише без видалених (`canRestoreWithAncestors`).
 */
export function parentRetiredOf(error: unknown): ParentRetired | null {
  if (!(error instanceof EcrApiError)) return null;
  const extensions = error.problem.extensions2 ?? {};
  if (extensions['messageKey'] !== ParentRetiredKey) return null;

  return { ancestorIds: ids(extensions['ancestorIds']), deletedAncestorIds: ids(extensions['deletedAncestorIds']) };
}

export function canRestoreWithAncestors(refusal: ParentRetired): boolean {
  return refusal.ancestorIds.length > 0 && refusal.deletedAncestorIds.length === 0;
}
