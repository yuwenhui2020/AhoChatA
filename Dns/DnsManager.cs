using System;
using System.Collections.Generic;
using System.IO;
using Android.Content;
using Newtonsoft.Json;

namespace AhoChatA.DnsCore
{
    /// <summary>
    /// Android DNS 管理器，单例模式
    /// </summary>
    public class DnsManager
    {
        private static readonly Lazy<DnsManager> _instance = new Lazy<DnsManager>(() => new DnsManager());
        public static DnsManager Instance => _instance.Value;

        private DnsServer _dnsServer;
        private DnsServerConfig _config;
        private string _configPath;
        private Context _context;

        private DnsManager()
        {
            // 5353 is already used by Libp2p mDNS; use a separate unprivileged DNS listener.
            _config = new DnsServerConfig { Port = 1053 };
        }

        private readonly object _initializeLock = new object();
        public void Initialize(Context context)
        {
            lock (_initializeLock)
            {
                if (_context != null) return;
                var application = (context ?? throw new ArgumentNullException(nameof(context))).ApplicationContext;
                var dir = Path.Combine(application.FilesDir.AbsolutePath, "dns");
                Directory.CreateDirectory(dir);
                _configPath = Path.Combine(dir, "dns_config.json");
                LoadConfig();
                _context = application;
            }
        }

        private void EnsureInitialized()
        {
            if (_context == null) Initialize(AhoChatApp.Context);
        }

        public DnsServerConfig GetConfig()
        {
            EnsureInitialized();
            return _config;
        }

        public bool AutoStart
        {
            get
            {
                EnsureInitialized();
                return _context.GetSharedPreferences("dns_boot", FileCreationMode.Private).GetBoolean("enabled", false);
            }
            set
            {
                EnsureInitialized();
                _context.GetSharedPreferences("dns_boot", FileCreationMode.Private).Edit().PutBoolean("enabled", value).Apply();
            }
        }

        public bool RequiresVpnConsent
        {
            get { EnsureInitialized(); return Android.Net.VpnService.Prepare(_context) != null; }
        }

        public void SetDnsServer(DnsServer server)
        {
            _dnsServer = server;
        }

        public DnsServer GetDnsServer() => _dnsServer;

        public void UpdateConfig(Action<DnsServerConfig> updateAction)
        {
            EnsureInitialized();
            if (_dnsServer != null) _dnsServer.UpdateConfig(updateAction);
            else updateAction(_config);
            SaveConfig();
        }

        public void StartVpn()
        {
            EnsureInitialized();
            if (RequiresVpnConsent)
                throw new InvalidOperationException(L10n.T("请先在 Android 设备上授权 DNS VPN"));

            // 直接启动服务
            var serviceIntent = new Intent(_context, typeof(DnsVpnService));
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
            {
                _context.StartForegroundService(serviceIntent);
            }
            else
            {
                _context.StartService(serviceIntent);
            }
        }

        public void StopVpn()
        {
            EnsureInitialized();
            DnsVpnService.Instance?.StopVpn();
            _context.StopService(new Intent(_context, typeof(DnsVpnService)));
        }

        public bool IsVpnRunning => DnsVpnService.Instance?.IsRunning ?? false;

        public DnsStats GetStats() => _dnsServer?.GetStats();

        public System.Collections.Generic.List<DnsQueryLog> GetQueryLog(int limit = 100) => _dnsServer?.GetQueryLog(limit);

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonConvert.DeserializeObject<DnsServerConfig>(json) ?? new DnsServerConfig();
                    // 旧默认上游（8.8.8.8/1.1.1.1）在多数国内网络会超时，导致 DNS 看起来“卡死”。
                    // 仅在用户从未改过时迁移，尊重任何自定义上游。
                    if (_config.UpstreamServers != null &&
                        _config.UpstreamServers.Count == 2 &&
                        _config.UpstreamServers[0] == "8.8.8.8" &&
                        _config.UpstreamServers[1] == "1.1.1.1")
                    {
                        _config.UpstreamServers = new List<string> { "223.5.5.5", "119.29.29.29" };
                        SaveConfig();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"加载 DNS 配置失败: {ex.Message}");
            }
        }

        private void SaveConfig()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                Log($"保存 DNS 配置失败: {ex.Message}");
            }
        }

        private void Log(string message)
        {
            Android.Util.Log.Info("DnsManager", message);
        }
    }
}