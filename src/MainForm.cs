using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace MultiProxy
{
    public class MainForm : Form
    {
        private TabControl _tabs;
        private DataGridView _gridInterfaces;
        private Button _btnRefresh;

        private RadioButton _rbWhitelist, _rbBlacklist, _rbAll;
        private ListBox _listProcesses;
        private TextBox _txtNewProcess;
        private Button _btnAddProcess, _btnRemoveProcess, _btnPickRunning;

        private TextBox _txtPort;
        private Button _btnStart, _btnStop;
        private Label _lblStatus;
        private DataGridView _gridStats;
        private Chart _chartSpeed;
        private Dictionary<string, Series> _seriesMap = new Dictionary<string, Series>();
        private Queue<double>[] _history;
        private const int MAX_POINTS = 60;

        private TextBox _txtLog;

        private ProxyServer _proxy;
        private List<NetworkInterfaceInfo> _interfaces;
        private Timer _statsTimer;
        private Dictionary<string, long> _lastBytes = new Dictionary<string, long>();
        private DateTime _lastTick = DateTime.Now;

        public MainForm()
        {
            Text = "多网卡代理聚合器 v1.0";
            Size = new Size(880, 660);
            StartPosition = FormStartPosition.CenterScreen;

            InitUI();
            LoadInterfaces();
            RebuildProxy();
        }

        private void InitUI()
        {
            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs.TabPages.Add(CreateInterfacesTab());
            _tabs.TabPages.Add(CreateRulesTab());
            _tabs.TabPages.Add(CreateStatusTab());
            _tabs.TabPages.Add(CreateLogTab());
            Controls.Add(_tabs);

            _statsTimer = new Timer { Interval = 1000 };
            _statsTimer.Tick += StatsTimer_Tick;
        }

        // ============ 网卡配置 ============
        private TabPage CreateInterfacesTab()
        {
            var page = new TabPage("网卡配置");
            _gridInterfaces = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            _gridInterfaces.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "启用", FillWeight = 15 });
            _gridInterfaces.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "网卡", ReadOnly = true, FillWeight = 45 });
            _gridInterfaces.Columns.Add(new DataGridViewTextBoxColumn { Name = "IP", HeaderText = "IPv4", ReadOnly = true, FillWeight = 25 });
            _gridInterfaces.Columns.Add(new DataGridViewTextBoxColumn { Name = "Weight", HeaderText = "权重", FillWeight = 15 });

            _gridInterfaces.CellValueChanged += (s, e) =>
            {
                RebuildProxy();
                if (_proxy != null && _proxy.IsRunning) ResetChart();
            };
            _gridInterfaces.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_gridInterfaces.IsCurrentCellDirty)
                    _gridInterfaces.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            _btnRefresh = new Button { Text = "刷新网卡", Dock = DockStyle.Bottom, Height = 32 };
            _btnRefresh.Click += (s, e) => LoadInterfaces();

            page.Controls.Add(_gridInterfaces);
            page.Controls.Add(_btnRefresh);
            return page;
        }

        // ============ 进程规则 ============
        private TabPage CreateRulesTab()
        {
            var page = new TabPage("进程规则");
            var topPanel = new Panel { Dock = DockStyle.Top, Height = 80 };
            _rbWhitelist = new RadioButton { Text = "白名单（仅以下进程走代理）", Location = new Point(15, 10), AutoSize = true, Checked = true };
            _rbBlacklist = new RadioButton { Text = "黑名单（以下进程直连，其他走代理）", Location = new Point(15, 32), AutoSize = true };
            _rbAll = new RadioButton { Text = "全部走代理（忽略列表）", Location = new Point(15, 54), AutoSize = true };

            _rbWhitelist.CheckedChanged += (s, e) => RebuildProxy();
            _rbBlacklist.CheckedChanged += (s, e) => RebuildProxy();
            _rbAll.CheckedChanged += (s, e) => RebuildProxy();

            topPanel.Controls.Add(_rbWhitelist);
            topPanel.Controls.Add(_rbBlacklist);
            topPanel.Controls.Add(_rbAll);

            _listProcesses = new ListBox { Dock = DockStyle.Fill };

            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            _txtNewProcess = new TextBox { Location = new Point(10, 8), Width = 260, Text = "idman.exe" };
            _btnAddProcess = new Button { Text = "添加", Location = new Point(280, 6), Width = 70 };
            _btnRemoveProcess = new Button { Text = "删除选中", Location = new Point(360, 6), Width = 90 };
            _btnPickRunning = new Button { Text = "从运行进程选择", Location = new Point(460, 6), Width = 130 };

            _btnAddProcess.Click += (s, e) =>
            {
                string p = _txtNewProcess.Text.Trim().ToLower();
                if (p.Length == 0) return;
                if (!_listProcesses.Items.Contains(p))
                    _listProcesses.Items.Add(p);
                RebuildProxy();
            };
            _btnRemoveProcess.Click += (s, e) =>
            {
                if (_listProcesses.SelectedIndex >= 0)
                {
                    _listProcesses.Items.RemoveAt(_listProcesses.SelectedIndex);
                    RebuildProxy();
                }
            };
            _btnPickRunning.Click += (s, e) => PickRunningProcess();

            bottomPanel.Controls.Add(_txtNewProcess);
            bottomPanel.Controls.Add(_btnAddProcess);
            bottomPanel.Controls.Add(_btnRemoveProcess);
            bottomPanel.Controls.Add(_btnPickRunning);

            page.Controls.Add(_listProcesses);
            page.Controls.Add(bottomPanel);
            page.Controls.Add(topPanel);
            return page;
        }

        private void PickRunningProcess()
        {
            var procs = Process.GetProcesses()
                .Select(p => { try { return p.ProcessName.ToLower() + ".exe"; } catch { return null; } })
                .Where(x => x != null).Distinct().OrderBy(x => x).ToArray();

            using (var dlg = new Form())
            {
                dlg.Text = "选择运行中的进程";
                dlg.Size = new Size(300, 460);
                dlg.StartPosition = FormStartPosition.CenterParent;
                var lb = new ListBox { Dock = DockStyle.Fill };
                lb.Items.AddRange(procs);
                lb.DoubleClick += (s, e) =>
                {
                    if (lb.SelectedItem != null)
                    {
                        string p = lb.SelectedItem.ToString();
                        if (!_listProcesses.Items.Contains(p))
                            _listProcesses.Items.Add(p);
                        RebuildProxy();
                        dlg.Close();
                    }
                };
                dlg.Controls.Add(lb);
                dlg.ShowDialog(this);
            }
        }

        // ============ 运行状态 ============
        private TabPage CreateStatusTab()
        {
            var page = new TabPage("运行状态");

            var topPanel = new Panel { Dock = DockStyle.Top, Height = 50 };
            var lblPort = new Label { Text = "监听端口:", Location = new Point(15, 16), AutoSize = true };
            _txtPort = new TextBox { Location = new Point(80, 12), Width = 70, Text = "1080" };
            _btnStart = new Button { Text = "启动", Location = new Point(170, 10), Width = 80 };
            _btnStop = new Button { Text = "停止", Location = new Point(260, 10), Width = 80, Enabled = false };
            _lblStatus = new Label { Text = "未运行", Location = new Point(360, 16), AutoSize = true, ForeColor = Color.Gray };

            _btnStart.Click += (s, e) =>
            {
                int port;
                if (!int.TryParse(_txtPort.Text, out port) || port < 1024 || port > 65535)
                {
                    MessageBox.Show("端口无效，建议 1024-65535");
                    return;
                }
                try
                {
                    RebuildProxy();
                    _proxy.Start(port);
                    _btnStart.Enabled = false;
                    _btnStop.Enabled = true;
                    _lblStatus.Text = $"运行中 (127.0.0.1:{port})";
                    _lblStatus.ForeColor = Color.Green;
                    ResetChart();
                    _statsTimer.Start();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("启动失败: " + ex.Message);
                }
            };
            _btnStop.Click += (s, e) =>
            {
                _proxy?.Stop();
                _btnStart.Enabled = true;
                _btnStop.Enabled = false;
                _lblStatus.Text = "未运行";
                _lblStatus.ForeColor = Color.Gray;
                _statsTimer.Stop();
            };

            topPanel.Controls.Add(lblPort);
            topPanel.Controls.Add(_txtPort);
            topPanel.Controls.Add(_btnStart);
            topPanel.Controls.Add(_btnStop);
            topPanel.Controls.Add(_lblStatus);

            _gridStats = new DataGridView
            {
                Dock = DockStyle.Top,
                Height = 120,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            _gridStats.Columns.Add("Name", "网卡");
            _gridStats.Columns.Add("Speed", "实时速度");
            _gridStats.Columns.Add("Total", "累计流量");

            _chartSpeed = new Chart
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            var chartArea = new ChartArea("main");
            chartArea.AxisX.Title = "时间（秒前）";
            chartArea.AxisY.Title = "速度 (KB/s)";
            chartArea.AxisX.Minimum = 0;
            chartArea.AxisX.Maximum = MAX_POINTS;
            chartArea.AxisX.Interval = 10;
            chartArea.AxisX.LabelStyle.Format = "0";
            chartArea.AxisY.LabelStyle.Format = "0";
            chartArea.AxisX.MajorGrid.LineColor = Color.LightGray;
            chartArea.AxisY.MajorGrid.LineColor = Color.LightGray;
            _chartSpeed.ChartAreas.Add(chartArea);

            var legend = new Legend("legend") { Docking = Docking.Top, Alignment = StringAlignment.Center };
            _chartSpeed.Legends.Add(legend);

            page.Controls.Add(_chartSpeed);
            page.Controls.Add(_gridStats);
            page.Controls.Add(topPanel);
            return page;
        }

        private void ResetChart()
        {
            if (_chartSpeed == null) return;
            _chartSpeed.Series.Clear();
            _seriesMap.Clear();
            if (_interfaces == null) return;

            var colors = new[] { Color.DodgerBlue, Color.OrangeRed, Color.SeaGreen, Color.MediumPurple, Color.Goldenrod };
            int ci = 0;
            _history = new Queue<double>[_interfaces.Count];

            for (int i = 0; i < _interfaces.Count; i++)
            {
                var iface = _interfaces[i];
                if (!iface.Enabled) { _history[i] = null; continue; }
                _history[i] = new Queue<double>();

                var series = new Series(iface.Name)
                {
                    ChartType = SeriesChartType.Spline,
                    BorderWidth = 2,
                    Color = colors[ci % colors.Length],
                    XValueType = ChartValueType.Int32,
                    YValueType = ChartValueType.Double,
                    IsVisibleInLegend = true
                };
                _chartSpeed.Series.Add(series);
                _seriesMap[iface.Name] = series;
                ci++;
            }
        }

        private void StatsTimer_Tick(object sender, EventArgs e)
        {
            if (_proxy == null || _interfaces == null) return;
            var now = DateTime.Now;
            double dt = (now - _lastTick).TotalSeconds;
            if (dt <= 0) dt = 1;
            _lastTick = now;

            _gridStats.Rows.Clear();

            for (int i = 0; i < _interfaces.Count; i++)
            {
                var iface = _interfaces[i];
                if (!iface.Enabled) continue;

                long total = _proxy.BytesPerInterface.TryGetValue(iface.Name, out var b) ? b : 0;
                long last = _lastBytes.TryGetValue(iface.Name, out var lb) ? lb : 0;
                double speed = (total - last) / dt;
                _lastBytes[iface.Name] = total;

                _gridStats.Rows.Add(iface.Name, FormatSpeed(speed), FormatBytes(total));

                if (_seriesMap.TryGetValue(iface.Name, out var series) && _history[i] != null)
                {
                    double kbSpeed = speed / 1024.0;
                    _history[i].Enqueue(kbSpeed);
                    while (_history[i].Count > MAX_POINTS)
                        _history[i].Dequeue();

                    series.Points.Clear();
                    int idx = 0;
                    foreach (var v in _history[i])
                        series.Points.AddXY(idx++, v);
                }
            }

            if (_chartSpeed != null && _chartSpeed.ChartAreas.Count > 0 && _seriesMap.Count > 0)
            {
                double max = 0;
                foreach (var s in _seriesMap.Values)
                    if (s.Points.Count > 0)
                        max = Math.Max(max, s.Points.Max(p => p.YValues[0]));

                var area = _chartSpeed.ChartAreas[0];
                double upper = Math.Max(100, Math.Ceiling(max * 1.2 / 100) * 100);
                area.AxisY.Maximum = upper;
                area.AxisY.Interval = upper / 5.0;
            }
        }

        private static string FormatSpeed(double bps)
        {
            if (bps < 1024) return $"{bps:F0} B/s";
            if (bps < 1024 * 1024) return $"{bps / 1024:F1} KB/s";
            return $"{bps / 1024 / 1024:F2} MB/s";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
            return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
        }

        // ============ 日志 ============
        private TabPage CreateLogTab()
        {
            var page = new TabPage("日志");
            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Font = new Font("Consolas", 9)
            };
            page.Controls.Add(_txtLog);
            return page;
        }

        // ============ 逻辑 ============
        private void LoadInterfaces()
        {
            var selected = new Dictionary<string, (bool, int)>();
            if (_interfaces != null)
                foreach (var i in _interfaces)
                    selected[i.Name] = (i.Enabled, i.Weight);

            _interfaces = NetworkHelper.GetActiveInterfaces();
            foreach (var i in _interfaces)
            {
                if (selected.TryGetValue(i.Name, out var saved))
                {
                    i.Enabled = saved.Item1;
                    i.Weight = saved.Item2;
                }
            }

            _gridInterfaces.Rows.Clear();
            foreach (var i in _interfaces)
                _gridInterfaces.Rows.Add(i.Enabled, i.DisplayName, i.IPv4.ToString(), i.Weight);

            RebuildProxy();
            if (_proxy != null && _proxy.IsRunning) ResetChart();
        }

        private void RebuildProxy()
        {
            if (_gridInterfaces != null && _interfaces != null)
            {
                for (int row = 0; row < _gridInterfaces.Rows.Count && row < _interfaces.Count; row++)
                {
                    var r = _gridInterfaces.Rows[row];
                    _interfaces[row].Enabled = r.Cells["Enabled"].Value is bool b && b;
                    int w;
                    int.TryParse(Convert.ToString(r.Cells["Weight"].Value), out w);
                    _interfaces[row].Weight = Math.Max(1, w);
                }
            }

            var mode = _rbAll != null && _rbAll.Checked ? RuleMode.All :
                       _rbBlacklist != null && _rbBlacklist.Checked ? RuleMode.Blacklist :
                       RuleMode.Whitelist;

            var rules = new HashSet<string>();
            if (_listProcesses != null)
                foreach (var item in _listProcesses.Items)
                    rules.Add(item.ToString().ToLower());

            if (_proxy == null)
            {
                _proxy = new ProxyServer(_interfaces, mode, rules);
                _proxy.OnLog += AppendLog;
            }
            else
            {
                _proxy.UpdateConfig(_interfaces, mode, rules);
            }
        }

        private void AppendLog(string msg)
        {
            if (_txtLog == null) return;
            if (_txtLog.InvokeRequired)
                _txtLog.BeginInvoke(new Action(() => AppendLog(msg)));
            else
                _txtLog.AppendText(msg + Environment.NewLine);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { _proxy?.Stop(); } catch { }
            base.OnFormClosing(e);
        }
    }
}