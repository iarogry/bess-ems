using System.Data;
using Dapper;

namespace BatteryEms.Adapters.Persistence;

// One-shot Dapper configuration shared across all repositories in this
// assembly. MatchNamesWithUnderscores lets row classes use PascalCase
// property names while the SQL columns stay snake_case — readable on
// both sides without per-query AS aliases.
internal static class DapperConfig
{
    private static readonly Lazy<bool> Configuration = new(static () =>
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        return true;
    });

    public static void EnsureConfigured()
    {
        _ = Configuration.Value;
    }

    // Npgsql supports DateOnly; Dapper needs an explicit parameter/row handler.
    // A SQL date has no time zone and must not be converted via local/UTC time.
    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value;
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly date => date,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => throw new DataException("Unsupported PostgreSQL date representation."),
        };
    }
}
