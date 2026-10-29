using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

// Kırmızı BerTool marka simgesini üretir. Her boyut ayrı çizilir ve 32 bit
// PNG olarak tek bir ICO dosyasına yazılır; böylece Programlar ve Özellikler
// (appwiz.cpl), Başlat menüsü ve görev çubuğunda simge net görünür.
// İsteğe bağlı ikinci argüman verilirse aynı tasarım 512 px PNG olarak da yazılır.

var outputPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "BerToolBrand.ico");
var pngPath = args.Length > 1 ? args[1] : null;

var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
if (string.IsNullOrWhiteSpace(targetDirectory))
{
    throw new InvalidOperationException("Geçersiz çıktı yolu.");
}

Directory.CreateDirectory(targetDirectory);

int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

// 256 px kare PNG olarak, küçük kareler en geniş uyumluluk için 32 bit
// DIB (BMP) olarak saklanır; bazı Windows bileşenleri küçük PNG karelerini okuyamaz.
var images = sizes
    .Select(size => (Size: size, Png: size >= 256 ? RenderPng(size) : RenderDib(size)))
    .ToList();

using (var file = File.Create(outputPath))
using (var writer = new BinaryWriter(file))
{
    // ICONDIR
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)images.Count);

    var offset = 6 + (16 * images.Count);
    foreach (var image in images)
    {
        // ICONDIRENTRY; 256 px boyut 0 olarak yazılır.
        writer.Write((byte)(image.Size >= 256 ? 0 : image.Size));
        writer.Write((byte)(image.Size >= 256 ? 0 : image.Size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(image.Png.Length);
        writer.Write(offset);
        offset += image.Png.Length;
    }

    foreach (var image in images)
    {
        writer.Write(image.Png);
    }
}

Console.WriteLine($"Marka simgesi üretildi: {outputPath}");

if (!string.IsNullOrWhiteSpace(pngPath))
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath))!);
    File.WriteAllBytes(pngPath, RenderPng(512));
    Console.WriteLine($"Marka görseli üretildi: {pngPath}");
}

// Üçüncü argüman: kurulum sihirbazının sağ üst köşesindeki küçük görsel
// (Inno Setup WizardSmallImageFile, %200 ölçek için 110 px).
var wizardImagePath = args.Length > 2 ? args[2] : null;
if (!string.IsNullOrWhiteSpace(wizardImagePath))
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(wizardImagePath))!);
    File.WriteAllBytes(wizardImagePath, RenderPng(110));
    Console.WriteLine($"Kurulum görseli üretildi: {wizardImagePath}");
}

static byte[] RenderPng(int size)
{
    using var bitmap = RenderBitmap(size);
    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Png);
    return stream.ToArray();
}

static byte[] RenderDib(int size)
{
    using var bitmap = RenderBitmap(size);
    var data = bitmap.LockBits(
        new Rectangle(0, 0, size, size),
        ImageLockMode.ReadOnly,
        PixelFormat.Format32bppArgb);

    try
    {
        var pixels = new byte[size * size * 4];
        for (var row = 0; row < size; row++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                data.Scan0 + (row * data.Stride),
                pixels,
                row * size * 4,
                size * 4);
        }

        var maskStride = ((size + 31) / 32) * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // BITMAPINFOHEADER; ICO içinde yükseklik renk + maske için iki katıdır.
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(size * size * 4 + maskStride * size);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        // Renk verisi alttan üste (BGRA).
        for (var row = size - 1; row >= 0; row--)
        {
            writer.Write(pixels, row * size * 4, size * 4);
        }

        // AND maskesi: saydamlık alfa kanalından gelir, maske boş bırakılır.
        writer.Write(new byte[maskStride * size]);
        writer.Flush();
        return stream.ToArray();
    }
    finally
    {
        bitmap.UnlockBits(data);
    }
}

static Bitmap RenderBitmap(int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        // Küçük boyutlarda kenar boşluğu daraltılır ki simge silik görünmesin.
        var inset = size <= 24 ? 0.5f : size * 0.04f;
        var bounds = new RectangleF(inset, inset, size - (2 * inset), size - (2 * inset));
        var radius = bounds.Width * 0.24f;

        using var shape = RoundedRectangle(bounds, radius);
        using var fill = new LinearGradientBrush(
            new PointF(bounds.Left, bounds.Top),
            new PointF(bounds.Right, bounds.Bottom),
            Color.FromArgb(238, 28, 37),
            Color.FromArgb(176, 0, 11));
        graphics.FillPath(fill, shape);

        if (size >= 32)
        {
            // Üst kenarda hafif parlama: büyük boyutlarda derinlik verir.
            // Fırça yalnızca kendi alanını boyar; aksi halde gradyan alt
            // yarıda tekrarlanıp sert bir bant oluşturur.
            var highlightArea = new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height * 0.55f);
            using var highlight = new LinearGradientBrush(
                new PointF(0, highlightArea.Top - 1),
                new PointF(0, highlightArea.Bottom + 1),
                Color.FromArgb(56, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255));
            var previousClip = graphics.Clip;
            graphics.SetClip(shape);
            graphics.FillRectangle(highlight, highlightArea);
            graphics.Clip = previousClip;
        }

        var fontSize = size * (size <= 24 ? 0.80f : 0.68f);
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        // Harfin görsel merkezini yakalamak için yolu ölçüp ortala.
        using var glyph = new GraphicsPath();
        glyph.AddString("B", font.FontFamily, (int)FontStyle.Bold, fontSize, PointF.Empty, format);
        var glyphBounds = glyph.GetBounds();
        using var matrix = new Matrix();
        matrix.Translate(
            (size / 2f) - (glyphBounds.Left + (glyphBounds.Width / 2f)),
            (size / 2f) - (glyphBounds.Top + (glyphBounds.Height / 2f)));
        glyph.Transform(matrix);
        graphics.FillPath(textBrush, glyph);
    }

    return bitmap;
}

static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
{
    var diameter = radius * 2f;
    var path = new GraphicsPath();
    path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
    path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
    path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
    path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
}
