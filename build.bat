@echo off
chcp 936 >nul 2>&1
setlocal EnableExtensions
cd /d "%~dp0"
rem 从 Git Bash 等终端启动时 PATH 可能混入 GNU find/tar，显式把 System32 提到最前
set "PATH=%SystemRoot%\System32;%PATH%"

rem ============================================================
rem  AhoChatA / com.Ahochat.android  一键打包 + 签名 + 装机（纯 cmd，无需 Git Bash）
rem  默认：增量构建 - 签名 - 自动拉起 adb 安装到手机
rem  用法：build.bat --help
rem  注意：本文件为 GBK(936) 编码 + CRLF 行尾，改动后需保持，否则 cmd 解析中文会出错
rem  !! Keep this file GBK/CP936 + CRLF; UTF-8 breaks cmd parsing.
rem  !! If unsure about the editor encoding, use "bash build.sh" instead.
rem ============================================================

rem ---------- 环境（可用同名环境变量覆盖） ----------
set "MSB=C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
set "SDK=C:\Program Files (x86)\Android\android-sdk"
set "JDK=C:\Program Files\Java\jdk-17.0.1"
set "BT=C:\Program Files (x86)\Android\android-sdk\build-tools\35.0.0"
set "JAVA=C:\Program Files\Java\jdk-17.0.1\bin\java.exe"
set "ADB=C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe"
set "KS=C:\Users\User\AppData\Local\Xamarin\Mono for Android\debug.keystore"
if defined MSBUILD_EXE set "MSB=%MSBUILD_EXE%"
if defined ANDROID_SDK set "SDK=%ANDROID_SDK%"
if defined JAVA_SDK set "JDK=%JAVA_SDK%"
if defined BUILD_TOOLS set "BT=%BUILD_TOOLS%"
if defined JAVA_EXE set "JAVA=%JAVA_EXE%"
if defined ADB_EXE set "ADB=%ADB_EXE%"
if defined KEYSTORE set "KS=%KEYSTORE%"
if not defined ADB_WAIT set "ADB_WAIT=8"

rem 签名工具是 Windows 程序，用相对路径最稳
rem 目录约定：bin\ 下（Debug / Release / net9.0-android 等）全部是编译中间目录，
rem   未签名 APK 与 aligned.apk 只作过程临时文件，不作为交付物；
rem   签名后的唯一交付产物固定为 dist\com.Ahochat.android.apk。
set "PKG=com.Ahochat.android"
set "APK=bin\Debug\%PKG%.apk"
set "ALIGNED=bin\Debug\aligned.apk"
set "OUT_NAME=%PKG%.apk"
set "OUT=dist\%OUT_NAME%"
set "LOG=build.log"

rem AOT 默认关闭：本环境从终端继承的代理变量存在大小写重复（HTTPS_PROXY / https_proxy），
rem Xamarin 的 AOT 任务在建环境变量字典时会抛 XA0000；需要 AOT 时加 --aot。
set "AOT=0"
set "MODE=incremental"
set "DO_BUILD=1"
set "DO_SIGN=1"
set "DO_INSTALL=1"
set "CHECK_ONLY=0"
set "INSTALL_RC=0"

rem ---------- 参数 ----------
:parse
if "%~1"=="" goto :parsed
if /i "%~1"=="clean" (set "MODE=clean" & shift & goto :parse)
if /i "%~1"=="full" (set "MODE=clean" & shift & goto :parse)
if /i "%~1"=="rebuild" (set "MODE=clean" & shift & goto :parse)
if /i "%~1"=="restore" (set "MODE=restore" & set "DO_INSTALL=0" & shift & goto :parse)
if /i "%~1"=="sign" (set "DO_BUILD=0" & shift & goto :parse)
if /i "%~1"=="install" (set "DO_INSTALL=1" & shift & goto :parse)
if /i "%~1"=="--aot" (set "AOT=1" & shift & goto :parse)
if /i "%~1"=="noaot" (set "AOT=0" & shift & goto :parse)
if /i "%~1"=="-n" (set "DO_INSTALL=0" & shift & goto :parse)
if /i "%~1"=="--no-install" (set "DO_INSTALL=0" & shift & goto :parse)
if /i "%~1"=="noinstall" (set "DO_INSTALL=0" & shift & goto :parse)
if /i "%~1"=="--noinstall" (set "DO_INSTALL=0" & shift & goto :parse)
if /i "%~1"=="check" (set "DO_BUILD=0" & set "DO_SIGN=0" & set "DO_INSTALL=0" & set "CHECK_ONLY=1" & shift & goto :parse)
if /i "%~1"=="verify" (set "DO_BUILD=0" & set "DO_SIGN=0" & set "DO_INSTALL=0" & set "CHECK_ONLY=1" & shift & goto :parse)
if /i "%~1"=="-h" goto :usage
if /i "%~1"=="--help" goto :usage
if /i "%~1"=="/?" goto :usage
if /i "%~1"=="help" goto :usage
echo 未知参数: %~1
echo.
call :usage
exit /b 2
:parsed

set "AOT_ARGS=-p:AotAssemblies=false -p:AndroidEnableProfiledAot=false"
if "%AOT%"=="1" set "AOT_ARGS="

rem 记录开始时刻（纯 cmd，不依赖外部脚本引擎，避免被安全策略挂起）
set "T0=%TIME%"

call :say "环境自检"
call :precheck
if errorlevel 1 goto :abort

if /i "%MODE%"=="restore" goto :mode_restore

if "%DO_BUILD%"=="1" call :do_build
if errorlevel 1 goto :abort
if "%DO_SIGN%"=="1" call :do_sign
if errorlevel 1 goto :abort
call :verify
if errorlevel 1 goto :abort
if "%DO_INSTALL%"=="1" call :do_install
if errorlevel 1 set "INSTALL_RC=1"
goto :done

:abort
exit /b 1

:mode_restore
call :do_restore
if errorlevel 1 goto :abort
goto :done

:done
echo.
echo 完成（开始 %T0% / 结束 %TIME%）；产物: %CD%\%OUT%
if "%CHECK_ONLY%"=="1" echo （仅校验模式，未重新构建）
if not "%INSTALL_RC%"=="1" goto :done_ok
echo 注意：安装步骤失败，但 %OUT% 已生成且签名有效，可手动安装。
exit /b 1
:done_ok
exit /b 0

rem ============================================================
rem  子程序
rem ============================================================

:usage
echo 用法: build.bat [选项 ...]
echo.
echo   无参数        增量打包 + 签名 + 自动装到手机 → dist\com.Ahochat.android.apk（日常最快）
echo   clean         先 Clean 再 Restore + 打包 + 签名 + 装手机（新增/删除资源、.cs 文件后用）
echo   -n            只打包签名，不安装（同 --no-install / noinstall）
echo   sign          不编译，只把现有 bin\Debug\com.Ahochat.android.apk 重新签名（同样会自动安装）
echo   restore       只恢复 NuGet 引用
echo   --aot         启用 AOT 预编译（默认关闭；本环境代理变量大小写重复会让 AOT 报 XA0000）
echo   check         只校验现有 dist\com.Ahochat.android.apk（对齐 / 签名 / 内置网页资源），不构建不安装
echo   -h / --help   显示本帮助
echo.
echo 示例: build.bat            改完代码，一条命令出包并装到手机
echo       build.bat clean      新增资源后全量重编并装机
echo       build.bat -n         只要 dist\com.Ahochat.android.apk，不碰手机
echo       build.bat sign       不编译，重签现有包并装机
echo.
echo 产物: dist\com.Ahochat.android.apk 为唯一交付文件；bin\ 下均为编译过程临时文件
echo 日志: build.log / build_clean.log / build_restore.log
echo 环境变量覆盖: MSBUILD_EXE / ANDROID_SDK / JAVA_SDK / BUILD_TOOLS / JAVA_EXE / KEYSTORE / ADB_EXE / ADB_WAIT
echo 提示: 改动 .csproj 或新增资源时必须用 clean，且资源要逐文件登记 AndroidAsset / AndroidResource
echo       找不到设备不会让脚本失败；设备未授权时请在手机上点「允许 USB 调试」
exit /b 0

:say
echo.
echo == %~1 ==
exit /b 0

rem 路径里可能含 (x86)，绝对不能在 if 的括号块里展开，全部用 goto
:need
if exist "%~1" goto :need_ok
echo   [缺失] %~1
set "MISS=1"
exit /b 0
:need_ok
echo   [OK]   %~1
exit /b 0

:precheck
set "MISS=0"
call :need "%MSB%"
call :need "%JAVA%"
call :need "%BT%\zipalign.exe"
call :need "%BT%\lib\apksigner.jar"
call :need "%KS%"
if "%MISS%"=="1" goto :precheck_bad
exit /b 0
:precheck_bad
echo 环境不完整，请检查上面的路径（或用环境变量覆盖）。
exit /b 1

:show_errors
echo 构建失败，日志摘要（%~1）：
findstr /I /C:": error" /C:"Build FAILED" /C:"error MSB" "%~1"
echo --- 日志末尾 ---
if not exist "%~1" exit /b 0
set /a TOTAL=0
for /f "delims=:" %%a in ('findstr /n "^" "%~1"') do set /a TOTAL+=1
set /a FIRST=TOTAL-11
if %FIRST% LSS 1 set /a FIRST=1
for /f "tokens=1,* delims=:" %%a in ('findstr /n "^" "%~1"') do if %%a GEQ %FIRST% echo.%%b
exit /b 0

:do_restore
call :say "恢复 NuGet 引用"
"%MSB%" AhoChatA.csproj -t:Restore -p:Configuration=Debug -verbosity:minimal -p:AndroidSdkDirectory="%SDK%" -p:JavaSdkDirectory="%JDK%" > build_restore.log 2>&1
if errorlevel 1 goto :restore_fail
echo RESTORE_OK
exit /b 0
:restore_fail
call :show_errors build_restore.log
exit /b 1

:do_build
call :say "打包（%MODE%）"
rem auto-restore NuGet deps when obj cache was cleaned (e.g. after temp cleanup)
if /i "%MODE%"=="clean" goto :build_restore_done
if exist "obj\project.assets.json" goto :build_restore_done
echo   obj cache missing, restoring NuGet packages ...
call :do_restore
if errorlevel 1 goto :build_fail
:build_restore_done
set "BEFORE="
if exist "%APK%" for %%I in ("%APK%") do set "BEFORE=%%~tI %%~zI"

set "TARGET=-t:PackageForAndroid"
if /i not "%MODE%"=="clean" goto :build_run
echo   清理旧的编译/资源缓存 ...
if exist obj rd /s /q obj
if exist bin rd /s /q bin
echo   Restoring NuGet packages (Restore)...
"%MSB%" AhoChatA.csproj -t:Restore -p:Configuration=Debug -verbosity:minimal -p:AndroidSdkDirectory="%SDK%" -p:JavaSdkDirectory="%JDK%" > build_restore.log 2>&1
if not errorlevel 1 goto :restore_ok
echo   Restore failed; summary (build_restore.log):
findstr /I /C:": error" /C:"Build FAILED" build_restore.log
exit /b 1
:restore_ok
set "TARGET=-t:PackageForAndroid"

:build_run
if "%AOT%"=="0" echo   AOT: 关闭（JIT 包，功能一致，冷启动略慢）
echo   编译中（日志 -^> %LOG%）...
"%MSB%" AhoChatA.csproj %TARGET% -p:Configuration=Debug -maxcpucount -verbosity:minimal -p:AndroidSdkDirectory="%SDK%" -p:JavaSdkDirectory="%JDK%" %AOT_ARGS% > "%LOG%" 2>&1
if errorlevel 1 goto :build_fail
findstr /I /C:": error" /C:"Build FAILED" "%LOG%" >nul
if not errorlevel 1 goto :build_fail
if exist "%APK%" goto :build_have
rem 兼容 MSBuild 输出名与预期不一致（如包名大小写规范化）：取 bin\Debug 下最新 APK
for /f "delims=" %%F in ('dir /b /a-d /o-d "bin\Debug\*.apk" 2^>nul') do (
  if /i not "%%~nxF"=="aligned.apk" if /i not "%%~nxF"=="signed.apk" (
    set "APK=bin\Debug\%%~nxF"
    goto :build_have
  )
)
goto :build_noapk
:build_have

set "AFTER="
for %%I in ("%APK%") do set "AFTER=%%~tI %%~zI"
set "UP=0"
if defined BEFORE if "%BEFORE%"=="%AFTER%" set "UP=1"
if "%UP%"=="1" echo   提示：APK 时间戳未变化（增量 up-to-date）；若改了网页资源请改用 clean。

set /a WARNS=0
set "WARNS=0"
for /f "delims=:" %%a in ('findstr /C:"warning " "%LOG%"') do set /a WARNS+=1
if "%UP%"=="1" echo   warnings: %WARNS%（仅本次增量输出）  errors: 0
if not "%UP%"=="1" echo   warnings: %WARNS%   errors: 0
for %%I in ("%APK%") do set /a SZMB=%%~zI/1048576
echo   %APK%  (%SZMB% MB)
exit /b 0

:build_noapk
echo 未找到 %APK%
:build_fail
call :show_errors %LOG%
exit /b 1

:do_sign
call :say "对齐 + 签名"
if not exist "%APK%" goto :sign_noapk
if not exist "dist" mkdir "dist"

rem 统一产物名：清掉 dist 下历史命名的 APK / idsig，保证 dist 只有唯一交付文件
for %%F in (dist\*.apk) do if /i not "%%~nxF"=="%OUT_NAME%" del /q "%%F"
for %%F in (dist\*.apk.idsig) do if /i not "%%~nxF"=="%OUT_NAME%.idsig" del /q "%%F"
if exist "%ALIGNED%" del /q "%ALIGNED%"

"%BT%\zipalign.exe" -p 4 "%APK%" "%ALIGNED%"
if errorlevel 1 goto :sign_fail_align
"%JAVA%" -jar "%BT%\lib\apksigner.jar" sign --ks "%KS%" --ks-pass pass:android --key-pass pass:android --ks-key-alias androiddebugkey --out "%OUT%" "%ALIGNED%"
if errorlevel 1 goto :sign_fail_sign
echo   已生成 %OUT%
if exist "%ALIGNED%" del /q "%ALIGNED%"
echo   bin\ 下的未签名包与对齐临时文件仅作过程文件；唯一交付: %OUT%
exit /b 0

:sign_noapk
echo 缺少 %APK%，无法签名
exit /b 1
:sign_fail_align
echo   zipalign 失败
exit /b 1
:sign_fail_sign
echo   apksigner 签名失败
exit /b 1

:verify
call :say "校验产物"
if not exist "%OUT%" goto :verify_noout
"%BT%\zipalign.exe" -c -p 4 "%OUT%"
if errorlevel 1 goto :verify_fail
echo   ALIGN_OK
"%JAVA%" -jar "%BT%\lib\apksigner.jar" verify --print-certs "%OUT%" 2>nul | findstr /B /C:"Signer #1"
set "ASSETS=0"
if not exist "%SystemRoot%\System32\tar.exe" goto :verify_notar
for /f "delims=" %%a in ('tar -tf "%OUT%" 2^>nul ^| findstr /C:"assets/webui/" /C:"assets/www/"') do set /a ASSETS+=1
echo   内置网页资源条目: %ASSETS%
for %%F in (assets/webui/index.html assets/www/index.html assets/webui/js/web.js assets/www/js/chat.js) do (
  tar -tf "%OUT%" 2>nul | findstr /B /C:"%%F" >nul
  if errorlevel 1 echo   [警告] 缺少 %%F
)
:verify_notar
for %%I in ("%OUT%") do set /a OSZMB=%%~zI/1048576
echo   %OUT%  (%OSZMB% MB)
exit /b 0

:verify_noout
echo 缺少 %OUT%
exit /b 1
:verify_fail
echo   [错误] 对齐校验未通过
exit /b 1

rem 找 adb：环境变量优先，其次 PATH、常见安装位置
:find_adb
if exist "%ADB%" exit /b 0
set "ADBFOUND="
for /f "delims=" %%p in ('where adb.exe 2^>nul') do call :try_adb "%%p"
if defined ADBFOUND exit /b 0
if exist "%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" goto :adb_local
if defined ANDROID_HOME if exist "%ANDROID_HOME%\platform-tools\adb.exe" goto :adb_home
if defined ANDROID_SDK_ROOT if exist "%ANDROID_SDK_ROOT%\platform-tools\adb.exe" goto :adb_root
exit /b 1

:try_adb
if defined ADBFOUND exit /b 0
if not exist "%~1" exit /b 0
set "ADB=%~1"
set "ADBFOUND=1"
exit /b 0

:adb_local
set "ADB=%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe"
exit /b 0
:adb_home
set "ADB=%ANDROID_HOME%\platform-tools\adb.exe"
exit /b 0
:adb_root
set "ADB=%ANDROID_SDK_ROOT%\platform-tools\adb.exe"
exit /b 0

:do_install
call :say "安装到手机"
call :find_adb
if errorlevel 1 goto :install_noadb
echo   adb: %ADB%
"%ADB%" version >nul 2>&1
if errorlevel 1 goto :install_badadb
rem 自动拉起 daemon（可能已被系统回收）
"%ADB%" start-server >nul 2>&1
echo   等待设备（最多 %ADB_WAIT%s）...
set /a TRIES=0

:waitdev
set "DEV="
set "STATE="
"%ADB%" devices > "%TEMP%\mchat_devices.txt" 2>nul
for /f "usebackq skip=1 tokens=1,2" %%a in ("%TEMP%\mchat_devices.txt") do call :take_dev "%%a" "%%b"
if defined DEV goto :devok
set /a TRIES+=1
if %TRIES% GEQ %ADB_WAIT% goto :nodev
ping -n 2 127.0.0.1 >nul
goto :waitdev

:take_dev
if defined DEV exit /b 0
if "%~2"=="" exit /b 0
if /i "%~2"=="device" goto :take_dev_ok
set "STATE=%~2"
exit /b 0
:take_dev_ok
set "DEV=%~1"
exit /b 0

:install_noadb
echo   未找到 adb（可设 ADB_EXE 指定路径），跳过安装。
exit /b 0
:install_badadb
echo   adb 无法运行，跳过安装。
exit /b 0

:nodev
if /i "%STATE%"=="unauthorized" goto :nodev_auth
if /i "%STATE%"=="offline" goto :nodev_off
echo   未检测到在线设备，跳过安装（APK 已生成，可直接拷走）。
exit /b 0
:nodev_auth
echo   设备未授权：请在手机上点「允许 USB 调试」后重试。
exit /b 0
:nodev_off
echo   设备 offline：请重新插拔 USB，或 adb kill-server 后重试。
exit /b 0

:devok
echo   设备: %DEV%
"%ADB%" install -r "%CD%\%OUT%" > "%TEMP%\mchat_install.log" 2>&1
type "%TEMP%\mchat_install.log"
findstr /C:"Success" "%TEMP%\mchat_install.log" >nul
if errorlevel 1 goto :install_fail
echo   安装成功。
exit /b 0

:install_fail
echo   安装失败（APK 本身已生成）：
findstr /C:"INSTALL_FAILED_UPDATE_INCOMPATIBLE" "%TEMP%\mchat_install.log" >nul
if not errorlevel 1 goto :fail_unsigned
findstr /C:"INSTALL_FAILED_VERSION_DOWNGRADE" "%TEMP%\mchat_install.log" >nul
if not errorlevel 1 goto :fail_downgrade
findstr /C:"INSTALL_FAILED_INSUFFICIENT_STORAGE" "%TEMP%\mchat_install.log" >nul
if not errorlevel 1 goto :fail_storage
echo     检查 USB / 无线调试连接，或手动执行上面的 adb install 命令查看详情。
exit /b 1
:fail_unsigned
echo     与本机已装版本签名不一致，需先卸载（会清应用数据）：
echo     "%ADB%" uninstall com.Ahochat.android
exit /b 1
:fail_downgrade
echo     版本降级被拒：改用 adb install -r -d，或在 csproj 提高 VersionCode。
exit /b 1
:fail_storage
echo     手机存储空间不足，清理后重试。
exit /b 1
