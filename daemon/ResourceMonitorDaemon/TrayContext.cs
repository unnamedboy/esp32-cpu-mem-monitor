using Microsoft.Extensions.Hosting;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace ResourceMonitorDaemon
{
    public class TrayContext
    {
        private readonly IHost _host;
        private readonly string _logFilePath;
        private readonly NotifyIcon _notifyIcon;
        private readonly MonitorService _monitorService;
        private ToolStripMenuItem? _pauseResumeItem;

        public TrayContext(IHost host, string logFilePath, MonitorService monitorService)
        {
            _host = host;
            _logFilePath = logFilePath;
            _monitorService = monitorService;

            var contextMenu = new ContextMenuStrip();
            
            _pauseResumeItem = new ToolStripMenuItem(
                _monitorService.IsPaused ? "▶ 恢复发送 (Resume)" : "⏸ 暂停发送 (Pause)",
                null,
                OnPauseResumeClick
            );
            contextMenu.Items.Add(_pauseResumeItem);
            
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("查看日志 (Log)", null, OnViewLog);
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("退出 (Exit)", null, OnExitAsync);

            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                ContextMenuStrip = contextMenu,
                Text = "Resource Monitor Daemon",
                Visible = true
            };

            _notifyIcon.DoubleClick += OnViewLog;
            
            _monitorService.OnNotification = (title, message, icon) =>
            {
                _notifyIcon.ShowBalloonTip(5000, title, message, icon);
            };
            
            var updateTimer = new System.Windows.Forms.Timer();
            updateTimer.Interval = 1000;
            updateTimer.Tick += (s, e) => UpdatePauseResumeLabel();
            updateTimer.Start();
        }

        private void UpdatePauseResumeLabel()
        {
            if (_pauseResumeItem != null)
            {
                string newText = _monitorService.IsPaused 
                    ? "▶ 恢复发送 (Resume)" 
                    : "⏸ 暂停发送 (Pause)";
                
                if (_pauseResumeItem.Text != newText)
                {
                    _pauseResumeItem.Text = newText;
                }
            }
        }

        private void OnPauseResumeClick(object? sender, EventArgs e)
        {
            if (_monitorService.IsPaused)
            {
                _monitorService.Resume();
                _notifyIcon.ShowBalloonTip(2000, "已恢复", "监控发送已恢复运行", ToolTipIcon.Info);
            }
            else
            {
                _monitorService.Pause();
                _notifyIcon.ShowBalloonTip(2000, "已暂停", "监控发送已暂停", ToolTipIcon.Info);
            }
        }

        private void OnViewLog(object? sender, EventArgs e)
        {
            if (File.Exists(_logFilePath))
            {
                Process.Start(new ProcessStartInfo(_logFilePath) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show("暂无日志记录！", "Daemon", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private async void OnExitAsync(object? sender, EventArgs e)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();

            Application.Exit();
        }
    }
}
