using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Cafe24Auth
{
    public class MainForm : Form
    {
        // ──────────────── 상수 ────────────────
        private static readonly string KEY_DIR       = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "key");
        private static readonly string SETTINGS_PATH = Path.Combine(KEY_DIR, "cafe24_settings.json");

        // 토큰 경로: mall_id 포함
        private string TokenPath(string mallId)
            => Path.Combine(KEY_DIR, $"cafe24_token_{mallId}.json");

        // ──────────────── 컨트롤 ────────────────
        private ComboBox txtMallId;
        private TextBox txtClientId, txtClientSecret, txtRedirectUri, txtLocalPort;
        private TextBox txtAuthScope;
        private TextBox txtAccessToken, txtRefreshToken, txtExpiry, txtScope;
        private Button  btnStartAuth, btnRefreshToken, btnLoadToken, btnSaveSettings, btnCopyToken;
        private Button  btnTunnel;
        private Label   lblTunnelStatus;
        private RichTextBox rtbLog;
        private Label   lblStatus;

        // ──────────────── 런타임 ────────────────
        private TcpListener?             _tcpListener;
        private CancellationTokenSource? _cts;
        private Process?                 _tunnelProcess;
        private bool                     _isAuthRunning;
        private bool                     _preferLocalRedirect;
        private readonly List<string>    _savedMallIds = new();
        private System.Windows.Forms.Timer _autoRefreshTimer = new();
        private Label   lblAutoRefresh = new();
        private bool    _reAuthWarningShown;
        private bool    _isAutoRefreshing;

        // ──────────────── DTO ────────────────
        private class Cafe24Token
        {
            public string   MallId        { get; set; } = "";
            public string   ClientId      { get; set; } = "";
            public string   ClientSecret  { get; set; } = "";
            public string   AccessToken   { get; set; } = "";
            public string   RefreshToken  { get; set; } = "";
            public string   RedirectUri   { get; set; } = "";
            public string   ApiVersion    { get; set; } = "2025-12-01";
            public string   ShopNo        { get; set; } = "1";
            public string   Scope                  { get; set; } = "";
            public DateTime UpdatedAt              { get; set; }
            public DateTime RefreshTokenUpdatedAt  { get; set; }
        }

        private class MallConfig
        {
            public string client_id     { get; set; } = "";
            public string client_secret { get; set; } = "";
            public string scope         { get; set; } = "";
            public string redirect_uri  { get; set; } = "";
        }

        private class AppSettings
        {
            public string mall_id       { get; set; } = "";
            public List<string> mall_ids { get; set; } = new();
            public string client_id     { get; set; } = "";
            public string client_secret { get; set; } = "";
            public string redirect_uri  { get; set; } = "";
            public string local_port    { get; set; } = "5000";
            public string scope         { get; set; } = "";
            public Dictionary<string, MallConfig> mall_configs { get; set; } = new();
        }

        // ══════════════════════════════════════════
        public MainForm()
        {
            InitializeUI();
            LoadSettings();
            LoadToken();

            _autoRefreshTimer.Interval = 60_000; // 1분마다 체크
            _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
            _autoRefreshTimer.Start();
        }

        // ══════════════════════════════════════════
        #region UI 초기화
        private void InitializeUI()
        {
            Text            = "Cafe24 API 인증 관리자";
            Size            = new Size(720, 710);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox     = false;
            BackColor       = Color.FromArgb(245, 247, 250);

            int y = 15;

            // ── 앱 설정 그룹 ──
            var grpSettings = MakeGroup("⚙  앱 설정", 10, y, 690, 305);
            Controls.Add(grpSettings);

            int gy = 28;
            AddComboRow(grpSettings, ref gy, "Mall ID:", out txtMallId, "예: myshop  (myshop.cafe24.com)");
            txtMallId.Leave += (s, e) => HandleMallIdEdited();
            txtMallId.SelectionChangeCommitted += (s, e) => HandleMallIdEdited(loadToken: true);
            AddRow(grpSettings, ref gy, "Client ID:",     out txtClientId,     "Cafe24 앱 관리 > Client ID");
            AddRow(grpSettings, ref gy, "Client Secret:", out txtClientSecret, "", password: true);
            AddRow(grpSettings, ref gy, "Redirect URI:",  out txtRedirectUri,  "https://<public-domain>/callback");
            // Scope 행 (버튼 포함)
            grpSettings.Controls.Add(new Label { Text = "API Scope:", Left = 12, Top = gy + 3, AutoSize = true });
            txtAuthScope = new TextBox { Left = 140, Top = gy, Width = 370, PlaceholderText = "아래 '선택' 버튼으로 설정하세요" };
            grpSettings.Controls.Add(txtAuthScope);
            var btnScopePicker = MakeButton("☑ 권한 선택", 518, gy - 1, 100, 28, Color.FromArgb(99, 102, 241));
            btnScopePicker.Click += (s, e) =>
            {
                var dlg = new ScopePickerForm(txtAuthScope.Text);
                if (dlg.ShowDialog() == DialogResult.OK)
                    txtAuthScope.Text = dlg.SelectedScope;
            };
            grpSettings.Controls.Add(btnScopePicker);
            gy += 33;

            // 포트 + ngrok 버튼 행
            grpSettings.Controls.Add(new Label { Text = "로컬 포트:", Left = 12, Top = gy + 3, AutoSize = true });
            txtLocalPort = new TextBox { Left = 140, Top = gy, Width = 70, Text = "5000" };
            txtLocalPort.Leave += (s, e) => ApplyPreferredRedirectUri(force: _preferLocalRedirect);
            grpSettings.Controls.Add(txtLocalPort);

            btnTunnel = MakeButton("▶ ngrok 실행", 220, gy - 1, 175, 28, Color.FromArgb(22, 163, 74));
            btnTunnel.Click += BtnTunnel_Click;
            grpSettings.Controls.Add(btnTunnel);

            lblTunnelStatus = new Label
            {
                Left = 405, Top = gy + 5, Width = 260, AutoSize = false,
                Text = "고정 ngrok Redirect URI 필요", ForeColor = Color.Gray,
                Font = new Font("맑은 고딕", 8.5f)
            };
            grpSettings.Controls.Add(lblTunnelStatus);

            gy += 38;

            // cloudflare 안내 라벨
            var lblNote = new Label
            {
                Text      = "① Redirect URI에는 고정 ngrok 주소를 넣으세요. 예: https://my-shop.ngrok.app/callback\n" +
                            "② 버튼을 누르면 같은 주소로 ngrok 터널을 열고 Cafe24 Developers 설정과 1:1로 맞춥니다.",
                Left = 12, Top = gy, Width = 650, Height = 36,
                ForeColor = Color.DimGray, Font = new Font("맑은 고딕", 8.5f)
            };
            grpSettings.Controls.Add(lblNote);

            gy += 45;
            btnSaveSettings = MakeButton("💾 설정 저장", 580, gy - 83, 95, 30, Color.SteelBlue);
            btnSaveSettings.Click += BtnSaveSettings_Click;
            grpSettings.Controls.Add(btnSaveSettings);

            y += 315;

            // ── 토큰 정보 그룹 ──
            var grpToken = MakeGroup("🔑  토큰 정보", 10, y, 690, 155);
            Controls.Add(grpToken);

            gy = 28;
            grpToken.Controls.Add(new Label { Text = "Access Token:", Left = 12, Top = gy + 3, AutoSize = true });
            txtAccessToken = new TextBox { Left = 140, Top = gy, Width = 510, ReadOnly = true, BackColor = Color.FromArgb(240, 240, 240) };
            grpToken.Controls.Add(txtAccessToken);
            gy += 32;

            grpToken.Controls.Add(new Label { Text = "Refresh Token:", Left = 12, Top = gy + 3, AutoSize = true });
            txtRefreshToken = new TextBox { Left = 140, Top = gy, Width = 510, ReadOnly = true, BackColor = Color.FromArgb(240, 240, 240) };
            grpToken.Controls.Add(txtRefreshToken);
            gy += 32;

            grpToken.Controls.Add(new Label { Text = "만료 시각:", Left = 12, Top = gy + 3, AutoSize = true });
            txtExpiry = new TextBox { Left = 140, Top = gy, Width = 200, ReadOnly = true, BackColor = Color.FromArgb(240, 240, 240) };
            grpToken.Controls.Add(txtExpiry);

            grpToken.Controls.Add(new Label { Text = "Scope:", Left = 355, Top = gy + 3, AutoSize = true });
            txtScope = new TextBox { Left = 405, Top = gy, Width = 245, ReadOnly = true, BackColor = Color.FromArgb(240, 240, 240) };
            grpToken.Controls.Add(txtScope);

            y += 165;

            // ── 액션 버튼 ──
            btnStartAuth = MakeButton("🔐 재인증", 10, y, 175, 38, Color.FromArgb(37, 99, 235));
            btnStartAuth.Click += BtnStartAuth_Click;
            Controls.Add(btnStartAuth);

            btnRefreshToken = MakeButton("🔄 토큰 갱신", 195, y, 135, 38, Color.FromArgb(5, 150, 105));
            btnRefreshToken.Click += BtnRefreshToken_Click;
            Controls.Add(btnRefreshToken);

            btnLoadToken = MakeButton("📂 파일 불러오기", 340, y, 140, 38, Color.FromArgb(100, 116, 139));
            btnLoadToken.Click += (s, e) => LoadToken();
            Controls.Add(btnLoadToken);

            btnCopyToken = MakeButton("📋 토큰 복사", 490, y, 120, 38, Color.FromArgb(124, 58, 237));
            btnCopyToken.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(txtAccessToken.Text))
                {
                    Clipboard.SetText(txtAccessToken.Text);
                    Log("Access Token 클립보드 복사 완료");
                }
            };
            Controls.Add(btnCopyToken);

            lblStatus = new Label
            {
                Left = 620, Top = y + 10, Width = 80, AutoSize = false,
                Text = "대기 중", ForeColor = Color.Gray,
                Font = new Font("맑은 고딕", 9f, FontStyle.Bold)
            };
            Controls.Add(lblStatus);

            y += 44;

            // ── 자동갱신 상태 라벨 ──
            lblAutoRefresh = new Label
            {
                Left = 10, Top = y, Width = 690, Height = 22, AutoSize = false,
                Text = "🔄 자동갱신 대기 중...", ForeColor = Color.DimGray,
                Font = new Font("맑은 고딕", 9f)
            };
            Controls.Add(lblAutoRefresh);

            y += 26;

            // ── 로그 ──
            var grpLog = MakeGroup("📋  로그", 10, y, 690, 120);
            Controls.Add(grpLog);
            rtbLog = new RichTextBox
            {
                Left = 6, Top = 22, Width = 672, Height = 88,
                ReadOnly = true, BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(134, 239, 172),
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.None
            };
            grpLog.Controls.Add(rtbLog);
        }

        private void AddRow(GroupBox grp, ref int y, string label, out TextBox tb, string placeholder, bool password = false)
        {
            grp.Controls.Add(new Label { Text = label, Left = 12, Top = y + 3, AutoSize = true });
            tb = new TextBox { Left = 140, Top = y, Width = 510, PlaceholderText = placeholder };
            if (password) tb.UseSystemPasswordChar = true;
            grp.Controls.Add(tb);
            y += 33;
        }

        private void AddComboRow(GroupBox grp, ref int y, string label, out ComboBox cb, string placeholder)
        {
            grp.Controls.Add(new Label { Text = label, Left = 12, Top = y + 3, AutoSize = true });
            var combo = new ComboBox
            {
                Left = 140,
                Top = y,
                Width = 510,
                DropDownStyle = ComboBoxStyle.DropDown
            };
            combo.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            combo.AutoCompleteSource = AutoCompleteSource.ListItems;
            grp.Controls.Add(combo);

            var hint = new Label
            {
                Left = 140,
                Top = y + 4,
                Width = 300,
                Height = combo.Height,
                Text = placeholder,
                ForeColor = Color.Gray,
                BackColor = Color.Transparent,
                Enabled = false
            };
            hint.Click += (s, e) => combo.Focus();
            combo.TextChanged += (s, e) => hint.Visible = string.IsNullOrWhiteSpace(combo.Text);
            combo.GotFocus += (s, e) => hint.Visible = false;
            combo.LostFocus += (s, e) => hint.Visible = string.IsNullOrWhiteSpace(combo.Text);
            grp.Controls.Add(hint);
            hint.BringToFront();
            combo.BringToFront();

            cb = combo;

            y += 33;
        }

        private static GroupBox MakeGroup(string text, int x, int y, int w, int h)
            => new GroupBox { Text = text, Left = x, Top = y, Width = w, Height = h, Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold) };

        private static Button MakeButton(string text, int x, int y, int w, int h, Color bg)
        {
            var btn = new Button
            {
                Text = text, Left = x, Top = y, Width = w, Height = h,
                BackColor = bg, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }
        #endregion

        // ══════════════════════════════════════════
        #region 유틸
        private void Log(string msg, Color? color = null)
        {
            if (rtbLog.InvokeRequired) { rtbLog.Invoke(() => Log(msg, color)); return; }
            rtbLog.SelectionStart  = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor  = color ?? Color.FromArgb(134, 239, 172);
            rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            rtbLog.ScrollToCaret();
        }

        private void SetStatus(string text, Color color)
        {
            if (lblStatus.InvokeRequired) { lblStatus.Invoke(() => SetStatus(text, color)); return; }
            lblStatus.Text      = text;
            lblStatus.ForeColor = color;
        }

        private void SetTunnelStatus(string text, Color color)
        {
            if (lblTunnelStatus.InvokeRequired) { lblTunnelStatus.Invoke(() => SetTunnelStatus(text, color)); return; }
            lblTunnelStatus.Text      = text;
            lblTunnelStatus.ForeColor = color;
        }

        private string BuildLocalRedirectUri()
        {
            var port = string.IsNullOrWhiteSpace(txtLocalPort.Text) ? "5000" : txtLocalPort.Text.Trim();
            return $"http://localhost:{port}/callback";
        }

        private void ApplyPreferredRedirectUri(bool force)
        {
            if (!_preferLocalRedirect && !force)
                return;

            txtRedirectUri.Text = BuildLocalRedirectUri();
            if (_preferLocalRedirect)
                SetTunnelStatus("localhost 테스트 모드", Color.Gray);
        }

        private static bool IsLoopbackRedirectUri(string? value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsLoopback;
        }

        private static List<string> BuildListenerPrefixes(Uri redirectUriObj, int fallbackPort)
        {
            var callbackPath = redirectUriObj.AbsolutePath.TrimEnd('/') + "/";
            var port = redirectUriObj.IsLoopback && !redirectUriObj.IsDefaultPort
                ? redirectUriObj.Port
                : fallbackPort;

            if (redirectUriObj.IsLoopback)
            {
                return new List<string>
                {
                    $"http://localhost:{port}{callbackPath}",
                    $"http://127.0.0.1:{port}{callbackPath}"
                };
            }

            return new List<string>
            {
                $"http://localhost:{port}{callbackPath}",
                $"http://127.0.0.1:{port}{callbackPath}"
            };
        }
        #endregion

        // ══════════════════════════════════════════
        #region ngrok Tunnel
        private void BtnTunnel_Click(object? sender, EventArgs e)
        {
            if (_tunnelProcess != null && !_tunnelProcess.HasExited)
            {
                _tunnelProcess.Kill();
                _tunnelProcess = null;
                _preferLocalRedirect = false;
                RestoreConfiguredRedirectUri();
                btnTunnel.Text      = "▶ ngrok 실행";
                btnTunnel.BackColor = Color.FromArgb(22, 163, 74);
                SetTunnelStatus("고정 ngrok Redirect URI 필요", Color.Gray);
                Log("ngrok 터널 종료");
                return;
            }

            string port = txtLocalPort.Text.Trim();
            string redirectUri = txtRedirectUri.Text.Trim();

            if (!TryBuildNgrokPublicUrl(redirectUri, out var publicUrl))
            {
                MessageBox.Show(
                    "Redirect URI에 고정 ngrok HTTPS 주소를 먼저 입력하세요.\n\n" +
                    "예: https://my-shop.ngrok.app/callback",
                    "ngrok 주소 필요", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string? ngrokExe = FindNgrok();
            if (ngrokExe == null)
            {
                MessageBox.Show(
                    "ngrok.exe를 찾을 수 없습니다.\n\n" +
                    "아래 방법 중 하나로 설치하세요:\n" +
                    "  • winget install ngrok.ngrok\n" +
                    "  • 또는 ngrok.exe를 이 프로그램과 같은 폴더에 두세요.\n" +
                    "  다운로드: https://ngrok.com/download",
                    "ngrok 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                StopExistingLocalNgrokProcesses();

                _tunnelProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName               = ngrokExe,
                        Arguments              = $"http {port} --url \"{publicUrl}\" --host-header=localhost",
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true
                    },
                    EnableRaisingEvents = true
                };

                _tunnelProcess.OutputDataReceived += (s, ev) =>
                {
                    if (string.IsNullOrEmpty(ev.Data)) return;
                    if (ev.Data.Contains("started tunnel", StringComparison.OrdinalIgnoreCase) ||
                        ev.Data.Contains(publicUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        this.Invoke(() => SetTunnelStatus("연결됨 ✓", Color.DarkGreen));
                    }
                    if (ev.Data.Contains("ERR_NGROK", StringComparison.OrdinalIgnoreCase) ||
                        ev.Data.Contains("failed", StringComparison.OrdinalIgnoreCase))
                    {
                        this.Invoke(() => HandleNgrokError(ev.Data));
                    }
                };
                _tunnelProcess.ErrorDataReceived += (s, ev) =>
                {
                    if (string.IsNullOrEmpty(ev.Data)) return;
                    this.Invoke(() =>
                    {
                        if (ev.Data.Contains("ERR_NGROK", StringComparison.OrdinalIgnoreCase) ||
                            ev.Data.Contains("failed", StringComparison.OrdinalIgnoreCase))
                            HandleNgrokError(ev.Data);
                        else
                            Log($"ngrok: {ev.Data}", Color.DimGray);
                    });
                };
                _tunnelProcess.Exited += (s, ev) =>
                {
                    this.Invoke(() =>
                    {
                        btnTunnel.Text      = "▶ ngrok 실행";
                        btnTunnel.BackColor = Color.FromArgb(22, 163, 74);
                        _preferLocalRedirect = false;
                        RestoreConfiguredRedirectUri();
                        SetTunnelStatus("고정 ngrok Redirect URI 필요", Color.Gray);
                    });
                };

                _tunnelProcess.Start();
                _tunnelProcess.BeginErrorReadLine();
                _tunnelProcess.BeginOutputReadLine();

                btnTunnel.Text      = "■ ngrok 중지";
                btnTunnel.BackColor = Color.FromArgb(220, 38, 38);
                SetTunnelStatus("터널 시작 중...", Color.Orange);
                Log($"ngrok 고정 주소 시작: {publicUrl}", Color.Cyan);
                Log($"Redirect URI 확인: {redirectUri}", Color.Yellow);
            }
            catch (Exception ex)
            {
                Log($"ngrok 실행 오류: {ex.Message}", Color.Red);
            }
        }

        private string? FindNgrok()
        {
            // 1) PATH에서 찾기
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                var p = Path.Combine(dir.Trim(), "ngrok.exe");
                if (File.Exists(p)) return p;
            }
            // 2) 현재 실행 파일 폴더
            var appDir = Path.GetDirectoryName(Application.ExecutablePath)!;
            var local  = Path.Combine(appDir, "ngrok.exe");
            if (File.Exists(local)) return local;
            // 3) Desktop
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ngrok.exe");
            if (File.Exists(desktop)) return desktop;
            // 4) winget 기본 설치 경로
            var winget = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WinGet\Links\ngrok.exe");
            if (File.Exists(winget)) return winget;

            return null;
        }

        private void StopExistingLocalNgrokProcesses()
        {
            foreach (var process in Process.GetProcessesByName("ngrok"))
            {
                if (_tunnelProcess != null && process.Id == _tunnelProcess.Id)
                    continue;

                try
                {
                    Log($"기존 ngrok 프로세스 종료: PID {process.Id}", Color.Orange);
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Log($"기존 ngrok 종료 실패 (PID {process.Id}): {ex.Message}", Color.Red);
                }
            }
        }

        private static bool TryBuildNgrokPublicUrl(string redirectUri, out string publicUrl)
        {
            publicUrl = string.Empty;

            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
                return false;

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;

            if (uri.IsLoopback)
                return false;

            var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port)
            {
                Path = string.Empty
            };

            publicUrl = builder.Uri.ToString().TrimEnd('/');
            return true;
        }

        private async Task<bool> WaitForNgrokTunnelReadyAsync(string redirectUri)
        {
            if (IsLoopbackRedirectUri(redirectUri))
                return true;

            if (!TryBuildNgrokPublicUrl(redirectUri, out var expectedPublicUrl))
                return false;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    var json = await client.GetStringAsync("http://127.0.0.1:4040/api/tunnels");
                    if (json.Contains($"\"public_url\":\"{expectedPublicUrl}\"", StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"ngrok 터널 준비 완료: {expectedPublicUrl}", Color.Cyan);
                        return true;
                    }
                }
                catch
                {
                }

                await Task.Delay(500);
            }

            Log($"ngrok 터널 준비 확인 실패: {expectedPublicUrl}", Color.Orange);
            return false;
        }

        private void HandleNgrokError(string data)
        {
            Log($"ngrok 오류: {data}", Color.Red);

            string hint;
            if (data.Contains("ERR_NGROK_108"))
                hint = "터널 1개 초과 오류(ERR_NGROK_108)\n\n무료 플랜은 동시에 터널 1개만 허용됩니다.\n다른 ngrok 터널(다른 앱 등)을 모두 종료한 뒤 다시 시도하세요.";
            else if (data.Contains("ERR_NGROK_302") || data.Contains("ERR_NGROK_105"))
                hint = "ngrok 인증 오류\n\nngrok 계정 로그인 후 아래 명령을 실행하세요:\n  ngrok config add-authtoken <YOUR_TOKEN>\n\n토큰은 https://dashboard.ngrok.com/get-started/your-authtoken 에서 확인";
            else if (data.Contains("ERR_NGROK_1006") || data.Contains("ERR_NGROK_3004"))
                hint = "Static Domain 오류\n\nRedirect URI에 입력한 ngrok 주소가 등록되지 않았습니다.\nhttps://dashboard.ngrok.com/domains 에서 무료 도메인을 생성하세요.";
            else
                hint = $"ngrok 오류 발생:\n{data}\n\nngrok 로그를 확인하세요.";

            SetTunnelStatus("ngrok 오류 ✗", Color.Red);
            MessageBox.Show(hint, "ngrok 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        #endregion

        // ══════════════════════════════════════════
        #region 설정 저장/불러오기
        private void HandleMallIdEdited(bool loadToken = true)
        {
            string mallId = txtMallId.Text.Trim();
            if (string.IsNullOrEmpty(mallId))
                return;

            RememberMallId(mallId, persist: true);
            LoadMallConfig(mallId);
            if (loadToken)
                LoadToken();
        }

        private void LoadMallConfig(string mallId)
        {
            if (!File.Exists(SETTINGS_PATH)) return;
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SETTINGS_PATH));
                if (s?.mall_configs != null && s.mall_configs.TryGetValue(mallId, out var cfg))
                {
                    if (!string.IsNullOrEmpty(cfg.client_id))     txtClientId.Text     = cfg.client_id;
                    if (!string.IsNullOrEmpty(cfg.client_secret)) txtClientSecret.Text = cfg.client_secret;
                    if (!string.IsNullOrEmpty(cfg.scope))         txtAuthScope.Text    = cfg.scope;
                    if (!string.IsNullOrEmpty(cfg.redirect_uri) && !_preferLocalRedirect)
                        txtRedirectUri.Text = cfg.redirect_uri;
                    Log($"몰 설정 로드: {mallId}");
                }
            }
            catch { }
        }

        private void RememberMallId(string? mallId, bool persist)
        {
            mallId = mallId?.Trim();
            if (string.IsNullOrEmpty(mallId))
                return;

            foreach (var existingMallId in _savedMallIds)
            {
                if (string.Equals(existingMallId, mallId, StringComparison.OrdinalIgnoreCase))
                {
                    RefreshMallIdItems(existingMallId);
                    if (persist)
                        SaveSettings(showMessage: false);
                    return;
                }
            }

            _savedMallIds.Add(mallId);
            _savedMallIds.Sort(StringComparer.OrdinalIgnoreCase);
            RefreshMallIdItems(mallId);

            if (persist)
                SaveSettings(showMessage: false);
        }

        private void RefreshMallIdItems(string? selectedMallId = null)
        {
            string currentText = string.IsNullOrWhiteSpace(selectedMallId) ? txtMallId.Text : selectedMallId;

            txtMallId.BeginUpdate();
            txtMallId.Items.Clear();
            foreach (var mallId in _savedMallIds)
                txtMallId.Items.Add(mallId);
            txtMallId.EndUpdate();

            if (!string.IsNullOrWhiteSpace(currentText))
                txtMallId.Text = currentText;
        }

        private AppSettings BuildCurrentSettings()
        {
            string currentMallId = txtMallId.Text.Trim();
            RememberMallId(currentMallId, persist: false);

            // 기존 설정 파일의 mall_configs 불러와서 현재 몰 정보 업데이트
            Dictionary<string, MallConfig> mallConfigs = new();
            if (File.Exists(SETTINGS_PATH))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SETTINGS_PATH));
                    if (existing?.mall_configs != null)
                        mallConfigs = existing.mall_configs;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(currentMallId))
            {
                mallConfigs[currentMallId] = new MallConfig
                {
                    client_id     = txtClientId.Text.Trim(),
                    client_secret = txtClientSecret.Text.Trim(),
                    scope         = txtAuthScope.Text.Trim(),
                    redirect_uri  = txtRedirectUri.Text.Trim()
                };
            }

            return new AppSettings
            {
                mall_id      = currentMallId,
                mall_ids     = new List<string>(_savedMallIds),
                client_id    = txtClientId.Text.Trim(),
                client_secret = txtClientSecret.Text.Trim(),
                redirect_uri = txtRedirectUri.Text.Trim(),
                local_port   = txtLocalPort.Text.Trim(),
                scope        = txtAuthScope.Text.Trim(),
                mall_configs = mallConfigs
            };
        }

        private void SaveSettings(bool showMessage)
        {
            var settings = BuildCurrentSettings();
            Directory.CreateDirectory(KEY_DIR);
            File.WriteAllText(SETTINGS_PATH, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            Log("설정 저장 완료");

            if (showMessage)
                MessageBox.Show("설정이 저장되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void LoadSettings()
        {
            if (!File.Exists(SETTINGS_PATH))
            {
                return;
            }
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SETTINGS_PATH))!;
                _savedMallIds.Clear();
                if (s.mall_ids != null)
                {
                    foreach (var mallId in s.mall_ids)
                        RememberMallId(mallId, persist: false);
                }
                RememberMallId(s.mall_id, persist: false);
                txtMallId.Text       = s.mall_id;
                txtClientId.Text     = s.client_id;
                txtClientSecret.Text = s.client_secret;
                txtRedirectUri.Text  = s.redirect_uri;
                txtLocalPort.Text    = s.local_port;
                txtAuthScope.Text    = s.scope;
                _preferLocalRedirect = false;
                Log("설정 파일 로드 완료");
            }
            catch (Exception ex) { Log($"설정 로드 오류: {ex.Message}", Color.Orange); }
        }

        private void BtnSaveSettings_Click(object? sender, EventArgs e)
        {
            SaveSettings(showMessage: true);
        }
        #endregion

        // ══════════════════════════════════════════
        #region 토큰 표시/저장/불러오기
        private void LoadToken()
        {
            string mallId = txtMallId.Text.Trim();
            if (string.IsNullOrEmpty(mallId)) { Log("Mall ID를 먼저 입력하세요."); return; }

            string path = TokenPath(mallId);
            if (!File.Exists(path)) { Log($"토큰 파일 없음: {path}"); return; }
            try
            {
                var t = JsonSerializer.Deserialize<Cafe24Token>(File.ReadAllText(path))!;
                RememberMallId(mallId, persist: false);
                ApplyTokenSettings(t);
                DisplayToken(t);
                Log($"토큰 파일 로드: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { Log($"토큰 로드 오류: {ex.Message}", Color.Red); }
        }

        private void ApplyTokenSettings(Cafe24Token token)
        {
            if (InvokeRequired) { Invoke(() => ApplyTokenSettings(token)); return; }

            if (!string.IsNullOrWhiteSpace(token.MallId))
            {
                txtMallId.Text = token.MallId;
                RememberMallId(token.MallId, persist: false);
            }
            if (!string.IsNullOrWhiteSpace(token.ClientId))
                txtClientId.Text = token.ClientId;
            if (!string.IsNullOrWhiteSpace(token.ClientSecret))
                txtClientSecret.Text = token.ClientSecret;
            if (!_preferLocalRedirect && !string.IsNullOrWhiteSpace(token.RedirectUri))
                txtRedirectUri.Text = token.RedirectUri;
            if (!string.IsNullOrWhiteSpace(token.Scope))
                txtAuthScope.Text = token.Scope;

            if (_preferLocalRedirect)
                ApplyPreferredRedirectUri(force: true);
        }

        private void RestoreConfiguredRedirectUri()
        {
            if (InvokeRequired) { Invoke(() => RestoreConfiguredRedirectUri()); return; }

            string mallId = txtMallId.Text.Trim();
            if (!string.IsNullOrEmpty(mallId))
            {
                string tokenPath = TokenPath(mallId);
                if (File.Exists(tokenPath))
                {
                    try
                    {
                        var token = JsonSerializer.Deserialize<Cafe24Token>(File.ReadAllText(tokenPath));
                        if (token != null && !string.IsNullOrWhiteSpace(token.RedirectUri))
                        {
                            txtRedirectUri.Text = token.RedirectUri;
                            return;
                        }
                    }
                    catch { }
                }
            }

            if (File.Exists(SETTINGS_PATH))
            {
                try
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SETTINGS_PATH));
                    if (settings != null && !string.IsNullOrWhiteSpace(settings.redirect_uri))
                    {
                        txtRedirectUri.Text = settings.redirect_uri;
                        return;
                    }
                }
                catch { }
            }
        }

        private void DisplayToken(Cafe24Token token)
        {
            if (InvokeRequired) { Invoke(() => DisplayToken(token)); return; }

            txtAccessToken.Text  = token.AccessToken;
            txtRefreshToken.Text = token.RefreshToken;
            txtScope.Text        = token.Scope;

            var expiry = token.UpdatedAt.AddSeconds(7200);
            txtExpiry.Text = $"만료: {expiry:yyyy-MM-dd HH:mm:ss}";

            if (DateTime.Now > expiry)
            {
                txtExpiry.ForeColor = Color.Red;
                SetStatus("만료됨", Color.Red);
            }
            else
            {
                txtExpiry.ForeColor = Color.DarkGreen;
                var remain = expiry - DateTime.Now;
                SetStatus($"유효 {(int)remain.TotalHours}h", Color.DarkGreen);
            }
        }

        private void SaveToken(Cafe24Token token)
        {
            string path = TokenPath(token.MallId);
            Directory.CreateDirectory(KEY_DIR);
            File.WriteAllText(path, JsonSerializer.Serialize(token, new JsonSerializerOptions { WriteIndented = true }));
            RememberMallId(token.MallId, persist: true);
            Log($"토큰 저장 → {Path.GetFileName(path)}");
        }
        #endregion

        // ══════════════════════════════════════════
        #region OAuth 인증
        private async void BtnStartAuth_Click(object? sender, EventArgs e)
        {
            if (_isAuthRunning)
            {
                MessageBox.Show("이미 인증이 진행 중입니다. 브라우저에서 로그인과 권한 승인을 완료할 때까지 기다리세요.",
                    "인증 진행 중", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtMallId.Text) ||
                string.IsNullOrWhiteSpace(txtClientId.Text) ||
                string.IsNullOrWhiteSpace(txtClientSecret.Text) ||
                string.IsNullOrWhiteSpace(txtRedirectUri.Text))
            {
                MessageBox.Show("Mall ID, Client ID, Client Secret, Redirect URI를 모두 입력하세요.", "입력 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var redirectUri = txtRedirectUri.Text.Trim();
            if (!IsLoopbackRedirectUri(redirectUri) && !await WaitForNgrokTunnelReadyAsync(redirectUri))
            {
                MessageBox.Show("ngrok 고정 주소가 아직 준비되지 않았습니다. 'ngrok 실행' 후 상태가 연결됨인지 확인하고 다시 시도하세요.",
                    "ngrok 준비 필요", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetAuthRunning(true);
            SetStatus("인증 중...", Color.Orange);
            try { await StartOAuthFlow(); }
            finally { SetAuthRunning(false); }
        }

        private void SetAuthRunning(bool running)
        {
            _isAuthRunning = running;
            if (running)
            {
                btnStartAuth.Text      = "승인 대기 중...";
                btnStartAuth.BackColor = Color.FromArgb(37, 99, 235);
            }
            else
            {
                btnStartAuth.Text      = "🔐 재인증";
                btnStartAuth.BackColor = Color.FromArgb(37, 99, 235);
            }

            btnStartAuth.Enabled     = !running;
            btnTunnel.Enabled        = !running;
            btnRefreshToken.Enabled  = !running;
            btnSaveSettings.Enabled  = !running;
            btnLoadToken.Enabled     = !running;
        }

        private async Task StartOAuthFlow()
        {
            string mallId       = txtMallId.Text.Trim();
            string clientId     = txtClientId.Text.Trim();
            string clientSecret = txtClientSecret.Text.Trim();
            string redirectUri  = txtRedirectUri.Text.Trim();
            int    port         = int.Parse(txtLocalPort.Text.Trim());

            var redirectUriObj = new Uri(redirectUri);
            var callbackPath = string.IsNullOrWhiteSpace(redirectUriObj.AbsolutePath)
                ? "/"
                : redirectUriObj.AbsolutePath.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(callbackPath))
                callbackPath = "/";

            var callbackUrls = new List<string>
            {
                $"http://localhost:{port}{callbackPath}",
                $"http://127.0.0.1:{port}{callbackPath}"
            };

            _cts = new CancellationTokenSource();
            _tcpListener = new TcpListener(IPAddress.IPv6Any, port);
            _tcpListener.Server.DualMode = true;

            try
            {
                _tcpListener.Start();
                Log($"로컬 서버 시작: {string.Join(", ", callbackUrls)}", Color.Cyan);
            }
            catch (Exception ex)
            {
                Log($"서버 시작 실패 (포트 {port} 충돌?): {ex.Message}", Color.Red);
                MessageBox.Show(
                    $"로컬 HTTP 서버를 시작할 수 없습니다.\nRedirect URI와 포트 설정을 확인하세요.\n\n{ex.Message}",
                    "서버 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("오류", Color.Red);
                return;
            }

            string scope = txtAuthScope.Text.Trim();
            if (string.IsNullOrEmpty(scope))
            {
                MessageBox.Show("API Scope를 입력하세요.\n\nCafe24 개발자센터 → 앱 수정 → STEP 03에서 등록된 권한을 확인하세요.\n예: mall.read_product,mall.write_product", "Scope 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string state   = Guid.NewGuid().ToString("N");
            string authUrl = $"https://{mallId}.cafe24api.com/api/v2/oauth/authorize" +
                             $"?response_type=code" +
                             $"&client_id={Uri.EscapeDataString(clientId)}" +
                             $"&state={Uri.EscapeDataString(state)}" +
                             $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                             $"&scope={Uri.EscapeDataString(scope)}";

            Log($"[DEBUG] redirect_uri 전송값: {redirectUri}", Color.Yellow);
            Log($"[DEBUG] 전체 auth URL: {authUrl}", Color.Yellow);
            Log($"Cafe24 개발자센터 Redirect URL도 {redirectUri} 로 등록되어 있어야 합니다.", Color.Yellow);
            Log("브라우저에서 Cafe24 인증 페이지 열기...");
            Process.Start(new ProcessStartInfo { FileName = authUrl, UseShellExecute = true });
            Log("브라우저에서 로그인 후 앱 권한을 승인하세요. (대기 중...)");

            try
            {
                var query = await WaitForCallbackQueryAsync(_tcpListener, callbackPath, _cts.Token);

                query.TryGetValue("error", out var errorParam);
                query.TryGetValue("error_description", out var errorDescription);
                if (!string.IsNullOrEmpty(errorParam))
                {
                    Log($"인증 거부/오류: {errorParam} - {errorDescription}", Color.Red);
                    SetStatus("인증 실패", Color.Red);
                    return;
                }

                query.TryGetValue("code", out var code);
                query.TryGetValue("state", out var returnedState);

                if (string.IsNullOrEmpty(code))
                {
                    Log("인증 코드가 없습니다.", Color.Red);
                    SetStatus("실패", Color.Red);
                    return;
                }

                if (returnedState != state)
                {
                    Log("State 불일치 - CSRF 위험, 재시도하세요.", Color.Red);
                    SetStatus("보안 오류", Color.Red);
                    return;
                }

                Log("인증 코드 수신 완료. 토큰 교환 중...");
                await ExchangeCodeForToken(mallId, clientId, clientSecret, code, redirectUri, scope);
            }
            catch (OperationCanceledException)
            {
                Log("인증이 취소되었습니다.", Color.Orange);
                SetStatus("취소됨", Color.Gray);
            }
            catch (Exception ex)
            {
                Log($"콜백 처리 오류: {ex.Message}", Color.Red);
                SetStatus("오류", Color.Red);
            }
            finally
            {
                try { _tcpListener?.Stop(); }
                catch { }
                _tcpListener = null;
                Log("로컬 서버 종료");
            }

            async Task<Dictionary<string, string>> WaitForCallbackQueryAsync(TcpListener listener, string expectedPath, CancellationToken cancellationToken)
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true);

                    var requestLine = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(requestLine))
                    {
                        await WriteHttpResponseAsync(stream, 400, "Bad Request", "<html><meta charset='utf-8'><body><h3>잘못된 요청입니다.</h3></body></html>");
                        continue;
                    }

                    string? hostHeader = null;
                    while (true)
                    {
                        var headerLine = await reader.ReadLineAsync();
                        if (string.IsNullOrEmpty(headerLine))
                            break;

                        if (headerLine.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                            hostHeader = headerLine.Substring(5).Trim();
                    }

                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2)
                    {
                        await WriteHttpResponseAsync(stream, 400, "Bad Request", "<html><meta charset='utf-8'><body><h3>요청 형식이 올바르지 않습니다.</h3></body></html>");
                        continue;
                    }

                    var requestTarget = parts[1];
                    var requestUri = new Uri($"http://localhost{requestTarget}");
                    var requestPath = string.IsNullOrWhiteSpace(requestUri.AbsolutePath)
                        ? "/"
                        : requestUri.AbsolutePath.TrimEnd('/');
                    if (string.IsNullOrWhiteSpace(requestPath))
                        requestPath = "/";

                    Log($"콜백 요청 수신: {requestPath} | Host={hostHeader ?? "(없음)"}", Color.Cyan);

                    if (!string.Equals(requestPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteHttpResponseAsync(stream, 404, "Not Found", "<html><meta charset='utf-8'><body><h3>잘못된 콜백 경로입니다.</h3></body></html>");
                        continue;
                    }

                    const string successHtml = "<html><meta charset='utf-8'><body style='font-family:Arial;text-align:center;padding:60px'><h2 style='color:#16a34a'>✅ 인증 완료!</h2><p>이 창을 닫고 프로그램으로 돌아오세요.</p></body></html>";
                    await WriteHttpResponseAsync(stream, 200, "OK", successHtml);
                    return ParseQueryParameters(requestUri.Query);
                }
            }

            static Dictionary<string, string> ParseQueryParameters(string query)
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(query))
                    return result;

                foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var idx = pair.IndexOf('=');
                    var key = idx >= 0 ? pair.Substring(0, idx) : pair;
                    var value = idx >= 0 ? pair.Substring(idx + 1) : string.Empty;
                    result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
                }

                return result;
            }

            static async Task WriteHttpResponseAsync(NetworkStream stream, int statusCode, string statusText, string html)
            {
                var bodyBytes = Encoding.UTF8.GetBytes(html);
                var headers = $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                var headerBytes = Encoding.ASCII.GetBytes(headers);
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
                await stream.FlushAsync();
            }
        }
        private async Task ExchangeCodeForToken(string mallId, string clientId, string clientSecret, string code, string redirectUri, string scope)
        {
            string tokenUrl    = $"https://{mallId}.cafe24api.com/api/v2/oauth/token";
            string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Basic {credentials}");

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type",  "authorization_code" },
                { "code",         code },
                { "redirect_uri", redirectUri }
            });

            try
            {
                var resp = await client.PostAsync(tokenUrl, form);
                string body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    Log($"토큰 발급 실패 ({(int)resp.StatusCode}): {body}", Color.Red);
                    SetStatus("발급 실패", Color.Red);
                    MessageBox.Show($"토큰 발급 실패:\n{body}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var token = ParseToken(body, mallId, clientId, clientSecret, redirectUri, scope);
                SaveToken(token);
                DisplayToken(token);
                Log($"✅ 토큰 발급 완료! → cafe24_token_{mallId}.json", Color.Cyan);
                MessageBox.Show($"Cafe24 API 인증이 완료되었습니다!\n저장: cafe24_token_{mallId}.json", "인증 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log($"토큰 교환 오류: {ex.Message}", Color.Red);
                SetStatus("오류", Color.Red);
            }
        }
        #endregion

        // ══════════════════════════════════════════
        #region 토큰 갱신
        private async void BtnRefreshToken_Click(object? sender, EventArgs e)
        {
            string mallId = txtMallId.Text.Trim();
            if (string.IsNullOrEmpty(mallId))
            {
                MessageBox.Show("Mall ID를 입력하세요.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string path = TokenPath(mallId);
            if (!File.Exists(path))
            {
                MessageBox.Show($"토큰 파일이 없습니다:\n{path}\n\n먼저 OAuth 인증을 진행하세요.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnRefreshToken.Enabled = false;
            SetStatus("갱신 중...", Color.Orange);
            try
            {
                var t = JsonSerializer.Deserialize<Cafe24Token>(File.ReadAllText(path))!;
                if (string.IsNullOrEmpty(t.RefreshToken))
                {
                    Log("Refresh Token이 없습니다. 다시 OAuth 인증을 진행하세요.", Color.Red);
                    return;
                }

                string resolvedMallId = string.IsNullOrEmpty(t.MallId) ? mallId : t.MallId;
                string clientId       = string.IsNullOrEmpty(t.ClientId)     ? txtClientId.Text.Trim()     : t.ClientId;
                string clientSecret   = string.IsNullOrEmpty(t.ClientSecret) ? txtClientSecret.Text.Trim() : t.ClientSecret;

                Log("Refresh Token으로 갱신 중...");
                await RefreshAccessToken(resolvedMallId, clientId, clientSecret, t.RefreshToken, t);
            }
            catch (Exception ex)
            {
                Log($"갱신 오류: {ex.Message}", Color.Red);
                SetStatus("갱신 실패", Color.Red);
            }
            finally
            {
                btnRefreshToken.Enabled = true;
            }
        }

        private async Task RefreshAccessToken(string mallId, string clientId, string clientSecret, string refreshToken, Cafe24Token existing, bool updateDisplay = true, bool showFailureDialog = true)
        {
            string tokenUrl    = $"https://{mallId}.cafe24api.com/api/v2/oauth/token";
            string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Basic {credentials}");

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type",    "refresh_token" },
                { "refresh_token",  refreshToken }
            });

            try
            {
                var resp = await client.PostAsync(tokenUrl, form);
                string body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    Log($"갱신 실패 ({(int)resp.StatusCode}): {body}", Color.Red);
                    Log("Refresh Token이 만료된 경우 OAuth 인증을 다시 진행하세요.", Color.Orange);
                    SetStatus("갱신 실패", Color.Red);
                    if (showFailureDialog)
                        MessageBox.Show($"토큰 갱신 실패:\n{body}\n\n'OAuth 인증 시작'으로 재인증하세요.", "갱신 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var token = ParseToken(body, mallId, clientId, clientSecret, existing.RedirectUri, existing.Scope);
                if (string.IsNullOrEmpty(token.RefreshToken))
                {
                    // Cafe24가 새 refresh_token을 안 줬으면 기존 것 유지 + 발급일도 유지
                    token.RefreshToken         = refreshToken;
                    token.RefreshTokenUpdatedAt = existing.RefreshTokenUpdatedAt == default
                        ? existing.UpdatedAt   // 구 토큰 파일 호환
                        : existing.RefreshTokenUpdatedAt;
                }

                SaveToken(token);
                if (updateDisplay || string.Equals(txtMallId.Text.Trim(), token.MallId, StringComparison.OrdinalIgnoreCase))
                    DisplayToken(token);
                Log($"[{token.MallId}] 토큰 갱신 완료!", Color.Cyan);
            }
            catch (Exception ex)
            {
                Log($"갱신 처리 오류: {ex.Message}", Color.Red);
                SetStatus("오류", Color.Red);
            }
        }
        #endregion

        // ══════════════════════════════════════════
        #region 헬퍼
        private static Cafe24Token ParseToken(string json, string mallId, string clientId, string clientSecret, string redirectUri, string scope)
        {
            var d = JsonSerializer.Deserialize<JsonElement>(json);
            // API가 scope를 비워서 반환하는 경우 요청 scope 사용
            string returnedScope = d.TryGetProperty("scope", out var sc) ? sc.GetString() ?? "" : "";
            return new Cafe24Token
            {
                MallId                = mallId,
                ClientId              = clientId,
                ClientSecret          = clientSecret,
                AccessToken           = d.TryGetProperty("access_token",  out var at) ? at.GetString() ?? "" : "",
                RefreshToken          = d.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "",
                RedirectUri           = redirectUri,
                ApiVersion            = "2025-12-01",
                ShopNo                = "1",
                Scope                 = string.IsNullOrEmpty(returnedScope) ? scope : returnedScope,
                UpdatedAt             = DateTime.Now,
                RefreshTokenUpdatedAt = DateTime.Now   // 신규 발급 시 항상 now
            };
        }
        #endregion

        // ══════════════════════════════════════════
        #region 자동갱신 타이머
        private async void AutoRefreshTimer_Tick(object? sender, EventArgs e)
        {
            if (_isAuthRunning || _isAutoRefreshing)
                return;

            _isAutoRefreshing = true;
            var previousRefreshEnabled = btnRefreshToken.Enabled;
            try
            {
                btnRefreshToken.Enabled = false;
                await AutoRefreshKnownTokenFilesAsync();
            }
            finally
            {
                _isAutoRefreshing = false;
                btnRefreshToken.Enabled = previousRefreshEnabled && !_isAuthRunning;
            }
        }

        private async Task AutoRefreshKnownTokenFilesAsync()
        {
            var tokenPaths = GetKnownTokenPaths();
            if (tokenPaths.Count == 0)
                return;

            var selectedMallId = txtMallId.Text.Trim();
            var selectedMallSeen = false;
            var refreshedCount = 0;
            var refreshWarningMall = "";
            var refreshWarningRemain = TimeSpan.MaxValue;
            var now = DateTime.Now;

            foreach (var path in tokenPaths)
            {
                Cafe24Token? t;
                try
                {
                    t = JsonSerializer.Deserialize<Cafe24Token>(File.ReadAllText(path));
                }
                catch (Exception ex)
                {
                    Log($"토큰 파일 읽기 실패: {Path.GetFileName(path)} - {ex.Message}", Color.Orange);
                    continue;
                }

                if (t == null)
                    continue;

                if (string.IsNullOrWhiteSpace(t.MallId))
                    t.MallId = MallIdFromTokenPath(path);

                if (string.IsNullOrWhiteSpace(t.MallId) ||
                    string.IsNullOrWhiteSpace(t.AccessToken) ||
                    string.IsNullOrWhiteSpace(t.RefreshToken))
                    continue;

                var accessExpiry = t.UpdatedAt.AddHours(2);
                var rtBase = t.RefreshTokenUpdatedAt == default ? t.UpdatedAt : t.RefreshTokenUpdatedAt;
                var refreshExpiry = rtBase.AddDays(14);
                var accessRemain = accessExpiry - now;
                var refreshRemain = refreshExpiry - now;
                var isSelectedMall = string.Equals(selectedMallId, t.MallId, StringComparison.OrdinalIgnoreCase);

                if (isSelectedMall)
                {
                    selectedMallSeen = true;
                    UpdateAutoRefreshLabel(accessRemain, refreshRemain);
                }

                if (refreshRemain < refreshWarningRemain)
                {
                    refreshWarningRemain = refreshRemain;
                    refreshWarningMall = t.MallId;
                }

                if (accessRemain.TotalMinutes >= 5 || refreshRemain.TotalSeconds <= 0)
                    continue;

                var (clientId, clientSecret) = ResolveClientCredentials(t);
                if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                {
                    Log($"[{t.MallId}] Client ID/Secret이 없어 자동갱신을 건너뜁니다.", Color.Orange);
                    continue;
                }

                Log($"[{t.MallId}] Access Token 자동갱신 시작...", Color.Cyan);
                await RefreshAccessToken(t.MallId, clientId, clientSecret, t.RefreshToken, t, updateDisplay: isSelectedMall, showFailureDialog: false);
                refreshedCount++;
            }

            if (!selectedMallSeen && tokenPaths.Count > 0)
            {
                lblAutoRefresh.Text = refreshedCount > 0
                    ? $"🔄 Cafe24 JSON {refreshedCount}개 자동갱신 완료 — {tokenPaths.Count}개 감시 중"
                    : $"🔄 Cafe24 JSON {tokenPaths.Count}개 자동갱신 감시 중";
                lblAutoRefresh.ForeColor = Color.DimGray;
            }

            if (refreshWarningRemain.TotalDays < 2 && !_reAuthWarningShown)
            {
                _reAuthWarningShown = true;
                var msg = refreshWarningRemain.TotalHours < 1
                    ? $"⚠ {refreshWarningMall} Refresh Token이 {(int)refreshWarningRemain.TotalMinutes}분 후 만료됩니다!\n즉시 '🔐 재인증'을 눌러주세요."
                    : $"⚠ {refreshWarningMall} Refresh Token이 {(int)refreshWarningRemain.TotalHours}시간 후 만료됩니다.\n'🔐 재인증' 버튼으로 재인증하세요.";
                MessageBox.Show(msg, "재인증 필요", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private List<string> GetKnownTokenPaths()
        {
            var paths = new List<string>();
            if (!Directory.Exists(KEY_DIR))
                return paths;

            foreach (var path in Directory.EnumerateFiles(KEY_DIR, "cafe24_token_*.json"))
            {
                var fileName = Path.GetFileName(path);
                if (string.Equals(fileName, "cafe24_token.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                paths.Add(path);
            }

            return paths;
        }

        private static string MallIdFromTokenPath(string path)
        {
            const string prefix = "cafe24_token_";
            var name = Path.GetFileNameWithoutExtension(path);
            return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(prefix.Length)
                : "";
        }

        private (string ClientId, string ClientSecret) ResolveClientCredentials(Cafe24Token token)
        {
            var clientId = token.ClientId;
            var clientSecret = token.ClientSecret;
            if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
                return (clientId, clientSecret);

            try
            {
                if (File.Exists(SETTINGS_PATH))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SETTINGS_PATH));
                    if (settings?.mall_configs != null && settings.mall_configs.TryGetValue(token.MallId, out var config))
                    {
                        if (string.IsNullOrWhiteSpace(clientId))
                            clientId = config.client_id;
                        if (string.IsNullOrWhiteSpace(clientSecret))
                            clientSecret = config.client_secret;
                    }
                }
            }
            catch
            {
            }

            if (string.Equals(txtMallId.Text.Trim(), token.MallId, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(clientId))
                    clientId = txtClientId.Text.Trim();
                if (string.IsNullOrWhiteSpace(clientSecret))
                    clientSecret = txtClientSecret.Text.Trim();
            }

            return (clientId, clientSecret);
        }

        private void UpdateAutoRefreshLabel(TimeSpan accessRemain, TimeSpan refreshRemain)
        {
            if (lblAutoRefresh.InvokeRequired) { lblAutoRefresh.Invoke(() => UpdateAutoRefreshLabel(accessRemain, refreshRemain)); return; }

            if (refreshRemain.TotalDays < 2)
            {
                lblAutoRefresh.Text      = $"⚠ 재인증 필요! Refresh Token 만료까지 {(int)refreshRemain.TotalHours}h {refreshRemain.Minutes}m — '🔐 재인증' 버튼을 누르세요";
                lblAutoRefresh.ForeColor = Color.Red;
            }
            else if (accessRemain.TotalMinutes < 5)
            {
                lblAutoRefresh.Text      = "⏰ Access Token 갱신 중...";
                lblAutoRefresh.ForeColor = Color.Orange;
            }
            else if (accessRemain.TotalSeconds > 0)
            {
                lblAutoRefresh.Text      = $"🔄 자동갱신 대기 중 — Access Token 만료까지 {(int)accessRemain.TotalHours}h {accessRemain.Minutes}m | Refresh Token D-{(int)refreshRemain.TotalDays}";
                lblAutoRefresh.ForeColor = Color.DimGray;
            }
            else
            {
                lblAutoRefresh.Text      = "⚠ Access Token 만료됨 — 자동갱신 시도 중...";
                lblAutoRefresh.ForeColor = Color.DarkOrange;
            }
        }
        #endregion

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cts?.Cancel();
            _tcpListener?.Stop();
            if (_tunnelProcess != null && !_tunnelProcess.HasExited)
                _tunnelProcess.Kill();
            base.OnFormClosing(e);
        }
    }

    // ══════════════════════════════════════════
    // Scope 선택 다이얼로그
    // ══════════════════════════════════════════
    public class ScopePickerForm : Form
    {
        public string SelectedScope { get; private set; } = "";

        private static readonly (string Label, string Key, bool CanWrite, bool DefaultCheck)[] _scopes =
        {
            ("앱 (Application)",       "application", true,  true),
            ("상품 (Product)",          "product",     true,  true),
            ("상품분류 (Category)",      "category",    true,  false),
            ("판매분류 (Collection)",    "collection",  true,  false),
            ("주문 (Order)",            "order",       true,  true),
            ("배송 (Shipping)",         "shipping",    true,  true),
            ("회원 (Customer)",         "customer",    true,  false),
            ("상점 (Store)",            "store",       true,  false),
            ("프로모션 (Promotion)",     "promotion",   true,  false),
            ("게시판 (Community)",       "community",   true,  false),
            ("공급사 (Supply)",          "supply",      true,  false),
            ("개인화정보 (Personal)",    "personal",    false, false),
            ("알림 (Notification)",     "notification",true,  false),
            ("디자인 (Design)",          "design",      true,  false),
            ("매출통계 (Salesreport)",   "salesreport", false, false),
            ("번역 (Translation)",      "translation", true,  false),
            ("접속통계 (Analytics)",     "analytics",   false, false),
        };

        private readonly CheckBox[] _readChecks;
        private readonly CheckBox[] _writeChecks;

        public ScopePickerForm(string currentScope)
        {
            Text            = "API Scope 선택";
            Size            = new Size(480, 600);
            StartPosition   = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            BackColor       = Color.FromArgb(245, 247, 250);

            _readChecks  = new CheckBox[_scopes.Length];
            _writeChecks = new CheckBox[_scopes.Length];

            // 헤더
            var pnlHeader = new Panel { Left = 0, Top = 0, Width = 480, Height = 36, BackColor = Color.FromArgb(37, 99, 235) };
            pnlHeader.Controls.Add(new Label { Text = "사용할 권한을 선택하세요", Left = 12, Top = 8, ForeColor = Color.White, Font = new Font("맑은 고딕", 11f, FontStyle.Bold), AutoSize = true });
            Controls.Add(pnlHeader);

            // 컬럼 헤더
            int y = 44;
            Controls.Add(new Label { Text = "분류",  Left = 16, Top = y, Width = 200, Font = new Font("맑은 고딕", 9f, FontStyle.Bold) });
            Controls.Add(new Label { Text = "Read",  Left = 232, Top = y, Width = 60,  Font = new Font("맑은 고딕", 9f, FontStyle.Bold) });
            Controls.Add(new Label { Text = "Write", Left = 320, Top = y, Width = 60,  Font = new Font("맑은 고딕", 9f, FontStyle.Bold) });
            y += 24;

            var panel = new Panel { Left = 8, Top = y, Width = 448, Height = 440, AutoScroll = true, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(panel);

            // 파싱: 현재 scope 문자열로 체크 상태 초기화
            var existing = new HashSet<string>(currentScope.Split(',', StringSplitOptions.RemoveEmptyEntries));

            int py = 4;
            for (int i = 0; i < _scopes.Length; i++)
            {
                var (lbl, key, canWrite, defaultCheck) = _scopes[i];
                bool bgAlt = i % 2 == 0;

                var row = new Panel { Left = 0, Top = py, Width = 445, Height = 28, BackColor = bgAlt ? Color.FromArgb(249, 250, 251) : Color.White };
                panel.Controls.Add(row);

                row.Controls.Add(new Label { Text = lbl, Left = 8, Top = 5, Width = 210, AutoSize = false });

                bool hasRead  = existing.Count > 0 ? existing.Contains($"mall.read_{key}")  : defaultCheck;
                bool hasWrite = existing.Count > 0 ? existing.Contains($"mall.write_{key}") : defaultCheck && canWrite;

                _readChecks[i] = new CheckBox { Left = 222, Top = 4, Checked = hasRead };
                row.Controls.Add(_readChecks[i]);

                _writeChecks[i] = new CheckBox { Left = 310, Top = 4, Checked = hasWrite && canWrite, Enabled = canWrite };
                row.Controls.Add(_writeChecks[i]);

                // Read 체크 해제 시 Write도 해제
                int idx = i;
                _readChecks[i].CheckedChanged += (s, e) =>
                {
                    if (!_readChecks[idx].Checked) _writeChecks[idx].Checked = false;
                };

                py += 28;
            }

            // 전체선택 / 초기화
            var btnAll = new Button { Text = "전체 Read", Left = 8, Top = 500, Width = 100, Height = 28, FlatStyle = FlatStyle.Flat };
            btnAll.Click += (s, e) => { foreach (var c in _readChecks) c.Checked = true; };
            Controls.Add(btnAll);

            var btnNone = new Button { Text = "전체 해제", Left = 115, Top = 500, Width = 100, Height = 28, FlatStyle = FlatStyle.Flat };
            btnNone.Click += (s, e) => { foreach (var c in _readChecks) c.Checked = false; foreach (var c in _writeChecks) c.Checked = false; };
            Controls.Add(btnNone);

            var btnOk = new Button
            {
                Text = "✅ 확인", Left = 260, Top = 500, Width = 90, Height = 28,
                BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.OK
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += BtnOk_Click;
            Controls.Add(btnOk);

            var btnCancel = new Button
            {
                Text = "취소", Left = 358, Top = 500, Width = 80, Height = 28,
                FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel
            };
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private void BtnOk_Click(object? sender, EventArgs e)
        {
            var parts = new List<string>();
            for (int i = 0; i < _scopes.Length; i++)
            {
                if (_readChecks[i].Checked)  parts.Add($"mall.read_{_scopes[i].Key}");
                if (_writeChecks[i].Checked) parts.Add($"mall.write_{_scopes[i].Key}");
            }
            SelectedScope = string.Join(",", parts);
        }
    }
}
