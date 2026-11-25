/* AhoChat Web UI · i18n
 * 中文为源文案，英文词典随附；按浏览器语言自动切换（可用 ?lang=zh|en 或 localStorage['ahochat.lang'] 覆盖）。
 * Chinese is the source text; English dictionary bundled; switches by browser language
 * (override with ?lang=zh|en or localStorage['ahochat.lang']). */
(function () {
  'use strict';
  var DICT = {
    "' + (g.members || 0) + ' 人": "' + (g.members || 0) + ' members",
    "' + (it.isDir ? '目录' : sizeText(it.size)) + '": "' + (it.isDir ? 'Directory' : sizeText(it.size)) + '",
    "(已编辑)": "(edited)",
    "(未知)": "(Unknown)",
    "(自己)": "(self)",
    "</h2><div class=\"r-ch-body r-loading\">加载中…</div>": "</h2><div class=\"r-ch-body r-loading\">Loading…</div>",
    "<div class=\"fc-act\">打开</div>": "<div class=\"fc-act\">Open</div>",
    "<div class=\"fc-act\">添加</div>": "<div class=\"fc-act\">Add</div>",
    "<div class=\"mc-artist\">点击播放</div>": "<div class=\"mc-artist\">Tap to play</div>",
    "<div class=\"muted\">精度:": "<div class=\"muted\">Accuracy: ",
    "<div class=\"r-empty-sub\">无法访问共享目录（需先在 Mchat 授予文件访问权限）</div>": "<div class=\"r-empty-sub\">Cannot access shared directory (grant file access in AhoChat first)</div>",
    "<div class=\"r-loading\">正在从": "<div class=\"r-loading\">Fetching from ",
    "<div class=\"r-loading\">正在获取目录…</div>": "<div class=\"r-loading\">Fetching contents…</div>",
    "<div class=\"r-loading\">目录为空，该书源规则可能未匹配到章节</div>": "<div class=\"r-loading\">Table of contents is empty; the source rules may not have matched any chapters</div>",
    "<div class=\"r-loading\">获取目录失败：": "<div class=\"r-loading\">Failed to get contents: ",
    "<div class=\"tools-empty\">当前环境不支持</div>": "<div class=\"tools-empty\">Not supported in this environment</div>",
    "<div class=\"tools-empty\">暂无群组</div>": "<div class=\"tools-empty\">No groups</div>",
    "<div class=\"tools-empty\">频道未就绪</div>": "<div class=\"tools-empty\">Channel not ready</div>",
    "<div class=\"tools-empty\">（空目录）</div>": "<div class=\"tools-empty\">(Empty directory)</div>",
    "<div class=\"tools-sub\">链路</div>": "<div class=\"tools-sub\">Links</div>",
    "<div style=\"padding: 40px; text-align: center; font-family: system-ui;\">网络已恢复，<a href=\"/\" style=\"color: #007aff;\">刷新页面</a></div>": "<div style=\"padding: 40px; text-align: center; font-family: system-ui;\">Network restored, <a href=\"/\" style=\"color: #007aff;\">Refresh page</a></div>",
    "<div>坐标:": "<div>Coordinates:",
    "<div>对方朝向:": "<div>Peer heading: ",
    "<div>水平距离:": "<div>Horizontal distance: ",
    "<div>电量:": "<div>Battery:",
    "<div>直线距离:": "<div>Straight-line distance: ",
    "<div>相对方位:": "<div>Relative bearing: ",
    "<div>高度差:": "<div>Height difference: ",
    "<li class=\"bt-empty\">未发现设备，请稍候…</li>": "<li class=\"bt-empty\">No devices found, please wait…</li>",
    "<p style=\"color:#c0392b\">本章加载失败：": "<p style=\"color:#c0392b\">Failed to load this chapter: ",
    "<p>（本章无正文）</p>": "<p>(No content in this chapter)</p>",
    "<span style=\"color:#7dd8ff\">(自己)</span>": "<span style=\"color:#7dd8ff\">(me)</span>",
    "></span> 上传<input type=": "></span> Upload<input type=",
    "><span>qaul 公钥指纹</span><b id=": "><span>qaul public key fingerprint</span><b id=",
    "><span>一起听（分享播放列表）</span><b id=": "><span>Listen together (share playlist)</span><b id=",
    "><span>书签</span><button class=": "><span>Bookmarks</span><button class=",
    "><span>从共享目录导入</span><button class=": "><span>Import from shared directory</span><button class=",
    "><span>双通道</span><b id=": "><span>Dual channel</span><b id=",
    "><span>显示名称</span><b id=": "><span>Display name</span><b id=",
    "><span>阅读设置</span><button class=": "><span>Reading settings</span><button class=",
    ">目录</span><button class=": ">Contents</span><button class=",
    ">编辑书源</span><button class=": ">Edit source</span><button class=",
    ">网络已恢复，<a href=": ">Network restored, <a href=",
    ">默认直接保存到公共下载目录（/sdcard/Download）。若自定义到其它公共目录，需先点": ">Files are saved to the public Downloads directory (/sdcard/Download) by default. To use another public directory, first tap ",
    "EPUB 未解析到章节": "No chapters parsed from EPUB",
    "EPUB 缺少 container.xml": "EPUB is missing container.xml",
    "GPS 共享位置": "GPS shared location",
    "JSON 解析失败：": "JSON parse failed: ",
    "Web Bluetooth Polyfill: navigator.bluetooth 已就绪": "Web Bluetooth Polyfill: navigator.bluetooth is ready",
    "WiFi局域网": "WiFi LAN",
    "[App] 网络已断开": "[App] Network disconnected",
    "[App] 网络已连接": "[App] Network connected",
    "[Keypad] Bridge.send 异常:": "[Keypad] Bridge.send error: ",
    "[Keypad] Bridge.send 返回:": "[Keypad] Bridge.send returned: ",
    "[Keypad] window.Bridge 未定义": "[Keypad] window.Bridge is undefined",
    "[Keypad] window.Bridge.send 不是函数": "[Keypad] window.Bridge.send is not a function",
    "[Keypad] 发送内容:": "[Keypad] Sending content: ",
    "[Keypad] 发送按键:": "[Keypad] Sending key: ",
    "[Keypad] 按键大全元素不存在，跳过初始化": "[Keypad] Keypad element not found, skipping init",
    "[Keypad] 按键大全已初始化，支持自动组合键检测": "[Keypad] Keypad initialized, auto shortcut detection enabled",
    "[SW] Service Worker 已加载": "[SW] Service Worker loaded",
    "[SW] 删除旧缓存:": "[SW] Deleting old cache: ",
    "[SW] 后台同步消息": "[SW] Syncing messages in background",
    "[SW] 旧缓存清理完成": "[SW] Old cache cleanup complete",
    "[SW] 正在预缓存静态资源...": "[SW] Pre-caching static assets...",
    "[SW] 注册失败:": "[SW] Registration failed: ",
    "[SW] 注册成功:": "[SW] Registration succeeded: ",
    "[SW] 静态资源预缓存完成": "[SW] Static asset precache complete",
    "[SW] 预缓存失败:": "[SW] Precache failed: ",
    "qaul 公钥指纹": "qaul public key fingerprint",
    "qaul 收/发": "qaul send/receive",
    "qaul 端口": "qaul port",
    "qaul 节点": "qaul node",
    "qaul 运行": "qaul running",
    "· 收": "· Receive",
    "· 更新:": "· Update:",
    "·发现": "·Discover",
    "″（点击/松手发送）": "″ (tap/release to send)",
    "‹ 聊天": "‹ Chat",
    "」的全部消息？在线设备会同步删除。": "」? Online devices will delete them too.",
    "一起听": "Listen together",
    "一起听不可用": "Listen Together unavailable",
    "一起听已关闭": "Listen Together turned off",
    "一起听已开启": "Listen Together turned on",
    "一起听（分享播放列表）": "Listen together (share playlist)",
    "一起看": "Watch together",
    "上一曲": "Previous track",
    "上传": "Upload",
    "上传失败": "Upload failed",
    "上级": "Parent",
    "上页": "Previous page",
    "下一曲": "Next track",
    "下页": "Next page",
    "不在局域网": "Not on LAN",
    "不支持的压缩方式:": "Unsupported compression method: ",
    "不支持的地址（仅支持 http/https）：": "Unsupported address (only http/https supported): ",
    "不是有效的 EPUB/ZIP 文件": "Not a valid EPUB/ZIP file",
    "东西：": "East-west:",
    "两端对齐": "Justify",
    "个书源": "book sources",
    "个书源搜索…</div>": " book sources…</div>",
    "为参照中心": "as reference center",
    "主页": "Home",
    "主题": "Theme",
    "书": "Book",
    "书架": "Bookshelf",
    "书架还是空的": "Bookshelf is empty",
    "书源": "Book source",
    "书源管理": "Book source management",
    "书签": "Bookmark",
    "人</span>": "people</span>",
    "仅在 App 内支持录音": "Recording is only supported in the app",
    "仅在 App 内支持通话": "Calls are only supported in the app",
    "仅支持 TXT / EPUB": "Only TXT / EPUB supported",
    "仅支持浏览 http/https 网页": "Only http/https pages can be browsed",
    "从共享目录": "From shared folder",
    "从共享目录导入": "Import from shared folder",
    "任意加载的网页": "Any loaded web page",
    "会话列表": "Conversation list",
    "位置": "Location",
    "位置共享": "Location sharing",
    "位置接口不可用": "Location API unavailable",
    "佚名": "Anonymous",
    "使用本地文件管理功能": "Use local file manager",
    "例：\\s*广告\\s*\\n  （每行一条，格式 正则 或 正则→替换）": "e.g. \\s*ads\\s*\\n  (one per line, as regex or regex→replacement)",
    "侧栏": "Sidebar",
    "保存": "Save",
    "保存净化规则": "Save cleaning rules",
    "保存失败：": "Save failed: ",
    "保存目录": "Save folder",
    "保存设置": "Save settings",
    "停止": "Stop",
    "停用": "Disable",
    "共享": "Share",
    "共享位置": "Share location",
    "关闭": "Close",
    "关闭共享": "Stop sharing",
    "内容为空": "Content is empty",
    "净化规则（逐行正则→替换，留空=删除）": "Cleanup rules (one regex per line → replace, blank = delete)",
    "分享的视频": "Shared video",
    "分页": "Pagination",
    "切屏": "Switch screen",
    "切换": "Switch",
    "列表": "List",
    "创建失败（需 qaul 在线）": "Create failed (qaul must be online)",
    "创建新的聊天会话": "Create a new chat conversation",
    "删除": "Delete",
    "删除(仅本机)": "Delete (this device only)",
    "删除该书源？": "Delete this book source?",
    "删除这条消息？双方都会删除。": "Delete this message? It will be deleted on both sides.",
    "刷新": "Refresh",
    "刷新页面": "Refresh page",
    "前往": "Go",
    "剪切": "Cut",
    "功能控制": "Feature controls",
    "功能控制菜单": "Feature controls menu",
    "加入房间": "Join room",
    "加载中…": "Loading…",
    "加载失败": "Failed to load",
    "加载更多...": "Load more...",
    "南北：": "North-south:",
    "去开启": "Turn on",
    "去授权": "Authorize",
    "双通道": "Dual channel",
    "双通道已关闭": "Dual channel off",
    "双通道已开启": "Dual channel on",
    "发": "Send",
    "发现新版本，是否立即更新？": "New version found, update now?",
    "发起端不在线，无法一起看": "Initiator is offline, cannot watch together",
    "发送": "Send",
    "发送: \"": "Sent: \"",
    "发送图片": "Send image",
    "发送失败": "Failed to send",
    "发送失败，内容已保留，点发送可重试": "Send failed, content kept. Tap Send to retry",
    "发送文件": "Send file",
    "发送文件给局域网设备": "Send files to LAN devices",
    "发送视频": "Send video",
    "发送通知": "Send notification",
    "发送链接": "Send link",
    "发送音乐": "Send music",
    "取消": "Cancel",
    "只看到文字": "Text only",
    "台设备": "devices",
    "台：": "Devices:",
    "后退": "Back",
    "向东": "East",
    "向北": "North",
    "向南": "South",
    "向西": "West",
    "否": "No",
    "启用": "Enable",
    "图片": "Image",
    "图片预览": "Image preview",
    "在线": "Online",
    "在线书源": "Online book sources",
    "地址栏": "Address bar",
    "坐标: ' + p.lat.toFixed(6) + ', ' + p.lon.toFixed(6) + (p.alt ? ' (' + Math.round(p.alt) + 'm)' : '') + '": "Coordinates: ' + p.lat.toFixed(6) + ', ' + p.lon.toFixed(6) + (p.alt ? ' (' + Math.round(p.alt) + 'm)' : '') + '",
    "基本同轴（0 m）": "Nearly coaxial (0 m)",
    "复制": "Copy",
    "复制文本": "Copy text",
    "多人同屏 · 距离/高度 · 朝向": "Multi-user on screen · Distance/height · Heading",
    "媒体": "Media",
    "媒体库": "Media library",
    "字体": "Font",
    "字号": "Font size",
    "存储权限": "Storage permission",
    "定位不可用": "Location unavailable",
    "定位中…": "Locating…",
    "对方朝向: ' + Math.round(p.bearing) + '°": "Peer heading: ' + Math.round(p.bearing) + '°",
    "对端": "Peer",
    "导入书源 JSON": "Import book source JSON",
    "导入失败：": "Import failed: ",
    "导入完成": "Import complete",
    "导出": "Export",
    "导航菜单": "Navigation menu",
    "小说 · 书源 · 书架": "Novels · Sources · Shelf",
    "尚未授予悬浮窗权限，请先授权": "Overlay permission not granted, please grant it first",
    "尾页": "Last page",
    "局域网即时通讯与媒体共享应用": "LAN instant messaging and media sharing app",
    "局域网消息": "LAN messages",
    "局域网端口": "LAN port",
    "屏幕画板": "Screen canvas",
    "工具": "Tools",
    "工具分类": "Tool categories",
    "工具面板": "Tool panel",
    "已": "Done",
    "已上传：": "Uploaded: ",
    "已保存": "Saved",
    "已保存（原生桥）": "Saved (native bridge)",
    "已保存（自动接收关闭）": "Saved (auto-receive off)",
    "已停止共享（保留上次数据，仍可接收他人位置）": "Sharing stopped (last data kept; you can still receive others' locations)",
    "已关闭": "Closed",
    "已加入一起听，正在对齐播放进度…": "Joined listen together, syncing playback…",
    "已加入局域网，设备名：": "Joined LAN, device name: ",
    "已在网页端播放共享列表": "Shared playlist is playing on the web client",
    "已处理": "Handled",
    "已复制": "Copied",
    "已导入": "Imported",
    "已开启": "On",
    "已开启自动接收": "Auto receive enabled",
    "已授予悬浮窗权限，打开开关即可显示": "Overlay permission granted, turn on the switch to show it",
    "已播放共享歌单（可自由切歌）": "Shared playlist is playing (skip freely)",
    "已是最后一章": "Already at last chapter",
    "已是第一章": "Already at first chapter",
    "已暂停": "Paused",
    "已添加书签": "Bookmark added",
    "已移除": "Removed",
    "已跳转授权页": "Redirected to authorization page",
    "已进入房间": "Joined room",
    "已退出一起听": "Left Listen Together",
    "应用已更新，是否刷新？": "App updated, refresh now?",
    "开启共享": "Enable sharing",
    "开启后本机播放音乐时，会把整条播放列表连同封面、歌词一起发给局域网设备；关闭后音乐卡片照常更新，但不再附带列表。": "When on, playing music on this device sends the whole playlist along with covers and lyrics to LAN devices; when off, music cards still update but no longer include the playlist.",
    "开启需要授予悬浮窗权限": "Requires overlay permission to enable",
    "当前 WebView 不支持 DecompressionStream，无法解压 EPUB": "This WebView does not support DecompressionStream, cannot unpack EPUB",
    "当前处于离线状态": "Currently offline",
    "当前局域网内没有其他设备": "No other devices on this LAN",
    "当前时间": "Current time",
    "当前环境不支持": "Not supported in this environment",
    "当前环境不支持 IndexedDB": "IndexedDB is not supported in this environment",
    "当前环境不支持导入": "Import is not supported in this environment",
    "当前环境不支持录音": "Recording is not supported in this environment",
    "当前环境不支持打开该文件": "Opening this file is not supported in this environment",
    "当前环境不支持朗读": "Read-aloud is not supported in this environment",
    "录制中": "Recording",
    "影音": "Audio/video",
    "待机": "Standby",
    "思源宋体（衬线）": "Source Han Serif (serif)",
    "总时长": "Total duration",
    "恢复连接后：": "After reconnecting: ",
    "恢复通话": "Resume call",
    "悬浮窗": "Floating window",
    "悬浮窗开关": "Floating window toggle",
    "悬浮窗设置": "Floating window settings",
    "悬浮窗运行中：可拖拽浮标，点击展开面板": "Overlay running: drag the bubble, tap to open the panel",
    "悬浮窗（快速收发消息 / 媒体控制键）": "Overlay (quick messages / media controls)",
    "我发送的通知": "Notifications I sent",
    "房间号": "Room ID",
    "打印": "Print",
    "打开": "Open",
    "打开侧栏": "Open sidebar",
    "扫描结束，点击设备连接（可重新扫描）": "Scan finished, tap a device to connect (you can rescan)",
    "挂断": "Hang up",
    "按下的键:": "Key pressed:",
    "按住说话": "Hold to talk",
    "按键大全": "Key reference",
    "按键网格": "Key grid",
    "授予文件访问": "Grant file access",
    "排版": "Layout",
    "接听": "Answer",
    "控制拨盘": "Control dial",
    "控制选项": "Control options",
    "插入": "Insert",
    "搜索": "Search",
    "搜索书籍": "Search books",
    "撤回": "Recall",
    "撤回这条消息？双方都会删除。": "Recall this message? It will be deleted on both sides.",
    "播放": "Play",
    "播放/暂停": "Play/Pause",
    "播放失败": "Playback failed",
    "播放暂停": "Play / Pause",
    "播放进度": "Playback progress",
    "操作失败": "Operation failed",
    "支持标准 legado 书源格式：bookSourceUrl / bookSourceName / searchUrl / ruleSearch / ruleBookInfo / ruleToc / ruleContent / header 等。联网抓取由本机代理完成，无需 legado 服务器。": "Supports the standard legado source format: bookSourceUrl / bookSourceName / searchUrl / ruleSearch / ruleBookInfo / ruleToc / ruleContent / header, etc. Online fetching is done by the local proxy, no legado server needed.",
    "收到任意文件后自动保存": "Auto-save any received file",
    "改名": "Rename",
    "放映": "Slideshow",
    "文件": "File",
    "文件传输": "File transfer",
    "文件传输仅支持 qaul 会话（qaul 私聊 / qaul 群 / 频道）": "File transfer only works in qaul sessions (qaul DM / qaul group / channel)",
    "文本": "Text",
    "新建": "New",
    "新建书源": "New book source",
    "新建会话": "New conversation",
    "新消息": "New message",
    "新消息将自动同步": "New messages will sync automatically",
    "新群组名称": "New group name",
    "新通知": "New notification",
    "无效的数据类型": "Invalid data type",
    "无法定位 OPF": "Cannot locate OPF",
    "无法访问共享目录（需先在 Mchat 授予文件访问权限）": "Cannot access shared directory (grant file access in AhoChat first)",
    "无法连接位置服务：": "Cannot connect to location services: ",
    "无网络·未扫描局域网": "No network · LAN not scanned",
    "日间/夜间主题": "Day/night theme",
    "是": "Yes",
    "显示名称": "Display name",
    "暂停": "Pause",
    "暂无书源，点击「新建书源」或「导入书源 JSON」": "No sources yet, tap \"New source\" or \"Import source JSON\"",
    "暂无书签": "No bookmarks",
    "暂无其他在线设备": "No other online devices",
    "暂无内嵌歌词": "No embedded lyrics",
    "暂无群组": "No groups",
    "更多": "More",
    "最小化": "Minimize",
    "最小化通话": "Minimize call",
    "朗读": "Read aloud",
    "未发现设备，请稍候…": "No devices found, please wait…",
    "未命名": "Untitled",
    "未开启通知转发：收到系统通知会自动作为消息发出": "Notification forwarding off: system notifications will be sent as messages automatically",
    "未找到结果，或书源规则需调整": "No results found, or the source rules need adjusting",
    "未知": "Unknown",
    "未知歌曲": "Unknown song",
    "未知艺术家": "Unknown artist",
    "未知设备": "Unknown device",
    "未连接 WiFi": "Not connected to WiFi",
    "本…": "This…",
    "本地": "Local",
    "本机无 GPS · 以": "No GPS on this device · using ",
    "本机：": "This device:",
    "本章加载失败：' + escapeHtml(e.message) + '": "Failed to load this chapter: ' + escapeHtml(e.message) + '",
    "条净化规则": "cleaning rules",
    "来电…": "Incoming call…",
    "松手发送": "Release to send",
    "查看已缓存的图片和文件": "View cached images and files",
    "查看已缓存的聊天记录": "View cached chat history",
    "桌面": "Desktop",
    "桌面1": "Desktop 1",
    "桌面2": "Desktop 2",
    "桌面3": "Desktop 3",
    "桌面4": "Desktop 4",
    "楷体": "KaiTi",
    "歌词": "Lyrics",
    "正在从 ' + sources.length + ' 个书源搜索…": "Searching ' + sources.length + ' book sources…",
    "正在呼叫": "Calling",
    "正在导入": "Importing",
    "正在扫描…": "Scanning…",
    "正在搜索设备…": "Searching for devices…",
    "正在播放": "Now playing",
    "正在获取 GPS 定位…": "Getting GPS fix…",
    "正在获取目录…": "Fetching folder…",
    "正在获取详情…": "Fetching details…",
    "正在读取": "Reading",
    "正在连接…": "Connecting…",
    "正文": "Body",
    "段距": "Paragraph spacing",
    "水平距离: ' + (m.horiz": "Horizontal distance: ' + (m.horiz",
    "没有设备": "No devices",
    "注入到每个文档的": "Injected into each document",
    "浮窗可拖拽移动，点击展开：预览最近消息、输入发送、控制播放/音量，重启 App 后按开关状态自动恢复。": "The floating window can be dragged; tap to expand: preview recent messages, send messages, control playback/volume. After an app restart it restores automatically according to the switch state.",
    "消息列表": "Message list",
    "添加": "Add",
    "清空「": "Clear \"",
    "滚动": "Scroll",
    "点击「导入本地书籍」添加 TXT / EPUB，或到「书源」添加在线书源后联网搜索。": "Tap \"Import local books\" to add TXT / EPUB, or add an online source in \"Sources\" and search online.",
    "点击按键发送指令 · 也可直接按键盘组合键": "Tap a key to send a command · you can also press a keyboard shortcut",
    "点击按键或按下键盘组合键": "Tap a key or press a keyboard shortcut",
    "点击播放": "Tap to play",
    "点击添加到本机": "Tap to add to this device",
    "热点局域网": "Hotspot LAN",
    "用默认目录": "Use default folder",
    "申请所有文件访问权限": "Request all files access",
    "电量: ' + p.battery + '%": "Battery: ' + p.battery + '%",
    "留空使用默认目录": "Leave blank to use the default directory",
    "目录": "Contents",
    "目录为空，该书源规则可能未匹配到章节": "Table of contents is empty; the source rules may not have matched any chapters",
    "直线距离: ' + (m.straight": "Straight-line distance: ' + (m.straight",
    "相对方位: ' + Math.round(m.bearing) + '°": "Relative bearing: ' + Math.round(m.bearing) + '°",
    "确定从书架移除该书？": "Remove this book from the shelf?",
    "确认": "Confirm",
    "示例": "Example",
    "示例·笔趣阁": "Example · Biquge",
    "示例·笔趣阁风格（默认关闭，请按实际站点调整规则后启用）": "Example · Biquge style (off by default; adjust the rules for your site before enabling)",
    "离线": "Offline",
    "离线 - Mchat": "Offline - AhoChat",
    "离线期间的操作将同步上传": "Actions taken offline will sync later",
    "离线模式下可用功能：": "Available in offline mode: ",
    "移动1": "Mobile 1",
    "移动2": "Mobile 2",
    "移动3": "Mobile 3",
    "移动4": "Mobile 4",
    "移动数据·未扫描局域网": "Mobile data · LAN not scanned",
    "空格": "Space",
    "空目录": "Empty folder",
    "章": "Chapter",
    "第": "No.",
    "等待位置数据…": "Waiting for location data…",
    "等待手机上报位置…点击\"开启共享\"或\"请求位置\"": "Waiting for phone to report location… tap \"Start sharing\" or \"Request location\"",
    "等待手机上报位置…点击「开启共享」或「请求位置」": "Waiting for phone to report location… tap \"Start sharing\" or \"Request location\"",
    "粘贴": "Paste",
    "粘贴 legado 书源 JSON（单个对象或数组）": "Paste legado source JSON (single object or array)",
    "精度: ' + (p.acc ? Math.round(p.acc) + 'm' : '未知') + ' · 更新: ' + new Date(p.time).toLocaleTimeString() + '": "Accuracy: ' + (p.acc ? Math.round(p.acc) + 'm' : 'unknown') + ' · Updated: ' + new Date(p.time).toLocaleTimeString() + '",
    "系统默认": "System default",
    "纬度": "Latitude",
    "编辑": "Edit",
    "编辑中：修改后发送即更新该消息": "Editing: send to update this message",
    "编辑书源": "Edit book source",
    "编辑失败，内容已保留": "Edit failed, content kept",
    "网址或搜索关键词": "URL or search keywords",
    "网格": "Grid",
    "网络已恢复，": "Network restored, ",
    "网络连接不可用，无法加载页面内容。": "Network unavailable, cannot load page content.",
    "网络错误": "Network error",
    "网络错误：": "Network error: ",
    "网页端视图 / 通话浮层": "Web view / Call overlay",
    "网页链接": "Web link",
    "网页（": "Web page (",
    "网页（在上方输入网址或关键词打开）": "Web (enter a URL or keywords above to open)",
    "群组": "Group",
    "群组已创建": "Group created",
    "群组新名称": "New group name",
    "翻页模式": "Page turn mode",
    "聊天": "Chat",
    "聊天界面": "Chat interface",
    "自动接收": "Auto receive",
    "自动接收局域网文件": "Auto-receive LAN files",
    "自己": "Self",
    "节": "Section",
    "获取目录失败：' + escapeHtml(e.message) + '": "Failed to get contents: ' + escapeHtml(e.message) + '",
    "获取详情失败：": "Failed to get details: ",
    "蓝牙设备列表": "Bluetooth device list",
    "虚拟自己": "Virtual self",
    "行距": "Line spacing",
    "衬线 Serif": "Serif",
    "观看": "Watch",
    "视频": "Video",
    "视频播放器": "Video player",
    "设置": "Settings",
    "设置 · 自动接收文件": "Settings · Auto-receive files",
    "语音": "Voice",
    "语音输入": "Voice input",
    "语音通话": "Voice call",
    "请先在「书源」启用带搜索的书源": "Enable a source with search in \"Sources\" first",
    "请在系统设置中授予文件访问权限": "Grant file access permission in system settings",
    "请求位置": "Request location",
    "请输入群组名称": "Enter a group name",
    "读取失败：": "Read failed: ",
    "读取文件失败": "Failed to read file",
    "读取设置失败：": "Failed to read settings: ",
    "调试端": "Debug client",
    "身份": "Identity",
    "身份已轮换": "Identity rotated",
    "转发到宿主失败": "Failed to forward to host",
    "轮换失败": "Rotation failed",
    "轮换身份": "Rotate identity",
    "轮换身份会生成新的 qaul 密钥，确定继续？": "Rotating identity generates a new qaul key. Continue?",
    "轮换身份会生成新的 qaul 密钥；旧身份的历史私聊将独立保留。文件访问用于把共享目录落到公共存储。": "Rotating identity generates a new qaul key; past DMs of the old identity are kept separately. File access is used to place the shared directory in public storage.",
    "输入书名或作者搜索（联网书源）": "Search by title or author (online sources)",
    "输入工具栏": "Input toolbar",
    "输入房间号即可进入同一个频道（字母/数字/下划线），例如 mesh、501。": "Enter a room number to join the same channel (letters/digits/underscore), e.g. mesh, 501.",
    "输入消息": "Type a message",
    "输入网址直达，或输入关键词必应搜索": "Enter a URL to go directly, or keywords to search on Bing",
    "运行": "Run",
    "返回首页": "Back to home",
    "进入": "Enter",
    "进入群聊": "Enter group chat",
    "连接中…": "Connecting…",
    "退出": "Exit",
    "退出一起听": "Leave Listen Together",
    "退出群组「": "Leave group \"",
    "退群": "Leave group",
    "选择蓝牙设备": "Select Bluetooth device",
    "通知": "Notifications",
    "通话": "Call",
    "通话中": "In call",
    "通话中（点击恢复）": "In call (tap to resume)",
    "通话中，点击恢复": "In call, tap to resume",
    "通话已结束": "Call ended",
    "通话成员": "Call members",
    "邻居数": "Neighbors",
    "重新加载": "Reload",
    "重新扫描": "Rescan",
    "链接": "Link",
    "链路": "Link path",
    "阅读": "Read",
    "阅读 · Mchat": "Reading · AhoChat",
    "阅读设置": "Reading settings",
    "阅读进度": "Reading progress",
    "附件工具栏": "Attachment toolbar",
    "静音": "Mute",
    "音乐": "Music",
    "音乐 · 视频 · 歌单": "Music · Videos · Playlists",
    "音量减": "Volume down",
    "音量加": "Volume up",
    "音频": "Audio",
    "页边距": "Margins",
    "页面将自动刷新": "The page will refresh automatically",
    "频道": "Channel",
    "频道未就绪": "Channel not ready",
    "高度差：": "Height difference: ",
    "黑体 Sans": "Hei Sans",
    "默认直接保存到公共下载目录（/sdcard/Download）。若自定义到其它公共目录，需先点\"申请所有文件访问权限\"并允许。同名文件自动加序号，不覆盖。": "Files are saved to the public Downloads directory (/sdcard/Download) by default. To use another public directory, first tap \"Request all files access\" and allow it. Files with the same name get a numeric suffix instead of being overwritten.",
    "（我）": "(me)",
    "（本章无正文）": "(No text in this chapter)",
    "（空目录）": "(Empty folder)",
    "（精度 ±": "(accuracy ±",
    "＋ 导入本地书籍": "＋ Import local books",
    "＋ 建群": "+ New group",
    "＋ 房间": "+ Room",
    "＋ 新建书源": "+ New book source",
    "，偏低": ", low",
    "，海拔": ", altitude",
    "，经度": ", longitude"
  };

  var LS_KEY = 'ahochat.lang';
  var CJK = /[\u3400-\u9fff\uf900-\ufaff]/;
  var SKIP = '#messageList, #readerContent, #bookGrid, #searchResults, #fileList, #groupList, #btDeviceList, textarea, [contenteditable="true"], script, style, noscript';

  function pick() {
    var q = null;
    try { q = new URLSearchParams(location.search).get('lang'); } catch (e) { q = null; }
    if (q) return q;
    try { var s = localStorage.getItem(LS_KEY); if (s) return s; } catch (e) {}
    return navigator.language || navigator.userLanguage || 'en';
  }

  var LANG = String(pick() || 'en');
  var EN = !/^zh/i.test(LANG);
  // 空白归一化索引：源码里的多行文案带缩进/换行，节点文本往往只是空白不同
  var WSDICT = {};
  (function () {
    var sq = function (x) { return String(x).replace(/\s+/g, ' ').trim(); };
    for (var k in DICT) if (Object.prototype.hasOwnProperty.call(DICT, k)) WSDICT[sq(k)] = DICT[k];
  })();
  var keys = Object.keys(DICT).sort(function (a, b) { return b.length - a.length; });
  var SUB = keys.filter(function (k) { return k.length >= 2; });

  function core(s) {
    var t = String(s == null ? '' : s);
    var m = /^(\s*)([\s\S]*?)(\s*)$/.exec(t);
    return { lead: m[1], mid: m[2], tail: m[3] };
  }

  // 英文下把残留的全角标点归一化为半角（拼装串里常见）
  function norm(s) {
    if (!EN || s == null) return s;
    return String(s)
      .replace(/：/g, ': ')
      .replace(/，/g, ', ')
      .replace(/、/g, ', ')
      .replace(/（/g, ' (')
      .replace(/）/g, ')')
      .replace(/。/g, '. ')
      .replace(/「/g, '"')
      .replace(/」/g, '"')
      .replace(/！/g, '!')
      .replace(/？/g, '?')
      .replace(/:([A-Z])/g, ': $1')
      .replace(/\(\s+/g, '(')
      .replace(/\s+\)/g, ')')
      .replace(/\s+([,.;!?])/g, '$1')
      .replace(/\s{2,}/g, ' ')
      .replace(/\s+$/, '');
  }

  // 数量词兜底（与 webui 版保持一致；www 版词条本身已含多数整句，这里只兜底拆散的情况）
  var COUNT = [
    [/(\d+)\s*个分类/g, '$1 categories'],
    [/(\d+)\s*个视频/g, '$1 videos'],
    [/(\d+)\s*首歌曲/g, '$1 songs'],
    [/(\d+)\s*台设备/g, '$1 devices'],
    [/(\d+)\s*首/g, '$1 songs'],
    [/(\d+)\s*张/g, '$1 photos'],
    [/(\d+)\s*台/g, '$1 devices'],
    [/(\d+)\s*条/g, '$1 items'],
    [/(\d+)\s*个/g, '$1']
  ];

  function counters(s) {
    if (!EN || !s) return s;
    var out = s;
    for (var i = 0; i < COUNT.length; i++) out = out.replace(COUNT[i][0], COUNT[i][1]);
    return out;
  }

  function trText(s) {
    if (!EN || s == null) return s;
    var p = core(s);
    if (!p.mid || !CJK.test(p.mid)) return s;
    var hit = DICT[p.mid];
    if (hit === undefined) hit = WSDICT[p.mid.replace(/\s+/g, ' ').trim()];
    if (hit !== undefined) return p.lead + counters(norm(hit)) + p.tail;
    // 整句查不到时做片段替换，但只接受「替换后不再含中文」的结果：
    // 否则会得到「共享Root directory」这类中英混排，比保留原文更糟。
    var out = p.mid;
    for (var i = 0; i < SUB.length; i++) {
      var k = SUB[i];
      if (out.indexOf(k) >= 0) out = out.split(k).join(DICT[k]);
      if (!CJK.test(out) && !/[\uff01-\uff5e\u3001\u3002\u300c\u300d]/.test(out)) break;
    }
    out = counters(out);
    if (CJK.test(out)) return s;
    return p.lead + norm(out) + p.tail;
  }

  function t(zh, en) {
    if (!EN) return zh;
    if (en != null) return en;
    if (zh == null) return zh;
    if (DICT[zh] !== undefined) return DICT[zh];
    return trText(zh);
  }

  // 用户内容容器：其中的文本一律不翻译（聊天正文、书籍正文、文件名、设备名等）
  var USER_CONTENT = '#messageList, #readerContent, #bookGrid, #searchResults, #fileList, #groupList, #btDeviceList';

  function inUserContent(el) {
    return !!(el && el.closest && el.closest(USER_CONTENT));
  }

  function skipped(node) {
    var el = node.nodeType === 1 ? node : node.parentNode;
    if (!el || !el.closest) return true;
    // .sys-tip 是程序生成的界面提示（非用户内容），即使位于消息列表内也允许翻译
    if (el.closest('.sys-tip') && !el.closest('textarea, input, [contenteditable="true"]')) return false;
    return !!el.closest(SKIP);
  }

  function doText(node) {
    if (!node || node.nodeType !== 3) return;
    if (node.__i18nOut === node.data) return;
    if (skipped(node)) return;
    var next = trText(node.data);
    if (next !== node.data) {
      node.__i18nOut = next;
      node.data = next;
    }
  }

  var ATTRS = ['placeholder', 'title', 'aria-label', 'alt', 'label'];

  function doAttrs(el) {
    // 属性（placeholder / title / aria-label）都是界面文案，输入框与 textarea 也允许翻译；
    // 只排除用户内容容器（其中的 title 可能来自文件名等用户数据）。
    if (!el || el.nodeType !== 1 || inUserContent(el)) return;
    for (var i = 0; i < ATTRS.length; i++) {
      var a = ATTRS[i];
      if (!el.hasAttribute(a)) continue;
      var v = el.getAttribute(a);
      if (el.__i18nA && el.__i18nA[a] === v) continue;
      var nv = trText(v);
      if (nv !== v) {
        el.__i18nA = el.__i18nA || {};
        el.__i18nA[a] = nv;
        el.setAttribute(a, nv);
      }
    }
    var tag = el.tagName;
    if (tag === 'INPUT' && /^(button|submit|reset)$/i.test(el.type || '')) {
      var vv = el.value;
      if (el.__i18nV !== vv) {
        var nvv = trText(vv);
        if (nvv !== vv) { el.__i18nV = nvv; el.value = nvv; }
      }
    }
  }

  function walk(root) {
    if (!root) return;
    if (root.nodeType === 3) { doText(root); return; }
    if (root.nodeType !== 1 && root.nodeType !== 9) return;
    if (root.nodeType === 1) doAttrs(root);
    var tw = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null, false);
    var n, buf = [];
    while ((n = tw.nextNode())) buf.push(n);
    for (var i = 0; i < buf.length; i++) doText(buf[i]);
    if (root.querySelectorAll) {
      var els = root.querySelectorAll('[placeholder],[title],[aria-label],[alt]');
      for (var j = 0; j < els.length; j++) doAttrs(els[j]);
    }
  }

  var pending = [];
  function flush() {
    var arr = pending.slice(0);
    pending.length = 0;
    for (var i = 0; i < arr.length; i++) {
      var r = arr[i];
      if (r.type === 'characterData') doText(r.target);
      else if (r.type === 'attributes') doAttrs(r.target);
      else for (var j = 0; j < r.addedNodes.length; j++) walk(r.addedNodes[j]);
    }
  }
  var scheduled = false;
  function schedule(rec) {
    pending.push(rec);
    if (scheduled) return;
    scheduled = true;
    var run = function () { scheduled = false; try { flush(); } catch (e) {} };
    if (window.requestAnimationFrame) requestAnimationFrame(run); else setTimeout(run, 16);
  }

  function start() {
    if (!EN) { window.T = function (zh) { return zh; }; return; }
    document.documentElement.setAttribute('lang', 'en');
    if (document.title) document.title = trText(document.title);
    walk(document.body || document.documentElement);
    try {
      var mo = new MutationObserver(function (recs) { for (var i = 0; i < recs.length; i++) schedule(recs[i]); });
      mo.observe(document.documentElement, {
        childList: true, subtree: true, characterData: true,
        attributes: true, attributeFilter: ['placeholder', 'title', 'aria-label', 'alt']
      });
    } catch (e) {}
  }

  window.T = function (zh, en) { return t(zh, en); };
  window.AhoChatI18n = {
    lang: LANG, en: EN, dict: DICT, t: t, tr: trText,
    setLang: function (l) {
      try { localStorage.setItem(LS_KEY, l); } catch (e) {}
      location.reload();
    },
    apply: function (root) { walk(root || document.body); }
  };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
