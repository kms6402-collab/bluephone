# 파란전화기 빌드: src 를 Release 로 빌드한 뒤 루트에 파란전화기.exe 로 복사
$root = Split-Path $PSScriptRoot
dotnet build "$root\src\BluePhone.csproj" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item "$root\src\bin\Release\net48\BluePhone.exe" "$root\파란전화기.exe" -Force
Write-Host "완료: $root\파란전화기.exe"
