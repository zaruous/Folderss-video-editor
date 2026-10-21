# 구현 계획 — 동영상 편집기 플러그인

ECC `orch-build-mvp` 단계(0 요구 사항 읽기 → 1 조사·재사용 → 2 계획 → **GATE 1** → 3 뼈대 → 4 TDD 구현 → 5 리뷰 → 6 커밋)를 따른다.
요구 사항은 [PRD.md](PRD.md).

**크기 등급: large** — 새 외부 의존(FFmpeg·NAudio), 새 저장소, 열린 질문 여러 개.

---

## 1. 조사·재사용 결과

| 후보 | 판단 |
|---|---|
| FFmpeg 명령줄(ffmpeg·ffprobe) | **채택.** 정보 읽기·썸네일·파형·미리 보기 렌더·내보내기를 모두 맡긴다. 별도 프로세스라 디코더가 죽어도 Folderss는 살아 있다 |
| NAudio 2.x (MIT) | **채택.** 미리 보기 소리 재생(PCM 스트림 → WaveOut) |
| FFMediaToolkit (MIT, FFmpeg를 프로세스 안에 올림) | 보류. 프레임 접근은 빠르지만 네이티브 DLL 오류가 Folderss를 함께 죽인다. 미리 보기가 느리면 다시 본다 |
| LibVLCSharp (LGPL) | 제외. 재생은 좋지만 프레임 단위 편집 미리 보기·합성이 어렵고 네이티브가 크다 |
| WPF MediaElement | 제외. 프레임 정확 탐색이 안 되고 트랙 합성이 안 된다 |
| FramePFX (C# NLE, GPL-3.0) | 코드는 쓰지 않음(라이선스). 구조만 참고 |
| 그 밖의 WPF 편집기 저장소 | 작고 라이선스 없음 — 쓰지 않음 |

## 2. 구조

```
src/VideoEditor/                     ← 플러그인 (net8.0-windows, WPF)
├── Core/        ← WPF를 쓰지 않는 편집 모델 (테스트가 net8.0으로 링크 → Linux CI에서도 돈다)
│   ├── Project · Track · Clip · Marker        프로젝트 모델 (시간은 정수 틱, 프레임 단위로 맞춤)
│   ├── TimelineEdits                          분할·다듬기·이동·리플 삭제·마그네틱·스냅 (순수 함수)
│   ├── UndoStack                              편집 명령 실행 취소/다시 실행
│   ├── RenderGraph                            프로젝트 → ffmpeg filter_complex 인수 (미리 보기·내보내기 같은 그래프)
│   └── ProjectFile                            JSON 저장/열기 (*.fvproj)
├── Media/       ← FFmpeg 프로세스: 찾기·ffprobe·썸네일·파형·프레임 렌더·소리 스트림·내보내기(진행률·취소)
└── Ui/          ← 미디어 패널 · 플레이어 패널 · 속성 패널 · 타임라인(직접 그리는 컨트롤) · 단축키
tests/VideoEditor.Tests/             ← Core·RenderGraph 단위 시험 (xUnit, CI)
tests/VideoEditor.UiTests/           ← 템플릿의 헤드리스 시험 호스트 (화면 흐름·캡처, ffmpeg 있으면 실제 렌더)
```

- **미리 보기 = 내보내기 그래프**: 같은 `RenderGraph`를 미리 보기 해상도로 돌려 raw BGRA 프레임을 받아 `WriteableBitmap`에 그린다. 보이는 것과 내보낸 결과가 어긋나지 않는다.
- **시간 단위**: 내부는 정수 틱(1/90000초), 편집점은 프로젝트 프레임 레이트로 맞춘다 — 부동소수 누적 오차로 컷이 한 프레임 밀리는 일을 막는다.
- **Folderss 연동**: 미디어 패널에 Folderss 폴더 패널 탭(`CreateFolderPanel`) — 폴더에서 영상을 바로 타임라인으로 끌어 온다. 설정(ffmpeg 경로·최근 프로젝트)은 `IPluginManager` 설정.
- **안전**: 백그라운드 작업은 모두 try/catch + 취소 토큰, 프로세스는 창 닫힐 때 종료(Job Object).

## 3. 작업 목록 (얇은 세로 조각)

각 조각은 끝에서 끝까지 동작하는 상태로 끝낸다. 구현은 TDD(Core는 단위 시험 먼저, 화면은 헤드리스 시험), 조각마다 리뷰 후 `feat:` 커밋.

| # | 조각 | 끝났을 때 되는 것 | 시험 |
|---|---|---|---|
| S1 | **걷는 뼈대** | 이름 바꾸기(MyPlugin → VideoEditor), ffmpeg 찾기·내려받기 팝업, 파일 가져오기(Ctrl+I — 동영상·오디오·이미지)·ffprobe 정보, 미디어 패널 → 주 트랙에 끌어 놓기, 타임라인에 클립 표시, MP4 내보내기(Ctrl+E, 진행률·취소) | Core: 모델·RenderGraph(단일 트랙 trim+concat) / 통합: 실제 ffmpeg로 2클립 내보내기 후 ffprobe로 길이 확인 / 헤드리스: 가져오기 → 배치 → 내보내기 |
| S2 | **미리 보기** | 재생 헤드·타임코드(입력해 이동), Space 재생/정지, ←/→ 프레임, Shift+←/→ 1초, Home/End, 눈금자 클릭 탐색, 소리 재생 | Core: 시간 변환·프레임 맞춤 / 헤드리스: 탐색 후 미리 보기 프레임 캡처 |
| S3 | **컷 편집** | 선택·이동·가장자리 다듬기, 분할 Ctrl+B, 삭제/리플 삭제 Shift+Delete, Alt+[ / Alt+], 스냅, 마그네틱 주 트랙, 실행 취소/다시 실행, 확대/축소/맞춤(Ctrl+= / Ctrl+- / Shift+Z), 저장/열기(Ctrl+S/O) | Core: 편집 연산 표 시험(겹침·경계·잠금) / 헤드리스: 키로 편집 흐름 |
| S4 | **다중 트랙** | 동영상 덮어 쓰기 트랙(위가 덮음 — 영상 위 이미지·사진), 오디오 트랙(오디오 파일·배경 음악), 출력·소리·잠금, 트랙 추가/삭제·높이, 오디오 분리 Ctrl+Alt+D, 썸네일·파형 | Core: 그래프 overlay·amix / 통합: 2트랙 합성 프레임 비교 |
| S5 | **클립 속성** | 속성 패널: 위치·크기·회전·불투명도(동영상·이미지·글자), 음량·페이드 인/아웃(오디오), 속도(Ctrl+R) | Core: 그래프 인수 / 헤드리스: 값 바꾸면 미리 보기 변화 |
| S6 | **마무리** | 마커(M·Shift+M·Shift+↑/↓), In/Out(I/O)과 구간 내보내기, 글자 클립, 크로스페이드, 내보내기 설정(해상도·fps·품질·인코더) | Core + 통합 |

**GATE 1**: 2026-10-21 승인(아래 4절).

## 4. 정한 것 (GATE 1, 2026-10-21)

| 질문 | 결정 |
|---|---|
| FFmpeg 구하는 방법 | **설치된 것을 먼저 쓴다**(설정 경로 → PATH → winget 링크 폴더 → 플러그인 데이터 폴더). 없으면 **내려받기 팝업**: GitHub BtbN/FFmpeg-Builds 최신 win64 GPL 빌드를 받아 SHA-256을 같은 릴리스의 checksums로 확인한 뒤 플러그인 데이터 폴더에 푼다. [직접 지정…]으로 ffmpeg.exe를 고를 수도 있다. 플러그인 zip에는 넣지 않는다(배포하지 않으므로 GPL 빌드의 libx264를 써도 된다) |
| 저장소 | `zaruous/Folderss-video-editor` **공개** |
| 범위 | **S1~S6 전부** |
| 커밋(GATE 2) | 조각마다 시험·리뷰를 통과하면 **확인 없이 커밋·푸시** |
| 추가 요청 | 동영상뿐 아니라 **이미지·사진·오디오**도 편집 요소로: 이미지는 동영상 트랙에 길이를 자유롭게 늘리는 클립(사진 슬라이드, 영상 위 덮어 쓰기), 오디오 파일은 오디오 트랙(배경 음악·내레이션), 음량·페이드 인/아웃 |

## 5. 확인 방법

- `dotnet test tests/VideoEditor.Tests` — Core, CI(Linux)에서도
- `pwsh scripts/ui-test.ps1` — 숨은 데스크톱 화면 시험, 캡처는 shots
- 내보내기 정확도: 시험이 만든 짧은 색 막대 영상(ffmpeg `testsrc`)을 편집해 내보내고 ffprobe로 길이·프레임 수 비교
