using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Suzu.Tests
{
    /// <summary>
    /// 测试环境准备：在任何测试（以及被测代码的任何静态初始化）之前，
    /// 把 SUZU_DATA_DIR 指到独立临时目录 —— 引擎与日志会写文件
    /// （log.txt / config.json），绝不能碰真实的 %APPDATA%\Suzu。
    /// </summary>
    public static class TestSetup
    {
        public static readonly string DataDir =
            Path.Combine(Path.GetTempPath(), "SuzuTests", Guid.NewGuid().ToString("N"));

        [ModuleInitializer]
        internal static void Run()
        {
            Directory.CreateDirectory(DataDir);
            Environment.SetEnvironmentVariable("SUZU_DATA_DIR", DataDir);
        }
    }
}
