namespace CodexUsageTray;

internal sealed class ResetCreditTimingDialog : Form
{
    private static readonly Color Background = Color.FromArgb(18, 18, 18);
    private static readonly Color Surface = Color.FromArgb(31, 31, 31);
    private static readonly Color TextPrimary = Color.FromArgb(246, 246, 246);
    private static readonly Color TextMuted = Color.FromArgb(175, 175, 175);
    private static readonly Color Accent = Color.FromArgb(0, 95, 184);
    private static readonly Color Error = Color.FromArgb(248, 113, 113);

    private readonly NumericUpDown _hours;
    private readonly NumericUpDown _minutes;
    private readonly Label _validationMessage;

    public ResetCreditTimingDialog(int leadTimeMinutes)
    {
        var normalized = ResetCreditAutomation.NormalizeLeadTimeMinutes(leadTimeMinutes);

        Name = nameof(ResetCreditTimingDialog);
        AccessibleName = "Reset-credit timing";
        Text = "Reset-credit timing";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Background;
        ForeColor = TextPrimary;
        ClientSize = new Size(440, 270);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Background,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(24, 20, 24, 18)
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var title = CreateLabel("Run before the credit expires", 14, FontStyle.Bold, TextPrimary);
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;

        var description = CreateLabel(
            "Choose when notifications or automatic use should begin. Allowed range: 5 minutes to 7 days.",
            9,
            FontStyle.Regular,
            TextMuted);
        description.Dock = DockStyle.Fill;
        description.TextAlign = ContentAlignment.TopLeft;

        var inputRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(12, 10, 12, 10),
            Margin = Padding.Empty
        };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _hours = CreateNumberInput("Hours before expiry", 0, 168, normalized / 60);
        _minutes = CreateNumberInput("Minutes before expiry", 0, 59, normalized % 60);
        inputRow.Controls.Add(_hours, 0, 0);
        inputRow.Controls.Add(CreateUnitLabel("hours"), 1, 0);
        inputRow.Controls.Add(_minutes, 2, 0);
        inputRow.Controls.Add(CreateUnitLabel("minutes"), 3, 0);

        _validationMessage = CreateLabel(string.Empty, 8.5f, FontStyle.Regular, Error);
        _validationMessage.Dock = DockStyle.Fill;
        _validationMessage.TextAlign = ContentAlignment.MiddleLeft;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Background,
            Padding = Padding.Empty
        };
        var save = CreateButton("Save", Accent, Color.White);
        save.Click += (_, _) => SaveAndClose();
        var cancel = CreateButton("Cancel", Surface, TextPrimary);
        cancel.DialogResult = DialogResult.Cancel;
        var restoreDefault = CreateButton("Default 6 hours", Surface, TextPrimary, width: 120);
        restoreDefault.Click += (_, _) => SetDefault();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(restoreDefault);

        content.Controls.Add(title, 0, 0);
        content.Controls.Add(description, 0, 1);
        content.Controls.Add(inputRow, 0, 2);
        content.Controls.Add(_validationMessage, 0, 3);
        content.Controls.Add(buttons, 0, 4);
        Controls.Add(content);

        AcceptButton = save;
        CancelButton = cancel;
        _hours.ValueChanged += (_, _) => KeepMaximumInRange();
        Shown += (_, _) => _hours.Select();
        KeepMaximumInRange();
    }

    public int LeadTimeMinutes => checked((int)_hours.Value * 60 + (int)_minutes.Value);

    internal static string FormatLeadTime(int leadTimeMinutes)
    {
        var normalized = ResetCreditAutomation.NormalizeLeadTimeMinutes(leadTimeMinutes);
        var days = normalized / (24 * 60);
        var hours = normalized % (24 * 60) / 60;
        var minutes = normalized % 60;
        var parts = new List<string>(3);
        if (days > 0) parts.Add($"{days} {(days == 1 ? "day" : "days")}");
        if (hours > 0) parts.Add($"{hours} {(hours == 1 ? "hour" : "hours")}");
        if (minutes > 0) parts.Add($"{minutes} {(minutes == 1 ? "minute" : "minutes")}");
        return string.Join(" ", parts);
    }

    private void SaveAndClose()
    {
        var total = LeadTimeMinutes;
        if (total < ResetCreditAutomation.MinimumLeadTimeMinutes ||
            total > ResetCreditAutomation.MaximumLeadTimeMinutes)
        {
            _validationMessage.Text = "Enter a time from 5 minutes through 7 days.";
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void KeepMaximumInRange()
    {
        if (_hours.Value < 168)
        {
            _minutes.Enabled = true;
            return;
        }

        _minutes.Value = 0;
        _minutes.Enabled = false;
    }

    private void SetDefault()
    {
        _hours.Value = ResetCreditAutomation.DefaultLeadTimeMinutes / 60;
        _minutes.Value = ResetCreditAutomation.DefaultLeadTimeMinutes % 60;
        _validationMessage.Text = string.Empty;
        _hours.Select();
    }

    private static NumericUpDown CreateNumberInput(
        string accessibleName,
        decimal minimum,
        decimal maximum,
        decimal value)
        => new()
        {
            Dock = DockStyle.Fill,
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = HorizontalAlignment.Right,
            AccessibleName = accessibleName,
            TabStop = true
        };

    private static Label CreateUnitLabel(string text)
    {
        var label = CreateLabel(text, 9, FontStyle.Regular, TextMuted);
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Padding = new Padding(8, 0, 0, 0);
        return label;
    }

    private static Label CreateLabel(string text, float size, FontStyle style, Color color)
    {
        var font = new Font("Segoe UI", size, style, GraphicsUnit.Point);
        var label = new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
            AutoEllipsis = true
        };
        label.Disposed += (_, _) => font.Dispose();
        return label;
    }

    private static Button CreateButton(string text, Color background, Color foreground, int width = 88)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 34,
            BackColor = background,
            ForeColor = foreground,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Margin = new Padding(8, 4, 0, 4)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 70);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 55, 55);
        return button;
    }
}
