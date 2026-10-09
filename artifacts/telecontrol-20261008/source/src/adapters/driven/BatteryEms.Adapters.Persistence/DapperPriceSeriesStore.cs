using BatteryEms.Application.Markets;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperPriceSeriesStore : IPriceSeriesSource, IPriceSeriesImportSink
{
    private const string UpsertHeaderSql = """
        INSERT INTO price_series (
            series_id,
            market_bid_area,
            product,
            price_kind,
            source,
            unit,
            horizon_start,
            horizon_end,
            time_step_ticks,
            imported_at)
        VALUES (
            @SeriesId,
            @MarketBidArea,
            @Product,
            @PriceKind,
            @Source,
            @Unit,
            @HorizonStart,
            @HorizonEnd,
            @TimeStepTicks,
            @ImportedAt)
        ON CONFLICT (
            market_bid_area,
            product,
            price_kind,
            source,
            horizon_start,
            horizon_end,
            time_step_ticks)
        DO UPDATE SET
            unit = EXCLUDED.unit,
            imported_at = EXCLUDED.imported_at
        RETURNING series_id;
        """;

    private const string DeletePointsSql = "DELETE FROM price_series_points WHERE series_id = @SeriesId;";

    private const string InsertPointSql = """
        INSERT INTO price_series_points (series_id, position, timestamp, value)
        VALUES (@SeriesId, @Position, @Timestamp, @Value);
        """;

    private const string SelectHeaderSql = """
        SELECT
            series_id,
            market_bid_area,
            product,
            price_kind,
            source,
            unit,
            horizon_start,
            horizon_end,
            time_step_ticks
        FROM price_series
        WHERE market_bid_area = @MarketBidArea
          AND product = @Product
          AND price_kind = @PriceKind
          AND source = @Source
          AND horizon_start = @HorizonStart
          AND horizon_end = @HorizonEnd
          AND time_step_ticks = @TimeStepTicks;
        """;

    private const string SelectPointsSql = """
        SELECT value
        FROM price_series_points
        WHERE series_id = @SeriesId
        ORDER BY position ASC;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public DapperPriceSeriesStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DapperConfig.EnsureConfigured();
        _dataSource = dataSource;
    }

    public async Task ImportAsync(PriceSeries series, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(series);
        cancellationToken.ThrowIfCancellationRequested();

        var horizonStartUtc = series.HorizonStart.ToUniversalTime();
        var horizonEndUtc = series.HorizonEnd.ToUniversalTime();

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var seriesId = await connection.QuerySingleAsync<Guid>(new CommandDefinition(
                    UpsertHeaderSql,
                    new
                    {
                        SeriesId = Guid.NewGuid(),
                        series.MarketBidArea,
                        series.Product,
                        series.PriceKind,
                        series.Source,
                        series.Unit,
                        HorizonStart = horizonStartUtc,
                        HorizonEnd = horizonEndUtc,
                        TimeStepTicks = series.TimeStep.Ticks,
                        ImportedAt = DateTimeOffset.UtcNow,
                    },
                    transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                await connection.ExecuteAsync(new CommandDefinition(
                    DeletePointsSql,
                    new { SeriesId = seriesId },
                    transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                for (var i = 0; i < series.Values.Count; i++)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        InsertPointSql,
                        new
                        {
                            SeriesId = seriesId,
                            Position = i,
                            Timestamp = horizonStartUtc + (series.TimeStep * i),
                            Value = series.Values[i],
                        },
                        transaction: transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<PriceSeries> LoadAsync(
        PriceSeriesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.EnsureValid();
        var horizonStartUtc = request.HorizonStart.ToUniversalTime();
        var horizonEndUtc = request.HorizonEnd.ToUniversalTime();

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var header = await connection.QuerySingleOrDefaultAsync<PriceSeriesRow>(new CommandDefinition(
                SelectHeaderSql,
                new
                {
                    request.MarketBidArea,
                    request.Product,
                    request.PriceKind,
                    request.Source,
                    HorizonStart = horizonStartUtc,
                    HorizonEnd = horizonEndUtc,
                    TimeStepTicks = request.TimeStep.Ticks,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (header is null)
            {
                throw new PriceSeriesNotFoundException(request);
            }

            var values = await connection.QueryAsync<double>(new CommandDefinition(
                SelectPointsSql,
                new { header.SeriesId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            return new PriceSeries(
                header.MarketBidArea,
                header.Product,
                header.PriceKind,
                header.Unit,
                header.Source,
                TimestampConverter.ToOffset(header.HorizonStart),
                TimestampConverter.ToOffset(header.HorizonEnd),
                TimeSpan.FromTicks(header.TimeStepTicks),
                values.ToArray());
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class PriceSeriesRow
    {
        public Guid SeriesId { get; init; }
        public string MarketBidArea { get; init; } = string.Empty;
        public string Product { get; init; } = string.Empty;
        public string PriceKind { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;
        public DateTime HorizonStart { get; init; }
        public DateTime HorizonEnd { get; init; }
        public long TimeStepTicks { get; init; }
    }
}
