using System.Globalization;
using System.Text.Json;

namespace ChipTracking.Services;

public sealed class TwsePriceScraper : IDisposable
{
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public TwsePriceScraper()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126 Safari/537.36");
    }

    public async Task<IReadOnlyList<StockClosePrice>> FetchMonthlyClosePricesAsync(
        string stockNo,
        DateOnly month,
        CancellationToken cancellationToken = default)
    {
        var twseRows = await FetchTwseMonthlyClosePricesAsync(stockNo, month, cancellationToken);
        if (twseRows.Count > 0)
        {
            return twseRows;
        }

        return await FetchTpexMonthlyClosePricesAsync(stockNo, month, cancellationToken);
    }

    private async Task<IReadOnlyList<StockClosePrice>> FetchTwseMonthlyClosePricesAsync(
        string stockNo,
        DateOnly month,
        CancellationToken cancellationToken)
    {
        var url = $"https://www.twse.com.tw/exchangeReport/STOCK_DAY?response=json&date={month:yyyyMM}01&stockNo={Uri.EscapeDataString(stockNo)}";
        var json = await _httpClient.GetStringAsync(url, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("stat", out var stat) ||
            !string.Equals(stat.GetString(), "OK", StringComparison.OrdinalIgnoreCase) ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<StockClosePrice>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 7)
            {
                continue;
            }

            var dateText = item[0].GetString() ?? "";
            var closeText = item[6].GetString() ?? "";
            if (TryParseTwseDate(dateText, out var tradeDate) &&
                decimal.TryParse(closeText.Replace(",", "", StringComparison.Ordinal), NumberStyles.Any, CultureInfo.InvariantCulture, out var closePrice))
            {
                rows.Add(new StockClosePrice(tradeDate, closePrice));
            }
        }

        return rows;
    }

    private async Task<IReadOnlyList<StockClosePrice>> FetchTpexMonthlyClosePricesAsync(
        string stockNo,
        DateOnly month,
        CancellationToken cancellationToken)
    {
        var url = $"https://www.tpex.org.tw/www/zh-tw/afterTrading/tradingStock?code={Uri.EscapeDataString(stockNo)}&date={month:yyyy/MM}/01&response=json";
        var json = await _httpClient.GetStringAsync(url, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("tables", out var tables) ||
            tables.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<StockClosePrice>();
        foreach (var table in tables.EnumerateArray())
        {
            if (!table.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 7)
                {
                    continue;
                }

                var dateText = item[0].GetString() ?? "";
                var closeText = item[6].GetString() ?? "";
                if (TryParseTwseDate(dateText, out var tradeDate) &&
                    decimal.TryParse(closeText.Replace(",", "", StringComparison.Ordinal), NumberStyles.Any, CultureInfo.InvariantCulture, out var closePrice))
                {
                    rows.Add(new StockClosePrice(tradeDate, closePrice));
                }
            }
        }

        return rows;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient.Dispose();
        _disposed = true;
    }

    private static bool TryParseTwseDate(string text, out DateOnly date)
    {
        date = default;
        var parts = text.Split('/');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var rocYear) ||
            !int.TryParse(parts[1], out var month) ||
            !int.TryParse(parts[2], out var day))
        {
            return false;
        }

        date = new DateOnly(rocYear + 1911, month, day);
        return true;
    }
}

public sealed record StockClosePrice(DateOnly TradeDate, decimal ClosePrice);
