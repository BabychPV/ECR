using Microsoft.Data.SqlClient;

namespace Ecr.MethodologyImport.Reading;

/// <summary>
/// Читання дзеркал методологій старої системи з SQL Server: лише <c>SELECT … WITH (NOLOCK)</c> з
/// <c>AF_Formulas</c> і <c>AF_Constants</c>, з'єднання з наміром лише-читання. Нічого не пише; рядок з'єднання
/// задає користувач (копія бази старої системи, не live).
/// </summary>
public static class LegacySqlMirrorSource
{
    private const string FormulasSql =
        "SELECT MInfo_Name, MInfo_Version, FInfo_Name, FInfo_Version, FInfo_Arguments, FInfo_Text, " +
        "FInfo_StartDate, FInfo_EndDate, FInfo_IsAvailable, FInfo_Report, CONVERT(nvarchar(36), ID) " +
        "FROM dbo.AF_Formulas WITH (NOLOCK)";

    private const string ConstantsSql =
        "SELECT MInfo_Name, MInfo_Version, CInfo_Name, CInfo_Category, CInfo_Version, CInfo_Value, CInfo_Parameter, " +
        "CInfo_StartDate, CInfo_EndDate, CInfo_Unit, CONVERT(nvarchar(36), ID) " +
        "FROM dbo.AF_Constants WITH (NOLOCK)";

    public static (IReadOnlyList<LegacyFormulaRow> Formulas, IReadOnlyList<LegacyConstantRow> Constants) Read(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "Ecr.MethodologyImport (read-only)",
        };

        using var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();

        var formulas = new List<LegacyFormulaRow>();
        using (var command = new SqlCommand(FormulasSql, connection) { CommandTimeout = 300 })
        using (var r = command.ExecuteReader())
        {
            while (r.Read())
            {
                formulas.Add(new LegacyFormulaRow(
                    Str(r, 0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5),
                    Date(r, 6), Date(r, 7), !r.IsDBNull(8) && r.GetBoolean(8), Str(r, 9), Str(r, 10) ?? string.Empty));
            }
        }

        var constants = new List<LegacyConstantRow>();
        using (var command = new SqlCommand(ConstantsSql, connection) { CommandTimeout = 300 })
        using (var r = command.ExecuteReader())
        {
            while (r.Read())
            {
                constants.Add(new LegacyConstantRow(
                    Str(r, 0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5), Str(r, 6),
                    Date(r, 7), Date(r, 8), Str(r, 9), Str(r, 10) ?? string.Empty));
            }
        }

        return (formulas, constants);
    }

    private static string? Str(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTime? Date(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);
}
