import { Suspense, lazy, type ComponentProps, type JSX } from 'react';
import type { ReportDefinitionsModal } from './ReportDefinitionsModal';

/*
 * «Manage report definitions» — лінивим чанком (D-132, бюджет маршруту `SnapshotsPage`): вікно тягне `ReasonModal`
 * (`Textarea`), `Switch`, `ActionIcon` і `LocalizedInput`, а потрібне лише після кліку і лише з правом
 * `Report.EditDefinition`.
 *
 * ⚠ Обгортка змонтована ЗАВЖДИ, як і статичне вікно до неї: закрита Mantine `Modal` нічого не малює, тож розмітка
 * сторінки та сама, чанк вантажиться разом зі сторінкою, а повернення фокуса на кнопку після закриття працює, як і
 * було (Mantine запам'ятовує відкривача на переході `opened` false → true).
 */
const ReportDefinitionsModalChunk = lazy(async () => ({
  default: (await import('./ReportDefinitionsModal')).ReportDefinitionsModal,
}));

/** `ReportDefinitionsModal` лінивим чанком. */
export function LazyReportDefinitionsModal(props: ComponentProps<typeof ReportDefinitionsModal>): JSX.Element {
  return (
    <Suspense fallback={null}>
      <ReportDefinitionsModalChunk {...props} />
    </Suspense>
  );
}
