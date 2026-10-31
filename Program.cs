using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ImeSwitcher;

/// <summary>
/// ImeSwitcher —— 从 SoundViz.Overlay 抽出的独立小工具（v1.1.3）。
///
/// 职责只有一件事：前台是 cs2 时切到游戏内目标输入法（默认微软拼音），离开 CS2 恢复进入前的输入法。
///
/// 【为什么默认微软拼音】CS2 全屏下只有微软拼音/谷歌拼音能正常弹出候选词，
/// 第三方输入法（搜狗/QQ拼音/百度等）在游戏内无法显示候选窗。
///
/// 【与 SoundViz 的区别（风控画像考量）】
///   - 不含音频采集（无 WASAPI）、无 DirectX/D2D 覆盖层、无 Raw Input 鼠标/键盘监听
///   - 进程名不带 Overlay 字样
///   - 仅有的系统交互：
///       1) GetForegroundWindow + OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) 识别前台进程名
///          （只查询进程映像路径，不读写目标进程内存）
///       2) TSF 输入法 API（枚举/读取/激活输入法 profile）
///   - 这两点与 Windows 自带输入法指示器的能力范围相同。
///
/// 【移植来源】SoundViz.Overlay/ImeAutoSwitcher.cs（TSF 签名按 msctf.h 10.0.26100 核对）。
/// 差异：原版由 D2D 覆盖层帧循环调用 Tick(cs2Present)；本工具自带前台检测，用
/// WinForms Timer（UI 线程、500ms 限频）驱动。
/// </summary>
internal static class Program
{
    // 目标进程（前台为该进程时切换输入法）。如需支持其它进程，改这里。
    private const string TargetProcess = "cs2";

    private static readonly Mutex SingleInstance = new(false, "Local\\ImeSwitcher_SingleInstance_20261031");

    [STAThread]
    private static void Main()
    {
        if (!SingleInstance.WaitOne(0))
        {
            MessageBox.Show("ImeSwitcher 已在运行（托盘图标）。", "ImeSwitcher",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        var switcher = new ImeSwitcherCore(TargetProcess, Log);

        using var icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "ImeSwitcher：运行中",
            Visible = true,
        };

        var menu = new ContextMenuStrip();

        // "游戏内输入法"子菜单：打开时动态枚举系统已启用的输入法，勾选当前目标
        var targetMenu = new ToolStripMenuItem("游戏内输入法");
        targetMenu.DropDownOpening += (_, _) => RebuildTargetMenu(targetMenu, switcher);
        menu.Items.Add(targetMenu);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("关于 ImeSwitcher…", null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            switcher.Disable();     // 若正处于 CS2 切入态，先恢复原输入法再退出
            icon.Visible = false;
            Application.Exit();
        });
        icon.ContextMenuStrip = menu;

        // UI 线程定时器驱动，与原版"唯一线程调用 Tick"的约定一致
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) =>
        {
            switcher.Tick();
            var st = switcher.Status;
            icon.Text = st.Length <= 63 ? st : st[..63];
        };
        timer.Start();

        // 启动气泡：让用户知道工具在哪、怎么用（Win11 默认把托盘新图标藏进 ^ 折叠区）
        icon.BalloonTipTitle = "ImeSwitcher 已启动";
        icon.BalloonTipText = "图标在任务栏右下角 ^（隐藏图标）里，可到 设置→个性化→任务栏→其他系统托盘图标 设为常驻显示。" +
                              $"进入 {TargetProcess} 自动切微软拼音，切出自动恢复原输入法。右键图标可更换游戏内输入法。";
        icon.ShowBalloonTip(8000);

        // 首次切换生效时弹一次气泡确认；异常时随时弹
        bool firstSwitchShown = false;
        switcher.Transitioned += msg =>
        {
            bool isError = msg.Contains("失败") || msg.Contains("异常") || msg.Contains("未找到");
            if (isError)
            {
                ShowBalloon(icon, "ImeSwitcher 异常", msg);
            }
            else if (!firstSwitchShown && msg.Contains("进入"))
            {
                firstSwitchShown = true;
                ShowBalloon(icon, "ImeSwitcher 已生效", msg);
            }
        };

        Log($"ImeSwitcher v1.2.0 启动：目标进程={TargetProcess}.exe");
        Application.Run();

        SingleInstance.ReleaseMutex();
    }

    private static void RebuildTargetMenu(ToolStripMenuItem targetMenu, ImeSwitcherCore switcher)
    {
        targetMenu.DropDownItems.Clear();

        targetMenu.DropDownItems.Add(new ToolStripMenuItem(
            "提示：CS2 内仅微软拼音/谷歌拼音能显示候选词", null, (_, _) => { })
        { Enabled = false });
        targetMenu.DropDownItems.Add(new ToolStripSeparator());

        foreach (var (profile, name) in switcher.ListSelectableTargets())
        {
            var item = new ToolStripMenuItem(name) { Checked = switcher.IsCurrentTarget(profile) };
            item.Click += (_, _) => switcher.SetTarget(profile, name);
            targetMenu.DropDownItems.Add(item);
        }
    }

    private static void ShowBalloon(NotifyIcon icon, string title, string text)
    {
        try
        {
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = text.Length > 250 ? text[..250] : text;
            icon.ShowBalloonTip(5000);
        }
        catch { /* 通知失败不影响主流程 */ }
    }

    /// <summary>关于窗口：显示版本号与工具说明（版本取自程序集，与 csproj 的 Version 单一来源）。</summary>
    private static void ShowAbout()
    {
        var ver = typeof(Program).Assembly.GetName().Version;
        MessageBox.Show(
            $"ImeSwitcher v{ver?.ToString(3)}\n\n" +
            "CS2 输入法自动切换器\n" +
            $"前台为 {TargetProcess} 时自动切到所选输入法，离开自动恢复原输入法。\n\n" +
            "技术边界：不注入、不读内存、不 hook、不联网；无覆盖层、无输入监听。\n\n" +
            "开源地址：github.com/Teympodi/cs2-ime-switcher\n" +
            @"日志文件：%LOCALAPPDATA%\ImeSwitcher\ime_switch.log",
            "关于 ImeSwitcher",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ===== 文件日志 =====

    private static readonly object LogLock = new();

    /// <summary>写一行到 %LOCALAPPDATA%\ImeSwitcher\ime_switch.log。</summary>
    private static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                string dir = Paths.DataDir;
                Directory.CreateDirectory(dir);
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}";
                File.AppendAllText(Path.Combine(dir, "ime_switch.log"), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}

/// <summary>%LOCALAPPDATA%\ImeSwitcher 下的文件路径。</summary>
internal static class Paths
{
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImeSwitcher");
    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string LogFile => Path.Combine(DataDir, "ime_switch.log");
}

/// <summary>config.json 结构。</summary>
internal sealed class ConfigData
{
    public string Name { get; set; } = "";
    public string Clsid { get; set; } = "";
    public string ProfileGuid { get; set; } = "";
    public ushort LangId { get; set; }
}

/// <summary>
/// 输入法自动切换核心：前台进程为目标（cs2）时切到"游戏内输入法"（默认微软拼音），离开时恢复进入前的输入法。
/// 移植自 ImeAutoSwitcher.cs，逻辑保持一致：
///   - 读取：GetKeyboardLayout 判语言层；TSF GetActiveLanguageProfile 按 CLSID 探测激活状态
///   - 切换：ITfInputProcessorProfileMgr::ActivateProfile（FORSESSION | DONTCARECURRENTINPUTLANGUAGE）
/// </summary>
internal sealed class ImeSwitcherCore
{
    // 默认目标输入法 CLSID（微软拼音，实测确认）
    private static readonly Guid ClsidMicrosoftPinyin = new("81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E");
    private static readonly Guid ClsidWeixin = new("86598FB9-66A2-463E-B9C2-AEB906D477AD");

    private readonly string _targetProcess;
    private readonly Action<string> _log;

    private bool _inited;
    private bool _inCs2;
    private long _lastCheckMs;

    // 最近一次非目标进程前台时用户激活的输入法 CLSID（持续跟踪）
    private string? _lastNonCs2ActiveClsid;

    // 进入目标进程时锁定的"用户原本激活的输入法"，用于离开时恢复
    private string? _beforeEnterClsid;

    // 离开恢复后待校验的期望输入法 CLSID（恢复是否真实生效，下一拍复核）
    private string? _pendingVerifyClsid;

    // 游戏内目标输入法（托盘可选，默认微软拼音）
    private LayoutOrTipProfile? _targetProfile;
    private string _targetName = "微软拼音";

    /// <summary>托盘提示用的状态描述。</summary>
    public string Status { get; private set; } = "ImeSwitcher：运行中";

    /// <summary>发生状态切换或异常时触发（供气泡通知）。</summary>
    public event Action<string>? Transitioned;

    public ImeSwitcherCore(string targetProcess, Action<string> log)
    {
        _targetProcess = targetProcess;
        _log = log;
    }

    /// <summary>由定时器调用（内部 500ms 限频）。须在唯一 UI 线程调用。</summary>
    public void Tick()
    {
        long now = Environment.TickCount64;
        if (now - _lastCheckMs < 450) return;   // 定时器 500ms，这里再兜底限频
        _lastCheckMs = now;

        try
        {
            if (!_inited)
            {
                _targetProfile = ResolveTarget();
                _inited = true;
                if (_targetProfile == null)
                {
                    Status = "ImeSwitcher：未找到目标输入法";
                    _log($"初始化失败：枚举不到目标输入法（默认微软拼音 {ClsidMicrosoftPinyin:B}），且 config.json 指向的输入法不存在");
                    return;
                }
                var t = _targetProfile.Value;
                _log($"初始化：目标={_targetName} clsid={t.Clsid:B} guid={t.ProfileGuid:B} langid={t.LangId}");
            }

            // _inited 成功后 _targetProfile 必非空（失败已提前 return）
            LayoutOrTipProfile target = _targetProfile!.Value;

            bool isTarget = IsTargetForeground();
            if (isTarget && !_inCs2)
            {
                // 进入目标进程：锁定进入前最后一次记录的输入法，再切游戏内输入法
                _inCs2 = true;
                _beforeEnterClsid = _lastNonCs2ActiveClsid;
                string? cur = GetActiveInputClsid();
                string curName = DescribeClsid(cur);
                _log($"切入{_targetProcess}前台：当前输入法=[{curName}] 快照记录=[{(_beforeEnterClsid ?? "无记录")}]");
                bool ok = ActivateTip(target);
                Status = ok ? $"ImeSwitcher：进入 {_targetProcess} → {_targetName}"
                            : $"ImeSwitcher：切换{_targetName}失败";
                _log($"进入{_targetProcess}：切{_targetName} {(ok ? "成功" : "失败")}");
                Transitioned?.Invoke(Status);
            }
            else if (isTarget)
            {
                // 仍在目标进程中：不更新 _lastNonCs2ActiveClsid，避免被焦点内输入法污染
            }
            else
            {
                // 刚离开目标进程的这一拍：先恢复，且【跳过快照记录】——
                // 此时系统还停留在被强切的游戏内输入法上，此刻的读数是污染值；
                // 若记入快照，下次进入会快照到游戏内输入法 → 恢复变"无需切换" → 永久卡死。
                // （2026-10-31 15:39 事故根因：离开后 417ms 内快速重进，污染快照先于纠偏读入。）
                if (_inCs2)
                {
                    _inCs2 = false;
                    RestoreBeforeEnter();   // 内部会设置 _lastRestoredClsid
                }
                else
                {
                    // 非目标前台：持续记录用户当时激活的输入法，供下次进入时恢复
                    string? activeClsid = GetActiveInputClsid();
                    if (activeClsid != null)
                    {
                        // 恢复后校验（离开后的第一拍）：期望已恢复到原输入法，
                        // 若系统仍停留在游戏内输入法上，说明恢复未真实生效，补一刀重试。
                        if (_pendingVerifyClsid != null)
                        {
                            string targetClsid = _targetProfile!.Value.Clsid.ToString();
                            bool stillOnGame = activeClsid.Equals(targetClsid, StringComparison.OrdinalIgnoreCase);
                            bool expectGame = _pendingVerifyClsid.Equals(targetClsid, StringComparison.OrdinalIgnoreCase);
                            if (stillOnGame && !expectGame &&
                                TryFindByClsid(_pendingVerifyClsid, out var again))
                            {
                                bool ok2 = ActivateTip(again);
                                _log($"恢复校验：系统仍在{_targetName}，重试恢复{(ok2 ? "成功" : "失败")}");
                                // 重试后的读数不可信，本拍跳过快照记录
                                _pendingVerifyClsid = null;
                                return;
                            }
                            _pendingVerifyClsid = null;
                        }
                        _lastNonCs2ActiveClsid = activeClsid;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Status = "ImeSwitcher：异常";
            _log($"异常：{ex.Message}");
            Transitioned?.Invoke(Status);
        }
    }

    /// <summary>退出/停用：若处于切入态先恢复原输入法，避免用户被"钉"在游戏内输入法。</summary>
    public void Disable()
    {
        if (_inCs2)
        {
            _inCs2 = false;
            RestoreBeforeEnter();
        }
        _log("ImeSwitcher：已停用退出");
    }

    // ===== 目标输入法：枚举 / 选择 / 配置 =====

    /// <summary>枚举系统已启用的输入法 profile（TSF 输入处理器类），供托盘菜单选择。</summary>
    public List<(LayoutOrTipProfile Profile, string Name)> ListSelectableTargets()
    {
        var list = new List<(LayoutOrTipProfile, string)>();
        foreach (var p in GetEnabledProfiles())
        {
            if (p.ProfileType != LOTP_INPUTPROCESSOR) continue;
            list.Add((p, GetProfileDescription(p)));
        }
        // 微软拼音排最前（默认推荐）
        list.Sort((a, b) => Weight(a.Item1.Clsid).CompareTo(Weight(b.Item1.Clsid)));
        return list;

        static int Weight(Guid clsid) => clsid == ClsidMicrosoftPinyin ? 0 : 1;
    }

    public bool IsCurrentTarget(LayoutOrTipProfile p) =>
        _targetProfile != null &&
        p.Clsid == _targetProfile.Value.Clsid &&
        p.ProfileGuid == _targetProfile.Value.ProfileGuid;

    /// <summary>托盘选定新目标：保存配置；若此刻在游戏中则立即切换。</summary>
    public void SetTarget(LayoutOrTipProfile p, string name)
    {
        _targetProfile = p;
        _targetName = name;
        _inited = true;
        SaveConfig(p, name);
        _log($"目标输入法改为：{name} clsid={p.Clsid:B} guid={p.ProfileGuid:B} langid={p.LangId}");
        if (_inCs2)
        {
            bool ok = ActivateTip(p);
            _log($"游戏内立即切换到 {name}：{(ok ? "成功" : "失败")}");
        }
        Status = $"ImeSwitcher：目标={name}";
    }

    /// <summary>启动时解析目标：优先 config.json（须仍存在），否则默认微软拼音。</summary>
    private LayoutOrTipProfile? ResolveTarget()
    {
        var all = GetEnabledProfiles().Where(p => p.ProfileType == LOTP_INPUTPROCESSOR).ToList();

        var cfg = LoadConfig();
        if (cfg != null &&
            Guid.TryParse(cfg.Clsid, out var clsid) &&
            Guid.TryParse(cfg.ProfileGuid, out var guid))
        {
            var found = all.FirstOrDefault(p => p.Clsid == clsid && p.ProfileGuid == guid);
            if (found.Clsid == clsid)
            {
                _targetName = string.IsNullOrWhiteSpace(cfg.Name) ? GetProfileDescription(found) : cfg.Name;
                _log($"从配置加载目标：{_targetName}");
                return found;
            }
            _log($"config.json 指向的输入法（{cfg.Name}）已不存在，回退默认微软拼音");
        }

        var def = all.FirstOrDefault(p => p.Clsid == ClsidMicrosoftPinyin);
        if (def.Clsid == ClsidMicrosoftPinyin)
        {
            _targetName = "微软拼音";
            return def;
        }
        return null;
    }

    private static ConfigData? LoadConfig()
    {
        try
        {
            if (!File.Exists(Paths.ConfigFile)) return null;
            return JsonSerializer.Deserialize<ConfigData>(File.ReadAllText(Paths.ConfigFile));
        }
        catch { return null; }
    }

    private void SaveConfig(LayoutOrTipProfile p, string name)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            var cfg = new ConfigData
            {
                Name = name,
                Clsid = p.Clsid.ToString("D"),
                ProfileGuid = p.ProfileGuid.ToString("D"),
                LangId = p.LangId,
            };
            File.WriteAllText(Paths.ConfigFile,
                JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log($"保存配置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 取输入法显示名。实测（本机 26200）GetLanguageProfileDescription 对系统 IME 返回 S_OK 但空串，
    /// 故回退顺序：已知 CLSID 对照表 → COM 描述（部分第三方 IME 有）→ 注册表 CTF\TIP Description → CLSID。
    /// </summary>
    private static string GetProfileDescription(LayoutOrTipProfile p)
    {
        // 1) 已知 CLSID 中文对照表（实测确认的常见输入法）
        if (KnownNames.TryGetValue(p.Clsid.ToString("B"), out var known)) return known;

        // 2) COM GetLanguageProfileDescription（槽 10）
        try
        {
            var profiles = (ITfInputProcessorProfiles)new TfInputProcessorProfiles();
            Guid clsid = p.Clsid, guid = p.ProfileGuid;
            if (profiles.GetLanguageProfileDescription(ref clsid, p.LangId, ref guid, out var desc) == 0 &&
                !string.IsNullOrWhiteSpace(desc))
            {
                return desc;
            }
        }
        catch { }

        // 3) 注册表 HKLM/HKCU\SOFTWARE\Microsoft\CTF\TIP\<clsid>\LanguageProfile\0x<len>\<guid> 的 Description
        try
        {
            string keyPath = $@"SOFTWARE\Microsoft\CTF\TIP\{p.Clsid.ToString("B").ToUpperInvariant()}" +
                             $@"\LanguageProfile\0x{p.LangId:X8}\{p.ProfileGuid.ToString("B").ToUpperInvariant()}";
            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    using var k = baseKey.OpenSubKey(keyPath);
                    if (k?.GetValue("Description") is string s && !string.IsNullOrWhiteSpace(s))
                        return s;
                }
                catch { }
            }
        }
        catch { }

        // 4) 兜底：CLSID 字符串
        return p.Clsid.ToString("B");
    }

    /// <summary>常见输入法 CLSID → 中文名（实测确认）。</summary>
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}"] = "微软拼音",
        ["{86598FB9-66A2-463E-B9C2-AEB906D477AD}"] = "微信输入法",
        ["{6A498709-E00B-4C45-A018-8F9E4081AE40}"] = "微软五笔",
    };

    private string DescribeClsid(string? clsid) =>
        clsid == null ? "英文键盘"
        : clsid.Equals(ClsidMicrosoftPinyin.ToString(), StringComparison.OrdinalIgnoreCase) ? "微软拼音"
        : clsid.Equals(ClsidWeixin.ToString(), StringComparison.OrdinalIgnoreCase) ? "微信"
        : clsid;

    private void RestoreBeforeEnter()
    {
        bool isTarget = _beforeEnterClsid == null ||
                        (_targetProfile != null &&
                         _beforeEnterClsid.Equals(_targetProfile.Value.Clsid.ToString(), StringComparison.OrdinalIgnoreCase));
        if (!isTarget && _beforeEnterClsid != null)
        {
            if (TryFindByClsid(_beforeEnterClsid, out var restore))
            {
                bool ok = ActivateTip(restore);
                string restoreName = restore.Clsid.Equals(ClsidMicrosoftPinyin) ? "微软拼音"
                    : restore.Clsid.Equals(ClsidWeixin) ? "微信" : restore.Clsid.ToString("B");
                Status = ok ? $"ImeSwitcher：已恢复 {restoreName}" : "ImeSwitcher：恢复输入法失败";
                _log($"离开{_targetProcess}：恢复={restoreName} {(ok ? "成功" : "失败")}");
                _pendingVerifyClsid = ok ? _beforeEnterClsid : null;   // 下一拍复核恢复是否真实生效
            }
            else
            {
                Status = "ImeSwitcher：原输入法已卸载，维持当前";
                _log($"离开{_targetProcess}：原输入法已卸载，维持当前");
            }
        }
        else
        {
            Status = $"ImeSwitcher：离开 {_targetProcess}（无需切换）";
            _log($"离开{_targetProcess}：无需切换（beforeEnterClsid={(_beforeEnterClsid ?? "null")}）");
        }
        _beforeEnterClsid = null;
        Transitioned?.Invoke(Status);
    }

    // ===== 前台进程识别 =====

    /// <summary>
    /// 判断前台窗口是否属于目标进程。只用 PROCESS_QUERY_LIMITED_INFORMATION 查映像路径，
    /// 不打开写句柄、不读内存。（原版由覆盖层用纯句柄查找判定后传入，此处独立完成。）
    /// </summary>
    private bool IsTargetForeground()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            uint tid = GetWindowThreadProcessId(hwnd, out uint p);
            if (tid == 0 || p == 0) return false;

            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p);
            if (hProc == IntPtr.Zero) return false;   // 查不到就当不是目标（安全侧）
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (!QueryFullProcessImageName(hProc, 0, sb, ref size)) return false;
                string exeName = Path.GetFileNameWithoutExtension(sb.ToString(0, (int)size));
                return exeName.Equals(_targetProcess, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                CloseHandle(hProc);
            }
        }
        catch { return false; }
    }

    // ===== input.dll 枚举（取正确的 clsid+guidProfile 以切换）=====

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LayoutOrTipProfile
    {
        public uint ProfileType;      // LOTP_INPUTPROCESSOR=1
        public ushort LangId;
        public Guid Clsid;
        public Guid ProfileGuid;
        public Guid CategoryGuid;
        public uint SubstituteLayout;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Id;
    }

    private const uint LOTP_INPUTPROCESSOR = 1;

    private static LayoutOrTipProfile? FindTipByClsid(Guid clsid)
    {
        foreach (var p in GetEnabledProfiles())
            if (p.ProfileType == LOTP_INPUTPROCESSOR && p.Clsid == clsid)
                return p;
        return null;
    }

    private static bool TryFindByClsid(string clsidStr, out LayoutOrTipProfile profile)
    {
        profile = default;
        if (!Guid.TryParse(clsidStr, out var g)) return false;
        var hit = FindTipByClsid(g);
        if (hit == null) return false;
        profile = hit.Value;
        return true;
    }

    private static List<LayoutOrTipProfile> GetEnabledProfiles()
    {
        var list = new List<LayoutOrTipProfile>();
        uint count = EnumEnabledLayoutOrTip(null, null, null, null, 0);
        if (count == 0) return list;
        var profiles = new LayoutOrTipProfile[count];
        uint copied = EnumEnabledLayoutOrTip(null, null, null, profiles, count);
        for (int i = 0; i < (int)copied && i < profiles.Length; i++)
            list.Add(profiles[i]);
        return list;
    }

    // ===== 读取当前激活输入法 =====

    /// <summary>
    /// 返回当前前台线程实际激活的输入法 CLSID（字符串）。
    /// 语言层 GetKeyboardLayout 判中/英；TSF 层用 GetActiveLanguageProfile 对已知 IME 探测激活状态。
    /// </summary>
    private static string? GetActiveInputClsid()
    {
        IntPtr hwnd = GetForegroundWindow();
        uint tid = GetWindowThreadProcessId(hwnd, out _);
        IntPtr hkl = GetKeyboardLayout(tid);
        ushort langId = (ushort)(hkl.ToInt64() & 0xFFFF);

        if (langId == 0x0409) return null; // 英文键盘，无 TSF CLSID

        var profiles = (ITfInputProcessorProfiles)new TfInputProcessorProfiles();
        try
        {
            Guid weixin = ClsidWeixin, msPinyin = ClsidMicrosoftPinyin;
            if (IsActive(profiles, ref msPinyin)) return msPinyin.ToString();
            if (IsActive(profiles, ref weixin)) return weixin.ToString();
        }
        catch { }
        return null;
    }

    /// <summary>GetActiveLanguageProfile 返回 S_OK(0) 表示该 CLSID 输入法当前激活。</summary>
    private static bool IsActive(ITfInputProcessorProfiles profiles, ref Guid clsid)
    {
        int hr = profiles.GetActiveLanguageProfile(ref clsid, out _, out _);
        return hr == 0; // S_OK
    }

    // ===== ActivateProfile(FORSESSION | DONTCARECURRENTINPUTLANGUAGE) 切换 =====

    private static bool ActivateTip(LayoutOrTipProfile p)
    {
        try
        {
            var mgr = (ITfInputProcessorProfileMgr)new TfInputProcessorProfiles();
            Guid clsid = p.Clsid;
            Guid profileGuid = p.ProfileGuid;
            int hr = mgr.ActivateProfile(
                p.ProfileType,
                p.LangId,
                ref clsid,
                ref profileGuid,
                IntPtr.Zero,
                TF_IPPMF_FORSESSION | TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE);
            return hr == 0;
        }
        catch { return false; }
    }

    // ===== 常量与 P/Invoke =====

    private const uint TF_IPPMF_FORSESSION = 0x20000000;
    private const uint TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE = 0x00000004;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("input.dll", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Winapi)]
    private static extern uint EnumEnabledLayoutOrTip(string? pszUserReg, string? pszSystemReg, string? pszSoftwareReg,
        [Out] LayoutOrTipProfile[]? pLayoutOrTipProfile, uint uBufLength);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
}

// ===== TSF COM 接口（签名以 msctf.h 10.0.26100 核对，与原版一致）=====

/// <summary>
/// TSF 输入处理器管理器：切换用 ActivateProfile（槽 0）。
/// 真实 vtable 槽位（0 基，IUnknown 占 0-2）：
///   0 ActivateProfile / 1 DeactivateProfile / 2 GetProfile / 3 EnumProfiles
///   4 ReleaseInputProcessor / 5 RegisterProfile / 6 UnregisterProfile / 7 GetActiveProfile
/// </summary>
[ComImport]
[Guid("71C6E74C-0F28-11D8-A82A-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfInputProcessorProfileMgr
{
    [PreserveSig]
    int ActivateProfile(uint dwProfileType, ushort langId, ref Guid clsid, ref Guid profileGuid, nint hkl, uint dwFlags);
}

/// <summary>TSF 输入处理器配置：读取用 GetActiveLanguageProfile（槽 8）、GetLanguageProfileDescription（槽 10）。</summary>
[ComImport]
[Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfInputProcessorProfiles
{
    // 槽位 0 基顺序需与 msctf.h 一致：
    //   0 Register / 1 Unregister / 2 AddLanguageProfile / 3 RemoveLanguageProfile
    //   4 EnumInputProcessorInfo / 5 GetDefaultLanguageProfile / 6 SetDefaultLanguageProfile
    //   7 ActivateLanguageProfile / 8 GetActiveLanguageProfile
    //   9 EnumLanguageProfiles / 10 GetLanguageProfileDescription
    [PreserveSig] int Register();
    [PreserveSig] int Unregister();
    [PreserveSig] int AddLanguageProfile();
    [PreserveSig] int RemoveLanguageProfile();
    [PreserveSig] int EnumInputProcessorInfo();
    [PreserveSig] int GetDefaultLanguageProfile();
    [PreserveSig] int SetDefaultLanguageProfile();
    [PreserveSig] int ActivateLanguageProfile();
    [PreserveSig]
    int GetActiveLanguageProfile(ref Guid rclsid, out ushort langid, out Guid guidProfile);
    [PreserveSig] int EnumLanguageProfiles();   // 槽 9（未使用，占位）
    [PreserveSig]
    int GetLanguageProfileDescription(ref Guid rclsid, ushort langid, ref Guid guidProfile,
        [MarshalAs(UnmanagedType.BStr)] out string pbstrProfileDesc);
}

/// <summary>TSF 管理器 coclass，同时实现 ITfInputProcessorProfiles 与 ITfInputProcessorProfileMgr。</summary>
[ComImport]
[Guid("33C53A50-F456-4884-B049-85FD643ECFED")]
internal class TfInputProcessorProfiles
{
}
