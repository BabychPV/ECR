/** Стан типу дозволу в DTO (`PermitTypeDto.status` / `PermitTypeListItemDto.status`). */
export const PermitTypeStatus = { Draft: 'Draft', Published: 'Published' } as const;

/**
 * Тип ніколи не публікувався (`status = Draft`). Саме так сервер вирішує, чи можна видалити тип
 * (`DeletePermitTypeHandler`: не `Draft` → `ECR-PRM-4094`).
 *
 * ⛔ Не `version === 0`: для ніколи не опублікованого типу сервер віддає `version = 1` (майбутня перша),
 * тож номер версії не відрізняє чернетку від опублікованого.
 */
export function isNeverPublished(type: { readonly status: string }): boolean {
  return type.status === PermitTypeStatus.Draft;
}

/** Тип має опубліковану версію (відкрита чернетка нової версії статусу не змінює: лишається `Published`). */
export function hasPublishedVersion(type: { readonly status: string }): boolean {
  return type.status === PermitTypeStatus.Published;
}
