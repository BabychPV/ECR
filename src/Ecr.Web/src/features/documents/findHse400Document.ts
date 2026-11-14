import type { components } from '@/api/schema';
import { listDocuments } from '@/features/documents/api';

/** Код шаблону HSE400 (склад газу для розрахунків Land, `Build-Hse400Template`). */
export const Hse400TemplateCode = 'HSE400';

type DocumentSummary = components['schemas']['DocumentSummary'];

/** ✎ B109: розмір сторінки запиту — стеля сервера; документів HSE400 за рік зазвичай одиниці, тож це один запит. */
const PageSize = 500;

/**
 * ✎ B93 (c): який документ року відкрити, коли їх кілька, — явне правило, а не порядок переліку:
 * 1) не сам поточний документ; 2) спершу документ, що має період панелі (`periodStates` містить `periodKey` — перехід
 * відкриває саме цей період); 3) далі найменший Id (найстаріший документ року). Проєкт поточного документа перевіряється
 * першим ще до цього правила (див. `findHse400Document`), рік — фільтр сервера `year`.
 */
function pickDocument(
  items: readonly Pick<DocumentSummary, 'id' | 'periodStates'>[],
  currentDocumentId: number,
  periodKey: number | null,
): number | null {
  const candidates = items.filter((item) => item.id !== currentDocumentId);
  if (candidates.length === 0) return null;

  const withPeriod = periodKey === null
    ? []
    : candidates.filter((item) => (item.periodStates ?? []).some((state) => state.periodKey === periodKey));
  const pool = withPeriod.length > 0 ? withPeriod : candidates;

  return Math.min(...pool.map((item) => item.id));
}

/** Усі видимі документи HSE400 року — фільтр сервера `templateCode` (зазвичай одна сторінка, отже один запит). */
async function hse400DocumentsOf(year: number): Promise<DocumentSummary[]> {
  const items: DocumentSummary[] = [];
  let cursor: string | null = null;
  do {
    const page = await listDocuments({ periodKey: null, year, templateCode: Hse400TemplateCode, limit: PageSize, cursor });
    items.push(...page.items);
    cursor = page.nextCursor ?? null;
  } while (cursor !== null);

  return items;
}

/**
 * ✎ B78 F13 P3-2: документ HSE400 для «Відкрити HSE400» — за КОДОМ ШАБЛОНУ, а не першим збігом пошуку «HSE400».
 *
 * ✎ B109: ОДИН запит `GET /documents?year=&templateCode=HSE400` замість шкал проєктів порціями (B93 b): сервер бере
 * версію шаблону, ЧИННУ в році (остання з `fromPeriodKey` ≤ кінця року, D-511), і шаблон самого документа, у межах
 * видимості читача. Правило вибору незмінне (D-511): спершу проєкт поточного документа, далі решта; у межах кожного —
 * `pickDocument` (не поточний → з періодом панелі → найменший Id).
 *
 * @param periodKey Період панелі — перевага документу з цим періодом (`pickDocument`); `null` — без переваги.
 * @returns Id документа; `null` — такого документа немає (або він читачеві невидимий). Відмова API — виняток.
 */
export async function findHse400Document(
  currentProjectId: number | null,
  year: number,
  currentDocumentId: number,
  periodKey: number | null = null,
): Promise<number | null> {
  const documents = await hse400DocumentsOf(year);

  if (currentProjectId !== null) {
    const own = pickDocument(
      documents.filter((document) => document.projectId === currentProjectId), currentDocumentId, periodKey,
    );
    if (own !== null) return own;
  }

  return pickDocument(documents, currentDocumentId, periodKey);
}
