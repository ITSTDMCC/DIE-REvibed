using System;
using System.IO;

namespace EpidemicServer
{
    public static class Log
    {
        private static readonly object Gate = new object();
        public static string FilePath;

        public static void Info(string text) { Write("INFO", text); }
        public static void Warn(string text) { Write("WARN", text); }
        public static void Error(string text) { Write("ERROR", text); }

        private static void Write(string level, string text)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + text;
            lock (Gate)
            {
                Console.WriteLine(line);
                if (FilePath != null)
                {
                    try { File.AppendAllText(FilePath, line + Environment.NewLine); }
                    catch (IOException) { }
                }
            }
        }
    }
}
