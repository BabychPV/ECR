import { useMemo, type JSX } from 'react';
import { Button, Group, Stack } from '@mantine/core';
import { EcrApiError, newCorrelationId } from '@/api/client';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { isStaleVersion } from './staleVersion';

/**
 * Екран «сторінку не вдалося показати» (`D14-11`).
 *
 * **Факт, з якого це виросло.** У застосунку не було ЖОДНОЇ межі помилки —
 * ні `errorElement` у `router.tsx`, ні класового `ErrorBoundary` у
 * `main.tsx`. Будь-яка помилка рендера замінювала весь застосунок
 * розробницьким екраном React Router («Unexpected Application Error!»), а
 * разом із ним зникали навігація, шапка і все, що користувач не встиг
 * зберегти. Код сам це визнавав у трьох місцях
 * (`CreateDocumentModal.tsx:236`, `RegistryEntryEditor.tsx:124`,
 * `SecurityPage.tsx:552`) — тобто дефект був відомий і не закритий.
 *
 * ⚠ Показ помилки — той самий `ErrorAlert`, що й скрізь (він і є «єдине
 * місце» показу помилки, див. коментар у `shared/ui/ErrorAlert.tsx`): та сама
 * подача, той самий стабільний код, той самий ідентифікатор кореляції. Друга
 * власна форма помилки на цьому екрані означала б, що найстрашніший екран
 * системи виглядає інакше за всі інші.
 *
 * ⚠ `correlationId` тут КЛІЄНТСЬКИЙ (`newCorrelationId()`): помилка рендера
 * не має запиту, з яким її можна зіставити, але скарга користувача без
 * жодного ідентифікатора непорівнянна ні з чим узагалі.
 *
 * ⛔ Написи — ЛІТЕРАЛИ, не `t()`. Прецедент і аргумент — `CATALOG_LOAD_FAILED`
 * у `pages/LoginPage.tsx`: рядки інтерфейсу приходять із СЕРВЕРНОГО каталогу
 * (`09-seed.sql`), ключів під ці написи там немає, а на ЦЬОМУ екрані `t()`
 * небезпечний удвічі — його показують саме тоді, коли застосунок зламався, і
 * зламаний (чи просто не завантажений) каталог перетворив би пояснення
 * причини на `⟦...⟧`. `ErrorAlert` показує `problem.title`/`message`
 * напряму, у каталог не заглядаючи, — тому літерал доходить до екрана
 * дослівно.
 */

/** Стабільний код помилки рендера — для звернення в підтримку. */
export const RenderErrorCode = 'ECR-WEB-RENDER-FAILED';

/** Стабільний код «чанк зник після оновлення» (`DAT-08`). */
export const StaleChunkErrorCode = 'ECR-WEB-CHUNK-STALE';

/**
 * Перетворює будь-що кинуте на `EcrApiError` — форму, яку вміє `ErrorAlert`.
 *
 * ⚠ Технічний текст помилки НЕ ховається: без нього і користувач, і
 * підтримка бачать лише «щось зламалося», а саме це `07-checkpoints`
 * (Етап 6) і забороняє. Він іде ПІСЛЯ пояснення, а не замість нього.
 */
function renderProblem(error: unknown): EcrApiError {
  const technical =
    error instanceof Error ? `${error.name}: ${error.message}` : String(error ?? 'unknown');

  return new EcrApiError({
    title: 'This screen could not be displayed',
    status: 0,
    errorCode: RenderErrorCode,
    correlationId: newCorrelationId(),
    detail:
      'The application hit an internal error while drawing this screen. Navigation still works: ' +
      `you can reload the page or go back to the document list. Technical detail: ${technical}`,
  });
}

/**
 * Той самий екран, але з ПРАВДИВОЮ причиною, коли збірка застаріла
 * (`DAT-08`): відхилений `import()` чанка приходить сюди як звичайна помилка
 * рендера, і назвати її «внутрішньою помилкою» означало б збрехати — вона
 * лікується перезавантаженням, а не зверненням у підтримку.
 */
function staleChunkProblem(): EcrApiError {
  return new EcrApiError({
    title: 'A new version is installed',
    status: 0,
    errorCode: StaleChunkErrorCode,
    correlationId: newCorrelationId(),
    detail:
      'This tab is running an older build of the application and can no longer load part of ' +
      'itself. Reload the page to get the new version.',
  });
}

export function RenderErrorScreen({ error }: { error: unknown }): JSX.Element {
  const outdated = isStaleVersion();

  // ⚠ Ідентифікатор кореляції рахується РАЗ на помилку, а не на рендер:
  // інакше користувач диктував би підтримці різні номери щоразу, як екран
  // перемалюється (а він перемальовується — банер, тема, зміна розміру).
  const problem = useMemo(
    () => (outdated ? staleChunkProblem() : renderProblem(error)),
    [error, outdated],
  );

  return (
    <Stack gap="md" data-testid="render-error-screen">
      <ErrorAlert error={problem} />

      {/*
       * ⛔ Тупикових екранів не буває (`ФВ-14.24`). Дві дії, названі
       * директивою: «Перезавантажити сторінку» і «До переліку документів».
       *
       * ⚠ Обидві — ПОВНИЙ перехід браузера (`window.location`), а не
       * `<Link>`/`navigate`. Дві причини: (1) цей же екран показує класова
       * межа з `main.tsx`, де контексту маршрутизатора може не бути взагалі;
       * (2) після помилки рендера частина дерева React уже в невизначеному
       * стані, і клієнтський перехід поніс би цей стан із собою — саме те,
       * від чого користувач тут і тікає.
       */}
      <Group gap="xs">
        <Button size="xs" onClick={() => window.location.reload()}>
          Reload page
        </Button>
        <Button size="xs" variant="default" onClick={() => window.location.assign('/')}>
          Go to document list
        </Button>
      </Group>
    </Stack>
  );
}
