using System.Drawing.Drawing2D;
using System.Globalization;

namespace CodexUsageTray;

public sealed class UsageDetailsForm : Form
{
    private static readonly Color Background = Color.FromArgb(15, 23, 42);
    private static readonly Color Panel = Color.FromArgb(30, 41, 59);
    private static readonly Color TextPrimary = Color.FromArgb(241, 245, 249);
    private static readonly Color TextMuted = Color.FromArgb(148, 163, 184);
    private static readonly Color Accent = Color.FromArgb(52, 211, 153);

    private readonly ComboBox _rangePicker;
    private readonly Label _summary;
    private readonly Label _rangeNote;
    private readonly DailyUsageChart _chart;
    private readonly DataGridView _projects;
    private readonly Label _footnote;
    private ApiEquivalentEstimate _estimate = new();

    public UsageDetailsForm()
    {
        Name = "UsageDetailsForm";
        AccessibleName = "Codex API-equivalent usage details";
        Text = "Codex Usage Details";
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Background;
        ForeColor = TextPrimary;
        MinimumSize = new Size(720, 520);
        ClientSize = new Size(860, 650);
        FormBorderStyle = FormBorderStyle.Sizable;

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Background,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(22, 18, 22, 8)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = CreateLabel("API-equivalent usage", 16, FontStyle.Bold, TextPrimary);
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        _rangePicker = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Panel,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            AccessibleName = "Usage date range"
        };
        _rangePicker.Items.AddRange(
        [
            new RangeItem("Last 7 days", UsageHistoryRange.SevenDays),
            new RangeItem("Last 30 days", UsageHistoryRange.ThirtyDays),
            new RangeItem("All time", UsageHistoryRange.AllTime)
        ]);

        _summary = CreateLabel("No local token history found", 10.5f, FontStyle.Bold, Accent);
        _summary.Dock = DockStyle.Fill;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.AutoEllipsis = true;
        _rangeNote = CreateLabel(string.Empty, 8.5f, FontStyle.Regular, TextMuted);
        _rangeNote.Dock = DockStyle.Fill;
        _rangeNote.TextAlign = ContentAlignment.MiddleRight;
        _rangeNote.AutoEllipsis = true;
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(_rangePicker, 1, 0);
        header.Controls.Add(_summary, 0, 1);
        header.Controls.Add(_rangeNote, 1, 1);

        _chart = new DailyUsageChart
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(22, 0, 22, 12),
            BackColor = Panel
        };

        _projects = new DataGridView
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(22, 0, 22, 8),
            BackgroundColor = Panel,
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            EnableHeadersVisualStyles = false,
            AccessibleName = "Project API-equivalent usage"
        };
        var headerFont = new Font("Segoe UI", 9, FontStyle.Bold);
        var rowFont = new Font("Segoe UI", 9.5f);
        Disposed += (_, _) =>
        {
            headerFont.Dispose();
            rowFont.Dispose();
        };
        _projects.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(51, 65, 85),
            ForeColor = TextPrimary,
            SelectionBackColor = Color.FromArgb(51, 65, 85),
            Font = headerFont
        };
        _projects.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Panel,
            ForeColor = TextPrimary,
            SelectionBackColor = Color.FromArgb(6, 95, 70),
            SelectionForeColor = Color.White,
            Font = rowFont
        };
        _projects.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(25, 35, 52),
            ForeColor = TextPrimary,
            SelectionBackColor = Color.FromArgb(6, 95, 70),
            SelectionForeColor = Color.White
        };
        _projects.GridColor = Color.FromArgb(51, 65, 85);
        _projects.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Project",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 220
        });
        _projects.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Tokens",
            Width = 135,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        _projects.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Estimated USD",
            Width = 130,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        _projects.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Unpriced",
            Width = 120,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
        });

        _footnote = CreateLabel(
            "Standard API equivalent only. This is not a ChatGPT or Codex plan charge.",
            8.5f,
            FontStyle.Regular,
            TextMuted);
        _footnote.Dock = DockStyle.Fill;
        _footnote.Padding = new Padding(22, 0, 22, 12);
        _footnote.TextAlign = ContentAlignment.MiddleLeft;
        _footnote.AutoEllipsis = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Background
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_chart, 0, 1);
        layout.Controls.Add(_projects, 0, 2);
        layout.Controls.Add(_footnote, 0, 3);
        Controls.Add(layout);

        _rangePicker.SelectedIndexChanged += (_, _) => RenderSelectedRange();
        _rangePicker.SelectedIndex = 0;
        FormClosing += (_, args) =>
        {
            if (args.CloseReason == CloseReason.UserClosing)
            {
                args.Cancel = true;
                Hide();
            }
        };
    }

    public void UpdateEstimate(ApiEquivalentEstimate estimate)
    {
        _estimate = estimate;
        RenderSelectedRange();
    }

    public void ShowNearCursor()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = Math.Clamp(area.Left + ((area.Width - Width) / 2), area.Left, Math.Max(area.Left, area.Right - Width));
        var y = Math.Clamp(area.Top + ((area.Height - Height) / 2), area.Top, Math.Max(area.Top, area.Bottom - Height));
        Location = new Point(x, y);
        if (!Visible)
        {
            Show();
        }
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void RenderSelectedRange()
    {
        if (_rangePicker.SelectedItem is not RangeItem item)
        {
            return;
        }

        var summary = UsageHistoryCalculator.Summarize(
            _estimate,
            item.Range,
            DateOnly.FromDateTime(DateTime.Now));
        _summary.Text = summary.TotalTokens == 0
            ? "No dated token usage in this range"
            : $"{FormatTokens(summary.TotalTokens)} tokens · {FormatUsd(summary.EstimatedUsd, summary.UnpricedTokens)} estimated";
        _rangeNote.Text = item.Range == UsageHistoryRange.AllTime && summary.DailyUsage.Count > 30
            ? "Chart: latest 30 dated days"
            : $"{summary.Projects.Count} project{(summary.Projects.Count == 1 ? string.Empty : "s")}";
        _chart.SetData(summary.DailyUsage);

        _projects.Rows.Clear();
        foreach (var project in summary.Projects)
        {
            _projects.Rows.Add(
                project.Name,
                FormatTokens(project.TotalTokens),
                FormatUsd(project.EstimatedUsd, project.UnpricedTokens),
                project.UnpricedTokens == 0 ? "—" : FormatTokens(project.UnpricedTokens));
        }

        var notes = new List<string>
        {
            $"Prices {_estimate.PricingReferenceDate}",
            "Standard API equivalent only; not an actual plan charge"
        };
        if (_estimate.UsesProxyPricing)
        {
            notes.Add("Spark uses GPT-5.3-Codex proxy pricing");
        }
        if (summary.ExcludedUndatedTokens > 0)
        {
            notes.Add($"{FormatTokens(summary.ExcludedUndatedTokens)} tokens without timestamps excluded from this range");
        }
        _footnote.Text = string.Join(" · ", notes);
    }

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => $"{tokens / 1_000_000_000d:0.##}B",
        >= 1_000_000 => $"{tokens / 1_000_000d:0.##}M",
        >= 1_000 => $"{tokens / 1_000d:0.##}K",
        _ => tokens.ToString("N0", CultureInfo.CurrentCulture)
    };

    private static string FormatUsd(decimal amount, long unpricedTokens)
    {
        if (amount == 0 && unpricedTokens > 0)
        {
            return "Unpriced";
        }
        var text = amount is > 0 and < 0.01m
            ? "<$0.01"
            : amount.ToString("$#,##0.00", CultureInfo.GetCultureInfo("en-US"));
        return unpricedTokens > 0 ? $"{text}+" : text;
    }

    private static Label CreateLabel(string text, float size, FontStyle style, Color color)
    {
        var font = new Font("Segoe UI", size, style);
        var label = new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
            AutoSize = false,
            UseMnemonic = false
        };
        label.Disposed += (_, _) => font.Dispose();
        return label;
    }

    private sealed record RangeItem(string Text, UsageHistoryRange Range)
    {
        public override string ToString() => Text;
    }
}

internal sealed class DailyUsageChart : Control
{
    private static readonly Color Grid = Color.FromArgb(71, 85, 105);
    private static readonly Color MutedText = Color.FromArgb(148, 163, 184);
    private static readonly Color Bar = Color.FromArgb(52, 211, 153);
    private IReadOnlyList<DailyUsageEstimate> _data = [];

    public DailyUsageChart()
    {
        AccessibleName = "Daily API-equivalent usage chart";
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
    }

    public void SetData(IReadOnlyList<DailyUsageEstimate> data)
    {
        _data = data.Count > 30 ? data.Skip(data.Count - 30).ToList() : data;
        AccessibleDescription = _data.Count == 0
            ? "No dated usage"
            : $"{_data.Count} dated usage points from {_data[0].Date:MMM d} to {_data[^1].Date:MMM d}";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(BackColor);

        var titleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        var labelFont = new Font("Segoe UI", 8, FontStyle.Regular);
        try
        {
            if (_data.Count == 0 || _data.All(item => item.TotalTokens == 0))
            {
                graphics.DrawString("Daily estimated API cost", titleFont, Brushes.White, 16, 12);
                using var emptyBrush = new SolidBrush(MutedText);
                graphics.DrawString("No dated token usage", labelFont, emptyBrush, 16, 78);
                return;
            }

            var useCost = _data.Any(item => item.EstimatedUsd > 0);
            graphics.DrawString(
                useCost ? "Daily estimated API cost" : "Daily token usage",
                titleFont,
                Brushes.White,
                16,
                12);
            var max = useCost
                ? (double)_data.Max(item => item.EstimatedUsd)
                : _data.Max(item => (double)item.TotalTokens);
            if (max <= 0)
            {
                max = 1;
            }

            var plot = new RectangleF(16, 42, Math.Max(1, Width - 32), Math.Max(1, Height - 72));
            using var gridPen = new Pen(Grid, 1);
            graphics.DrawLine(gridPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
            graphics.DrawLine(gridPen, plot.Left, plot.Top + (plot.Height / 2), plot.Right, plot.Top + (plot.Height / 2));

            var slot = plot.Width / _data.Count;
            var barWidth = Math.Max(2, Math.Min(22, slot * 0.68f));
            using var barBrush = new SolidBrush(Bar);
            for (var index = 0; index < _data.Count; index++)
            {
                var item = _data[index];
                var value = useCost ? (double)item.EstimatedUsd : item.TotalTokens;
                var height = value <= 0 ? 0 : Math.Max(2, (float)(plot.Height * (value / max)));
                var x = plot.Left + (slot * index) + ((slot - barWidth) / 2);
                var rectangle = new RectangleF(x, plot.Bottom - height, barWidth, height);
                graphics.FillRectangle(barBrush, rectangle);
            }

            var first = _data[0].Date.ToString("MMM d", CultureInfo.GetCultureInfo("en-US"));
            var last = _data[^1].Date.ToString("MMM d", CultureInfo.GetCultureInfo("en-US"));
            using var textBrush = new SolidBrush(MutedText);
            graphics.DrawString(first, labelFont, textBrush, plot.Left, plot.Bottom + 6);
            var lastSize = graphics.MeasureString(last, labelFont);
            graphics.DrawString(last, labelFont, textBrush, plot.Right - lastSize.Width, plot.Bottom + 6);
            var axis = useCost ? $"max ${max:0.##}" : $"max {FormatCompact(max)} tokens";
            var axisSize = graphics.MeasureString(axis, labelFont);
            graphics.DrawString(axis, labelFont, textBrush, plot.Right - axisSize.Width, 14);
        }
        finally
        {
            titleFont.Dispose();
            labelFont.Dispose();
        }
    }

    private static string FormatCompact(double value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.#}B",
        >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
        >= 1_000 => $"{value / 1_000d:0.#}K",
        _ => value.ToString("0", CultureInfo.CurrentCulture)
    };
}
