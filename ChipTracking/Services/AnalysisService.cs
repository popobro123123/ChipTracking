using ChipTracking.Data;

namespace ChipTracking.Services;

public sealed class AnalysisService
{
    private const decimal PositionEpsilon = 0.0001m;
    private readonly ChipDatabase _database;

    public AnalysisService(ChipDatabase database)
    {
        _database = database;
    }

    public AnalysisResult Analyze(string stockNo, DateOnly from, DateOnly to)
    {
        var trades = _database.GetTrades(stockNo, from, to, periodDays: 1);
        var branches = _database.GetBranches().ToDictionary(x => x.BranchKey, x => x);
        var company = _database.GetCompanyLocation(stockNo);

        var performances = BuildPerformances(trades, branches, company);
        var holderRanks = BuildHolderRanks(performances);
        var costDistribution = BuildCostDistribution(trades);
        var campSignals = BuildCampSignals(trades, branches, company);

        return new AnalysisResult
        {
            UnifiedRows = BuildUnifiedRows(stockNo, trades, performances, campSignals),
            Performances = performances,
            HolderRanks = holderRanks,
            CostDistribution = costDistribution,
            DayTradeSignals = [],
            CampSignals = campSignals
        };
    }

    private static IReadOnlyList<BranchPerformance> BuildPerformances(
        IReadOnlyList<BrokerTradeRecord> trades,
        Dictionary<string, BrokerBranch> branches,
        CompanyLocation? company)
    {
        var rows = new List<BranchPerformance>();
        foreach (var group in trades.GroupBy(x => x.BranchKey))
        {
            var ordered = group.OrderBy(x => x.TradeDate).ThenBy(x => x.Id).ToList();
            decimal openQty = 0;
            decimal openCost = 0;
            decimal estimatedPnl = 0;
            var winCount = 0;
            var lossCount = 0;

            foreach (var record in ordered)
            {
                var netQty = record.NetQty;
                if (netQty > 0)
                {
                    openCost += netQty * record.AvgPrice;
                    openQty += netQty;
                    continue;
                }

                if (netQty >= 0 || openQty <= PositionEpsilon)
                {
                    continue;
                }

                var sellQty = Math.Min(openQty, Math.Abs(netQty));
                var averageCost = openCost / openQty;
                var pnl = (record.AvgPrice - averageCost) * sellQty;
                estimatedPnl += pnl;

                if (pnl > 0)
                {
                    winCount++;
                }
                else if (pnl < 0)
                {
                    lossCount++;
                }

                openQty -= sellQty;
                openCost -= averageCost * sellQty;
                if (openQty <= PositionEpsilon)
                {
                    openQty = 0;
                    openCost = 0;
                }
            }

            var branch = branches.GetValueOrDefault(group.Key);
            var isLocal = IsLocalBroker(branch, company);
            var totalClosedTrades = winCount + lossCount;

            rows.Add(new BranchPerformance
            {
                Branch = ordered[0].BranchDisplayName,
                OpenQty = openQty,
                AverageCost = openQty > PositionEpsilon ? openCost / openQty : 0,
                EstimatedPnl = estimatedPnl,
                WinCount = winCount,
                LossCount = lossCount,
                WinRate = totalClosedTrades == 0 ? 0 : (decimal)winCount / totalClosedTrades,
                InferredCamp = InferCamp(branch?.CampTag, isLocal)
            });
        }

        return rows
            .OrderByDescending(x => x.OpenQty)
            .ThenByDescending(x => x.EstimatedPnl)
            .ToList();
    }

    private static IReadOnlyList<HolderRank> BuildHolderRanks(IReadOnlyList<BranchPerformance> performances)
    {
        return performances
            .Where(x => x.OpenQty > PositionEpsilon)
            .OrderByDescending(x => x.OpenQty)
            .Select((x, index) => new HolderRank
            {
                Rank = index + 1,
                Branch = x.Branch,
                PositionQty = x.OpenQty,
                AverageCost = x.AverageCost
            })
            .ToList();
    }

    private static IReadOnlyList<CostDistributionRow> BuildCostDistribution(IReadOnlyList<BrokerTradeRecord> trades)
    {
        var rows = new List<CostDistributionRow>();
        foreach (var group in trades.GroupBy(x => x.BranchKey))
        {
            decimal position = 0;
            foreach (var record in group.OrderBy(x => x.TradeDate).ThenBy(x => x.Id))
            {
                var before = position;
                position += record.NetQty;
                rows.Add(new CostDistributionRow
                {
                    TradeDate = record.TradeDate.ToString("yyyy-MM-dd"),
                    Branch = record.BranchDisplayName,
                    BuyQty = record.BuyQty,
                    SellQty = record.SellQty,
                    NetQty = record.NetQty,
                    AvgPrice = record.AvgPrice,
                    RunningPosition = position,
                    Action = DescribeAction(before, position, record.NetQty)
                });
            }
        }

        return rows
            .OrderByDescending(x => x.TradeDate)
            .ThenBy(x => x.Branch)
            .ToList();
    }

    private static IReadOnlyList<CampSignal> BuildCampSignals(
        IReadOnlyList<BrokerTradeRecord> trades,
        Dictionary<string, BrokerBranch> branches,
        CompanyLocation? company)
    {
        var rows = new List<CampSignal>();
        foreach (var group in trades.GroupBy(x => x.BranchKey))
        {
            var branch = branches.GetValueOrDefault(group.Key);
            var isLocal = IsLocalBroker(branch, company);
            rows.Add(new CampSignal
            {
                Branch = group.First().BranchDisplayName,
                ManualTag = branch?.CampTag ?? "未知",
                IsLocalBroker = isLocal,
                InferredCamp = InferCamp(branch?.CampTag, isLocal)
            });
        }

        return rows
            .OrderByDescending(x => x.IsLocalBroker)
            .ThenBy(x => x.Branch)
            .ToList();
    }

    private static IReadOnlyList<UnifiedBranchRow> BuildUnifiedRows(
        string stockNo,
        IReadOnlyList<BrokerTradeRecord> trades,
        IReadOnlyList<BranchPerformance> performances,
        IReadOnlyList<CampSignal> campSignals)
    {
        var campByBranch = campSignals.ToDictionary(x => x.Branch);
        var tradeDateByBranch = trades
            .GroupBy(x => x.BranchDisplayName)
            .ToDictionary(
                group => group.Key,
                group => group.Max(x => x.TradeDate).ToString("yyyy-MM-dd"));
        var stockDisplay = trades.FirstOrDefault() is { } firstTrade
            ? string.IsNullOrWhiteSpace(firstTrade.StockName) ? firstTrade.StockNo : $"{firstTrade.StockNo} {firstTrade.StockName}"
            : stockNo;

        return performances
            .Select((performance, index) =>
            {
                campByBranch.TryGetValue(performance.Branch, out var camp);
                return new UnifiedBranchRow
                {
                    Rank = index + 1,
                    Stock = stockDisplay,
                    TradeDate = tradeDateByBranch.GetValueOrDefault(performance.Branch, ""),
                    Branch = performance.Branch,
                    PositionQty = performance.OpenQty,
                    AverageCost = performance.AverageCost,
                    EstimatedPnl = performance.EstimatedPnl,
                    PnlStatus = performance.PnlStatus,
                    WinCount = performance.WinCount,
                    LossCount = performance.LossCount,
                    WinRateText = performance.WinRateText,
                    InferredCamp = camp?.InferredCamp ?? performance.InferredCamp,
                    LocalBrokerText = camp?.LocalBrokerText ?? "否"
                };
            })
            .OrderByDescending(x => x.PositionQty)
            .ThenByDescending(x => x.EstimatedPnl)
            .ToList();
    }

    private static string DescribeAction(decimal before, decimal after, decimal netQty)
    {
        if (before <= PositionEpsilon && after > PositionEpsilon)
        {
            return "開倉";
        }

        if (netQty > 0 && after > before)
        {
            return "加碼";
        }

        if (netQty < 0 && after > PositionEpsilon)
        {
            return "減碼";
        }

        if (before > PositionEpsilon && after <= PositionEpsilon)
        {
            return "出清";
        }

        return netQty >= 0 ? "買進" : "賣出";
    }

    private static bool IsLocalBroker(BrokerBranch? branch, CompanyLocation? company)
    {
        if (branch is null || company is null)
        {
            return false;
        }

        var companyKeys = ExtractRegionKeys($"{company.CompanyName} {company.Address}");
        if (companyKeys.Count == 0)
        {
            return false;
        }

        var branchKeys = ExtractRegionKeys($"{branch.MajorName} {branch.BranchName} {branch.Address}");
        return branchKeys.Overlaps(companyKeys);
    }

    private static HashSet<string> ExtractRegionKeys(string text)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return keys;
        }

        foreach (var group in RegionAliasGroups)
        {
            if (group.Any(alias => text.Contains(alias, StringComparison.Ordinal)))
            {
                foreach (var alias in group)
                {
                    keys.Add(alias);
                }
            }
        }

        return keys;
    }

    private static string InferCamp(string? manualTag, bool isLocal)
    {
        if (!string.IsNullOrWhiteSpace(manualTag) && manualTag != "未知")
        {
            return manualTag;
        }

        return isLocal ? "公司派" : "市場派/未知";
    }

    private static readonly string[][] RegionAliasGroups =
    [
        ["竹科", "新竹科學園區", "新竹", "竹北", "竹東", "寶山", "湖口", "竹南"],
        ["南科", "台南科學園區", "臺南科學園區", "台南", "臺南", "善化", "新市", "安南"],
        ["中科", "中部科學園區", "台中", "臺中", "西屯", "大雅", "后里"],
        ["內科", "內湖科學園區", "內湖", "南港"],
        ["汐止", "新台五", "新臺五"],
        ["台北", "臺北", "松山", "信義", "大安", "中山", "中正", "敦南"],
        ["新北", "板橋", "三重", "新莊", "中和", "永和", "土城"],
        ["桃園", "中壢", "龜山", "蘆竹"],
        ["苗栗", "頭份"],
        ["彰化", "員林"],
        ["雲林", "斗六"],
        ["嘉義"],
        ["高雄", "左營", "前鎮", "楠梓"],
        ["屏東"],
        ["宜蘭"],
        ["花蓮"],
        ["台東", "臺東"]
    ];
}
