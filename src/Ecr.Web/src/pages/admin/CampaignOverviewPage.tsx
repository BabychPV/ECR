import type { JSX } from 'react';
import { Stack, Text } from '@mantine/core';
import { CampaignOverview } from '@/features/campaign/CampaignOverview';
import { t } from '@/shared/i18n';
import { PageHeader } from '@/shared/ui/PageHeader';
import { PeriodPicker } from '@/shared/ui/PeriodPicker';
import { useUrlNumber } from '@/shared/ui/useUrlState';

/**
 * Огляд звітної кампанії (`BE-22`): «хто затримує кампанію періоду».
 *
 * ⚠ Маршрут закрито правом `Report.ViewCampaign` (`routes.ts`, `RouteGuard`);
 * відмову сервера на рівні запиту (право відкликали посеред сесії) малює
 * `AsyncBoundary` окремим станом «немає права».
 *
 * ⚠ Період — у тому вигляді, який приймає API: `Рік*100 + Номер`
 * (`GET /campaign/summary?periodKey`), і живе в адресі, щоб посиланням на
 * огляд можна було поділитися.
 */
export function CampaignOverviewPage(): JSX.Element {
  const [urlPeriod, setPeriodKey] = useUrlNumber('periodKey');
  const periodKey = urlPeriod ?? currentPeriodKey();

  return (
    <Stack gap="lg">
      <PageHeader
        title={t('campaign.title')}
        actions={
          // ⛔ UI-06: `NumberInput` → `PeriodPicker` (`DIRECTIVE-15-FRONTEND.md:129`).
          // Формат `periodKey` і місце в адресі — БЕЗ змін: `setPeriodKey`
          // приймає `number | null` так само, як приймав старий обробник
          // (`typeof value === 'number' ? value : null` — те саме, що вже дає
          // `PeriodPicker.onChange`), тому підставляється напряму.
          <PeriodPicker size="xs" miw={110} value={periodKey} onChange={setPeriodKey} />
        }
      />

      {/* ⛔ Не обмеження, а його відсутність — і людина має знати, що бачить чужі проєкти. */}
      <Text size="sm" c="dimmed">
        {t('campaign.scopeHint')}
      </Text>

      <CampaignOverview periodKey={periodKey} />
    </Stack>
  );
}

/**
 * Поточний період як `Year*100 + Sequence` — лише початкове значення поля,
 * той самий дефолт, що й у `DocumentPage`: справжній поточний період задає
 * календар проєкту, і людина бачить число й може його змінити.
 */
function currentPeriodKey(): number {
  const now = new Date();

  return now.getUTCFullYear() * 100 + (now.getUTCMonth() + 1);
}
