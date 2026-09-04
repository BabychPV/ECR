import type { paths } from './schema';

/**
 * Шляхи OpenAPI — основа типізованого клієнта.
 * Заповнюються `npm run api:types` із живого бекенда; до першої генерації
 * `schema.d.ts` є заглушкою (Q-017).
 */
export type ApiPaths = paths;

/**
 * Помилка API у форматі EcrProblemDetails.
 * Клієнт розрізняє причини **за кодом**, а не за текстом: текст локалізований
 * і може змінюватися, код — ні.
 */
export interface EcrProblem {
  type?: string;
  title: string;
  status: number;
  detail?: string;
  errorCode: string;
  correlationId: string;
  extensions2?: Record<string, unknown>;
}

/** Виняток клієнта API. */
export class EcrApiError extends Error {
  constructor(readonly problem: EcrProblem) {
    super(problem.detail ?? problem.title);
    this.name = 'EcrApiError';
  }

  /** Чи це конфлікт паралельного редагування. */
  get isConflict(): boolean {
    return this.problem.errorCode === 'ECR-CELL-0409';
  }

  /** Перелік конфліктів, якщо вони є. */
  get conflicts(): unknown[] {
    return (this.problem.extensions2?.['conflicts'] as unknown[]) ?? [];
  }
}

/**
 * Базовий HTTP-клієнт.
 * TODO: реалізувати fetch-обгортку:
 *  - credentials: 'include' (автентифікація на cookie, не на токені);
 *  - заголовок X-Correlation-Id генерувати на клієнті і логувати — це єдиний
 *    спосіб звірити скаргу користувача з серверним логом;
 *  - на 401 — редирект на сторінку входу, БЕЗ спроби мовчазного повторного входу;
 *  - на не-2xx — розібрати EcrProblemDetails і кинути EcrApiError;
 *  - на 202 — повернути jobId і statusUrl для стеження за фоновою операцією.
 */
export async function apiFetch<T>(_path: string, _init?: RequestInit): Promise<T> {
  throw new Error('TODO: реалізувати обгортку fetch за описом вище');
}
