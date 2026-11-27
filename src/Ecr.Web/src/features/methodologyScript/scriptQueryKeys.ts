/**
 * Ключі кешу редактора скрипта.
 *
 * ⚠ Тут, а не в `api/queryKeys.ts`: той файл лежить у спільному вхідному чанку, і кожен новий ключ роздував би
 * бюджет КОЖНОГО маршруту (`D-132`); ключі редактора потрібні лише його сторінці. Префікс `methodologies` —
 * той самий, що в `queryKeys.methodologies`: скидання домену чіпає й їх.
 */
export const scriptKeys = {
  /** `GET /api/v1/methodologies/{methodologyId}/versions/{versionId}/script` — текст і структура скрипта версії. */
  script: (versionId: number) => ['methodologies', 'script', versionId] as const,

  /** `GET /api/v1/expressions/metadata?dialect=Methodology` для редактора скрипта (функції, поля шапки). */
  metadata: (versionId: number) => ['methodologies', 'scriptMetadata', versionId] as const,

  /** `GET /api/v1/methodologies/script/tables?codes=` — колонки таблиць для підказок (ключ — коди через кому). */
  tables: (codes: string) => ['methodologies', 'scriptTables', codes] as const,

  /** `GET /api/v1/methodologies/{methodologyId}/versions/{versionId}/substances`. */
  substances: (versionId: number) => ['methodologies', 'substances', versionId] as const,

  /** Документи для перегляду скрипта (`GET /api/v1/documents?templateCode&q`). */
  documents: (templateCode: string | null, q: string) =>
    ['methodologies', 'scriptDocuments', templateCode, q] as const,

  /** Підсумок документа в періоді (підпис джерела тесту скрипта). */
  document: (documentId: number, periodKey: number) =>
    ['methodologies', 'scriptDocument', documentId, periodKey] as const,

  /** Екземпляри таблиць документа в періоді для перегляду скрипта. */
  documentTables: (documentId: number, periodKey: number) =>
    ['methodologies', 'scriptDocumentTables', documentId, periodKey] as const,

  /** Результат перевірки тексту скрипта без збереження (`POST …/script/validate`). */
  validation: (versionId: number, text: string) =>
    ['methodologies', 'scriptValidation', versionId, text] as const,

  /** Покриття версії «виходи → колонки» (вкладка «Входи й виходи» редактора скрипта). */
  coverage: (methodologyId: number, versionId: number) =>
    ['methodologies', 'coverage', methodologyId, versionId] as const,
};
