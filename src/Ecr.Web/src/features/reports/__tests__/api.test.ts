import { describe, expect, it } from 'vitest';

import { snapshotExportUrl } from '../api';

describe('features/reports/api', () => {
  it('адреса вивантаження зрізу несе ідентифікатор і розширення книги', () => {
    // ⛔ Розширення `.xlsx` — частина МАРШРУТУ на сервері
    // (`snapshots/{id:long}/export.xlsx`), а не косметика імені файлу: без
    // нього запит іде на адресу, якої немає, і браузер показує 404 замість
    // книги. Тому звіряється рядок цілком, а не «містить id».
    expect(snapshotExportUrl(77)).toBe('/api/v1/reports/snapshots/77/export.xlsx');
  });
});
