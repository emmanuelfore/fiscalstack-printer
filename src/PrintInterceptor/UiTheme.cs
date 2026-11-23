using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PrintInterceptor.Core.State;

namespace PrintInterceptor;

/// <summary>
/// One visual language for the operator console. WinForms has no stylesheet, so these helpers
/// keep the shell, jobs worklist and reports on the same spacing, type and status colours.
/// Nothing here changes parse or fiscalisation behaviour.
/// </summary>
internal static class UiTheme
{
    public static readonly Color Navy = Color.FromArgb(15, 23, 42);
    public static readonly Color NavySoft = Color.FromArgb(30, 41, 59);
    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color AccentDark = Color.FromArgb(29, 78, 216);
    public static readonly Color AccentLight = Color.FromArgb(219, 234, 254);
    public static readonly Color Canvas = Color.FromArgb(247, 249, 252);
    public static readonly Color Surface = Color.White;
    public static readonly Color Border = Color.FromArgb(226, 232, 240);
    public static readonly Color Text = Color.FromArgb(15, 23, 42);
    public static readonly Color Muted = Color.FromArgb(100, 116, 139);
    public static readonly Color Success = Color.FromArgb(5, 150, 105);
    public static readonly Color SuccessBg = Color.FromArgb(209, 250, 229);
    public static readonly Color Warning = Color.FromArgb(180, 83, 9);
    public static readonly Color WarningBg = Color.FromArgb(254, 243, 199);
    public static readonly Color Danger = Color.FromArgb(190, 18, 60);
    public static readonly Color DangerBg = Color.FromArgb(255, 228, 230);
    public static readonly Color Info = Color.FromArgb(29, 78, 216);
    public static readonly Color InfoBg = Color.FromArgb(219, 234, 254);
    public static readonly Color Neutral = Color.FromArgb(71, 85, 105);
    public static readonly Color NeutralBg = Color.FromArgb(241, 245, 249);

    public static Font Ui(float size, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style);

    public static Font Mono(float size = 9f) => new("Consolas", size);

    public static Label Heading(string text) => new()
    {
        Text = text,
        Font = Ui(16f, FontStyle.Bold),
        ForeColor = Text,
        AutoSize = true
    };

    public static Label Subheading(string text) => new()
    {
        Text = text,
        Font = Ui(9.5f),
        ForeColor = Muted,
        AutoSize = true
    };

    public static Label SectionLabel(string text) => new()
    {
        Text = text,
        Font = Ui(9f, FontStyle.Bold),
        ForeColor = Muted,
        AutoSize = true
    };

    public static void Primary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.Font = Ui(9.5f, FontStyle.Bold);
        button.Padding = new Padding(10, 5, 10, 5);
        button.Cursor = Cursors.Hand;
        button.Height = Math.Max(button.Height, 34);
    }

    public static void Secondary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = Surface;
        button.ForeColor = Text;
        button.Font = Ui(9.5f);
        button.Padding = new Padding(10, 5, 10, 5);
        button.Cursor = Cursors.Hand;
        button.Height = Math.Max(button.Height, 34);
    }

    public static void Ghost(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Color.Transparent;
        button.ForeColor = Muted;
        button.Font = Ui(9.5f);
        button.Padding = new Padding(8, 5, 8, 5);
        button.Cursor = Cursors.Hand;
    }

    public static void DangerButton(Button button)
    {
        Primary(button);
        button.BackColor = Danger;
    }

    /// <summary>
    /// One nav-button shape for the console rail and the parse-rule section rail. Dark mode
    /// keeps the current console look; light mode gives the editor a softer, card-like rail.
    /// </summary>
    public static void NavButton(Button button, bool selected, bool dark = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = dark ? 0 : 1;
        button.FlatAppearance.BorderColor = selected ? Accent : Border;
        button.BackColor = dark
            ? (selected ? Accent : NavySoft)
            : (selected ? AccentLight : Surface);
        button.ForeColor = dark
            ? (selected ? Color.White : Color.FromArgb(226, 232, 240))
            : (selected ? AccentDark : Text);
        button.Font = Ui(9.5f, selected ? FontStyle.Bold : FontStyle.Regular);
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.Padding = new Padding(14, 0, 10, 0);
        button.Height = 40;
        button.Cursor = Cursors.Hand;
    }

    /// <summary>Form controls get the same type, spacing and border as the rest of the console.</summary>
    public static void Input(Control control)
    {
        control.Font = Ui(9f);
        control.BackColor = Surface;
        control.ForeColor = Text;
        control.Padding = new Padding(6, 4, 6, 4);
        if (control is TextBoxBase text)
        {
            text.BorderStyle = BorderStyle.FixedSingle;
        }
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.GridColor = Border;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.ReadOnly = true;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
        grid.RowTemplate.Height = 36;
        grid.EnableHeadersVisualStyles = false;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.ColumnHeadersHeight = 34;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        grid.ColumnHeadersDefaultCellStyle.Font = Ui(8.5f, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(248, 250, 252);
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Muted;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = AccentLight;
        grid.DefaultCellStyle.SelectionForeColor = Text;
        grid.DefaultCellStyle.Font = Ui(9f);
        grid.DefaultCellStyle.Padding = new Padding(8, 3, 8, 3);
        grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(252, 253, 255);
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = AccentLight;
    }

    public static void ButtonColumn(DataGridViewButtonColumn column, Color fore, Color back)
    {
        column.FlatStyle = FlatStyle.Flat;
        column.UseColumnTextForButtonValue = true;
        column.DefaultCellStyle.BackColor = Surface;
        column.DefaultCellStyle.ForeColor = fore;
        column.DefaultCellStyle.SelectionBackColor = AccentLight;
        column.DefaultCellStyle.SelectionForeColor = fore;
        column.DefaultCellStyle.Font = Ui(8.5f, FontStyle.Bold);
        column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        column.CellTemplate.Style.BackColor = back;
    }

    public static (string Text, Color Fore, Color Back) Status(LedgerStatus status) => status switch
    {
        LedgerStatus.Fiscalised => ("Fiscalised", Success, SuccessBg),
        LedgerStatus.FiscalisedOffline => ("Signed offline", Warning, WarningBg),
        LedgerStatus.InFlight => ("Processing", Info, InfoBg),
        LedgerStatus.NeedsReview => ("Needs review", Warning, WarningBg),
        LedgerStatus.Rejected => ("Rejected", Danger, DangerBg),
        LedgerStatus.AmbiguousFailure => ("Verify at ZIMRA", Danger, DangerBg),
        _ => (status.ToString(), Neutral, NeutralBg)
    };

    public static void PaintPill(Graphics graphics, Rectangle bounds, string text, Color fore, Color back)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var box = new Rectangle(bounds.X + 3, bounds.Y + (bounds.Height - 22) / 2,
            Math.Max(24, Math.Min(bounds.Width - 6, TextRenderer.MeasureText(text, Ui(8.5f, FontStyle.Bold)).Width + 18)), 22);
        using var path = RoundedRectangle(box, 11);
        using var brush = new SolidBrush(back);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.FillPath(brush, path);
        TextRenderer.DrawText(graphics, text, Ui(8.5f, FontStyle.Bold), box, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var r = Math.Max(1, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, r * 2, r * 2, 180, 90);
        path.AddArc(bounds.Right - r * 2, bounds.Y, r * 2, r * 2, 270, 90);
        path.AddArc(bounds.Right - r * 2, bounds.Bottom - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>A card with soft corners and a one-pixel border; used for KPIs and report summaries.</summary>
internal sealed class CardPanel : Panel
{
    public Color CardBackColor { get; set; } = UiTheme.Surface;
    public Color CardBorderColor { get; set; } = UiTheme.Border;
    public int CornerRadius { get; set; } = 12;

    public CardPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
        Padding = new Padding(16, 14, 16, 14);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var outer = new Rectangle(ClientRectangle.X, ClientRectangle.Y,
            ClientRectangle.Width - 1, ClientRectangle.Height - 1);
        if (outer.Width <= 4 || outer.Height <= 4)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        // WinForms has no stylesheet shadow; three translucent, offset copies give a soft
        // bottom-right lift while staying inside the control's paint region.
        for (var offset = 3; offset >= 1; offset--)
        {
            var shadowBounds = new Rectangle(outer.X + offset, outer.Y + offset,
                outer.Width - offset, outer.Height - offset);
            using var shadowPath = UiTheme.RoundedRectangle(shadowBounds, CornerRadius);
            using var shadow = new SolidBrush(Color.FromArgb(9, 15, 23, 42));
            e.Graphics.FillPath(shadow, shadowPath);
        }

        var bounds = new Rectangle(outer.X, outer.Y, outer.Width - 3, outer.Height - 3);
        using var fill = new SolidBrush(CardBackColor);
        using var border = new Pen(CardBorderColor);
        using var path = UiTheme.RoundedRectangle(bounds, CornerRadius);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
        base.OnPaint(e);
    }
}
