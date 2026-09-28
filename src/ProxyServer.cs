using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiProxy
{
    public enum RuleMode { Whitelist, Blacklist, All }

    public class ProxyServer
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private List<NetworkInterfaceInfo> _interfaces;
        private RuleMode _mode;
        private HashSet<string> _processRules;
        private int _currentIndex = -1;
        private readonly object _lock = new object();

        public event Action<string> OnLog;
        public ConcurrentDictionary<string, long> BytesPerInterface = new ConcurrentDictionary<string, long>();
        public bool IsRunning => _listener != null;

        public ProxyServer(List<NetworkInterfaceInfo> interfaces, RuleMode mode, HashSet<string> rules)
        {
            UpdateConfig(interfaces, mode, rules);
        }

        public void UpdateConfig(List<NetworkInterfaceInfo> interfaces, RuleMode mode, HashSet<string> rules)
        {
            _interfaces = interfaces;
            _mode = mode;
            _processRules = rules;
        }

        public void Start(int port)
        {
            if (_listener != null) return;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Log($"代理已启动，监听 127.0.0.1:{port}");
            Task.Run(() => AcceptLoop(_cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            Log("代理已停止");
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClient(client, ct), ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        Log($"接受连接出错: {ex.Message}");
                }
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken ct)
        {
            string clientProcess = "unknown";
            try
            {
                var clientEndPoint = (IPEndPoint)client.Client.RemoteEndPoint;
                int clientPort = clientEndPoint.Port;

                int pid = ProcessHelper.GetPidByLocalPort(clientPort);
                if (pid > 0)
                {
                    try { clientProcess = Process.GetProcessById(pid).ProcessName.ToLower() + ".exe"; }
                    catch { }
                }

                if (!ShouldUseProxy(clientProcess))
                {
                    Log($"[{clientProcess}] 按规则拒绝代理");
                    client.Close();
                    return;
                }

                var stream = client.GetStream();

                byte[] header = new byte[2];
                if (!await ReadExactAsync(stream, header, 0, 2, ct)) return;
                if (header[0] != 5) return;

                byte[] methods = new byte[header[1]];
                if (!await ReadExactAsync(stream, methods, 0, methods.Length, ct)) return;

                await stream.WriteAsync(new byte[] { 5, 0 }, 0, 2, ct);

                byte[] req = new byte[4];
                if (!await ReadExactAsync(stream, req, 0, 4, ct)) return;
                if (req[0] != 5 || req[1] != 1) return;

                string host;
                if (req[3] == 1)
                {
                    byte[] addr = new byte[4];
                    if (!await ReadExactAsync(stream, addr, 0, 4, ct)) return;
                    host = new IPAddress(addr).ToString();
                }
                else if (req[3] == 3)
                {
                    byte[] lenBuf = new byte[1];
                    if (!await ReadExactAsync(stream, lenBuf, 0, 1, ct)) return;
                    byte[] domain = new byte[lenBuf[0]];
                    if (!await ReadExactAsync(stream, domain, 0, domain.Length, ct)) return;
                    host = Encoding.ASCII.GetString(domain);
                }
                else if (req[3] == 4)
                {
                    byte[] addr = new byte[16];
                    if (!await ReadExactAsync(stream, addr, 0, 16, ct)) return;
                    host = new IPAddress(addr).ToString();
                }
                else return;

                byte[] portBuf = new byte[2];
                if (!await ReadExactAsync(stream, portBuf, 0, 2, ct)) return;
                int port = (portBuf[0] << 8) | portBuf[1];

                var iface = SelectInterface();
                if (iface == null)
                {
                    Log("没有可用的出口网卡");
                    client.Close();
                    return;
                }

                Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    // 直接绑定到指定网卡的 IP 即可
                    socket.Bind(new IPEndPoint(iface.IPv4, 0));
                    await socket.ConnectAsync(host, port);
                }
                catch (Exception ex)
                {
                    Log($"[{clientProcess}] 连接 {host}:{port} 失败: {ex.Message}");
                    try { socket.Close(); } catch { }
                    await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10, ct);
                    client.Close();
                    return;
                }

                var localEp = (IPEndPoint)socket.LocalEndPoint;
                byte[] resp = new byte[10];
                resp[0] = 5; resp[1] = 0; resp[2] = 0; resp[3] = 1;
                Array.Copy(localEp.Address.GetAddressBytes(), 0, resp, 4, 4);
                resp[8] = (byte)(localEp.Port >> 8);
                resp[9] = (byte)(localEp.Port & 0xFF);
                await stream.WriteAsync(resp, 0, 10, ct);

                var remoteStream = new NetworkStream(socket, true);
                Log($"[{clientProcess}] {host}:{port} → {iface.Name}");

                var task1 = PumpAsync(stream, remoteStream, iface.Name, ct);
                var task2 = PumpAsync(remoteStream, stream, iface.Name, ct);
                await Task.WhenAny(task1, task2);
                try { socket.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Log($"处理客户端时出错: {ex.Message}");
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private async Task PumpAsync(NetworkStream from, NetworkStream to, string ifaceName, CancellationToken ct)
        {
            byte[] buffer = new byte[16384];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int n = await from.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (n == 0) break;
                    await to.WriteAsync(buffer, 0, n, ct);
                    BytesPerInterface.AddOrUpdate(ifaceName, n, (k, v) => v + n);
                }
            }
            catch { }
            finally
            {
                try { to.Close(); } catch { }
            }
        }

        private NetworkInterfaceInfo SelectInterface()
        {
            var enabled = _interfaces.FindAll(i => i.Enabled);
            if (enabled.Count == 0) return null;

            var weighted = new List<NetworkInterfaceInfo>();
            foreach (var iface in enabled)
                for (int i = 0; i < Math.Max(1, iface.Weight); i++)
                    weighted.Add(iface);

            lock (_lock)
            {
                _currentIndex = (_currentIndex + 1) % weighted.Count;
                return weighted[_currentIndex];
            }
        }

        private bool ShouldUseProxy(string processName)
        {
            if (_processRules == null) return true;
            switch (_mode)
            {
                case RuleMode.All: return true;
                case RuleMode.Whitelist: return _processRules.Contains(processName);
                case RuleMode.Blacklist: return !_processRules.Contains(processName);
                default: return true;
            }
        }

        private async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await stream.ReadAsync(buffer, offset + total, count - total, ct);
                if (n == 0) return false;
                total += n;
            }
            return true;
        }

        private void Log(string msg) => OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");
    }
}