using System;
using System.IO;
using System.Windows.Forms;

namespace MultiProxy
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                // 将真实报错写入 error.log 文件
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
                File.WriteAllText(logPath, $"[{DateTime.Now}] 启动失败:\n{ex}");
                MessageBox.Show($"程序启动失败，错误已写入 error.log：\n\n{ex.Message}", "启动错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}