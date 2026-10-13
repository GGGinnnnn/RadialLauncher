using System.Windows;
using System.Windows.Controls;
using RadialLauncher.Controls;
using Xunit;

namespace RadialLauncher.Tests;

/// <summary>
/// RadialMenu.UpdateSelection 的角度 → 索引映射。
/// WPF 的 Panel/FrameworkElement 构造要求 STA 线程，而 xUnit 默认跑在 MTA，
/// 因此每个用例都通过 <see cref="StaRunner.Run"/> 起一个临时 STA 线程执行。
/// </summary>
public class RadialMenuTests
{
    /// <summary>造一个带 count 个占位控件的菜单（必须在 STA 线程上调用）。</summary>
    private static RadialMenu MakeMenu(int count, double deadZone = 34.0)
    {
        var menu = new RadialMenu
        {
            ItemCount = count,
            DeadZone = deadZone,
            StartAngle = -90.0,
        };

        for (int i = 0; i < count; i++)
            menu.Children.Add(new Border());

        return menu;
    }

    // ==================== 死区 ====================

    [Fact]
    public void UpdateSelection_InsideDeadZoneSelectsNothing() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(8);
        menu.UpdateSelection(new Point(10, 10));   // 距离 ≈14 < 34
        Assert.Equal(-1, menu.SelectedIndex);
    });

    [Fact]
    public void UpdateSelection_ExactlyAtOriginSelectsNothing() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(8);
        menu.UpdateSelection(new Point(0, 0));
        Assert.Equal(-1, menu.SelectedIndex);
    });

    [Fact]
    public void UpdateSelection_JustOutsideDeadZoneSelectsSomething() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(8, deadZone: 10);
        menu.UpdateSelection(new Point(0, -50));   // 正上方，距离 50
        Assert.NotEqual(-1, menu.SelectedIndex);
    });

    [Fact]
    public void UpdateSelection_NoChildrenSelectsNothing() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(0);
        menu.UpdateSelection(new Point(100, 100));
        Assert.Equal(-1, menu.SelectedIndex);
    });

    // ==================== 八向映射（StartAngle = -90，索引 0 在正上方）====================

    [Fact]
    public void UpdateSelection_EightWay_UpIsZero() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(8);
        menu.UpdateSelection(new Point(0, -100));
        Assert.Equal(0, menu.SelectedIndex);
    });

    [Fact]
    public void UpdateSelection_EightWay_UpRightIsOne() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(8);
        menu.UpdateSelection(new Point(100, -100));
        Assert.Equal(1, menu.SelectedIndex);
    });

    [Fact]
    public void UpdateSelection_EightWay_RightIsTwo() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(8);
        menu.UpdateSelection(new Point(100, 0));
        Assert.Equal(2, menu.SelectedIndex);
    });

    [Fact]
    public void UpdateSelection_EightWay_DownRightIsThree() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(8);
        menu.UpdateSelection(new Point(100, 100));
        Assert.Equal(3, menu.SelectedIndex);
    });

    [Fact]
    public void UpdateSelection_EightWay_DownIsFour() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(8);
        menu.UpdateSelection(new Point(0, 100));
        Assert.Equal(4, menu.SelectedIndex);
    });

    [Fact]
    public void UpdateSelection_EightWay_LeftIsSix() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(8);
        menu.UpdateSelection(new Point(-100, 0));
        Assert.Equal(6, menu.SelectedIndex);
    });

    [Fact]
    public void UpdateSelection_EightWay_UpLeftIsSeven() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(8);
        menu.UpdateSelection(new Point(-100, -100));
        Assert.Equal(7, menu.SelectedIndex);
    });

    // ==================== 四向映射 ====================

    [Fact]
    public void UpdateSelection_FourWay_UpIsZero() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(4);
        menu.UpdateSelection(new Point(0, -100));
        Assert.Equal(0, menu.SelectedIndex);
    });

    [Fact]
    public void UpdateSelection_FourWay_RightIsOne() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(4);
        menu.UpdateSelection(new Point(100, 0));
        Assert.Equal(1, menu.SelectedIndex);
    });

    [Fact]
    public void UpdateSelection_FourWay_DownIsTwo() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(4);
        menu.UpdateSelection(new Point(0, 100));
        Assert.Equal(2, menu.SelectedIndex);
    });

    [Fact]
    public void UpdateSelection_FourWay_LeftIsThree() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(4);
        menu.UpdateSelection(new Point(-100, 0));
        Assert.Equal(3, menu.SelectedIndex);
    });

    // ==================== 边界 / 异常 ====================

    [Fact]
    public void UpdateSelection_NeverReturnsIndexOutOfRange() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(8);
        var samples = new[]
        {
            new Point(0, -100), new Point(1, -100), new Point(100, -1),
            new Point(100, 0), new Point(100, 1), new Point(1, 100),
            new Point(0, 100), new Point(-1, 100), new Point(-100, 1),
            new Point(-100, 0), new Point(-100, -1), new Point(-1, -100),
        };

        foreach (var point in samples)
        {
            menu.UpdateSelection(point);
            Assert.InRange(menu.SelectedIndex, -1, 7);
        }
    });

    [Fact]
    public void UpdateSelection_WhenItemCountExceedsChildren_ClampsToMinusOne() => StaRunner.Run(() =>
    {
        // ItemCount 说 8 向，但面板里只有 4 个子元素 —— 超出范围的索引应被丢弃
        var menu = new RadialMenu { ItemCount = 8, DeadZone = 0 };
        for (int i = 0; i < 4; i++) menu.Children.Add(new Border());

        // 正下方会算出索引 4（超出 4 个子元素的范围）
        menu.UpdateSelection(new Point(0, 100));
        Assert.Equal(-1, menu.SelectedIndex);
    });

    // ==================== 事件与通知 ====================

    [Fact]
    public void UpdateSelection_RaisesChangedEventOnlyWhenIndexActuallyChanges() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(8, deadZone: 0);
        int raised = 0;
        menu.SelectedIndexChanged += (_, _) => raised++;

        menu.UpdateSelection(new Point(0, -100));   // 0，首次变化 → 触发
        Assert.Equal(1, raised);

        menu.UpdateSelection(new Point(0, -100));   // 仍是 0 → 不触发
        Assert.Equal(1, raised);

        menu.UpdateSelection(new Point(100, 0));    // 2 → 触发
        Assert.Equal(2, raised);
    });

    [Fact]
    public void ClearSelection_ResetsToMinusOne() => StaRunner.Run(() =>
    {
        var menu = MakeMenu(8, deadZone: 0);
        menu.UpdateSelection(new Point(0, -100));
        Assert.Equal(0, menu.SelectedIndex);

        menu.ClearSelection();
        Assert.Equal(-1, menu.SelectedIndex);
    });

    // ==================== 默认值 ====================

    [Fact]
    public void DefaultValues_AreApplied() => StaRunner.Run(() =>
    {
        var menu = new RadialMenu();
        Assert.Equal(8, menu.ItemCount);
        Assert.Equal(140.0, menu.Radius);
        Assert.Equal(34.0, menu.DeadZone);
        Assert.Equal(-90.0, menu.StartAngle);
        Assert.Equal(-1, menu.SelectedIndex);
    });
}

/// <summary>把一段需要 STA 的逻辑放到临时 STA 线程上执行，并把异常原样抛回。</summary>
internal static class StaRunner
{
    public static void Run(Action action)
    {
        Exception? captured = null;

        var thread = new System.Threading.Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });

        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (captured != null)
            throw new Xunit.Sdk.XunitException($"STA 线程内抛出异常：{captured}");
    }
}
