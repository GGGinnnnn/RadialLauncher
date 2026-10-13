using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 从 exe / 快捷方式 / 目录里取出系统图标，供轮盘显示。
    /// 只用 shell32 + WPF 互操作，不引入额外依赖。
    /// </summary>
    public static class IconService
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfoW(
            string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// 图标缓存。每个条目持有一个已 Freeze 的 BitmapSource（占显存），
        /// 而设置面板是长驻窗口、用户可能反复切换大量不同路径 ——
        /// 因此必须设上限，否则缓存会无限增长。
        ///
        /// 实现为 LRU：<see cref="CacheKeys"/> 按"最近使用"顺序排队，
        /// 头部最旧、尾部最新；超过 <see cref="CacheCapacity"/> 时从头淘汰。
        /// 用双向链表 + 字典，访问/插入/淘汰都是 O(1)。
        /// </summary>
        private const int CacheCapacity = 256;

        private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<CacheEntry> CacheKeys = new();
        private static readonly object Gate = new();

        private readonly record struct CacheEntry(string Key, ImageSource? Icon);

        /// <summary>当前缓存条目数（诊断 / 测试用）。</summary>
        public static int CacheCount
        {
            get { lock (Gate) return Cache.Count; }
        }

        /// <summary>
        /// 查缓存。<paramref name="found"/> 表示"键存在"（值可能是 null —— 
        /// 那表示"这个路径确实没有图标"，也要当成命中，否则每次都会重新问系统）。
        /// </summary>
        private static ImageSource? TryGetCached(string key, out bool found)
        {
            if (!Cache.TryGetValue(key, out var node))
            {
                found = false;
                return null;
            }

            // 命中即提到队尾，标记为最近使用
            CacheKeys.Remove(node);
            CacheKeys.AddLast(node);

            found = true;
            return node.Value.Icon;
        }

        private static void Store(string key, ImageSource? icon)
        {
            if (Cache.TryGetValue(key, out var existing))
            {
                // 已有条目：更新内容并提到队尾
                CacheKeys.Remove(existing);
                Cache.Remove(key);
            }

            var node = CacheKeys.AddLast(new CacheEntry(key, icon));
            Cache[key] = node;

            // 超出上限就淘汰最久未使用的（队头）
            while (Cache.Count > CacheCapacity && CacheKeys.First != null)
            {
                var oldest = CacheKeys.First;
                CacheKeys.RemoveFirst();
                Cache.Remove(oldest.Value.Key);
            }
        }

        /// <summary>清空图标缓存（设置面板关闭或用户主动刷新时调用）。</summary>
        public static void ClearCache()
        {
            lock (Gate)
            {
                Cache.Clear();
                CacheKeys.Clear();
            }
        }

        /// <summary>取文件/文件夹的系统图标。失败返回 null。</summary>
        public static ImageSource? GetIcon(string? path, bool small = false)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            var key = (small ? "s|" : "l|") + path;

            lock (Gate)
            {
                var hit = TryGetCached(key, out bool found);
                if (found) return hit;
            }

            ImageSource? result = null;

            try
            {
                bool exists = System.IO.File.Exists(path) || System.IO.Directory.Exists(path);

                uint flags = SHGFI_ICON | (small ? SHGFI_SMALLICON : SHGFI_LARGEICON);
                uint attributes = FILE_ATTRIBUTE_NORMAL;

                if (!exists)
                {
                    // 路径还不存在时按扩展名问系统要一个通用图标
                    flags |= SHGFI_USEFILEATTRIBUTES;
                    var ext = System.IO.Path.GetExtension(path);
                    if (string.IsNullOrEmpty(ext)) attributes = FILE_ATTRIBUTE_DIRECTORY;
                }
                else if (System.IO.Directory.Exists(path))
                {
                    attributes = FILE_ATTRIBUTE_DIRECTORY;
                }

                var info = new SHFILEINFO();
                var handle = SHGetFileInfoW(path, attributes, ref info,
                    (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

                if (handle != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        result = Imaging.CreateBitmapSourceFromHIcon(
                            info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        result.Freeze();
                    }
                    finally
                    {
                        DestroyIcon(info.hIcon);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"提取图标失败（{path}）：{ex.Message}");
            }

            lock (Gate)
            {
                Store(key, result);
            }

            return result;
        }

        /// <summary>尝试从“启动应用”的目标里推断出可执行文件路径。</summary>
        public static string GuessExecutable(string? actionValue)
        {
            if (string.IsNullOrWhiteSpace(actionValue)) return "";

            var value = Environment.ExpandEnvironmentVariables(actionValue.Trim().Trim('"'));

            if (System.IO.File.Exists(value)) return value;

            // 处理 "C:\app\a.exe" -arg 这种带参数的写法
            int space = value.IndexOf(' ');
            if (space > 0)
            {
                var head = value[..space].Trim('"');
                if (System.IO.File.Exists(head)) return head;
            }

            return "";
        }
    }
}
