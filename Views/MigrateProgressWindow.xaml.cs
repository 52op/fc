using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FC.Services;

namespace FC.Views
{
    /// <summary>
    /// 迁移/还原进度窗。实现 IProgressDialog：VM 写日志、取取消令牌，结束 Close。
    /// 用户点取消或关闭窗口都会触发 Token 取消。
    /// 进度条驱动策略：不依赖 WPF 动画/渲染帧（远程/后台会话可能无渲染帧导致 Value 不动），
    /// 改用 DispatcherTimer 每 250ms 直接写 Value 递增（0→100→0 循环）——纯消息循环驱动，任何环境必然动；
    /// 里程碑（SetProgress）会短暂显示真实进度，完成后固定 100。
    /// </summary>
    public partial class MigrateProgressWindow : Window, IProgressDialog
    {
        private readonly CancellationTokenSource _cts;
        private readonly DispatcherTimer _creepTimer;
        private DateTime _lastMilestoneAt;
        private double _creepValue;
        private bool _done;
        private bool _closed;

        public MigrateProgressWindow(string title, string operationLabel)
        {
            InitializeComponent();
            Title = "FC - " + (title ?? "操作");
            StatusText.Text = operationLabel ?? "";
            _cts = new CancellationTokenSource();

            // 蠕动定时器：消息循环驱动，不依赖渲染帧
            _creepTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _creepTimer.Tick += OnCreepTick;
            _creepTimer.Start();
        }

        public CancellationToken Token
        {
            get { return _cts.Token; }
        }

        /// <summary>进度条元素（仅供自动化/测试读值）。</summary>
        public System.Windows.Controls.ProgressBar ProgressBarView
        {
            get { return ProgressBar; }
        }

        private void OnCreepTick(object sender, EventArgs e)
        {
            if (_closed || _done)
            {
                return;
            }
            // 里程碑刚设置后的短暂停留（给真实进度一个可读窗口）
            if ((DateTime.Now - _lastMilestoneAt).TotalMilliseconds < 1500)
            {
                return;
            }
            // 无真实进度时：缓慢爬升到 90% 后停住（不 0→100 循环跳动），表示"正在工作"
            _creepValue += 1.5;
            if (_creepValue >= 90)
            {
                _creepValue = 90;
            }
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = _creepValue;
            UpdateValueText();
        }

        private void UpdateValueText()
        {
            if (ValueText == null)
            {
                return;
            }
            // 距上次真实里程碑已超过 1.5s（即当前是蠕动爬升、无真实进度）→ 只显示"进行中"，不显示假 %
            bool creeping = (DateTime.Now - _lastMilestoneAt).TotalMilliseconds >= 1500;
            if (_done)
            {
                ValueText.Text = "100%";
            }
            else if (creeping)
            {
                ValueText.Text = "进行中…";
            }
            else
            {
                ValueText.Text = ((int)Math.Round(ProgressBar.Value)).ToString() + "%";
            }
        }

        public void AppendLog(string line)
        {
            if (_closed)
            {
                return;
            }
            Action action = () =>
            {
                if (_closed)
                {
                    return;
                }
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.ScrollToEnd();
            };
            if (Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.Invoke(action);
            }
        }

        public void SetStatus(string text)
        {
            if (_closed)
            {
                return;
            }
            Action action = () =>
            {
                if (!_closed)
                {
                    StatusText.Text = text;
                }
            };
            if (Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.Invoke(action);
            }
        }

        public void SetProgress(double value)
        {
            if (_closed)
            {
                return;
            }
            Action action = () =>
            {
                if (_closed)
                {
                    return;
                }
                if (value >= 99.5)
                {
                    // 完成态：固定 100 停止蠕动
                    _done = true;
                    ProgressBar.IsIndeterminate = false;
                    ProgressBar.Value = 100;
                    UpdateValueText();
                    _creepTimer.Stop();
                    return;
                }
                if (value >= 0)
                {
                    ProgressBar.IsIndeterminate = false;
                    ProgressBar.Value = Math.Max(0, Math.Min(100, value));
                    _creepValue = Math.Min(98, ProgressBar.Value);
                    _lastMilestoneAt = DateTime.Now;
                    UpdateValueText();
                }
            };
            if (Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.Invoke(action);
            }
        }

        public new void Close()
        {
            if (_closed)
            {
                return;
            }
            _creepTimer.Stop();
            _closed = true;
            if (Dispatcher.CheckAccess())
            {
                base.Close();
            }
            else
            {
                Dispatcher.Invoke(base.Close);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _creepTimer.Stop();
            _closed = true;
            _cts.Cancel();
        }

        private void OnCancelClicked(object sender, RoutedEventArgs e)
        {
            CancelButton.IsEnabled = false;
            StatusText.Text = "正在取消…（等待当前命令结束后返回）";
            _cts.Cancel();
        }

        public void Dispose()
        {
            if (!_closed)
            {
                Close();
            }
        }
    }
}