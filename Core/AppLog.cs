using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Suzu.Core
{
    /// <summary>简单的滚动文件日志 + 界面可订阅的日志事件。</summary>
    public static class AppLog
    {
        private const long MaxBytes = 1024 * 1024; // 1 MB
        private static readonly object Sync = new object();
        private static string _dir;
        private static string _file;

        /// <summary>日志写入后触发（可能在任意线程）。</summary>
        public static event Action<string> Written;

        public static string LogFilePath { get { return _file; } }
        public static string DataDirectory { get { return _dir; } }

        public static void Init()
        {
            try
            {
                _dir = AppPaths.DataDirectory;
                Directory.CreateDirectory(_dir);
                _file = Path.Combine(_dir, "log.txt");
                RollIfNeeded();
            }
            catch
            {
                _dir = null;
                _file = null;
            }
        }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        public static void Error(string msg, Exception ex)
        {
            Write("ERROR", msg + (ex == null ? "" : " | " + ex.GetType().Name + ": " + ex.Message));
        }

        private static void Write(string level, string msg)
        {
            var line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}", DateTime.Now, level, msg);
            lock (Sync)
            {
                try
                {
                    if (_file == null) Init();
                    RollIfNeeded();
                    File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // 日志失败不能影响主流程
                }
            }

            var handler = Written;
            if (handler != null)
            {
                try { handler(line); } catch { }
            }
        }

        private static void RollIfNeeded()
        {
            try
            {
                if (_file == null || !File.Exists(_file)) return;
                var info = new FileInfo(_file);
                if (info.Length < MaxBytes) return;
                var old = Path.Combine(_dir, "log.old.txt");
                if (File.Exists(old)) File.Delete(old);
                File.Move(_file, old);
            }
            catch
            {
            }
        }
    }
}
