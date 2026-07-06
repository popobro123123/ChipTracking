using ChipTracking.Data;
using ChipTracking.Services;
using Microsoft.Web.WebView2.WinForms;

namespace ChipTracking;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            return RunSelfTest();
        }

        if (args.Contains("--wantgoo-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            return RunWantGooSmokeTest();
        }

        if (args.Contains("--wantgoo-rank-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            return RunWantGooRankSmokeTest();
        }

        if (args.Contains("--fubon-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            return RunFubonSmokeTest();
        }

        if (args.Contains("--fubon-stock-backfill", StringComparer.OrdinalIgnoreCase))
        {
            return RunFubonStockBackfill(args);
        }

        ApplicationConfiguration.Initialize();

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChipTracking");
        Directory.CreateDirectory(dataDirectory);

        using var database = new ChipDatabase(Path.Combine(dataDirectory, "chiptracking.db"));
        database.Initialize();

        var analysisService = new AnalysisService(database);
        Application.Run(new MainForm(database, analysisService));
        return 0;
    }

    private static int RunSelfTest()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), "ChipTrackingSelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            using var database = new ChipDatabase(Path.Combine(testDirectory, "test.db"));
            database.Initialize();
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                database.UpsertTrade(connection, new BrokerTradeRecord
                {
                    TradeDate = new DateOnly(2026, 7, 1),
                    StockNo = "2330",
                    StockName = "台積電",
                    MajorId = "9800",
                    MajorName = "元大",
                    BranchId = "9801",
                    BranchName = "測試分點",
                    BuyQty = 100,
                    SellQty = 0,
                    NetQty = 100,
                    AvgPrice = 100,
                    Source = "self-test"
                });
                database.UpsertTrade(connection, new BrokerTradeRecord
                {
                    TradeDate = new DateOnly(2026, 7, 2),
                    StockNo = "2330",
                    StockName = "台積電",
                    MajorId = "9800",
                    MajorName = "元大",
                    BranchId = "9801",
                    BranchName = "測試分點",
                    BuyQty = 0,
                    SellQty = 100,
                    NetQty = -100,
                    AvgPrice = 110,
                    Source = "self-test"
                });
                transaction.Commit();
            }

            var analysis = new AnalysisService(database).Analyze("2330", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 5));
            if (analysis.Performances.Count != 1 ||
                analysis.UnifiedRows.Count != 1 ||
                analysis.Performances[0].WinCount != 1 ||
                analysis.Performances[0].EstimatedPnl <= 0)
            {
                return 2;
            }

            return 0;
        }
        catch
        {
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(testDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static int RunWantGooSmokeTest()
    {
        ApplicationConfiguration.Initialize();
        var exitCode = 1;
        var userDataFolder = Path.Combine(Path.GetTempPath(), "ChipTrackingWebView2Smoke", Guid.NewGuid().ToString("N"));
        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Size = new Size(80, 80),
            Opacity = 0
        };
        using var webView = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(webView);

        form.Load += async (_, _) =>
        {
            try
            {
                var scraper = new WantGooBrowserScraper(webView, userDataFolder);
                var stocks = await scraper.GetStockCatalogAsync();
                if (stocks.Count == 0)
                {
                    exitCode = 2;
                    return;
                }

                var today = DateOnly.FromDateTime(DateTime.Today);
                var fetchResult = await scraper.FetchStockBranchTradesAsync("2330", today.AddDays(-45), today, maxAgents: 1);
                exitCode = fetchResult.AgentCount > 0 ? 0 : 3;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "ChipTrackingWantGooSmoke.log"), ex.ToString());
                }
                catch
                {
                    // Best-effort diagnostics only.
                }

                exitCode = 1;
            }
            finally
            {
                form.Close();
            }
        };

        Application.Run(form);
        try
        {
            Directory.Delete(userDataFolder, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }

        return exitCode;
    }

    private static int RunWantGooRankSmokeTest()
    {
        ApplicationConfiguration.Initialize();
        var exitCode = 1;
        var userDataFolder = Path.Combine(Path.GetTempPath(), "ChipTrackingWebView2RankSmoke", Guid.NewGuid().ToString("N"));
        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Size = new Size(80, 80),
            Opacity = 0
        };
        using var webView = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(webView);

        form.Load += async (_, _) =>
        {
            try
            {
                var scraper = new WantGooBrowserScraper(webView, userDataFolder);
                var stocks = await scraper.GetStockCatalogAsync();
                var stockNames = stocks
                    .GroupBy(stock => stock.StockNo)
                    .ToDictionary(group => group.Key, group => group.First().StockName);
                var catalog = await scraper.GetBrokerBranchCatalogAsync();
                var branch = catalog.Branches.FirstOrDefault(x => x.MajorId == "9800" && x.BranchName.Contains("松江", StringComparison.Ordinal));
                branch ??= catalog.Branches.FirstOrDefault(x => x.MajorId == "9800");
                if (branch is null)
                {
                    exitCode = 2;
                    return;
                }

                var fetchResult = await scraper.FetchBrokerBuySellRankAsync(branch, catalog.TradeDate, stockNames);
                exitCode = fetchResult.Trades.Count > 0 ? 0 : 3;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "ChipTrackingWantGooRankSmoke.log"), ex.ToString());
                }
                catch
                {
                    // Best-effort diagnostics only.
                }

                exitCode = 1;
            }
            finally
            {
                form.Close();
            }
        };

        Application.Run(form);
        try
        {
            Directory.Delete(userDataFolder, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }

        return exitCode;
    }

    private static int RunFubonSmokeTest()
    {
        try
        {
            using var scraper = new FubonMoneyDjScraper();
            var branches = scraper.GetBrokerBranchesAsync().GetAwaiter().GetResult();
            if (branches.Count == 0)
            {
                return 2;
            }

            var latestTradeDate = scraper.GetLatestTradeDateAsync().GetAwaiter().GetResult();
            var branch = branches.FirstOrDefault(x =>
                x.MajorId.Equals("9800", StringComparison.OrdinalIgnoreCase) &&
                x.BranchId.Equals("9800", StringComparison.OrdinalIgnoreCase)) ?? branches[0];
            var result = scraper.FetchDailyBrokerRankAsync(branch, latestTradeDate).GetAwaiter().GetResult();
            var stockResult = scraper.FetchStockBranchTradesAsync("1101", "台泥", latestTradeDate).GetAwaiter().GetResult();
            var detailResult = scraper.FetchBrokerStockDetailAsync(branch, "1101", "台泥", latestTradeDate.AddDays(-45), latestTradeDate).GetAwaiter().GetResult();
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "ChipTrackingFubonSmoke.log"),
                    $"branches={branches.Count}, latest={latestTradeDate:yyyy-MM-dd}, branch={branch.DisplayName}, trades={result.Trades.Count}, stock1101={stockResult.Trades.Count}, detail1101={detailResult.Trades.Count}");
            }
            catch
            {
                // Best-effort diagnostics only.
            }

            return result.Trades.Count > 0 && stockResult.Trades.Count > 0 && detailResult.Trades.Count > 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "ChipTrackingFubonSmoke.log"), ex.ToString());
            }
            catch
            {
                // Best-effort diagnostics only.
            }

            return 1;
        }
    }

    private static int RunFubonStockBackfill(string[] args)
    {
        try
        {
            var optionIndex = Array.FindIndex(args, arg => arg.Equals("--fubon-stock-backfill", StringComparison.OrdinalIgnoreCase));
            var stockNo = optionIndex >= 0 && optionIndex + 1 < args.Length ? args[optionIndex + 1] : "";
            if (string.IsNullOrWhiteSpace(stockNo))
            {
                return 2;
            }

            var from = optionIndex + 2 < args.Length && DateOnly.TryParse(args[optionIndex + 2], out var parsedFrom)
                ? parsedFrom
                : DateOnly.FromDateTime(DateTime.Today.AddYears(-3));
            var to = optionIndex + 3 < args.Length && DateOnly.TryParse(args[optionIndex + 3], out var parsedTo)
                ? parsedTo
                : DateOnly.FromDateTime(DateTime.Today);
            var stockName = optionIndex + 4 < args.Length ? args[optionIndex + 4] : "";

            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChipTracking");
            Directory.CreateDirectory(dataDirectory);

            using var database = new ChipDatabase(Path.Combine(dataDirectory, "chiptracking.db"));
            database.Initialize();
            using var scraper = new FubonMoneyDjScraper();

            var latestTradeDate = scraper.GetLatestTradeDateAsync().GetAwaiter().GetResult();
            var effectiveTo = DateOnly.FromDayNumber(Math.Min(to.DayNumber, latestTradeDate.DayNumber));
            var dates = EnumerateWeekdays(from, effectiveTo)
                .Where(date => !database.IsPrefetchComplete(GetFubonStockDateKey(stockNo, date), date, date))
                .OrderByDescending(date => date)
                .ToList();

            var nextIndex = -1;
            var importedRows = 0;
            var failed = 0;
            var saveLock = new object();
            Parallel.ForEach(
                Enumerable.Range(0, Math.Min(8, Math.Max(1, dates.Count))),
                workerIndex =>
                {
                    while (true)
                    {
                        var index = Interlocked.Increment(ref nextIndex);
                        if (index >= dates.Count)
                        {
                            return;
                        }

                        var tradeDate = dates[index];
                        try
                        {
                            var result = scraper.FetchStockBranchTradesAsync(stockNo, stockName, tradeDate).GetAwaiter().GetResult();
                            lock (saveLock)
                            {
                                SaveFetchResult(database, result, "fubon-stock-backfill");
                                database.UpsertPrefetchState(
                                    GetFubonStockDateKey(stockNo, tradeDate),
                                    tradeDate,
                                    tradeDate,
                                    "ok",
                                    $"stock={stockNo}, trades={result.Trades.Count}, worker={workerIndex + 1}",
                                    result.Trades.Count);
                            }

                            Interlocked.Add(ref importedRows, result.Trades.Count);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref failed);
                            lock (saveLock)
                            {
                                database.UpsertPrefetchState(GetFubonStockDateKey(stockNo, tradeDate), tradeDate, tradeDate, "failed", ex.Message, 0);
                                database.AddSyncRun("fubon-stock-backfill", "failed", $"{stockNo} {tradeDate:yyyy-MM-dd}: {ex.Message}", 0);
                            }
                        }
                    }
                });

            database.UpsertPrefetchState(
                GetFubonStockSummaryKey(stockNo),
                from,
                effectiveTo,
                failed == 0 ? "ok" : "partial",
                $"stock={stockNo}, dates={dates.Count}, failed={failed}, rows={importedRows}",
                importedRows);

            return failed == 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "ChipTrackingFubonStockBackfill.log"), ex.ToString());
            }
            catch
            {
                // Best-effort diagnostics only.
            }

            return 1;
        }
    }

    private static void SaveFetchResult(ChipDatabase database, WantGooFetchResult fetchResult, string syncSource)
    {
        using (var connection = database.OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            foreach (var branch in fetchResult.Branches)
            {
                database.UpsertBranch(connection, branch);
            }

            foreach (var trade in fetchResult.Trades)
            {
                database.UpsertTrade(connection, trade);
            }

            transaction.Commit();
        }

        database.AddSyncRun(syncSource, "ok", $"trades={fetchResult.Trades.Count}", fetchResult.Trades.Count);
    }

    private static string GetFubonStockSummaryKey(string stockNo)
    {
        return $"fubon-stock:{stockNo}";
    }

    private static string GetFubonStockDateKey(string stockNo, DateOnly tradeDate)
    {
        return $"fubon-stock:{stockNo}:{tradeDate:yyyyMMdd}";
    }

    private static IEnumerable<DateOnly> EnumerateWeekdays(DateOnly from, DateOnly to)
    {
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                yield return date;
            }
        }
    }
}
