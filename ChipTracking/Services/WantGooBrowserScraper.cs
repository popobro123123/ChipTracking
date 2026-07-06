using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ChipTracking.Services;

public sealed class WantGooBrowserScraper
{
    private const string BaseUrl = "https://www.wantgoo.com";
    private const int MaxQueryDays = 30;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly WebView2 _webView;
    private readonly string _userDataFolder;
    private bool _initialized;
    private bool _brokerRankPageReady;

    public WantGooBrowserScraper(WebView2 webView, string userDataFolder)
    {
        _webView = webView;
        _userDataFolder = userDataFolder;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(_userDataFolder);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
        await _webView.EnsureCoreWebView2Async(environment);
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _initialized = true;

        await NavigateAsync($"{BaseUrl}/", cancellationToken);
    }

    public async Task<IReadOnlyList<StockOption>> GetStockCatalogAsync(CancellationToken cancellationToken = default)
    {
        await EnsureWantGooOriginAsync(cancellationToken);
        var response = await FetchAsync("/investrue/all-alive", cancellationToken);
        if (!response.Ok)
        {
            throw new InvalidOperationException($"WantGoo 股票清單取得失敗：HTTP {response.Status} {response.StatusText}");
        }

        using var document = JsonDocument.Parse(response.Text);
        var data = GetDataElement(document.RootElement);
        var stocks = new List<StockOption>();
        foreach (var item in data.EnumerateArray())
        {
            var id = GetString(item, "id", "stockNo", "investrueId").Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            stocks.Add(new StockOption
            {
                StockNo = id,
                StockName = GetString(item, "name", "stockName").Trim()
            });
        }

        return stocks
            .GroupBy(x => x.StockNo, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.StockNo)
            .ToList();
    }

    public async Task<WantGooBrokerBranchCatalog> GetBrokerBranchCatalogAsync(CancellationToken cancellationToken = default)
    {
        await EnsureBrokerRankPageAsync(cancellationToken);
        using var document = await FetchJsonDocumentAsync(
            "/stock/major-investors/branches-data",
            "WantGoo 券商分點清單",
            cancellationToken);

        var catalog = new WantGooBrokerBranchCatalog
        {
            TradeDate = ReadLastDate(document.RootElement) ?? DateOnly.FromDateTime(DateTime.Today)
        };

        var data = GetDataElement(document.RootElement);
        foreach (var major in data.EnumerateArray())
        {
            var majorId = GetString(major, "id", "majorId").Trim();
            var majorName = GetString(major, "name", "majorName").Trim();
            if (string.IsNullOrWhiteSpace(majorId))
            {
                continue;
            }

            if (!TryGetProperty(major, out var branchesElement, "branches") ||
                branchesElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var branch in branchesElement.EnumerateArray())
            {
                var branchId = GetString(branch, "id", "branchId").Trim();
                if (string.IsNullOrWhiteSpace(branchId))
                {
                    continue;
                }

                catalog.Branches.Add(new BrokerBranch
                {
                    MajorId = majorId,
                    MajorName = majorName,
                    BranchId = branchId,
                    BranchName = GetString(branch, "name", "branchName").Trim(),
                    Notes = "WantGoo broker-buy-sell-rank"
                });
            }
        }

        return catalog;
    }

    public async Task<WantGooFetchResult> FetchBrokerBuySellRankAsync(
        BrokerBranch branch,
        DateOnly tradeDate,
        IReadOnlyDictionary<string, string> stockNames,
        CancellationToken cancellationToken = default)
    {
        await EnsureBrokerRankPageAsync(cancellationToken);
        var query = new List<string>
        {
            "during=1",
            $"majorId={Uri.EscapeDataString(branch.MajorId)}",
            "orderBy=count"
        };
        if (!string.IsNullOrWhiteSpace(branch.BranchId))
        {
            query.Add($"branchId={Uri.EscapeDataString(branch.BranchId)}");
        }

        var path = $"/stock/major-investors/broker-buy-sell-rank-data?{string.Join("&", query)}";
        using var document = await FetchJsonDocumentAsync(path, $"WantGoo 券商買賣超排行 {branch.DisplayName}", cancellationToken);
        var result = new WantGooFetchResult
        {
            LastDate = tradeDate,
            AgentCount = 1
        };
        result.Branches.Add(branch);

        var data = GetDataElement(document.RootElement);
        foreach (var item in data.EnumerateArray())
        {
            var stockNo = GetString(item, "stockNo", "id", "investrueId").Trim();
            if (string.IsNullOrWhiteSpace(stockNo))
            {
                continue;
            }

            var stockName = stockNames.GetValueOrDefault(stockNo);
            if (string.IsNullOrWhiteSpace(stockName))
            {
                stockName = GetString(item, "name", "stockName").Trim();
            }

            TryGetDecimal(item, out var buyQty, "buyQuantities", "buyQuantity", "buy");
            TryGetDecimal(item, out var sellQty, "sellQuantities", "sellQuantity", "sell");
            TryGetDecimal(item, out var amount, "amount", "buySellAmount");
            TryGetDecimal(item, out var avgPrice, "avgPrice", "averagePrice");

            result.Stocks.Add(new StockOption
            {
                StockNo = stockNo,
                StockName = stockName
            });
            result.Trades.Add(new BrokerTradeRecord
            {
                TradeDate = tradeDate,
                PeriodDays = 1,
                StockNo = stockNo,
                StockName = stockName,
                MajorId = branch.MajorId,
                MajorName = branch.MajorName,
                BranchId = branch.BranchId,
                BranchName = branch.BranchName,
                BuyQty = buyQty,
                SellQty = sellQty,
                NetQty = buyQty - sellQty,
                Amount = amount,
                AvgPrice = avgPrice,
                Source = "wantgoo-broker-rank"
            });
        }

        if (result.Trades.Count == 0)
        {
            result.Warnings.Add("WantGoo 券商買賣超排行沒有回傳資料。");
        }

        return result;
    }

    public async Task<WantGooFetchResult> FetchStockBranchTradesAsync(
        string stockNo,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default,
        int? maxAgents = null)
    {
        if (from > to)
        {
            throw new ArgumentException("起日不能晚於迄日。", nameof(from));
        }

        var stockPath = $"/stock/{Uri.EscapeDataString(stockNo)}/major-investors/branch-buysell";
        await NavigateAsync($"{BaseUrl}{stockPath}", cancellationToken);
        var stockName = await ReadStockNameAsync(cancellationToken);

        if (to.DayNumber - from.DayNumber + 1 > MaxQueryDays)
        {
            return await FetchStockBranchTradesByChunksAsync(stockNo, stockPath, stockName, from, to, cancellationToken, maxAgents);
        }

        return await FetchStockBranchTradesSingleRangeAsync(stockNo, stockPath, stockName, from, to, cancellationToken, maxAgents);
    }

    private async Task<WantGooFetchResult> FetchStockBranchTradesByChunksAsync(
        string stockNo,
        string stockPath,
        string stockName,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken,
        int? maxAgents)
    {
        var aggregate = new WantGooFetchResult();
        var current = from;
        var successfulChunks = 0;
        var failedChunks = 0;
        while (current <= to)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkTo = DateOnly.FromDayNumber(Math.Min(to.DayNumber, current.DayNumber + MaxQueryDays - 1));
            try
            {
                var chunk = await FetchStockBranchTradesSingleRangeAsync(stockNo, stockPath, stockName, current, chunkTo, cancellationToken, maxAgents);
                successfulChunks++;
                MergeFetchResult(aggregate, chunk);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedChunks++;
                aggregate.Warnings.Add($"{current:yyyy-MM-dd}~{chunkTo:yyyy-MM-dd} 抓取失敗：{ex.Message}");
            }

            current = chunkTo.AddDays(1);
            await Task.Delay(250, cancellationToken);
        }

        DeduplicateFetchResult(aggregate);
        if (successfulChunks == 0 && failedChunks > 0)
        {
            throw new InvalidOperationException(string.Join("；", aggregate.Warnings.Take(3)));
        }

        return aggregate;
    }

    private async Task<WantGooFetchResult> FetchStockBranchTradesSingleRangeAsync(
        string stockNo,
        string stockPath,
        string stockName,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken,
        int? maxAgents)
    {
        var result = new WantGooFetchResult();
        var begin = Uri.EscapeDataString(from.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));
        var end = Uri.EscapeDataString(to.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));
        var rankingPath = $"{stockPath}-data?isOverBuy=true&beginDate={begin}&endDate={end}";
        using var rankingDocument = await FetchJsonDocumentAsync(rankingPath, "WantGoo 分點排行", cancellationToken);
        result.LastDate = ReadLastDate(rankingDocument.RootElement);

        var agents = ReadAgents(rankingDocument.RootElement);
        if (maxAgents.HasValue)
        {
            agents = agents.Take(maxAgents.Value).ToList();
        }

        result.AgentCount = agents.Count;
        if (agents.Count == 0)
        {
            result.Warnings.Add("WantGoo 沒有回傳分點排行，可能是區間無資料、股票代號錯誤或需要登入。");
            return result;
        }

        foreach (var agent in agents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Branches.Add(new BrokerBranch
            {
                MajorId = agent.AgentId,
                MajorName = agent.AgentName,
                BranchId = agent.AgentId,
                BranchName = "",
                Notes = "WantGoo 分點"
            });

            var encodedAgentId = Uri.EscapeDataString(EncodeAgentId(agent.AgentId));
            var agentPath = $"{stockPath}-agent-data/{encodedAgentId}?beginDate={begin}&endDate={end}";
            using var agentDocument = await FetchJsonDocumentAsync(agentPath, $"WantGoo 分點日資料 {agent.AgentName}", cancellationToken);
            ReadAgentTrades(agentDocument.RootElement, stockNo, stockName, agent, from, to, result);
            await Task.Delay(120, cancellationToken);
        }

        if (result.SkippedMaskedRows > 0)
        {
            result.Warnings.Add($"有 {result.SkippedMaskedRows} 筆分點日資料被 WantGoo 遮罩，已略過未入庫。");
        }

        if (result.Trades.Count == 0 && result.Warnings.Count == 0)
        {
            result.Warnings.Add("已連到 WantGoo，但沒有取得可入庫的每日分點交易資料。");
        }

        return result;
    }

    private static void MergeFetchResult(WantGooFetchResult aggregate, WantGooFetchResult chunk)
    {
        aggregate.Trades.AddRange(chunk.Trades);
        aggregate.Branches.AddRange(chunk.Branches);
        aggregate.Stocks.AddRange(chunk.Stocks);
        aggregate.Warnings.AddRange(chunk.Warnings);
        aggregate.AgentCount += chunk.AgentCount;
        aggregate.SkippedMaskedRows += chunk.SkippedMaskedRows;
        aggregate.LastDate = MaxDate(aggregate.LastDate, chunk.LastDate);
    }

    private static void DeduplicateFetchResult(WantGooFetchResult result)
    {
        var uniqueBranches = result.Branches
            .GroupBy(x => x.BranchKey)
            .Select(x => x.First())
            .ToList();
        result.Branches.Clear();
        result.Branches.AddRange(uniqueBranches);

        var uniqueTrades = result.Trades
            .GroupBy(x => $"{x.TradeDate:yyyy-MM-dd}|{x.StockNo}|{x.MajorId}|{x.BranchId}|{x.Source}")
            .Select(x => x.First())
            .OrderBy(x => x.TradeDate)
            .ThenBy(x => x.MajorName)
            .ToList();
        result.Trades.Clear();
        result.Trades.AddRange(uniqueTrades);
    }

    private static DateOnly? MaxDate(DateOnly? left, DateOnly? right)
    {
        if (!left.HasValue)
        {
            return right;
        }

        if (!right.HasValue)
        {
            return left;
        }

        return left.Value.DayNumber >= right.Value.DayNumber ? left : right;
    }

    private async Task EnsureWantGooOriginAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (_webView.Source?.Host.Equals("www.wantgoo.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        await NavigateAsync($"{BaseUrl}/", cancellationToken);
    }

    private async Task EnsureBrokerRankPageAsync(CancellationToken cancellationToken)
    {
        if (_brokerRankPageReady &&
            _webView.Source?.Host.Equals("www.wantgoo.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        await NavigateAsync($"{BaseUrl}/stock/major-investors/broker-buy-sell-rank", cancellationToken);
        _brokerRankPageReady = true;
    }

    private async Task NavigateAsync(string url, CancellationToken cancellationToken)
    {
        await InitializeCoreOnlyAsync();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            _webView.NavigationCompleted -= Handler;
            if (args.IsSuccess)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(new InvalidOperationException($"WebView2 導頁失敗：{args.WebErrorStatus}"));
            }
        }

        _webView.NavigationCompleted += Handler;
        _webView.CoreWebView2.Navigate(url);
        await using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        await completion.Task;
        await WaitForDocumentReadyAsync(cancellationToken);
        await Task.Delay(700, cancellationToken);
    }

    private async Task InitializeCoreOnlyAsync()
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(_userDataFolder);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
        await _webView.EnsureCoreWebView2Async(environment);
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _initialized = true;
    }

    private async Task WaitForDocumentReadyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _webView.ExecuteScriptAsync("document.readyState === 'complete'");
            if (result.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(150, cancellationToken);
        }
    }

    private async Task<JsonDocument> FetchJsonDocumentAsync(string path, string operation, CancellationToken cancellationToken)
    {
        await EnsureWantGooOriginAsync(cancellationToken);
        await WaitForHttpWrapperAsync(cancellationToken);
        var pathJson = JsonSerializer.Serialize(path);
        return await ExecutePromiseJsonAsync($"$.http.get({pathJson})", operation, cancellationToken);
    }

    private async Task<BrowserFetchResponse> FetchAsync(string path, CancellationToken cancellationToken)
    {
        await EnsureWantGooOriginAsync(cancellationToken);
        await WaitForHttpWrapperAsync(cancellationToken);
        var pathJson = JsonSerializer.Serialize(path);
        try
        {
            using var document = await ExecutePromiseJsonAsync($"$.http.get({pathJson})", $"WantGoo {path}", cancellationToken);
            return new BrowserFetchResponse
            {
                Ok = true,
                Status = 200,
                Text = document.RootElement.GetRawText()
            };
        }
        catch (InvalidOperationException ex)
        {
            return new BrowserFetchResponse
            {
                Ok = false,
                Status = 0,
                StatusText = ex.Message,
                Text = ""
            };
        }
    }

    private async Task WaitForHttpWrapperAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _webView.ExecuteScriptAsync("typeof window.$ !== 'undefined' && !!$.http && typeof chrome !== 'undefined' && !!chrome.webview");
            if (result.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(150, cancellationToken);
        }

        throw new InvalidOperationException("WantGoo 前端 HTTP wrapper 尚未載入。");
    }

    private async Task<JsonDocument> ExecutePromiseJsonAsync(string expression, string operation, CancellationToken cancellationToken)
    {
        var messageId = $"ct_{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            var text = args.TryGetWebMessageAsString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            using var envelope = JsonDocument.Parse(text);
            if (!TryGetProperty(envelope.RootElement, out var idElement, "id") ||
                !messageId.Equals(GetElementString(idElement), StringComparison.Ordinal))
            {
                return;
            }

            _webView.CoreWebView2.WebMessageReceived -= Handler;
            completion.TrySetResult(text);
        }

        _webView.CoreWebView2.WebMessageReceived += Handler;
        var idJson = JsonSerializer.Serialize(messageId);
        var script = $$"""
            (() => {
                const __chipTrackingMessageId = {{idJson}};
                const __chipTrackingSend = payload => {
                    payload.id = __chipTrackingMessageId;
                    chrome.webview.postMessage(JSON.stringify(payload));
                };
                try {
                    Promise.resolve({{expression}})
                        .then(data => __chipTrackingSend({ ok: true, data: data }))
                        .catch(error => __chipTrackingSend({
                            ok: false,
                            status: error && error.status ? error.status : 0,
                            statusText: error && error.statusText ? error.statusText : String(error),
                            responseText: error && error.responseText ? error.responseText : ""
                        }));
                } catch (error) {
                    __chipTrackingSend({
                        ok: false,
                        status: 0,
                        statusText: String(error),
                        responseText: ""
                    });
                }
            })()
            """;

        try
        {
            await _webView.ExecuteScriptAsync(script);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            var rawEnvelope = await completion.Task;
            using var envelope = JsonDocument.Parse(rawEnvelope);
            var root = envelope.RootElement;
            var ok = TryGetProperty(root, out var okElement, "ok") &&
                okElement.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                var status = TryGetProperty(root, out var statusElement, "status") ? GetElementString(statusElement) : "0";
                var statusText = TryGetProperty(root, out var statusTextElement, "statusText") ? GetElementString(statusTextElement) : "";
                var responseText = TryGetProperty(root, out var responseElement, "responseText") ? GetElementString(responseElement) : "";
                var snippet = responseText.Length > 180 ? responseText[..180] : responseText;
                throw new InvalidOperationException($"{operation}取得失敗：HTTP {status} {statusText}. {snippet}");
            }

            if (!TryGetProperty(root, out var dataElement, "data"))
            {
                return JsonDocument.Parse("null");
            }

            return JsonDocument.Parse(dataElement.GetRawText());
        }
        finally
        {
            _webView.CoreWebView2.WebMessageReceived -= Handler;
        }
    }

    private async Task<string> ReadStockNameAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await _webView.ExecuteScriptAsync("""
                (() => {
                    const name = document.querySelector("#investrue-info-1 .astock-name")?.innerText?.trim();
                    if (name) return name;
                    const title = document.title || "";
                    return title.split("-")[0].replace("券商分點進出買賣超", "").trim();
                })()
                """);
            var text = ReadScriptObject<string>(raw) ?? "";
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            await Task.Delay(200, cancellationToken);
        }

        return "";
    }

    private static List<WantGooAgent> ReadAgents(JsonElement root)
    {
        var data = GetDataElement(root);
        var agents = new List<WantGooAgent>();
        foreach (var item in data.EnumerateArray())
        {
            var agentId = GetString(item, "agentId", "id").Trim();
            var agentName = GetString(item, "agentName", "name").Trim();
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(agentName))
            {
                continue;
            }

            agents.Add(new WantGooAgent(agentId, agentName));
        }

        return agents
            .GroupBy(x => x.AgentId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    private static void ReadAgentTrades(
        JsonElement root,
        string stockNo,
        string stockName,
        WantGooAgent agent,
        DateOnly from,
        DateOnly to,
        WantGooFetchResult result)
    {
        var data = GetDataElement(root);
        foreach (var item in data.EnumerateArray())
        {
            if (!TryGetDate(item, out var tradeDate) || tradeDate < from || tradeDate > to)
            {
                continue;
            }

            if (!TryGetDecimal(item, out var buyQty, "buyQuantities", "buyQuantity", "buy") ||
                !TryGetDecimal(item, out var sellQty, "sellQuantities", "sellQuantity", "sell"))
            {
                result.SkippedMaskedRows++;
                continue;
            }

            TryGetDecimal(item, out var buyPriceAvg, "buyPriceAvg", "buyAvgPrice");
            TryGetDecimal(item, out var sellPriceAvg, "sellPriceAvg", "sellAvgPrice");
            var avgPrice = CalculateAveragePrice(buyQty, buyPriceAvg, sellQty, sellPriceAvg);

            result.Trades.Add(new BrokerTradeRecord
            {
                TradeDate = tradeDate,
                PeriodDays = 1,
                StockNo = stockNo,
                StockName = stockName,
                MajorId = agent.AgentId,
                MajorName = agent.AgentName,
                BranchId = agent.AgentId,
                BranchName = "",
                BuyQty = buyQty,
                SellQty = sellQty,
                NetQty = buyQty - sellQty,
                Amount = Math.Round((buyQty + sellQty) * avgPrice, 2),
                AvgPrice = avgPrice,
                Source = "wantgoo"
            });
        }
    }

    private static JsonElement GetDataElement(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        return TryGetProperty(root, out var data, "data") && data.ValueKind == JsonValueKind.Array
            ? data
            : root;
    }

    private static DateOnly? ReadLastDate(JsonElement root)
    {
        if (!TryGetProperty(root, out var value, "lastDate", "date"))
        {
            return null;
        }

        var text = GetElementString(value);
        return text.Length >= 10 && DateOnly.TryParse(text[..10], out var date) ? date : null;
    }

    private static bool TryGetDate(JsonElement element, out DateOnly date)
    {
        date = default;
        if (!TryGetProperty(element, out var value, "date", "tradeDate"))
        {
            return false;
        }

        var text = GetElementString(value);
        return text.Length >= 10 && DateOnly.TryParse(text[..10], out date);
    }

    private static string GetString(JsonElement element, params string[] names)
    {
        return TryGetProperty(element, out var value, names) ? GetElementString(value) : "";
    }

    private static bool TryGetDecimal(JsonElement element, out decimal value, params string[] names)
    {
        value = 0;
        if (!TryGetProperty(element, out var jsonValue, names))
        {
            return false;
        }

        if (jsonValue.ValueKind == JsonValueKind.Number && jsonValue.TryGetDecimal(out value))
        {
            return true;
        }

        var text = GetElementString(jsonValue).Replace(",", "", StringComparison.Ordinal).Trim();
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string GetElementString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.ToString()
        };
    }

    private static decimal CalculateAveragePrice(decimal buyQty, decimal buyPriceAvg, decimal sellQty, decimal sellPriceAvg)
    {
        var quantity = buyQty + sellQty;
        if (quantity > 0)
        {
            return Math.Round((buyQty * buyPriceAvg + sellQty * sellPriceAvg) / quantity, 2);
        }

        return Math.Round(Math.Max(buyPriceAvg, sellPriceAvg), 2);
    }

    private static string EncodeAgentId(string agentId)
    {
        return Regex.Replace(agentId, "[A-Z]", match => $"({match.Value})").ToLowerInvariant();
    }

    private static T? ReadScriptObject<T>(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return default;
        }

        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind == JsonValueKind.String)
        {
            var text = document.RootElement.GetString();
            if (typeof(T) == typeof(string))
            {
                return string.IsNullOrWhiteSpace(text) ? default : (T)(object)text;
            }

            return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<T>(text, JsonOptions);
        }

        return JsonSerializer.Deserialize<T>(document.RootElement.GetRawText(), JsonOptions);
    }

    private sealed record WantGooAgent(string AgentId, string AgentName);

    private sealed class BrowserFetchResponse
    {
        public bool Ok { get; set; }
        public int Status { get; set; }
        public string StatusText { get; set; } = "";
        public string Text { get; set; } = "";
    }
}
