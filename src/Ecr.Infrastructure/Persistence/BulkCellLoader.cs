using System.Data;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Масове завантаження через <see cref="SqlBulkCopy"/> (D-05).
/// </summary>
/// <remarks>
/// <c>Id</c> для <c>TableRow</c> береться з <c>SEQUENCE</c>, а не
/// <c>IDENTITY</c>: значення потрібні **до** вставки, щоб завантажити рядки і
/// комірки одним проходом. З <c>IDENTITY</c> довелося б робити два кроки з
/// <c>OUTPUT</c>, а <c>OUTPUT</c> конфліктує з тригерами (B02 §2.3).
/// </remarks>
public sealed class BulkCellLoader(string connectionString, int batchSize)
{
    /// <summary>
    /// Колонки <c>doc.CellValue</c> у порядку, в якому їх віддає
    /// <see cref="CellRecordReader"/>.
    /// </summary>
    /// <remarks>
    /// Мапінг явний за іменами. Порядковий мапінг «за замовчуванням» мовчки
    /// поїде від першої ж зміни порядку колонок у таблиці, а <c>SqlBulkCopy</c>
    /// не скаржиться — він просто запише не те.
    /// </remarks>
    private static readonly string[] Columns =
    [
        "PeriodKey",
        "TableRowId",
        "ColumnDefId",
        "TableDefId",
        "ValueString",
        "ValueNumeric",
        "ValueDate",
        "ValueBool",
        "ValueRegistryEntryId",
        "ValueUnitId",
        "IsCalculated",
        "IsEmpty",
    ];

    /// <summary>Завантажує комірки.</summary>
    public async Task LoadAsync(IReadOnlyList<CellRecord> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // TableLock: без нього SqlBulkCopy бере блокування рядків і втрачає
        // мінімальне журналювання — на мільйонах рядків це різниця в рази.
        using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, externalTransaction: null)
        {
            DestinationTableName = "doc.CellValue",
            BatchSize = batchSize,
            BulkCopyTimeout = 0,
        };

        foreach (var column in Columns)
        {
            copy.ColumnMappings.Add(column, column);
        }

        // IDataReader, а не DataTable: 108 млн рядків у DataTable не
        // поміщаються в пам'ять, і навіть мільйон коштує гігабайти.
        using var reader = new CellRecordReader(records);
        await copy.WriteToServerAsync(reader, ct).ConfigureAwait(false);
    }

    /// <summary>Резервує діапазон ідентифікаторів із послідовності.</summary>
    /// <returns>Перше значення діапазону; далі йдуть <paramref name="count"/> послідовних.</returns>
    /// <remarks>
    /// Один виклик на весь батч, не на рядок: <c>NEXT VALUE FOR</c> у циклі на
    /// 100 тисяч рядків — це 100 тисяч звернень до системних структур.
    /// </remarks>
    public async Task<long> ReserveIdsAsync(string sequenceName, int count, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "sys.sp_sequence_get_range";
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.AddWithValue("@sequence_name", sequenceName);
        command.Parameters.AddWithValue("@range_size", count);

        var first = command.Parameters.Add("@range_first_value", SqlDbType.Variant);
        first.Direction = ParameterDirection.Output;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(first.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Потокова обгортка <see cref="CellRecord"/> у <see cref="IDataReader"/>.
    /// </summary>
    /// <remarks>
    /// Реалізовані лише члени, які насправді викликає <see cref="SqlBulkCopy"/>:
    /// <c>FieldCount</c>, <c>Read</c>, <c>GetValue</c>, <c>GetOrdinal</c>,
    /// <c>GetName</c>. Решта кидає <see cref="NotSupportedException"/> — це
    /// чесніше за мовчазний <c>null</c>, який перетворив би помилку виклику на
    /// зіпсовані дані.
    /// </remarks>
    private sealed class CellRecordReader(IReadOnlyList<CellRecord> records) : IDataReader
    {
        private int _index = -1;

        private CellRecord Current => records[_index];

        public int FieldCount => Columns.Length;

        public bool Read()
        {
            _index++;
            return _index < records.Count;
        }

        public object GetValue(int i)
        {
            var record = Current;
            var value = record.Value;

            return i switch
            {
                0 => record.Address.PeriodKey.Value,
                1 => record.Address.TableRowId,
                2 => record.Address.ColumnDefId,
                3 => record.TableDefId,
                4 => (object?)value.ValueString ?? DBNull.Value,
                5 => (object?)value.ValueNumeric ?? DBNull.Value,
                6 => (object?)value.ValueDate ?? DBNull.Value,
                7 => (object?)value.ValueBool ?? DBNull.Value,
                8 => (object?)value.ValueRegistryEntryId ?? DBNull.Value,
                9 => (object?)value.ValueUnitId ?? DBNull.Value,
                10 => value.IsCalculated,
                11 => value.IsEmpty,
                _ => throw new ArgumentOutOfRangeException(nameof(i), i, "Немає такої колонки."),
            };
        }

        public string GetName(int i) => Columns[i];

        public int GetOrdinal(string name)
        {
            var ordinal = Array.IndexOf(Columns, name);
            return ordinal >= 0
                ? ordinal
                : throw new ArgumentOutOfRangeException(nameof(name), name, "Немає такої колонки.");
        }

        public bool IsDBNull(int i) => GetValue(i) is DBNull;

        public void Close() { }

        public void Dispose() { }

        public int Depth => 0;

        public bool IsClosed => _index >= records.Count;

        public int RecordsAffected => -1;

        public object this[int i] => GetValue(i);

        public object this[string name] => GetValue(GetOrdinal(name));

        public bool NextResult() => false;

        public DataTable? GetSchemaTable() => null;

        public bool GetBoolean(int i) => (bool)GetValue(i);

        public byte GetByte(int i) => (byte)GetValue(i);

        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length)
            => throw new NotSupportedException();

        public char GetChar(int i) => (char)GetValue(i);

        public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length)
            => throw new NotSupportedException();

        public IDataReader GetData(int i) => throw new NotSupportedException();

        public string GetDataTypeName(int i) => throw new NotSupportedException();

        public DateTime GetDateTime(int i) => (DateTime)GetValue(i);

        public decimal GetDecimal(int i) => (decimal)GetValue(i);

        public double GetDouble(int i) => throw new NotSupportedException("float заборонений (D-30).");

        public Type GetFieldType(int i) => throw new NotSupportedException();

        public float GetFloat(int i) => throw new NotSupportedException("float заборонений (D-30).");

        public Guid GetGuid(int i) => (Guid)GetValue(i);

        public short GetInt16(int i) => (short)GetValue(i);

        public int GetInt32(int i) => (int)GetValue(i);

        public long GetInt64(int i) => (long)GetValue(i);

        public string GetString(int i) => (string)GetValue(i);

        public int GetValues(object[] values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var count = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < count; i++)
            {
                values[i] = GetValue(i);
            }

            return count;
        }
    }
}
