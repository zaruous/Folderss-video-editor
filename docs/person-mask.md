# 인물 마스크 (MODNet, 이 PC에서만)

"사람에게만" 효과가 쓰는 마스크를 만든다. 원본에서 사람만 따낸 흑백 동영상(흰색 = 사람)이고, 사진 원본이면 흑백 PNG 한 장이다.
렌더 그래프는 `IPersonMaskProvider.Find`로 마스크를 찾아 효과를 사람 위에만 칠한다(마스크가 없으면 클립 전체에).
PRD에서 AI 기능은 서버·모델이 필요해 뺐다. 인물 분리는 음성 인식처럼 서버 없이 로컬 모델로 할 수 있어서 범위에 넣는다.

## 1. 쓰는 법

- 타임라인의 동영상·사진 클립 오른쪽 메뉴 [인물 마스크 만들기], 또는 미디어 패널에서 동영상·사진 하나를 고르고 오른쪽 메뉴 [인물 마스크 만들기].
  속성 패널의 효과 탭에서도 같은 명령(`EditorCommands.BuildPersonMask`)을 부른다.
- 처음이면 [인물 분리 모델 내려받기] 창이 뜬다. 모델 이름·크기·라이선스·출처·저작자 표시와 "모든 처리는 이 PC에서"를 보이고,
  [내려받기]를 눌러야 받는다. 받는 동안 진행률이 보이고 [취소]하면 받던 파일을 지운다(폴더도 남기지 않음).
- 진행 창이 뜨자마자 만든다. 프레임 진행률과 남은 시간이 보이고 [취소]하면 FFmpeg를 끝내고 만들던 파일을 지운 뒤 닫힌다.
  다 되면 닫히고 미리 보기를 다시 그리며, 상태 줄에 "인물 마스크를 만들었습니다 — 사람에게만 효과가 사람 위에만 그려집니다(이름, 걸린 시간, GPU 또는 CPU)"가 나온다.
- 진행 창에 **처리 장치**가 보인다: "처리 장치: GPU(DirectML · 그래픽 카드 이름)" 또는 "처리 장치: CPU(6스레드)".
  GPU를 쓰려다 CPU로 돌아갔으면 그 까닭이 붙는다(예: "— 플러그인에 DirectML.dll이 없어 GPU를 쓰지 못합니다", "— GPU(DirectML) 추론이 실패해 CPU로 이어 만듭니다: …").
- 설정 › 동영상 편집 [인물 마스크를 GPU(DirectML)로 만들기](기본 켬). GPU에서 마스크가 이상하거나 멈추면 끄면 CPU로만 만든다(`personMaskGpu` = `false`).
- 이 PC용 엔진이 플러그인에 없으면(Windows ARM64의 ARM64 프로세스 등) 모델을 받기 전에 상태 줄에 알리고 멈춘다(2.10).
- 마스크는 **원본 파일마다 하나**다. 같은 원본의 다른 클립(나눈 클립·복사한 클립)도 함께 쓴다.
  다시 부르면 만들어 둔 것을 쓰고("이미 만들어 두었습니다"), 원본 파일을 바꿔 넣으면(크기·고친 시각이 바뀜) 새로 만들어야 한다.

## 2. 정한 것

1. **모델: MODNet(사진 인물 매팅)의 ONNX 변환.** 크기와 SHA-256은 코드(`PersonMaskText.Model`)에 고정해 두고, 받은 파일과 맞춰 본다.

   | 항목 | 값 |
   |---|---|
   | 모델 | MODNet — Trimap-Free Portrait Matting in Real Time (Zhanghan Ke 외, AAAI 2022) |
   | 원 저장소·라이선스 | [github.com/ZHKKKe/MODNet](https://github.com/ZHKKKe/MODNet) · Apache License 2.0 |
   | ONNX 변환 | Hugging Face [Xenova/modnet](https://huggingface.co/Xenova/modnet) · Apache License 2.0 · 리비전 `fa2fa546052fba4c08921230a26cc69a333fca12` |
   | 받는 주소 | `https://huggingface.co/Xenova/modnet/resolve/fa2fa546052fba4c08921230a26cc69a333fca12/onnx/model.onnx` (리비전 고정) |
   | 크기·SHA-256 | 25,888,640바이트 · `07c308cf0fc7e6e8b2065a12ed7fc07e1de8febb7dc7839d7b7f15dd66584df9` |
   | 저작자 표시 | MODNet © Zhanghan Ke 외(Apache License 2.0), ONNX 변환 Xenova/modnet(Apache License 2.0) — 내려받기 창에도 적는다 |

   - 입력 `input` 1×3×H×W(RGB, (값/255 − 0.5)/0.5), 출력 `output` 1×1×H×W(사람일 정도 0~1). H·W는 32의 배수면 된다.
   - 고른 까닭 — 허용 라이선스(Apache-2.0·MIT·BSD)이고 ONNX로 고정 주소에서 받을 수 있는 후보를 이 PC에서 사진 12장(얼굴·상반신·전신·여러 사람·거리)으로 견주었다.
     ONNX Runtime 1.22.1, CPU 6스레드, 한 장 시간은 모델만 돌린 것이다.

     | 후보 | 라이선스 | 크기 | 입력 | 한 장 | 본 것 |
     |---|---|---|---|---|---|
     | **MODNet**(Xenova/modnet fp32) | Apache-2.0 | 25.9MB | 448×256 | 약 64ms | 머리카락·어깨 가장자리가 가장 또렷하고 부드럽다. 머리가 안 보이거나 아주 작은(먼) 사람은 놓친다 |
     | MODNet 8비트 양자화 | Apache-2.0 | 6.6MB | 512×512 | 약 330ms | fp32보다 다섯 배 느리다(CPU의 정수 합성곱) |
     | MediaPipe 셀피 분리(onnx-community 변환) | Apache-2.0 | 0.46MB | 256×256 | 약 3ms | 아주 빠르지만 테두리가 계단지고 뭉개진다 |
     | PP-HumanSeg 2023mar(OpenCV zoo 변환) | Apache-2.0 | 6.2MB | 192×192 | 약 5ms | 멀리 있는 사람도 찾지만 테두리가 흐릿하다 |
     | RobustVideoMatting | GPL-3.0 | — | — | — | 라이선스 때문에 뺐다 |

     효과를 사람 테두리에 맞춰 칠하므로 가장자리 품질을 먼저 보고 MODNet을 골랐다. 대신 한 번 만들어 두고(캐시) 다시 쓰고, 진행률·취소를 둔다.
2. **입력 크기: 짧은 변 256, 32의 배수, 긴 변 512까지**(`PersonMaskText.InputSize` — 16:9는 448×256, 9:16은 256×448, 4:3은 352×256).
   - 224(16:9는 384×224)로 줄이면 한 장 약 47ms지만 전신·춤 사진에서 구멍이 생겼다. 288은 약 106ms다.
   - 비율이 조금 어긋나도(1920×1080 → 448×256) 마스크는 원본 화면 전체에 늘여 맞추므로 사람 자리는 그대로 겹친다.
3. **엔진: ONNX Runtime 1.22.1(MIT) DirectML 판 — GPU(DirectML)를 먼저, 안 되면 CPU**(#43).
   - NuGet `Microsoft.ML.OnnxRuntime.DirectML`의 관리 DLL과 **Windows x64 네이티브 DLL만** 플러그인 zip의 `runtimes\win-x64\native\`에 넣는다
     (`.deps.json`이 가리키는 자리 — Folderss의 플러그인 로드 컨텍스트가 그대로 찾는다): `onnxruntime.dll`(DirectML 판, 16.5MB)과
     그것이 기대는 `Microsoft.AI.DirectML` 1.15.4의 `DirectML.dll`(18.5MB). DirectML 판의 `onnxruntime.dll`에는 CPU 실행도 들어 있다.
   - zip 크기(Debug 빌드): CPU 전용 판일 때 약 5.9MB → **약 16.6MB**(+약 10.6MB: `onnxruntime.dll`이 압축해 4.7 → 6.0MB, `DirectML.dll`이 압축해 9.3MB).
   - 따로 내려받지 않는 까닭: 고정 주소로 받을 수 있는 것은 GitHub 릴리스 zip(약 80MB, 디버그 기호 포함)이나 NuGet 패키지(DirectML 약 200MB, 모든 플랫폼·디버그 판)뿐이라
     DLL 둘을 위해 그만큼 받게 된다. zip에 넣으면 받기·검증 단계가 없다.
   - **CPU 결과는 예전과 같다**: 같은 입력에서 CPU 전용 판과 DirectML 판의 CPU 실행 출력이 바이트까지 같았다(사람 그림 448×256). GPU 출력은 CPU와 0~255에서 1~4 차이
     (1080p 10초 300프레임에서 2를 넘는 화소는 3,440만 개 중 1개).
   - 세션 만들기(`PersonSegmenter.Open`): GPU를 켰으면 `AppendExecutionProvider_DML(0)`(0번 어댑터 — Windows가 먼저 고르는 GPU, `EnableMemoryPattern = false`)로 먼저 열고,
     다음이면 CPU로 연다(까닭은 `Fallback`, 진행 창에 보임):
     - 플러그인에 `DirectML.dll`이 없거나 불러오지 못함, 0번 어댑터가 소프트웨어(GPU 없음 — DXGI `DXGI_ADAPTER_FLAG_SOFTWARE`로 본다).
     - DirectML 세션 만들기가 실패함(드라이버·DirectX 12 미지원 등).
   - 추론 중에 GPU가 실패하면(예: GPU 메모리 부족 `8007000E` — 1024×1024 큰 모델을 8GB GPU에서 돌릴 때 실제로 났다) 그 프레임부터 CPU 세션으로 바꿔 다시 돌린다.
     앞 프레임까지 만든 것과 시간 평활은 그대로 이어진다. 드라이버가 프로세스째 멈추게 하는 실패는 잡을 수 없어서 설정으로 GPU를 끌 수 있게 했다.
   - `DirectML.dll`도 경로로 먼저 불러 둔다(`OnnxRuntimeLibrary.Ensure` — `onnxruntime.dll`과 같은 폴더). `onnxruntime.dll`은 이것을 이름으로 찾으므로,
     먼저 불러 두지 않으면 System32의 옛 `DirectML.dll`을 잡을 수 있다. 단위 시험이 프로세스에 불린 `DirectML.dll`이 하나이고 플러그인 것인지 확인한다.
   - 0번 어댑터 이름은 DXGI(`CreateDXGIFactory1` → `EnumAdapters1(0)` → `GetDesc1`)로 읽어 보인다. 노트북처럼 GPU가 둘이면 0번이 내장 GPU일 수 있다(고르기는 다음에).
   - 불러오기(`OnnxRuntimeLibrary.Ensure`): 관리 DLL 옆 `runtimes\win-x64\native\onnxruntime.dll`을 경로로 먼저 불러 두고 `NativeLibrary.SetDllImportResolver`로 그것을 쓰게 한다.
     Windows 11에는 System32에 다른 버전의 `onnxruntime.dll`(Windows ML)이 있어서, 이름만으로 찾으면 그것을 잡아 API가 맞지 않을 수 있다.
     화면 시험이 플러그인 zip(Folderss와 같은 로드 컨텍스트)에서 불러온 경로가 zip 안의 것인지 확인한다.
   - Microsoft Visual C++ 재배포 패키지(x64)가 없으면 불러오지 못한다 — 설치 주소를 알린다(whisper.cpp와 같음).
   - Windows 공식 빌드는 원격 측정(TraceLogging)이 켜져 있어 `OrtEnv.DisableTelemetryEvents()`로 끈다.
   - 스레드는 논리 코어의 절반(1~8): 6코어 12스레드에서 6스레드가 약 64ms, 12스레드는 하이퍼스레딩끼리 다퉈 120ms를 넘었다. 프레임 사이에 스레드가 헛돌지 않게 한다(`allow_spinning=0`).
   - 라이선스 고지: ONNX Runtime(DirectML 판)의 `LICENSE`·`ThirdPartyNotices.txt`를 `licenses\onnxruntime-*`로,
     DirectML의 `LICENSE.txt`(Microsoft 소프트웨어 사용 조건 — Windows용 앱·서비스에 넣어 배포하는 것을 허용, 역설계 금지, 고지를 지우지 말 것)·`ThirdPartyNotices.txt`를
     `licenses\directml-*`로 zip에 넣는다. DirectML은 MIT가 아니다(헤더 등 코드 부분만 `LICENSE-CODE.txt` MIT).
4. **만드는 순서**(`PersonMaskService.Build`)
   1. 디코더 FFmpeg: `-i 원본 -map 0:V:0 -an -sn -dn -vf fps=레이트,scale=W:H:flags=bilinear -f rawvideo -pix_fmt rgb24 pipe:1`
      (사진은 `-frames:v 1 -vf scale=W:H`). 한 사슬에 scale은 하나만 쓴다.
   2. 프레임마다 모델을 돌리고 후처리한다(`PersonMaskText.ToMask`).
      - 가벼운 시간 평활: 앞 프레임과 차이가 작으면 절반만 따라가고, 96(0~255) 넘게 바뀌면 바로 따라간다. 배경의 잔떨림·깜빡임은 줄고 움직임은 늦지 않다.
      - 거의 0·거의 1은 딱 0·255로. 평평한 곳이 잘 줄어들고 효과가 배경에 새지 않는다.
   3. 인코더 FFmpeg: `-f rawvideo -pix_fmt gray -s W×H -framerate 레이트 -i pipe:0 -c:v ffv1 -level 3 -g 1 -pix_fmt gray -f matroska 이름.mkv.part`
      (사진은 `-c:v png -f image2 -update 1`). FFV1은 무손실이고 FFmpeg에 늘 들어 있으며, `-g 1`로 모든 프레임이 키 프레임이라 어느 위치든 바로 읽힌다(미리 보기가 한 장씩 -ss로 읽음).
   - 디코딩·인코딩은 다른 프로세스라 추론과 겹친다. 파이프 읽기·쓰기·후처리는 프레임당 1ms 안이고 시간은 거의 모두 추론이다.
   - 만드는 동안 원본을 바꿔 넣었으면 그 마스크는 버린다. 취소하면 두 FFmpeg를 끝내고 `.part`를 지운다. 만들기는 한 번에 하나(CPU·GPU를 다 쓰므로).
5. **시간축** — 마스크는 원본 화면과 같은 시간축이어야 한다. 렌더 그래프는 원본과 같은 `-t`·속도로, `-ss`는 화면 시작(`MediaInfo.VideoStart`)만큼 당겨 마스크를 읽는다.
   - 원본 평균 프레임 레이트(`avg_frame_rate`, 없으면 `r_frame_rate`, 모르면 30)로 고정한다. 60을 넘으면 절반씩(120 → 60, 100 → 50) — 원본 프레임과 시각이 그대로 겹친다.
   - 마스크는 첫 화면부터 만든다 — 마스크 프레임 k는 원본 화면 시작 + k/레이트 초다. 동영상 스트림이 파일 시작보다 늦게 시작하면(앞에 소리만 있는 파일, 스트림 복사로 자른 파일)
     그래프가 원본을 `-ss`로 읽을 때 그 틈을 건너뛰므로, 마스크는 그 틈(ffprobe의 화면 `start_time` − 파일 `start_time`, 미디어를 읽을 때 기록)만큼 앞당겨 읽어야 같은 장면이다.
     (예전 형식 1은 틈을 첫 화면으로 채워 이런 파일에서 마스크가 늦었다 — 형식 2로 올려 다시 만든다. 예전 프로젝트의 미디어는 열 때 다시 읽는다 —
     `MediaInfo.ProbeVersion`(읽은 판, 예전 프로젝트는 0)이 `CurrentProbe`(1 = 화면 시작 기록)보다 낮은 동영상은 파일이 그대로여도 `MediaProbe.IsStale`이 다시 읽을 것으로 본다.
     FFmpeg가 없으면 다시 읽지 못해 그동안은 틈 0으로 본다.)
   - 가변 프레임 레이트 원본은 평균 레이트로 고정하면서 가장 가까운 프레임을 쓴다 — 어긋나도 반 프레임 안이다.
   - 회전 정보가 있는 동영상(휴대폰 세로 영상)은 FFmpeg 기본대로 돌려서 읽는다(렌더 그래프와 같다).
   - 진행률의 분모는 길이(초) × 마스크 레이트다.
6. **크기·속도**(이 PC, 6코어 12스레드)
   - 1080p 30fps 10초(300프레임): 23.9초(프레임당 약 79ms — FFmpeg와 함께 돌 때 추론이 약 72ms), 마스크 792KB(1분에 약 4.7MB).
   - 그래서 1분짜리 1080p 클립은 2분 남짓 걸린다. 사진은 1초 안.
   - GPU(DirectML, #43 — Windows 11, 6코어 12스레드, NVIDIA GeForce RTX 3070 8GB, 드라이버 610.88):

     | 잰 것 | CPU(6스레드) | GPU(DirectML) |
     |---|---|---|
     | 모델만, 448×256 한 장(가운데값) | 67~71ms | **8~11ms** |
     | 1080p 30fps 10초 사람이 지나가는 합성 영상(300프레임), 만들기 전체 | 20.7~20.9초(장당 69ms) | **3.9~5.5초(장당 13~18ms)** |
     | 세션 열기 | 0.1~0.2초 | 0.4~0.7초 |

     GPU로는 모델이 7~8배, 만들기 전체가 4~5배 빠르다. GPU 쪽은 FFmpeg 1080p 디코딩과 세션 열기가 차지하는 몫이 커진다.
7. **캐시**(데이터 폴더`\person-mask`)
   - 마스크: `masks\<경로 해시 16자>-<크기·고친 시각·모델·형식 해시 16자>.mkv`(사진은 `.png`). 파일 이름에 원본 이름을 쓰지 않는다.
     원본을 바꿔 넣으면 뒤쪽이 달라져 옛 마스크를 쓰지 않는다. 같은 원본의 새 마스크를 만들면 앞쪽이 같은 옛 마스크를 지운다.
     후처리를 바꾸면 `PersonMaskText.FormatVersion`을 올려 예전 마스크를 다시 만든다.
   - 찾기(`Find`)는 디스크만 본다(파일 정보 하나와 해시, 있는지 확인 둘). `.part`(만드는 중)는 보지 않는다. 찾은 마스크는 사용 시각을 지금으로 적는다(NTFS는 접근 시각을 잘 갱신하지 않음).
   - 마스크 옆 `<같은 이름>.source`에 원본 전체 경로를 적는다(이름이 해시라 원본을 알 수 없어서 — 정리용).
   - `.part`에 쓰고 다 되면 이름을 바꾼다. 편집기를 시작할 때(배경) 정리한다(`Cleanup`):
     - 한 시간 넘게 그대로인 `.part`(끊긴 만들기·내려받기).
     - 원본 기록이 있는데 원본 폴더는 있고 파일이 없거나(지움·옮김), 지금 원본으로 지은 이름이 그 마스크가 아니면(바꿔 넣음) 마스크와 기록. 원본 폴더째 없으면 빠진 USB·네트워크 드라이브일 수 있어 남긴다. 기록이 없는 예전 마스크는 원본을 모르므로 남긴다.
     - 마스크 없이 남은 기록.
     - 그래도 10GB(`MaxCacheBytes`)를 넘으면 오래 안 쓴 마스크부터(사용 시각 = 접근·수정 시각 중 늦은 것). 하루 안에 쓴 것은 넘어도 남긴다(다시 만드는 데 몇 분). 쓰는 중이라 못 지운 것은 건너뛴다.
   - 모델: `models\modnet-fa2fa546.onnx`(받는 동안 `.part`). 쓸 때는 크기를, 받을 때는 SHA-256까지 확인한다.
8. **개인 정보**: 동영상·사진은 이 PC에서만 처리한다. 인터넷은 모델 파일을 처음 한 번 Hugging Face에서 받을 때만 쓰고(본체와 같은 프록시 방식, User-Agent는 플러그인 이름),
   ONNX Runtime 원격 측정은 끈다.
9. **한계**: 사진 인물(상반신·전신) 매팅 모델이라 화면에 작게 나온 먼 사람, 머리가 안 보이는 사람, 뒤돌아선 사람은 놓치기 쉽다(11의 비교).
10. **Windows ARM64**(#43 — ARM64 PC가 없어 실행은 확인하지 못했다. 아래는 패키지·코드를 보고 정한 것이다)
    - 패키지에는 ARM64 것도 있다: ONNX Runtime 1.22.1(CPU 판·DirectML 판 모두) `runtimes\win-arm64\native\onnxruntime.dll`, DirectML 1.15.4 `bin\arm64-win\DirectML.dll`(arm64ec 판도).
      DirectML도 ARM64 Windows에서 돈다(DirectX 12 드라이버가 있는 GPU).
    - 플러그인 zip에는 **win-x64 것만** 넣는다. ARM64 것까지 넣으면 압축해 약 15MB(onnxruntime 6.0MB + DirectML 9.2MB)가 더해져 zip이 약 31MB가 되는데,
      돌려 볼 수 없는 플랫폼이라 넣지 않았다. 플러그인의 다른 부분(미리 보기 도우미 `helper\VideoEditor.Helper.exe`, 음성 인식 whisper.cpp)도 x64 실행 파일이다.
    - Folderss가 **ARM64 프로세스**로 돌면 런타임 식별자가 `win-arm64`라 `runtimes\win-arm64\native\onnxruntime.dll`을 찾다 없다 →
      [인물 마스크 만들기]가 모델을 받기 전에 "이 PC(win-arm64)에서 쓸 인물 분리 엔진(ONNX Runtime)이 플러그인에 들어 있지 않습니다 — 인물 마스크는 Windows x64에서만 만들 수 있습니다."를
      상태 줄에 알리고 멈춘다(`OnnxRuntimeLibrary.Unsupported`, 멈추거나 튕기지 않는다). 관리 DLL(`Microsoft.ML.OnnxRuntime.dll`)은 AnyCPU라 불러도 문제없다.
    - Folderss가 **x64 프로세스**(ARM64 Windows의 x64 에뮬레이션)로 돌면 `win-x64` DLL을 그대로 쓴다. 이론상 에뮬레이션으로 돌지만 확인하지 못했다.
    - ARM64를 지원하려면: PackPlugin에서 `RuntimeIdentifier == 'win-arm64'`인 네이티브와 `bin\arm64-win\DirectML.dll`도 `runtimes\win-arm64\native\`에 넣으면 된다(코드는 RID로 찾으므로 바꿀 것 없음).
      ARM64 PC에서 확인할 수 있을 때 한다.
11. **모델 비교**(#43, 연구·실측만 — 쓰는 모델은 바꾸지 않았다)
    - 이 PC(RTX 3070), ONNX Runtime 1.22.1 DirectML 판, CPU 6스레드. 시간은 모델만 돌린 한 장(가운데값)이다. 모델은 저장소 밖(임시 폴더)에 받았다.
    - 사진은 Wikimedia Commons에서 받은 10장(CC0·CC BY·CC BY-SA·퍼블릭 도메인, 1280px — 저장소에 넣지 않음): 뒤돈 사람 3(가까움·중간·작음), 군중 2, 작은 등산객 1, 아주 작은(약 10px) 등산객 1,
      아이들 놀이터 항공 사진 1, 앞모습 1, 가족(사람 둘·개) 1. 사람 수가 적어 "놓친 비율"은 숫자로 내지 않고 사진마다 눈으로 보았다(주관적).
      각 모델의 입력 크기로 늘여(MODNet만 비율 유지) 넣었다.

      | 모델 | 라이선스(출처에서 확인) | 크기 | 입력 | CPU | GPU | 본 것 |
      |---|---|---|---|---|---|---|
      | **MODNet**(지금, Xenova/modnet fp32) | Apache-2.0 | 25.9MB | 448×256 등 | 67ms | 8ms | 앞모습·뒤돈 사람(가까움·중간)은 가장자리까지 깔끔. 군중은 1~2명만, 작은 등산객·아주 작은 사람·놀이터 아이들은 놓침, 눈 덮인 길의 작은 뒷모습은 놓치고 가로등을 사람으로 봄. 개도 사람으로 칠함 |
      | MediaPipe 셀피(onnx-community, 256×256) | Apache-2.0 | 0.46MB | 256×256 | 4ms | 2ms | 테두리가 계단지고 배경이 섞임. 작은 등산객은 찾지만 군중·아주 작은 사람은 놓침 |
      | MediaPipe 셀피 가로(onnx-community) | Apache-2.0 | 0.46MB | 256×144 | 2ms | 2ms | 위와 비슷하고 더 거침. 세로 사진은 늘여서 망가짐. GPU 이득 없음 |
      | PP-HumanSeg 2023mar(OpenCV zoo, PaddleSeg) | Apache-2.0 | 6.2MB | 192×192 | 6ms | 1ms | 군중의 사람 대부분을 찾는 유일한 후보. 대신 테두리가 크게 흐리고 작은 사람은 희미하게만 |
      | U²-Net 사람 분리(u2net_human_seg, rembg 배포) | Apache-2.0(U-2-Net 저장소). 학습 데이터 조건은 확인 못 함 | 176MB | 320×320 | 464ms | 13ms | 가장 많이 찾음: 작은 뒷모습·작은 등산객·아주 작은 사람까지, 군중 8명쯤. 가장자리도 또렷하고 개는 뺌. 놀이터 아이들은 일부, 빈 곳에 헛것 한 줄기 |
      | BiRefNet lite(onnx-community, fp32) | MIT | 224MB | 1024×1024 | 9~13초 | 못 돎 | 사람 전용이 아닌 일반 전경 분리. 8GB GPU에서 DirectML 메모리 부족(8007000E)으로 추론 실패 — 품질은 보지 못함 |
      | RobustVideoMatting | GPL-3.0 | — | — | — | — | 라이선스 때문에 뺐다(전과 같음) |

    - 정리: 놓치는 사람을 가장 줄이는 것은 U²-Net 사람 분리다. 다만 받는 크기가 7배(176MB)이고 CPU로는 장당 0.46초(1분 1080p 30fps 클립에 약 14분)라
      GPU가 있을 때만 쓸 만하다. 군중만 보면 PP-HumanSeg가 가장 많이 찾지만 테두리가 흐려 "사람에게만" 효과가 번진다.
      바꿀지는 이슈에서 정한다(후보: GPU가 있으면 U²-Net, 없으면 MODNet처럼 둘을 고를 수 있게 — 이때 U²-Net 학습 데이터 조건을 먼저 확인).

## 3. 시험

- `PersonMaskTests`(모델·네트워크 없이 CI에서 돈다)
  - 모델 고정 값(리비전 고정 주소·크기·SHA-256 모양·라이선스), 입력 크기, 마스크 레이트·프레임 수를 본다.
  - 캐시 이름(경로·크기·시각·모델에 따라 바뀜, 원본 이름 없음)과 찾기(있음·없음·원본을 바꿔 넣음·`.part`는 무시, 찾으면 사용 시각), `.part` 정리, 원본을 지웠거나 바꾼 마스크 정리(빠진 드라이브·기록 없는 예전 마스크는 남김, 홀로 남은 기록), 크기 상한(오래 안 쓴 것부터, 하루 안에 쓴 것은 남김)을 본다.
  - FFmpeg 인수(디코더: 파일만·첫 동영상 스트림·고정 레이트·scale 하나, 인코더: FFV1 모든 프레임 키·.part에 형식 지정, 사진은 PNG), 모델 입력·출력 변환(시간 평활)을 본다.
  - 가짜 응답으로 모델 내려받기 — 내용이 다르면(SHA-256) 거부하고 파일·폴더를 남기지 않는지 본다.
- `PersonMaskIntegrationTests`는 `FOLDERSS_VE_PERSONMASK`에 모델 파일(`modnet-fa2fa546.onnx`, 바로 아래나 `models\` 아래)이 든 폴더를 줄 때만 돈다.
  - 시험용 사람 사진이 없어 FFmpeg로 사람 모양(머리·머리카락·목·어깨·몸통)을 그린다. 모델은 이것을 사람으로 따낸다.
  - 사진: 흑백 PNG 448×256, 사람 자리는 희고(평균 200 넘음) 벽은 검다(30 아래). 원본을 바꿔 넣으면 다시 만들고 옛 것은 지운다.
  - 동영상(사람이 초당 60픽셀 움직이는 2초 25fps): 흑백 FFV1, 프레임 수·길이가 원본과 같고, 첫·마지막 프레임에서 마스크가 사람을 따라간다.
    처음 진행률에서 취소하면 아무것도 남지 않고, 다시 부르면 만들어 둔 것을 쓴다.
  - 사진은 처음에 CPU로만(GPU 끔) 만들고(처리 장치 "CPU(…)"), 다시 만들 때는 기본(GPU가 되면 GPU) — 두 마스크가 0~255에서 2 안으로 같다.
  - `Segmenter_UsesGpuWhenItCan_FallsBackToCpu_SameMask`: CPU로만 연 세션은 "CPU(…스레드)"이고 까닭이 없다. 자동이면 GPU로 돌거나(그러면 CPU와 마스크 차이 2 안),
    GPU를 못 쓰면 까닭을 남기고 CPU로. 시험용 실패(`PersonSegmenter.SimulatedGpuFault`)로 GPU 열기 실패 → CPU로 열고 까닭, GPU 추론 실패 → 그 프레임부터 CPU — 두 경우 모두 CPU와 출력이 똑같다.
    Windows에서는 불린 `DirectML.dll`이 하나이고 `onnxruntime.dll` 옆의 것인지 본다. CPU·GPU 장당 시간을 출력에 남긴다.
  - `OnnxRuntime_OnlyWinX64InThePlugin_Arm64IsToldClearly`(모델 없이): win-x64만 든 플러그인 폴더에서 win-arm64는 엔진을 찾지 못하고, 알림에 "이 PC(win-arm64)"와 "Windows x64에서만"이 있다.
- 화면 시험 `PersonMaskTests`: 모델이 없으면 내려받기 창(내용 확인 후 [취소] → 폴더가 생기지 않음, 오디오에는 메뉴 없음).
  `FOLDERSS_VE_PERSONMASK`가 있으면 클립 오른쪽 메뉴 → 진행 창(프레임 진행률) → 상태 줄 알림 → 미디어 패널에서 다시 부르면 만들어 둔 것을 쓴다.
  ONNX Runtime이 zip 안의 DLL에서 불렸는지, 처리 장치(GPU를 쓸 수 있으면 "처리 장치: GPU(DirectML…"와 상태 줄 끝 ", GPU)." 그리고 zip 안의 `DirectML.dll`,
  못 쓰면 "CPU(…"와 그 까닭)도 본다.
  `PersonMask_GpuSettingOff_BuildsOnCpu`: 설정 탭의 [인물 마스크를 GPU(DirectML)로 만들기](기본 켬)를 끄고 저장하면 처리 장치가 "CPU(n스레드)"(까닭 없음)이고 상태 줄 끝이 ", CPU).", 다시 켜면 설정을 지운다.

## 4. 범위 밖(다음에)

- 더 빠른·덜 놓치는 모델로 바꾸기(2.11의 비교 — 정해지면), 사람 여럿 가운데 고르기, 마스크 손보기.
- GPU가 여럿일 때 고르기(지금은 0번 어댑터).
- Windows ARM64 네이티브 DLL 넣기(2.10 — ARM64 PC에서 확인할 수 있을 때).
