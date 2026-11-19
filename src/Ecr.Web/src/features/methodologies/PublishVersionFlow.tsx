import { lazy, Suspense, useState, type JSX } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { MethodologyPublicationDiff, PublishMethodologyRequest } from '@/api/types';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { showDone } from '@/shared/ui/notify';
import { publishMethodologyVersion } from './api';
import { showPublishError } from './publishError';

const PublishVersionDialogs = lazy(async () => ({
  default: (await import('./PublishVersionDialogs')).PublishVersionDialogs,
}));

export interface PublishVersionFlow {
  /** Відкриває діалог публікації версії. */
  readonly open: (versionId: number) => void;
  /** Діалоги (публікація й diff результатів) — вставити в дерево сторінки. */
  readonly dialogs: JSX.Element;
}

/**
 * Публікація версії методології: діалог причини й дати, виклик і показ diff
 * результатів. Спільна для сторінки версій і сторінки скрипта.
 *
 * ⛔ Причина й дата обов'язкові на сервері (`ECR-CALC-0422`, ФВ-14.7); кнопка
 * вимкнена, доки вони порожні, — не з ввічливості, а щоб не вести на гарантовану
 * відмову. Сама заборона лишається доменною.
 *
 * ⛔ Diff РЕЗУЛЬТАТІВ, не тексту формул (ФВ-9.6): змінений рядок виразу не каже
 * нічого, змінена на 4 % емісія каже все.
 */
export function usePublishVersion(
  methodologyId: number,
  onPublished?: (versionId: number) => void,
): PublishVersionFlow {
  const queryClient = useQueryClient();
  const [publishing, setPublishing] = useState<{ versionId: number } | null>(null);
  const [publishReason, setPublishReason] = useState('');
  const [publishEffectiveFrom, setPublishEffectiveFrom] = useState('');
  const [publishDiff, setPublishDiff] = useState<MethodologyPublicationDiff | null>(null);

  const publish = useMutation({
    mutationFn: () =>
      publishMethodologyVersion(methodologyId, publishing?.versionId ?? 0, {
        changeReason: publishReason,
        // ⚠ `date`, не `Date`: `toISOString()` іде через UTC і ввечері
        // зсуває дату на добу назад.
        effectiveFrom: publishEffectiveFrom,
      } satisfies PublishMethodologyRequest),
    onSuccess: async (diff) => {
      const publishedId = publishing?.versionId ?? 0;
      await queryClient.invalidateQueries({
        queryKey: queryKeys.methodologies.versionsOf(methodologyId),
      });
      setPublishing(null);
      setPublishReason('');
      setPublishEffectiveFrom('');
      setPublishDiff(diff);
      showDone(t('methodologies.published'));
      onPublished?.(publishedId);
    },

    // ⚠ Публікація падає з переліком конкретних проблем — немає золотого
    // тесту (ФВ-9.12), незайнята дата, цикл формул тощо (`ECR-CALC-0422`,
    // `ECR-CALC-0409`). `showPublishError` показує ТЕКСТ відмови сервера, а не
    // узагальнене «не вдалося». ⚠ F-15/B-12: під назвою — перелік проблем.
    onError: showPublishError,
  });

  const publishLoading = usePendingLoading(publish.isPending);

  // ⚠ Діалоги — за `import()` (`D-132`); чанк вантажиться при першому «Publish» і далі лишається змонтованим,
  // щоб закриття мало анімацію, як раніше.
  const [armed, setArmed] = useState(false);

  const dialogs = (
    <>
      {armed || publishing !== null || publishDiff !== null ? (
        <Suspense fallback={null}>
          <PublishVersionDialogs
            publishing={publishing}
            publishReason={publishReason}
            publishEffectiveFrom={publishEffectiveFrom}
            publishDiff={publishDiff}
            publishLoading={publishLoading}
            isPending={publish.isPending}
            onReason={setPublishReason}
            onEffectiveFrom={setPublishEffectiveFrom}
            onClosePublishing={() => setPublishing(null)}
            onCloseDiff={() => setPublishDiff(null)}
            onPublish={() => publish.mutate()}
          />
        </Suspense>
      ) : null}
    </>
  );

  return {
    open: (versionId) => {
      setArmed(true);
      setPublishing({ versionId });
    },
    dialogs,
  };
}
