using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ChipTracking.Services;

public sealed class FubonMoneyDjScraper : IDisposable
{
    private const string BaseUrl = "https://fubon-ebrokerdj.fbs.com.tw";
    private const string SourceName = "fubon-moneydj";
    private const string StockSourceName = "fubon-stock-zco";
    private const string BrokerStockDetailSourceName = "fubon-zco0-detail";
    private static readonly Encoding Big5Encoding;
    private static readonly Regex BrokerListRegex = new(@"g_BrokerList\s*=\s*'(?<value>[^']*)'", RegexOptions.Compiled);
    private static readonly Regex TableRowRegex = new(@"(?is)<tr[^>]*>(?<row>.*?)</tr>", RegexOptions.Compiled);
    private static readonly Regex TableCellRegex = new(@"(?is)<td[^>]*>(?<cell>.*?)</td>", RegexOptions.Compiled);
    private static readonly Regex ScriptLinkRegex = new(@"GenLink2stk\('\s*(?:AS)?(?<stock>[0-9A-Z]+)'\s*,\s*'(?<name>[^']*)'\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnchorLinkRegex = new(@"Link2Stk\('\s*(?<stock>[0-9A-Z]+)'\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BranchLinkRegex = new(@"zco0\.djhtm\?(?<query>[^""']+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PageDateRegex = new(@"資料日期：\s*(?<date>\d{8})", RegexOptions.Compiled);
    private static readonly Regex LastUpdateDateRegex = new(@"最後更新日：\s*(?<date>\d{4}/\d{1,2}/\d{1,2})", RegexOptions.Compiled);
    private static readonly Regex ScriptBlockRegex = new(@"(?is)<script\b.*?</script>", RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new(@"(?is)<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private bool _disposed;

    static FubonMoneyDjScraper()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Big5Encoding = Encoding.GetEncoding(950);
    }

    public FubonMoneyDjScraper()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Referrer = new Uri($"{BaseUrl}/z/zg/zgb/zgb0.djhtm");
    }

    public async Task<IReadOnlyList<BrokerBranch>> GetBrokerBranchesAsync(CancellationToken cancellationToken = default)
    {
        var text = await GetBig5StringAsync("/z/js/zbrokerjs.djjs", cancellationToken);
        var match = BrokerListRegex.Match(text);
        if (!match.Success)
        {
            throw new InvalidOperationException("Fubon broker list was not found.");
        }

        var branches = new List<BrokerBranch>();
        foreach (var groupText in match.Groups["value"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var entries = groupText.Split('!', StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseBrokerListEntry)
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
                .ToList();
            if (entries.Count == 0)
            {
                continue;
            }

            var majorId = entries[0].Id;
            var majorName = entries[0].Name;
            foreach (var entry in entries)
            {
                branches.Add(new BrokerBranch
                {
                    MajorId = majorId,
                    MajorName = majorName,
                    BranchId = entry.Id,
                    BranchName = NormalizeBranchName(entry.Name, majorName, entry.Id, majorId),
                    Notes = SourceName
                });
            }
        }

        return branches
            .GroupBy(branch => $"{branch.MajorId}|{branch.BranchId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(branch => branch.MajorId, StringComparer.Ordinal)
            .ThenBy(branch => branch.BranchId, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<DateOnly> GetLatestTradeDateAsync(CancellationToken cancellationToken = default)
    {
        var page = await FetchRankPageAsync("6010", "6010", "E", null, cancellationToken);
        return page.PageDate ?? DateOnly.FromDateTime(DateTime.Today);
    }

    public async Task<WantGooFetchResult> FetchDailyBrokerRankAsync(
        BrokerBranch branch,
        DateOnly tradeDate,
        CancellationToken cancellationToken = default)
    {
        var quantityPage = await FetchRankPageAsync(branch.MajorId, branch.BranchId, "E", tradeDate, cancellationToken);
        var result = new WantGooFetchResult
        {
            LastDate = quantityPage.PageDate,
            AgentCount = 1
        };
        result.Branches.Add(branch);

        if (quantityPage.PageDate != tradeDate)
        {
            result.Warnings.Add($"Fubon returned {quantityPage.PageDate:yyyy-MM-dd} for requested {tradeDate:yyyy-MM-dd}.");
            return result;
        }

        var amountPage = await FetchRankPageAsync(branch.MajorId, branch.BranchId, "B", tradeDate, cancellationToken);
        var amountRows = amountPage.Rows.ToDictionary(row => row.StockNo, StringComparer.OrdinalIgnoreCase);
        foreach (var quantityRow in quantityPage.Rows)
        {
            amountRows.TryGetValue(quantityRow.StockNo, out var amountRow);
            var totalQty = quantityRow.BuyValue + quantityRow.SellValue;
            var totalAmount = (amountRow?.BuyValue ?? 0) + (amountRow?.SellValue ?? 0);
            var averagePrice = totalQty > 0 && totalAmount > 0
                ? Math.Round(totalAmount / totalQty, 2)
                : 0;

            result.Stocks.Add(new StockOption
            {
                StockNo = quantityRow.StockNo,
                StockName = quantityRow.StockName
            });
            result.Trades.Add(new BrokerTradeRecord
            {
                TradeDate = tradeDate,
                PeriodDays = 1,
                StockNo = quantityRow.StockNo,
                StockName = quantityRow.StockName,
                MajorId = branch.MajorId,
                MajorName = branch.MajorName,
                BranchId = branch.BranchId,
                BranchName = branch.BranchName,
                BuyQty = quantityRow.BuyValue,
                SellQty = quantityRow.SellValue,
                NetQty = quantityRow.BuyValue - quantityRow.SellValue,
                Amount = totalAmount,
                AvgPrice = averagePrice,
                Source = SourceName
            });
        }

        return result;
    }

    public async Task<WantGooFetchResult> FetchStockBranchTradesAsync(
        string stockNo,
        string stockName,
        DateOnly tradeDate,
        decimal? closePrice = null,
        CancellationToken cancellationToken = default)
    {
        var query = $"a={Uri.EscapeDataString(stockNo)}&e={tradeDate:yyyy-MM-dd}&f={tradeDate:yyyy-MM-dd}";
        var html = await GetBig5StringAsync($"/z/zc/zco/zco.djhtm?{query}", cancellationToken);
        var pageDate = ReadLastUpdateDate(html);
        var result = new WantGooFetchResult
        {
            LastDate = pageDate,
            AgentCount = 1
        };
        result.Stocks.Add(new StockOption { StockNo = stockNo, StockName = stockName });

        if (pageDate != tradeDate)
        {
            result.Warnings.Add($"Fubon stock page returned {pageDate:yyyy-MM-dd} for requested {tradeDate:yyyy-MM-dd}.");
            return result;
        }

        var price = closePrice ?? 0;
        foreach (var row in ParseStockBranchRows(html))
        {
            result.Branches.Add(new BrokerBranch
            {
                MajorId = row.MajorId,
                MajorName = row.MajorName,
                BranchId = row.BranchId,
                BranchName = row.BranchName,
                Notes = StockSourceName
            });
            result.Trades.Add(new BrokerTradeRecord
            {
                TradeDate = tradeDate,
                PeriodDays = 1,
                StockNo = stockNo,
                StockName = stockName,
                MajorId = row.MajorId,
                MajorName = row.MajorName,
                BranchId = row.BranchId,
                BranchName = row.BranchName,
                BuyQty = row.BuyQty,
                SellQty = row.SellQty,
                NetQty = row.BuyQty - row.SellQty,
                Amount = Math.Round((row.BuyQty + row.SellQty) * price, 2),
                AvgPrice = price,
                ClosePrice = closePrice,
                Source = StockSourceName
            });
        }

        return result;
    }

    public async Task<WantGooFetchResult> FetchBrokerStockDetailAsync(
        BrokerBranch branch,
        string stockNo,
        string stockName,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var query =
            $"a={Uri.EscapeDataString(stockNo)}" +
            $"&BHID={Uri.EscapeDataString(branch.MajorId)}" +
            $"&b={Uri.EscapeDataString(branch.BranchId)}" +
            $"&C=1&D={from:yyyy-MM-dd}&E={to:yyyy-MM-dd}&ver=V3";
        var html = await GetBig5StringAsync($"/z/zc/zco/zco0/zco0.djhtm?{query}", cancellationToken);
        var result = new WantGooFetchResult
        {
            AgentCount = 1
        };
        result.Branches.Add(branch);
        result.Stocks.Add(new StockOption { StockNo = stockNo, StockName = stockName });

        foreach (var row in ParseBrokerStockDetailRows(html))
        {
            if (row.TradeDate < from || row.TradeDate > to)
            {
                continue;
            }

            result.Trades.Add(new BrokerTradeRecord
            {
                TradeDate = row.TradeDate,
                PeriodDays = 1,
                StockNo = stockNo,
                StockName = stockName,
                MajorId = branch.MajorId,
                MajorName = branch.MajorName,
                BranchId = branch.BranchId,
                BranchName = branch.BranchName,
                BuyQty = row.BuyQty,
                SellQty = row.SellQty,
                NetQty = row.NetQty,
                Amount = 0,
                AvgPrice = 0,
                Source = BrokerStockDetailSourceName
            });
        }

        result.LastDate = result.Trades.Count == 0 ? null : result.Trades.Max(trade => trade.TradeDate);
        return result;
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

    private async Task<RankPage> FetchRankPageAsync(
        string majorId,
        string branchId,
        string kind,
        DateOnly? tradeDate,
        CancellationToken cancellationToken)
    {
        var query = tradeDate.HasValue
            ? $"a={Uri.EscapeDataString(majorId)}&b={Uri.EscapeDataString(branchId)}&c={kind}&e={tradeDate.Value:yyyy-MM-dd}&f={tradeDate.Value:yyyy-MM-dd}"
            : $"a={Uri.EscapeDataString(majorId)}&b={Uri.EscapeDataString(branchId)}&c={kind}&d=1";
        var html = await GetBig5StringAsync($"/z/zg/zgb/zgb0.djhtm?{query}", cancellationToken);
        return new RankPage(ReadPageDate(html), ParseRankRows(html));
    }

    private async Task<string> GetBig5StringAsync(string path, CancellationToken cancellationToken)
    {
        var uri = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(path)
            : new Uri($"{BaseUrl}{path}");
        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return Big5Encoding.GetString(bytes);
    }

    private static List<RankRow> ParseRankRows(string html)
    {
        var rows = new List<RankRow>();
        foreach (Match rowMatch in TableRowRegex.Matches(html))
        {
            var rowHtml = rowMatch.Groups["row"].Value;
            if (!rowHtml.Contains("Link2Stk", StringComparison.OrdinalIgnoreCase) &&
                !rowHtml.Contains("GenLink2stk", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cells = TableCellRegex.Matches(rowHtml)
                .Select(match => StripHtml(match.Groups["cell"].Value))
                .Where(text => text.Length > 0)
                .ToList();
            if (cells.Count != 4)
            {
                continue;
            }

            if (!TryReadStock(rowHtml, cells[0], out var stockNo, out var stockName))
            {
                continue;
            }

            if (!TryParseDecimal(cells[1], out var buyValue) ||
                !TryParseDecimal(cells[2], out var sellValue))
            {
                continue;
            }

            rows.Add(new RankRow(stockNo, stockName, buyValue, sellValue));
        }

        return rows
            .GroupBy(row => row.StockNo, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RankRow(
                group.Key,
                group.First().StockName,
                group.Sum(row => row.BuyValue),
                group.Sum(row => row.SellValue)))
            .ToList();
    }

    private static List<StockBranchRow> ParseStockBranchRows(string html)
    {
        var rows = new List<StockBranchRow>();
        foreach (Match rowMatch in TableRowRegex.Matches(html))
        {
            var rowHtml = rowMatch.Groups["row"].Value;
            if (!rowHtml.Contains("zco0.djhtm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cellMatches = TableCellRegex.Matches(rowHtml).Cast<Match>().ToList();
            if (cellMatches.Count < 10)
            {
                continue;
            }

            AddStockBranchSide(rows, cellMatches, 0);
            AddStockBranchSide(rows, cellMatches, 5);
        }

        return rows
            .GroupBy(row => row.BranchId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new StockBranchRow(
                    first.MajorId,
                    first.MajorName,
                    first.BranchId,
                    first.BranchName,
                    group.Sum(row => row.BuyQty),
                    group.Sum(row => row.SellQty));
            })
            .ToList();
    }

    private static List<BrokerStockDetailRow> ParseBrokerStockDetailRows(string html)
    {
        var rows = new List<BrokerStockDetailRow>();
        foreach (Match rowMatch in TableRowRegex.Matches(html))
        {
            var cells = TableCellRegex.Matches(rowMatch.Groups["row"].Value)
                .Select(match => StripHtml(match.Groups["cell"].Value))
                .Where(text => text.Length > 0)
                .ToList();
            if (cells.Count != 5 ||
                !DateOnly.TryParse(cells[0], out var tradeDate) ||
                !TryParseDecimal(cells[1], out var buyQty) ||
                !TryParseDecimal(cells[2], out var sellQty) ||
                !TryParseDecimal(cells[4], out var netQty))
            {
                continue;
            }

            rows.Add(new BrokerStockDetailRow(tradeDate, buyQty, sellQty, netQty));
        }

        return rows;
    }

    private static void AddStockBranchSide(List<StockBranchRow> rows, IReadOnlyList<Match> cells, int offset)
    {
        var branchCellHtml = cells[offset].Groups["cell"].Value;
        var branchText = StripHtml(branchCellHtml);
        if (!TryReadBranch(branchCellHtml, branchText, out var majorId, out var majorName, out var branchId, out var branchName))
        {
            return;
        }

        if (!TryParseDecimal(StripHtml(cells[offset + 1].Groups["cell"].Value), out var buyQty) ||
            !TryParseDecimal(StripHtml(cells[offset + 2].Groups["cell"].Value), out var sellQty))
        {
            return;
        }

        rows.Add(new StockBranchRow(majorId, majorName, branchId, branchName, buyQty, sellQty));
    }

    private static bool TryReadStock(string rowHtml, string firstCellText, out string stockNo, out string stockName)
    {
        var scriptMatch = ScriptLinkRegex.Match(rowHtml);
        if (scriptMatch.Success)
        {
            stockNo = scriptMatch.Groups["stock"].Value.Trim();
            stockName = WebUtility.HtmlDecode(scriptMatch.Groups["name"].Value.Trim());
            return stockNo.Length > 0;
        }

        var anchorMatch = AnchorLinkRegex.Match(rowHtml);
        if (!anchorMatch.Success)
        {
            stockNo = "";
            stockName = "";
            return false;
        }

        stockNo = anchorMatch.Groups["stock"].Value.Trim();
        stockName = firstCellText.StartsWith(stockNo, StringComparison.OrdinalIgnoreCase)
            ? firstCellText[stockNo.Length..].Trim()
            : firstCellText.Trim();
        return stockNo.Length > 0;
    }

    private static DateOnly? ReadPageDate(string html)
    {
        var match = PageDateRegex.Match(html);
        return match.Success &&
            DateOnly.TryParseExact(match.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static DateOnly? ReadLastUpdateDate(string html)
    {
        var match = LastUpdateDateRegex.Match(html);
        return match.Success &&
            DateOnly.TryParse(match.Groups["date"].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static bool TryReadBranch(
        string cellHtml,
        string branchText,
        out string majorId,
        out string majorName,
        out string branchId,
        out string branchName)
    {
        majorId = "";
        majorName = "";
        branchId = "";
        branchName = "";

        var match = BranchLinkRegex.Match(cellHtml);
        if (!match.Success)
        {
            return false;
        }

        var query = WebUtility.HtmlDecode(match.Groups["query"].Value);
        branchId = ReadQueryValue(query, "b");
        majorId = ReadQueryValue(query, "BHID");
        if (string.IsNullOrWhiteSpace(branchId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(majorId))
        {
            majorId = branchId;
        }

        var dashIndex = branchText.IndexOf('-');
        if (dashIndex > 0)
        {
            majorName = branchText[..dashIndex].Trim();
            branchName = branchText[(dashIndex + 1)..].Trim();
        }
        else
        {
            majorName = branchText.Trim();
        }

        return true;
    }

    private static string ReadQueryValue(string query, string name)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(parts[1]).Trim();
            }
        }

        return "";
    }

    private static (string Id, string Name) ParseBrokerListEntry(string text)
    {
        var commaIndex = text.IndexOf(',');
        if (commaIndex <= 0)
        {
            return ("", "");
        }

        return (text[..commaIndex].Trim(), WebUtility.HtmlDecode(text[(commaIndex + 1)..].Trim()));
    }

    private static string NormalizeBranchName(string name, string majorName, string branchId, string majorId)
    {
        if (branchId.Equals(majorId, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(majorName, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var prefix = $"{majorName}-";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            ? name[prefix.Length..].Trim()
            : name;
    }

    private static string StripHtml(string html)
    {
        var withoutScript = ScriptBlockRegex.Replace(html, " ");
        var withoutTags = HtmlTagRegex.Replace(withoutScript, " ");
        return WhitespaceRegex.Replace(WebUtility.HtmlDecode(withoutTags), " ").Trim();
    }

    private static bool TryParseDecimal(string text, out decimal value)
    {
        return decimal.TryParse(
            text.Replace(",", "", StringComparison.Ordinal).Trim(),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out value);
    }

    private sealed record RankPage(DateOnly? PageDate, List<RankRow> Rows);

    private sealed record RankRow(string StockNo, string StockName, decimal BuyValue, decimal SellValue);

    private sealed record StockBranchRow(
        string MajorId,
        string MajorName,
        string BranchId,
        string BranchName,
        decimal BuyQty,
        decimal SellQty);

    private sealed record BrokerStockDetailRow(DateOnly TradeDate, decimal BuyQty, decimal SellQty, decimal NetQty);
}
