import type { PermitPreviewParams } from '../api/bindingsTypes';

/** Ключі кешу запитів прив'язок (PM-11). Перегляд належить прив'язці версії, тож їх скидає один префікс. */
export const bindingsKey = (templateVersionId: number) => ['permit-bindings', templateVersionId] as const;

export const bindingPreviewKey = (templateVersionId: number, code: string, params: PermitPreviewParams) =>
  ['permit-bindings', templateVersionId, 'preview', code, params] as const;
