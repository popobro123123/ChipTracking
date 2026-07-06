using Microsoft.Data.Sqlite;

namespace ChipTracking.Data;

public sealed class ChipDatabase : IDisposable
{
    private readonly string _connectionString;

    public ChipDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true
        }.ToString();
    }

    public string DatabasePath { get; }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS stocks (
                stock_no TEXT PRIMARY KEY,
                stock_name TEXT NOT NULL DEFAULT '',
                market TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS broker_branches (
                branch_key TEXT PRIMARY KEY,
                major_id TEXT NOT NULL DEFAULT '',
                major_name TEXT NOT NULL DEFAULT '',
                branch_id TEXT NOT NULL DEFAULT '',
                branch_name TEXT NOT NULL DEFAULT '',
                address TEXT NOT NULL DEFAULT '',
                latitude REAL NULL,
                longitude REAL NULL,
                camp_tag TEXT NOT NULL DEFAULT '未知',
                notes TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS broker_trades (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                trade_date TEXT NOT NULL,
                period_days INTEGER NOT NULL DEFAULT 1,
                stock_no TEXT NOT NULL,
                stock_name TEXT NOT NULL DEFAULT '',
                major_id TEXT NOT NULL DEFAULT '',
                major_name TEXT NOT NULL DEFAULT '',
                branch_id TEXT NOT NULL DEFAULT '',
                branch_name TEXT NOT NULL DEFAULT '',
                buy_qty REAL NOT NULL DEFAULT 0,
                sell_qty REAL NOT NULL DEFAULT 0,
                net_qty REAL NOT NULL DEFAULT 0,
                amount REAL NOT NULL DEFAULT 0,
                avg_price REAL NOT NULL DEFAULT 0,
                close_price REAL NULL,
                source TEXT NOT NULL DEFAULT 'manual',
                imported_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(trade_date, period_days, stock_no, major_id, branch_id, source)
            );

            CREATE TABLE IF NOT EXISTS price_history (
                stock_no TEXT NOT NULL,
                trade_date TEXT NOT NULL,
                close_price REAL NOT NULL,
                source TEXT NOT NULL DEFAULT 'manual',
                PRIMARY KEY(stock_no, trade_date)
            );

            CREATE TABLE IF NOT EXISTS company_locations (
                stock_no TEXT PRIMARY KEY,
                company_name TEXT NOT NULL DEFAULT '',
                address TEXT NOT NULL DEFAULT '',
                latitude REAL NULL,
                longitude REAL NULL,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS insider_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                stock_no TEXT NOT NULL,
                event_date TEXT NOT NULL,
                holder_name TEXT NOT NULL DEFAULT '',
                change_qty REAL NOT NULL DEFAULT 0,
                source TEXT NOT NULL DEFAULT 'manual',
                imported_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS sync_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                source TEXT NOT NULL,
                status TEXT NOT NULL,
                message TEXT NOT NULL DEFAULT '',
                rows_imported INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS stock_prefetch_state (
                stock_no TEXT PRIMARY KEY,
                from_date TEXT NOT NULL DEFAULT '',
                to_date TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT '',
                message TEXT NOT NULL DEFAULT '',
                rows_imported INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS branch_annotations (
                stock_no TEXT NOT NULL,
                major_id TEXT NOT NULL DEFAULT '',
                branch_id TEXT NOT NULL DEFAULT '',
                is_starred INTEGER NOT NULL DEFAULT 0,
                note TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY(stock_no, major_id, branch_id)
            );

            CREATE TABLE IF NOT EXISTS stock_notes (
                stock_no TEXT PRIMARY KEY,
                note TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS idx_broker_trades_stock_date
                ON broker_trades(stock_no, trade_date);
            CREATE INDEX IF NOT EXISTS idx_broker_trades_branch
                ON broker_trades(major_id, branch_id, stock_no);
            CREATE INDEX IF NOT EXISTS idx_insider_events_stock_date
                ON insider_events(stock_no, event_date);
            """;
        command.ExecuteNonQuery();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void UpsertStock(SqliteConnection connection, string stockNo, string stockName)
    {
        if (string.IsNullOrWhiteSpace(stockNo))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO stocks(stock_no, stock_name, updated_at)
            VALUES($stock_no, $stock_name, CURRENT_TIMESTAMP)
            ON CONFLICT(stock_no) DO UPDATE SET
                stock_name = CASE
                    WHEN excluded.stock_name <> '' THEN excluded.stock_name
                    ELSE stocks.stock_name
                END,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo.Trim());
        command.Parameters.AddWithValue("$stock_name", stockName.Trim());
        command.ExecuteNonQuery();
    }

    public void UpsertBranch(SqliteConnection connection, BrokerBranch branch)
    {
        var branchKey = string.IsNullOrWhiteSpace(branch.BranchId) ? branch.MajorId : branch.BranchId;
        if (string.IsNullOrWhiteSpace(branchKey))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO broker_branches(
                branch_key, major_id, major_name, branch_id, branch_name, address,
                latitude, longitude, camp_tag, notes, updated_at)
            VALUES(
                $branch_key, $major_id, $major_name, $branch_id, $branch_name, $address,
                $latitude, $longitude, $camp_tag, $notes, CURRENT_TIMESTAMP)
            ON CONFLICT(branch_key) DO UPDATE SET
                major_id = excluded.major_id,
                major_name = CASE WHEN excluded.major_name <> '' THEN excluded.major_name ELSE broker_branches.major_name END,
                branch_id = excluded.branch_id,
                branch_name = CASE WHEN excluded.branch_name <> '' THEN excluded.branch_name ELSE broker_branches.branch_name END,
                address = CASE WHEN excluded.address <> '' THEN excluded.address ELSE broker_branches.address END,
                latitude = COALESCE(excluded.latitude, broker_branches.latitude),
                longitude = COALESCE(excluded.longitude, broker_branches.longitude),
                camp_tag = CASE WHEN excluded.camp_tag <> '未知' THEN excluded.camp_tag ELSE broker_branches.camp_tag END,
                notes = CASE WHEN excluded.notes <> '' THEN excluded.notes ELSE broker_branches.notes END,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$branch_key", branchKey);
        command.Parameters.AddWithValue("$major_id", branch.MajorId);
        command.Parameters.AddWithValue("$major_name", branch.MajorName);
        command.Parameters.AddWithValue("$branch_id", branch.BranchId);
        command.Parameters.AddWithValue("$branch_name", branch.BranchName);
        command.Parameters.AddWithValue("$address", branch.Address);
        AddNullable(command, "$latitude", branch.Latitude);
        AddNullable(command, "$longitude", branch.Longitude);
        command.Parameters.AddWithValue("$camp_tag", string.IsNullOrWhiteSpace(branch.CampTag) ? "未知" : branch.CampTag);
        command.Parameters.AddWithValue("$notes", branch.Notes);
        command.ExecuteNonQuery();
    }

    public void UpsertTrade(SqliteConnection connection, BrokerTradeRecord record)
    {
        UpsertStock(connection, record.StockNo, record.StockName);
        UpsertBranch(connection, new BrokerBranch
        {
            MajorId = record.MajorId,
            MajorName = record.MajorName,
            BranchId = record.BranchId,
            BranchName = record.BranchName
        });

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO broker_trades(
                trade_date, period_days, stock_no, stock_name, major_id, major_name,
                branch_id, branch_name, buy_qty, sell_qty, net_qty, amount,
                avg_price, close_price, source, imported_at)
            VALUES(
                $trade_date, $period_days, $stock_no, $stock_name, $major_id, $major_name,
                $branch_id, $branch_name, $buy_qty, $sell_qty, $net_qty, $amount,
                $avg_price, $close_price, $source, CURRENT_TIMESTAMP)
            ON CONFLICT(trade_date, period_days, stock_no, major_id, branch_id, source) DO UPDATE SET
                stock_name = excluded.stock_name,
                major_name = excluded.major_name,
                branch_name = excluded.branch_name,
                buy_qty = excluded.buy_qty,
                sell_qty = excluded.sell_qty,
                net_qty = excluded.net_qty,
                amount = excluded.amount,
                avg_price = excluded.avg_price,
                close_price = excluded.close_price,
                imported_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$trade_date", record.TradeDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$period_days", record.PeriodDays);
        command.Parameters.AddWithValue("$stock_no", record.StockNo);
        command.Parameters.AddWithValue("$stock_name", record.StockName);
        command.Parameters.AddWithValue("$major_id", record.MajorId);
        command.Parameters.AddWithValue("$major_name", record.MajorName);
        command.Parameters.AddWithValue("$branch_id", record.BranchId);
        command.Parameters.AddWithValue("$branch_name", record.BranchName);
        command.Parameters.AddWithValue("$buy_qty", record.BuyQty);
        command.Parameters.AddWithValue("$sell_qty", record.SellQty);
        command.Parameters.AddWithValue("$net_qty", record.NetQty);
        command.Parameters.AddWithValue("$amount", record.Amount);
        command.Parameters.AddWithValue("$avg_price", record.AvgPrice);
        AddNullable(command, "$close_price", record.ClosePrice);
        command.Parameters.AddWithValue("$source", record.Source);
        command.ExecuteNonQuery();

        if (record.ClosePrice.HasValue)
        {
            UpsertPrice(connection, record.StockNo, record.TradeDate, record.ClosePrice.Value, record.Source);
        }
    }

    public void UpsertPrice(SqliteConnection connection, string stockNo, DateOnly tradeDate, decimal closePrice, string source)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO price_history(stock_no, trade_date, close_price, source)
            VALUES($stock_no, $trade_date, $close_price, $source)
            ON CONFLICT(stock_no, trade_date) DO UPDATE SET
                close_price = excluded.close_price,
                source = excluded.source;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo);
        command.Parameters.AddWithValue("$trade_date", tradeDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$close_price", closePrice);
        command.Parameters.AddWithValue("$source", source);
        command.ExecuteNonQuery();
    }

    public void UpsertCompanyLocation(SqliteConnection connection, CompanyLocation location)
    {
        if (string.IsNullOrWhiteSpace(location.StockNo))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO company_locations(stock_no, company_name, address, latitude, longitude, updated_at)
            VALUES($stock_no, $company_name, $address, $latitude, $longitude, CURRENT_TIMESTAMP)
            ON CONFLICT(stock_no) DO UPDATE SET
                company_name = excluded.company_name,
                address = excluded.address,
                latitude = excluded.latitude,
                longitude = excluded.longitude,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$stock_no", location.StockNo);
        command.Parameters.AddWithValue("$company_name", location.CompanyName);
        command.Parameters.AddWithValue("$address", location.Address);
        AddNullable(command, "$latitude", location.Latitude);
        AddNullable(command, "$longitude", location.Longitude);
        command.ExecuteNonQuery();
    }

    public void InsertInsiderEvent(SqliteConnection connection, InsiderEvent insiderEvent)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO insider_events(stock_no, event_date, holder_name, change_qty, source, imported_at)
            VALUES($stock_no, $event_date, $holder_name, $change_qty, $source, CURRENT_TIMESTAMP);
            """;
        command.Parameters.AddWithValue("$stock_no", insiderEvent.StockNo);
        command.Parameters.AddWithValue("$event_date", insiderEvent.EventDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$holder_name", insiderEvent.HolderName);
        command.Parameters.AddWithValue("$change_qty", insiderEvent.ChangeQty);
        command.Parameters.AddWithValue("$source", insiderEvent.Source);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<BrokerTradeRecord> GetTrades(string stockNo, DateOnly from, DateOnly to, int periodDays = 1)
    {
        using var connection = OpenConnection();
        var preferredSource = "";
        if (HasTradeSource(connection, stockNo, from, to, periodDays, "fubon-stock-zco"))
        {
            preferredSource = "fubon-stock-zco";
        }
        else if (HasTradeSource(connection, stockNo, from, to, periodDays, "fubon-moneydj"))
        {
            preferredSource = "fubon-moneydj";
        }
        var sourceFilter = string.IsNullOrWhiteSpace(preferredSource) ? "" : "  AND source = $source";
        using var command = connection.CreateCommand();
        command.CommandText = string.Join(Environment.NewLine, new[]
        {
            "SELECT id, trade_date, period_days, stock_no, stock_name, major_id, major_name,",
            "       branch_id, branch_name, buy_qty, sell_qty, net_qty, amount, avg_price,",
            "       close_price, source",
            "FROM broker_trades",
            "WHERE stock_no = $stock_no",
            "  AND trade_date BETWEEN $from AND $to",
            "  AND period_days = $period_days",
            sourceFilter,
            "ORDER BY trade_date, major_name, branch_name;"
        }.Where(line => !string.IsNullOrWhiteSpace(line)));
        command.Parameters.AddWithValue("$stock_no", stockNo.Trim());
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$period_days", periodDays);
        if (!string.IsNullOrWhiteSpace(preferredSource))
        {
            command.Parameters.AddWithValue("$source", preferredSource);
        }

        var rows = new List<BrokerTradeRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadTrade(reader));
        }

        return rows;
    }

    public IReadOnlyList<BrokerStockDailyRow> GetBrokerStockDailyRows(
        string stockNo,
        string majorId,
        string branchId,
        DateOnly from,
        DateOnly to)
    {
        using var connection = OpenConnection();
        var sourceFilter = HasBrokerStockSource(connection, stockNo, majorId, branchId, from, to, "fubon-zco0-detail")
            ? "  AND t.source = 'fubon-zco0-detail'"
            : "";
        using var command = connection.CreateCommand();
        command.CommandText = string.Join(Environment.NewLine, new[]
        {
            "SELECT t.trade_date, t.buy_qty, t.sell_qty, t.net_qty,",
            "       CASE WHEN t.avg_price > 0 THEN t.avg_price ELSE COALESCE(p.close_price, 0) END AS avg_price,",
            "       p.close_price, t.source",
            "FROM broker_trades t",
            "LEFT JOIN price_history p ON p.stock_no = t.stock_no AND p.trade_date = t.trade_date",
            "WHERE t.stock_no = $stock_no",
            "  AND t.major_id = $major_id",
            "  AND t.branch_id = $branch_id",
            "  AND t.trade_date BETWEEN $from AND $to",
            "  AND t.period_days = 1",
            sourceFilter,
            "ORDER BY t.trade_date DESC;"
        }.Where(line => !string.IsNullOrWhiteSpace(line)));
        command.Parameters.AddWithValue("$stock_no", stockNo.Trim());
        command.Parameters.AddWithValue("$major_id", majorId.Trim());
        command.Parameters.AddWithValue("$branch_id", branchId.Trim());
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));

        var rows = new List<BrokerStockDailyRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            decimal? closePrice = reader.IsDBNull(5) ? null : reader.GetDecimal(5);
            rows.Add(new BrokerStockDailyRow
            {
                TradeDate = DateOnly.Parse(reader.GetString(0)).ToString("yyyy/MM/dd"),
                BuyQty = reader.GetDecimal(1),
                SellQty = reader.GetDecimal(2),
                NetQty = reader.GetDecimal(3),
                AvgPrice = reader.GetDecimal(4),
                ClosePrice = closePrice,
                Source = reader.GetString(6)
            });
        }

        return rows;
    }

    public IReadOnlyList<StockBranchRankRow> GetStockBranchRankRows(string stockNo, DateOnly from, DateOnly to)
    {
        using var connection = OpenConnection();
        var sourceFilter = HasTradeSource(connection, stockNo, from, to, 1, "fubon-stock-zco")
            ? "  AND t.source = 'fubon-stock-zco'"
            : "";
        using var command = connection.CreateCommand();
        command.CommandText = string.Join(Environment.NewLine, new[]
        {
            "SELECT t.major_id, t.major_name, t.branch_id, t.branch_name,",
            "       SUM(t.buy_qty) AS buy_qty,",
            "       SUM(t.sell_qty) AS sell_qty,",
            "       SUM(t.net_qty) AS net_qty,",
            "       SUM(CASE",
            "           WHEN t.amount > 0 THEN t.amount",
            "           WHEN t.avg_price > 0 THEN (t.buy_qty + t.sell_qty) * t.avg_price",
            "           WHEN t.close_price IS NOT NULL THEN (t.buy_qty + t.sell_qty) * t.close_price",
            "           WHEN p.close_price IS NOT NULL THEN (t.buy_qty + t.sell_qty) * p.close_price",
            "           ELSE 0 END) AS amount_sum,",
            "       SUM(CASE",
            "           WHEN t.amount > 0 OR t.avg_price > 0 OR t.close_price IS NOT NULL OR p.close_price IS NOT NULL",
            "           THEN t.buy_qty + t.sell_qty",
            "           ELSE 0 END) AS priced_qty,",
            "       COUNT(DISTINCT t.trade_date) AS trade_days,",
            "       MAX(t.trade_date) AS latest_trade_date,",
            "       COALESCE(a.is_starred, 0) AS is_starred,",
            "       COALESCE(a.note, '') AS note",
            "FROM broker_trades t",
            "LEFT JOIN price_history p ON p.stock_no = t.stock_no AND p.trade_date = t.trade_date",
            "LEFT JOIN branch_annotations a ON a.stock_no = t.stock_no AND a.major_id = t.major_id AND a.branch_id = t.branch_id",
            "WHERE t.stock_no = $stock_no",
            "  AND t.trade_date BETWEEN $from AND $to",
            "  AND t.period_days = 1",
            sourceFilter,
            "GROUP BY t.major_id, t.major_name, t.branch_id, t.branch_name, a.is_starred, a.note",
            "ORDER BY SUM(t.net_qty) DESC, SUM(t.buy_qty + t.sell_qty) DESC;"
        }.Where(line => !string.IsNullOrWhiteSpace(line)));
        command.Parameters.AddWithValue("$stock_no", stockNo.Trim());
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));

        var rows = new List<StockBranchRankRow>();
        using var reader = command.ExecuteReader();
        var rank = 1;
        while (reader.Read())
        {
            var buyQty = reader.GetDecimal(4);
            var sellQty = reader.GetDecimal(5);
            var amount = reader.GetDecimal(7);
            var pricedQty = reader.GetDecimal(8);
            rows.Add(new StockBranchRankRow
            {
                Rank = rank++,
                StockNo = stockNo,
                MajorId = reader.GetString(0),
                MajorName = reader.GetString(1),
                BranchId = reader.GetString(2),
                BranchName = reader.GetString(3),
                BuyQty = buyQty,
                SellQty = sellQty,
                NetQty = reader.GetDecimal(6),
                AvgPrice = pricedQty > 0 && amount > 0 ? Math.Round(amount / pricedQty, 2) : 0,
                TradeDays = reader.GetInt32(9),
                LatestTradeDate = DateOnly.Parse(reader.GetString(10)).ToString("yyyy/MM/dd"),
                IsStarred = reader.GetInt32(11) != 0,
                Note = reader.GetString(12)
            });
        }

        return rows;
    }

    public BranchAnnotation GetBranchAnnotation(string stockNo, string majorId, string branchId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT stock_no, major_id, branch_id, is_starred, note
            FROM branch_annotations
            WHERE stock_no = $stock_no
              AND major_id = $major_id
              AND branch_id = $branch_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo.Trim());
        command.Parameters.AddWithValue("$major_id", majorId.Trim());
        command.Parameters.AddWithValue("$branch_id", branchId.Trim());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new BranchAnnotation
            {
                StockNo = stockNo,
                MajorId = majorId,
                BranchId = branchId
            };
        }

        return new BranchAnnotation
        {
            StockNo = reader.GetString(0),
            MajorId = reader.GetString(1),
            BranchId = reader.GetString(2),
            IsStarred = reader.GetInt32(3) != 0,
            Note = reader.GetString(4)
        };
    }

    public void SetBranchStarred(string stockNo, string majorId, string branchId, bool isStarred)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO branch_annotations(stock_no, major_id, branch_id, is_starred, note, updated_at)
            VALUES($stock_no, $major_id, $branch_id, $is_starred, '', CURRENT_TIMESTAMP)
            ON CONFLICT(stock_no, major_id, branch_id) DO UPDATE SET
                is_starred = excluded.is_starred,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo.Trim());
        command.Parameters.AddWithValue("$major_id", majorId.Trim());
        command.Parameters.AddWithValue("$branch_id", branchId.Trim());
        command.Parameters.AddWithValue("$is_starred", isStarred ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public void SetBranchNote(string stockNo, string majorId, string branchId, string note)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO branch_annotations(stock_no, major_id, branch_id, is_starred, note, updated_at)
            VALUES($stock_no, $major_id, $branch_id, 0, $note, CURRENT_TIMESTAMP)
            ON CONFLICT(stock_no, major_id, branch_id) DO UPDATE SET
                note = excluded.note,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo.Trim());
        command.Parameters.AddWithValue("$major_id", majorId.Trim());
        command.Parameters.AddWithValue("$branch_id", branchId.Trim());
        command.Parameters.AddWithValue("$note", note);
        command.ExecuteNonQuery();
    }

    public string GetStockNote(string stockNo)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT note
            FROM stock_notes
            WHERE stock_no = $stock_no
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo.Trim());
        return command.ExecuteScalar() as string ?? "";
    }

    public void SetStockNote(string stockNo, string note)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO stock_notes(stock_no, note, updated_at)
            VALUES($stock_no, $note, CURRENT_TIMESTAMP)
            ON CONFLICT(stock_no) DO UPDATE SET
                note = excluded.note,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo.Trim());
        command.Parameters.AddWithValue("$note", note);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<StockPriceRow> GetStockClosePrices(string stockNo, DateOnly from, DateOnly to)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT trade_date, close_price
            FROM price_history
            WHERE stock_no = $stock_no
              AND trade_date BETWEEN $from AND $to
            ORDER BY trade_date;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo.Trim());
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));

        var rows = new List<StockPriceRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StockPriceRow
            {
                TradeDate = DateOnly.Parse(reader.GetString(0)),
                ClosePrice = reader.GetDecimal(1)
            });
        }

        return rows;
    }

    public StockPriceRow? GetLatestStockClosePriceOnOrBefore(string stockNo, DateOnly date)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT trade_date, close_price
            FROM price_history
            WHERE stock_no = $stock_no
              AND trade_date <= $date
            ORDER BY trade_date DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo.Trim());
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new StockPriceRow
        {
            TradeDate = DateOnly.Parse(reader.GetString(0)),
            ClosePrice = reader.GetDecimal(1)
        };
    }

    private static bool HasBrokerStockSource(
        SqliteConnection connection,
        string stockNo,
        string majorId,
        string branchId,
        DateOnly from,
        DateOnly to,
        string source)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM broker_trades
            WHERE stock_no = $stock_no
              AND major_id = $major_id
              AND branch_id = $branch_id
              AND trade_date BETWEEN $from AND $to
              AND source = $source;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo.Trim());
        command.Parameters.AddWithValue("$major_id", majorId.Trim());
        command.Parameters.AddWithValue("$branch_id", branchId.Trim());
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$source", source);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static bool HasTradeSource(
        SqliteConnection connection,
        string stockNo,
        DateOnly from,
        DateOnly to,
        int periodDays,
        string source)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM broker_trades
            WHERE stock_no = $stock_no
              AND trade_date BETWEEN $from AND $to
              AND period_days = $period_days
              AND source = $source;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo.Trim());
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$period_days", periodDays);
        command.Parameters.AddWithValue("$source", source);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    public IReadOnlyList<StockOption> GetStocks()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT stock_no, stock_name
            FROM stocks
            ORDER BY stock_no;
            """;
        var rows = new List<StockOption>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StockOption
            {
                StockNo = reader.GetString(0),
                StockName = reader.GetString(1)
            });
        }

        return rows;
    }

    public IReadOnlyList<BrokerBranch> GetBranches()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT major_id, major_name, branch_id, branch_name, address,
                   latitude, longitude, camp_tag, notes
            FROM broker_branches
            ORDER BY major_name, branch_name;
            """;
        var rows = new List<BrokerBranch>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new BrokerBranch
            {
                MajorId = reader.GetString(0),
                MajorName = reader.GetString(1),
                BranchId = reader.GetString(2),
                BranchName = reader.GetString(3),
                Address = reader.GetString(4),
                Latitude = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                Longitude = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                CampTag = reader.GetString(7),
                Notes = reader.GetString(8)
            });
        }

        return rows;
    }

    public BrokerBranch? GetBranch(string branchKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT major_id, major_name, branch_id, branch_name, address,
                   latitude, longitude, camp_tag, notes
            FROM broker_branches
            WHERE branch_key = $branch_key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$branch_key", branchKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new BrokerBranch
        {
            MajorId = reader.GetString(0),
            MajorName = reader.GetString(1),
            BranchId = reader.GetString(2),
            BranchName = reader.GetString(3),
            Address = reader.GetString(4),
            Latitude = reader.IsDBNull(5) ? null : reader.GetDouble(5),
            Longitude = reader.IsDBNull(6) ? null : reader.GetDouble(6),
            CampTag = reader.GetString(7),
            Notes = reader.GetString(8)
        };
    }

    public CompanyLocation? GetCompanyLocation(string stockNo)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT stock_no, company_name, address, latitude, longitude
            FROM company_locations
            WHERE stock_no = $stock_no
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new CompanyLocation
        {
            StockNo = reader.GetString(0),
            CompanyName = reader.GetString(1),
            Address = reader.GetString(2),
            Latitude = reader.IsDBNull(3) ? null : reader.GetDouble(3),
            Longitude = reader.IsDBNull(4) ? null : reader.GetDouble(4)
        };
    }

    public IReadOnlyList<InsiderEvent> GetInsiderEvents(string stockNo, DateOnly from, DateOnly to)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, stock_no, event_date, holder_name, change_qty, source
            FROM insider_events
            WHERE stock_no = $stock_no
              AND event_date BETWEEN $from AND $to
            ORDER BY event_date;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo);
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
        var rows = new List<InsiderEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new InsiderEvent
            {
                Id = reader.GetInt64(0),
                StockNo = reader.GetString(1),
                EventDate = DateOnly.Parse(reader.GetString(2)),
                HolderName = reader.GetString(3),
                ChangeQty = reader.GetDecimal(4),
                Source = reader.GetString(5)
            });
        }

        return rows;
    }

    public Dictionary<string, int> GetTableCounts()
    {
        using var connection = OpenConnection();
        string[] tables = ["stocks", "broker_branches", "broker_trades", "price_history", "company_locations", "insider_events", "stock_prefetch_state", "stock_notes"];
        var result = new Dictionary<string, int>();
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            result[table] = Convert.ToInt32(command.ExecuteScalar());
        }

        return result;
    }

    public bool IsPrefetchComplete(string stockNo, DateOnly from, DateOnly to)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM stock_prefetch_state
            WHERE stock_no = $stock_no
              AND status = 'ok'
              AND from_date <= $from_date
              AND to_date >= $to_date;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo);
        command.Parameters.AddWithValue("$from_date", from.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$to_date", to.ToString("yyyy-MM-dd"));
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    public StockPrefetchState? GetPrefetchState(string stockNo)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT stock_no, from_date, to_date, status, message, rows_imported, updated_at
            FROM stock_prefetch_state
            WHERE stock_no = $stock_no
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new StockPrefetchState
        {
            StockNo = reader.GetString(0),
            FromDate = DateOnly.TryParse(reader.GetString(1), out var fromDate) ? fromDate : null,
            ToDate = DateOnly.TryParse(reader.GetString(2), out var toDate) ? toDate : null,
            Status = reader.GetString(3),
            Message = reader.GetString(4),
            RowsImported = reader.GetInt32(5),
            UpdatedAt = reader.GetString(6)
        };
    }

    public void UpsertPrefetchState(string stockNo, DateOnly from, DateOnly to, string status, string message, int rowsImported)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO stock_prefetch_state(stock_no, from_date, to_date, status, message, rows_imported, updated_at)
            VALUES($stock_no, $from_date, $to_date, $status, $message, $rows_imported, CURRENT_TIMESTAMP)
            ON CONFLICT(stock_no) DO UPDATE SET
                from_date = excluded.from_date,
                to_date = excluded.to_date,
                status = excluded.status,
                message = excluded.message,
                rows_imported = excluded.rows_imported,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$stock_no", stockNo);
        command.Parameters.AddWithValue("$from_date", from.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$to_date", to.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", message.Length > 900 ? message[..900] : message);
        command.Parameters.AddWithValue("$rows_imported", rowsImported);
        command.ExecuteNonQuery();
    }

    public int CountPrefetchComplete(DateOnly from, DateOnly to)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM stock_prefetch_state
            WHERE status = 'ok'
              AND from_date <= $from_date
              AND to_date >= $to_date;
            """;
        command.Parameters.AddWithValue("$from_date", from.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$to_date", to.ToString("yyyy-MM-dd"));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void AddSyncRun(string source, string status, string message, int rowsImported)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_runs(source, status, message, rows_imported)
            VALUES($source, $status, $message, $rows_imported);
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$rows_imported", rowsImported);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
    }

    private static BrokerTradeRecord ReadTrade(SqliteDataReader reader)
    {
        return new BrokerTradeRecord
        {
            Id = reader.GetInt64(0),
            TradeDate = DateOnly.Parse(reader.GetString(1)),
            PeriodDays = reader.GetInt32(2),
            StockNo = reader.GetString(3),
            StockName = reader.GetString(4),
            MajorId = reader.GetString(5),
            MajorName = reader.GetString(6),
            BranchId = reader.GetString(7),
            BranchName = reader.GetString(8),
            BuyQty = reader.GetDecimal(9),
            SellQty = reader.GetDecimal(10),
            NetQty = reader.GetDecimal(11),
            Amount = reader.GetDecimal(12),
            AvgPrice = reader.GetDecimal(13),
            ClosePrice = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
            Source = reader.GetString(15)
        };
    }

    private static void AddNullable(SqliteCommand command, string name, double? value)
    {
        command.Parameters.AddWithValue(name, value.HasValue ? value.Value : DBNull.Value);
    }

    private static void AddNullable(SqliteCommand command, string name, decimal? value)
    {
        command.Parameters.AddWithValue(name, value.HasValue ? value.Value : DBNull.Value);
    }
}
