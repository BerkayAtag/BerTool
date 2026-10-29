using BerTool.TrayAgent.Models;
using BerTool.TrayAgent.Utilities;

namespace BerTool.TrayAgent;

internal sealed class MainPopupForm : Form
{
    private readonly Label _overallStatusValueLabel;
    private readonly Label _networkQualityValueLabel;
    private readonly ListView _serviceListView;
    private readonly ListView _deviceInfoListView;
    private readonly ListView _bitLockerListView;
    private readonly Label _bitLockerSectionTitle;
    private readonly Label _preparedByLabel;
    private readonly Label _lastUpdatedLabel;
    private readonly Label _reportingStatusLabel;

    internal bool AllowClose { get; set; }

    public MainPopupForm()
    {
        Text = "BerTool Cihaz Durumu";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ControlBox = true;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 590;
        Height = 716;
        BackColor = Color.WhiteSmoke;

        // Pencere başlığında varsayılan WinForms simgesi yerine marka simgesi.
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
        }
        catch (Exception)
        {
            // Simge okunamazsa varsayılan simgeyle devam edilir.
        }

        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 11,
            AutoScroll = true
        };

        for (var rowIndex = 0; rowIndex < 11; rowIndex++)
        {
            container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        _preparedByLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            ForeColor = Color.DimGray,
            Text = "Berkay Atağ tarafından hazırlanmıştır.",
            Margin = new Padding(0, 0, 0, 6)
        };
        container.Controls.Add(_preparedByLabel, 0, 0);

        container.Controls.Add(CreateSectionTitle("Genel Durum"), 0, 1);

        var overallStatusPanel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 8),
            RowCount = 1
        };
        overallStatusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48F));
        overallStatusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52F));

        _overallStatusValueLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.Firebrick,
            Text = "Cihaz Durumu: Riskli",
            Margin = new Padding(0)
        };
        overallStatusPanel.Controls.Add(_overallStatusValueLabel, 0, 0);

        _networkQualityValueLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Font = new Font("Segoe UI", 8.8F, FontStyle.Bold),
            ForeColor = Color.DimGray,
            Text = "Network Hızı: -\r\nİnternet Hızı: -",
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0)
        };
        overallStatusPanel.Controls.Add(_networkQualityValueLabel, 1, 0);

        container.Controls.Add(overallStatusPanel, 0, 2);

        container.Controls.Add(CreateSectionTitle("Servis Kontrolleri"), 0, 3);
        _serviceListView = CreateListView(
            new[]
            {
                ("Ürün", 250),
                ("Durum", 180),
                ("İkon", 80)
            },
            170);
        container.Controls.Add(_serviceListView, 0, 4);

        container.Controls.Add(CreateSectionTitle("Cihaz Bilgileri"), 0, 5);
        _deviceInfoListView = CreateListView(
            new[]
            {
                ("Alan", 170),
                ("Değer", 330)
            },
            230);
        container.Controls.Add(_deviceInfoListView, 0, 6);

        _bitLockerSectionTitle = CreateSectionTitle("BitLocker Bilgisi");
        container.Controls.Add(_bitLockerSectionTitle, 0, 7);
        _bitLockerListView = CreateListView(
            new[]
            {
                ("Sürücü", 110),
                ("Şifreleme", 290),
                ("İkon", 80)
            },
            130);
        container.Controls.Add(_bitLockerListView, 0, 8);

        _lastUpdatedLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            ForeColor = Color.DimGray,
            Text = "Son Güncelleme: -",
            Margin = new Padding(0, 8, 0, 0)
        };
        container.Controls.Add(_lastUpdatedLabel, 0, 9);

        _reportingStatusLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 20,
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            ForeColor = Color.DimGray,
            Text = "Sunucu raporu: yapılandırılmadı",
            Margin = new Padding(0, 2, 0, 0)
        };
        container.Controls.Add(_reportingStatusLabel, 0, 10);

        Controls.Add(container);
    }

    public void SetReportingStatus(string statusText, bool isError)
    {
        if (_reportingStatusLabel.IsDisposed)
        {
            return;
        }

        _reportingStatusLabel.Text = statusText;
        _reportingStatusLabel.ForeColor = isError ? Color.Firebrick : Color.DimGray;
        _reportingStatusLabel.BackColor = Color.WhiteSmoke;
    }

    public void Render(HealthStatus status)
    {
        _overallStatusValueLabel.Text = $"Cihaz Durumu: {status.DeviceStatus}";
        _overallStatusValueLabel.ForeColor = status.IsHealthy ? Color.ForestGreen : Color.Firebrick;
        _networkQualityValueLabel.Text = $"Network Hızı: {status.NetworkQuality.BandwidthText}\r\nİnternet Hızı: {status.NetworkQuality.InternetSpeedText}";
        _networkQualityValueLabel.ForeColor = ResolveNetworkQualityColor(status.NetworkQuality.DisplayLevel);

        _serviceListView.BeginUpdate();
        _serviceListView.Items.Clear();
        foreach (var service in status.ServiceStatuses)
        {
            var item = new ListViewItem(new[] { service.DisplayName, service.StatusText, service.StatusIcon })
            {
                ForeColor = service.IsHealthy ? Color.ForestGreen : Color.Firebrick
            };
            _serviceListView.Items.Add(item);
        }
        _serviceListView.EndUpdate();

        _deviceInfoListView.BeginUpdate();
        _deviceInfoListView.Items.Clear();
        AddDeviceInfo("Makine Adı", status.SystemInfo.MachineName);
        AddDeviceInfo("Domain", status.SystemInfo.DomainOrWorkgroup);
        AddDeviceInfo("Kullanıcı", status.SystemInfo.LoggedInUser);
        AddDeviceInfo("Bilgisayar Modeli", status.SystemInfo.ComputerModel);
        AddDeviceInfo("CPU", status.SystemInfo.CpuModel);
        AddDeviceInfo("CPU Çekirdek Sayısı", status.SystemInfo.PhysicalCoreCount.ToString());
        AddDeviceInfo("Mantıksal İşlemci", status.SystemInfo.LogicalProcessorCount.ToString());
        AddDeviceInfo("RAM", SizeFormatter.ToFriendlyGb(status.SystemInfo.TotalRamGb));
        AddDeviceInfo("Toplam Disk Kapasitesi", SizeFormatter.ToFriendlyGb(status.SystemInfo.TotalDiskGb));

        if (status.SystemInfo.IpAddresses.Count == 0)
        {
            AddDeviceInfo("IP Adresleri", "Bulunamadı");
        }
        else
        {
            for (var i = 0; i < status.SystemInfo.IpAddresses.Count; i++)
            {
                var ip = status.SystemInfo.IpAddresses[i];
                var key = i == 0 ? "IP Adresleri" : string.Empty;
                AddDeviceInfo(key, $"{ip.Address} ({ip.AdapterType} - {ip.AdapterName})");
            }
        }

        if (status.SystemInfo.PhysicalDiskDetails.Count > 0)
        {
            for (var i = 0; i < status.SystemInfo.PhysicalDiskDetails.Count; i++)
            {
                var key = i == 0 ? "Disk Detayları" : string.Empty;
                AddDeviceInfo(key, status.SystemInfo.PhysicalDiskDetails[i]);
            }
        }

        _deviceInfoListView.EndUpdate();

        _bitLockerSectionTitle.Visible = status.IsBitLockerApplicable;
        _bitLockerListView.Visible = status.IsBitLockerApplicable;

        if (status.IsBitLockerApplicable)
        {
            _bitLockerListView.BeginUpdate();
            _bitLockerListView.Items.Clear();
            if (status.BitLockerVolumes.Count == 0)
            {
                _bitLockerListView.Items.Add(new ListViewItem(new[] { "-", "Bilgi alınamadı", "✖" })
                {
                    ForeColor = Color.Firebrick
                });
            }
            else
            {
                foreach (var volume in status.BitLockerVolumes.OrderBy(v => v.DriveLetter, StringComparer.OrdinalIgnoreCase))
                {
                    var item = new ListViewItem(new[] { volume.DriveLetter, volume.EncryptionMethod, volume.StatusIcon })
                    {
                        ForeColor = volume.IsHealthy ? Color.ForestGreen : Color.Firebrick
                    };
                    _bitLockerListView.Items.Add(item);
                }
            }

            _bitLockerListView.EndUpdate();
        }
        else
        {
            _bitLockerListView.Items.Clear();
        }

        _lastUpdatedLabel.Text = $"Son Güncelleme: {status.CheckedAt:dd.MM.yyyy HH:mm:ss}";
    }

    public void ShowNear(Point trayAnchor)
    {
        var workingArea = Screen.FromPoint(trayAnchor).WorkingArea;
        var targetX = Math.Min(Math.Max(workingArea.Left, trayAnchor.X - Width + 10), workingArea.Right - Width);
        var targetY = Math.Min(Math.Max(workingArea.Top, trayAnchor.Y - Height - 10), workingArea.Bottom - Height);

        Location = new Point(targetX, targetY);

        if (!Visible)
        {
            Show();
        }

        BringToFront();
    }

    private void AddDeviceInfo(string key, string value)
    {
        _deviceInfoListView.Items.Add(new ListViewItem(new[] { key, value }));
    }

    private static Label CreateSectionTitle(string title)
    {
        return new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(50, 50, 50),
            Text = title,
            Margin = new Padding(0, 8, 0, 6)
        };
    }

    private static ListView CreateListView((string Title, int Width)[] columns, int height)
    {
        var listView = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
            Height = height,
            Dock = DockStyle.Top
        };

        foreach (var column in columns)
        {
            listView.Columns.Add(column.Title, column.Width);
        }

        return listView;
    }

    private static Color ResolveNetworkQualityColor(NetworkQualityLevel level)
    {
        return level switch
        {
            NetworkQualityLevel.Good => Color.ForestGreen,
            NetworkQualityLevel.Warning => Color.DarkGoldenrod,
            NetworkQualityLevel.Critical => Color.Firebrick,
            _ => Color.DimGray
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override bool ShowWithoutActivation => true;
}
