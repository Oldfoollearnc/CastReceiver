# ============================================================================
# 投屏Window 桌面版 · 一键发布
#
# 一条命令完成：自包含发布 → 产物校验（含 GPL 检查）→ 可选签名 → 打安装包。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File build.ps1                  # 只做自包含发布（绿色版）
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Installer       # 发布 + 打安装包（默认不含开场视频）
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Installer -InstallerWithIntro   # 安装包也带上开场视频
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Installer -Sign -Pfx cert.pfx -PfxPassword 密码
#
# 版本号唯一来源：Directory.Build.props 的 <Version>。安装包版本由本脚本用
# /DAppVersion= 注入给 ISCC，**不要再去 .iss 里手工改**。
#
# 开场视频（assets\intro，约 191MB）：**绿色版照旧带上**（发布步骤会复制过去），
# 但安装包**默认不含** —— 191MB 视频压不动，光打安装包就要好几分钟，安装包也会涨到 250MB+。
# 要带就加 -InstallerWithIntro（由本脚本给 ISCC 传 /DNOINTRO 的开关控制）。
# ============================================================================
[CmdletBinding()]
param(
    # 覆盖版本号（默认从 Directory.Build.props 读）
    [string]$Version = "",
    # 目标运行时：默认 win-x64；交叉构建 ARM64 用 -Runtime win-arm64
    # （ARM64 原生构建已打通，但**真机行为尚未验证** —— 手头没有 ARM64 设备）
    [string]$Runtime = "win-x64",
    # 产出自包含发布的目标目录
    [string]$OutDir = "发布版\投屏Window",
    # 是否调用 Inno Setup 打安装包
    [switch]$Installer,
    # 打安装包时是否把开场视频（191MB）一起打进去。
    # 默认**不打**：视频几乎压不动，打进去要多花好几分钟、安装包也从 ~60MB 涨到 ~250MB。
    # 绿色版不受影响，始终带开场视频。
    [switch]$InstallerWithIntro,
    # 是否给 exe / 安装包签名（需要 -Pfx 或已安装证书；没有证书时不要开）
    [switch]$Sign,
    [string]$Pfx = "",
    [string]$PfxPassword = "",
    # 证书指纹（当前用户证书库里已有的代码签名证书）；没有 signtool 时用它走 Set-AuthenticodeSignature
    [string]$CertThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    # 跳过发布（只打安装包时用）
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$fail = 0

function Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Ok($text) { Write-Host "  [OK] $text" -ForegroundColor Green }
function Bad($text) { Write-Host "  [FAIL] $text" -ForegroundColor Red; $script:fail++ }
function Warn($text) { Write-Host "  [警告] $text" -ForegroundColor Yellow }

# ---------------------------------------------------------------- 版本号
if (-not $Version) {
    $props = Join-Path $root "Directory.Build.props"
    if (-not (Test-Path $props)) { throw "找不到 Directory.Build.props：$props" }
    $m = [regex]::Match((Get-Content $props -Raw -Encoding UTF8), '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw "Directory.Build.props 里没有 <Version>" }
    $Version = $m.Groups[1].Value.Trim()
}

# 非 x64 的产物单独放，避免把 x64 的绿色版覆盖掉
if ($Runtime -ne "win-x64" -and $OutDir -eq "发布版\投屏Window") {
    $OutDir = "发布版\投屏Window-$Runtime"
}

Step "配置"
Write-Host "  版本号（来自 Directory.Build.props）：$Version"
Write-Host "  目标运行时：$Runtime"
Write-Host "  发布目录：$OutDir"
Write-Host "  打安装包：$([bool]$Installer)　签名：$([bool]$Sign)"
if ($Installer) {
    Write-Host "  安装包带开场视频：$([bool]$InstallerWithIntro)$(if (-not $InstallerWithIntro) { '（默认不含，省时间也省体积）' })"
}

$outPath = Join-Path $root $OutDir
$exeName = "投屏Window.exe"
$appCsproj = Join-Path $root "src\CastReceiver.App\CastReceiver.App.csproj"

# ---------------------------------------------------------------- 发布
if (-not $SkipPublish) {
    Step "自包含发布（$Runtime，含 .NET 10 桌面运行时）"
    # ⚠️ 发布目录里有**非 dotnet publish 产出**的文件（例如「启动投屏.cmd」），
    #    而 发布版/ 不在版本控制里 —— 直接 Remove-Item 会把它们永久丢掉。
    #    所以：① 先把这些文件读进内存；② 旧目录整体改名备份（不是删除）；③ 发布后放回。
    $keepNames = @("启动投屏.cmd")
    $keep = @{}
    if (Test-Path $outPath) {
        foreach ($n in $keepNames) {
            $p = Join-Path $outPath $n
            if (Test-Path $p) { $keep[$n] = [System.IO.File]::ReadAllBytes($p); Write-Host "  将保留：$n" }
        }
        $backup = Join-Path (Split-Path $outPath -Parent) ("_old-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
        Move-Item $outPath $backup
        Ok "旧目录已备份为 $backup（确认新版没问题后可自行删除）"
    }
    New-Item -ItemType Directory -Force -Path $outPath | Out-Null

    # csproj 里写的是 SelfContained=false，这里显式覆盖为 true —— 发布包要自带运行环境
    & dotnet publish $appCsproj -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:Version=$Version -o $outPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }
    if (Test-Path (Join-Path $outPath $exeName)) { Ok "已产出 $exeName" } else { Bad "发布目录里没有 $exeName" }

    # 把刚才保留的非产物文件放回去（例如「启动投屏.cmd」）
    foreach ($kv in $keep.GetEnumerator()) {
        [System.IO.File]::WriteAllBytes((Join-Path $outPath $kv.Key), $kv.Value)
        Ok "已放回 $($kv.Key)"
    }

    # 法务文本随绿色版一起放，程序内「关于」页才能直接打开
    foreach ($doc in @("EULA.txt", "隐私政策.txt", "第三方声明.txt", "CHANGELOG.md")) {
        $src = Join-Path $root "docs\$doc"
        if (-not (Test-Path $src)) { $src = Join-Path $root $doc }
        if (Test-Path $src) { Copy-Item $src $outPath -Force; Ok "已附带 $doc" } else { Warn "缺少 $doc（「关于」页会提示找不到）" }
    }

    # 内置开场素材：assets\intro\* → 发布目录的 Assets\intro\
    # IntroClips.ResolvePath 就是按 "<程序目录>\Assets\intro\<FileName>" 找的。
    # ⚠️ 这里只影响**绿色版**：安装包默认会用 /DNOINTRO 把 Assets\intro 排除掉（见下面的安装包步骤）。
    $introSrc = Join-Path $root "assets\intro"
    if (Test-Path $introSrc) {
        $introFiles = @(Get-ChildItem $introSrc -File | Where-Object { $_.Extension -in @('.mp4', '.mkv', '.mov') })
        if ($introFiles.Count -gt 0) {
            $introDst = Join-Path $outPath "Assets\intro"
            New-Item -ItemType Directory -Force -Path $introDst | Out-Null
            foreach ($f in $introFiles) { Copy-Item $f.FullName $introDst -Force }
            Ok ("已附带内置开场素材 {0} 个，共 {1} MB" -f $introFiles.Count, [math]::Round(($introFiles | Measure-Object Length -Sum).Sum / 1MB, 1))
        } else {
            Warn "assets\intro 里没有视频文件：内置开场会找不到素材（程序会跳过入场动画）"
        }
    } else {
        Warn "缺少 assets\intro 目录：内置开场会找不到素材"
    }
} else {
    Step "跳过发布（-SkipPublish）"
    if (-not (Test-Path (Join-Path $outPath $exeName))) { throw "发布目录里没有现成的 $exeName，无法继续" }
}

# ---------------------------------------------------------------- 产物校验
Step "产物校验"
# 要挡的是**原生 libvlc（GPLv2+）**：文件名 libvlc.dll / libvlccore.dll，或整个 libvlc\ 目录。
# 注意：托管的 LibVLCSharp.dll 是 LGPL-2.1 的绑定、本来就该在包里（Core 里的 VlcPlayerEngine 要用），
# 不能用 "libvlc*" 这种模糊匹配把它一起误判掉 —— 我第一版就是这么写错的。
$nativeNames = @('libvlc.dll', 'libvlccore.dll')
$vlcJunk = Get-ChildItem $outPath -Recurse -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -match '\\libvlc\\' -or ($nativeNames -contains $_.Name.ToLower()) }
if ($vlcJunk) {
    Bad "发布包里出现了原生 libvlc（GPL 组件，不得分发）："
    $vlcJunk | Select-Object -First 5 | ForEach-Object { Write-Host "        $($_.FullName)" }
} else {
    Ok "未发现原生 libvlc（GPL 检查通过）"
}

# 托管绑定在包内是预期行为，明确说出来，免得下次有人把它当违规删掉
if (Get-ChildItem $outPath -Recurse -Filter 'LibVLCSharp.dll' -ErrorAction SilentlyContinue) {
    Write-Host "  说明：包内含托管绑定 LibVLCSharp.dll（LGPL-2.1）——产品不加载播放内核，"
    Write-Host "        但 Core 里的 VlcPlayerEngine 供自检回归用，LGPL 义务见《第三方声明》。"
}

$exe = Join-Path $outPath $exeName
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash
    Ok "主程序 $size MB"
    Write-Host "       SHA256(exe) = $hash"
    Write-Host "       文件版本     = $((Get-Item $exe).VersionInfo.FileVersion)"
    if ((Get-Item $exe).VersionInfo.FileVersion -notlike "*$Version*") {
        Warn "exe 的文件版本与 $Version 不一致，检查 -p:Version 是否生效"
    }
    $totalMB = [math]::Round(((Get-ChildItem $outPath -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
    Ok "发布目录总大小 $totalMB MB"
} else {
    Bad "找不到主程序：$exe"
}

# ---------------------------------------------------------------- 签名（可选）
# 统一签名入口：有 signtool（能带时间戳）就用它；没有就用 PowerShell 的
# Set-AuthenticodeSignature（不需要 Windows SDK，自签名证书也能用）。
$script:signtool = $null
$script:signCert = $null
if ($Sign) {
    Step "代码签名"
    $script:signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $script:signtool) {
        foreach ($p in @(
            "${env:ProgramFiles(x86)}\Windows Kits\10\bin\x64\signtool.exe",
            "$env:ProgramFiles\Windows Kits\10\bin\x64\signtool.exe")) {
            if (Test-Path $p) { $script:signtool = Get-Item $p; break }
        }
    }

    # 取证书：优先指纹（证书库），否则把 pfx 导入当前用户"个人"库
    if ($CertThumbprint) {
        $script:signCert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $CertThumbprint } | Select-Object -First 1
        if (-not $script:signCert) { Bad "证书库里没有指纹为 $CertThumbprint 的证书" }
    } elseif ($Pfx -and (Test-Path $Pfx)) {
        try {
            $sec = ConvertTo-SecureString $PfxPassword -AsPlainText -Force
            $script:signCert = Import-PfxCertificate -FilePath $Pfx -Password $sec -CertStoreLocation Cert:\CurrentUser\My
            Ok "已把 $([System.IO.Path]::GetFileName($Pfx)) 导入当前用户证书库（指纹 $($script:signCert.Thumbprint)）"
        } catch { Bad "导入 pfx 失败：$($_.Exception.Message)" }
    } else { Bad "没有可用证书：请给 -CertThumbprint 或 -Pfx" }

    if (-not $script:signtool -and $script:signCert) {
        Write-Host "  提示：系统里没有 signtool，改用 Set-AuthenticodeSignature（不带时间戳）" -ForegroundColor Yellow
    }
}
function Invoke-Sign([string]$file) {
    if (-not $script:signCert) { return $false }
    if ($script:signtool) {
        & $script:signtool.FullName sign /fd sha256 /tr $TimestampUrl /td sha256 /f $Pfx /p $PfxPassword $file | Out-Null
        return ($LASTEXITCODE -eq 0)
    }
    $r = Set-AuthenticodeSignature -FilePath $file -Certificate $script:signCert -HashAlgorithm SHA256
    return ($r.Status -in @('Valid', 'UnknownError'))   # 自签名证书链不受信任 → UnknownError，但签名本身是写进去了
}
if ($Sign) {
    if (Invoke-Sign $exe) {
        $sig = Get-AuthenticodeSignature $exe
        Ok "已签名：$([System.IO.Path]::GetFileName($exe))　状态 $($sig.Status)　签名者 $($sig.SignerCertificate.Subject)"
    } else { Bad "签名失败" }
}

# ---------------------------------------------------------------- 安装包
if ($Installer) {
    Step "打安装包（Inno Setup 6）"
    $iscc = $null
    foreach ($p in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { $iscc = $p; break }
    }
    if (-not $iscc) {
        Bad "找不到 ISCC.exe。请安装 Inno Setup 6，或把 ISCC 加到 PATH。"
    } else {
        $iss = Join-Path $root "installer\投屏Window.iss"
        $isccArgs = @("/DAppVersion=$Version", "`"$iss`"")
        if (-not $InstallerWithIntro) {
            # 默认把 Assets\intro 排除在安装包外（191MB，压不动、很费时间）。
            # 绿色版里那份不受影响；安装后程序找不到素材会自己跳过入场动画。
            $isccArgs = @("/DNOINTRO") + $isccArgs
            Write-Host "  安装包不含开场视频（Assets\intro 已排除）；要带就加 -InstallerWithIntro" -ForegroundColor Yellow
        } else {
            Write-Host "  安装包将包含开场视频（约 191MB，打包会比较慢）" -ForegroundColor Yellow
        }
        if ($Sign -and $script:signtool) {
            # 有 signtool 才把签名命令透给 .iss 的 SignTool=tsign（$f 由 Inno 替换为待签文件）
            $toolCmd = "`"$($script:signtool.FullName)`" sign /fd sha256 /tr $TimestampUrl /td sha256 /f `"$Pfx`" /p `"$PfxPassword`" `$f"
            $isccArgs = @("/DSIGN", "/Stsign=$toolCmd") + $isccArgs
        }
        & $iscc @isccArgs
        if ($LASTEXITCODE -eq 0) {
            $setup = Join-Path $root "发布版\投屏Window-Setup-$Version.exe"
            if (Test-Path $setup) {
                if ($Sign -and -not $script:signtool) {
                    if (Invoke-Sign $setup) { Ok "安装包已签名（Set-AuthenticodeSignature）" } else { Warn "安装包签名失败" }
                }
                Ok "安装包：$setup"
                Write-Host "       $([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB"
                Write-Host "       SHA256(setup) = $((Get-FileHash $setup -Algorithm SHA256).Hash)"
            } else { Warn "ISCC 成功但没找到预期安装包（名称可能不同）" }
        } else { Bad "ISCC 失败（退出码 $LASTEXITCODE）" }
    }
}

# ---------------------------------------------------------------- 汇总
Step "结果"
if ($fail -eq 0) {
    Write-Host "  全部通过。" -ForegroundColor Green
    Write-Host "  绿色版：$outPath"
    Write-Host "  若要做对外发布，请确认已签名（SmartScreen 会拦未签名的安装包）。"
} else {
    Write-Host "  有 $fail 项失败，请看上面的 [FAIL]。" -ForegroundColor Red
    exit 1
}
