<p align="center"><img src="docs/icon.png" width="96" alt=""></p>

<h1 align="center">파란전화기</h1>

<p align="center">서버 없이 같은 사내 네트워크에서 바로 쓰는 팝업 쪽지 메신저<br>
2007년의 "빨간전화기 X-PopUp" 감성을 지금 Windows에 맞게 다시 만든 프로그램입니다.</p>

<p align="center">
  <a href="../../releases/latest"><b>최신 버전 내려받기</b></a> ·
  <a href="CHANGELOG.md">변경 이력</a>
</p>

<p align="center">
  <img src="docs/main.png" width="300" alt="메인 창">
  <img src="docs/popup.png" width="400" alt="받은 쪽지 창">
</p>

## 특징

- **설치 없음** — 실행 파일 하나(약 270 KB). Windows 10/11에 기본으로 들어 있는 .NET Framework 4.8로 동작합니다.
- **서버 없음** — 같은 네트워크의 PC를 자동으로 찾습니다. 라우터 너머 다른 대역은 IP를 직접 등록하면 됩니다.
- **파일 이름을 마음대로** — `파란전화기.exe`, `사내쪽지.exe` 무엇으로 바꿔도 실행되고, 작업 관리자에도 바꾼 이름으로 표시됩니다.
- **자동 업데이트** — 이 저장소에 새 버전이 올라오면 프로그램이 받아 두고 변경 사항과 함께 알려 줍니다.

## 기능

| 구분 | 내용 |
|---|---|
| 동료 목록 | 자동 검색, 부서별 묶음, 상태(대화 가능·다른 용무 중·자리 비움·방해 금지), 상태 메시지, 프로필 사진, 내가 붙이는 별명 |
| 쪽지 | 1:1 · 여러 명 · 전체 공지, 긴급 쪽지(방해 금지 무시, 창 깜박임, 별도 알림음), 읽음 확인 |
| 받은 쪽지 창 | 화면 오른쪽 아래부터 위로 쌓임, 작업 중 입력을 가로채지 않음, 창 안에서 바로 답장(Ctrl+Enter) |
| 첨부 | 모든 형식의 파일, 폴더는 자동으로 ZIP 압축, 끌어다 놓기, 캡처 이미지 Ctrl+V |
| 받은 파일 | 기본 `다운로드\파란전화기`. 받은 뒤 바로 열기 / 폴더에서 보기. Shift+받기로 위치 지정 |
| 쪽지함 | 주고받은 쪽지 검색 · 다시 열기 · 답장 · 삭제 |
| 부재 | N분 입력이 없으면 자동 자리 비움, 부재 중 자동응답 |
| 기타 | 트레이 상주(안 읽은 쪽지 표시), Windows 시작 시 실행, 버전 정보 표시 |

<p align="center">
  <img src="docs/compose.png" width="420" alt="쪽지 쓰기">
  <img src="docs/settings.png" width="330" alt="설정">
</p>

## 설치

1. [Releases](../../releases/latest)에서 `BluePhone.exe`를 받습니다.
2. 원하는 이름(예: `파란전화기.exe`)으로 바꿔 쓰기 가능한 폴더(바탕화면, 문서 등)에 둡니다.
3. 실행하면 이름·부서를 입력하는 화면이 뜹니다.
4. 처음 실행할 때 **Windows 방화벽 허용** 창이 뜨면 *허용*을 눌러 주세요. 그래야 다른 PC와 통신할 수 있습니다.

> SmartScreen이 "알 수 없는 게시자" 경고를 띄우면 *추가 정보 → 실행*을 누르세요. 코드 서명 인증서가 없는 개인 프로젝트라서 나타나는 경고입니다.

## 자동 업데이트

- 프로그램은 시작 20초 뒤, 그리고 6시간마다 이 저장소의 **최신 릴리스**를 확인합니다.
- 새 버전이 있으면 백그라운드에서 받아 **SHA-256 해시를 검증**한 뒤 "업데이트 준비 완료" 창에 **변경 사항**을 보여 줍니다.
  - *지금 다시 시작* — 바로 교체하고 다시 실행합니다.
  - *나중에* — 프로그램을 종료할 때 교체됩니다.
- 교체할 때 **사용자가 바꾼 파일 이름을 그대로 유지**합니다.
- 업데이트 후 처음 실행하면 "○○ 버전으로 업데이트했습니다" 창에 이번 버전의 변경 사항이 표시됩니다.
- 설정 › 정보에서 자동 업데이트를 끄거나 *업데이트 확인*으로 직접 확인할 수 있습니다.

## 네트워크

| 용도 | 프로토콜 | 기본 포트 |
|---|---|---|
| 동료 검색 (접속 알림 브로드캐스트) | UDP | 9631 |
| 쪽지 · 읽음 확인 · 파일 · 프로필 사진 | TCP | 9631 |

- 모든 PC가 같은 포트를 써야 합니다(설정에서 변경).
- 메시지는 같은 네트워크 안에서만 오가며 외부 서버를 거치지 않습니다. 인터넷 연결은 업데이트 확인(GitHub)에만 씁니다.
- 통신 내용은 암호화하지 않습니다. 신뢰할 수 있는 사내망에서 사용하세요.

## 데이터 위치

| 내용 | 위치 |
|---|---|
| 설정 · 쪽지 기록 · 동료 목록 · 프로필 사진 | `%APPDATA%\BluePhone` |
| 받은 파일 | `다운로드\파란전화기` (설정에서 변경) |

`--data <폴더>` 인수로 실행하면 설정과 기록을 그 폴더에 따로 둡니다. USB 같은 포터블 환경에서 쓰거나, 한 PC에서 두 개를 띄워 시험할 때 유용합니다.

## 빌드

필요한 것: [.NET SDK 8](https://dotnet.microsoft.com/download) 이상 (Windows)

```powershell
git clone https://github.com/kms6402-collab/bluephone.git
cd bluephone
powershell -ExecutionPolicy Bypass -File tools\build.ps1   # → 파란전화기.exe
```

아이콘을 바꾸려면 `tools\make-icon.ps1`을 고친 뒤 실행하세요(`src\app.ico` 생성).

## 새 버전 내기 (관리자용)

1. `CHANGELOG.md` 맨 위에 `## [2.2.0] - 날짜` 항목과 변경 사항을 적습니다.
2. `src\BluePhone.csproj`의 `<Version>`을 같은 번호로 올립니다(로컬 빌드용).
3. 커밋한 뒤 태그를 올립니다.
   ```powershell
   git tag v2.2.0
   git push origin main --tags
   ```
4. GitHub Actions가 자동으로 빌드하고, CHANGELOG의 해당 항목을 노트로 붙여 릴리스를 만듭니다. 사용 중인 모든 파란전화기가 이 릴리스를 보고 업데이트합니다.

## 구조

```
src/
  App.xaml.cs        앱 수명주기, 상태 · 쪽지 · 트레이 · 업데이트 흐름 (Core)
  Net.cs             UDP 검색 + TCP 프레임 프로토콜 (쪽지, 읽음, 파일, 사진)
  Updater.cs         GitHub 릴리스 확인 · 다운로드 · 해시 검증 · 실행 파일 교체
  AvatarStore.cs     프로필 사진 처리와 캐시
  Store.cs           설정 · 기록 저장
  *Window.xaml       메인 / 쪽지 쓰기 / 받은 쪽지 / 설정 / 업데이트 창
  Theme.xaml         색상 · 버튼 · 입력칸 · 메뉴 스타일
tools/               빌드 · 아이콘 생성 스크립트
.github/workflows/   빌드 확인(build.yml), 릴리스 발행(release.yml)
```

## 크레딧

수화기 아이콘 모양은 Google [Material Icons](https://github.com/google/material-design-icons)(Apache License 2.0)의 `call` 아이콘을 사용했습니다.
