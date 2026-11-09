using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StatusDockPreview
{
    internal sealed class FlipperPreview
    {
        private const int Width = 128;
        private const int Height = 64;
        private const byte Light = 0;
        private const byte Dark = 1;
        private static readonly BitmapPalette Palette = new BitmapPalette(new[]
        {
            Color.FromRgb(255, 174, 76),
            Color.FromRgb(43, 31, 20)
        });

        // Five-bit rows for the 5 × 7 pixel font. Space and colon use three columns.
        private static readonly Dictionary<char, byte[]> Glyphs = new Dictionary<char, byte[]>
        {
            { '0', new byte[] { 14, 17, 17, 17, 17, 17, 14 } },
            { '1', new byte[] {  4, 12,  4,  4,  4,  4, 14 } },
            { '2', new byte[] { 14, 17,  1,  6,  8, 16, 31 } },
            { '3', new byte[] { 30,  1,  1, 14,  1,  1, 30 } },
            { '4', new byte[] {  2,  6, 10, 18, 31,  2,  2 } },
            { '5', new byte[] { 31, 16, 16, 30,  1,  1, 30 } },
            { '6', new byte[] { 14, 16, 16, 30, 17, 17, 14 } },
            { '7', new byte[] { 31,  1,  2,  4,  8,  8,  8 } },
            { '8', new byte[] { 14, 17, 17, 14, 17, 17, 14 } },
            { '9', new byte[] { 14, 17, 17, 15,  1,  1, 14 } },
            { ':', new byte[] {  0,  0,  2,  2,  0,  2,  2 } },
            { '-', new byte[] {  0,  0,  0, 31,  0,  0,  0 } },
            { '%', new byte[] { 25, 26,  4,  4,  4, 11, 19 } },
            { ' ', new byte[] {  0,  0,  0,  0,  0,  0,  0 } },
            { 'A', new byte[] { 14, 17, 17, 31, 17, 17, 17 } },
            { 'C', new byte[] { 14, 17, 16, 16, 16, 17, 14 } },
            { 'G', new byte[] { 14, 17, 16, 23, 17, 17, 15 } },
            { 'M', new byte[] { 17, 27, 21, 21, 17, 17, 17 } },
            { 'P', new byte[] { 30, 17, 17, 30, 16, 16, 16 } },
            { 'R', new byte[] { 30, 17, 17, 30, 20, 18, 17 } },
            { 'U', new byte[] { 17, 17, 17, 17, 17, 17, 14 } }
        };

        private readonly byte[] pixels = new byte[Width * Height];

        public static BitmapSource Render(
            LiveSample sample, int clockSeconds, bool clockOnly, bool showSeconds)
        {
            var preview = new FlipperPreview();
            var time = TimeSpan.FromSeconds(clockSeconds);
            string clock = time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            string seconds = showSeconds ? time.ToString(@"\:ss", CultureInfo.InvariantCulture) : "";
            if (clockOnly && showSeconds)
                preview.DrawClockGroup(clock, seconds, 3, 4, 2, 3, 6, 46, Dark);
            else if (clockOnly)
                preview.DrawClockGroup(clock, seconds, 4, 4, 1, 1, 4, 46, Dark);
            else
                preview.DrawStats(sample, clock, seconds);

            var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Indexed8,
                Palette, preview.pixels, Width);
            bitmap.Freeze();
            return bitmap;
        }

        private void DrawStats(LiveSample sample, string clock, string seconds)
        {
            // Header, meter rows and alignment follow desk_draw and desk_meter in statusdock.c.
            Fill(0, 0, Width, 23, Dark);
            DrawClockGroup(clock, seconds, 3, 3, 2, 2, 6, 22, Light);

            DrawMeter("CPU", 35, sample.Cpu, sample.CpuTemp);
            DrawMeter("RAM", 48, sample.Ram, LiveSample.UnknownMetric);
            DrawMeter("GPU", 61, sample.Gpu, sample.GpuTemp);
        }

        private void DrawClockGroup(
            string clock, string seconds, int clockScaleX, int clockScaleY, int secondsScaleX,
            int secondsScaleY, int gap, int bottom, byte color)
        {
            int clockWidth = TextWidth(clock, clockScaleX);
            int totalWidth = clockWidth + (seconds.Length > 0 ? gap + TextWidth(seconds, secondsScaleX) : 0);
            int left = (Width - totalWidth) / 2;
            DrawText(clock, left, bottom - 7 * clockScaleY, clockScaleX, color, clockScaleY);
            DrawText(seconds, left + clockWidth + gap, bottom - 7 * secondsScaleY, secondsScaleX,
                color, secondsScaleY);
        }

        private void DrawMeter(string label, int baseline, int percent, int temperature)
        {
            DrawText(label, 2, baseline - 7, 1, Dark);
            Fill(26, baseline - 7, 43, 7, Dark);
            Fill(27, baseline - 6, 41, 5, Light);
            string value = "--";
            if (percent <= 100)
            {
                Fill(27, baseline - 6, (41 * percent + 50) / 100, 5, Dark);
                value = percent.ToString(CultureInfo.InvariantCulture) + "%";
            }
            DrawRight(value, 95, baseline - 7, Dark);
            if (temperature != LiveSample.UnknownMetric)
                DrawRight(temperature.ToString(CultureInfo.InvariantCulture) + " C", 126, baseline - 7, Dark);
        }

        private void DrawRight(string text, int right, int y, byte color)
        {
            DrawText(text, right - TextWidth(text, 1), y, 1, color);
        }

        private static int TextWidth(string text, int scale)
        {
            int columns = 0;
            foreach (char character in text)
                columns += GlyphWidth(character) + 1;
            return text.Length == 0 ? 0 : (columns - 1) * scale;
        }

        private static int GlyphWidth(char character)
        {
            return character == ':' || character == ' ' ? 3 : 5;
        }

        private void DrawText(string text, int x, int y, int scaleX, byte color, int? heightScale = null)
        {
            int scaleY = heightScale ?? scaleX;
            foreach (char character in text)
            {
                int width = GlyphWidth(character);
                byte[] glyph = Glyphs[character];
                for (int row = 0; row < glyph.Length; row++)
                {
                    for (int column = 0; column < width; column++)
                    {
                        if ((glyph[row] & (1 << (width - 1 - column))) != 0)
                            Fill(x + column * scaleX, y + row * scaleY, scaleX, scaleY, color);
                    }
                }
                x += (width + 1) * scaleX;
            }
        }

        private void Fill(int x, int y, int width, int height, byte color)
        {
            for (int row = y; row < y + height; row++)
            {
                for (int column = x; column < x + width; column++)
                    pixels[row * Width + column] = color;
            }
        }
    }
}
