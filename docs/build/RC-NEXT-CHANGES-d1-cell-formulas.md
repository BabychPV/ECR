# RC-NEXT: D1 Cell-формули - відкриті пункти (P3)

- Cell-формули (Scope=Cell) не відображаються в `GET structure` та у `Verify` версії шаблону: автор бачить їх лише через
  `PUT/DELETE .../formulas/cell/{rowKey}/{columnDefId}`. Потрібно: віддавати у структурі й перевіряти в Verify.
- 4226 (Formula-колонка без джерела) навмисно приймає Cell-формулу як джерело (консолідаційні аркуші); 4225 - ні (див. PublishChecks.CheckRequiredCoverage).
- EndpointCoverageTests.ServerOnlyActions: два маршрути `formulas/cell` звільнені, доки немає виклику в `features/templates`; прибрати разом з клієнтом.
