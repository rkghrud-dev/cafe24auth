using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
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
        private static readonly string KEY_DIR       = @"C:\Users\rkghr\Desktop\key";
        private static readonly string SETTINGS_PATH = Path.Combine(KEY_DIR, "cafe24_settings.json");

        // 토큰 경로: mall_id 포함
        private string TokenPath(string mallId)
            => Path.Combine(KEY_DIR, $"cafe24_token_{mallId}.json");

        // ──────────────── 컨트롤 ────────────────
        private TextBox txtMallId, txtClientId, txtClientSecret, txtRedirectUri, txtLocalPort;
        private TextBox txtAuthScope;
        private TextBox txtAccessToken, txtRefreshToken, txtExpiry, txtScope;
        private Button  btnStartAuth, btnRefreshToken, btnLoadToken, btnSaveSettings, btnCopyToken;
        private Button  btnNgrok, btnNgrokUrl;
        private Label   lblNgrokStatus;
        private RichTextBox rtbLog;
        private Label   lblStatus;

        // ──────────────── 런타임 ────────────────
        private HttpListener?            _httpListener;
        private CancellationTokenSource? _cts;
        private Process?                 _ngrokProcess;
        private bool                     _isAuthRunning;

        // ──────────────── DTO ────────────────
        private class Cafe24Token
        {
            public string   access_token  { get; set; } = "";
            public string   refresh_token { get; set; } = "";
            public string   token_type    { get; set; } = "";
            public int      expires_in    { get; set; }
            public string   scope         { get; set; } = "";
            public DateTime issued_at     { get; set; }
            public string   mall_id       { get; set; } = "";
        }

        private class AppSettings
        {
            public string mall_id       { get; set; } = "";
            public string client_id     { get; set; } = "";
            public string client_secret { get; set; } = "";
            public string redirect_uri  { get; set; } = "";
            public string local_port    { get; set; } = "5000";
            public string scope         { get; set; } = "";
        }

        // ══════════════════════════════════════════
        public MainForm()
        {
            InitializeUI();
            LoadSettings();
            LoadToken();
        }

        // ══════════════════════════════════════════
        #region UI 초기화
        private void InitializeUI()
        {
            Text            = "Cafe24 API 인증 관리자";
            Size            = new Size(720, 680);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox     = false;
            BackColor       = Color.FromArgb(245, 247, 250);

            int y = 15;

            // ── 앱 설정 그룹 ──
            var grpSettings = MakeGroup("⚙  앱 설정", 10, y, 690, 305);
            Controls.Add(grpSettings);

            int gy = 28;
            AddRow(grpSettings, ref gy, "Mall ID:",       out txtMallId,       "예: myshop  (myshop.cafe24.com)");
            AddRow(grpSettings, ref gy, "Client ID:",     out txtClientId,     "Cafe24 앱 관리 > Client ID");
            AddRow(grpSettings, ref gy, "Client Secret:", out txtClientSecret, "", password: true);
            AddRow(grpSettings, ref gy, "Redirect URI:",  out txtRedirectUri,  "https://xxxx.ngrok.io/callback");
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
            grpSettings.Controls.Add(txtLocalPort);

            btnNgrok = MakeButton("▶ ngrok 실행", 220, gy - 1, 120, 28, Color.FromArgb(22, 163, 74));
            btnNgrok.Click += BtnNgrok_Click;
            grpSettings.Controls.Add(btnNgrok);

            btnNgrokUrl = MakeButton("🔗 URL 가져오기", 350, gy - 1, 125, 28, Color.FromArgb(59, 130, 246));
            btnNgrokUrl.Click += BtnNgrokUrl_Click;
            grpSettings.Controls.Add(btnNgrokUrl);

            lblNgrokStatus = new Label
            {
                Left = 485, Top = gy + 5, Width = 180, AutoSize = false,
                Text = "ngrok 미실행", ForeColor = Color.Gray,
                Font = new Font("맑은 고딕", 8.5f)
            };
            grpSettings.Controls.Add(lblNgrokStatus);

            gy += 38;

            // ngrok 안내 라벨
            var lblNote = new Label
            {
                Text      = "① ngrok 실행 → ② URL 가져오기 클릭 → Redirect URI 자동입력\n" +
                            "   Cafe24 앱 설정의 Redirect URL과 동일하게 등록하세요.",
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
            btnStartAuth = MakeButton("🔐 OAuth 인증 시작", 10, y, 175, 38, Color.FromArgb(37, 99, 235));
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

            y += 50;

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

        private void SetNgrokStatus(string text, Color color)
        {
            if (lblNgrokStatus.InvokeRequired) { lblNgrokStatus.Invoke(() => SetNgrokStatus(text, color)); return; }
            lblNgrokStatus.Text      = text;
            lblNgrokStatus.ForeColor = color;
        }
        #endregion

        // ══════════════════════════════════════════
        #region ngrok
        private void BtnNgrok_Click(object? sender, EventArgs e)
        {
            if (_ngrokProcess != null && !_ngrokProcess.HasExited)
            {
                // 이미 실행 중 → 종료
                _ngrokProcess.Kill();
                _ngrokProcess = null;
                btnNgrok.Text      = "▶ ngrok 실행";
                btnNgrok.BackColor = Color.FromArgb(22, 163, 74);
                SetNgrokStatus("ngrok 종료됨", Color.Gray);
                Log("ngrok 종료");
                return;
            }

            string port = txtLocalPort.Text.Trim();

            // ngrok.exe 위치 탐색: PATH → 현재 폴더 → Desktop
            string ngrokExe = FindNgrok();
            if (ngrokExe == null)
            {
                MessageBox.Show(
                    "ngrok.exe를 찾을 수 없습니다.\n\n" +
                    "ngrok을 설치하거나 ngrok.exe를 이 프로그램과 같은 폴더에 두세요.\n" +
                    "다운로드: https://ngrok.com/download",
                    "ngrok 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _ngrokProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName        = ngrokExe,
                        Arguments       = $"http {port}",
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    },
                    EnableRaisingEvents = true
                };
                _ngrokProcess.Exited += (s, ev) =>
                {
                    this.Invoke(() =>
                    {
                        btnNgrok.Text      = "▶ ngrok 실행";
                        btnNgrok.BackColor = Color.FromArgb(22, 163, 74);
                        SetNgrokStatus("ngrok 종료됨", Color.Gray);
                    });
                };
                _ngrokProcess.Start();

                btnNgrok.Text      = "■ ngrok 중지";
                btnNgrok.BackColor = Color.FromArgb(220, 38, 38);
                SetNgrokStatus("시작 중...", Color.Orange);
                Log($"ngrok 시작: http {port}");

                // 2초 후 URL 자동 가져오기
                Task.Delay(2000).ContinueWith(_ => this.Invoke(async () => await FetchNgrokUrl()));
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

            return null;
        }

        private async void BtnNgrokUrl_Click(object? sender, EventArgs e)
            => await FetchNgrokUrl();

        private async Task FetchNgrokUrl()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var resp = await client.GetStringAsync("http://localhost:4040/api/tunnels");
                var doc  = JsonSerializer.Deserialize<JsonElement>(resp);
                var tunnels = doc.GetProperty("tunnels");

                string? httpsUrl = null;
                foreach (var t in tunnels.EnumerateArray())
                {
                    var proto = t.GetProperty("proto").GetString();
                    if (proto == "https")
                    {
                        httpsUrl = t.GetProperty("public_url").GetString();
                        break;
                    }
                }

                if (string.IsNullOrEmpty(httpsUrl))
                {
                    Log("ngrok HTTPS 터널을 찾지 못했습니다.", Color.Orange);
                    SetNgrokStatus("터널 없음", Color.Orange);
                    return;
                }

                string redirectUri = httpsUrl.TrimEnd('/') + "/callback";
                txtRedirectUri.Text = redirectUri;
                SetNgrokStatus("연결됨 ✓", Color.DarkGreen);
                Log($"ngrok URL: {redirectUri}", Color.Cyan);
                Log("Redirect URI가 자동 입력되었습니다. Cafe24 앱 설정과 동일한지 확인하세요.", Color.Yellow);
            }
            catch (Exception)
            {
                SetNgrokStatus("API 응답 없음", Color.Red);
                Log("ngrok API(localhost:4040)에 연결할 수 없습니다. ngrok이 실행 중인지 확인하세요.", Color.Orange);
            }
        }
        #endregion

        // ══════════════════════════════════════════
        #region 설정 저장/불러오기
        private void LoadSettings()
        {
            if (!File.Exists(SETTINGS_PATH)) return;
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SETTINGS_PATH))!;
                txtMallId.Text       = s.mall_id;
                txtClientId.Text     = s.client_id;
                txtClientSecret.Text = s.client_secret;
                txtRedirectUri.Text  = s.redirect_uri;
                txtLocalPort.Text    = s.local_port;
                txtAuthScope.Text    = s.scope;
                Log("설정 파일 로드 완료");
            }
            catch (Exception ex) { Log($"설정 로드 오류: {ex.Message}", Color.Orange); }
        }

        private void BtnSaveSettings_Click(object? sender, EventArgs e)
        {
            var s = new AppSettings
            {
                mall_id       = txtMallId.Text.Trim(),
                client_id     = txtClientId.Text.Trim(),
                client_secret = txtClientSecret.Text.Trim(),
                redirect_uri  = txtRedirectUri.Text.Trim(),
                local_port    = txtLocalPort.Text.Trim(),
                scope         = txtAuthScope.Text.Trim()
            };
            Directory.CreateDirectory(KEY_DIR);
            File.WriteAllText(SETTINGS_PATH, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            Log("설정 저장 완료");
            MessageBox.Show("설정이 저장되었습니다.", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                DisplayToken(t);
                Log($"토큰 파일 로드: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { Log($"토큰 로드 오류: {ex.Message}", Color.Red); }
        }

        private void DisplayToken(Cafe24Token token)
        {
            if (InvokeRequired) { Invoke(() => DisplayToken(token)); return; }

            txtAccessToken.Text  = token.access_token;
            txtRefreshToken.Text = token.refresh_token;
            txtScope.Text        = token.scope;

            var expiry = token.issued_at.AddSeconds(token.expires_in);
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
            string path = TokenPath(token.mall_id);
            Directory.CreateDirectory(KEY_DIR);
            File.WriteAllText(path, JsonSerializer.Serialize(token, new JsonSerializerOptions { WriteIndented = true }));
            Log($"토큰 저장 → {Path.GetFileName(path)}");
        }
        #endregion

        // ══════════════════════════════════════════
        #region OAuth 인증
        private async void BtnStartAuth_Click(object? sender, EventArgs e)
        {
            // 인증 중이면 → 중지
            if (_isAuthRunning)
            {
                _cts?.Cancel();
                _httpListener?.Stop();
                SetAuthRunning(false);
                SetStatus("중지됨", Color.Gray);
                Log("인증이 중지되었습니다.", Color.Orange);
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

            SetAuthRunning(true);
            SetStatus("인증 중...", Color.Orange);
            try   { await StartOAuthFlow(); }
            finally { SetAuthRunning(false); }
        }

        private void SetAuthRunning(bool running)
        {
            _isAuthRunning = running;
            if (running)
            {
                btnStartAuth.Text      = "⏹ 인증 중지";
                btnStartAuth.BackColor = Color.FromArgb(220, 38, 38);
            }
            else
            {
                btnStartAuth.Text      = "🔐 OAuth 인증 시작";
                btnStartAuth.BackColor = Color.FromArgb(37, 99, 235);
            }
            // 인증 중에는 갱신/설정저장 비활성
            btnRefreshToken.Enabled  = !running;
            btnSaveSettings.Enabled  = !running;
        }

        private async Task StartOAuthFlow()
        {
            string mallId       = txtMallId.Text.Trim();
            string clientId     = txtClientId.Text.Trim();
            string clientSecret = txtClientSecret.Text.Trim();
            string redirectUri  = txtRedirectUri.Text.Trim();
            int    port         = int.Parse(txtLocalPort.Text.Trim());

            var redirectUriObj   = new Uri(redirectUri);
            string callbackPath  = redirectUriObj.AbsolutePath.TrimEnd('/') + "/";
            string localCallback = $"http://+:{port}{callbackPath}";

            _cts          = new CancellationTokenSource();
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(localCallback);

            try
            {
                _httpListener.Start();
                Log($"로컬 서버 시작: {localCallback}");
            }
            catch (Exception ex)
            {
                Log($"서버 시작 실패 (포트 {port} 충돌?): {ex.Message}", Color.Red);
                MessageBox.Show(
                    $"로컬 HTTP 서버를 시작할 수 없습니다.\n포트 {port}를 변경하거나 관리자 권한으로 실행하세요.\n\n{ex.Message}",
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
                             $"&state={state}" +
                             $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                             $"&scope={Uri.EscapeDataString(scope)}";

            Log($"[DEBUG] redirect_uri 전송값: {redirectUri}", Color.Yellow);
            Log($"[DEBUG] 전체 auth URL: {authUrl}", Color.Yellow);
            Log("브라우저에서 Cafe24 인증 페이지 열기...");
            Process.Start(new ProcessStartInfo { FileName = authUrl, UseShellExecute = true });
            Log("브라우저에서 로그인 후 앱 권한을 승인하세요. (대기 중...)");

            try
            {
                var context = await Task.Run(() => _httpListener!.GetContext(), _cts.Token);
                var qs = context.Request.QueryString;

                string html = "<html><meta charset='utf-8'><body style='font-family:Arial;text-align:center;padding:60px'>" +
                              "<h2 style='color:#16a34a'>✅ 인증 완료!</h2><p>이 창을 닫고 프로그램으로 돌아오세요.</p></body></html>";
                byte[] buf = Encoding.UTF8.GetBytes(html);
                context.Response.ContentType     = "text/html; charset=utf-8";
                context.Response.ContentLength64 = buf.Length;
                context.Response.OutputStream.Write(buf, 0, buf.Length);
                context.Response.Close();

                string? errorParam = qs["error"];
                if (!string.IsNullOrEmpty(errorParam))
                {
                    Log($"인증 거부/오류: {errorParam} - {qs["error_description"]}", Color.Red);
                    SetStatus("인증 실패", Color.Red);
                    return;
                }

                string? code          = qs["code"];
                string? returnedState = qs["state"];

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
                await ExchangeCodeForToken(mallId, clientId, clientSecret, code, redirectUri);
            }
            catch (TaskCanceledException)
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
                _httpListener?.Stop();
                _httpListener?.Close();
                Log("로컬 서버 종료");
            }
        }

        private async Task ExchangeCodeForToken(string mallId, string clientId, string clientSecret, string code, string redirectUri)
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

                var token = ParseToken(body, mallId);
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
                if (string.IsNullOrEmpty(t.refresh_token))
                {
                    Log("Refresh Token이 없습니다. 다시 OAuth 인증을 진행하세요.", Color.Red);
                    return;
                }

                string resolvedMallId = string.IsNullOrEmpty(t.mall_id) ? mallId : t.mall_id;
                string clientId       = txtClientId.Text.Trim();
                string clientSecret   = txtClientSecret.Text.Trim();

                Log("Refresh Token으로 갱신 중...");
                await RefreshAccessToken(resolvedMallId, clientId, clientSecret, t.refresh_token);
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

        private async Task RefreshAccessToken(string mallId, string clientId, string clientSecret, string refreshToken)
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
                    MessageBox.Show($"토큰 갱신 실패:\n{body}\n\n'OAuth 인증 시작'으로 재인증하세요.", "갱신 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var token = ParseToken(body, mallId);
                if (string.IsNullOrEmpty(token.refresh_token))
                    token.refresh_token = refreshToken;

                SaveToken(token);
                DisplayToken(token);
                Log("✅ 토큰 갱신 완료!", Color.Cyan);
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
        private static Cafe24Token ParseToken(string json, string mallId)
        {
            var d = JsonSerializer.Deserialize<JsonElement>(json);
            return new Cafe24Token
            {
                access_token  = d.TryGetProperty("access_token",  out var at) ? at.GetString() ?? "" : "",
                refresh_token = d.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "",
                token_type    = d.TryGetProperty("token_type",    out var tt) ? tt.GetString() ?? "" : "",
                expires_in    = d.TryGetProperty("expires_in",    out var ei) ? ei.GetInt32()      : 7200,
                scope         = d.TryGetProperty("scope",         out var sc) ? sc.GetString() ?? "" : "",
                issued_at     = DateTime.Now,
                mall_id       = mallId
            };
        }
        #endregion

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cts?.Cancel();
            _httpListener?.Stop();
            if (_ngrokProcess != null && !_ngrokProcess.HasExited)
                _ngrokProcess.Kill();
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
