# 파란전화기 설치 스크립트
#
#   irm https://raw.githubusercontent.com/kms6402-collab/bluephone/main/install.ps1 | iex
#
# 브라우저로 받은 exe 에는 '인터넷에서 받은 파일' 표시(Mark of the Web)가 붙어
# 실행할 때 "Windows의 PC 보호" / "알 수 없는 게시자" 경고가 뜹니다.
# 이 스크립트는 PowerShell 로 직접 내려받아(표시가 붙지 않음) SHA-256 을 검증한 뒤 설치하므로
# 그런 경고 없이 바로 실행됩니다. 관리자 권한이 필요 없습니다.
#
# 옵션 (환경 변수):
#   $env:BP_NAME    = "사내쪽지"   # 실행 파일 이름 (기본: 파란전화기) → 작업 관리자에도 이 이름으로 표시
#   $env:BP_DESKTOP = "1"          # 바탕 화면 바로 가기도 만들기
#   $env:BP_DIR     = "D:\Apps\BP"   # 설치 폴더 (기본: %LOCALAPPDATA%\Programs\파란전화기)
#   $env:BP_NORUN   = "1"          # 설치 후 실행하지 않음
#   $env:BP_NOLINK  = "1"          # 바로 가기를 만들지 않음

$ErrorActionPreference = 'Stop'
$repo = 'kms6402-collab/bluephone'
$name = if ($env:BP_NAME) { $env:BP_NAME.Trim() -replace '\.exe$', '' } else { '파란전화기' }
$dir  = if ($env:BP_DIR) { $env:BP_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\파란전화기' }
$exe  = Join-Path $dir "$name.exe"

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Write-Host ''
Write-Host '  파란전화기 설치' -ForegroundColor Blue
Write-Host ''

$rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'BluePhone-Installer' }
$asset = $rel.assets | Where-Object name -eq 'BluePhone.exe' | Select-Object -First 1
$sum   = $rel.assets | Where-Object name -eq 'BluePhone.exe.sha256' | Select-Object -First 1
if (-not $asset) { throw '릴리스에서 BluePhone.exe 를 찾지 못했습니다.' }
Write-Host "  · 최신 버전 $($rel.tag_name) 내려받는 중…"

New-Item -ItemType Directory -Force $dir | Out-Null
$tmp = Join-Path $dir 'BluePhone.download'
$wc = New-Object Net.WebClient
$wc.Headers.Add('User-Agent', 'BluePhone-Installer')
$wc.DownloadFile($asset.browser_download_url, $tmp)

if ($sum) {
    $expected = ($wc.DownloadString($sum.browser_download_url) -split '\s+')[0].Trim().ToLower()
    $actual = (Get-FileHash $tmp -Algorithm SHA256).Hash.ToLower()
    if ($expected -ne $actual) { Remove-Item $tmp -Force; throw "SHA-256 이 일치하지 않습니다. 다운로드가 손상되었을 수 있습니다." }
    Write-Host '  · SHA-256 검증 완료'
}

# 실행 중이면 종료 후 교체
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | ForEach-Object { $_.CloseMainWindow() | Out-Null; Start-Sleep 2; if (-not $_.HasExited) { $_.Kill() } }
Move-Item $tmp $exe -Force
Unblock-File $exe -ErrorAction SilentlyContinue

# 시작 메뉴 / 바탕 화면 바로 가기
$shell = New-Object -ComObject WScript.Shell
$links = @()
if ($env:BP_NOLINK -ne '1') { $links += Join-Path ([Environment]::GetFolderPath('Programs')) "$name.lnk" }
if ($env:BP_NOLINK -ne '1' -and $env:BP_DESKTOP -eq '1') { $links += Join-Path ([Environment]::GetFolderPath('Desktop')) "$name.lnk" }
foreach ($l in $links) {
    $s = $shell.CreateShortcut($l)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $dir
    $s.Description = '파란전화기 — 사내 쪽지'
    $s.Save()
}

Write-Host "  · 설치 위치: $exe"
if ($links.Count -gt 0) { Write-Host '  · 시작 메뉴에 바로 가기를 만들었습니다' }
Write-Host ''
Write-Host '  설치가 끝났습니다.' -ForegroundColor Green
Write-Host '  (처음 실행 시 Windows 방화벽 허용 창이 뜨면 "허용"을 눌러 주세요)'
Write-Host ''
if ($env:BP_NORUN -ne '1') { Start-Process $exe }
