using System.ComponentModel;

namespace ChipTracking;

public sealed class BrokerBranch
{
    public string MajorId { get; set; } = "";
    public string MajorName { get; set; } = "";
    public string BranchId { get; set; } = "";
    public string BranchName { get; set; } = "";
    public string Address { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string CampTag { get; set; } = "未知";
    public string Notes { get; set; } = "";

    public string BranchKey => string.IsNullOrWhiteSpace(BranchId) ? MajorId : BranchId;
    public string DisplayName => string.IsNullOrWhiteSpace(BranchName) ? MajorName : $"{MajorName}-{BranchName}";
}

public sealed class BrokerTradeRecord
{
    public long Id { get; set; }
    public DateOnly TradeDate { get; set; }
    public int PeriodDays { get; set; } = 1;
    public string StockNo { get; set; } = "";
    public string StockName { get; set; } = "";
    public string MajorId { get; set; } = "";
    public string MajorName { get; set; } = "";
    public string BranchId { get; set; } = "";
    public string BranchName { get; set; } = "";
    public decimal BuyQty { get; set; }
    public decimal SellQty { get; set; }
    public decimal NetQty { get; set; }
    public decimal Amount { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal? ClosePrice { get; set; }
    public string Source { get; set; } = "manual";

    public string BranchKey => string.IsNullOrWhiteSpace(BranchId) ? MajorId : BranchId;
    public string BranchDisplayName => string.IsNullOrWhiteSpace(BranchName) ? MajorName : $"{MajorName}-{BranchName}";
}

public sealed class CompanyLocation
{
    public string StockNo { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string Address { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

public sealed class InsiderEvent
{
    public long Id { get; set; }
    public string StockNo { get; set; } = "";
    public DateOnly EventDate { get; set; }
    public string HolderName { get; set; } = "";
    public decimal ChangeQty { get; set; }
    public string Source { get; set; } = "manual";
}

public sealed class StockOption
{
    public string StockNo { get; set; } = "";
    public string StockName { get; set; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(StockName) ? StockNo : $"{StockNo} {StockName}";
}

public sealed class BranchPerformance
{
    [DisplayName("分點")]
    public string Branch { get; set; } = "";

    [DisplayName("持有張數")]
    public decimal OpenQty { get; set; }

    [DisplayName("剩餘均價")]
    public decimal AverageCost { get; set; }

    [DisplayName("損益估算")]
    public decimal EstimatedPnl { get; set; }

    [DisplayName("損益狀態")]
    public string PnlStatus => EstimatedPnl > 0 ? "賺錢" : EstimatedPnl < 0 ? "賠錢" : "--";

    [DisplayName("賺錢次數")]
    public int WinCount { get; set; }

    [DisplayName("賠錢次數")]
    public int LossCount { get; set; }

    [DisplayName("勝率")]
    public string WinRateText => WinCount + LossCount == 0 ? "--" : $"{WinRate:P1}";

    public decimal WinRate { get; set; }

    [DisplayName("推定角色")]
    public string InferredCamp { get; set; } = "未知";
}

public sealed class HolderRank
{
    [DisplayName("排名")]
    public int Rank { get; set; }

    [DisplayName("分點")]
    public string Branch { get; set; } = "";

    [DisplayName("估計持有張數")]
    public decimal PositionQty { get; set; }

    [DisplayName("估計成本")]
    public decimal AverageCost { get; set; }
}

public sealed class CostDistributionRow
{
    [DisplayName("日期")]
    public string TradeDate { get; set; } = "";

    [DisplayName("分點")]
    public string Branch { get; set; } = "";

    [DisplayName("買進")]
    public decimal BuyQty { get; set; }

    [DisplayName("賣出")]
    public decimal SellQty { get; set; }

    [DisplayName("買賣超")]
    public decimal NetQty { get; set; }

    [DisplayName("均價")]
    public decimal AvgPrice { get; set; }

    [DisplayName("累計持有")]
    public decimal RunningPosition { get; set; }

    [DisplayName("動作")]
    public string Action { get; set; } = "";
}

public sealed class DayTradeSignal
{
    public string TradeDate { get; set; } = "";
    public string Branch { get; set; } = "";
    public decimal BuyNetQty { get; set; }
    public decimal NextSellNetQty { get; set; }
    public decimal SellBackRatio { get; set; }
    public bool ClosePriceNear { get; set; }
    public decimal Score { get; set; }
}

public sealed class CampSignal
{
    public string Branch { get; set; } = "";
    public string ManualTag { get; set; } = "未知";
    public string LocalBrokerText => IsLocalBroker ? "是" : "否";
    public bool IsLocalBroker { get; set; }
    public string InferredCamp { get; set; } = "未知";
}

public sealed class UnifiedBranchRow
{
    [DisplayName("排名")]
    public int Rank { get; set; }

    [DisplayName("股票")]
    public string Stock { get; set; } = "";

    [DisplayName("交易日期")]
    public string TradeDate { get; set; } = "";

    [DisplayName("分點")]
    public string Branch { get; set; } = "";

    [DisplayName("持有張數")]
    public decimal PositionQty { get; set; }

    [DisplayName("剩餘均價")]
    public decimal AverageCost { get; set; }

    [DisplayName("損益估算")]
    public decimal EstimatedPnl { get; set; }

    [DisplayName("損益狀態")]
    public string PnlStatus { get; set; } = "--";

    [DisplayName("賺錢次數")]
    public int WinCount { get; set; }

    [DisplayName("賠錢次數")]
    public int LossCount { get; set; }

    [DisplayName("勝率")]
    public string WinRateText { get; set; } = "--";

    [DisplayName("推定角色")]
    public string InferredCamp { get; set; } = "未知";

    [DisplayName("地緣券商")]
    public string LocalBrokerText { get; set; } = "否";
}

public sealed class AnalysisResult
{
    public IReadOnlyList<UnifiedBranchRow> UnifiedRows { get; init; } = [];
    public IReadOnlyList<BranchPerformance> Performances { get; init; } = [];
    public IReadOnlyList<HolderRank> HolderRanks { get; init; } = [];
    public IReadOnlyList<CostDistributionRow> CostDistribution { get; init; } = [];
    public IReadOnlyList<DayTradeSignal> DayTradeSignals { get; init; } = [];
    public IReadOnlyList<CampSignal> CampSignals { get; init; } = [];
}

public sealed class WantGooFetchResult
{
    public List<BrokerTradeRecord> Trades { get; } = [];
    public List<BrokerBranch> Branches { get; } = [];
    public List<StockOption> Stocks { get; } = [];
    public List<string> Warnings { get; } = [];
    public DateOnly? LastDate { get; set; }
    public int AgentCount { get; set; }
    public int SkippedMaskedRows { get; set; }
}

public sealed class WantGooBrokerBranchCatalog
{
    public DateOnly TradeDate { get; set; }
    public List<BrokerBranch> Branches { get; } = [];
}

public sealed class StockPrefetchState
{
    public string StockNo { get; set; } = "";
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public int RowsImported { get; set; }
    public string UpdatedAt { get; set; } = "";
}

public sealed class BrokerStockDailyRow
{
    [DisplayName("日期")]
    public string TradeDate { get; set; } = "";

    [DisplayName("買進(張)")]
    public decimal BuyQty { get; set; }

    [DisplayName("賣出(張)")]
    public decimal SellQty { get; set; }

    [DisplayName("買賣總額(張)")]
    public decimal TotalQty => BuyQty + SellQty;

    [DisplayName("買賣超(張)")]
    public decimal NetQty { get; set; }

    [DisplayName("均價/估價")]
    public decimal AvgPrice { get; set; }

    [DisplayName("收盤價")]
    public decimal? ClosePrice { get; set; }

    [DisplayName("來源")]
    public string Source { get; set; } = "";
}
public sealed class StockBranchRankRow
{
    public string WatchMark => IsStarred ? "*" : "";

    public int Rank { get; set; }

    public string StockNo { get; set; } = "";

    public string StockName { get; set; } = "";

    public string MajorId { get; set; } = "";

    public string MajorName { get; set; } = "";

    public string BranchId { get; set; } = "";

    public string BranchName { get; set; } = "";

    public string Branch => string.IsNullOrWhiteSpace(BranchName) ? MajorName : $"{MajorName}-{BranchName}";

    public decimal BuyQty { get; set; }

    public decimal SellQty { get; set; }

    public decimal TotalQty => BuyQty + SellQty;

    public decimal NetQty { get; set; }

    public decimal AvgPrice { get; set; }

    public int TradeDays { get; set; }

    public string LatestTradeDate { get; set; } = "";

    public bool IsStarred { get; set; }

    public string Note { get; set; } = "";

    public BrokerBranch ToBranch()
    {
        return new BrokerBranch
        {
            MajorId = MajorId,
            MajorName = MajorName,
            BranchId = BranchId,
            BranchName = BranchName
        };
    }
}

public sealed class BranchAnnotation
{
    public string StockNo { get; set; } = "";

    public string MajorId { get; set; } = "";

    public string BranchId { get; set; } = "";

    public bool IsStarred { get; set; }

    public string Note { get; set; } = "";
}

public sealed class StockPriceRow
{
    public DateOnly TradeDate { get; set; }

    public decimal ClosePrice { get; set; }
}
