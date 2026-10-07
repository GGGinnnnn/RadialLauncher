namespace RadialLauncher.Services
{
    /// <summary>
    /// 快捷键目录中的一条记录。Keys 是形如 <c>Ctrl+Shift+Esc</c> 的可合成序列。
    /// </summary>
    public sealed record ShortcutDefinition(
        string Id,
        string CategoryZh,
        string CategoryEn,
        string NameZh,
        string NameEn,
        string Keys);
}
