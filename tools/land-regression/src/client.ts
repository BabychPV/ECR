// Тонкий HTTP-клієнт ECR: cookie-сесія після /auth/login/local, JSON.

export type Fetch = typeof fetch;

export class EcrApiError extends Error {
  status: number;
  path: string;
  body: string;
  constructor(status: number, path: string, body: string) {
    super(`ECR ${status} ${path}: ${body.slice(0, 300)}`);
    this.status = status;
    this.path = path;
    this.body = body;
  }
}

export class EcrClient {
  private cookies = new Map<string, string>();
  private baseUrl: string;
  private fetchImpl: Fetch;
  constructor(baseUrl: string, fetchImpl: Fetch = fetch) {
    this.baseUrl = baseUrl;
    this.fetchImpl = fetchImpl;
  }

  private async call<T>(method: string, path: string, body?: unknown): Promise<T> {
    const headers: Record<string, string> = {
      Accept: 'application/json',
      // CSRF-сторож відхиляє cross-site; Origin збігається з хостом.
      Origin: new URL(this.baseUrl).origin,
    };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (this.cookies.size) {
      headers.Cookie = [...this.cookies].map(([k, v]) => `${k}=${v}`).join('; ');
    }
    const res = await this.fetchImpl(`${this.baseUrl}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    for (const line of res.headers.getSetCookie?.() ?? []) {
      const [pair] = line.split(';');
      const eq = pair!.indexOf('=');
      if (eq > 0) this.cookies.set(pair!.slice(0, eq).trim(), pair!.slice(eq + 1));
    }
    const text = await res.text();
    if (!res.ok) throw new EcrApiError(res.status, path, text);
    return (text ? JSON.parse(text) : undefined) as T;
  }

  login(userName: string, password: string) {
    return this.call<unknown>('POST', '/api/v1/auth/login/local', { userName, password });
  }

  createDocument(req: { projectId: number; sheetDefIds: number[]; templateVersionId?: number; name?: Record<string, string> }) {
    return this.call<{ documentId: number }>('POST', '/api/v1/documents', req);
  }

  tables(documentId: number, periodKey: number) {
    return this.call<Array<{ sheetDefId: number; tableCode: string; tableInstanceId: number }>>(
      'GET', `/api/v1/documents/${documentId}/tables?periodKey=${periodKey}`);
  }

  createRow(documentId: number, tableInstanceId: number, rowKey: string) {
    return this.call<{ rowKey: string }>('POST', `/api/v1/documents/${documentId}/rows`, { tableInstanceId, rowKey });
  }

  patchCells(documentId: number, req: {
    tableInstanceId: number; periodKey: number; origin: string;
    rows: Array<{ rowKey: string; baseVersion: string | null; cells: Array<{ columnCode: string; value: unknown }> }>;
  }) {
    return this.call<{ appliedCells: number }>('PATCH', `/api/v1/documents/${documentId}/cells`, req);
  }

  recalculate(documentId: number, periodKey: number, sheetDefId?: number) {
    return this.call<{ jobId: string }>('POST', `/api/v1/documents/${documentId}/recalculate`, { periodKey, sheetDefId });
  }

  job(jobId: string) {
    return this.call<{ jobId: string; state: string; error?: string | null; message?: string | null }>(
      'GET', `/api/v1/jobs/${encodeURIComponent(jobId)}`);
  }

  calculationResults(documentId: number, periodKey: number) {
    return this.call<Array<{ sourceRowKey: string | null; outputCode: string; value: number }>>(
      'GET', `/api/v1/documents/${documentId}/calculation-results?periodKey=${periodKey}`);
  }
}
