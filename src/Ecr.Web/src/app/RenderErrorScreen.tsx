import { useEffect, useMemo, useState, type JSX } from 'react';
import { Button, Group, Loader, Stack, Text } from '@mantine/core';
import { EcrApiError, newCorrelationId } from '@/api/client';
import { hasText, t } from '@/shared/i18n';
import { ErrorAlert, TechnicalDetails } from '@/shared/ui/ErrorAlert';
import { isChunkLoadError, reloadOnceForStaleBuild } from './chunkReload';
import { useStaleVersionState } from './staleVersion';

/**
 * Екран «сторінку не вдалося показати» (`D14-11`).
 *
 * **Факт, з якого це виросло.** У застосунку не було ЖОДНОЇ межі помилки —
 * ні `errorElement` у `router.tsx`, ні класового `ErrorBoundary` у
 * `main.tsx`. Будь-яка помилка рендера замінювала весь застосунок
 * розробницьким екраном React Router («Unexpected Application Error!»), а
 * разом із ним зникали навігація, шапка і все, що користувач не встиг
 * зберегти.
 *
 * ⚠ Показ помилки — той самий `ErrorAlert`, що й скрізь: та сама подача, той
 * самий стабільний код, той самий ідентифікатор кореляції.
 *
 * ⚠ `correlationId` тут КЛІЄНТСЬКИЙ (`newCorrelationId()`): помилка рендера
 * не має запиту, з яким її можна зіставити, але скарга користувача без
 * жодного ідентифікатора непорівнянна ні з чим узагалі.
 *
 * ✎ 25.11 (en/ru/kz): написи — з каталогу (`appError.*`, ПУБЛІЧНА область, тобто є й до входу), але кожен
 * виклик стоїть за `hasText(...) ? t(...) : 'літерал'`. ⛔ Саме так, а не голий `t()`: екран показують тоді,
 * коли застосунок зламався, і зламаний (чи ще не завантажений) каталог перетворив би пояснення на `⟦...⟧`
 * (прецедент — `CATALOG_LOAD_FAILED` у `pages/LoginPage.tsx`). Англійський літерал — запасний текст клієнта
 * (`D14-13`). Обгортки `tOr` немає навмисно (див. `hasText` у `shared/i18n`): кожен ключ — у цьому файлі.
 *
 * ✎ 25.11: відмова лінивого чанка (`isChunkLoadError` або подія `vite:preloadError`) лікується ОДНИМ
 * автоматичним перезавантаженням на збірку (`chunkReload.ts`) — після того, як незбережене збережено
 * (`staleVersion.tsx`). Не вдалося зберегти, або цю збірку вже перезавантажували — лишається пояснення з кнопкою.
 */

/** Стабільний код помилки рендера — для звернення в підтримку. */
export const RenderErrorCode = 'ECR-WEB-RENDER-FAILED';

/** Стабільний код «чанк зник після оновлення» (`DAT-08`). */
export const StaleChunkErrorCode = 'ECR-WEB-CHUNK-STALE';

/**
 * Перетворює будь-що кинуте на `EcrApiError` — форму, яку вміє `ErrorAlert`.
 *
 * ✎ `X-27`: технічний текст (`TypeError: …`) НЕ частина пояснення — він під
 * розгортанням «Technical details» нижче (підтримці він потрібен дослівно).
 *
 * ⚠ Параметр лишається (`_error`): нова помилка — нова кореляція (`useMemo`
 * нижче перераховує саме за нею), хоча в текст вона вже не йде.
 */
function renderProblem(_error: unknown): EcrApiError {
  return new EcrApiError({
    title: hasText('appError.renderTitle') ? t('appError.renderTitle') : 'This screen could not be displayed',
    status: 0,
    errorCode: RenderErrorCode,
    correlationId: newCorrelationId(),
    detail: hasText('appError.renderDetail')
      ? t('appError.renderDetail')
      : 'The application hit an internal error while drawing this screen. Navigation still works: ' +
        'you can reload the page or go back to the document list.',
  });
}

/** Технічний текст кинутого — для розгортання, не для пояснення. */
function technicalOf(error: unknown): string {
  return error instanceof Error ? `${error.name}: ${error.message}` : String(error ?? 'unknown');
}

/**
 * Той самий екран, але з ПРАВДИВОЮ причиною, коли збірка застаріла
 * (`DAT-08`): назвати її «внутрішньою помилкою» означало б збрехати — вона
 * лікується перезавантаженням, а не зверненням у підтримку.
 */
function staleChunkProblem(): EcrApiError {
  return new EcrApiError({
    title: hasText('appError.staleTitle') ? t('appError.staleTitle') : 'A new version is installed',
    status: 0,
    errorCode: StaleChunkErrorCode,
    correlationId: newCorrelationId(),
    detail: hasText('appError.staleDetail')
      ? t('appError.staleDetail')
      : 'This tab is running an older build of the application and can no longer load part of ' +
        'itself. Reload the page to get the new version.',
  });
}

export function RenderErrorScreen({ error }: { error: unknown }): JSX.Element {
  const { outdated, save } = useStaleVersionState();
  const chunk = outdated || isChunkLoadError(error);

  // ⚠ Ідентифікатор кореляції рахується РАЗ на помилку, а не на рендер:
  // інакше користувач диктував би підтримці різні номери щоразу, як екран
  // перемалюється (а він перемальовується — банер, тема, зміна розміру).
  const problem = useMemo(
    () => (chunk ? staleChunkProblem() : renderProblem(error)),
    [error, chunk],
  );

  // ⛔ Не під час збереження (перезавантаження обірвало б запит) і не після відмови збереження (викинуло б
  // правки). `reloadOnceForStaleBuild` сам перевіряє, що незбережених правок немає і що цю збірку ще не
  // перезавантажували.
  const [reloading, setReloading] = useState(false);
  useEffect(() => {
    if (!chunk || reloading || save === 'saving' || save === 'failed') return;
    if (reloadOnceForStaleBuild()) setReloading(true);
  }, [chunk, reloading, save]);

  return (
    <Stack gap="md" data-testid="render-error-screen" data-chunk-error={chunk ? 'true' : undefined}>
      <ErrorAlert error={problem} />

      {reloading && (
        <Group gap="xs" role="status" data-testid="render-error-reloading">
          <Loader size="xs" />
          <Text size="sm">
            {hasText('appError.reloading') ? t('appError.reloading') : 'Loading the new version...'}
          </Text>
        </Group>
      )}

      {/* Застарілій збірці технічних подробиць немає — причина там названа повністю. */}
      {!chunk && (
        <TechnicalDetails
          label={hasText('appError.technicalDetails') ? t('appError.technicalDetails') : 'Technical details'}
        >
          {technicalOf(error)}
        </TechnicalDetails>
      )}

      {/*
       * ⛔ Тупикових екранів не буває (`ФВ-14.24`). Дві дії: «Перезавантажити сторінку» і «До переліку
       * документів». ⚠ Обидві — ПОВНИЙ перехід браузера (`window.location`), а не `<Link>`/`navigate`: цей
       * же екран показує класова межа з `main.tsx`, де контексту маршрутизатора може не бути взагалі, а після
       * помилки рендера частина дерева React уже в невизначеному стані.
       */}
      <Group gap="xs">
        <Button onClick={() => window.location.reload()}>
          {hasText('appError.reload') ? t('appError.reload') : 'Reload page'}
        </Button>
        <Button variant="default" onClick={() => window.location.assign('/')}>
          {hasText('appError.toDocuments') ? t('appError.toDocuments') : 'Go to document list'}
        </Button>
      </Group>
    </Stack>
  );
}
