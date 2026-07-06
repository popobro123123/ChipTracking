using System.ComponentModel;
using System.Reflection;
using ChipTracking.Data;
using ChipTracking.Services;

namespace ChipTracking;

public sealed class MainForm : Form
{
    private const int RankFetchConcurrency = 4;

    private readonly ChipDatabase _database;
    private readonly FubonMoneyDjScraper _fubonScraper = new();
    private readonly TwsePriceScraper _priceScraper = new();
    private readonly CancellationTokenSource _closingCts = new();

    private readonly TextBox _stockTextBox = new();
    private readonly DateTimePicker _fromDatePicker = new();
    private readonly DateTimePicker _toDatePicker = new();
    private readonly Button _searchButton = new();
    private readonly Label _titleLabel = new();
    private readonly Label _summaryLabel = new();
    private readonly Label _branchListTitleLabel = new();
    private readonly Label _statusLabel = new();
    private readonly DataGridView _branchGrid = CreateGrid();
    private readonly ContextMenuStrip _branchMenu = new();
    private readonly TextBox _stockNoteBox = new();
    private readonly Label _noteStatusLabel = new();

    private List<StockOption> _stockOptions = [];
    private StockOption? _currentStock;
    private DateOnly _currentFrom;
    private DateOnly _currentTo;
    private bool _loadingBranchDetail;
    private bool _loadingStockNote;

    public MainForm(ChipDatabase database, AnalysisService _)
    {
        _database = database;

        Text = "ChipTracking 主力進出";
        Width = 1500;
        Height = 920;
        MinimumSize = new Size(1180, 720);
        Font = new Font("Microsoft JhengHei UI", 9F);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(230, 230, 230);

        BuildLayout();
        WireEvents();
        BuildBranchMenu();

        Load += async (_, _) => await LoadInitialDataAsync();
        FormClosing += (_, _) =>
        {
            SaveCurrentStockNote();
            _closingCts.Cancel();
            _fubonScraper.Dispose();
            _priceScraper.Dispose();
        };
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildToolbar(), 0, 1);
        root.Controls.Add(BuildContent(), 0, 2);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = Color.FromArgb(185, 185, 185);
        root.Controls.Add(_statusLabel, 0, 3);

        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        _titleLabel.Dock = DockStyle.Fill;
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _titleLabel.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
        _titleLabel.ForeColor = Color.FromArgb(255, 206, 36);
        _titleLabel.Text = "主力進出";

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleRight;
        _summaryLabel.ForeColor = Color.FromArgb(210, 210, 210);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        header.Controls.Add(_titleLabel, 0, 0);
        header.Controls.Add(_summaryLabel, 1, 0);
        return header;
    }

    private Control BuildToolbar()
    {
        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 1,
            Padding = new Padding(0, 8, 0, 8)
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        StyleInput(_stockTextBox);
        _stockTextBox.PlaceholderText = "輸入股票代號或名稱，例如 1101 台泥";
        _stockTextBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _stockTextBox.AutoCompleteSource = AutoCompleteSource.CustomSource;

        _fromDatePicker.Format = DateTimePickerFormat.Custom;
        _fromDatePicker.CustomFormat = "yyyy/MM/dd";
        _fromDatePicker.Value = DateTime.Today.AddYears(-1);
        StyleInput(_fromDatePicker);

        _toDatePicker.Format = DateTimePickerFormat.Custom;
        _toDatePicker.CustomFormat = "yyyy/MM/dd";
        _toDatePicker.Value = DateTime.Today;
        StyleInput(_toDatePicker);

        _searchButton.Text = "查分點";
        _searchButton.Dock = DockStyle.Fill;
        _searchButton.BackColor = Color.FromArgb(255, 206, 36);
        _searchButton.ForeColor = Color.FromArgb(24, 24, 24);
        _searchButton.FlatStyle = FlatStyle.Flat;
        _searchButton.FlatAppearance.BorderColor = Color.FromArgb(255, 206, 36);

        toolbar.Controls.Add(BuildLabel("股票"), 0, 0);
        toolbar.Controls.Add(_stockTextBox, 1, 0);
        toolbar.Controls.Add(BuildLabel("起日"), 2, 0);
        toolbar.Controls.Add(_fromDatePicker, 3, 0);
        toolbar.Controls.Add(BuildLabel("迄"), 4, 0);
        toolbar.Controls.Add(_toDatePicker, 5, 0);
        toolbar.Controls.Add(_searchButton, 6, 0);
        return toolbar;
    }

    private Control BuildContent()
    {
        _branchListTitleLabel.Dock = DockStyle.Fill;
        _branchListTitleLabel.Text = "分點總覽：先查股票，再點選分點看圖表";
        _branchListTitleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _branchListTitleLabel.ForeColor = Color.FromArgb(255, 206, 36);
        _branchListTitleLabel.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);

        _branchGrid.Font = new Font(Font.FontFamily, 10F);
        _branchGrid.ColumnHeadersDefaultCellStyle.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
        _branchGrid.RowTemplate.Height = 28;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            BackColor = BackColor
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
        panel.Controls.Add(_branchListTitleLabel, 0, 0);
        panel.Controls.Add(_branchGrid, 0, 1);
        panel.Controls.Add(BuildStockNoteHeader(), 0, 2);
        panel.Controls.Add(_stockNoteBox, 0, 3);
        return panel;
    }

    private Control BuildStockNoteHeader()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 1,
            ColumnCount = 3,
            BackColor = BackColor,
            Padding = new Padding(0, 6, 0, 0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

        var label = new Label
        {
            Text = "股票筆記",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(255, 206, 36),
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold)
        };

        _noteStatusLabel.Dock = DockStyle.Fill;
        _noteStatusLabel.TextAlign = ContentAlignment.MiddleRight;
        _noteStatusLabel.ForeColor = Color.FromArgb(170, 170, 170);

        var saveButton = new Button
        {
            Text = "儲存",
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(255, 206, 36),
            ForeColor = Color.FromArgb(24, 24, 24),
            FlatStyle = FlatStyle.Flat
        };
        saveButton.Click += (_, _) =>
        {
            SaveCurrentStockNote();
            _noteStatusLabel.Text = "已儲存";
        };

        _stockNoteBox.Dock = DockStyle.Fill;
        _stockNoteBox.Multiline = true;
        _stockNoteBox.AcceptsReturn = true;
        _stockNoteBox.AcceptsTab = true;
        _stockNoteBox.ScrollBars = ScrollBars.Vertical;
        _stockNoteBox.BackColor = Color.FromArgb(24, 24, 24);
        _stockNoteBox.ForeColor = Color.FromArgb(235, 235, 235);
        _stockNoteBox.BorderStyle = BorderStyle.FixedSingle;
        _stockNoteBox.TextChanged += (_, _) =>
        {
            if (!_loadingStockNote)
            {
                _noteStatusLabel.Text = "未儲存";
            }
        };

        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(_noteStatusLabel, 1, 0);
        panel.Controls.Add(saveButton, 2, 0);
        return panel;
    }

    private void WireEvents()
    {
        _searchButton.Click += async (_, _) => await SearchStockAsync();
        _stockTextBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await SearchStockAsync();
            }
        };
        _branchGrid.CellMouseDown += (_, e) =>
        {
            if (e.RowIndex < 0 || e.Button != MouseButtons.Right)
            {
                return;
            }

            _branchGrid.ClearSelection();
            _branchGrid.Rows[e.RowIndex].Selected = true;
            if (e.ColumnIndex >= 0)
            {
                _branchGrid.CurrentCell = _branchGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            }
        };
        _branchGrid.CellMouseClick += async (_, e) =>
        {
            if (e.RowIndex < 0)
            {
                return;
            }

            _branchGrid.ClearSelection();
            _branchGrid.Rows[e.RowIndex].Selected = true;
            if (e.ColumnIndex >= 0)
            {
                _branchGrid.CurrentCell = _branchGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            }

            if (e.Button == MouseButtons.Left)
            {
                await OpenBranchDetailFromGridAsync();
            }
        };
        _branchGrid.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await OpenBranchDetailFromGridAsync();
            }
        };
    }

    private void BuildBranchMenu()
    {
        var toggleStarItem = new ToolStripMenuItem("加上星號註記");
        toggleStarItem.Click += (_, _) => ToggleSelectedBranchStar();
        _branchMenu.Items.Add(toggleStarItem);
        _branchMenu.Opening += (_, e) =>
        {
            e.Cancel = _currentStock is null || _branchGrid.CurrentRow?.DataBoundItem is not StockBranchRankRow;
            if (!e.Cancel && _branchGrid.CurrentRow?.DataBoundItem is StockBranchRankRow row)
            {
                toggleStarItem.Text = row.IsStarred ? "取消星號註記" : "加上星號註記";
            }
        };
        _branchGrid.ContextMenuStrip = _branchMenu;
    }

    private async Task LoadInitialDataAsync()
    {
        SetStatus("載入股票與券商分點清單...");
        _stockOptions = _database.GetStocks().ToList();

        try
        {
            var branches = await _fubonScraper.GetBrokerBranchesAsync(_closingCts.Token);
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var branch in branches)
            {
                _database.UpsertBranch(connection, branch);
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            SetStatus($"券商分點清單更新失敗，先使用 DB 既有資料：{ex.Message}");
        }

        BindReferenceControls();
        SetStatus($"DB: {_database.DatabasePath}");
    }

    private void BindReferenceControls()
    {
        var stockAutoComplete = new AutoCompleteStringCollection();
        stockAutoComplete.AddRange(_stockOptions.Select(stock => stock.DisplayName).ToArray());
        _stockTextBox.AutoCompleteCustomSource = stockAutoComplete;
        if (string.IsNullOrWhiteSpace(_stockTextBox.Text))
        {
            _stockTextBox.Text = _stockOptions.FirstOrDefault(stock => stock.StockNo == "1101")?.DisplayName
                ?? _stockOptions.FirstOrDefault()?.DisplayName
                ?? "";
        }
    }

    private void LoadStockNote(string stockNo)
    {
        _loadingStockNote = true;
        try
        {
            _stockNoteBox.Text = _database.GetStockNote(stockNo);
            _noteStatusLabel.Text = _stockNoteBox.TextLength > 0 ? "已載入" : "";
        }
        finally
        {
            _loadingStockNote = false;
        }
    }

    private void SaveCurrentStockNote()
    {
        if (_currentStock is null)
        {
            return;
        }

        _database.SetStockNote(_currentStock.StockNo, _stockNoteBox.Text);
        if (!_loadingStockNote)
        {
            _noteStatusLabel.Text = "已儲存";
        }
    }

    private async Task SearchStockAsync()
    {
        var stock = ResolveStock(_stockTextBox.Text);
        if (stock is null)
        {
            MessageBox.Show(this, "請先輸入股票代號或名稱。", "查詢股票", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var from = DateOnly.FromDateTime(_fromDatePicker.Value);
        var to = DateOnly.FromDateTime(_toDatePicker.Value);
        if (from > to)
        {
            MessageBox.Show(this, "起日不能晚於迄日。", "查詢股票", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _searchButton.Enabled = false;
        _branchGrid.Enabled = false;
        UseWaitCursor = true;
        try
        {
            SaveCurrentStockNote();
            _currentStock = stock;
            _currentFrom = from;
            _currentTo = to;
            _stockTextBox.Text = stock.DisplayName;
            LoadStockNote(stock.StockNo);
            _titleLabel.Text = $"{stock.DisplayName} 分點總覽";
            _summaryLabel.Text = "";
            _branchListTitleLabel.Text = $"{stock.DisplayName} 分點總覽";
            BindGrid(_branchGrid, Array.Empty<StockBranchRankRow>());

            SetStatus($"抓取 {stock.DisplayName} 分點總覽資料...");
            var tradeDays = await EnsureStockBranchRankDataAsync(stock, from, to, _closingCts.Token);
            if (tradeDays.Count > 0)
            {
                _currentFrom = tradeDays.Min();
                _currentTo = tradeDays.Max();
            }

            var rows = _database.GetStockBranchRankRows(stock.StockNo, _currentFrom, _currentTo);
            BindGrid(_branchGrid, rows);
            _branchGrid.ClearSelection();

            var totalBuy = rows.Sum(row => row.BuyQty);
            var totalSell = rows.Sum(row => row.SellQty);
            var net = rows.Sum(row => row.NetQty);
            _summaryLabel.Text = $"買 {totalBuy:N0} 張 / 賣 {totalSell:N0} 張 / 買賣超 {net:N0} 張";

            var rangeText = $"{_currentFrom:yyyy/MM/dd} - {_currentTo:yyyy/MM/dd}";
            SetStatus(rows.Count == 0
                ? $"{stock.DisplayName} 在 {rangeText} 沒有分點資料。"
                : $"完成：{rows.Count:N0} 個分點，區間 {rangeText}。點任一分點會開啟圖表視窗。");
        }
        catch (Exception ex)
        {
            SetStatus($"查詢失敗：{ex.Message}");
            MessageBox.Show(this, ex.Message, "查詢失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _branchGrid.Enabled = true;
            _searchButton.Enabled = true;
            UseWaitCursor = false;
        }
    }

    private async Task OpenBranchDetailFromGridAsync()
    {
        if (_loadingBranchDetail ||
            _currentStock is null ||
            _branchGrid.CurrentRow?.DataBoundItem is not StockBranchRankRow rankRow)
        {
            return;
        }

        _loadingBranchDetail = true;
        _branchGrid.Enabled = false;
        UseWaitCursor = true;
        try
        {
            var branch = rankRow.ToBranch();
            SetStatus($"抓取 {rankRow.Branch} 對 {_currentStock.DisplayName} 的每日明細...");
            await EnsureBrokerStockDataAsync(branch, _currentStock.StockNo, _currentStock.StockName, _currentFrom, _currentTo, _closingCts.Token);

            var rows = _database.GetBrokerStockDailyRows(_currentStock.StockNo, branch.MajorId, branch.BranchId, _currentFrom, _currentTo);
            var detailForm = new BranchDetailForm(_database, _currentStock, rankRow, rows, _currentFrom, _currentTo)
            {
                StartPosition = FormStartPosition.CenterParent
            };
            detailForm.Show(this);
            SetStatus($"完成：{rankRow.Branch} 每日明細 {rows.Count:N0} 筆。");
        }
        catch (Exception ex)
        {
            SetStatus($"分點明細查詢失敗：{ex.Message}");
            MessageBox.Show(this, ex.Message, "分點明細查詢失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _branchGrid.Enabled = true;
            _loadingBranchDetail = false;
            UseWaitCursor = false;
        }
    }

    private void ToggleSelectedBranchStar()
    {
        if (_currentStock is null || _branchGrid.CurrentRow?.DataBoundItem is not StockBranchRankRow row)
        {
            return;
        }

        row.IsStarred = !row.IsStarred;
        _database.SetBranchStarred(_currentStock.StockNo, row.MajorId, row.BranchId, row.IsStarred);
        _branchGrid.Refresh();
        _branchGrid.Invalidate();
        SetStatus($"{row.Branch} {(row.IsStarred ? "已加上星號註記" : "已取消星號註記")}");
    }

    private async Task<IReadOnlyList<DateOnly>> EnsureStockBranchRankDataAsync(
        StockOption stock,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var priceFrom = from.AddDays(-7);
        await EnsurePriceHistoryAsync(stock.StockNo, priceFrom, to, cancellationToken);

        var priceRows = _database.GetStockClosePrices(stock.StockNo, from, to).ToList();
        var tradeDays = priceRows.Select(row => row.TradeDate).Distinct().OrderBy(date => date).ToList();
        if (tradeDays.Count == 0)
        {
            var latestPrice = _database.GetLatestStockClosePriceOnOrBefore(stock.StockNo, to);
            if (latestPrice is not null && latestPrice.TradeDate >= from.AddDays(-7))
            {
                priceRows = [latestPrice];
                tradeDays = [latestPrice.TradeDate];
            }
        }

        if (tradeDays.Count == 0)
        {
            try
            {
                var latestTradeDate = await _fubonScraper.GetLatestTradeDateAsync(cancellationToken);
                if (latestTradeDate <= to && latestTradeDate >= from.AddDays(-7))
                {
                    tradeDays = [latestTradeDate];
                }
            }
            catch
            {
                tradeDays = EnumerateWeekdays(from, to).ToList();
            }
        }

        if (tradeDays.Count == 0)
        {
            return [];
        }

        var closePrices = priceRows
            .GroupBy(row => row.TradeDate)
            .ToDictionary(group => group.Key, group => group.First().ClosePrice);
        var pendingDays = tradeDays
            .Where(day => !_database.IsPrefetchComplete(GetStockBranchStateKey(stock.StockNo, day), day, day))
            .ToList();

        if (pendingDays.Count == 0)
        {
            return tradeDays;
        }

        var completed = 0;
        using var gate = new SemaphoreSlim(RankFetchConcurrency);
        var tasks = pendingDays.Select(async day =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                closePrices.TryGetValue(day, out var closePrice);
                var result = await _fubonScraper.FetchStockBranchTradesAsync(
                    stock.StockNo,
                    stock.StockName,
                    day,
                    closePrice > 0 ? closePrice : null,
                    cancellationToken);
                var done = Interlocked.Increment(ref completed);
                SetStatusSafe($"抓分點總覽 {done:N0}/{pendingDays.Count:N0}：{day:yyyy/MM/dd}");
                return (Day: day, Result: result);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var fetched = await Task.WhenAll(tasks);
        SaveFetchResults(fetched.Select(item => item.Result), "fubon-stock-zco");
        foreach (var item in fetched)
        {
            _database.UpsertPrefetchState(
                GetStockBranchStateKey(stock.StockNo, item.Day),
                item.Day,
                item.Day,
                "ok",
                $"trades={item.Result.Trades.Count}",
                item.Result.Trades.Count);
        }

        return tradeDays;
    }

    private async Task EnsureBrokerStockDataAsync(
        BrokerBranch branch,
        string stockNo,
        string stockName,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        await EnsurePriceHistoryAsync(stockNo, from, to, cancellationToken);
        var stateKey = GetBrokerStockStateKey(stockNo, branch, from, to);
        if (_database.IsPrefetchComplete(stateKey, from, to))
        {
            return;
        }

        var result = await _fubonScraper.FetchBrokerStockDetailAsync(branch, stockNo, stockName, from, to, cancellationToken);
        SaveFetchResult(result, "fubon-zco0-detail");
        _database.UpsertPrefetchState(stateKey, from, to, "ok", $"trades={result.Trades.Count}", result.Trades.Count);
    }

    private async Task EnsurePriceHistoryAsync(string stockNo, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var month = new DateOnly(from.Year, from.Month, 1);
        var endMonth = new DateOnly(to.Year, to.Month, 1);
        while (month <= endMonth)
        {
            var stateKey = $"twse-price:{stockNo}:{month:yyyyMM}";
            var monthEnd = month.AddMonths(1).AddDays(-1);
            var monthHasPrices = _database.GetStockClosePrices(stockNo, month, monthEnd).Count > 0;
            if (!_database.IsPrefetchComplete(stateKey, month, monthEnd) || !monthHasPrices)
            {
                var prices = await _priceScraper.FetchMonthlyClosePricesAsync(stockNo, month, cancellationToken);
                using (var connection = _database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    foreach (var price in prices)
                    {
                        _database.UpsertPrice(connection, stockNo, price.TradeDate, price.ClosePrice, "stock-day");
                    }

                    transaction.Commit();
                }

                _database.UpsertPrefetchState(stateKey, month, monthEnd, "ok", $"prices={prices.Count}", prices.Count);
            }

            month = month.AddMonths(1);
        }
    }

    private void SaveFetchResult(WantGooFetchResult fetchResult, string syncSource)
    {
        SaveFetchResults([fetchResult], syncSource);
    }

    private void SaveFetchResults(IEnumerable<WantGooFetchResult> fetchResults, string syncSource)
    {
        var results = fetchResults.ToList();
        using (var connection = _database.OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            foreach (var result in results)
            {
                foreach (var branch in result.Branches)
                {
                    _database.UpsertBranch(connection, branch);
                }

                foreach (var trade in result.Trades)
                {
                    _database.UpsertTrade(connection, trade);
                }
            }

            transaction.Commit();
        }

        var totalRows = results.Sum(result => result.Trades.Count);
        _database.AddSyncRun(syncSource, "ok", $"trades={totalRows}", totalRows);
    }

    private StockOption? ResolveStock(string text)
    {
        var query = text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var firstToken = ExtractStockNo(query);
        var exact = _stockOptions.FirstOrDefault(stock =>
            stock.StockNo.Equals(firstToken, StringComparison.OrdinalIgnoreCase) ||
            stock.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var fuzzy = _stockOptions.FirstOrDefault(stock =>
            stock.StockName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            stock.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (fuzzy is not null)
        {
            return fuzzy;
        }

        return firstToken.Any(char.IsDigit)
            ? new StockOption { StockNo = firstToken, StockName = "" }
            : null;
    }

    private static IEnumerable<DateOnly> EnumerateWeekdays(DateOnly from, DateOnly to)
    {
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                yield return day;
            }
        }
    }

    internal static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            AutoGenerateColumns = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(24, 24, 24),
            ForeColor = Color.FromArgb(230, 230, 230),
            BorderStyle = BorderStyle.FixedSingle,
            GridColor = Color.FromArgb(58, 58, 58),
            EnableHeadersVisualStyles = false
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(36, 36, 36);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(255, 206, 36);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(36, 36, 36);
        grid.DefaultCellStyle.BackColor = Color.FromArgb(26, 26, 26);
        grid.DefaultCellStyle.ForeColor = Color.FromArgb(230, 230, 230);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 120, 215);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(38, 38, 38);
        grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0)
            {
                return;
            }

            if (grid.Columns[e.ColumnIndex].DataPropertyName == nameof(BrokerStockDailyRow.NetQty) && e.Value is decimal value)
            {
                e.CellStyle.ForeColor = value >= 0 ? Color.FromArgb(245, 75, 75) : Color.FromArgb(70, 205, 80);
            }
        };
        return grid;
    }

    internal static void BindGrid<T>(DataGridView grid, IReadOnlyList<T> rows)
    {
        grid.DataSource = rows.ToList();
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (DataGridViewColumn column in grid.Columns)
        {
            var property = properties.FirstOrDefault(x => x.Name == column.DataPropertyName);
            column.HeaderText = ResolveHeaderText(column.DataPropertyName, property);
            column.Visible = !ShouldHideColumn(column.DataPropertyName);

            if (column.ValueType == typeof(decimal) || column.ValueType == typeof(decimal?))
            {
                column.DefaultCellStyle.Format = IsQuantityColumn(column.DataPropertyName) ? "N0" : "N2";
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            else if (column.ValueType == typeof(int))
            {
                column.DefaultCellStyle.Format = "N0";
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
        }

        if (grid.Columns.Contains(nameof(StockBranchRankRow.Rank)))
        {
            grid.Columns[nameof(StockBranchRankRow.Rank)]!.FillWeight = 42;
        }

        if (grid.Columns.Contains(nameof(StockBranchRankRow.WatchMark)))
        {
            grid.Columns[nameof(StockBranchRankRow.WatchMark)]!.FillWeight = 34;
            grid.Columns[nameof(StockBranchRankRow.WatchMark)]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }

        if (grid.Columns.Contains(nameof(StockBranchRankRow.Branch)))
        {
            grid.Columns[nameof(StockBranchRankRow.Branch)]!.FillWeight = 180;
        }

        if (grid.Columns.Contains(nameof(BrokerStockDailyRow.TradeDate)))
        {
            grid.Columns[nameof(BrokerStockDailyRow.TradeDate)]!.FillWeight = 90;
        }
    }

    private static string ResolveHeaderText(string propertyName, PropertyInfo? property)
    {
        return propertyName switch
        {
            nameof(StockBranchRankRow.WatchMark) => "*",
            nameof(StockBranchRankRow.Rank) => "排名",
            nameof(StockBranchRankRow.Branch) => "分點",
            nameof(StockBranchRankRow.BuyQty) => "買進(張)",
            nameof(StockBranchRankRow.SellQty) => "賣出(張)",
            nameof(StockBranchRankRow.TotalQty) => "買賣總額(張)",
            nameof(StockBranchRankRow.NetQty) => "買賣超(張)",
            nameof(StockBranchRankRow.AvgPrice) => "均價/估價",
            nameof(StockBranchRankRow.LatestTradeDate) => "最後交易日",
            nameof(BrokerStockDailyRow.TradeDate) => "日期",
            nameof(BrokerStockDailyRow.ClosePrice) => "收盤價",
            nameof(BrokerStockDailyRow.Source) => "來源",
            _ => property?.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? propertyName
        };
    }

    private static bool ShouldHideColumn(string propertyName)
    {
        return propertyName is
            nameof(StockBranchRankRow.StockNo) or
            nameof(StockBranchRankRow.StockName) or
            nameof(StockBranchRankRow.MajorId) or
            nameof(StockBranchRankRow.MajorName) or
            nameof(StockBranchRankRow.BranchId) or
            nameof(StockBranchRankRow.BranchName) or
            nameof(StockBranchRankRow.TradeDays) or
            nameof(StockBranchRankRow.IsStarred) or
            nameof(StockBranchRankRow.Note) or
            nameof(BrokerStockDailyRow.Source);
    }

    private static bool IsQuantityColumn(string propertyName)
    {
        return propertyName.EndsWith("Qty", StringComparison.Ordinal) ||
            propertyName == nameof(StockBranchRankRow.TotalQty);
    }

    private static Label BuildLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(224, 224, 224),
            Padding = new Padding(0, 2, 4, 0)
        };
    }

    private static void StyleInput(Control control)
    {
        control.Dock = DockStyle.Fill;
        control.BackColor = Color.FromArgb(46, 46, 46);
        control.ForeColor = Color.FromArgb(238, 238, 238);
    }

    private static string ExtractStockNo(string text)
    {
        return text.Trim()
            .Split([' ', '\t', '-'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";
    }

    private static string GetStockBranchStateKey(string stockNo, DateOnly tradeDate)
    {
        return $"zco:{stockNo}:{tradeDate:yyyyMMdd}";
    }

    private static string GetBrokerStockStateKey(string stockNo, BrokerBranch branch, DateOnly from, DateOnly to)
    {
        return $"zco0:{stockNo}:{branch.MajorId}:{branch.BranchId}:{from:yyyyMMdd}:{to:yyyyMMdd}";
    }

    private void SetStatus(string message)
    {
        _statusLabel.Text = message;
        _statusLabel.Refresh();
    }

    private void SetStatusSafe(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(message));
            return;
        }

        SetStatus(message);
    }
}
