using System;

namespace RadialLauncher.Services
{
    /// <summary>
    /// 主窗口向设置面板暴露的能力，避免两个窗口互相直接依赖具体类型。
    /// </summary>
    public interface IAppHost
    {
        ConfigService ConfigService { get; }

        WindowDetectorService Detector { get; }

        /// <summary>钩子是否正在运行。</summary>
        bool IsRunning { get; }

        /// <summary>配置发生变化后立刻生效。</summary>
        void ApplyConfig();

        /// <summary>用指定配置直接显示一次轮盘（用于预览/测试，不写回磁盘）。</summary>
        void TestWheel(Models.AppConfig config);

        /// <summary>暂停全局钩子（例如在设置里录制按键时）。</summary>
        void PauseHook();

        /// <summary>恢复全局钩子。</summary>
        void ResumeHook();

        /// <summary>打开配置目录。</summary>
        void OpenDataFolder();
    }
}
