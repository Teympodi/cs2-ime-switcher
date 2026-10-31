# CS2 IME Switcher（CS2 输入法自动切换器）

一个 179KB 的 Windows 托盘小工具：**前台是 CS2 时自动切到微软拼音，离开 CS2 自动恢复进入前的输入法。**

解决的问题：全屏/独占模式下，Windows 自带的「让我为每个应用窗口使用不同的输入法」经常失灵，导致游戏内打字切不出中文、退出游戏又切不回来。

## 为什么它是安全的

本工具**不提供任何游戏内信息优势**，技术边界如下：

- ❌ 不注入游戏进程
- ❌ 不读写任何进程内存
- ❌ 不 hook 任何 API（无 SetWindowsHookEx）
- ❌ 不联网
- ❌ 无音频采集、无屏幕覆盖层（Overlay）、无鼠标/键盘输入监听

✅ 仅有的系统交互（与 Windows 自带输入法指示器同级）：

1. `GetForegroundWindow` + `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageName` —— 查询前台窗口属于哪个进程（只读映像路径）
2. Windows TSF 输入法 API（`ITfInputProcessorProfiles::GetActiveLanguageProfile` 读取 / `ITfInputProcessorProfileMgr::ActivateProfile` 切换）

> 各对战平台（5E、完美、B5 等）对第三方软件的政策不同，使用前请自行确认所在平台的规则。本工具的实现能力不超出系统输入法指示器，但不为任何平台的判定结果背书。

## 使用

1. 下载 `ImeSwitcher.exe`（或自行编译），双击运行
2. 托盘出现图标即在工作；悬浮可见当前状态
3. 打开 CS2 → 自动切微软拼音；切出 → 自动恢复
4. 右键托盘图标 → 退出（退出时若在游戏中会先恢复你的输入法）

- 目标进程默认为 `cs2`，要支持其它程序改 `Program.cs` 里的 `TargetProcess` 常量重新编译即可
- 目标输入法默认微软拼音（CLSID 已实测），改 `ClsidMicrosoftPinyin` 常量可换
- 日志：`%LOCALAPPDATA%\ImeSwitcher\ime_switch.log`
- 单实例运行，重复双击只会提示

## 编译

需要 [.NET 10 SDK](https://dotnet.microsoft.com/)：

```bash
dotnet publish -c Release
```

产物为单文件 exe（依赖本机 .NET 10 桌面运行时）。

## 技术说明

TSF COM 接口签名单独声明（未引用 COM 包装），vtable 槽位按 Windows SDK 10.0.26100 的 `msctf.h` 核对：

- 读取激活输入法：`ITfInputProcessorProfiles::GetActiveLanguageProfile`（槽 8），按 CLSID 探测
- 切换：`ITfInputProcessorProfileMgr::ActivateProfile`（槽 0），flags = `TF_IPPMF_FORSESSION | TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE`（后者避免跨语言切换静默失败）
- 已启用的输入法 profile 通过 `input.dll!EnumEnabledLayoutOrTip` 枚举，拿到正确的 clsid + guidProfile 组合

## License

MIT
