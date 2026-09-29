using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MultiProxy
{
    public static class SystemProxyManager
    {
        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        private static bool _isSet = false;
        private static string _originalProxyEnable = "";
        private static string _originalProxyServer = "";

        public static void Set(int port)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
                {
                    if (key != null)
                    {
                        // 保存原始设置，以便恢复
                        if (!_isSet)
                        {
                            _originalProxyEnable = key.GetValue("ProxyEnable")?.ToString() ?? "0";
                            _originalProxyServer = key.GetValue("ProxyServer")?.ToString() ?? "";
                            _isSet = true;
                        }

                        // 设置全局代理指向我们的本地 SOCKS5 代理
                        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                        key.SetValue("ProxyServer", $"socks=127.0.0.1:{port}", RegistryValueKind.String);
                        
                        // 刷新系统代理设置
                        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("设置系统代理失败: " + ex.Message);
            }
        }

        public static void Restore()
        {
            if (!_isSet) return;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
                {
                    if (key != null)
                    {
                        key.SetValue("ProxyEnable", int.Parse(_originalProxyEnable), RegistryValueKind.DWord);
                        if (string.IsNullOrEmpty(_originalProxyServer))
                            key.DeleteValue("ProxyServer", false);
                        else
                            key.SetValue("ProxyServer", _originalProxyServer, RegistryValueKind.String);

                        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
                    }
                }
                _isSet = false;
            }
            catch { }
        }
    }
}