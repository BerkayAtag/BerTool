using System.Runtime.InteropServices;

namespace BerTool.TrayAgent.Utilities;

internal static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateHealthyIcon() => CreateStatusIcon(Color.FromArgb(30, 162, 75), true);
    public static Icon CreateRiskIcon() => CreateStatusIcon(Color.FromArgb(208, 46, 46), false);

    private static Icon CreateStatusIcon(Color baseColor, bool healthy)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var fillBrush = new SolidBrush(baseColor);
            using var borderPen = new Pen(Color.FromArgb(35, 35, 35), 1.2f);
            graphics.FillEllipse(fillBrush, 0.8f, 0.8f, 14.2f, 14.2f);
            graphics.DrawEllipse(borderPen, 0.8f, 0.8f, 14.2f, 14.2f);

            if (healthy)
            {
                using var glyphPen = new Pen(Color.White, 1.9f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round
                };
                graphics.DrawLines(glyphPen, new[]
                {
                    new PointF(4.0f, 8.4f),
                    new PointF(6.8f, 11.0f),
                    new PointF(11.6f, 5.3f)
                });
            }
            else
            {
                using var glyphPen = new Pen(Color.White, 1.9f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round
                };
                graphics.DrawLine(glyphPen, 4.4f, 4.4f, 11.6f, 11.6f);
                graphics.DrawLine(glyphPen, 11.6f, 4.4f, 4.4f, 11.6f);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporaryIcon = Icon.FromHandle(handle);
            return (Icon)temporaryIcon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
