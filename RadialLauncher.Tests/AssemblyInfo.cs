using Xunit;

// 被测代码里有进程内单例（Loc.Instance）与静态缓存（FontCatalog、TrayIconFactory），
// 并行跑测试会互相污染语言状态。这里统一关掉容器级并行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
