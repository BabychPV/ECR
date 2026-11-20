import { readFileSync } from 'node:fs';
import path from 'node:path';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { DocumentPendingHost } from '@/features/grid/DocumentPendingHost';
import { currentDocumentId, resetPending } from '@/features/grid/pendingStore';

/**
 * ✎ D-132 (14.12 прогін інтеграційної гілки): `autosave.ts` важив ≈4 КБ gzip у чанку `DocumentPage`, і маршрут
 * (254.0 КБ) вийшов за межу 250. Хук незбережених правок переїхав у лінивий `DocumentPendingHost`; сторінка монтує
 * його НА СВОЄМУ РІВНІ (поза `AsyncBoundary`/`recovery`) разом із сіткою.
 *
 * Мутації (локально): (1) повернути статичний `import { useDocumentPending } from '@/features/grid/autosave'` —
 * червоне (перший тест); (2) прибрати `<PendingHost …/>` з розмітки — червоне (другий тест); (3) хост не викликає
 * `useDocumentPending` — червоне (третій тест).
 */
const Page = path.resolve(process.cwd(), 'src', 'pages', 'DocumentPage.tsx');

function codeOf(text: string): string {
  return text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`\\])\/\/.*$/gm, '$1');
}

afterEach(() => {
  cleanup();
  resetPending();
});

describe('DocumentPage: незбережені правки — лінивим хостом', () => {
  const code = codeOf(readFileSync(Page, 'utf8'));

  it('немає статичного імпорту autosave; хост вантажиться через import()', () => {
    expect(code).not.toMatch(/^import\s[^;]*from\s+'@\/features\/grid\/autosave'/m);
    expect(code).toMatch(/import\('@\/features\/grid\/DocumentPendingHost'\)/);
  });

  it('сторінка монтує хост із ідентифікатором документа', () => {
    expect(code).toMatch(/<PendingHost\s+documentId=\{documentId\}/);
  });

  it('хост відкриває сховище правок на документ і скидає його при розмонтуванні', () => {
    const queryClient = new QueryClient();
    const view = render(
      <QueryClientProvider client={queryClient}>
        <DocumentPendingHost documentId={4242} ownerUserId={1} />
      </QueryClientProvider>,
    );

    expect(currentDocumentId()).toBe(4242);

    view.unmount();
    // Розмонтування викликає `resetPending()`: сховище більше не належить документові.
    expect(currentDocumentId()).not.toBe(4242);
  });
});
