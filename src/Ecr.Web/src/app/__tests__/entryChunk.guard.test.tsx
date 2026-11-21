import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';

/**
 * Бюджет D-132: вхідний чанк (оболонка) — спільний для КОЖНОГО маршруту, тож те, що оболонці не потрібне для першої
 * відмальовки, туди не їде. Виміряно (main→гілка): кожен маршрут −4,4…−4,8 КБ gzip, TemplateVersionPage 250,0 → 245,2.
 *
 *  - `<Notifications />` (контейнер тостів із `react-transition-group`) — ледачим чанком через alias
 *    `@ecr/notifications-container` (`vite.config.ts`): пакет експортує лише `index`, який статично бере
 *    `notifications.show`, тож `import('@mantine/notifications')` лишив би контейнер у вхідному чанку;
 *  - бейдж меню бере `./listSummary`, а не весь `features/documents/api`;
 *  - `app/queryClient` бере `./sliceCachePolicy`, а не весь `features/grid/sliceCache`.
 *
 * Мутаційний доказ (не в коміті): повернути статичний `import { Notifications } from '@mantine/notifications'` в
 * `App.tsx` → червоний перший тест; `npm run budget` — +3,4…3,5 КБ на кожному маршруті.
 */

const Src = path.resolve(process.cwd(), 'src');
const read = (file: string): string => readFileSync(path.join(Src, file), 'utf8');

describe('D-132: вхідний чанк не тягне того, що оболонці не треба для першої відмальовки', () => {
  it('контейнер тостів — ледачим імпортом через alias, не статичним з пакета', () => {
    const app = read('app/App.tsx');
    expect(app).not.toMatch(/import\s*\{[^}]*\bNotifications\b[^}]*\}\s*from\s*'@mantine\/notifications'/);
    expect(app).toContain("import('@ecr/notifications-container')");
  });

  it('бейдж меню й політика кешу зрізів — з легких модулів', () => {
    expect(read('app/useStaleResultsNavCount.ts')).toContain("from '@/features/documents/listSummary'");
    expect(read('app/queryClient.ts')).toContain("from '@/features/grid/sliceCachePolicy'");
  });

  it('тост, показаний ДО того, як ледачий контейнер змонтувався, з\'являється з ним (сховище спільне)', async () => {
    notifications.show({ id: 'before-mount', message: 'toast-before-mount' });

    const { Notifications } = await import('@ecr/notifications-container');
    render(
      <MantineProvider>
        <Notifications />
      </MantineProvider>,
    );

    expect(await screen.findByText('toast-before-mount')).toBeTruthy();
    notifications.clean();
  });
});
