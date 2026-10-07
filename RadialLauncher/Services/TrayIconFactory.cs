using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 生成托盘图标。
    ///
    /// H.NotifyIcon 的 IconSource 只认 BitmapFrame / BitmapImage，而且会去读它的
    /// UriSource 并打开那个文件（内部按 System.Drawing.Icon 处理）。
    /// 所以不能直接给 DrawingImage，也不能给内存里的位图——必须落一个真正的 .ico 文件。
    /// 这里把矢量轮盘渲染成多尺寸 ICO（经典 32 位 DIB 格式）写到配置目录，再包成 BitmapFrame。
    /// </summary>
    public static class TrayIconFactory
    {
        private static BitmapFrame? _cached;

        /// <summary>图标文件名。</summary>
        public const string IconFileName = "tray.ico";

        /// <summary>取托盘图标（结果会缓存）。失败返回 null。</summary>
        public static BitmapFrame? GetIcon()
        {
            if (_cached != null) return _cached;

            try
            {
                var path = EnsureIconFile();
                if (path == null) return null;

                _cached = BitmapFrame.Create(
                    new Uri(path, UriKind.Absolute),
                    BitmapCreateOptions.None,
                    BitmapCacheOption.OnLoad);

                _cached.Freeze();
                return _cached;
            }
            catch (Exception ex)
            {
                Logger.Error("生成托盘图标失败", ex);
                return null;
            }
        }

        /// <summary>把图标写到配置目录，返回完整路径。</summary>
        public static string? EnsureIconFile()
        {
            try
            {
                var directory = ConfigService.DataDirectory;
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

                var path = Path.Combine(directory, IconFileName);

                // 每次启动都重写一遍，几 KB 而已，省得图标和版本对不上
                File.WriteAllBytes(path, BuildIco(16, 20, 24, 32, 48));
                return path;
            }
            catch (Exception ex)
            {
                Logger.Error("写入托盘图标文件失败", ex);
                return null;
            }
        }

        // ==================== ICO 容器 ====================

        private sealed class Frame
        {
            public int Size;
            public byte[] Bgra = Array.Empty<byte>();
        }

        private static byte[] BuildIco(params int[] sizes)
        {
            var frames = new List<Frame>(sizes.Length);
            foreach (var size in sizes) frames.Add(RenderFrame(size));

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // ICONDIR
            writer.Write((ushort)0);              // reserved
            writer.Write((ushort)1);              // type = icon
            writer.Write((ushort)frames.Count);   // image count

            int andRowSize = ((sizes[0] + 31) / 32) * 4;

            // 先占位 ICONDIRENTRY，稍后回填偏移
            int directoryStart = (int)ms.Position;
            foreach (var frame in frames) writer.Write(new byte[16]);
            int directoryEnd = (int)ms.Position;

            var entries = new List<(int Offset, int Length)>();

            foreach (var frame in frames)
            {
                int offset = (int)ms.Position;
                int size = frame.Size;
                int rowBytes = size * 4;

                andRowSize = ((size + 31) / 32) * 4;

                // BITMAPINFOHEADER：高度写两倍，因为后面还要跟 AND 掩码
                writer.Write(40);                 // biSize
                writer.Write(size);               // biWidth
                writer.Write(size * 2);           // biHeight
                writer.Write((ushort)1);          // biPlanes
                writer.Write((ushort)32);         // biBitCount
                writer.Write(0);                  // biCompression = BI_RGB
                writer.Write(rowBytes * size);    // biSizeImage
                writer.Write(0);                  // biXPelsPerMeter
                writer.Write(0);                  // biYPelsPerMeter
                writer.Write(0);                  // biClrUsed
                writer.Write(0);                  // biClrImportant

                // XOR 位图：BGRA，自下而上
                for (int y = size - 1; y >= 0; y--)
                    writer.Write(frame.Bgra, y * rowBytes, rowBytes);

                // AND 掩码：32 位带 alpha 的图标全填 0 即可
                var empty = new byte[andRowSize];
                for (int y = 0; y < size; y++) writer.Write(empty);

                entries.Add((offset, (int)ms.Position - offset));
            }

            // 回填 ICONDIRENTRY
            ms.Position = directoryStart;
            for (int i = 0; i < frames.Count; i++)
            {
                int size = frames[i].Size;

                writer.Write((byte)(size >= 256 ? 0 : size));   // 宽度
                writer.Write((byte)(size >= 256 ? 0 : size));   // 高度
                writer.Write((byte)0);                          // 调色板数
                writer.Write((byte)0);                          // reserved
                writer.Write((ushort)1);                        // planes
                writer.Write((ushort)32);                       // bit count
                writer.Write(entries[i].Length);                 // 数据长度
                writer.Write(entries[i].Offset);                 // 数据偏移
            }

            ms.Position = directoryEnd;
            writer.Flush();
            return ms.ToArray();
        }

        private static Frame RenderFrame(int size)
        {
            var visual = new DrawingVisual();

            using (var dc = visual.RenderOpen())
            {
                double center = size / 2.0;
                double outer = center - Math.Max(0.5, size * 0.03);

                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2D, 0x89, 0xEF)),
                    null, new Point(center, center), outer, outer);

                // 中心圆点
                double core = outer * 0.30;
                dc.DrawEllipse(Brushes.White, null, new Point(center, center), core, core);

                // 小尺寸时只画四个正方向，避免糊成一团
                int dots = size <= 20 ? 4 : 8;
                double ring = outer * 0.70;

                for (int i = 0; i < dots; i++)
                {
                    double angle = -Math.PI / 2 + i * 2 * Math.PI / dots;
                    double radius = dots == 4 ? outer * 0.165 : (i % 2 == 0 ? outer * 0.16 : outer * 0.135);

                    dc.DrawEllipse(Brushes.White, null, new Point(
                        center + ring * Math.Cos(angle),
                        center + ring * Math.Sin(angle)), radius, radius);
                }
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            int stride = size * 4;
            var pixels = new byte[stride * size];
            bitmap.CopyPixels(pixels, stride, 0);

            return new Frame { Size = size, Bgra = pixels };
        }
    }
}
