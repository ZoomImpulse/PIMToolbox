using System.IO;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Svg.Skia;
using SkiaSharp;

namespace PIMToolbox.Desktop;

internal static class TrayIconFactory
{
    public static WindowIcon Create()
    {
        const int size = 32;
        using var svgStream = AssetLoader.Open(new Uri("avares://PIMToolbox.Desktop/Assets/PIMToolbox.svg"));
        using var svg = SvgSource.LoadFromStream(svgStream)
            ?? throw new InvalidDataException("The PIM Toolbox SVG logo could not be loaded.");
        var picture = svg.Picture
            ?? throw new InvalidDataException("The PIM Toolbox SVG logo did not contain drawable artwork.");
        var bounds = picture.CullRect;

        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(size / bounds.Width, size / bounds.Height);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            const int headerSize = 40;
            const int pixelDataSize = size * size * 4;
            const int maskDataSize = size * 4;
            const int imageSize = headerSize + pixelDataSize + maskDataSize;

            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write((byte)size);
            writer.Write((byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(imageSize);
            writer.Write(22);

            writer.Write(headerSize);
            writer.Write(size);
            writer.Write(size * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(pixelDataSize);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);

            for (var y = size - 1; y >= 0; y--)
            for (var x = 0; x < size; x++)
            {
                var color = bitmap.GetPixel(x, y);
                writer.Write(color.Blue);
                writer.Write(color.Green);
                writer.Write(color.Red);
                writer.Write(color.Alpha);
            }

            writer.Write(new byte[maskDataSize]);
        }

        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
