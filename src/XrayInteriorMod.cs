using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(XrayInterior.XrayInteriorMod), "载具内构透视", "1.1.0", "rhino2024")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace XrayInterior
{
    // 游戏原版的 X 光键（默认 O）只显示自己车里的弹药，看不到乘员、发动机、装甲、内衬。
    // 按下去的时候把玩家这台车的其余内构件一起点亮，显示效果照赛后回放那套。
    // 本模组不绑键：认游戏自己的 X 光开关，玩家把 X 光改到别的键也一样有效。
    //
    // 为什么原版看不到：GHPC 的内构件全是 GHPC.AarVisual，战斗里它们一条 renderer 都是关着的。
    // AarVisual.updatedXray(bool) 的 IL 是 set_Highlighted(AarController.InAar && 参数)——
    // 战斗中 InAar 永远是 false，于是按 O 只能把 _highlighted 写成 false：
    //   · RenderMode = HighlightedOnly 的件（车组、大部分模块）要看 _highlighted 才开，永远黑着；
    //   · HideUntilAar = true 的件（舱内装甲衬层那批）要 InAar 才开，也永远黑着；
    //   · 只有 RenderMode = XrayOnly 且 HideUntilAar = false 的件（弹药）能亮——这就是玩家看到的
    //     「只有弹药」。同一份 IL 里 enabled 还要过 PlayerCam.HideAarCam 这道门，而
    //     HideAarCam = !ShowXray，叠加相机（只清深度、只画 AAR/Xray 两个图层）也正是这时候被打开的，
    //     所以把件子点亮之后不需要自己建相机。
    //
    // 1.0.0 为什么只亮出一小半（发动机/装甲/内衬全缺）：它把每件内构件都往 Xray Only(21) 搬，
    // 而搬图层碰到两条一律让步的安全规则——件子自己带 Collider 就不搬、父级子树里有 Collider 就不激活。
    // 装甲衬层和发动机那批正是靠 Collider 撑着的，于是它们被规则拦下，点亮了也没相机画。
    // 但内构美术本来就躺在 Raycast Only(8) 这类「主相机不画、X 光叠加相机画」的图层上
    // （实测车长 X 光机掩码 2097408 = bit 8|21）：根本不用搬图层就能画出来。
    //
    // 1.1.0 的做法：
    //   1) 图层只在「这个图层会被主视角相机画」时才搬到 21（不搬就会被世界通道画成一坨）；
    //      其余情况原地不动，改为把这些图层位或进 X 光叠加相机的 cullingMask，关窗还原。
    //   2) 件子的 renderer 清单不只信 AarVisual._renderers——它在 Awake 里用
    //      GetComponentsInChildren<Renderer>()（不含未激活）填的，挂在未激活子级上的件天生缺项，
    //      缺的那几条由我们自己 enabled=true 并换上 AarVisual.AarMaterial（游戏不会管它们）。
    //   3) X 光开着期间每 0.4 s 补扫一轮：Awake 还没跑、_renderers 当时为空的件（延迟实例化的模块）
    //      下一轮就补上，1.0.0 是一次性扫描，空清单的件被永久丢弃。
    //   4) Collider 规则放宽到「只看真的会参与物理的」：已禁用的 Collider 搬层/激活都不改变命中判定，
    //      只有 enabled==true 的 Collider 才拦住。
    // 归属判定照旧走 Utility.CodeUtils.GetParentUnitFollowAware（内构挂在独立的 HULL/TURRET FOLLOW
    // 跟随根下，那根上有 LateFollow.ParentUnit 指回本车）；解析不出归属的件再按本车 Collider 包围盒兜底。
    // X 光关掉、进入回放、换车、退出载具、关模组，全部按快照还原；期间不做逐帧刷新
    // （逐帧翻 Highlighted 等于每帧给几百条 renderer 重设材质）。
    public sealed class XrayInteriorMod : MelonMod
    {
        const float QuickResweep = 0.4f;    // 补扫间隔：等 Awake 晚到的件把 renderer 清单填上
        const float IdleResweep = 1.5f;     // 连着补空之后放慢，别每轮白扫全场
        const int SampleCap = 6;

        static MelonPreferences_Category cat;
        static MelonPreferences_Entry<bool> eEnabled, eKeepTextures, eDebugLog;

        static AccessTools.FieldRef<GHPC.AarVisual, List<Renderer>> RenderersRef;
        static MethodInfo UpdateMaterialsMethod;
        static MethodInfo UpdateLayersMethod;

        sealed class Snap
        {
            public GHPC.AarVisual Visual;
            public bool Hidden;
            public bool Highlighted;
            public bool SwitchMaterials;
            public bool PreserveLayer;
            public bool HideUntilAar;
        }

        sealed class LayerMove
        {
            public GameObject Object;
            public int Layer;
        }

        // 游戏不会管的 renderer（不在 _renderers 里的那些）由我们自己开、自己上色，自己收回去
        sealed class RendererSnap
        {
            public Renderer R;
            public bool WasEnabled;
            public Material[] Mats;
        }

        sealed class CameraSnap
        {
            public Camera Cam;
            public int Mask;
        }

        static readonly List<Snap> snaps = new List<Snap>();
        static readonly List<LayerMove> moves = new List<LayerMove>();
        static readonly List<RendererSnap> forced = new List<RendererSnap>();
        static readonly List<CameraSnap> cameras = new List<CameraSnap>();
        static readonly List<GameObject> activated = new List<GameObject>();
        static readonly HashSet<int> touched = new HashSet<int>();
        static readonly HashSet<int> litVisuals = new HashSet<int>();
        static readonly List<Renderer> buffer = new List<Renderer>();
        // 只为日志留几条例子：下次出问题，看名字就知道缺的是哪一批件
        static readonly List<string> litSamples = new List<string>();
        static readonly List<string> blockedSamples = new List<string>();

        static bool applied;
        static bool handedToAar;          // 已把内构状态交给赛后回放，等它结束再还原
        static bool cameraDumped;
        static GHPC.Unit appliedUnit;
        static float retryTimer;
        static float resweepTimer;
        static int xrayLayer = -1;
        static int aarLayer = -1;
        static int interiorBits;          // 本次要替内构件或给叠加相机的图层位
        static Bounds unitBounds;
        static bool hasBounds;

        // 每轮扫描的计数，只为日志自证
        static int scanned, owned, byBounds, lit, exteriorSkipped, noRenderer;
        static int ancestorBlocked, colliderKept, forcedLit, alreadyOn;

        static StreamWriter log;
        static readonly object logLock = new object();

        public override void OnInitializeMelon()
        {
            cat = MelonPreferences.CreateCategory("XrayInterior", "载具内构透视");
            eEnabled = cat.CreateEntry<bool>("Enabled", true,
                "Master switch. When off, the X-ray key shows only what the game shows by itself");
            eKeepTextures = cat.CreateEntry<bool>("KeepOriginalTextures", false,
                "Show the interior with its own textures instead of the flat post-battle-review look");
            eDebugLog = cat.CreateEntry<bool>("DebugLog", false,
                "Write how many interior pieces were lit and skipped, which layers the X-ray camera draws and why a piece was refused, to Bin/Mods/XrayInterior.debug.log; only turn it on when something is wrong");

            try
            {
                RenderersRef = AccessTools.FieldRefAccess<GHPC.AarVisual, List<Renderer>>("_renderers");
                UpdateMaterialsMethod = AccessTools.Method(typeof(GHPC.AarVisual), "updateMaterials");
                UpdateLayersMethod = AccessTools.Method(typeof(GHPC.AarVisual), "updateLayers");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("内构透视：AarVisual 内部成员没绑上，功能不可用：" + e.Message);
            }

            xrayLayer = LayerMask.NameToLayer("Xray Only");
            if (xrayLayer < 0) xrayLayer = 21;
            aarLayer = LayerMask.NameToLayer("AAR Objects");
            if (aarLayer < 0) aarLayer = 15;

            if (eDebugLog.Value) OpenLog();
            MelonLogger.Msg("内构透视 已加载（按游戏的 X 光键查看自己车的内构，"
                + (eEnabled.Value ? "现在是开的" : "现在是关的") + "）");
            Log("initialised, keepTextures=" + eKeepTextures.Value + " xrayLayer=" + xrayLayer);
        }

        public override void OnDeinitializeMelon()
        {
            Restore("退出游戏/卸载模组");
            CloseLog();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            RestoreSilent("场景切换：" + sceneName);
        }

        public override void OnUpdate()
        {
            if (retryTimer > 0f) retryTimer -= Time.unscaledDeltaTime;

            var pi = GHPC.Player.PlayerInput.Instance;
            var unit = pi != null ? pi.CurrentPlayerUnit : null;

            if (applied)
            {
                // 回放里游戏自己会点亮同一批 AarVisual，别和它抢：进场就交出去，出场再还原。
                if (GHPC.AarController.InAar) { handedToAar = true; return; }
                if (handedToAar)
                {
                    handedToAar = false;
                    Restore("回放结束");
                    return;
                }
                if (!eEnabled.Value) { Restore("模组已关闭"); return; }
                if (!ShowXray()) { Restore("关了 X 光"); return; }
                if (unit != appliedUnit) { Restore("换了载具"); return; }   // 同 TryApply，用 Unity 的 == 比实例 ID

                resweepTimer -= Time.unscaledDeltaTime;
                if (resweepTimer > 0f) return;
                int before = litVisuals.Count;
                Sweep(unit, false);
                // 补扫不再点亮新件就放慢，别每轮都白扫全场
                resweepTimer = litVisuals.Count == before ? IdleResweep : QuickResweep;
                return;
            }

            if (!eEnabled.Value || unit == null || GHPC.AarController.InAar || !ShowXray()) return;
            if (retryTimer > 0f) return;
            TryApply(unit);
        }

        static bool ShowXray()
        {
            try { return GHPC.AarController.ShowXray; }
            catch (Exception e) { Log("reading ShowXray threw: " + e.Message); return false; }
        }

        // ---------------------------------------------------------------- apply

        static void TryApply(GHPC.Unit unit)
        {
            snaps.Clear();
            moves.Clear();
            forced.Clear();
            cameras.Clear();
            activated.Clear();
            touched.Clear();
            litVisuals.Clear();
            litSamples.Clear();
            blockedSamples.Clear();
            interiorBits = 0;
            hasBounds = false;
            unitBounds = new Bounds();
            ResetCounters();

            Sweep(unit, true);

            if (lit == 0)
            {
                RestoreCore();
                retryTimer = 1f;
                Log("nothing to light: scanned=" + scanned + " owned=" + owned);
                return;
            }

            applied = true;
            appliedUnit = unit;
            resweepTimer = QuickResweep;
            MelonLogger.Msg("内构透视：点亮这台车的 " + lit + " 组内构件（共扫描 " + scanned + "）");
            DumpCounts("first apply");
        }

        static void ResetCounters()
        {
            scanned = owned = byBounds = lit = exteriorSkipped = noRenderer = 0;
            ancestorBlocked = colliderKept = forcedLit = alreadyOn = 0;
        }

        static void DumpCounts(string stage)
        {
            Log(stage + ": scanned=" + scanned + " owned=" + owned + " byBounds=" + byBounds
                + " lit=" + litVisuals.Count + " thisSweep=" + lit + " exterior=" + exteriorSkipped
                + " noRenderer=" + noRenderer + " ancestorBlocked=" + ancestorBlocked
                + " colliderKept=" + colliderKept + " forcedRenderers=" + forced.Count
                + " layerMoved=" + moves.Count + " activated=" + activated.Count
                + " interiorBits=" + LayerList(interiorBits) + " cameras=" + cameras.Count
                + " litSample=[" + Join(litSamples) + "] blockedSample=[" + Join(blockedSamples) + "]");
        }

        static string Join(List<string> items)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(items[i]);
            }
            return sb.ToString();
        }

        static string PathOf(Transform t)
        {
            var names = new List<string>();
            var cur = t;
            int guard = 0;
            while (cur != null && guard++ < 20) { names.Add(cur.name); cur = cur.parent; }
            var sb = new System.Text.StringBuilder();
            for (int i = names.Count - 1; i >= 0; i--)
            {
                sb.Append(names[i]);
                if (i > 0) sb.Append('/');
            }
            return sb.ToString();
        }

        // 一轮扫描：只处理还没点亮的件，所以点亮中途新出现的模块（延迟 Awake、_renderers 当时为空）
        // 会在下一轮补上，而不是像 1.0.0 那样被永久丢弃。
        static void Sweep(GHPC.Unit unit, bool first)
        {
            lit = 0;
            ResetCounters();
            int world = WorldMask();

            GHPC.AarVisual[] all = null;
            try { all = UnityEngine.Resources.FindObjectsOfTypeAll<GHPC.AarVisual>(); }
            catch (Exception e) { Log("FindObjectsOfTypeAll threw: " + e.Message); return; }
            if (all == null) return;

            for (int i = 0; i < all.Length; i++)
            {
                var v = all[i];
                if (v == null) continue;
                var go = v.gameObject;
                if (go == null) continue;
                var scene = go.scene;
                if (!scene.IsValid() || !scene.isLoaded) continue;   // 预制体/资源里的副本
                scanned++;

                if (litVisuals.Contains(v.GetInstanceID())) continue;

                GHPC.Unit owner = null;
                try { owner = GHPC.Utility.CodeUtils.GetParentUnitFollowAware(v.transform); }
                catch (Exception e) { Log("owner lookup threw: " + e.Message); continue; }

                if (owner == unit) owned++;
                else if (owner == null && InsideUnitBounds(unit, v.transform)) byBounds++;
                else continue;

                CollectRenderers(v);
                if (buffer.Count == 0) { noRenderer++; continue; }   // Awake 还没跑，下一轮补扫再来
                if (IsExterior(v, world)) { exteriorSkipped++; continue; }

                if (ApplyOne(v, world))
                {
                    lit++;
                    litVisuals.Add(v.GetInstanceID());
                    if (eDebugLog.Value && litSamples.Count < SampleCap) litSamples.Add(PathOf(v.transform));
                }
            }

            if (interiorBits != 0) PushCameraBits(interiorBits, first);
            if (first && eDebugLog.Value && !cameraDumped) { cameraDumped = true; DumpCameras(world); }
            if (!first && lit > 0) DumpCounts("resweep");
        }

        // 外壳的判据不是图层而是「现在有没有在画」：战斗期内构件一条 renderer 都不亮，
        // 正在被世界通道画的件就是外壳/炮管那批，点亮它们只会把整车糊成黄壳。
        static bool IsExterior(GHPC.AarVisual v, int world)
        {
            if ((world & (1 << v.gameObject.layer)) == 0) return false;
            for (int i = 0; i < buffer.Count; i++)
            {
                var r = buffer[i];
                if (r == null || !r.enabled) continue;
                if (!r.gameObject.activeInHierarchy) continue;
                alreadyOn++;
                return true;
            }
            return false;
        }

        static bool InsideUnitBounds(GHPC.Unit unit, Transform t)
        {
            if (!hasBounds)
            {
                var cols = unit.transform.GetComponentsInChildren<Collider>();
                if (cols == null || cols.Length == 0) return false;
                var b = new Bounds(unit.transform.position, Vector3.zero);
                for (int i = 0; i < cols.Length; i++)
                    if (cols[i] != null) b.Encapsulate(cols[i].bounds);
                // 跟随根比车体 Collider 高出一截，炮塔/车顶的件都在包络之上
                b.Expand(new Vector3(2.5f, 4f, 2.5f));
                unitBounds = b;
                hasBounds = true;
            }
            return unitBounds.Contains(t.position);
        }

        static void PushCameraBits(int bits, bool first)
        {
            Camera[] cams = null;
            try { cams = UnityEngine.Resources.FindObjectsOfTypeAll<Camera>(); }
            catch (Exception e) { Log("camera scan threw: " + e.Message); return; }
            if (cams == null) return;

            for (int i = 0; i < cams.Length; i++)
            {
                var cam = cams[i];
                if (cam == null) continue;
                var go = cam.gameObject;
                if (!go.activeInHierarchy || !cam.enabled) continue;
                if (cam.clearFlags != CameraClearFlags.Depth) continue;
                // 只碰 AAR/Xray 那台叠加相机；UI/描边之类的深度相机加了图层会把画面糊脏
                int aarBits = (1 << xrayLayer) | (1 << aarLayer);
                if ((cam.cullingMask & aarBits) == 0) continue;

                int missing = bits & ~cam.cullingMask;
                if (missing == 0) continue;

                if (!KnowsCamera(cam))
                {
                    cameras.Add(new CameraSnap { Cam = cam, Mask = cam.cullingMask });
                    Log("camera \"" + go.name + "\" mask " + cam.cullingMask + " -> +" + LayerList(missing));
                }
                cam.cullingMask |= missing;
                if (first) Log("x-ray camera \"" + go.name + "\" now draws " + LayerList(cam.cullingMask));
            }
        }

        static bool KnowsCamera(Camera cam)
        {
            for (int i = 0; i < cameras.Count; i++)
                if (cameras[i].Cam == cam) return true;
            return false;
        }

        static void DumpCameras(int world)
        {
            Camera[] cams = null;
            try { cams = UnityEngine.Resources.FindObjectsOfTypeAll<Camera>(); }
            catch { return; }
            if (cams == null) return;
            Log("world mask " + world + " = " + LayerList(world));
            for (int i = 0; i < cams.Length; i++)
            {
                var cam = cams[i];
                if (cam == null) continue;
                var go = cam.gameObject;
                if (!go.scene.IsValid() || !go.scene.isLoaded) continue;
                Log("cam \"" + go.name + "\" active=" + go.activeInHierarchy + " on=" + cam.enabled
                    + " clear=" + cam.clearFlags + " mask=" + cam.cullingMask
                    + " depth=" + cam.depth);
            }
        }

        static string LayerList(int mask)
        {
            var sb = new System.Text.StringBuilder();
            for (int bit = 0; bit < 32; bit++)
            {
                if ((mask & (1 << bit)) == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(bit).Append('(').Append(LayerMask.LayerToName(bit)).Append(')');
            }
            return sb.ToString();
        }

        // 主视角世界通道会画哪些图层：这些图层上的件必须搬走，否则要么被外壳挡住、
        // 要么在正常视野里多画一层。叠加相机（只清深度）不算。
        static int WorldMask()
        {
            int mask = 0;
            Camera[] cams = null;
            try { cams = UnityEngine.Resources.FindObjectsOfTypeAll<Camera>(); }
            catch { cams = null; }
            if (cams != null)
            {
                for (int i = 0; i < cams.Length; i++)
                {
                    var cam = cams[i];
                    if (cam == null || !cam.enabled) continue;
                    if (!cam.gameObject.activeInHierarchy) continue;
                    if (cam.clearFlags == CameraClearFlags.Depth) continue;
                    mask |= cam.cullingMask;
                }
            }
            if (mask != 0) return mask;
            var main = Camera.main;
            return main != null ? main.cullingMask : 0;
        }

        static bool ApplyOne(GHPC.AarVisual v, int world)
        {
            EnsureAncestorsActive(v.transform);   // renderer 清单已由扫描填好，激活父级不影响它

            var snap = new Snap
            {
                Visual = v,
                Hidden = v.Hidden,
                Highlighted = v.Highlighted,
                SwitchMaterials = v.SwitchMaterials,
                PreserveLayer = v.PreserveLayerUntilAar,
                HideUntilAar = v.HideUntilAar
            };

            v.PreserveLayerUntilAar = true;                      // 拦住 updateLayers，图层由我们自己管
            if (eKeepTextures.Value) v.SwitchMaterials = false;   // 换材质发生在 Highlighted 之后，得先压住
            v.HideUntilAar = false;
            v.Hidden = false;
            v.Highlighted = true;                                 // set_Highlighted 自己就调 UpdateVisibility

            for (int i = 0; i < buffer.Count; i++)
            {
                Renderer r = null;
                try { r = buffer[i]; } catch { }
                if (r == null) continue;
                var go = r.gameObject;
                if (go == null) continue;
                if (!touched.Add(go.GetInstanceID())) continue;
                if (!go.activeSelf)                                 // 父级激活了但件子自己还是关的，一样画不出来
                {
                    if (HasLiveColliderAnywhere(go))
                    {
                        ancestorBlocked++;
                        if (eDebugLog.Value && blockedSamples.Count < SampleCap)
                            blockedSamples.Add("activate " + PathOf(go.transform));
                        continue;
                    }
                    go.SetActive(true);
                    activated.Add(go);
                }

                // 不在 _renderers 里的件游戏不会管：自己开、自己上色，还原也自己来
                if (!r.enabled)
                {
                    var fs = new RendererSnap { R = r, WasEnabled = false };
                    PaintManually(r, v, fs);
                    forced.Add(fs);
                    forcedLit++;
                }

                int layer = go.layer;
                if (layer == xrayLayer) continue;
                if ((world & (1 << layer)) == 0)
                {
                    // 这个图层本来就没相机画，原地不动、把图层位喂给 X 光叠加相机就行。
                    // 1.0.0 在这里还要搬图层，等于把带 Collider 的装甲衬层/发动机全排除在外。
                    interiorBits |= 1 << layer;
                    continue;
                }
                if (HasLiveCollider(go))
                {
                    // 图层决定来袭炮弹打得到谁，带活 Collider 的件不搬
                    colliderKept++;
                    // 件点亮了但留在主相机会画的图层上：外壳挡住了就等于没显示，记下是谁
                    if (eDebugLog.Value && blockedSamples.Count < SampleCap)
                        blockedSamples.Add("layer " + layer + " " + PathOf(go.transform));
                    continue;
                }
                moves.Add(new LayerMove { Object = go, Layer = layer });
                go.layer = xrayLayer;
            }

            snaps.Add(snap);
            return true;
        }

        static void CollectRenderers(GHPC.AarVisual v)
        {
            buffer.Clear();
            List<Renderer> field = null;
            try { if (RenderersRef != null) field = RenderersRef(v); }
            catch (Exception e) { Log("reading _renderers threw: " + e.Message); }
            if (field != null)
                for (int i = 0; i < field.Count; i++)
                    if (field[i] != null) buffer.Add(field[i]);

            // Awake 填 _renderers 用的是不含未激活的 GetComponentsInChildren，缺的那几条只能自己补
            Renderer[] more = null;
            try { more = v.GetComponentsInChildren<Renderer>(true); }
            catch (Exception e) { Log("GetComponentsInChildren threw: " + e.Message); }
            if (more == null) return;
            for (int i = 0; i < more.Length; i++)
            {
                var r = more[i];
                if (r == null) continue;
                bool seen = false;
                for (int k = 0; k < buffer.Count; k++) if (buffer[k] == r) { seen = true; break; }
                if (!seen) buffer.Add(r);
            }
        }

        static void PaintManually(Renderer r, GHPC.AarVisual v, RendererSnap fs)
        {
            if (eKeepTextures.Value) return;
            var mr = r as MeshRenderer;
            if (mr == null) return;
            Material aar = v.AarMaterial;
            if (aar == null) return;
            Material[] current = null;
            try { current = mr.sharedMaterials; } catch { }
            if (current == null || current.Length == 0) return;
            var replaced = new Material[current.Length];
            for (int i = 0; i < replaced.Length; i++) replaced[i] = aar;
            // 记下原材质；还原时按 WasEnabled 一起收回
            fs.Mats = current;
            mr.sharedMaterials = replaced;
        }

        // 只有 enabled 的 Collider 才真的参与命中；已禁用的搬层/激活都不改变物理，不必拦。
        static bool HasLiveCollider(GameObject go)
        {
            try
            {
                var cols = go.GetComponents<Collider>();
                for (int i = 0; i < cols.Length; i++)
                    if (cols[i] != null && cols[i].enabled) return true;
                return false;
            }
            catch (Exception) { return true; }
        }

        static bool HasLiveColliderAnywhere(GameObject go)
        {
            try
            {
                var cols = go.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < cols.Length; i++)
                    if (cols[i] != null && cols[i].enabled) return true;
                return false;
            }
            catch (Exception) { return true; }
        }

        // 内构美术可能挂在没激活的父级上；不激活就画不出来。带活 Collider 的那一支不激活（同理，不改物理）。
        static void EnsureAncestorsActive(Transform t)
        {
            var cur = t;
            int guard = 0;
            while (cur != null && guard++ < 24)
            {
                var go = cur.gameObject;
                if (!go.activeSelf)
                {
                    if (HasLiveColliderAnywhere(go)) return;
                    go.SetActive(true);
                    activated.Add(go);
                }
                cur = cur.parent;
            }
        }

        // ---------------------------------------------------------------- restore

        static void Restore(string why)
        {
            int count = snaps.Count;
            RestoreCore();
            if (count > 0)
            {
                MelonLogger.Msg("内构透视：还原（" + why + "，" + count + " 组）");
                Log("restore: " + why + " pieces=" + count);
            }
        }

        static void RestoreSilent(string why)
        {
            Log("restore: " + why + " pieces=" + snaps.Count);
            RestoreCore();
        }

        static void RestoreCore()
        {
            for (int i = forced.Count - 1; i >= 0; i--)
            {
                var fs = forced[i];
                var r = fs.R;
                if (r == null) continue;
                try
                {
                    if (fs.Mats != null) r.sharedMaterials = fs.Mats;
                    r.enabled = fs.WasEnabled;
                }
                catch (Exception e) { Log("forced renderer restore threw: " + e.Message); }
            }
            forced.Clear();

            for (int i = snaps.Count - 1; i >= 0; i--)
            {
                var s = snaps[i];
                var v = s.Visual;
                if (v == null) continue;
                try
                {
                    v.SwitchMaterials = s.SwitchMaterials;         // 先还原，材质还原要靠它放行
                    v.Highlighted = false;                          // _highlighted 落回 false
                    Flush(v, UpdateMaterialsMethod);                 // renderer 一关 UpdateVisibility 就不管材质了，手动补一次
                    v.Hidden = s.Hidden;
                    v.HideUntilAar = s.HideUntilAar;
                    v.PreserveLayerUntilAar = s.PreserveLayer;
                    Flush(v, UpdateLayersMethod);
                }
                catch (Exception e) { Log("restore piece threw: " + e.Message); }
            }
            snaps.Clear();

            for (int i = 0; i < moves.Count; i++)
            {
                var m = moves[i];
                if (m.Object == null) continue;
                try { m.Object.layer = m.Layer; } catch (Exception e) { Log("layer restore threw: " + e.Message); }
            }
            moves.Clear();

            for (int i = 0; i < cameras.Count; i++)
            {
                var cs = cameras[i];
                if (cs.Cam == null) continue;
                try { cs.Cam.cullingMask = cs.Mask; } catch (Exception e) { Log("camera mask restore threw: " + e.Message); }
            }
            cameras.Clear();

            touched.Clear();
            litVisuals.Clear();
            interiorBits = 0;
            hasBounds = false;

            RollBackActivations();

            applied = false;
            handedToAar = false;
            appliedUnit = null;
            retryTimer = 0.5f;
        }

        static void RollBackActivations()
        {
            for (int i = activated.Count - 1; i >= 0; i--)
            {
                var go = activated[i];
                if (go == null || !go.activeSelf) continue;
                try { go.SetActive(false); } catch (Exception e) { Log("SetActive threw: " + e.Message); }
            }
            activated.Clear();
        }

        static void Flush(GHPC.AarVisual v, MethodInfo method)
        {
            if (method == null) return;
            try { method.Invoke(v, null); }
            catch (Exception e) { Log(method.Name + " threw: " + e.Message); }
        }

        // ---------------------------------------------------------------- logging

        static void OpenLog()
        {
            try
            {
                string dir = Path.GetDirectoryName(UnityEngine.Application.dataPath);
                string path = Path.Combine(dir, "Mods", "XrayInterior.debug.log");
                log = new StreamWriter(path, true) { AutoFlush = true };
                MelonLogger.Msg("内构透视：调试日志写入 " + path);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("内构透视：调试日志打不开：" + e.Message);
                log = null;
            }
        }

        static void CloseLog()
        {
            lock (logLock)
            {
                if (log == null) return;
                try { log.Dispose(); } catch { }
                log = null;
            }
        }

        static void Log(string text)
        {
            if (!eDebugLog.Value || log == null) return;
            lock (logLock)
            {
                try { log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + text); }
                catch { }
            }
        }
    }
}
