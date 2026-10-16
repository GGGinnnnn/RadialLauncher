using RadialLauncher.Services;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// AutoStartService 的纯逻辑部分 —— 从 Run 项命令行里解析可执行文件路径。
///
/// 之所以把它抽成纯函数来测：真机上的自启失效几乎都出在这里。
/// Windows 的 Run 值可能是两种形态，而路径本身**允许含空格**（甚至必然含，
/// 例如 C:\Program Files\...），所以任何“按空格切”的写法都是错的。
/// </summary>
public class AutoStartServiceTests
{
    [Theory]
    [InlineData("\"C:\\app\\RadialLauncher.exe\"", "C:\\app\\RadialLauncher.exe")]
    [InlineData("\"C:\\a b\\RadialLauncher.exe\"", "C:\\a b\\RadialLauncher.exe")]
    [InlineData("\"C:\\Program Files\\RL\\RadialLauncher.exe\"", "C:\\Program Files\\RL\\RadialLauncher.exe")]
    public void ParseExecutable_QuotedPath(string command, string expected)
        => Assert.Equal(expected, AutoStartService.ParseExecutable(command));

    [Fact]
    public void ParseExecutable_QuotedPathWithArguments()
        => Assert.Equal("C:\\app\\RadialLauncher.exe",
            AutoStartService.ParseExecutable("\"C:\\app\\RadialLauncher.exe\" --minimized"));

    [Fact]
    public void ParseExecutable_UnquotedPathWithSpaces()
        => Assert.Equal("C:\\Program Files\\RadialLauncher\\RadialLauncher.exe",
            AutoStartService.ParseExecutable("C:\\Program Files\\RadialLauncher\\RadialLauncher.exe"));

    [Fact]
    public void ParseExecutable_UnquotedPathWithArguments()
        => Assert.Equal("C:\\app\\RadialLauncher.exe",
            AutoStartService.ParseExecutable("C:\\app\\RadialLauncher.exe --minimized"));

    /// <summary>大小写不敏感：注册表里可能写成 .EXE。</summary>
    [Fact]
    public void ParseExecutable_ExtensionIsCaseInsensitive()
        => Assert.Equal("C:\\app\\RL.EXE",
            AutoStartService.ParseExecutable("C:\\app\\RL.EXE -foo"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseExecutable_NullOrBlank_ReturnsNull(string? command)
        => Assert.Null(AutoStartService.ParseExecutable(command));

    /// <summary>只有开引号没有闭引号（注册表被写坏）→ 解析不出来，不能抛。</summary>
    [Fact]
    public void ParseExecutable_UnterminatedQuote_ReturnsNull()
        => Assert.Null(AutoStartService.ParseExecutable("\"C:\\app\\x.exe"));

    /// <summary>
    /// 这条是本次修复的核心：注册表里可能残留一条**指向别的路径**的登记项。
    /// 解析必须如实返回它，交由上层判断“是否指向当前 exe / 文件是否还在”。
    /// </summary>
    [Fact]
    public void ParseExecutable_ReturnsStalePathAsIs()
        => Assert.Equal("D:\\old\\RadialLauncher-v0.5.exe",
            AutoStartService.ParseExecutable("\"D:\\old\\RadialLauncher-v0.5.exe\""));
}
