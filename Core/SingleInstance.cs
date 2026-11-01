using System;
using System.IO.Pipes;
using System.Text;

namespace Suzu.Core
{
    /// <summary>
    /// 单实例进程间通信：再次双击 exe 时不新建托盘进程，
    /// 而是通过命名管道让已经在运行的实例把设置窗口调出来。
    /// </summary>
    public static class SingleInstance
    {
        public const string PipeName = "Suzu_ShowSettings";
        public const string ShowCommand = "SHOW";

        /// <summary>
        /// 请求已运行实例打开设置窗口。
        /// true = 请求已送达（对方会负责弹窗/提示）；false = 连不上（对方可能卡死或版本过旧）。
        /// </summary>
        public static bool TrySendShowRequest(int connectTimeoutMs = 2500)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    client.Connect(connectTimeoutMs);

                    var payload = Encoding.UTF8.GetBytes(ShowCommand + "\n");
                    client.Write(payload, 0, payload.Length);
                    client.Flush();

                    // 读回执只是确认一下；读不到也不影响（请求已经写进去了）
                    try
                    {
                        var buffer = new byte[16];
                        var read = client.ReadAsync(buffer, 0, buffer.Length);
                        read.Wait(1200);
                    }
                    catch { }

                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
