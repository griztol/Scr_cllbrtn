using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Screener
{
    public enum LogType
    {
       Data, Action, Error, Info, Result
    };

    public static class Logger
    {
        static string currentDate = DateTime.Now.ToString("dd-MM-yyyy");
        static string path = GetLogFilePath();
        static StreamWriter logsFile = new StreamWriter(path, true, System.Text.Encoding.Default);
        static ConcurrentQueue<string> logs = new ConcurrentQueue<string>();

        static Logger()
        {
            Task.Run(loggerWork);
        }

        public static void Add(string? cName, string mes, LogType tp)
        {
            //if (!GlbConst.workStopped && tp == LogType.Error)
            //{
            //    GlbConst.workStopped = true;
            //    File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "stop_100.txt"), "");
            //}


            if (cName == null) { cName = "GLOBAL"; }
            string tm = DateTime.Now.ToString("HH:mm:ss.fff");
            logs.Enqueue(tm + " " + tp + " " + cName + "  " + mes);
        }

        static void loggerWork()
        {
            string s;
            while (true)
            {
                UpdateLogFileIfNeeded();
                int bInt = logs.Count;
                if (bInt > 0)
                {
                    for (int i = 0; i < bInt; i++)
                    {
                        if (!logs.TryDequeue(out s)) { s = "error AddDataToLog"; }
                        logsFile.WriteLine(s);
                        Console.WriteLine(s);
                    }
                    logsFile.Flush();
                }
                else { Task.Delay(100).Wait(); }
            }
        }

        static string GetLogFilePath()
        {
            string dateTimeString = DateTime.Now.ToString("dd-MM-yyyy_HH-mm-ss");
            string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + "\\Logs";
            Directory.CreateDirectory(folderPath);
            return folderPath + $"\\Log_{dateTimeString}.txt";
        }

        static void UpdateLogFileIfNeeded()
        {
            string todayDate = DateTime.Now.ToString("dd-MM-yyyy");
            if (todayDate != currentDate)
            {
                // Close the current log file
                logsFile.Close();

                // Update the current date and create a new log file
                currentDate = todayDate;
                path = GetLogFilePath();
                logsFile = new StreamWriter(path, true, System.Text.Encoding.Default);
            }
        }

    }
}
