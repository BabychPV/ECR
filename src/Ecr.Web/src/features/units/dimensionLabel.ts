import { t } from '@/shared/i18n';

/**
 * Людська назва розмірності за її кодом (`UI RC9`, P3: у ru/kk колонка «Dimension» показувала англійські коди).
 *
 * ⚠ Кожен ключ — ЛІТЕРАЛОМ у своїй гілці: сторож каталогу (`EndpointCoverageTests`) читає `t('…')` з тексту, а
 * ключ, зібраний з коду розмірності, лишився б для нього невидимим. Код, якого немає в переліку (сервер додав
 * розмірність), показується як є — чесніше за `⟦units.dim.…⟧`.
 *
 * ⚠ Значення фільтра, пошуку й сортування лишаються КОДОМ: мовою інтерфейсу змінюється лише підпис.
 */
export function dimensionLabel(code: string): string {
  switch (code) {
    case 'Mass':
      return t('units.dim.Mass');
    case 'Volume':
      return t('units.dim.Volume');
    case 'Energy':
      return t('units.dim.Energy');
    case 'Time':
      return t('units.dim.Time');
    case 'Temperature':
      return t('units.dim.Temperature');
    case 'Amount':
      return t('units.dim.Amount');
    case 'Dimensionless':
      return t('units.dim.Dimensionless');
    case 'MassFlow':
      return t('units.dim.MassFlow');
    case 'MassPerMass':
      return t('units.dim.MassPerMass');
    case 'MassPerEnergy':
      return t('units.dim.MassPerEnergy');
    case 'MassPerVolume':
      return t('units.dim.MassPerVolume');
    case 'StdVolume':
      return t('units.dim.StdVolume');
    case 'StdVolumeFlow':
      return t('units.dim.StdVolumeFlow');
    case 'Velocity':
      return t('units.dim.Velocity');
    case 'Area':
      return t('units.dim.Area');
    case 'MassPerStdVolume':
      return t('units.dim.MassPerStdVolume');
    case 'EnergyPerStdVolume':
      return t('units.dim.EnergyPerStdVolume');
    case 'EnergyPerMass':
      return t('units.dim.EnergyPerMass');
    case 'MassPerAmount':
      return t('units.dim.MassPerAmount');
    default:
      return code;
  }
}
