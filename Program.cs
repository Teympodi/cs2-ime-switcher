using System.Runtime.InteropServices;
using System.Text;

namespace ImeSwitcher;

/// <summary>
/// ImeSwitcher —— 从 SoundViz.Overlay 抽出的独立小工具（v1.0.0）。
///
/// 职责只有一件事：前台是 cs2 时切到微软拼音，离开 CS2 恢复进入前的输入法。
///
/// 【与 SoundViz 的区别（风控画像考量）】
///   - 不含音频采集（无 WASAPI）、无 DirectX/D2D 覆盖层、无 Raw Input 鼠标/键盘监听
///   - 进程名不带 Overlay 字样
///   - 仅有的系统交互：
///       1) GetForegroundWindow + OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) 识别前台进程名
///          （只查询进程映像路径，不读写目标进程内存）
///       2) TSF 输入法 API（读取/激活输入法 profile）
///   - 这两点与 Windows 自带输入法指示器的能力范围相同。
///
/// 【移植来源】SoundViz.Overlay/ImeAutoSwitcher.cs（TSF 签名按 msctf.h 10.0.26100 核对）。
/// 差异：原版由 D2D 覆盖层帧循环调用 Tick(cs2Present)；本工具自带前台检测，用
/// WinForms Timer（UI 线程、500ms 限频）驱动。
/// </summary>
internal static class Program
{
    // 目标进程（前台为该进程时切微软拼音）。如需支持其它进程，改这里。
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
        menu.Items.Add("ImeSwitcher v1.0.0", null, (_, _) => { }).Enabled = false;
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

        Log($"ImeSwitcher 启动：目标进程={TargetProcess}.exe，目标输入法=微软拼音");
        Application.Run();

        SingleInstance.ReleaseMutex();
    }

    // ===== 文件日志 =====

    private static readonly object LogLock = new();

    /// <summary>写一行到 %LOCALAPPDATA%\ImeSwitcher\ime_switch.log（与 SoundViz 的日志文件分离）。</summary>
    private static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ImeSwitcher");
                Directory.CreateDirectory(dir);
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}";
                File.AppendAllText(Path.Combine(dir, "ime_switch.log"), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}

/// <summary>
/// 输入法自动切换核心：前台进程为目标（cs2）时切微软拼音，离开时恢复进入前的输入法。
/// 移植自 ImeAutoSwitcher.cs，逻辑保持一致：
///   - 读取：GetKeyboardLayout 判语言层；TSF GetActiveLanguageProfile 按 CLSID 探测激活状态
///   - 切换：ITfInputProcessorProfileMgr::ActivateProfile（FORSESSION | DONTCARECURRENTINPUTLANGUAGE）
/// </summary>
internal sealed class ImeSwitcherCore
{
    // 目标输入法 CLSID（实测确认，与原版一致）
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

    // 目标（微软拼音）profile（枚举得到 clsid+guidProfile）
    private LayoutOrTipProfile? _pinyin;

    /// <summary>托盘提示用的状态描述。</summary>
    public string Status { get; private set; } = "ImeSwitcher：运行中";

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
                _pinyin = FindTipByClsid(ClsidMicrosoftPinyin);
                _inited = true;
                if (_pinyin == null)
                {
                    Status = "ImeSwitcher：未找到微软拼音";
                    _log($"初始化失败：枚举不到微软拼音 {ClsidMicrosoftPinyin:B}");
                    return;
                }
                var pinyinInit = _pinyin.Value;
                _log($"初始化：目标=微软拼音 clsid={ClsidMicrosoftPinyin:B} guid={pinyinInit.ProfileGuid:B}");
            }

            // _inited 成功后 _pinyin 必非空（失败已提前 return）
            LayoutOrTipProfile pinyin = _pinyin!.Value;

            bool isTarget = IsTargetForeground();
            if (isTarget && !_inCs2)
            {
                // 进入目标进程：锁定进入前最后一次记录的输入法，再切微软拼音
                _inCs2 = true;
                _beforeEnterClsid = _lastNonCs2ActiveClsid;
                string? cur = GetActiveInputClsid();
                string curName = DescribeClsid(cur);
                _log($"切入{_targetProcess}前台：当前输入法=[{curName}] 快照记录=[{(_beforeEnterClsid ?? "无记录")}]");
                bool ok = ActivateTip(pinyin);
                Status = ok ? $"ImeSwitcher：进入 {_targetProcess} → 微软拼音"
                            : "ImeSwitcher：切换微软拼音失败";
                _log($"进入{_targetProcess}：切微软拼音 {(ok ? "成功" : "失败")}");
            }
            else if (isTarget)
            {
                // 仍在目标进程中：不更新 _lastNonCs2ActiveClsid，避免被焦点内输入法污染
            }
            else
            {
                // 非目标前台：持续记录用户当时激活的输入法，供下次进入时恢复
                string? activeClsid = GetActiveInputClsid();
                if (activeClsid != null) _lastNonCs2ActiveClsid = activeClsid;

                if (_inCs2)
                {
                    _inCs2 = false;
                    RestoreBeforeEnter();
                }
            }
        }
        catch (Exception ex)
        {
            Status = "ImeSwitcher：异常";
            _log($"异常：{ex.Message}");
        }
    }

    /// <summary>退出/停用：若处于切入态先恢复原输入法，避免用户被"钉"在微软拼音。</summary>
    public void Disable()
    {
        if (_inCs2)
        {
            _inCs2 = false;
            RestoreBeforeEnter();
        }
        _log("ImeSwitcher：已停用退出");
    }

    private string DescribeClsid(string? clsid) =>
        clsid == null ? "英文键盘"
        : clsid.Equals(ClsidMicrosoftPinyin.ToString(), StringComparison.OrdinalIgnoreCase) ? "微软拼音"
        : clsid.Equals(ClsidWeixin.ToString(), StringComparison.OrdinalIgnoreCase) ? "微信"
        : clsid;

    private void RestoreBeforeEnter()
    {
        bool isPinyin = _beforeEnterClsid == null ||
                        _beforeEnterClsid.Equals(ClsidMicrosoftPinyin.ToString(), StringComparison.OrdinalIgnoreCase);
        if (!isPinyin && _beforeEnterClsid != null)
        {
            if (TryFindByClsid(_beforeEnterClsid, out var target))
            {
                bool ok = ActivateTip(target);
                string targetName = target.Clsid.Equals(ClsidMicrosoftPinyin) ? "微软拼音"
                    : target.Clsid.Equals(ClsidWeixin) ? "微信" : target.Clsid.ToString("B");
                Status = ok ? $"ImeSwitcher：已恢复 {targetName}" : "ImeSwitcher：恢复输入法失败";
                _log($"离开{_targetProcess}：恢复={targetName} {(ok ? "成功" : "失败")}");
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
            uint pid = GetWindowThreadProcessId(hwnd, out uint p);
            if (pid == 0 || p == 0) return false;

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
    private struct LayoutOrTipProfile
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

    private static LayoutOrTipProfile? FindTipByClsid(Guid clsid) =>
        GetEnabledProfiles().FirstOrDefault(p => p.ProfileType == LOTP_INPUTPROCESSOR && p.Clsid == clsid);

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

/// <summary>TSF 输入处理器配置：读取用 GetActiveLanguageProfile（槽 8）。</summary>
[ComImport]
[Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfInputProcessorProfiles
{
    // 槽位 0 基顺序需与 msctf.h 一致（仅到用到的 GetActiveLanguageProfile）：
    //   0 Register / 1 Unregister / 2 AddLanguageProfile / 3 RemoveLanguageProfile
    //   4 EnumInputProcessorInfo / 5 GetDefaultLanguageProfile / 6 SetDefaultLanguageProfile
    //   7 ActivateLanguageProfile / 8 GetActiveLanguageProfile
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
}

/// <summary>TSF 管理器 coclass，同时实现 ITfInputProcessorProfiles 与 ITfInputProcessorProfileMgr。</summary>
[ComImport]
[Guid("33C53A50-F456-4884-B049-85FD643ECFED")]
internal class TfInputProcessorProfiles
{
}
