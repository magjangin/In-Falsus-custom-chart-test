using System;
using System.IO;
using MelonLoader;
using UnityEngine;
using UnityEngine.Bindings;
using UnityEngine.Video;
using Il2Cpp_J;
using Il2Cppifapp.Game;
using Il2Cppifapp.Game.Scenes;
using Il2Cppifapp.Game.UI.Common;

namespace InFalsusMods
{
    /// <summary>
    /// 커스텀 BGA — hwa/video.mp4 를 플레이 씬 배경(GameScene.backgroundObject) 자리에 그린다.
    ///
    /// VideoPlayer 가 RenderTexture 에 영상을 그리고, 배경 머티리얼을 복제해 mainTexture 를 그 텍스처로 바꾼 뒤
    /// Constrained2D._irA 로 배경에 꽂는다. 게임 자체 영상 플레이어(C2DVideoPlayer: videoPlayer + targetOutput
    /// Constrained2D + RenderTexture + Material)와 같은 구조다. 영상이 배경 깊이에 그대로 들어가서 노트·UI 뒤에 보인다.
    ///
    /// 2026-09-24 1차 시도(CameraFarPlane)는 재생·싱크(-43ms)는 됐지만 화면에 안 보였다 — 카메라 먼 평면은 씬 전체보다
    /// 뒤인데, 배경이 화면 전체를 덮는 불투명 메시(Unlit/StenciledUnlitSpriteOpaque, 레이어 6)라 영상을 덮었다.
    ///
    /// 머티리얼은 영상 첫 프레임이 나온 뒤(재생 시작 후)에 꽂는다. 그 전엔 원래 배경이 보인다.
    /// 게임이 플레이 중 배경 머티리얼을 다시 꽂으면 틱에서 알아채고 우리 것을 다시 꽂는다.
    ///
    /// 싱크: timeReference = ExternalTime 으로 두고 매 프레임 게임 재생 위치(_Dg._miA, 초)를 externalReferenceTime 에
    /// 넣는다. VideoPlayer 가 그 시계에 맞춰 프레임을 건너뛰거나 반복한다. 게임이 멈추면 시계도 멈춰 영상이 같이 선다.
    /// 영상 소리는 끈다 — 음악은 FMOD 가 튼다(AudioInjector).
    ///
    /// 대상은 새 곡 슬롯(hwa)의 차트일 때만. 플레이 씬을 벗어나면 만든 것을 전부 지운다.
    /// </summary>
    internal static class VideoInjector
    {
        // 목적을 달성하면 false 로 끌 것
        public static bool Enabled { get; set; } = true;

        // 확인이 끝나면 false — 플레이 중 화면 캡처는 프레임을 잡아먹는다
        private static readonly bool DebugCapture = true;
        private static readonly double[] CaptureAtSeconds = { 8.0, 40.0 };

        // 싱크 확인용 로그 시점 (곡 재생 위치, 초). 플레이 중 로그 한 줄이 수십 ms 라 몇 번만 찍는다 (docs/04).
        private static readonly double[] SyncLogAtSeconds = { 5.0, 30.0, 60.0, 90.0 };

        // 게임이 배경 머티리얼을 다시 꽂아서 우리가 되돌린 횟수 — 로그는 처음 몇 번만
        private const int MaxReapplyLogs = 5;

        // 새 곡 슬롯(NewSongInjector)의 슬러그. 원곡(alamode)은 영상 없이 원래대로 둔다.
        private const string TargetSlug = "hwa";
        private const string FileName = "video.mp4";

        private static MelonLogger.Instance _logger;
        private static bool _broken;

        // 이번 플레이 씬 상태 — 씬을 벗어나면 Teardown 에서 초기화
        private static VideoPlayer _vp;
        private static RenderTexture _rt;
        private static Material _mat;
        private static Constrained2D _bg;
        private static int _appliedMatId;   // 배경에 꽂은 우리 머티리얼. 0 = 아직 안 꽂음
        private static bool _confirmed;     // _H 가 우리 머티리얼을 돌려주는 걸 확인했는지 (_irA 는 다음 프레임에 반영된다)
        private static int _reapplies;
        private static _Dg _player;
        private static bool _skipThisScene;
        private static bool _loggedPrepared;
        private static int _nextSyncLog;
        private static int _nextCapture;

        public static void Init(MelonLogger.Instance logger)
        {
            _logger = logger;
            if (!Enabled) return;

            logger.Msg($"[VideoInjector] 준비 완료 (대상 차트: '{TargetSlug}' → hwa/{FileName}, 배경 머티리얼 교체)");
        }

        /// <summary>디텍터 틱(10프레임)마다 — 붙이기·떼기, 배경이 바뀌었으면 다시 꽂기. 싱크는 매 프레임 Update 에서.</summary>
        public static void Tick(MelonLogger.Instance logger)
        {
            if (!Enabled || _broken) return;

            try
            {
                if (!SceneRefs.IsGameScene)
                {
                    Teardown(logger);
                    return;
                }

                if (_vp != null)
                {
                    KeepBackground(logger);
                    return;
                }
                if (_skipThisScene) return;

                var cam = SceneRefs.MainCamera;
                if ((UnityEngine.Object)cam == null) return;

                // 차트는 씬보다 늦게 올라온다 — 비어 있으면 다음 틱에 다시 본다
                string chartId = GetChartId();
                if (chartId == null) return;

                if (!IsTargetChart(chartId))
                {
                    _skipThisScene = true;
                    logger.Msg($"[VideoInjector] 대상 곡 아님 ('{chartId}') — 이번 플레이는 영상 없음");
                    return;
                }

                Setup(cam, chartId, logger);
            }
            catch (Exception ex)
            {
                _broken = true;
                logger.Error($"[VideoInjector] 예외 — 영상 주입 중단: {ex}");
            }
        }

        /// <summary>매 프레임 — 게임 재생 위치를 영상 시계로 넘긴다.</summary>
        public static void Update()
        {
            if (_vp == null || _broken) return;

            try
            {
                // 카메라와 함께 파괴됐다 — 참조는 남겨 둬야 Tick 이 다시 붙이지 않는다. 정리는 Teardown 이 한다.
                if ((UnityEngine.Object)_vp == null) return;

                if (!_vp.isPrepared) return;

                if (!_loggedPrepared)
                {
                    _loggedPrepared = true;
                    OnPrepared();
                }

                if (_player == null) _player = FindPlayer();
                if (_player == null) return;

                double t = _player._miA();
                _vp.externalReferenceTime = Math.Max(0.0, t);

                // 곡이 시작되면 재생. 재시작(시계가 앞으로 돌아감)으로 영상이 끝나 있으면 다시 튼다.
                if (t > 0.0 && t < _vp.length && !_vp.isPlaying)
                {
                    _vp.Play();
                    _logger?.Msg($"[VideoInjector] 재생 시작 (곡 위치 {t:F3}초)");
                }

                // 첫 프레임이 텍스처에 들어간 뒤에 배경을 바꾼다 — 그 전에 꽂으면 빈 텍스처가 보인다
                if (_appliedMatId == 0 && _vp.isPlaying && _vp.frame >= 0)
                {
                    ApplyBackground("첫 프레임");
                }

                LogSyncCheckpoint(t);
                CaptureCheckpoint(t);
            }
            catch (Exception ex)
            {
                _broken = true;
                _logger?.Error($"[VideoInjector] 예외 — 영상 주입 중단: {ex}");
            }
        }

        private static void Setup(Camera cam, string chartId, MelonLogger.Instance logger)
        {
            string path = Path.Combine(HwaPaths.HwaDirectory ?? string.Empty, FileName);
            if (!File.Exists(path))
            {
                _skipThisScene = true;
                logger.Warning($"[VideoInjector] 파일 없음: {path} — 영상 없이 진행");
                return;
            }

            var scene = SceneRefs.GameScene;
            var bg = scene != null ? scene.backgroundObject : null;
            if ((UnityEngine.Object)bg == null) return;   // 배경이 아직 없다 — 다음 틱에

            logger.Msg("══════════════════════════════════════════════════════════════════════════");
            logger.Msg($"[VideoInjector] 플레이 씬 영상 붙이기 — 차트 '{chartId}'");
            LogBackground(bg, logger);

            // RenderTexture 모드라 어디 붙어도 되지만, 1차 시도에서 문제없던 카메라 오브젝트에 그대로 붙인다.
            // 렌더 텍스처는 영상 크기를 알고 나서(준비 완료 후) 만든다.
            var vp = cam.gameObject.AddComponent<VideoPlayer>();
            vp.playOnAwake = false;
            vp.source = VideoSource.Url;
            vp.url = path;
            vp.audioOutputMode = VideoAudioOutputMode.None;
            vp.renderMode = VideoRenderMode.RenderTexture;
            vp.aspectRatio = VideoAspectRatio.FitOutside;   // 텍스처를 꽉 채우고 넘치는 쪽은 자른다
            vp.isLooping = false;
            vp.skipOnDrop = true;
            vp.timeReference = VideoTimeReference.ExternalTime;
            vp.externalReferenceTime = 0.0;
            vp.Prepare();

            _vp = vp;
            _bg = bg;
            logger.Msg($"  └ VideoPlayer 추가('{cam.name}') → Prepare 호출 (url: {path})");
            logger.Msg("══════════════════════════════════════════════════════════════════════════");
        }

        /// <summary>영상 크기를 알았으니 렌더 텍스처를 만들어 연결한다.</summary>
        private static void OnPrepared()
        {
            int w = (int)_vp.width;
            int h = (int)_vp.height;

            _rt = new RenderTexture(w, h, 0)
            {
                name = "hwa_video",
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            _rt.Create();
            _vp.targetTexture = _rt;

            _logger?.Msg($"[VideoInjector] 영상 준비 완료: {w}x{h}, {_vp.length:F3}초, {_vp.frameRate:F2}fps, " +
                         $"{_vp.frameCount}프레임 → 렌더 텍스처 {_rt.width}x{_rt.height} ({_rt.format}) 연결");
        }

        /// <summary>배경 머티리얼을 복제해 영상 텍스처를 넣고 배경에 꽂는다.</summary>
        private static void ApplyBackground(string reason)
        {
            if ((UnityEngine.Object)_bg == null || (UnityEngine.Object)_rt == null) return;

            var current = _bg._H;
            if ((UnityEngine.Object)current == null) return;

            // 게임이 다른 배경 머티리얼로 바꿨으면 그것을 기준으로 다시 복제한다 (셰이더·스텐실 설정 유지)
            if ((UnityEngine.Object)_mat == null || current.shader.GetInstanceID() != _mat.shader.GetInstanceID())
            {
                if ((UnityEngine.Object)_mat != null) UnityEngine.Object.Destroy(_mat);

                _mat = new Material(current)
                {
                    name = "hwa_video_bg",
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                };
            }
            _mat.mainTexture = _rt;

            // 넘긴 머티리얼을 복제 없이 그대로 쓰지만 _H 에는 다음 프레임에야 반영된다(2026-09-24 실측) — 확인은 KeepBackground 가 한다
            _bg._irA(_mat, false);
            _appliedMatId = _mat.GetInstanceID();
            _confirmed = false;

            _logger?.Msg($"[VideoInjector] 배경 교체({reason}): '{current.name}' → '{_mat.name}' " +
                         $"(곡 위치 {(_player != null ? _player._miA() : 0.0):F3}초)");
        }

        /// <summary>틱마다 — 게임이 배경 머티리얼을 다시 꽂았으면 우리 것으로 되돌린다.</summary>
        private static void KeepBackground(MelonLogger.Instance logger)
        {
            if (_appliedMatId == 0) return;   // 아직 첫 적용 전

            if ((UnityEngine.Object)_bg == null)
            {
                var scene = SceneRefs.GameScene;
                _bg = scene != null ? scene.backgroundObject : null;
                if ((UnityEngine.Object)_bg == null) return;
                logger.Msg("[VideoInjector] 배경 오브젝트가 바뀜 — 새 배경에 다시 꽂는다");
                _appliedMatId = 0;
                ApplyBackground("배경 오브젝트 교체");
                return;
            }

            var current = _bg._H;
            if ((UnityEngine.Object)current == null) return;

            if (current.GetInstanceID() == _appliedMatId)
            {
                if (!_confirmed)
                {
                    _confirmed = true;
                    var mr = _bg.constrained2DTransform != null ? _bg.constrained2DTransform.meshRenderer : null;
                    string shared = (UnityEngine.Object)mr != null && (UnityEngine.Object)mr.sharedMaterial != null
                        ? mr.sharedMaterial.name
                        : "null";
                    var tex = current.mainTexture;
                    logger.Msg($"[VideoInjector] 배경 적용 확인: 텍스처 '{((UnityEngine.Object)tex != null ? tex.name : "null")}', " +
                               $"MeshRenderer '{shared}'");
                }
                return;
            }

            // 확인 전의 불일치는 _irA 가 아직 반영되기 전일 뿐이다
            if (!_confirmed) return;

            _reapplies++;
            if (_reapplies <= MaxReapplyLogs)
            {
                logger.Msg($"[VideoInjector] 게임이 배경 머티리얼을 '{current.name}' 로 다시 꽂음 ({_reapplies}회째) — 되돌림");
            }
            ApplyBackground("게임이 다시 꽂음");
        }

        /// <summary>
        /// 게임 종료 직전 — 디코더를 먼저 세우고 렌더 텍스처를 놓는다.
        /// 2026-09-24 alamode 플레이 중(일시정지 메뉴) 종료한 세 번 모두 Player.log 가 FMOD 종료에서 끊기고 게임이 CPU 코어 하나를
        /// 잡은 채 멈췄다. 이 정리를 넣은 뒤에도 똑같이 멈췄다. 다른 곡(bethere) 플레이 중 종료는 정상이라 원인은 영상 또는
        /// 플레이 씬의 커스텀 음원 — 영상만 끄고 alamode 로 비교할 것 (docs/01 7장).
        /// </summary>
        public static void Shutdown()
        {
            if (_vp == null && _rt == null) return;

            try
            {
                if ((UnityEngine.Object)_vp != null)
                {
                    _vp.Stop();
                    _vp.targetTexture = null;
                    UnityEngine.Object.DestroyImmediate(_vp);
                }
                if ((UnityEngine.Object)_rt != null)
                {
                    _rt.Release();
                    UnityEngine.Object.DestroyImmediate(_rt);
                }
                _vp = null;
                _rt = null;
                _logger?.Msg("[VideoInjector] 게임 종료 — 영상 정지, 렌더 텍스처 해제");
            }
            catch (Exception ex)
            {
                _logger?.Warning($"[VideoInjector] 종료 정리 실패: {ex.Message}");
            }
        }

        private static void Teardown(MelonLogger.Instance logger)
        {
            if (_vp != null || _rt != null || _mat != null)
            {
                try
                {
                    if ((UnityEngine.Object)_vp != null) UnityEngine.Object.Destroy(_vp);
                    if ((UnityEngine.Object)_rt != null)
                    {
                        _rt.Release();
                        UnityEngine.Object.Destroy(_rt);
                    }
                    if ((UnityEngine.Object)_mat != null) UnityEngine.Object.Destroy(_mat);
                    logger.Msg($"[VideoInjector] 플레이 씬 종료 — VideoPlayer·렌더 텍스처·머티리얼 제거 (배경 되돌림 {_reapplies}회)");
                }
                catch { }
            }

            _vp = null;
            _rt = null;
            _mat = null;
            _bg = null;
            _appliedMatId = 0;
            _confirmed = false;
            _reapplies = 0;
            _player = null;
            _skipThisScene = false;
            _loggedPrepared = false;
            _nextSyncLog = 0;
            _nextCapture = 0;
        }

        private static _Dg FindPlayer()
        {
            var scene = SceneRefs.GameScene;
            if (scene == null) return null;
            return scene._gN ?? GameScene._nk();
        }

        /// <summary>'alamode0.spc' → 'alamode0'. 차트가 아직 안 올라왔으면 null.</summary>
        private static string GetChartId()
        {
            var np = LogicalNotePlayer._Qe;
            string file = np != null ? np._Ve : null;
            if (string.IsNullOrEmpty(file)) return null;

            int dot = file.LastIndexOf('.');
            return dot > 0 ? file.Substring(0, dot) : file;
        }

        /// <summary>차트 ID = 슬러그 + 난이도 숫자('alamode0'). 슬러그가 겹치는 다른 곡을 거르려고 뒷부분이 숫자인지 본다.</summary>
        private static bool IsTargetChart(string chartId)
        {
            if (!chartId.StartsWith(TargetSlug, StringComparison.Ordinal)) return false;

            string rest = chartId.Substring(TargetSlug.Length);
            if (rest.Length == 0) return false;
            foreach (char c in rest)
            {
                if (!char.IsDigit(c)) return false;
            }
            return true;
        }

        private static void LogSyncCheckpoint(double t)
        {
            if (_nextSyncLog >= SyncLogAtSeconds.Length || t < SyncLogAtSeconds[_nextSyncLog]) return;
            _nextSyncLog++;

            double vt = _vp.time;
            _logger?.Msg($"[VideoInjector][싱크] 곡 {t:F3}초 / 영상 {vt:F3}초 (차이 {(vt - t) * 1000.0:F0}ms, " +
                         $"프레임 {_vp.frame}, 재생 중 {_vp.isPlaying})");
        }

        private static void CaptureCheckpoint(double t)
        {
            if (!DebugCapture) return;
            if (_nextCapture >= CaptureAtSeconds.Length || t < CaptureAtSeconds[_nextCapture]) return;
            _nextCapture++;

            try
            {
                string dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "InFalsusMods", "screens");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"play_{DateTime.Now:HHmmss}_{t:F0}s.png");

                CaptureScreenshot(file);
                _logger?.Msg($"[VideoInjector][캡처] 곡 {t:F1}초 화면 → {file}");
            }
            catch (Exception ex)
            {
                _logger?.Warning($"[VideoInjector][캡처] 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// ScreenCapture.CaptureScreenshot 을 인터롭 래퍼 없이 부른다. 래퍼는 문자열을 ReadOnlySpan 으로 넘기다
        /// GetPinnableReference 가 없어 MissingMethodException 이 난다(2026-09-24, JacketInjector.LoadImage 와 같은 문제).
        /// 네이티브는 파일 이름을 UTF-16 스팬으로 받아 호출 중에 복사하므로 fixed 로 충분하다. 파일은 프레임 끝에 써진다.
        /// </summary>
        private static unsafe void CaptureScreenshot(string file)
        {
            fixed (char* begin = file)
            {
                var span = new ManagedSpanWrapper(begin, file.Length);
                ScreenCapture.CaptureScreenshot_Injected(ref span, 1, ScreenCapture.StereoScreenCaptureMode.LeftEye);
            }
        }

        /// <summary>영상이 배경에 어떻게 들어갈지(UV 타일링·가로세로비) 판단하려고 배경 상태를 남긴다. 줄마다 따로 실패할 수 있다.</summary>
        private static void LogBackground(Constrained2D bg, MelonLogger.Instance logger)
        {
            var mat = bg._H;
            try
            {
                string matName = (UnityEngine.Object)mat != null ? mat.name : "null";
                string shader = (UnityEngine.Object)mat != null && (UnityEngine.Object)mat.shader != null ? mat.shader.name : "null";
                logger.Msg($"  ├ 배경: '{bg.name}' (활성: {bg.gameObject.activeInHierarchy}, 레이어: {bg.gameObject.layer}, " +
                           $"머티리얼: {matName}, 셰이더: {shader})");
            }
            catch (Exception ex)
            {
                logger.Warning($"  ! 배경 정보 실패: {ex.Message}");
            }

            try
            {
                if ((UnityEngine.Object)mat != null)
                {
                    var tex = mat.mainTexture;
                    string texInfo = (UnityEngine.Object)tex != null ? $"'{tex.name}' {tex.width}x{tex.height}" : "null";
                    logger.Msg($"  ├ 배경 텍스처: {texInfo}, 타일링 {mat.mainTextureScale}, 오프셋 {mat.mainTextureOffset}");
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"  ! 배경 텍스처 정보 실패: {ex.Message}");
            }

            try
            {
                var ct = bg.constrained2DTransform;
                if (ct != null)
                {
                    logger.Msg($"  ├ 배경 크기: {ct.sizeInPixels}px, 타입 {ct.type}, 스케일 {ct.renderableScale}, 색 {ct.color}");
                    var mr = ct.meshRenderer;
                    if ((UnityEngine.Object)mr != null)
                    {
                        var size = mr.bounds.size;
                        logger.Msg($"  ├ 배경 MeshRenderer: 월드 크기 {size.x:F2}x{size.y:F2} (가로세로비 {(size.y > 0 ? size.x / size.y : 0):F3}), " +
                                   $"렌더 큐 {((UnityEngine.Object)mr.sharedMaterial != null ? mr.sharedMaterial.renderQueue : -1)}");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"  ! 배경 크기 정보 실패: {ex.Message}");
            }
        }
    }
}
