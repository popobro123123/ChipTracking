using System.ComponentModel;
using System.Globalization;

namespace ChipTracking;

public enum BrokerTrendChartMode
{
    PriceLine,
    NetBar
}

public sealed class BrokerTrendChart : Control
{
    private readonly List<BrokerStockDailyRow> _rows = [];
    private Point? _mousePoint;
    private int? _hoverIndex;
    private bool _externalHover;

    public BrokerTrendChart()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        Dock = DockStyle.Fill;
        MinimumSize = new Size(240, 140);
        BackColor = Color.FromArgb(24, 24, 24);
        ForeColor = Color.FromArgb(220, 220, 220);
        Font = new Font("Microsoft JhengHei UI", 9F);
    }

    public event Action<int?>? HoverIndexChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public BrokerTrendChartMode Mode { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "請先選擇分點";

    public void SetRows(IEnumerable<BrokerStockDailyRow> rows)
    {
        _rows.Clear();
        _rows.AddRange(rows.OrderBy(row => ParseDate(row.TradeDate)));
        _mousePoint = null;
        _hoverIndex = null;
        _externalHover = false;
        Invalidate();
    }

    public void SetExternalHoverIndex(int? index)
    {
        if (index.HasValue && (index.Value < 0 || index.Value >= _rows.Count))
        {
            index = null;
        }

        _externalHover = index.HasValue;
        _hoverIndex = index;
        _mousePoint = null;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var plot = GetPlotRectangle();
        if (_rows.Count == 0 || !plot.Contains(e.Location))
        {
            ClearLocalHover();
            return;
        }

        var index = GetNearestIndex(plot, e.X);
        _mousePoint = e.Location;
        _externalHover = false;
        if (_hoverIndex != index)
        {
            _hoverIndex = index;
            HoverIndexChanged?.Invoke(index);
        }

        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        ClearLocalHover();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var plot = GetPlotRectangle();
        DrawGrid(g, plot);

        using var titleBrush = new SolidBrush(Color.FromArgb(255, 206, 36));
        g.DrawString(Title, Font, titleBrush, 12, 9);

        if (_rows.Count == 0)
        {
            using var brush = new SolidBrush(Color.FromArgb(165, 165, 165));
            g.DrawString(EmptyText, Font, brush, plot.Left + 12, plot.Top + 14);
            return;
        }

        if (Mode == BrokerTrendChartMode.PriceLine)
        {
            DrawPriceLine(g, plot);
        }
        else
        {
            DrawNetBars(g, plot);
        }

        DrawDateLabels(g, plot);
        DrawCrosshair(g, plot);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    private void ClearLocalHover()
    {
        if (_externalHover)
        {
            return;
        }

        if (_hoverIndex.HasValue || _mousePoint.HasValue)
        {
            _hoverIndex = null;
            _mousePoint = null;
            HoverIndexChanged?.Invoke(null);
            Invalidate();
        }
    }

    private Rectangle GetPlotRectangle()
    {
        var left = 58;
        var top = 32;
        var rightPadding = 82;
        var bottomPadding = 34;
        return new Rectangle(
            left,
            top,
            Math.Max(10, ClientSize.Width - left - rightPadding),
            Math.Max(10, ClientSize.Height - top - bottomPadding));
    }

    private void DrawGrid(Graphics g, Rectangle plot)
    {
        using var gridPen = new Pen(Color.FromArgb(48, 48, 48));
        using var axisPen = new Pen(Color.FromArgb(95, 95, 95));
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + plot.Height * i / 4;
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
        }

        for (var i = 0; i <= 6; i++)
        {
            var x = plot.Left + plot.Width * i / 6;
            g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
        }

        g.DrawRectangle(axisPen, plot);
    }

    private void DrawPriceLine(Graphics g, Rectangle plot)
    {
        var values = GetPriceValues();
        var nonZero = values.Where(value => value > 0).ToList();
        if (nonZero.Count == 0)
        {
            using var brush = new SolidBrush(Color.FromArgb(165, 165, 165));
            g.DrawString("這段期間沒有價格資料", Font, brush, plot.Left + 12, plot.Top + 14);
            return;
        }

        var min = nonZero.Min();
        var max = nonZero.Max();
        NormalizeRange(ref min, ref max);

        var points = new List<PointF>();
        for (var i = 0; i < _rows.Count; i++)
        {
            var value = values[i];
            if (value <= 0)
            {
                continue;
            }

            points.Add(new PointF(GetX(plot, i), GetY(plot, value, min, max)));
        }

        using var linePen = new Pen(Color.FromArgb(255, 206, 36), 2.2F);
        if (points.Count >= 2)
        {
            g.DrawLines(linePen, points.ToArray());
        }

        using var markerBrush = new SolidBrush(Color.FromArgb(255, 206, 36));
        foreach (var point in points)
        {
            g.FillEllipse(markerBrush, point.X - 2.5F, point.Y - 2.5F, 5F, 5F);
        }

        using var labelBrush = new SolidBrush(Color.FromArgb(218, 218, 218));
        g.DrawString($"{max:N2}", Font, labelBrush, plot.Right + 5, plot.Top - 5);
        g.DrawString($"{min:N2}", Font, labelBrush, plot.Right + 5, plot.Bottom - 15);
    }

    private void DrawNetBars(Graphics g, Rectangle plot)
    {
        var maxAbs = GetMaxAbsNet();
        var zeroY = plot.Top + plot.Height / 2;
        using var axisPen = new Pen(Color.FromArgb(120, 120, 120));
        g.DrawLine(axisPen, plot.Left, zeroY, plot.Right, zeroY);

        var barWidth = Math.Max(2F, plot.Width / Math.Max(1F, _rows.Count * 1.45F));
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var height = (float)(Math.Abs(row.NetQty) / maxAbs * (plot.Height / 2 - 5));
            var x = GetX(plot, i) - barWidth / 2;
            var y = row.NetQty >= 0 ? zeroY - height : zeroY;
            using var brush = new SolidBrush(row.NetQty >= 0 ? Color.FromArgb(205, 52, 52) : Color.FromArgb(53, 168, 58));
            g.FillRectangle(brush, x, y, barWidth, Math.Max(1, height));
        }

        using var labelBrush = new SolidBrush(Color.FromArgb(218, 218, 218));
        g.DrawString($"{maxAbs:N0}", Font, labelBrush, plot.Right + 5, plot.Top - 5);
        g.DrawString($"-{maxAbs:N0}", Font, labelBrush, plot.Right + 5, plot.Bottom - 15);
    }

    private void DrawCrosshair(Graphics g, Rectangle plot)
    {
        if (_hoverIndex is not { } index || _rows.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, _rows.Count - 1);
        var row = _rows[index];
        var x = GetX(plot, index);
        var y = _mousePoint.HasValue && !_externalHover && plot.Contains(_mousePoint.Value)
            ? Math.Clamp(_mousePoint.Value.Y, plot.Top, plot.Bottom)
            : GetDataY(plot, index);

        using var crossPen = new Pen(Color.FromArgb(190, 190, 190)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        g.DrawLine(crossPen, x, plot.Top, x, plot.Bottom);
        g.DrawLine(crossPen, plot.Left, y, plot.Right, y);

        var label = Mode == BrokerTrendChartMode.PriceLine
            ? $"{row.TradeDate}  價 {GetPrice(row):N2}"
            : $"{row.TradeDate}  買賣超 {row.NetQty:N0}";
        DrawHoverLabel(g, plot, x, y, label);
    }

    private float GetDataY(Rectangle plot, int index)
    {
        if (Mode == BrokerTrendChartMode.PriceLine)
        {
            var values = GetPriceValues();
            var nonZero = values.Where(value => value > 0).ToList();
            if (nonZero.Count == 0 || values[index] <= 0)
            {
                return plot.Top + plot.Height / 2F;
            }

            var min = nonZero.Min();
            var max = nonZero.Max();
            NormalizeRange(ref min, ref max);
            return GetY(plot, values[index], min, max);
        }

        var zeroY = plot.Top + plot.Height / 2F;
        var maxAbs = GetMaxAbsNet();
        var height = (float)(Math.Abs(_rows[index].NetQty) / maxAbs * (plot.Height / 2 - 5));
        return _rows[index].NetQty >= 0 ? zeroY - height : zeroY + height;
    }

    private void DrawHoverLabel(Graphics g, Rectangle plot, float x, float y, string label)
    {
        var size = g.MeasureString(label, Font);
        var labelWidth = size.Width + 14;
        var labelHeight = size.Height + 8;
        var labelX = x + 10;
        var labelY = y - labelHeight - 10;

        if (labelX + labelWidth > plot.Right)
        {
            labelX = x - labelWidth - 10;
        }

        if (labelY < plot.Top)
        {
            labelY = y + 10;
        }

        var rect = new RectangleF(labelX, labelY, labelWidth, labelHeight);
        using var bg = new SolidBrush(Color.FromArgb(230, 18, 18, 18));
        using var border = new Pen(Color.FromArgb(255, 206, 36));
        using var text = new SolidBrush(Color.FromArgb(245, 245, 245));
        g.FillRectangle(bg, rect);
        g.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);
        g.DrawString(label, Font, text, rect.X + 7, rect.Y + 4);
    }

    private void DrawDateLabels(Graphics g, Rectangle plot)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        using var labelBrush = new SolidBrush(Color.FromArgb(170, 170, 170));
        var first = _rows.First().TradeDate;
        var last = _rows.Last().TradeDate;
        g.DrawString(first, Font, labelBrush, plot.Left, plot.Bottom + 7);

        var lastSize = g.MeasureString(last, Font);
        g.DrawString(last, Font, labelBrush, plot.Right - lastSize.Width, plot.Bottom + 7);
    }

    private List<decimal> GetPriceValues()
    {
        return _rows.Select(GetPrice).ToList();
    }

    private decimal GetMaxAbsNet()
    {
        var maxAbs = _rows.Select(row => Math.Abs(row.NetQty)).DefaultIfEmpty(0).Max();
        return maxAbs <= 0 ? 1 : maxAbs;
    }

    private static decimal GetPrice(BrokerStockDailyRow row)
    {
        return row.AvgPrice > 0 ? row.AvgPrice : row.ClosePrice ?? 0;
    }

    private static void NormalizeRange(ref decimal min, ref decimal max)
    {
        if (min == max)
        {
            min -= 1;
            max += 1;
        }
    }

    private static float GetX(Rectangle plot, int index, int count)
    {
        return plot.Left + (float)(plot.Width * (index / Math.Max(1.0, count - 1.0)));
    }

    private float GetX(Rectangle plot, int index)
    {
        return GetX(plot, index, _rows.Count);
    }

    private static float GetY(Rectangle plot, decimal value, decimal min, decimal max)
    {
        return plot.Bottom - (float)((value - min) / (max - min) * plot.Height);
    }

    private int GetNearestIndex(Rectangle plot, int mouseX)
    {
        if (_rows.Count <= 1)
        {
            return 0;
        }

        var ratio = (mouseX - plot.Left) / (double)Math.Max(1, plot.Width);
        return Math.Clamp((int)Math.Round(ratio * (_rows.Count - 1)), 0, _rows.Count - 1);
    }

    private static DateOnly ParseDate(string text)
    {
        return DateOnly.TryParseExact(text, "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : DateOnly.MinValue;
    }
}
