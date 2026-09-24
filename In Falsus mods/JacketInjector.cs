using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.Bindings;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2Cppifapp.Game.UI.Common;

namespace InFalsusMods
{
    /// <summary>
    /// 커스텀 자켓 — alamode 자켓을 hwa/Thumbnail.png 로 바꿔 보이게 한다.
    ///
    /// 자켓은 곡마다 Addressable Material 두 개다: 큰 자켓 'alamode', 작은 자켓 'alamode_small'
    /// (SongData.songIdJacketMaterials). 게임은 불러온 머티리얼을 UI(Constrained2D._H)에 그대로 꽂는다
    /// (JacketHook 로그 — 곡 선택 카드 'alamode' 2048x2048, 플레이 화면 'alamode_small', 2026-09-23).
    /// 곡 선택 배경(SongSelectJacketBacking)·로딩 화면(JacketTriangleMask Variant)처럼 자켓 머티리얼 대신
    /// 텍스처만 옮겨 쓰는 곳도 있어서, 머티리얼을 바꾸지 않고 텍스처 자체의 내용을 PNG 로 갈아 끼운다
    /// (ImageConversion.LoadImage). 그 텍스처를 쓰는 곳이 전부 한 번에 바뀐다.
    /// LoadImage 가 실패하면 머티리얼의 mainTexture 를 우리 텍스처로 바꾸는 쪽으로 물러난다.
    ///
    /// 감지는 두 갈래다.
    /// 1. Constrained2D._irA(Material, bool) postfix — UI 에 머티리얼을 꽂는 함수(게임 내 호출부 432곳).
    ///    참조형 + bool 이라 docs/04 기준 안전한 시그니처다. 화면에 그려지기 전에 잡는 게 목적.
    /// 2. 틱마다 아는 자켓 슬롯(곡 선택 큰 카드·로딩 화면·플레이 화면)을 한 번 더 본다 — 1 이 놓쳤을 때 대비.
    /// 로그의 "발견 경로" 로 어느 쪽이 먼저 잡았는지 남긴다.
    ///
    /// 2026-09-24 인게임 확인: 허브 → 곡 선택 진입 때 _irA 가 작은 자켓(256x256)과 큰 자켓(2048x2048)을 둘 다
    /// 먼저 잡았고, 곡 선택 작은/큰 카드·배경 자켓·로딩 화면·플레이 화면 자켓이 전부 바뀌었다.
    /// 플레이 씬은 곡 선택 때 바꾼 텍스처를 그대로 다시 썼다. _irA 호출은 곡 선택에서 초당 17~26회라 부담 없다.
    /// </summary>
    internal static class JacketInjector
    {
        // 목적을 달성하면 false 로 끌 것
        public static bool Enabled { get; set; } = true;

        // 바꿀 자켓 머티리얼 이름. 기존 곡 덮어쓰기일 때는 큰 자켓 = 슬러그, 작은 자켓 = 슬러그 + "_small".
        // 새 곡 슬롯(NewSongInjector)을 쓰면 새 곡은 자켓 항목이 없어 게임 기본 자켓(nojacket)을 쓰므로 그쪽으로 바뀐다
        // — 원곡 자켓은 건드리지 않는다. 머티리얼이 쓰이기 전(Init 단계)에만 바꿀 것 (판정 결과를 캐시한다).
        internal static string[] TargetMaterialNames { get; set; } = { "alamode", "alamode_small" };

        private const string FileName = "Thumbnail.png";

        private static MelonLogger.Instance _logger;
        private static bool _broken;

        private static byte[] _png;
        private static bool _pngMissing;
        private static Texture2D _fallbackTexture;

        // 인스턴스 ID 는 세션 안에서 재사용되지 않아서 포인터보다 안전한 캐시 키다 (포인터는 파괴 후 재사용될 수 있음).
        // 머티리얼 인스턴스 ID → 자켓 대상 여부
        private static readonly Dictionary<int, bool> _materialVerdicts = new();
        // 이미 PNG 로 바꾼 텍스처 인스턴스 ID (폴백으로 꽂은 우리 텍스처 포함)
        private static readonly HashSet<int> _replacedTextures = new();

        // _irA 가 얼마나 자주 불리는지 — 후킹 비용 판단용. 씬이 바뀔 때 한 줄로 남긴다.
        private static long _irACalls;
        private static long _irACallsSince = Environment.TickCount64;

        public static void Init(HarmonyLib.Harmony harmony, MelonLogger.Instance logger)
        {
            _logger = logger;
            if (!Enabled) return;

            // PatchAll 에 섞으면 이 패치가 실패할 때 뒤따르는 다른 패치까지 같이 빠진다 — 따로 건다.
            try
            {
                var target = AccessTools.Method(typeof(Constrained2D), nameof(Constrained2D._irA));
                var postfix = AccessTools.Method(typeof(JacketInjector), nameof(Postfix_irA));
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                logger.Msg($"[JacketInjector] Constrained2D._irA 후킹 완료 (대상 자켓: {string.Join(", ", TargetMaterialNames)} → hwa/{FileName})");
            }
            catch (Exception ex)
            {
                logger.Error($"[JacketInjector] Constrained2D._irA 후킹 실패 — 틱 폴링으로만 감지: {ex}");
            }

            SceneRefs.OnSceneChanged += OnSceneChanged;
        }

        public static void Tick(MelonLogger.Instance logger)
        {
            if (!Enabled || _broken) return;

            try
            {
                var select = SceneRefs.SongSelect;
                if (select != null && select.largeSongCard != null)
                {
                    ConsiderSlots(select.largeSongCard.jackets, "틱: 곡 선택 큰 카드");
                }

                var trans = SceneRefs.Transition;
                if (trans != null)
                {
                    ConsiderSlot(trans.jacket, "틱: 로딩 화면");
                }

                var game = SceneRefs.GameScene;
                if (game != null && game.songInfoContainer != null)
                {
                    ConsiderSlots(game.songInfoContainer.jacket, "틱: 플레이 화면");
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                logger.Error($"[JacketInjector] 예외 — 자켓 교체 중단: {ex}");
            }
        }

        private static void OnSceneChanged(int buildIndex, string sceneName)
        {
            long now = Environment.TickCount64;
            double seconds = Math.Max(1, now - _irACallsSince) / 1000.0;
            _logger?.Msg($"[JacketInjector] 직전 구간 _irA 호출 {_irACalls}회 / {seconds:F1}초 (초당 {_irACalls / seconds:F0}회) → '{sceneName}' 진입");
            _irACalls = 0;
            _irACallsSince = now;
        }

        private static void Postfix_irA(Material __0)
        {
            _irACalls++;
            if (_broken || __0 is null) return;

            try
            {
                Consider(__0, "_irA 후킹");
            }
            catch (Exception ex)
            {
                // 호출이 잦은 함수라 한 번 터지면 끄고 끝낸다 (로그 폭주 방지)
                _broken = true;
                _logger?.Error($"[JacketInjector][_irA] 예외 — 자켓 교체 중단: {ex}");
            }
        }

        private static void ConsiderSlots(Il2CppReferenceArray<Constrained2D> slots, string source)
        {
            if (slots == null) return;

            for (int i = 0; i < slots.Length; i++)
            {
                ConsiderSlot(slots[i], source);
            }
        }

        private static void ConsiderSlot(Constrained2D slot, string source)
        {
            if (slot == null) return;

            var mat = slot._H;
            if (mat != null) Consider(mat, source);
        }

        /// <summary>자켓 대상 머티리얼이면 그 텍스처를 PNG 로 바꾼다. 이미 바꾼 텍스처면 아무것도 안 한다.</summary>
        private static void Consider(Material mat, string source)
        {
            int matId = mat.GetInstanceID();
            if (!_materialVerdicts.TryGetValue(matId, out bool isTarget))
            {
                isTarget = IsTargetMaterialName(mat.name);
                _materialVerdicts[matId] = isTarget;
            }
            if (!isTarget) return;

            // 머티리얼은 먼저 꽂히고 텍스처는 나중에 올 수 있다 — 비어 있으면 다음 호출/틱에서 다시 본다
            var tex = mat.mainTexture;
            if (tex == null) return;
            if (_replacedTextures.Contains(tex.GetInstanceID())) return;

            Replace(mat, tex, source);
        }

        private static bool IsTargetMaterialName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            const string instanceSuffix = " (Instance)";
            if (name.EndsWith(instanceSuffix, StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - instanceSuffix.Length);
            }

            return Array.IndexOf(TargetMaterialNames, name) >= 0;
        }

        private static void Replace(Material mat, Texture tex, string source)
        {
            if (!EnsurePng()) return;

            string before = $"'{tex.name}' {tex.width}x{tex.height}";

            // 1순위: 게임 텍스처 자체의 내용을 PNG 로 교체 — 텍스처를 옮겨 쓰는 곳까지 같이 바뀐다
            var tex2d = tex.TryCast<Texture2D>();
            if (tex2d != null)
            {
                bool loaded = false;
                try
                {
                    loaded = LoadImage(tex2d, _png, true);
                }
                catch (Exception ex)
                {
                    _logger?.Warning($"[JacketInjector] LoadImage 예외 ({before}) — 머티리얼 교체로 물러남: {ex.Message}");
                }

                if (loaded)
                {
                    _replacedTextures.Add(tex2d.GetInstanceID());
                    _logger?.Msg($"[JacketInjector] {source}: 머티리얼 '{mat.name}' 텍스처 {before} → hwa/{FileName} " +
                                 $"({tex2d.width}x{tex2d.height}, {tex2d.format}) — 텍스처 내용 교체");
                    return;
                }
            }

            // 2순위: 이 머티리얼이 가리키는 텍스처만 우리 것으로 — 머티리얼을 직접 쓰는 곳만 바뀐다
            var own = GetFallbackTexture();
            if (own == null) return;

            mat.mainTexture = own;
            _replacedTextures.Add(own.GetInstanceID());
            _logger?.Msg($"[JacketInjector] {source}: 머티리얼 '{mat.name}' 텍스처 {before} → hwa/{FileName} " +
                         $"({own.width}x{own.height}) — 머티리얼 교체(폴백, 텍스처를 옮겨 쓰는 곳은 안 바뀜)");
        }

        private static bool EnsurePng()
        {
            if (_png != null) return true;
            if (_pngMissing) return false;

            string path = Path.Combine(HwaPaths.HwaDirectory ?? string.Empty, FileName);
            if (!File.Exists(path))
            {
                _pngMissing = true;
                _logger?.Warning($"[JacketInjector] 파일 없음: {path} — 자켓 교체 안 함");
                return false;
            }

            _png = File.ReadAllBytes(path);
            _logger?.Msg($"[JacketInjector] hwa/{FileName} 읽음 ({_png.Length:N0} 바이트)");
            return true;
        }

        private static Texture2D GetFallbackTexture()
        {
            if (_fallbackTexture != null) return _fallbackTexture;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true)
            {
                name = "hwa_jacket",
                // 씬 전환 때 Resources.UnloadUnusedAssets 에 치이지 않게
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };

            if (!LoadImage(tex, _png, true))
            {
                _logger?.Error($"[JacketInjector] hwa/{FileName} 디코딩 실패 — PNG 가 맞는지 확인할 것");
                UnityEngine.Object.Destroy(tex);
                _broken = true;
                return null;
            }

            _fallbackTexture = tex;
            return tex;
        }

        /// <summary>
        /// ImageConversion.LoadImage 를 인터롭 래퍼 없이 부른다.
        /// 래퍼는 Il2CppSystem.ReadOnlySpan.GetPinnableReference 를 쓰는데 게임 mscorlib 에 그 함수가 없어서
        /// MissingMethodException 이 난다 (2026-09-24). 래퍼가 하는 일 — 네이티브 포인터 얻기, 바이트 배열 고정 —
        /// 을 그대로 풀어서 네이티브 LoadImage_Injected 를 직접 부른다. 데이터는 호출 중에만 읽히므로 fixed 로 충분하다.
        /// </summary>
        private static unsafe bool LoadImage(Texture2D tex, byte[] data, bool markNonReadable)
        {
            IntPtr native = UnityEngine.Object.MarshalledUnityObject.MarshalNotNull(tex);
            if (native == IntPtr.Zero) return false;

            fixed (byte* begin = data)
            {
                var span = new ManagedSpanWrapper(begin, data.Length);
                return ImageConversion.LoadImage_Injected(native, ref span, markNonReadable);
            }
        }
    }
}
