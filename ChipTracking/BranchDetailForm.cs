using ChipTracking.Data;

namespace ChipTracking;

public sealed class BranchDetailForm : Form
{
    private readonly BrokerTrendChart _priceChart = new()
    {
        Mode = BrokerTrendChartMode.PriceLine,
        Title = "均價/收盤價趨勢"
    };

    private readonly BrokerTrendChart _netChart = new()
    {
        Mode = BrokerTrendChartMode.NetBar,
        Title = "每日買賣超"
    };

    private readonly DataGridView _detailGrid = MainForm.CreateGrid();
    private readonly Label _titleLabel = new();
    private readonly Label _summaryLabel = new();

    public BranchDetailForm(
        ChipDatabase _,
        StockOption stock,
        StockBranchRankRow branch,
        IReadOnlyList<BrokerStockDailyRow> rows,
        DateOnly from,
        DateOnly to)
    {
        Text = $"{branch.Branch} 對 {stock.DisplayName} 主力進出";
        Width = 1280;
        Height = 860;
        MinimumSize = new Size(980, 700);
        Font = new Font("Microsoft JhengHei UI", 9F);
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(230, 230, 230);

        BuildLayout();
        WireChartSync();
        BindData(stock, branch, rows, from, to);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(14),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 27));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));

        _titleLabel.Dock = DockStyle.Fill;
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _titleLabel.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
        _titleLabel.ForeColor = Color.FromArgb(255, 206, 36);

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleRight;
        _summaryLabel.ForeColor = Color.FromArgb(210, 210, 210);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        header.Controls.Add(_titleLabel, 0, 0);
        header.Controls.Add(_summaryLabel, 1, 0);

        _detailGrid.RowTemplate.Height = 26;

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_priceChart, 0, 1);
        root.Controls.Add(_netChart, 0, 2);
        root.Controls.Add(_detailGrid, 0, 3);
        Controls.Add(root);
    }

    private void WireChartSync()
    {
        _priceChart.HoverIndexChanged += index => _netChart.SetExternalHoverIndex(index);
        _netChart.HoverIndexChanged += index => _priceChart.SetExternalHoverIndex(index);
    }

    private void BindData(
        StockOption stock,
        StockBranchRankRow branch,
        IReadOnlyList<BrokerStockDailyRow> rows,
        DateOnly from,
        DateOnly to)
    {
        _titleLabel.Text = $"{branch.Branch} 對 {stock.DisplayName} 主力進出";

        var totalBuy = rows.Sum(row => row.BuyQty);
        var totalSell = rows.Sum(row => row.SellQty);
        var net = rows.Sum(row => row.NetQty);
        var average = rows.Where(row => row.AvgPrice > 0).Select(row => row.AvgPrice).DefaultIfEmpty(0).Average();
        _summaryLabel.Text = $"買 {totalBuy:N0} 張 / 賣 {totalSell:N0} 張 / 買賣超 {net:N0} 張 / 均價 {average:N2}";

        _priceChart.Title = $"{branch.Branch} 對 {stock.DisplayName} 均價/收盤價趨勢";
        _netChart.Title = $"{branch.Branch} 對 {stock.DisplayName} 每日買賣超";
        _priceChart.EmptyText = "這個分點在區間內沒有價格資料";
        _netChart.EmptyText = "這個分點在區間內沒有買賣資料";
        _priceChart.SetRows(rows);
        _netChart.SetRows(rows);

        MainForm.BindGrid(_detailGrid, rows);
        Text = $"{branch.Branch} 對 {stock.DisplayName} 主力進出 ({from:yyyy/MM/dd}-{to:yyyy/MM/dd})";
    }
}
