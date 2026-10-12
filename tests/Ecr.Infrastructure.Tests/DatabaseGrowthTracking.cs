using Ecr.TestKit;

// D-375: після кожного тесту колекції `SqlServer` записуємо ріст файлів спільної бази — так у Step Summary CI
// видно, який клас виростив журнал/дані (`DatabaseGrowthTracker`). Один запит до sys.database_files на тест.
[assembly: TrackDatabaseGrowth]
