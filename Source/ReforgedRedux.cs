using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using KSP.UI.Screens;

namespace ReforgedRedux
{
    public class ModuleReforged : PartModule, IPartMassModifier
    {
        [KSPField(isPersistant = true)] public bool  rdxFinish = false;
        [KSPField(isPersistant = true)] public float rdxMetal = 0f;
        [KSPField(isPersistant = true)] public float rdxSmooth = 0f;
        [KSPField(isPersistant = true)] public bool  rdxColored = false;
        [KSPField(isPersistant = true)] public int rdxColorMode = 0;
        [KSPField(isPersistant = true)] public float rdxPaintDetail = 1f;
        [KSPField(isPersistant = true)] public bool rdxTexDetail = true;
        [KSPField(isPersistant = true)] public float rdxR = 1f;
        [KSPField(isPersistant = true)] public float rdxG = 1f;
        [KSPField(isPersistant = true)] public float rdxB = 1f;
        [KSPField(isPersistant = true)] public bool  rdxKeepShiny = false;
        [KSPField(isPersistant = true)] public float rdxScale = 1f;
        [KSPField(isPersistant = true)] public int   rdxShineSpot = 0;
        [KSPField(isPersistant = true)] public string rdxRegion = "whole";

        [KSPField(isPersistant = true)] public string rdxStack = "";

        [KSPField(isPersistant = true)] public string rdxZones = "";
        [KSPField(guiActive = false, guiActiveEditor = false, guiName = "Heat tiles", guiUnits = " t", guiFormat = "F3")]
        public float rdxTileMass = 0f;

        public bool rdxHeatOn = false;
        public double rdxHeatMax = 0d;
        public double rdxHeatSkin = 0d;

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) { return rdxTileMass; }
        public ModifierChangeWhen GetModuleMassChangeWhen() { return ModifierChangeWhen.FIXED; }

        public bool Active { get { return rdxFinish || rdxColored || WeatherOn || !string.IsNullOrEmpty(rdxZones); } }
        public bool WeatherOn { get { return !string.IsNullOrEmpty(rdxStack); } }

        readonly Dictionary<Renderer, Material[]> pristine = new Dictionary<Renderer, Material[]>();

        readonly Dictionary<Renderer, Material[]> origShared = new Dictionary<Renderer, Material[]>();
        readonly Dictionary<Renderer, Mesh> origMesh = new Dictionary<Renderer, Mesh>();
        readonly List<Mesh> splitMeshes = new List<Mesh>();
        readonly List<Material> created = new List<Material>();

        static Shader tuShader;
        static bool tuTried;
        static readonly int IdMainTex  = Shader.PropertyToID("_MainTex");
        static readonly int IdBumpMap  = Shader.PropertyToID("_BumpMap");
        static readonly int IdColor    = Shader.PropertyToID("_Color");
        static readonly int IdMetal    = Shader.PropertyToID("_Metal");
        static readonly int IdSmooth   = Shader.PropertyToID("_Smoothness");
        static readonly int IdMetalGloss = Shader.PropertyToID("_MetallicGlossMap");
        static readonly int IdSpecColor = Shader.PropertyToID("_SpecColor");
        static readonly int IdShininess = Shader.PropertyToID("_Shininess");

        static readonly HashSet<ModuleReforged> liveModules = new HashSet<ModuleReforged>();

        public static Shader TuShader()
        {
            if (tuTried) return tuShader;
            tuTried = true;

            try
            {
                foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                {
                    if (la.assembly == null) continue;
                    Type t = la.assembly.GetType("KSPShaderTools.TexturesUnlimitedLoader", false);
                    if (t == null) continue;
                    MethodInfo mi = t.GetMethod("getShader", BindingFlags.Public | BindingFlags.Static);
                    if (mi != null) tuShader = mi.Invoke(null, new object[] { "TU/Metallic" }) as Shader;
                    if (tuShader != null)
                    {
                        Debug.Log("[ReforgedRedux] TU/Metallic via TexturesUnlimitedLoader (" + la.assembly.GetName().Name + ")");
                        return tuShader;
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[ReforgedRedux] TU loader lookup failed: " + e.Message); }

            try
            {
                Shader[] all = Resources.FindObjectsOfTypeAll<Shader>();
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].name == "TU/Metallic")
                    {
                        tuShader = all[i];
                        Debug.Log("[ReforgedRedux] TU/Metallic via loaded-shader scan");
                        return tuShader;
                    }
            }
            catch (Exception e) { Debug.LogWarning("[ReforgedRedux] shader scan failed: " + e.Message); }

            tuShader = Shader.Find("TU/Metallic");
            if (tuShader == null)
                Debug.LogError("[ReforgedRedux] TU/Metallic shader NOT FOUND - finishes disabled. Is TexturesUnlimited installed?");
            return tuShader;
        }

        static Shader deferredTu;
        static bool deferredTried;
        static Shader ActiveTuShader()
        {
            Shader baseTu = TuShader();
            if (baseTu == null) return null;
            if (deferredTried) return (deferredTu != null) ? deferredTu : baseTu;
            deferredTried = true;
            try
            {
                foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                {
                    if (la.assembly == null || la.assembly.GetName().Name != "Deferred") continue;
                    Type[] types = la.assembly.GetTypes();
                    for (int i = 0; i < types.Length; i++)
                    {
                        if (types[i].Name != "ShaderLoader") continue;
                        PropertyInfo pi = types[i].GetProperty("ReplacementShaders", BindingFlags.Public | BindingFlags.Static);
                        if (pi == null) continue;
                        System.Collections.IDictionary dict = pi.GetValue(null, null) as System.Collections.IDictionary;
                        if (dict != null && dict.Contains("TU/Metallic"))
                        {
                            deferredTu = dict["TU/Metallic"] as Shader;
                            if (deferredTu != null)
                                Debug.Log("[ReforgedRedux] using Deferred's replacement TU/Metallic shader");
                        }
                        break;
                    }
                    break;
                }
            }
            catch (Exception e) { Debug.LogWarning("[ReforgedRedux] Deferred lookup failed: " + e.Message); }
            return (deferredTu != null) ? deferredTu : baseTu;
        }

        static readonly Dictionary<string, Texture2D> fillTex = new Dictionary<string, Texture2D>();
        static Texture2D FillTexture(byte r, byte g, byte b, byte a)
        {
            string key = r + "," + g + "," + b + "," + a;
            Texture2D t;
            if (fillTex.TryGetValue(key, out t) && t != null) return t;
            t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Color32 c = new Color32(r, g, b, a);
            t.SetPixels32(new Color32[] { c, c, c, c });
            t.Apply(false, false);
            fillTex[key] = t;
            return t;
        }
        static void FillSlot(Material m, string slot, byte r, byte g, byte b, byte a)
        {
            if (m.HasProperty(slot) && m.GetTexture(slot) == null)
                m.SetTexture(slot, FillTexture(r, g, b, a));
        }
        static void FillEmptyTuSlots(Material m)
        {
            FillSlot(m, "_MainTex", 0, 0, 0, 255);
            FillSlot(m, "_BumpMap", 128, 128, 128, 128);
            FillSlot(m, "_Emissive", 0, 0, 0, 0);
            FillSlot(m, "_MetallicGlossMap", 255, 255, 255, 255);
            FillSlot(m, "_SpecGlossMap", 255, 255, 255, 255);
            FillSlot(m, "_MaskTex", 255, 0, 0, 255);
        }

        static readonly string[] AlienShaders = {
            "waterfall", "particle", "/additive", "smoke", "kerbalfx", "sootstreak",
            "scatterer", "eve/", "eve-", "eve ", "atmosphere", "deferred/",
            "sunflare", "sunspot", "kopernicus", "parallax", "/rings", "flare",
            "planetshine", "singularity", "firefly" };

        static readonly string[] SkipNames = { "flag", "window", "glass", "canopy", "overlay" };

        internal static bool SkipRenderer(Renderer r) { return SkipReason(r) != null; }

        internal static string SkipReason(Renderer r)
        {
            if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) return "not a mesh";

            float sz = r.bounds.size.magnitude;
            if (float.IsNaN(sz) || float.IsInfinity(sz) || sz > 2000f) return "oversized bounds";

            if (r.GetComponentInParent<Camera>() != null) return "camera child";

            if (r.GetComponent<Light>() != null) return "light";

            Material sm = r.sharedMaterial;
            if (sm != null && sm.shader != null)
            {
                string shn = sm.shader.name.ToLowerInvariant();
                if (shn.Contains("depthmask")) return "depth mask shader";
                for (int i = 0; i < AlienShaders.Length; i++)
                    if (shn.Contains(AlienShaders[i])) return "effect shader (" + AlienShaders[i] + ")";
            }

            string n = r.name.ToLowerInvariant();
            for (int i = 0; i < SkipNames.Length; i++)
                if (n.Contains(SkipNames[i])) return "name contains '" + SkipNames[i] + "'";
            return null;
        }

        static MethodInfo tuIsTransparent;
        static bool tuIsTransparentTried;
        static bool IsGlass(Material m)
        {
            if (!tuIsTransparentTried)
            {
                tuIsTransparentTried = true;
                try
                {
                    foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                    {
                        if (la.assembly == null) continue;
                        Type t = la.assembly.GetType("KSPShaderTools.TexturesUnlimitedLoader", false);
                        if (t == null) continue;
                        tuIsTransparent = t.GetMethod("isTransparentMaterial", BindingFlags.Public | BindingFlags.Static);
                        break;
                    }
                }
                catch { }
            }
            if (tuIsTransparent != null)
            {
                try { if ((bool)tuIsTransparent.Invoke(null, new object[] { m })) return true; }
                catch { tuIsTransparent = null; }
            }
            if (m.renderQueue >= 2900) return true;
            Shader sh = m.shader;
            if (sh != null)
            {
                string sn = sh.name.ToLowerInvariant();
                if (sn.Contains("transparent") || sn.Contains("translucent") || sn.Contains("glass")
                    || sn.Contains("/alpha/") || sn.Contains("cutoff") || sn.Contains("unlit"))
                    return true;
            }
            return false;
        }

        bool excludedPart;
        bool isFairing;
        int lastRenderCount = -1;
        internal static readonly HashSet<string> IgnoredParts = new HashSet<string>();
        internal bool Excluded { get { return excludedPart; } }

        internal static void RefreshIgnored(string partName)
        {
            liveModules.RemoveWhere(delegate(ModuleReforged mm) { return mm == null; });
            List<ModuleReforged> ms = new List<ModuleReforged>(liveModules);
            for (int i = 0; i < ms.Count; i++)
            {
                ModuleReforged m = ms[i];
                if (m == null || m.part == null || m.part.partInfo == null || m.part.partInfo.name != partName) continue;
                bool was = m.excludedPart;
                m.excludedPart = m.ExcludedPart();
                if (m.excludedPart && !was) { m.RestoreOriginals(); m.FixDecals(); m.UpdateHeatBuff(); }
                else if (!m.excludedPart && was) { if (m.Active) m.Rebuild(); m.UpdateHeatBuff(); }
            }
        }

        bool ExcludedPart()
        {
            if (part.partInfo != null && IgnoredParts.Contains(part.partInfo.name)) return true;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                string mn = part.Modules[i].moduleName;
                if (mn == "FSengineBladed" || mn.StartsWith("FSpropeller")) return true;
            }
            bool engine = false;
            for (int i = 0; i < part.Modules.Count; i++)
                if (part.Modules[i].moduleName.StartsWith("ModuleEngines") ||
                    part.Modules[i].moduleName == "ModuleRoboticServoRotor") { engine = true; break; }
            if (!engine) return false;
            Renderer[] rs = part.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
                if (rs[i] != null && rs[i].name.ToLowerInvariant().Contains("blur")) return true;
            return false;
        }

        internal static string RelPath(Transform t, Transform root)
        {
            string path = t.name;
            Transform pn = t.parent;
            while (pn != null && pn != root) { path = pn.name + "/" + path; pn = pn.parent; }
            return path;
        }

        Material[] PrefabMats(Renderer r)
        {
            try
            {
                if (part == null || part.partInfo == null || part.partInfo.partPrefab == null || r == null) return null;
                Transform pref = part.partInfo.partPrefab.transform;

                Transform pt = pref.Find(RelPath(r.transform, part.transform));
                Renderer pr = (pt != null) ? pt.GetComponent<Renderer>() : null;

                if (pr == null)
                {
                    Renderer[] all = part.partInfo.partPrefab.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < all.Length; i++)
                        if (all[i] != null && all[i].name == r.name) { pr = all[i]; break; }
                }
                return (pr != null) ? pr.sharedMaterials : null;
            }
            catch { return null; }
        }

        internal static Mesh MeshOf(Renderer r)
        {
            if (r == null) return null;
            SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            MeshFilter mf = r.GetComponent<MeshFilter>();
            return (mf != null) ? mf.sharedMesh : null;
        }

        static void SetMeshOf(Renderer r, Mesh m)
        {
            if (r == null) return;
            SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
            if (smr != null) { smr.sharedMesh = m; return; }
            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = m;
        }

        static List<BoneWeight> SkinWeights(Mesh src, Renderer r)
        {
            if (!(r is SkinnedMeshRenderer) || src == null) return null;
            BoneWeight[] bw = src.boneWeights;
            return (bw != null && bw.Length == src.vertexCount) ? new List<BoneWeight>(bw) : null;
        }

        static bool SkinOk(Renderer r, Mesh src)
        {
            if (!(r is SkinnedMeshRenderer)) return true;
            return src != null && src.boneWeights != null && src.boneWeights.Length == src.vertexCount;
        }

        static readonly HashSet<int> ourSplitIds = new HashSet<int>();

        static bool IsCut(Mesh m) { return m != null && (ourSplitIds.Contains(m.GetInstanceID()) || m.name.StartsWith("RDXcut")); }

        Mesh OriginalMesh(Renderer r, Mesh cur)
        {
            if (cur != null && !IsCut(cur)) return cur;
            Mesh pm = PrefabMesh(r);
            return (pm != null) ? pm : cur;
        }

        void TrackSplit(Renderer r, Mesh nm)
        {
            nm.name = "RDXcut";
            SetMeshOf(r, nm);
            splitMeshes.Add(nm);
            ourSplitIds.Add(nm.GetInstanceID());
        }

        bool ResetForeignMesh(Renderer r)
        {
            Mesh cm = MeshOf(r);
            bool gone = !object.ReferenceEquals(cm, null) && cm == null;
            bool foreign = IsCut(cm) && !splitMeshes.Contains(cm);
            if (!gone && !foreign) return false;
            Mesh pm = PrefabMesh(r);
            if (pm == null) return false;
            SetMeshOf(r, pm);
            Material[] stock = PrefabMats(r);
            if (stock != null) r.sharedMaterials = stock;
            return true;
        }

        void ResetForeignMeshes(List<Renderer> rs)
        {
            for (int i = 0; i < rs.Count; i++)
                if (rs[i] != null && !pristine.ContainsKey(rs[i])) ResetForeignMesh(rs[i]);
        }

        void ReleaseMaterials(List<Material> prev)
        {
            if (prev.Count == 0) return;
            HashSet<int> dying = new HashSet<int>();
            for (int i = 0; i < prev.Count; i++) if (prev[i] != null) dying.Add(prev[i].GetInstanceID());
            foreach (KeyValuePair<Renderer, Material[]> kv in pristine)
            {
                Renderer r = kv.Key;
                if (r == null) continue;
                Material[] sm = r.sharedMaterials;
                bool stale = false;
                for (int j = 0; j < sm.Length && !stale; j++)
                    if (!object.ReferenceEquals(sm[j], null) && dying.Contains(sm[j].GetInstanceID())) stale = true;
                if (!stale) continue;
                Mesh om;
                if (origMesh.TryGetValue(r, out om) && om != null) SetMeshOf(r, om);
                Material[] os;
                if (origShared.TryGetValue(r, out os) && os != null) r.sharedMaterials = os;
            }
            for (int i = 0; i < prev.Count; i++) if (prev[i] != null) Destroy(prev[i]);
        }

        void PadMaterials(List<Renderer> rs)
        {
            for (int i = 0; i < rs.Count; i++)
            {
                Renderer r = rs[i];
                if (r == null) continue;
                Mesh m = MeshOf(r);
                if (!IsCut(m)) continue;
                Material[] sm = r.sharedMaterials;
                if (sm.Length == 0 || sm.Length >= m.subMeshCount) continue;
                Material[] pad = new Material[m.subMeshCount];
                for (int j = 0; j < pad.Length; j++) pad[j] = sm[Mathf.Min(j, sm.Length - 1)];
                r.sharedMaterials = pad;
            }
        }

        static int cdlState = -1;
        static Type cdlType;
        static FieldInfo cdlTargets, cdlRend, cdlMesh;

        static bool CdlReady()
        {
            if (cdlState >= 0) return cdlState == 1;
            cdlState = 0;
            try
            {
                foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                {
                    if (la == null || la.assembly == null) continue;
                    Type t = la.assembly.GetType("ConformalDecals.ModuleConformalDecal", false);
                    Type pt = la.assembly.GetType("ConformalDecals.ProjectionTarget", false);
                    if (t == null || pt == null) continue;
                    BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance;
                    cdlTargets = t.GetField("_targets", bf);
                    cdlRend = pt.GetField("_targetRenderer", bf);
                    cdlMesh = pt.GetField("_targetMesh", bf);
                    if (cdlTargets != null && cdlRend != null && cdlMesh != null) cdlType = t;
                    break;
                }
            }
            catch { cdlType = null; }
            cdlState = (cdlType != null) ? 1 : 0;
            return cdlState == 1;
        }

        Mesh FullMesh(Renderer r)
        {
            Mesh om;
            if (origMesh.TryGetValue(r, out om) && om != null && !IsCut(om)) return om;
            Mesh cm = MeshOf(r);
            if (cm != null && !IsCut(cm)) return cm;
            return PrefabMesh(r);
        }

        void FixDecals()
        {
            if (part == null || part.children == null || part.children.Count == 0 || !CdlReady()) return;
            for (int c = 0; c < part.children.Count; c++)
            {
                Part ch = part.children[c];
                if (ch == null) continue;
                for (int k = 0; k < ch.Modules.Count; k++)
                {
                    PartModule pm = ch.Modules[k];
                    if (pm == null || !cdlType.IsInstanceOfType(pm)) continue;
                    System.Collections.IList tl = null;
                    try { tl = cdlTargets.GetValue(pm) as System.Collections.IList; } catch { }
                    if (tl == null) continue;
                    for (int i = 0; i < tl.Count; i++)
                    {
                        object tg = tl[i];
                        if (tg == null) continue;
                        try
                        {
                            Renderer tr = cdlRend.GetValue(tg) as Renderer;
                            if (tr == null) continue;
                            Mesh tm = cdlMesh.GetValue(tg) as Mesh;
                            bool gone = !object.ReferenceEquals(tm, null) && tm == null;
                            if (!gone && !IsCut(tm)) continue;
                            Mesh full = FullMesh(tr);
                            if (full != null && full != tm) cdlMesh.SetValue(tg, full);
                        }
                        catch { }
                    }
                }
            }
        }

        internal static void FixAllDecals()
        {
            if (!CdlReady()) return;
            foreach (ModuleReforged m in liveModules)
                if (m != null && m.part != null && m.part.children != null && m.part.children.Count > 0) m.FixDecals();
        }

        void FreeSplitMeshes()
        {
            for (int i = 0; i < splitMeshes.Count; i++)
                if (splitMeshes[i] != null) { ourSplitIds.Remove(splitMeshes[i].GetInstanceID()); Destroy(splitMeshes[i]); }
            splitMeshes.Clear();
        }

        Mesh PrefabMesh(Renderer r)
        {
            try
            {
                if (part == null || part.partInfo == null || part.partInfo.partPrefab == null || r == null) return null;
                Transform pref = part.partInfo.partPrefab.transform;
                Transform pt = pref.Find(RelPath(r.transform, part.transform));
                Mesh pm = (pt != null) ? MeshOf(pt.GetComponent<Renderer>()) : null;
                if (pm == null)
                {
                    Renderer[] all = part.partInfo.partPrefab.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < all.Length; i++)
                        if (all[i] != null && all[i].name == r.name && all[i].GetType() == r.GetType()) { pm = MeshOf(all[i]); if (pm != null) break; }
                }
                return pm;
            }
            catch { return null; }
        }

        List<Renderer> JettisonRenderers()
        {
            List<Renderer> list = new List<Renderer>();
            if (part == null) return list;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                ModuleJettison mj = part.Modules[i] as ModuleJettison;
                if (mj == null || mj.isJettisoned) continue;
                Transform jt = mj.jettisonTransform;
                if (jt == null || jt.IsChildOf(part.transform)) continue;
                Renderer[] rs = jt.GetComponentsInChildren<Renderer>(true);
                for (int k = 0; k < rs.Length; k++)
                    if (rs[k] != null && !list.Contains(rs[k]) && !SkipRenderer(rs[k])) list.Add(rs[k]);
            }
            return list;
        }

        class Orphan { public Renderer r; public Mesh mesh; public readonly List<Material> mats = new List<Material>(); }
        static readonly List<Orphan> orphans = new List<Orphan>();

        void HandOff(Renderer r)
        {
            Orphan o = new Orphan();
            o.r = r;
            Material[] pris;
            pristine.TryGetValue(r, out pris);
            Material[] sm = r.sharedMaterials;
            for (int j = 0; j < sm.Length; j++)
            {
                Material m = sm[j];
                if (m == null || o.mats.Contains(m)) continue;
                if (created.Remove(m) || (pris != null && Array.IndexOf(pris, m) >= 0)) o.mats.Add(m);
            }
            if (pris != null)
                for (int j = 0; j < pris.Length; j++)
                    if (pris[j] != null && !o.mats.Contains(pris[j])) Destroy(pris[j]);
            Mesh cm = MeshOf(r);
            if (cm != null && splitMeshes.Remove(cm)) o.mesh = cm;
            pristine.Remove(r);
            origShared.Remove(r);
            origMesh.Remove(r);
            if (o.mats.Count > 0 || o.mesh != null) orphans.Add(o);
        }

        void ReleaseLeavers(bool dying)
        {
            if (part == null || pristine.Count == 0) return;
            List<Renderer> keep = dying ? null : JettisonRenderers();
            List<Renderer> gone = null;
            foreach (KeyValuePair<Renderer, Material[]> kv in pristine)
            {
                Renderer r = kv.Key;
                if (r == null || r.transform.IsChildOf(part.transform) || (keep != null && keep.Contains(r))) continue;
                if (gone == null) gone = new List<Renderer>();
                gone.Add(r);
            }
            if (gone != null)
                for (int i = 0; i < gone.Count; i++) HandOff(gone[i]);
        }

        internal static void SweepOrphans()
        {
            for (int i = orphans.Count - 1; i >= 0; i--)
            {
                Orphan o = orphans[i];
                if (o.r != null) continue;
                for (int j = 0; j < o.mats.Count; j++) if (o.mats[j] != null) UnityEngine.Object.Destroy(o.mats[j]);
                if (o.mesh != null) { ourSplitIds.Remove(o.mesh.GetInstanceID()); UnityEngine.Object.Destroy(o.mesh); }
                orphans.RemoveAt(i);
            }
        }

        List<Renderer> OwnRenderers()
        {
            List<Renderer> mine = new List<Renderer>();
            if (part == null) return mine;
            Transform iva = (part.internalModel != null) ? part.internalModel.transform : null;
            Transform root = part.transform.Find("model");
            if (root == null) root = part.transform;
            Renderer[] all = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || SkipRenderer(all[i])) continue;
                if (iva != null && all[i].transform.IsChildOf(iva)) continue;
                if (all[i].GetComponentInParent<Camera>() != null) continue;
                Part owner = all[i].GetComponentInParent<Part>();
                if (owner == null || owner == part) mine.Add(all[i]);
            }
            List<Renderer> jr = JettisonRenderers();
            for (int i = 0; i < jr.Count; i++) if (!mine.Contains(jr[i])) mine.Add(jr[i]);
            return mine;
        }

        static string TexInfo(Texture t) { return (t == null) ? "-" : (t.name + " " + t.width + "x" + t.height); }

        internal void LogDiag()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            string pn = (part.partInfo != null) ? part.partInfo.name : part.name;
            string pt = (part.partInfo != null) ? part.partInfo.title : "";
            sb.Append("[ReforgedRedux] part report: ").Append(pn).Append(" (").Append(pt).Append(")\n");
            sb.Append("  painted=").Append(Active).Append(" excluded=").Append(excludedPart).Append(" fairing=").Append(isFairing)
              .Append(" colour=").Append(rdxColored).Append(" mode=").Append(rdxColorMode).Append(" finish=").Append(rdxFinish)
              .Append(" keepShiny=").Append(rdxKeepShiny).Append(" region=").Append(rdxRegion)
              .Append(" detail=").Append(rdxPaintDetail.ToString("0.00")).Append(" texDetail=").Append(rdxTexDetail).Append("\n");
            sb.Append("  stack=").Append(string.IsNullOrEmpty(rdxStack) ? "-" : rdxStack).Append(" zones=").Append(ParseZones(rdxZones).Count)
              .Append(" paintRes=").Append(WeatherCache.LowVram ? "low" : (WeatherCache.SmallPaintPart(part) ? "small" : "full")).Append("\n");
            sb.Append("  modules:");
            for (int i = 0; i < part.Modules.Count; i++) sb.Append(' ').Append(part.Modules[i].moduleName);
            sb.Append("\n");
            List<Renderer> own = OwnRenderers();
            List<Renderer> all = new List<Renderer>(part.GetComponentsInChildren<Renderer>(true));
            for (int i = 0; i < own.Count; i++) if (!all.Contains(own[i])) all.Add(own[i]);
            for (int i = 0; i < all.Count; i++)
            {
                Renderer r = all[i];
                if (r == null) continue;
                Part owner = r.GetComponentInParent<Part>();
                if (owner != null && owner != part && !own.Contains(r)) continue;
                string why = SkipReason(r);
                if (why == null && !own.Contains(r)) why = "outside the part model or IVA";
                Mesh ms = MeshOf(r);
                sb.Append("  [").Append(i).Append("] ").Append(RelPath(r.transform, part.transform))
                  .Append(" | ").Append(r.GetType().Name)
                  .Append((r.enabled && r.gameObject.activeInHierarchy) ? " visible" : " hidden")
                  .Append(" | ").Append(why == null ? "paintable" : ("skipped: " + why));
                if (ms != null)
                    sb.Append(" | mesh ").Append(ms.name).Append(" v=").Append(ms.vertexCount).Append(" sub=").Append(ms.subMeshCount)
                      .Append(ms.isReadable ? " readable" : " not-readable")
                      .Append(IsCut(ms) && !splitMeshes.Contains(ms) ? " (cut of another part)" : "");
                else sb.Append(" | no mesh");
                Material[] mats = r.sharedMaterials;
                sb.Append(" | mats=").Append(mats.Length);
                sb.Append("\n");
                for (int j = 0; j < mats.Length; j++)
                {
                    Material m = mats[j];
                    if (m == null) { sb.Append("      mat").Append(j).Append(": none\n"); continue; }
                    sb.Append("      mat").Append(j).Append(": ").Append(m.shader != null ? m.shader.name : "?")
                      .Append(" q=").Append(m.renderQueue)
                      .Append(IsGlass(m) ? " transparent" : "")
                      .Append(created.Contains(m) ? " reforged" : "");
                    if (m.HasProperty("_Opacity") && m.GetFloat("_Opacity") < 0.99f) sb.Append(" opacity=").Append(m.GetFloat("_Opacity").ToString("0.00"));
                    if (m.HasProperty(IdSpecColor)) { Color sc = m.GetColor(IdSpecColor); sb.Append(" spec=").Append(((sc.r + sc.g + sc.b) / 3f).ToString("0.00")); }
                    if (m.HasProperty(IdShininess)) sb.Append(" shine=").Append(m.GetFloat(IdShininess).ToString("0.00"));
                    if (m.HasProperty(IdMainTex))
                    {
                        Texture mt = m.GetTexture(IdMainTex);
                        sb.Append(" main=").Append(TexInfo(mt)).Append(WeatherCache.IsBaked(mt) ? " (painted)" : "");
                    }
                    if (m.HasProperty(IdBumpMap)) sb.Append(" bump=").Append(TexInfo(m.GetTexture(IdBumpMap)));
                    if (m.HasProperty("_MaskTex")) sb.Append(" mask=").Append(TexInfo(m.GetTexture("_MaskTex")));
                    string[] kw = m.shaderKeywords;
                    if (kw != null && kw.Length > 0) sb.Append(" keywords=").Append(string.Join(",", kw));
                    sb.Append("\n");
                }
            }
            Debug.Log(sb.ToString());
        }

        public override void OnStart(StartState state)
        {
            ReforgedPainter.EnsureSettings();
            liveModules.Add(this);
            excludedPart = ExcludedPart();
            isFairing = false;
            for (int i = 0; i < part.Modules.Count; i++)
                if (part.Modules[i].moduleName.IndexOf("Fairing", StringComparison.OrdinalIgnoreCase) >= 0) { isFairing = true; break; }
            if (isFairing && HighLogic.LoadedSceneIsEditor) StartCoroutine(FairingWatch());
        }

        int ModelRenderCount()
        {
            Transform m = (part != null) ? part.transform.Find("model") : null;
            return (m != null) ? m.GetComponentsInChildren<Renderer>(true).Length : 0;
        }
        System.Collections.IEnumerator FairingWatch()
        {
            WaitForSeconds wait = new WaitForSeconds(0.3f);
            while (part != null)
            {
                yield return wait;
                if (Active && ModelRenderCount() != lastRenderCount) Rebuild();
            }
        }

        public override void OnStartFinished(StartState state)
        {
            BaseField tf = Fields["rdxTileMass"];
            if (tf != null) tf.guiName = Loc.T("#RDX_HeatTiles", "Heat tiles");
            if (Active) { if (DeferLoadBakes) EnqueueLoadBake(this); else Rebuild(); }
            UpdateHeatBuff(false);
        }

        bool shapeDirty;
        float shapeTime;

        [KSPEvent(active = true)]
        public void OnPartModelChanged(BaseEventDetails data) { shapeDirty = true; shapeTime = Time.realtimeSinceStartup; }

        [KSPEvent(active = true)]
        public void OnChangeTextureScale(BaseEventDetails data) { shapeDirty = true; shapeTime = Time.realtimeSinceStartup; }

        void Update()
        {
            if (!shapeDirty || Time.realtimeSinceStartup - shapeTime < 0.3f) return;
            shapeDirty = false;
            if (part == null) return;
            RefreshPristineTiling();
            List<int> meshIds = new List<int>();
            foreach (KeyValuePair<Renderer, Mesh> kv in origMesh) if (kv.Value != null) meshIds.Add(kv.Value.GetInstanceID());
            WeatherCache.ForgetShape(part, meshIds);
            if (Active) Rebuild();
            UpdateHeatBuff();
        }

        void RefreshPristineTiling()
        {
            int[] props = { IdMainTex, IdBumpMap };
            foreach (KeyValuePair<Renderer, Material[]> kv in pristine)
            {
                Renderer r = kv.Key;
                if (r == null) continue;
                Material[] os; origShared.TryGetValue(r, out os);
                Mesh cm = MeshOf(r);
                Material[] cur = IsCut(cm) ? null : r.sharedMaterials;
                for (int j = 0; j < kv.Value.Length; j++)
                {
                    Material pm = kv.Value[j];
                    if (pm == null) continue;
                    Material om = (os != null && j < os.Length) ? os[j] : null;
                    Material lm = (cur != null && j < cur.Length) ? cur[j] : null;
                    for (int k = 0; k < props.Length; k++)
                    {
                        int id = props[k];
                        if (!pm.HasProperty(id)) continue;
                        Vector2 ps = pm.GetTextureScale(id), po = pm.GetTextureOffset(id);
                        Material src = null;
                        if (om != null && om.HasProperty(id) && (om.GetTextureScale(id) != ps || om.GetTextureOffset(id) != po)) src = om;
                        else if (lm != null && lm.HasProperty(id) && (lm.GetTextureScale(id) != ps || lm.GetTextureOffset(id) != po)) src = lm;
                        if (src == null) continue;
                        pm.SetTextureScale(id, src.GetTextureScale(id));
                        pm.SetTextureOffset(id, src.GetTextureOffset(id));
                    }
                }
            }
        }

        void OnDestroy()
        {
            liveModules.Remove(this);
            try { ReleaseLeavers(true); } catch { }
            for (int i = 0; i < created.Count; i++) if (created[i] != null) Destroy(created[i]);
            created.Clear();
            FreeSplitMeshes();
        }

        internal static bool DeferLoadBakes = false;
        internal static bool EvenShine = true;
        internal static bool HeatTileWeight = true;
        internal static bool HeatTileHeat = true;
        const float TileKgPerM2 = 8f;

        static int roState = -1;
        internal static bool RealismOverhaul
        {
            get
            {
                if (roState < 0)
                {
                    roState = 0;
                    try
                    {
                        foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                            if (la != null && la.name == "RealismOverhaul") { roState = 1; break; }
                    }
                    catch { }
                    if (roState == 1) Debug.Log("[ReforgedRedux] Realism Overhaul found, heat tile effects are off");
                }
                return roState == 1;
            }
        }
        const float StockMetal = 0f;
        const float StockSmooth = 0.45f;
        const float WinSmooth = 0.8f;

        static bool AutoStock(bool colored, int cmode) { return EvenShine && colored && cmode == 0; }

        void ApplyStockFinish(Material m, Shader tu)
        {
            ApplyFinish(m, tu, StockMetal, StockSmooth);
            if (rdxKeepShiny && m.HasProperty(IdMainTex) && m.HasProperty(IdMetalGloss)
                && WeatherCache.HasShiny(m.GetTexture(IdMainTex)))
            {
                Texture gm = WeatherCache.StockGlossMask(m.GetTexture(IdMainTex), StockMetal, StockSmooth, WinSmooth);
                if (gm != null) { m.SetTexture(IdMetalGloss, gm); m.SetFloat(IdMetal, 1f); m.SetFloat(IdSmooth, 1f); }
            }
        }

        static Texture TexOf(Material m, string n) { return m.HasProperty(n) ? m.GetTexture(n) : null; }
        static Color ColOf(Material m, string n) { return m.HasProperty(n) ? m.GetColor(n) : Color.white; }
        static Vector4 VecOf(Material m, string n, Vector4 def) { return m.HasProperty(n) ? m.GetVector(n) : def; }

        static void FlattenRecolor(Material m)
        {
            if (m == null || !m.IsKeywordEnabled("TU_RECOLOR") || !m.HasProperty(IdMainTex)) return;
            bool norm = m.IsKeywordEnabled("TU_RECOLOR_NORM") || m.IsKeywordEnabled("TU_RECOLOR_NORM_INPUT");
            bool input = m.IsKeywordEnabled("TU_RECOLOR_INPUT") || m.IsKeywordEnabled("TU_RECOLOR_NORM_INPUT");
            bool stockSpec = m.IsKeywordEnabled("TU_STOCK_SPEC");
            Vector4 one = new Vector4(1f, 1f, 1f, 0f);
            Texture2D albedo, gloss;
            if (!WeatherCache.FlattenTu(m.GetTexture(IdMainTex), TexOf(m, "_MaskTex"), TexOf(m, "_MetallicGlossMap"),
                TexOf(m, "_MetalGlossNormMask"), TexOf(m, "_MetalGlossInputMask"), norm, input, stockSpec,
                ColOf(m, "_MaskColor1"), ColOf(m, "_MaskColor2"), ColOf(m, "_MaskColor3"),
                VecOf(m, "_MaskMetallic", Vector4.zero), VecOf(m, "_DiffuseNorm", one), VecOf(m, "_MetalNorm", one),
                VecOf(m, "_SmoothnessNorm", one), VecOf(m, "_DetailMult", one), out albedo, out gloss)) return;
            m.SetTexture(IdMainTex, albedo);
            if (m.HasProperty(IdMetalGloss)) m.SetTexture(IdMetalGloss, gloss);
            string[] kw = m.shaderKeywords;
            if (kw == null) return;
            for (int i = 0; i < kw.Length; i++)
                if (kw[i].StartsWith("TU_RECOLOR") || kw[i] == "TU_STOCK_SPEC") m.DisableKeyword(kw[i]);
        }

        static void ColorDone(Material m)
        {
            if (m.HasProperty(IdColor)) m.SetColor(IdColor, Color.white);
            string[] kw = m.shaderKeywords;
            if (kw == null) return;
            for (int i = 0; i < kw.Length; i++)
                if (kw[i].StartsWith("TU_RECOLOR")) m.DisableKeyword(kw[i]);
        }

        internal static void RebuildAllPainted()
        {
            liveModules.RemoveWhere(delegate(ModuleReforged mm) { return mm == null; });
            List<ModuleReforged> ms = new List<ModuleReforged>(liveModules);
            for (int i = 0; i < ms.Count; i++)
                if (ms[i] != null && ms[i].part != null && ms[i].Active) ms[i].Rebuild();
        }
        static readonly Queue<ModuleReforged> loadQueue = new Queue<ModuleReforged>();
        static void EnqueueLoadBake(ModuleReforged m) { if (m != null) loadQueue.Enqueue(m); }
        public static void ClearLoadQueue() { loadQueue.Clear(); }

        public static bool PumpLoadQueue()
        {
            while (loadQueue.Count > 0)
            {
                ModuleReforged m = loadQueue.Dequeue();
                if (m == null || m.part == null || !m.Active) continue;
                try { m.Rebuild(); }
                catch (Exception e) { Debug.LogError("[ReforgedRedux] deferred paint failed: " + e.Message); }
                return true;
            }
            return false;
        }

        static void SweepCaches()
        {
            liveModules.RemoveWhere(delegate(ModuleReforged mm) { return mm == null; });
            HashSet<int> live = new HashSet<int>();
            foreach (ModuleReforged m in liveModules)
            {
                if (m == null) continue;
                foreach (KeyValuePair<Renderer, Material[]> kv in m.pristine)
                {
                    Renderer r = kv.Key;
                    if (r == null) continue;
                    Material[] mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        Material mat = mats[i];
                        if (mat == null) continue;
                        Texture a = mat.HasProperty(IdMainTex) ? mat.GetTexture(IdMainTex) : null; if (a != null) live.Add(a.GetInstanceID());
                        Texture b = mat.HasProperty(IdBumpMap) ? mat.GetTexture(IdBumpMap) : null; if (b != null) live.Add(b.GetInstanceID());
                        Texture g = mat.HasProperty(IdMetalGloss) ? mat.GetTexture(IdMetalGloss) : null; if (g != null) live.Add(g.GetInstanceID());
                    }
                }
            }
            for (int o = 0; o < orphans.Count; o++)
            {
                Renderer r = orphans[o].r;
                if (r == null) continue;
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material mat = mats[i];
                    if (mat == null) continue;
                    Texture a = mat.HasProperty(IdMainTex) ? mat.GetTexture(IdMainTex) : null; if (a != null) live.Add(a.GetInstanceID());
                    Texture b = mat.HasProperty(IdBumpMap) ? mat.GetTexture(IdBumpMap) : null; if (b != null) live.Add(b.GetInstanceID());
                    Texture g = mat.HasProperty(IdMetalGloss) ? mat.GetTexture(IdMetalGloss) : null; if (g != null) live.Add(g.GetInstanceID());
                }
            }
            WeatherCache.EvictExcept(live);
        }

        void RestoreOriginals()
        {
            foreach (KeyValuePair<Renderer, Material[]> kv in origShared)
                if (kv.Key != null && kv.Value != null) kv.Key.sharedMaterials = kv.Value;
            for (int i = 0; i < created.Count; i++)
                if (created[i] != null) Destroy(created[i]);
            created.Clear();
            foreach (KeyValuePair<Renderer, Material[]> kv in pristine)
                for (int j = 0; j < kv.Value.Length; j++)
                    if (kv.Value[j] != null) Destroy(kv.Value[j]);
            pristine.Clear();
            origShared.Clear();
            RestoreMeshesToOriginal();
            origMesh.Clear();
            ReapplyVariant();
        }

        void ReapplyVariant()
        {
            try
            {
                ModulePartVariants mpv = part.FindModuleImplementing<ModulePartVariants>();
                if (mpv == null || mpv.SelectedVariant == null || string.IsNullOrEmpty(mpv.SelectedVariant.Name)) return;
                string cur = mpv.SelectedVariant.Name;

                List<PartVariant> vl = mpv.variantList;
                if (vl != null && vl.Count > 1)
                    for (int i = 0; i < vl.Count; i++)
                        if (vl[i] != null && vl[i].Name != cur) { mpv.SetVariant(vl[i].Name); break; }
                mpv.SetVariant(cur);
            }
            catch (Exception e) { Debug.LogWarning("[ReforgedRedux] variant reapply failed: " + e.Message); }
        }

        static string StripPlaces(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return stack;
            string[] es = stack.Split(';');
            for (int i = 0; i < es.Length; i++)
            {
                string[] f = es[i].Split(':');
                if (f.Length >= 3) es[i] = f[0] + ":whole:" + f[2] + (f.Length >= 4 ? ":" + f[3] : "");
            }
            return string.Join(";", es);
        }

        internal static string ScaleSuffix(string tex, float scale)
        {
            if (string.IsNullOrEmpty(tex) || tex.StartsWith("tint#") || tex.StartsWith("paint#") || tex.StartsWith("omul#")) return "";
            if (scale <= 0.01f || float.IsNaN(scale)) scale = 1f;
            return ":" + scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        void RestoreMeshesToOriginal()
        {
            List<Renderer> adopt = null;
            foreach (KeyValuePair<Renderer, Mesh> kv in origMesh)
            {
                if (kv.Key == null || kv.Value == null) continue;
                Mesh cur = MeshOf(kv.Key);
                if (cur != null && cur != kv.Value && !IsCut(cur))
                {
                    if (adopt == null) adopt = new List<Renderer>();
                    adopt.Add(kv.Key);
                    continue;
                }
                SetMeshOf(kv.Key, kv.Value);
            }
            if (adopt != null)
                for (int i = 0; i < adopt.Count; i++) origMesh[adopt[i]] = MeshOf(adopt[i]);
            FreeSplitMeshes();
        }

        void RegionExtent(List<Renderer> rs, Vector3 wdir, out float lo, out float hi)
        {
            lo = float.MaxValue; hi = float.MinValue;
            for (int i = 0; i < rs.Count; i++)
            {
                Mesh mfm = ModuleReforged.MeshOf(rs[i]);
                if (mfm == null || !mfm.isReadable) continue;
                Vector3[] vs = mfm.vertices;
                Transform t = rs[i].transform;
                for (int v = 0; v < vs.Length; v++)
                {
                    float c = Vector3.Dot(t.TransformPoint(vs[v]), wdir);
                    if (c < lo) lo = c;
                    if (c > hi) hi = c;
                }
            }
        }

        internal static void MeshToLists(Mesh m, out List<Vector3> V, out List<Vector3> N,
            out List<Vector4> T, out List<Vector2> U, out List<int> tris)
        {
            Vector3[] v = m.vertices; V = new List<Vector3>(v);
            Vector3[] n = m.normals; N = (n != null && n.Length == v.Length) ? new List<Vector3>(n) : null;
            Vector4[] t4 = m.tangents; T = (t4 != null && t4.Length == v.Length) ? new List<Vector4>(t4) : null;
            Vector2[] uv = m.uv; U = (uv != null && uv.Length == v.Length) ? new List<Vector2>(uv) : null;
            tris = new List<int>(m.triangles);
        }

        internal static void ClipSoup(List<Vector3> V, List<Vector3> N, List<Vector4> T, List<Vector2> U,
            Transform rt, Vector3 wdir, float cw, List<int> inTris, List<int> keep, List<int> drop)
        { ClipSoup(V, N, T, U, null, rt, wdir, cw, inTris, keep, drop); }

        internal static void ClipSoup(List<Vector3> V, List<Vector3> N, List<Vector4> T, List<Vector2> U, List<BoneWeight> W,
            Transform rt, Vector3 wdir, float cw, List<int> inTris, List<int> keep, List<int> drop)
        {
            keep.Clear(); drop.Clear();
            List<int> rp = new List<int>(4), op = new List<int>(4);
            int[] idx = new int[3]; float[] dd = new float[3];
            for (int t = 0; t + 2 < inTris.Count; t += 3)
            {
                idx[0] = inTris[t]; idx[1] = inTris[t + 1]; idx[2] = inTris[t + 2];
                for (int k = 0; k < 3; k++) dd[k] = Vector3.Dot(rt.TransformPoint(V[idx[k]]), wdir) - cw;
                rp.Clear(); op.Clear();
                for (int k = 0; k < 3; k++)
                {
                    int cur = idx[k], nxt = idx[(k + 1) % 3];
                    float dc = dd[k], dn = dd[(k + 1) % 3];
                    if (dc >= 0f) rp.Add(cur); else op.Add(cur);
                    if ((dc >= 0f) != (dn >= 0f))
                    {
                        float tt = dc / (dc - dn);
                        int vi = V.Count;
                        V.Add(Vector3.Lerp(V[cur], V[nxt], tt));
                        if (N != null) N.Add(Vector3.Lerp(N[cur], N[nxt], tt).normalized);
                        if (T != null) T.Add(Vector4.Lerp(T[cur], T[nxt], tt));
                        if (U != null) U.Add(Vector2.Lerp(U[cur], U[nxt], tt));
                        if (W != null) W.Add(tt < 0.5f ? W[cur] : W[nxt]);
                        rp.Add(vi); op.Add(vi);
                    }
                }
                for (int i = 1; i + 1 < rp.Count; i++) { keep.Add(rp[0]); keep.Add(rp[i]); keep.Add(rp[i + 1]); }
                for (int i = 1; i + 1 < op.Count; i++) { drop.Add(op[0]); drop.Add(op[i]); drop.Add(op[i + 1]); }
            }
        }
        static bool InRegionPt(Vector3 p, Vector3 wdir, float cw, Vector3 upDir, float cwHeight, bool useHeight)
        {
            return Vector3.Dot(p, wdir) >= cw && (!useHeight || Vector3.Dot(p, upDir) <= cwHeight);
        }

        bool AssignRegionSplit(Renderer r, Material[] work, Material[] pris, bool geom,
            Vector3 wdir, float cw, Vector3 upDir, float cwHeight, bool useHeight, int target)
        {
            if (!geom || target == 2) return false;
            int n = pris.Length;
            Material[] outside = new Material[n];
            for (int j = 0; j < n; j++) outside[j] = (pris[j] != null) ? StockCopy(pris[j]) : null;
            Material[] inMats = (target == 1) ? outside : work;
            Material[] outMats = (target == 1) ? work : outside;
            Mesh curMesh = MeshOf(r);
            Mesh src = null;
            if (curMesh != null)
            {
                if (!origMesh.ContainsKey(r)) origMesh[r] = OriginalMesh(r, curMesh);
                src = origMesh[r];
            }
            bool centerIn = InRegionPt(r.bounds.center, wdir, cw, upDir, cwHeight, useHeight);
            if (src == null || !src.isReadable || src.subMeshCount != n || work.Length != n || !SkinOk(r, src))
            {
                r.materials = centerIn ? inMats : outMats;
                return true;
            }
            try
            {
                List<Vector3> V, N; List<Vector4> T; List<Vector2> U; List<int> all;
                MeshToLists(src, out V, out N, out T, out U, out all);
                List<BoneWeight> W = SkinWeights(src, r);
                List<List<int>> subTris = new List<List<int>>();
                List<Material> subMats = new List<Material>();
                Material[] whole = new Material[n];
                bool split = false;
                for (int j = 0; j < n; j++)
                {
                    List<int> tris = (n == 1) ? all : new List<int>(src.GetTriangles(j));
                    List<int> keep1 = new List<int>(), drop1 = new List<int>();
                    ClipSoup(V, N, T, U, W, r.transform, wdir, cw, tris, keep1, drop1);
                    List<int> painted = keep1, stock = drop1;
                    if (useHeight)
                    {
                        List<int> keep2 = new List<int>(), drop2 = new List<int>();
                        ClipSoup(V, N, T, U, W, r.transform, -upDir, -cwHeight, keep1, keep2, drop2);
                        painted = keep2; stock = drop1; stock.AddRange(drop2);
                    }
                    if (painted.Count > 0) { subTris.Add(painted); subMats.Add(inMats[j]); }
                    if (stock.Count > 0) { subTris.Add(stock); subMats.Add(outMats[j]); }
                    if (painted.Count > 0 && stock.Count > 0) split = true;
                    whole[j] = (painted.Count > 0 || (stock.Count == 0 && centerIn)) ? inMats[j] : outMats[j];
                }
                if (!split)
                {
                    r.materials = whole;
                    return true;
                }
                Mesh nm = new Mesh();
                if (V.Count > 65000) nm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                nm.vertices = V.ToArray();
                if (N != null) nm.normals = N.ToArray();
                if (T != null) nm.tangents = T.ToArray();
                if (U != null) nm.uv = U.ToArray();
                if (W != null) { nm.boneWeights = W.ToArray(); nm.bindposes = src.bindposes; }
                nm.subMeshCount = subTris.Count;
                for (int k = 0; k < subTris.Count; k++) nm.SetTriangles(subTris[k], k);
                nm.RecalculateBounds();
                TrackSplit(r, nm);
                r.materials = subMats.ToArray();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ReforgedRedux] region split failed: " + e.Message);
                r.materials = centerIn ? inMats : outMats;
                return true;
            }
        }

        void ApplyFinish(Material m, Shader tu) { ApplyFinish(m, tu, rdxMetal, rdxSmooth); }
        void ApplyFinish(Material m, Shader tu, float metal, float smooth)
        {
            Texture mt = m.HasProperty(IdMainTex) ? m.GetTexture(IdMainTex) : null;
            Texture bt = m.HasProperty(IdBumpMap) ? m.GetTexture(IdBumpMap) : null;
            int origQ = m.renderQueue;
            m.shader = tu;
            m.renderQueue = origQ;
            if (mt != null) m.SetTexture(IdMainTex, mt);
            if (bt != null) m.SetTexture(IdBumpMap, bt);
            FillEmptyTuSlots(m);
            if (m.HasProperty(IdMetalGloss)) m.SetTexture(IdMetalGloss, FillTexture(255, 255, 255, 255));
            m.DisableKeyword("TU_STOCK_SPEC");
            m.SetFloat(IdMetal, metal);
            m.SetFloat(IdSmooth, smooth);
        }

        public void Rebuild()
        {
            if (part == null || excludedPart) return;
            ReleaseLeavers(false);
            if (!Active) { RestoreOriginals(); FixDecals(); return; }
            WeatherCache.PaintDetailCur = rdxPaintDetail;
            WeatherCache.TexSkipCur = rdxTexDetail;
            RebuildInner();
            FixDecals();
            if (WeatherCache.NeedSweep()) SweepCaches();
        }
        void RebuildInner()
        {
            {
                List<Zone> _ex = ParseZones(rdxZones); bool _fd = false;
                if (_ex.Count == 0) { Vector3 _d; float _c, _f, _h; if (WeatherCache.RegionAxisDir(rdxRegion, out _d, out _c, out _f, out _h)) _fd = _f > 0.001f; else if (IsPlaneTok(rdxRegion)) _fd = PlaneFaded(rdxRegion); }
                if (_ex.Count > 0 || _fd) { RebuildMulti(_ex); return; }
            }
            List<Renderer> rs = OwnRenderers();
            bool autoFin = !rdxFinish && AutoStock(rdxColored, rdxColorMode);
            Shader tu = (rdxFinish || autoFin) ? ActiveTuShader() : null;
            Color col = new Color(rdxR, rdxG, rdxB);

            string regionPlace = rdxRegion;
            bool pickMode = regionPlace != null && regionPlace.StartsWith("pick|");
            string pickTarget = pickMode ? regionPlace.Substring(5) : null;
            bool regionMode = !string.IsNullOrEmpty(regionPlace) && regionPlace != "whole";
            bool geomRegion = regionMode && !pickMode;
            string bakeStack = StripPlaces(rdxStack);
            RestoreMeshesToOriginal();
            ResetForeignMeshes(rs);
            Vector3 wdir = Vector3.up; float cw = 0f;
            Vector3 upDir = part.transform.up; float cwHeight = 0f; bool useHeight = false;
            if (geomRegion && IsPlaneTok(regionPlace))
            {
                float pfw, pfh;
                if (!PlaneWorld(part.transform, regionPlace, out wdir, out cw, out pfw, out upDir, out cwHeight, out useHeight, out pfh)) geomRegion = false;
            }
            else if (geomRegion)
            {
                Vector3 ldir; float lcut, lfade, lheight;
                if (WeatherCache.RegionAxisDir(regionPlace, out ldir, out lcut, out lfade, out lheight))
                {
                    float rlo, rhi;
                    wdir = part.transform.TransformDirection(ldir).normalized;
                    RegionExtent(rs, wdir, out rlo, out rhi);
                    cw = rlo + lcut * (rhi - rlo);
                    if (lheight < 0.999f)
                    {
                        float vlo, vhi;
                        RegionExtent(rs, upDir, out vlo, out vhi);
                        cwHeight = vlo + lheight * (vhi - vlo);
                        useHeight = true;
                    }
                }
                else geomRegion = false;
            }

            List<Material> prev = new List<Material>(created);
            created.Clear();

            for (int i = 0; i < rs.Count; i++)
            {
                Renderer r = rs[i];
                Material[] pris = EnsurePristine(r);

                Material[] work = new Material[pris.Length];
                for (int j = 0; j < pris.Length; j++)
                {
                    if (pris[j] == null) { work[j] = null; continue; }
                    Material m = new Material(pris[j]);
                    created.Add(m);
                    bool glass = IsGlass(m) && !isFairing;
                    bool pickPaint = true;
                    if (pickMode)
                    {
                        bool mtc = RelPath(r.transform, part.transform) + "#" + j == pickTarget;
                        pickPaint = (rdxShineSpot == 2) || (rdxShineSpot == 1 ? !mtc : mtc);
                    }
                    if (pickPaint)
                    {
                        FlattenRecolor(m);
                        if (autoFin && tu != null && !glass) ApplyStockFinish(m, tu);
                        if (rdxFinish && tu != null && !glass)
                        {
                            ApplyFinish(m, tu);
                            if (rdxKeepShiny && m.HasProperty(IdMainTex) && m.HasProperty(IdMetalGloss)
                                && WeatherCache.HasShiny(m.GetTexture(IdMainTex)))
                            {
                                Texture mm = WeatherCache.ShinyMetalMask(m.GetTexture(IdMainTex));
                                if (mm != null) m.SetTexture(IdMetalGloss, mm);
                            }
                        }
                        if (rdxKeepShiny && (rdxColored || WeatherOn) && m.HasProperty(IdMainTex)
                            && WeatherCache.HasShiny(m.GetTexture(IdMainTex)))
                        {
                            Texture orig = m.GetTexture(IdMainTex);
                            if (orig != null)
                            {
                                Texture ob = m.HasProperty(IdBumpMap) ? m.GetTexture(IdBumpMap) : null;
                                Texture dirt = orig;
                                string cstack = PrependPaint(bakeStack, rdxColored, col, "whole", rdxColorMode);
                                if (cstack != "")
                                {
                                    Texture wantB;
                                    Texture want = WeatherCache.Baked(orig, ob, cstack, part, rdxScale, out wantB);
                                    if (want != null) dirt = want;
                                    if (wantB != null && m.HasProperty(IdBumpMap))
                                    {
                                        Texture mb = WeatherCache.MaskWindowsBump(wantB, ob, orig);
                                        m.SetTexture(IdBumpMap, mb != null ? mb : wantB);
                                    }
                                }
                                Texture masked = WeatherCache.MaskWindows(dirt, orig);
                                if (masked != null) m.SetTexture(IdMainTex, masked);
                                if (StackHasColor(cstack)) ColorDone(m);
                            }
                        }
                        else
                        {
                            string cstack = PrependPaint(bakeStack, rdxColored, col, "whole", rdxColorMode);
                            if (cstack != "" && m.HasProperty(IdMainTex))
                            {
                                Texture orig = m.GetTexture(IdMainTex);
                                if (orig != null)
                                {
                                    Texture ob = m.HasProperty(IdBumpMap) ? m.GetTexture(IdBumpMap) : null;
                                    Texture wantB;
                                    Texture want = WeatherCache.Baked(orig, ob, cstack, part, rdxScale, out wantB);
                                    if (want != null) m.SetTexture(IdMainTex, want);
                                    if (wantB != null && m.HasProperty(IdBumpMap)) m.SetTexture(IdBumpMap, wantB);
                                }
                                if (StackHasColor(cstack)) ColorDone(m);
                            }
                        }
                    }
                    work[j] = m;
                }
                if (!AssignRegionSplit(r, work, pris, geomRegion, wdir, cw, upDir, cwHeight, useHeight, rdxShineSpot))
                    r.materials = work;
            }

            PadMaterials(rs);
            ReleaseMaterials(prev);
            if (isFairing) lastRenderCount = ModelRenderCount();
        }

        public class Zone
        {
            public string region = "whole";
            public bool finish; public float metal, smooth;
            public bool colored; public float r = 1f, g = 1f, b = 1f;
            public string stack = ""; public int shineSpot; public float scale = 1f;
            public int cmode;
            public bool HasContent { get { return finish || colored || !string.IsNullOrEmpty(stack); } }
            public Color Col { get { return new Color(r, g, b); } }
        }
        static string FS(float f) { return f.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        static float FP(string x) { float f; float.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f); return f; }
        static string SerZones(List<Zone> zs)
        {
            if (zs == null || zs.Count == 0) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < zs.Count; i++)
            {
                if (i > 0) sb.Append('^');
                Zone z = zs[i];
                sb.Append(z.region).Append('~').Append(z.finish ? '1' : '0').Append('~').Append(FS(z.metal)).Append('~').Append(FS(z.smooth))
                  .Append('~').Append(z.colored ? '1' : '0').Append('~').Append(FS(z.r)).Append('~').Append(FS(z.g)).Append('~').Append(FS(z.b))
                  .Append('~').Append(z.shineSpot).Append('~').Append(FS(z.scale)).Append('~').Append(z.stack == null ? "" : z.stack)
                  .Append('~').Append(z.cmode);
            }
            return sb.ToString();
        }
        static List<Zone> ParseZones(string str)
        {
            List<Zone> list = new List<Zone>();
            if (string.IsNullOrEmpty(str)) return list;
            string[] ps = str.Split('^');
            for (int i = 0; i < ps.Length; i++)
            {
                string[] f = ps[i].Split('~');
                if (f.Length < 11) continue;
                Zone z = new Zone();
                z.region = f[0]; z.finish = f[1] == "1"; z.metal = FP(f[2]); z.smooth = FP(f[3]);
                z.colored = f[4] == "1"; z.r = FP(f[5]); z.g = FP(f[6]); z.b = FP(f[7]);
                int.TryParse(f[8], out z.shineSpot); z.scale = FP(f[9]); z.stack = f[10];
                if (f.Length > 11) int.TryParse(f[11], out z.cmode);
                list.Add(z);
            }
            return list;
        }
        Zone ActiveZone()
        {
            Zone z = new Zone();
            z.region = string.IsNullOrEmpty(rdxRegion) ? "whole" : rdxRegion;
            z.finish = rdxFinish; z.metal = rdxMetal; z.smooth = rdxSmooth;
            z.colored = rdxColored; z.r = rdxR; z.g = rdxG; z.b = rdxB; z.cmode = rdxColorMode;
            z.stack = rdxStack == null ? "" : rdxStack; z.shineSpot = rdxShineSpot; z.scale = rdxScale;
            return z;
        }
        void LoadZoneIntoFields(Zone z)
        {
            rdxRegion = string.IsNullOrEmpty(z.region) ? "whole" : z.region;
            rdxFinish = z.finish; rdxMetal = z.metal; rdxSmooth = z.smooth;
            rdxColored = z.colored; rdxR = z.r; rdxG = z.g; rdxB = z.b; rdxColorMode = z.cmode;
            rdxStack = z.stack == null ? "" : z.stack; rdxShineSpot = z.shineSpot; rdxScale = z.scale;
        }

        public void SetRegion(string reg)
        {
            if (string.IsNullOrEmpty(reg)) reg = "whole";
            if (reg == rdxRegion) return;
            List<Zone> ex = ParseZones(rdxZones);
            Zone act = ActiveZone();
            ex.RemoveAll(delegate(Zone zz) { return zz.region == act.region; });
            if (act.HasContent) ex.Add(act);
            Zone tgt = ex.Find(delegate(Zone zz) { return zz.region == reg; });
            if (tgt != null) ex.Remove(tgt); else tgt = new Zone();
            tgt.region = reg;
            LoadZoneIntoFields(tgt);
            rdxZones = SerZones(ex);
        }

        Material[] EnsurePristine(Renderer r)
        {
            Material[] pris;
            if (pristine.TryGetValue(r, out pris)) return pris;
            Material[] cur = r.materials;
            Material[] stock = PrefabMats(r);
            Material[] live = r.sharedMaterials;
            bool curPainted = false;
            for (int cj = 0; cj < cur.Length; cj++)
                if (cur[cj] != null && cur[cj].HasProperty(IdMainTex) && WeatherCache.IsBaked(cur[cj].GetTexture(IdMainTex))) { curPainted = true; break; }
            bool useStock = curPainted && stock != null && stock.Length == cur.Length;
            Material[] os = new Material[cur.Length];
            pris = new Material[cur.Length];
            for (int j = 0; j < cur.Length; j++)
            {
                Material sm = useStock ? stock[j] : cur[j];
                pris[j] = (sm != null) ? new Material(sm) : null;
                int liveQ = (cur[j] != null) ? cur[j].renderQueue : -1;
                if (pris[j] != null && liveQ != -1) pris[j].renderQueue = liveQ;
                Material o = useStock ? stock[j] : ((j < live.Length) ? live[j] : null);
                if (useStock && o != null && liveQ != -1 && o.renderQueue != liveQ) { o = new Material(o); o.renderQueue = liveQ; }
                os[j] = o;
            }
            origShared[r] = os;
            pristine[r] = pris;
            return pris;
        }

        Zone MergeBase(Zone baseZ, Zone z)
        {
            if (baseZ == null || !baseZ.HasContent) return z;
            Zone m = new Zone();
            m.region = z.region;
            m.finish = z.finish || baseZ.finish;
            if (z.finish) { m.metal = z.metal; m.smooth = z.smooth; }
            else { m.metal = baseZ.metal; m.smooth = baseZ.smooth; }
            if (z.colored) { m.colored = true; m.r = z.r; m.g = z.g; m.b = z.b; m.cmode = z.cmode; }
            else if (baseZ.colored) { m.colored = true; m.r = baseZ.r; m.g = baseZ.g; m.b = baseZ.b; m.cmode = baseZ.cmode; }
            string bs = baseZ.stack == null ? "" : baseZ.stack;
            string zs = z.stack == null ? "" : z.stack;
            m.stack = (bs == "") ? zs : (zs == "" ? bs : bs + ";" + zs);
            m.shineSpot = z.shineSpot;
            m.scale = (z.scale != 1f) ? z.scale : baseZ.scale;
            return m;
        }

        public void ClearWeatherAll()
        {
            rdxStack = "";
            List<Zone> ex = ParseZones(rdxZones);
            for (int i = 0; i < ex.Count; i++) ex[i].stack = "";
            ex.RemoveAll(delegate(Zone zz) { return !zz.HasContent; });
            rdxZones = SerZones(ex);
            UpdateHeatBuff();
            Rebuild();
        }
        Material StockCopy(Material pristine)
        {
            Material m = new Material(pristine); created.Add(m); return m;
        }

        Material BlitStock(Material pristine)
        {
            Material m = new Material(pristine); created.Add(m);
            if (m.HasProperty(IdMainTex))
            {
                Texture bc = WeatherCache.BlitCopy(m.GetTexture(IdMainTex));
                if (bc != null) m.SetTexture(IdMainTex, bc);
            }
            return m;
        }

        static string ScaleStack(string stack, float s)
        {
            if (string.IsNullOrEmpty(stack) || s >= 0.999f) return stack;
            string[] es = stack.Split(';');
            for (int i = 0; i < es.Length; i++)
            {
                string[] f = es[i].Split(':');
                if (f.Length < 3) continue;
                int ol; if (!int.TryParse(f[2], out ol)) continue;
                float mul = (ol <= 5) ? ((ol >= 2) ? 2.5f : 0.6f) : ol / 100f;
                int nl = Mathf.Clamp(Mathf.RoundToInt(s * mul * 100f), 1, 250);
                f[2] = nl.ToString();
                es[i] = string.Join(":", f);
            }
            return string.Join(";", es);
        }

        Material BuildZoneMaterial(Material pristineMat, Zone z) { return BuildZoneMaterial(pristineMat, z, 1f); }
        Material BuildZoneMaterial(Material pristineMat, Zone z, float strength)
        {
            Material m = new Material(pristineMat); created.Add(m);
            FlattenRecolor(m);
            bool glass = IsGlass(m) && !isFairing;
            bool weatherOn = !string.IsNullOrEmpty(z.stack);
            string bakeStack = ScaleStack(StripPlaces(z.stack), strength);
            bool autoFin = !z.finish && AutoStock(z.colored, z.cmode);
            Shader tu = (z.finish || autoFin) ? ActiveTuShader() : null;
            if (autoFin && tu != null && !glass) ApplyStockFinish(m, tu);
            if (z.finish && tu != null && !glass)
            {
                ApplyFinish(m, tu, z.metal * strength, z.smooth * strength);
                if (rdxKeepShiny && m.HasProperty(IdMainTex) && m.HasProperty(IdMetalGloss)
                    && WeatherCache.HasShiny(m.GetTexture(IdMainTex)))
                {
                    Texture mm = WeatherCache.ShinyMetalMask(m.GetTexture(IdMainTex));
                    if (mm != null) m.SetTexture(IdMetalGloss, mm);
                }
            }
            Color col = z.Col;
            if (rdxKeepShiny && (z.colored || weatherOn) && m.HasProperty(IdMainTex)
                && WeatherCache.HasShiny(m.GetTexture(IdMainTex)))
            {
                Texture orig = m.GetTexture(IdMainTex);
                if (orig != null)
                {
                    Texture ob = m.HasProperty(IdBumpMap) ? m.GetTexture(IdBumpMap) : null;
                    Texture dirt = orig;
                    string cstack = PrependPaint(bakeStack, z.colored, col, "whole", z.cmode);
                    if (cstack != "")
                    {
                        Texture wantB;
                        Texture want = WeatherCache.Baked(orig, ob, cstack, part, z.scale, out wantB);
                        if (want != null) dirt = want;
                        if (wantB != null && m.HasProperty(IdBumpMap))
                        {
                            Texture mb = WeatherCache.MaskWindowsBump(wantB, ob, orig);
                            m.SetTexture(IdBumpMap, mb != null ? mb : wantB);
                        }
                    }
                    Texture masked = WeatherCache.MaskWindows(dirt, orig);
                    if (masked != null) m.SetTexture(IdMainTex, masked);
                    if (StackHasColor(cstack)) ColorDone(m);
                }
            }
            else
            {
                string cstack = PrependPaint(bakeStack, z.colored, col, "whole", z.cmode);
                if (cstack != "" && m.HasProperty(IdMainTex))
                {
                    Texture orig = m.GetTexture(IdMainTex);
                    if (orig != null)
                    {
                        Texture ob = m.HasProperty(IdBumpMap) ? m.GetTexture(IdBumpMap) : null;
                        Texture wantB;
                        Texture want = WeatherCache.Baked(orig, ob, cstack, part, z.scale, out wantB);
                        if (want != null) m.SetTexture(IdMainTex, want);
                        if (wantB != null && m.HasProperty(IdBumpMap)) m.SetTexture(IdBumpMap, wantB);
                    }
                    if (StackHasColor(cstack)) ColorDone(m);
                }
            }
            return m;
        }
        internal static string HexRGB(Color c)
        {
            return string.Format("{0:X2}{1:X2}{2:X2}",
                Mathf.Clamp((int)(c.r * 255f), 0, 255),
                Mathf.Clamp((int)(c.g * 255f), 0, 255),
                Mathf.Clamp((int)(c.b * 255f), 0, 255));
        }

        static string ColorToken(int cmode) { return cmode == 1 ? "omul#" : "paint#"; }
        internal static int CurColorMode { get { return WeatherCache.ColorReplace ? 0 : 1; } }
        static bool StackHasColor(string s) { return s != null && (s.Contains("paint#") || s.Contains("omul#")); }

        static string PrependPaint(string weatherStack, bool colored, Color col, string region, int cmode)
        {
            string ws = weatherStack == null ? "" : weatherStack;
            if (!colored) return ws;
            string p = ColorToken(cmode) + HexRGB(col) + ":" + (string.IsNullOrEmpty(region) ? "whole" : region) + ":2";
            return ws == "" ? p : p + ";" + ws;
        }

        internal static string PlaceStack(string stack, string place)
        {
            if (string.IsNullOrEmpty(stack)) return "";
            string[] es = stack.Split(';');
            for (int i = 0; i < es.Length; i++)
            {
                string[] f = es[i].Split(':');
                if (f.Length >= 3) { f[1] = place; es[i] = string.Join(":", f); }
            }
            return string.Join(";", es);
        }

        static bool MaskUsable(float[] m)
        {
            if (m == null) return false;
            for (int i = 0; i < m.Length; i++) if (m[i] > 0.02f) return true;
            return false;
        }
        Material BuildSmoothFadeMaterial(Material baseMat, Zone z, float[] customMask)
        {
            Material m = new Material(baseMat); created.Add(m);
            if (IsGlass(m) && !isFairing) return m;
            FlattenRecolor(m);
            if (!z.finish && AutoStock(z.colored, z.cmode))
            {
                Shader stu = ActiveTuShader();
                if (stu != null && m.shader != stu) ApplyStockFinish(m, stu);
            }
            if (!m.HasProperty(IdMainTex)) return m;
            Texture orig = m.GetTexture(IdMainTex);
            if (orig == null) return m;
            string stack = "";
            if (z.colored) stack = ColorToken(z.cmode) + HexRGB(z.Col) + ":" + z.region + ":2";
            string dirt = PlaceStack(z.stack, z.region);
            if (!string.IsNullOrEmpty(dirt)) stack = (stack == "") ? dirt : stack + ";" + dirt;
            if (stack != "")
            {
                Texture ob = m.HasProperty(IdBumpMap) ? m.GetTexture(IdBumpMap) : null;
                Texture wantB;
                Texture want = WeatherCache.Baked(orig, ob, stack, part, z.scale, out wantB, customMask);
                Texture result = (want != null) ? want : orig;
                if (rdxKeepShiny && WeatherCache.HasShiny(orig))
                {
                    Texture masked = WeatherCache.MaskWindows(result, orig);
                    if (masked != null) result = masked;
                }
                m.SetTexture(IdMainTex, result);
                if (z.colored) ColorDone(m);
                if (wantB != null && m.HasProperty(IdBumpMap))
                {
                    if (rdxKeepShiny && WeatherCache.HasShiny(orig))
                    {
                        Texture mb = WeatherCache.MaskWindowsBump(wantB, ob, orig);
                        m.SetTexture(IdBumpMap, mb != null ? mb : wantB);
                    }
                    else m.SetTexture(IdBumpMap, wantB);
                }
            }

            if (z.finish)
            {
                Shader tu = ActiveTuShader();
                if (tu != null)
                {
                    ApplyFinish(m, tu, 1f, 1f);
                    Texture2D mm = WeatherCache.MetallicMask(part, z.region, z.metal, z.smooth, customMask);
                    if (mm != null && m.HasProperty(IdMetalGloss)) m.SetTexture(IdMetalGloss, mm);
                }
            }
            return m;
        }

        internal static bool IsPlaneTok(string t) { return t != null && t.StartsWith("pl|"); }

        static string F5(float f) { return f.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture); }

        internal static bool ParsePlane(string tok, out Vector3 n, out float d, out float fl, out bool useH, out Vector3 u, out float hd, out float hf)
        {
            n = Vector3.up; d = 0f; fl = 0f; useH = false; u = Vector3.up; hd = 0f; hf = 0f;
            if (!IsPlaneTok(tok)) return false;
            string[] f = tok.Substring(3).Split(',');
            if (f.Length < 5) return false;
            n = new Vector3(FP(f[0]), FP(f[1]), FP(f[2])); d = FP(f[3]); fl = FP(f[4]);
            if (f.Length >= 10) { useH = true; u = new Vector3(FP(f[5]), FP(f[6]), FP(f[7])); hd = FP(f[8]); hf = FP(f[9]); }
            return n.sqrMagnitude > 1e-6f;
        }

        static bool PlaneFaded(string tok)
        {
            Vector3 n, u; float d, fl, hd, hf; bool uh;
            return ParsePlane(tok, out n, out d, out fl, out uh, out u, out hd, out hf) && fl > 1e-4f;
        }

        internal static string PlaneToken(Transform t, Vector3 wdir, float cw, float fw, bool useH, Vector3 upW, float cwH, float fhW)
        {
            Vector3 n = t.InverseTransformVector(wdir).normalized;
            float d = Vector3.Dot(t.InverseTransformPoint(wdir * cw), n);
            float fl = Vector3.Dot(t.InverseTransformPoint(wdir * (cw + fw)), n) - d;
            string s = "pl|" + F5(n.x) + "," + F5(n.y) + "," + F5(n.z) + "," + F5(d) + "," + F5(fl);
            if (useH)
            {
                Vector3 u = t.InverseTransformVector(upW).normalized;
                float hd = Vector3.Dot(t.InverseTransformPoint(upW * cwH), u);
                float hf = hd - Vector3.Dot(t.InverseTransformPoint(upW * (cwH - fhW)), u);
                s += "," + F5(u.x) + "," + F5(u.y) + "," + F5(u.z) + "," + F5(hd) + "," + F5(hf);
            }
            return s;
        }

        internal static bool PlaneWorld(Transform t, string tok, out Vector3 wdir, out float cw, out float fw, out Vector3 upW, out float cwH, out bool useH, out float fhW)
        {
            wdir = Vector3.up; cw = 0f; fw = 0f; upW = t.up; cwH = 0f; fhW = 0f;
            Vector3 n, u; float d, fl, hd, hf;
            if (!ParsePlane(tok, out n, out d, out fl, out useH, out u, out hd, out hf)) return false;
            wdir = t.TransformVector(n).normalized;
            cw = Vector3.Dot(t.TransformPoint(n * d), wdir);
            fw = Mathf.Max(0f, Vector3.Dot(t.TransformPoint(n * (d + fl)), wdir) - cw);
            if (useH)
            {
                upW = t.TransformVector(u).normalized;
                cwH = Vector3.Dot(t.TransformPoint(u * hd), upW);
                fhW = Mathf.Max(1e-4f, cwH - Vector3.Dot(t.TransformPoint(u * (hd - hf)), upW));
            }
            return true;
        }

        internal static float PlaneWeight(Vector3 pl, Vector3 n, float d, float fl, bool useH, Vector3 u, float hd, float hf)
        {
            float c = Vector3.Dot(pl, n) - d;
            float w = (fl > 1e-5f) ? Mathf.Clamp01(c / fl) : (c >= 0f ? 1f : 0f);
            if (useH)
            {
                float hc = hd - Vector3.Dot(pl, u);
                w *= (hf > 1e-5f) ? Mathf.Clamp01(hc / hf) : (hc >= 0f ? 1f : 0f);
            }
            return w;
        }

        bool ZoneGeom(string region, List<Renderer> rs, out Vector3 wdir, out float cw,
            ref Vector3 upDir, out float cwHeight, out bool useHeight, out float fadeAxisW, out float fadeHeightW)
        {
            wdir = Vector3.up; cw = 0f; cwHeight = 0f; useHeight = false; fadeAxisW = 0f; fadeHeightW = 0f;
            if (IsPlaneTok(region))
            {
                Vector3 pu;
                if (!PlaneWorld(part.transform, region, out wdir, out cw, out fadeAxisW, out pu, out cwHeight, out useHeight, out fadeHeightW)) return false;
                if (useHeight) upDir = pu;
                return true;
            }
            Vector3 ldir; float lcut, lfade, lheight;
            if (!WeatherCache.RegionAxisDir(region, out ldir, out lcut, out lfade, out lheight)) return false;
            float rlo, rhi;
            wdir = part.transform.TransformDirection(ldir).normalized;
            RegionExtent(rs, wdir, out rlo, out rhi);
            cw = rlo + lcut * (rhi - rlo);
            fadeAxisW = lfade * Mathf.Max(1e-4f, rhi - cw);
            if (lheight < 0.999f)
            {
                float vlo, vhi;
                RegionExtent(rs, upDir, out vlo, out vhi);
                cwHeight = vlo + lheight * (vhi - vlo);
                useHeight = true;
                fadeHeightW = lfade * Mathf.Max(1e-4f, cwHeight - vlo);
            }
            return true;
        }

        class ZoneGeo { public bool ok, useH; public Vector3 wdir, up; public float cw, cwH, faW, fhW; }
        readonly Dictionary<string, ZoneGeo> zoneGeoCache = new Dictionary<string, ZoneGeo>();

        ZoneGeo ZoneGeoOf(string region, List<Renderer> rs)
        {
            ZoneGeo g;
            if (zoneGeoCache.TryGetValue(region, out g)) return g;
            g = new ZoneGeo();
            g.up = part.transform.up;
            g.ok = ZoneGeom(region, rs, out g.wdir, out g.cw, ref g.up, out g.cwH, out g.useH, out g.faW, out g.fhW);
            zoneGeoCache[region] = g;
            return g;
        }

        void RebuildMulti(List<Zone> extras)
        {
            zoneGeoCache.Clear();
            List<Renderer> rs = OwnRenderers();
            RestoreMeshesToOriginal();
            ResetForeignMeshes(rs);
            List<Zone> zones = new List<Zone>(extras);
            Zone act = ActiveZone();
            if (act.HasContent) { zones.RemoveAll(delegate(Zone zz) { return zz.region == act.region; }); zones.Add(act); }
            Zone whole = zones.Find(delegate(Zone z) { return z.region == "whole" || string.IsNullOrEmpty(z.region); });
            Zone baseZone = zones.Find(delegate(Zone z) { return z.region == "base"; });

            List<Material> prev = new List<Material>(created); created.Clear();
            for (int i = 0; i < rs.Count; i++)
            {
                Renderer r = rs[i];
                Material[] pris = EnsurePristine(r);
                if (pris.Length == 0) continue;
                Mesh curMesh = MeshOf(r);
                if (!origMesh.ContainsKey(r)) origMesh[r] = OriginalMesh(r, curMesh);
                Mesh src = origMesh[r];
                bool canSplit = curMesh != null && src != null && src.isReadable && pris.Length > 0 && src.subMeshCount == pris.Length && SkinOk(r, src);
                bool singleSub = canSplit && pris.Length == 1 && pris[0] != null;

                List<Zone> geo = zones.FindAll(delegate(Zone z) { return z.region != null && (z.region.StartsWith("g") || IsPlaneTok(z.region)); });
                List<Zone> pick = zones.FindAll(delegate(Zone z) { return z.region != null && z.region.StartsWith("pick|"); });

                bool hasPickHere = false;
                for (int j = 0; j < pris.Length && !hasPickHere; j++)
                {
                    string pk = "pick|" + RelPath(r.transform, part.transform) + "#" + j;
                    if (pick.Exists(delegate(Zone z) { return z.region == pk; })) hasPickHere = true;
                }

                if (geo.Count > 0 && canSplit && !hasPickHere)
                {
                    try
                    {
                        List<Vector3> V, N; List<Vector4> T; List<Vector2> U; List<int> tris;
                        MeshToLists(src, out V, out N, out T, out U, out tris);
                        List<BoneWeight> W = SkinWeights(src, r);

                        bool allFaded = singleSub && geo.Count > 0 && pick.Count == 0 && U != null && U.Count == V.Count;
                        for (int z = 0; z < geo.Count && allFaded; z++)
                        {
                            ZoneGeo zg = ZoneGeoOf(geo[z].region, rs);
                            if (!zg.ok || zg.faW <= 1e-4f) allFaded = false;
                        }
                        if (allFaded)
                        {
                            Material combined = (whole != null && whole.HasContent) ? BuildZoneMaterial(pris[0], whole) : pris[0];
                            string fmBase = "fm|" + src.GetInstanceID() + "|";
                            for (int z = 0; z < geo.Count; z++)
                            {
                                float[] cm;
                                if (!WeatherCache.TryFadeMask(fmBase + geo[z].region, out cm))
                                {
                                    ZoneGeo zg = ZoneGeoOf(geo[z].region, rs);
                                    if (!zg.ok) continue;
                                    Vector3 wdir = zg.wdir, zu = zg.up; float cw = zg.cw, cwH = zg.cwH, faW = zg.faW, fhW = zg.fhW; bool useH = zg.useH;
                                    List<int> zk = new List<int>(), zd = new List<int>();
                                    ClipSoup(V, N, T, U, W, r.transform, wdir, cw, tris, zk, zd);
                                    List<int> zreg = zk;
                                    if (useH) { List<int> k2 = new List<int>(), d2 = new List<int>(); ClipSoup(V, N, T, U, W, r.transform, -zu, -cwH, zk, k2, d2); zreg = k2; }
                                    if (zreg.Count == 0) continue;
                                    List<float> wv = new List<float>(V.Count);
                                    for (int v = 0; v < V.Count; v++)
                                    {
                                        Vector3 wp = r.transform.TransformPoint(V[v]);
                                        float ww = Mathf.Clamp01((Vector3.Dot(wp, wdir) - cw) / faW);
                                        if (useH) ww *= Mathf.Clamp01((cwH - Vector3.Dot(wp, zu)) / fhW);
                                        wv.Add(ww);
                                    }
                                    cm = WeatherCache.RasterMask(U, wv, zreg);
                                    WeatherCache.PutFadeMask(fmBase + geo[z].region, cm);
                                }
                                combined = BuildSmoothFadeMaterial(combined, geo[z], MaskUsable(cm) ? cm : null);
                            }
                            r.materials = new Material[] { combined };
                            continue;
                        }
                        List<List<int>> subTris = new List<List<int>>();
                        List<Material> subMats = new List<Material>();
                        geo.Reverse();
                        for (int j = 0; j < pris.Length; j++)
                        {
                            List<int> pool = (pris.Length == 1) ? tris : new List<int>(src.GetTriangles(j));
                            if (pris[j] == null) { if (pool.Count > 0) { subTris.Add(pool); subMats.Add(null); } continue; }
                            bool fadedHere = false;
                            for (int z = 0; z < geo.Count; z++)
                            {
                                ZoneGeo zg = ZoneGeoOf(geo[z].region, rs);
                                if (!zg.ok) continue;
                                Vector3 wdir = zg.wdir, upDir = zg.up; float cw = zg.cw, cwH = zg.cwH, fadeAxisW = zg.faW, fadeHeightW = zg.fhW; bool useH = zg.useH;
                                List<int> keep = new List<int>(), drop = new List<int>();
                                ClipSoup(V, N, T, U, W, r.transform, wdir, cw, pool, keep, drop);
                                List<int> painted = keep;
                                if (useH)
                                {
                                    List<int> k2 = new List<int>(), d2 = new List<int>();
                                    ClipSoup(V, N, T, U, W, r.transform, -upDir, -cwH, keep, k2, d2);
                                    painted = k2; drop.AddRange(d2);
                                }
                                pool = drop;
                                if (painted.Count == 0) continue;
                                Zone mz = MergeBase(whole, geo[z]);
                                if (fadeAxisW > 1e-4f && U != null && U.Count == V.Count)
                                {
                                    float[] cm;
                                    string fsKey = "fs|" + src.GetInstanceID() + "|" + geo[z].region + "#" + j;
                                    if (!WeatherCache.TryFadeMask(fsKey, out cm))
                                    {
                                        List<float> wv = new List<float>(V.Count);
                                        for (int v = 0; v < V.Count; v++)
                                        {
                                            Vector3 wp = r.transform.TransformPoint(V[v]);
                                            float ww = Mathf.Clamp01((Vector3.Dot(wp, wdir) - cw) / fadeAxisW);
                                            if (useH) ww *= Mathf.Clamp01((cwH - Vector3.Dot(wp, upDir)) / fadeHeightW);
                                            wv.Add(ww);
                                        }
                                        cm = WeatherCache.RasterMask(U, wv, painted);
                                        WeatherCache.PutFadeMask(fsKey, cm);
                                    }
                                    Material baseMat = (whole != null && whole.HasContent) ? BuildZoneMaterial(pris[j], whole) : pris[j];
                                    subTris.Add(painted); subMats.Add(BuildSmoothFadeMaterial(baseMat, geo[z], MaskUsable(cm) ? cm : null));
                                    fadedHere = true;
                                }
                                else { subTris.Add(painted); subMats.Add(BuildZoneMaterial(pris[j], mz)); }
                            }
                            if (pool.Count > 0)
                            {
                                Zone lz = (baseZone != null && baseZone.HasContent) ? MergeBase(whole, baseZone) : whole;
                                Material rest = (lz != null && lz.HasContent) ? BuildZoneMaterial(pris[j], lz)
                                              : (fadedHere ? BlitStock(pris[j]) : StockCopy(pris[j]));
                                subTris.Add(pool);
                                subMats.Add(rest);
                            }
                        }
                        if (subTris.Count == 0)
                        {
                            Material[] sc = new Material[pris.Length];
                            for (int j = 0; j < pris.Length; j++) sc[j] = (pris[j] != null) ? StockCopy(pris[j]) : null;
                            r.materials = sc;
                            continue;
                        }
                        Mesh nm = new Mesh();
                        if (V.Count > 65000) nm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                        nm.vertices = V.ToArray();
                        if (N != null) nm.normals = N.ToArray();
                        if (T != null) nm.tangents = T.ToArray();
                        if (U != null) nm.uv = U.ToArray();
                        if (W != null) { nm.boneWeights = W.ToArray(); nm.bindposes = src.bindposes; }
                        nm.subMeshCount = subTris.Count;
                        for (int k = 0; k < subTris.Count; k++) nm.SetTriangles(subTris[k], k);
                        nm.RecalculateBounds();
                        TrackSplit(r, nm);
                        r.materials = subMats.ToArray();
                    }
                    catch (Exception e) { Debug.LogWarning("[ReforgedRedux] multi-zone split failed: " + e.Message); }
                }
                else if (pick.Count > 0)
                {
                    Material[] work = new Material[pris.Length];
                    for (int j = 0; j < pris.Length; j++)
                    {
                        if (pris[j] == null) { work[j] = null; continue; }
                        string key = "pick|" + RelPath(r.transform, part.transform) + "#" + j;
                        Zone pz = pick.Find(delegate(Zone z) { return z.region == key; });
                        if (pz != null) work[j] = BuildZoneMaterial(pris[j], MergeBase(whole, pz));
                        else { Zone lz = (baseZone != null && baseZone.HasContent) ? MergeBase(whole, baseZone) : whole; work[j] = (lz != null && lz.HasContent) ? BuildZoneMaterial(pris[j], lz) : StockCopy(pris[j]); }
                    }
                    r.materials = work;
                }
                else
                {
                    Zone gz = null;
                    for (int z = geo.Count - 1; z >= 0 && gz == null; z--)
                        if (InZone(ZoneGeoOf(geo[z].region, rs), r.bounds.center)) gz = geo[z];
                    Zone lz = (gz != null) ? MergeBase(whole, gz) : ((baseZone != null && baseZone.HasContent) ? MergeBase(whole, baseZone) : whole);
                    Material[] work = new Material[pris.Length];
                    for (int j = 0; j < pris.Length; j++)
                    {
                        if (pris[j] == null) { work[j] = null; continue; }
                        work[j] = (lz != null && lz.HasContent) ? BuildZoneMaterial(pris[j], lz) : StockCopy(pris[j]);
                    }
                    r.materials = work;
                }
            }
            PadMaterials(rs);
            ReleaseMaterials(prev);
            if (isFairing) lastRenderCount = ModelRenderCount();
        }

        public void SetFinish(float metal, float smooth)
        {
            if (ActiveTuShader() == null)
            {
                ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgNoTU", "Reforged: TU/Metallic shader not found (TexturesUnlimited missing?)"),
                    5f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            rdxFinish = true; rdxMetal = metal; rdxSmooth = smooth;
            Rebuild();
        }
        public void SetColour(Color c)
        {
            rdxColored = true; rdxR = c.r; rdxG = c.g; rdxB = c.b; rdxColorMode = CurColorMode;
            Rebuild();
        }
        public void SetBoth(float metal, float smooth, Color c)
        {
            rdxFinish = true; rdxMetal = metal; rdxSmooth = smooth;
            rdxColored = true; rdxR = c.r; rdxG = c.g; rdxB = c.b; rdxColorMode = CurColorMode;
            Rebuild();
        }
        public void ClearFinish() { rdxFinish = false; Rebuild(); }

        static string StripHeat(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return "";
            List<string> keep = new List<string>();
            string[] es = stack.Split(';');
            for (int i = 0; i < es.Length; i++)
                if (es[i].Length > 0 && !es[i].StartsWith("heatd")) keep.Add(es[i]);
            return string.Join(";", keep.ToArray());
        }

        public void RemoveMetal()
        {
            rdxFinish = false; rdxColored = false;
            rdxStack = StripHeat(rdxStack);
            List<Zone> ex = ParseZones(rdxZones);
            for (int i = 0; i < ex.Count; i++) { ex[i].finish = false; ex[i].colored = false; ex[i].stack = StripHeat(ex[i].stack); }
            ex.RemoveAll(delegate(Zone zz) { return !zz.HasContent; });
            rdxZones = SerZones(ex);
            UpdateHeatBuff();
            Rebuild();
        }

        public void ClearColourAndTints()
        {
            rdxColored = false;
            if (WeatherOn)
            {
                List<string> keep = new List<string>();
                string[] es = rdxStack.Split(';');
                for (int i = 0; i < es.Length; i++)
                    if (!es[i].StartsWith("tint#") && !es[i].StartsWith("paint#") && !es[i].StartsWith("omul#")) keep.Add(es[i]);
                rdxStack = string.Join(";", keep.ToArray());
            }
            Rebuild();
        }

        public void AddWeather(int level, string tex, string place, float scale)
        {
            if (string.IsNullOrEmpty(tex)) tex = "soot";
            if (string.IsNullOrEmpty(place)) place = "whole";
            string prefix = tex + ":" + place + ":";
            string suffix = ScaleSuffix(tex, scale);
            List<string> es = new List<string>();
            if (WeatherOn) es.AddRange(rdxStack.Split(';'));
            for (int i = es.Count - 1; i >= 0; i--)
            {
                string[] pf = es[i].Split(':');
                if (pf.Length >= 2 && pf[1] != place) es.RemoveAt(i);
            }
            int same = 0;
            for (int i = 0; i < es.Count; i++) if (es[i].StartsWith(prefix)) same++;
            if (same >= 3)
            {
                for (int i = es.Count - 1; i >= 0; i--)
                    if (es[i].StartsWith(prefix)) { es[i] = prefix + level + suffix; break; }
            }
            else es.Add(prefix + level + suffix);
            while (es.Count > 8) es.RemoveAt(0);
            rdxStack = string.Join(";", es.ToArray());
            UpdateHeatBuff();
            Rebuild();
        }

        public void AppendStack(string add)
        {
            if (!string.IsNullOrEmpty(add))
                rdxStack = string.IsNullOrEmpty(rdxStack) ? add : (rdxStack + ";" + add);
            UpdateHeatBuff();
            Rebuild();
        }
        public void ClearAll()
        {
            rdxFinish = false; rdxColored = false; rdxStack = ""; rdxZones = "";
            UpdateHeatBuff();
            Rebuild();
        }

        List<Zone> AllZones()
        {
            List<Zone> zs = ParseZones(rdxZones);
            Zone a = ActiveZone();
            if (a.HasContent) { zs.RemoveAll(delegate(Zone z) { return z.region == a.region; }); zs.Add(a); }
            return zs;
        }

        public string ComboRecipe()
        {
            List<Zone> zs = AllZones();
            List<string> names = new List<string>();
            for (int zi = 0; zi < zs.Count; zi++)
            {
                if (string.IsNullOrEmpty(zs[zi].stack)) continue;
                string[] es = zs[zi].stack.Split(';');
                for (int ei = 0; ei < es.Length; ei++)
                {
                    int c = es[ei].IndexOf(':');
                    string nm = c > 0 ? es[ei].Substring(0, c) : es[ei];
                    if (string.IsNullOrEmpty(nm)) continue;
                    names.Add(nm);
                }
            }
            return string.Join(";", names.ToArray());
        }
        public bool ComboFinish(out float m, out float sm)
        {
            List<Zone> zs = AllZones();
            for (int i = 0; i < zs.Count; i++) if (zs[i].finish) { m = zs[i].metal; sm = zs[i].smooth; return true; }
            m = 0f; sm = 0f; return false;
        }
        public bool ComboColor(out float cr, out float cg, out float cb)
        {
            List<Zone> zs = AllZones();
            for (int i = 0; i < zs.Count; i++) if (zs[i].colored) { cr = zs[i].r; cg = zs[i].g; cb = zs[i].b; return true; }
            cr = 0f; cg = 0f; cb = 0f; return false;
        }

        public void ApplyComboLayers(string region, int level, string[] layers, bool hasColor, Color col, bool hasFinish, float metal, float smooth, float scale)
        {
            if (hasColor) { rdxColored = true; rdxR = col.r; rdxG = col.g; rdxB = col.b; rdxColorMode = CurColorMode; }
            if (layers != null)
                for (int i = 0; i < layers.Length; i++)
                {
                    if (string.IsNullOrEmpty(layers[i])) continue;
                    string entry = layers[i] + ":" + region + ":" + level + ScaleSuffix(layers[i], scale);
                    rdxStack = string.IsNullOrEmpty(rdxStack) ? entry : (rdxStack + ";" + entry);
                }
            if (hasFinish) { rdxFinish = true; rdxMetal = metal; rdxSmooth = smooth; }
            UpdateHeatBuff();
            Rebuild();
        }

        public void RestoreState(bool fin, float m, float s, bool col, float r, float g, float b, string stack,
            string zones, string region, int spot, float scale, bool keepShiny, int cmode, float pdet, bool tdet)
        {
            rdxFinish = fin; rdxMetal = m; rdxSmooth = s;
            rdxColored = col; rdxR = r; rdxG = g; rdxB = b;
            rdxStack = stack ?? "";
            rdxZones = zones ?? "";
            rdxRegion = string.IsNullOrEmpty(region) ? "whole" : region;
            rdxShineSpot = spot; rdxScale = scale;
            rdxKeepShiny = keepShiny;
            rdxColorMode = cmode;
            rdxPaintDetail = pdet;
            rdxTexDetail = tdet;
            UpdateHeatBuff();
            Rebuild();
        }

        void ForPainted(System.Action<Zone> act)
        {
            List<Zone> zs = ParseZones(rdxZones);
            Zone a = ActiveZone();
            if (a.HasContent) { zs.RemoveAll(delegate(Zone z) { return z.region == a.region; }); zs.Add(a); }
            if (zs.Count == 0) return;
            for (int i = 0; i < zs.Count; i++) act(zs[i]);
            Zone last = zs[zs.Count - 1]; zs.RemoveAt(zs.Count - 1);
            rdxZones = SerZones(zs); LoadZoneIntoFields(last);
            UpdateHeatBuff(); Rebuild();
        }
        public void PaintedFinish(float metal, float smooth, bool alsoCol, Color c)
        {
            ForPainted(delegate(Zone z)
            {
                if (metal < 0f) z.finish = false;
                else { z.finish = true; z.metal = metal; z.smooth = smooth; if (alsoCol) { z.colored = true; z.r = c.r; z.g = c.g; z.b = c.b; z.cmode = CurColorMode; } }
            });
        }
        public void PaintedColour(Color c) { ForPainted(delegate(Zone z) { z.colored = true; z.r = c.r; z.g = c.g; z.b = c.b; z.cmode = CurColorMode; }); }
        public void PaintedClearColour() { ForPainted(delegate(Zone z) { z.colored = false; }); }
        public void PaintedWeather(int level, string tex, float scale)
        {
            ForPainted(delegate(Zone z)
            {
                string entry = (string.IsNullOrEmpty(tex) ? "soot" : tex) + ":whole:" + level + ScaleSuffix(tex, scale);
                z.stack = string.IsNullOrEmpty(z.stack) ? entry : z.stack + ";" + entry;
            });
        }
        static bool HasTiles(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return false;
            string[] es = stack.Split(';');
            for (int i = 0; i < es.Length; i++)
                if (es[i].StartsWith("tiles:") || es[i].StartsWith("tilesw:")) return true;
            return false;
        }

        static bool InZone(ZoneGeo g, Vector3 p)
        {
            return g.ok && Vector3.Dot(p, g.wdir) >= g.cw && (!g.useH || Vector3.Dot(p, g.up) <= g.cwH);
        }

        float TileArea(List<Zone> tz)
        {
            List<Renderer> rs = OwnRenderers();
            bool whole = tz.Exists(delegate(Zone z) { return string.IsNullOrEmpty(z.region) || z.region == "whole" || z.region == "base"; });
            List<ZoneGeo> geo = new List<ZoneGeo>();
            zoneGeoCache.Clear();
            for (int i = 0; i < tz.Count; i++)
                if (tz[i].region != null && (tz[i].region.StartsWith("g") || IsPlaneTok(tz[i].region))) geo.Add(ZoneGeoOf(tz[i].region, rs));
            zoneGeoCache.Clear();
            float total = 0f;
            for (int i = 0; i < rs.Count; i++)
            {
                Renderer r = rs[i];
                Mesh om;
                Mesh m = (origMesh.TryGetValue(r, out om) && om != null) ? om : MeshOf(r);
                if (m == null || !m.isReadable) continue;
                Vector3[] vs = m.vertices;
                Transform t = r.transform;
                string rp = RelPath(r.transform, part.transform);
                for (int sub = 0; sub < m.subMeshCount; sub++)
                {
                    string pk = "pick|" + rp + "#" + sub;
                    bool all = whole || tz.Exists(delegate(Zone z) { return z.region == pk; });
                    if (!all && geo.Count == 0) continue;
                    int[] tri = m.GetTriangles(sub);
                    for (int k = 0; k + 2 < tri.Length; k += 3)
                    {
                        Vector3 a = t.TransformPoint(vs[tri[k]]), b = t.TransformPoint(vs[tri[k + 1]]), c = t.TransformPoint(vs[tri[k + 2]]);
                        float area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                        if (all) { total += area; continue; }
                        Vector3 cen = (a + b + c) / 3f;
                        for (int g = 0; g < geo.Count; g++)
                            if (InZone(geo[g], cen)) { total += area; break; }
                    }
                }
            }
            return total;
        }

        public void UpdateHeatBuff() { UpdateHeatBuff(true); }
        void UpdateHeatBuff(bool notify)
        {
            if (part == null) return;
            List<Zone> tz = (RealismOverhaul || excludedPart) ? new List<Zone>() : AllZones().FindAll(delegate(Zone z) { return HasTiles(z.stack); });
            bool heat = tz.Count > 0;
            SetHeatBuff(heat && HeatTileHeat);
            float mass = 0f;
            if (heat && HeatTileWeight)
            {
                try { mass = TileArea(tz) * TileKgPerM2 / 1000f; }
                catch { mass = 0f; }
            }
            BaseField tf = Fields["rdxTileMass"];
            if (tf != null) { tf.guiActive = mass > 0f; tf.guiActiveEditor = mass > 0f; }
            if (Mathf.Abs(mass - rdxTileMass) < 1e-6f) return;
            rdxTileMass = mass;
            if (!notify) { if (!HighLogic.LoadedSceneIsEditor) { try { part.UpdateMass(); } catch { } } return; }
            if (HighLogic.LoadedSceneIsEditor) massDirty = true;
            else part.UpdateMass();
        }

        void SetHeatBuff(bool on)
        {
            const double K = 2.5;
            if (on && !rdxHeatOn)
            {
                part.maxTemp *= K;
                part.skinMaxTemp *= K;
                rdxHeatMax = part.maxTemp;
                rdxHeatSkin = part.skinMaxTemp;
                rdxHeatOn = true;
            }
            else if (!on && rdxHeatOn)
            {
                if (Math.Abs(part.maxTemp - rdxHeatMax) < 1e-6) part.maxTemp = rdxHeatMax / K;
                if (Math.Abs(part.skinMaxTemp - rdxHeatSkin) < 1e-6) part.skinMaxTemp = rdxHeatSkin / K;
                rdxHeatOn = false;
            }
        }

        static bool massDirty;
        internal static void FlushMassChange()
        {
            if (!massDirty) return;
            massDirty = false;
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
        }

        internal static void UpdateAllHeat()
        {
            liveModules.RemoveWhere(delegate(ModuleReforged mm) { return mm == null; });
            List<ModuleReforged> ms = new List<ModuleReforged>(liveModules);
            for (int i = 0; i < ms.Count; i++)
                if (ms[i] != null && ms[i].part != null) ms[i].UpdateHeatBuff();
        }
    }

    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class ReforgedPainterEditor : ReforgedPainter { }
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ReforgedPainterFlight : ReforgedPainter { }

    public class ReforgedPainter : MonoBehaviour
    {
        static readonly string[] FinLabels = { "Matte", "Semi-Gloss", "Satin", "Metallic Paint", "Brushed Metal", "Chrome (Mirror)", "Light Chrome" };
        static readonly float[][] FinVals = {
            new float[] { 0.10f, 0.00f }, new float[] { 0.15f, 0.55f }, new float[] { 0.40f, 0.72f },
            new float[] { 0.55f, 0.90f }, new float[] { 0.85f, 0.65f }, new float[] { 0.90f, 1.00f },
            new float[] { 0.45f, 1.00f } };

        static readonly string[] MetalNames = { "Gold", "Silver", "Steel", "Bronze", "Brass", "Platinum" };

        static readonly string[] HeatFiles  = { "heatd1", "heatd2", "heatd3", "heatd4", "heatd5", "heatd6", "heatd7", "heatd8" };
        static readonly string[] HeatNames  = { "Temper", "Oxide Green", "Rose Heat", "Copper Fire", "Rainbow", "Overheated Blue", "Heat Mottle", "Ember" };
        const float HeatSmooth = 0.90f;
        static readonly float[][] MetalRGB = {
            new float[] { 212f, 175f, 55f }, new float[] { 183f, 183f, 183f }, new float[] { 128f, 128f, 128f },
            new float[] { 191f, 149f, 99f }, new float[] { 250f, 230f, 150f }, new float[] { 213f, 208f, 200f } };
        const float MetalSmooth = 176f / 255f;

        static readonly string[] DirtNames = { "Dusty", "Dirty", "Muddy", "Rusty", "Oily", "Scorched", "Burned", "Worn Metal" };
        static readonly float[][] DirtRGB = {
            new float[] { 209f, 199f, 179f }, new float[] { 148f, 128f, 102f },
            new float[] { 115f,  94f,  71f }, new float[] { 148f,  84f,  51f },
            new float[] {  89f,  87f,  82f }, new float[] {  77f,  66f,  59f },
            new float[] {  41f,  38f,  36f }, new float[] { 158f, 158f, 153f } };

        static readonly List<string> WeatherFiles = new List<string> { "soot", "burn", "scorch", "grime", "peel", "scratch", "grunge", "mli", "grain", "tiles",
                                                   "tilesw", "diamond", "brushed", "fabric",
                                                   "paving1", "paving2", "tiles138", "metal3", "plates6", "plates14", "ornament",
                                                   "tactile1", "tactile2", "walkway1", "walkway2", "chip", "chipc",
                                                   "concrete3", "leather", "carpet", "net", "styro", "paving3", "paving4", "ornament2",
                                                   "roadlines", "roadlines2", "roadlines3", "leak",
                                                   "blockwall", "facade", "cladding", "intiles", "planksb", "msheet", "carbon",
                                                   "heatd1", "heatd2", "heatd3", "heatd4", "heatd5", "heatd6", "heatd7", "heatd8",
                                                   "camo1", "camo10", "camo13", "camo26", "camo54", "camo22", "camo44", "camo55",
                                                   "ventsteel", "hessian",
                                                   "wornsteel", "metalgrid", "grimymetal" };
        static readonly List<string> WeatherLabels = new List<string> { "Soot smudge", "Burn", "Scorch", "Grime", "Rust", "Scratches", "Grunge", "MLI foil", "Grainy metal", "Heat tiles",
                                                   "Heat tiles W", "Diamond plate", "Brushed steel", "Blanket",
                                                   "Paving A", "Paving B", "Panel tiles", "Cast metal", "Plates B", "Thermal Foil", "Ornament",
                                                   "Tactile A", "Tactile B", "Walkway A", "Walkway B", "Motherboard", "Motherboard (colour)",
                                                   "Concrete C", "Leather", "Carpet", "Net", "Styrofoam", "Paving C", "Paving D", "Ornament B",
                                                   "Yellow Lines", "Yellow Lines B", "Yellow Lines C", "Paint Peel",
                                                   "Block Wall", "Tile Facade", "Cladding", "Interior Tiles", "Planks B", "Foil Sheet", "Carbon Fiber",
                                                   "Temper", "Oxide Green", "Rose Heat", "Copper Fire", "Rainbow", "Overheated Blue", "Heat Mottle", "Ember",
                                                   "Camo Tiger Olive", "Camo Naval Green", "Camo Forest Fleck", "Camo Urban Red", "Camo Tiger Grey", "Camo Winter Blue", "Camo Desert Digital", "Camo Woodland DPM",
                                                   "Vented Steel", "Hessian",
                                                   "Worn Steel", "Metal Grid", "Grimy Metal" };

        static bool customScanned;
        void EnsureCustomScan()
        {
            if (customScanned || GameDatabase.Instance == null) return;
            customScanned = true;
            try
            {
                const string pfx = "ReforgedRedux/Weather/";
                var all = GameDatabase.Instance.databaseTexture;
                for (int i = 0; i < all.Count; i++)
                {
                    string url = all[i].name;
                    if (url == null || !url.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)) continue;
                    string bn = url.Substring(pfx.Length);
                    if (bn.Length == 0 || bn.IndexOf('/') >= 0) continue;
                    string low = bn.ToLowerInvariant();
                    if (low.EndsWith("_n") || low.EndsWith("_b")) continue;
                    bool known = false;
                    for (int k = 0; k < WeatherFiles.Count; k++)
                        if (string.Equals(WeatherFiles[k], bn, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                    if (known) continue;
                    WeatherFiles.Add(bn);
                    WeatherLabels.Add(PrettyLabel(bn));
                }
            }
            catch (Exception e) { Debug.LogWarning("[ReforgedRedux] custom weather scan failed: " + e.Message); }
        }

        static string PrettyLabel(string fn)
        {
            char[] c = fn.Replace('_', ' ').ToCharArray();
            bool up = true;
            for (int i = 0; i < c.Length; i++) { if (up && c[i] != ' ') c[i] = char.ToUpperInvariant(c[i]); up = (c[i] == ' '); }
            return new string(c);
        }

        static readonly List<string> palNames = new List<string>();
        static readonly List<Color> palCols = new List<Color>();
        static bool palLoaded;

        class Preset { public string name; public bool isColor; public float metal, smooth, r, g, b; }
        class Combo
        {
            public string name; public string stack = "";
            public string recipe = "";
            public bool hasFinish; public float metal, smooth;
            public bool hasColor; public float r, g, b;
            public float scale = 1f;
        }
        static readonly List<Preset> userFinish = new List<Preset>();
        static readonly List<Preset> userColor = new List<Preset>();
        static readonly List<Combo> userCombo = new List<Combo>();
        static readonly string[] DefaultPins = { "camo1", "leak", "msheet", "carbon", "grimymetal", "tiles", "grain", "metalgrid" };
        static readonly List<string> pinned = new List<string>(DefaultPins);
        static bool presetsLoaded;

        string brushLabel = "(none yet)";
        int brushKind;
        float brMetal, brSmooth;
        Color brColor = Color.white;
        bool brAlsoColor;
        int brLevel = 1; string brTex = "soot"; string brPlace = "whole";
        float brScale = 1f;
        Combo brCombo; string brComboStack;
        bool brForceNew;

        ApplicationLauncherButton button;
        bool show;
        int tab;
        int mode = 1;
        bool includeChildren;
        static bool paintSym = true;
        Part lastPaintedPart;
        Rect win = new Rect(300f, 100f, 360f, 520f);
        bool resizing;
        bool svDrag, hueDrag;
        string detailBuf;
        Vector2 dirtScroll, colScroll, tabScroll;
        float colContentH, dirtContentH;

        bool regWhole = true;
        int regAxis = 1;
        int regShineSpot = 0;
        bool coversOpen;
        float regCut = 0.4f;
        bool regCraft;
        float regHeight = 1f;
        float regFade = 0f;
        float regStrength = 1f;
        float regPatScale = 1f;
        string dirtSearch = "";
        string regCutBuf, regHeightBuf, regFadeBuf, regStrBuf, regScaleBuf;
        int heatVariant;
        bool showOverlay = true;
        static bool showHints = true;
        static float savedWinW = 360f, savedWinH = 520f;
        static float uiScale = 1f;
        float scaleReq;
        GUIStyle tinyBtn;
        bool settingsOpen;
        Rect setWin = new Rect(0f, 0f, 320f, 10f);
        int capturing = -1;
        bool diagPick;
        KeyBind[] draftBinds;
        bool draftGradual, draftEven, draftWeight, draftHeat, draftLowVram;
        int draftCacheLimit;
        Vector2 setScroll;
        float setContentH;
        long cacheBytes = -1;
        float cacheBytesAt;
        static readonly int[] CacheLimits = { 0, 500, 1024, 2048, 5120 };

        static string SizeText(long bytes)
        {
            double mb = bytes / 1048576.0;
            if (mb >= 1024.0) return (mb / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB";
            return mb.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }

        static string LimitText(int mb)
        {
            if (mb <= 0) return Loc.T("#RDX_NoLimit", "No limit");
            return (mb >= 1024) ? (mb / 1024) + " GB" : mb + " MB";
        }

        internal static void EnsureSettings()
        {
            if (!presetsLoaded) { LoadPresets(); presetsLoaded = true; }
        }
        bool capLocked;
        const string CapLockId = "ReforgedReduxKeyCapture";

        class KeyBind
        {
            public string id; public KeyCode key, defKey; public bool ctrl, shift, alt;
            public KeyBind(string id, KeyCode def) { this.id = id; key = def; defKey = def; }
        }
        static readonly KeyBind[] binds = {
            new KeyBind("paint", KeyCode.P),
            new KeyBind("undo", KeyCode.None),
            new KeyBind("redo", KeyCode.None),
            new KeyBind("resetBrush", KeyCode.None),
            new KeyBind("toggleMenu", KeyCode.None),
            new KeyBind("pick", KeyCode.Mouse2),
            new KeyBind("pin", KeyCode.Mouse1)
        };
        static KeyBind Bind(string id) { for (int i = 0; i < binds.Length; i++) if (binds[i].id == id) return binds[i]; return null; }

        static string BindLabel(string id)
        {
            switch (id)
            {
                case "paint": return Loc.T("#RDX_KeyPaint", "Paint");
                case "undo": return Loc.T("#RDX_KeyUndo", "Undo");
                case "redo": return Loc.T("#RDX_KeyRedo", "Redo");
                case "resetBrush": return Loc.T("#RDX_KeyResetBrush", "Reset brush to stock");
                case "pick": return Loc.T("#RDX_KeyPick", "Pick piece");
                case "pin": return Loc.T("#RDX_KeyPin", "Pin preview");
                default: return Loc.T("#RDX_KeyToggleMenu", "Show / hide menu");
            }
        }

        static string KeyName(KeyCode k)
        {
            if (k == KeyCode.Mouse1) return Loc.T("#RDX_MouseRight", "Right mouse");
            if (k == KeyCode.Mouse2) return Loc.T("#RDX_MouseMiddle", "Middle mouse");
            if (k >= KeyCode.Mouse3 && k <= KeyCode.Mouse6) return Loc.T("#RDX_MouseN", "Mouse <<1>>", ((int)k - (int)KeyCode.Mouse0 + 1).ToString());
            return k.ToString();
        }

        static string BindName(string id)
        {
            KeyBind b = Bind(id);
            return (b != null) ? BindText(b) : "";
        }

        static string BindText(KeyBind b)
        {
            if (b.key == KeyCode.None) return Loc.T("#RDX_KeyNone", "None");
            string t = KeyName(b.key);
            if (b.alt) t = "Alt+" + t;
            if (b.shift) t = "Shift+" + t;
            if (b.ctrl) t = "Ctrl+" + t;
            return t;
        }

        static string BindSave(KeyBind b)
        {
            return (b.ctrl ? "Ctrl+" : "") + (b.shift ? "Shift+" : "") + (b.alt ? "Alt+" : "") + b.key.ToString();
        }

        static void BindLoad(KeyBind b, string v)
        {
            if (string.IsNullOrEmpty(v)) return;
            string[] ps = v.Split('+');
            bool c = false, sh = false, a = false;
            for (int i = 0; i < ps.Length - 1; i++)
            {
                if (ps[i] == "Ctrl") c = true; else if (ps[i] == "Shift") sh = true; else if (ps[i] == "Alt") a = true;
            }
            try
            {
                KeyCode k = (KeyCode)Enum.Parse(typeof(KeyCode), ps[ps.Length - 1]);
                b.key = k; b.ctrl = c; b.shift = sh; b.alt = a;
            }
            catch { }
        }

        static bool BindDown(string id)
        {
            KeyBind b = Bind(id);
            if (b == null || b.key == KeyCode.None || !Input.GetKeyDown(b.key)) return false;
            bool c = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool sh = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool a = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (b.ctrl || b.shift || b.alt) return c == b.ctrl && sh == b.shift && a == b.alt;
            return !c && !a;
        }

        static bool IsModifierKey(KeyCode k)
        {
            return k == KeyCode.LeftShift || k == KeyCode.RightShift || k == KeyCode.LeftControl || k == KeyCode.RightControl
                || k == KeyCode.LeftAlt || k == KeyCode.RightAlt || k == KeyCode.LeftCommand || k == KeyCode.RightCommand
                || k == KeyCode.AltGr || k == KeyCode.None;
        }

        void SyncCaptureLock()
        {
            bool want = show && capturing >= 0;
            if (want && !capLocked) { InputLockManager.SetControlLock(ControlTypes.All, CapLockId); capLocked = true; }
            else if (!want && capLocked) { InputLockManager.RemoveControlLock(CapLockId); capLocked = false; }
        }

        bool MouseOverMenus()
        {
            Vector2 mp = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / uiScale;
            return win.Contains(mp) || (settingsOpen && setWin.Contains(mp));
        }

        static Part PartUnderMouse()
        {
            Part p = Mouse.HoveredPart;
            if (p == null)
            {
                Camera cam = Camera.main;
                RaycastHit hit;
                if (cam != null && Physics.Raycast(cam.ScreenPointToRay(Input.mousePosition), out hit, 10000f))
                    p = hit.collider.GetComponentInParent<Part>();
            }
            return p;
        }

        static string PartTitle(string partName)
        {
            AvailablePart ap = PartLoader.getPartInfoByName(partName);
            return (ap != null && !string.IsNullOrEmpty(ap.title)) ? ap.title : partName;
        }

        void IgnorePartUnderMouse()
        {
            Part p = PartUnderMouse();
            if (p == null || p.partInfo == null)
            {
                ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgNoPartHit", "No part there. Try again."), 3f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            string pn = p.partInfo.name;
            ModuleReforged.IgnoredParts.Add(pn);
            SavePresets();
            ModuleReforged.RefreshIgnored(pn);
            ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgIgnored", "Ignored: <<1>>. Reforged won't paint this part type.", PartTitle(pn)), 4f, ScreenMessageStyle.UPPER_CENTER);
        }

        void ReportPartUnderMouse()
        {
            Part p = Mouse.HoveredPart;
            if (p == null)
            {
                Camera cam = Camera.main;
                RaycastHit hit;
                if (cam != null && Physics.Raycast(cam.ScreenPointToRay(Input.mousePosition), out hit, 10000f))
                    p = hit.collider.GetComponentInParent<Part>();
            }
            if (p == null)
            {
                ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgNoPartHit", "No part there. Try again."), 3f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            string title = (p.partInfo != null) ? p.partInfo.title : p.name;
            ModuleReforged m = p.Modules.GetModule<ModuleReforged>();
            if (m != null) m.LogDiag();
            else Debug.Log("[ReforgedRedux] part report: " + ((p.partInfo != null) ? p.partInfo.name : p.name) + " (" + title + ") has no Reforged module");
            ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgReported", "Part report written to KSP.log: <<1>>", title), 4f, ScreenMessageStyle.UPPER_CENTER);
        }

        void SetMenuShown(bool v)
        {
            show = v;
            if (!v) { settingsOpen = false; capturing = -1; diagPick = false; ignorePick = false; draftBinds = null; }
            if (button != null) { if (v) button.SetTrue(false); else button.SetFalse(false); }
        }

        bool TypingInMenu()
        {
            if (GUIUtility.keyboardControl == 0) return false;
            Vector2 mp = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / uiScale;
            return win.Contains(mp) || (settingsOpen && setWin.Contains(mp));
        }

        void OpenSettingsDraft()
        {
            draftBinds = new KeyBind[binds.Length];
            for (int i = 0; i < binds.Length; i++)
            {
                KeyBind d = new KeyBind(binds[i].id, binds[i].defKey);
                d.key = binds[i].key; d.ctrl = binds[i].ctrl; d.shift = binds[i].shift; d.alt = binds[i].alt;
                draftBinds[i] = d;
            }
            draftGradual = ModuleReforged.DeferLoadBakes;
            draftEven = ModuleReforged.EvenShine;
            draftWeight = ModuleReforged.HeatTileWeight;
            draftHeat = ModuleReforged.HeatTileHeat;
            draftLowVram = WeatherCache.LowVram;
            draftCacheLimit = WeatherCache.CacheLimitMB;
            cacheBytes = -1;
        }

        bool SettingsChanged()
        {
            if (draftBinds == null) return false;
            for (int i = 0; i < binds.Length; i++)
            {
                KeyBind a = binds[i], d = draftBinds[i];
                if (a.key != d.key || a.ctrl != d.ctrl || a.shift != d.shift || a.alt != d.alt) return true;
            }
            return draftGradual != ModuleReforged.DeferLoadBakes || draftEven != ModuleReforged.EvenShine || draftWeight != ModuleReforged.HeatTileWeight
                || draftHeat != ModuleReforged.HeatTileHeat || draftLowVram != WeatherCache.LowVram || draftCacheLimit != WeatherCache.CacheLimitMB;
        }

        void SaveSettings()
        {
            for (int i = 0; i < binds.Length; i++)
            {
                binds[i].key = draftBinds[i].key; binds[i].ctrl = draftBinds[i].ctrl;
                binds[i].shift = draftBinds[i].shift; binds[i].alt = draftBinds[i].alt;
            }
            ModuleReforged.DeferLoadBakes = draftGradual;
            bool shineChanged = draftEven != ModuleReforged.EvenShine;
            ModuleReforged.EvenShine = draftEven;
            bool weightChanged = draftWeight != ModuleReforged.HeatTileWeight || draftHeat != ModuleReforged.HeatTileHeat;
            ModuleReforged.HeatTileWeight = draftWeight;
            ModuleReforged.HeatTileHeat = draftHeat;
            bool vramChanged = draftLowVram != WeatherCache.LowVram;
            WeatherCache.LowVram = draftLowVram;
            bool limitChanged = draftCacheLimit != WeatherCache.CacheLimitMB;
            WeatherCache.CacheLimitMB = draftCacheLimit;
            capturing = -1;
            SavePresets();
            if (shineChanged || vramChanged) ModuleReforged.RebuildAllPainted();
            if (weightChanged) ModuleReforged.UpdateAllHeat();
            if (limitChanged) { WeatherCache.TrimDiskCache(); cacheBytes = -1; }
            ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgSettingsSaved", "Settings saved."), 2f, ScreenMessageStyle.UPPER_CENTER);
        }

        void CloseSettings() { settingsOpen = false; capturing = -1; diagPick = false; ignorePick = false; draftBinds = null; }

        void DrawSettings(int id)
        {
            if (draftBinds == null) OpenSettingsDraft();
            GUILayout.BeginVertical();
            float maxH = Mathf.Max(240f, Screen.height / uiScale - 140f);
            bool scroll = setContentH > maxH;
            if (scroll) setScroll = GUILayout.BeginScrollView(setScroll, GUILayout.Height(maxH));
            GUILayout.BeginVertical();
            Header(Loc.T("#RDX_HdrKeybinds", "Keybinds"));
            for (int i = 0; i < draftBinds.Length; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(BindLabel(draftBinds[i].id), builderLabel, GUILayout.Width(150f));
                string txt = (capturing == i) ? Loc.T("#RDX_KeyPress", "Press a key or mouse button...") : BindText(draftBinds[i]);
                if (GUILayout.Button(txt, GUILayout.Height(22f))) capturing = (capturing == i) ? -1 : i;
                GUILayout.EndHorizontal();
            }
            Hint(Loc.T("#RDX_HintKeybinds", "Click a bind, then press a key or mouse button.\nEsc cancels, Backspace clears it."));
            GUILayout.Space(4f);
            Header(Loc.T("#RDX_HdrPaint", "Paint"));
            draftEven = GUILayout.Toggle(draftEven, Loc.T("#RDX_EvenShine", "Even paint shine"));
            Hint(Loc.T("#RDX_HintEvenShine", "On: painted parts get one stock-like finish, so they all match.\nOff: each part keeps its own stock shine."));
            GUILayout.Space(4f);
            Header(Loc.T("#RDX_HdrGameplay", "Gameplay"));
            if (ModuleReforged.RealismOverhaul)
            {
                GUI.enabled = false;
                GUILayout.Toggle(false, Loc.T("#RDX_TileWeight", "Heat tiles add weight"));
                GUILayout.Toggle(false, Loc.T("#RDX_TileHeat", "Heat tiles add heat tolerance"));
                GUI.enabled = true;
                GUILayout.Label(Loc.T("#RDX_HintRO", "Realism Overhaul found: heat tiles are looks only.\nThey don't change heat tolerance or weight."), hintStyle);
            }
            else
            {
                draftWeight = GUILayout.Toggle(draftWeight, Loc.T("#RDX_TileWeight", "Heat tiles add weight"));
                draftHeat = GUILayout.Toggle(draftHeat, Loc.T("#RDX_TileHeat", "Heat tiles add heat tolerance"));
                Hint(Loc.T("#RDX_HintTileWeight", "Weight: about 8 kg per square metre of tiles.\nHeat tolerance: the part can take 2.5x more heat."));
            }
            GUILayout.Space(4f);
            Header(Loc.T("#RDX_HdrPerformance", "Performance"));
            draftGradual = GUILayout.Toggle(draftGradual, Loc.T("#RDX_GradualLoad", "Load painted crafts gradually"));
            Hint(Loc.T("#RDX_HintGradualLoad", "Off: a short pause, then the craft appears fully painted.\nOn: the craft appears right away and the paint fills in."));
            draftLowVram = GUILayout.Toggle(draftLowVram, Loc.T("#RDX_LowVram", "Low VRAM mode"));
            Hint(Loc.T("#RDX_HintLowVram", "Paint uses about 75% less graphics memory and\npaints faster, but looks softer up close."));
            GUILayout.Space(4f);
            Header(Loc.T("#RDX_HdrTrouble", "Troubleshooting"));
            if (GUILayout.Button(diagPick ? Loc.T("#RDX_MsgClickPart", "Point at the part to report and press <<1>>.", BindName("pick")) : Loc.T("#RDX_ReportPart", "Report a part"), GUILayout.Height(22f)))
            {
                diagPick = !diagPick;
                ignorePick = false;
                if (diagPick) ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgClickPart", "Point at the part to report and press <<1>>.", BindName("pick")), 3f, ScreenMessageStyle.UPPER_CENTER);
            }
            Hint(Loc.T("#RDX_HintTrouble", "For a part that paints wrongly. A report is\nwritten to KSP.log for support.", BindName("pick")));
            if (GUILayout.Button(ignorePick ? Loc.T("#RDX_MsgIgnorePick", "Point at the part to ignore and press <<1>>.", BindName("pick")) : Loc.T("#RDX_IgnorePart", "Ignore a part"), GUILayout.Height(22f)))
            {
                ignorePick = !ignorePick;
                diagPick = false;
                if (ignorePick) ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgIgnorePick", "Point at the part to ignore and press <<1>>.", BindName("pick")), 3f, ScreenMessageStyle.UPPER_CENTER);
            }
            Hint(Loc.T("#RDX_HintIgnore", "Reforged stops painting that part type completely.\nHandy if a part from another mod looks wrong."));
            if (ModuleReforged.IgnoredParts.Count > 0)
            {
                GUILayout.Label(Loc.T("#RDX_IgnoredParts", "Ignored parts:"), builderLabel);
                List<string> ign = new List<string>(ModuleReforged.IgnoredParts);
                ign.Sort();
                string drop = null;
                for (int i = 0; i < ign.Count; i++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(PartTitle(ign[i]), builderLabel);
                    if (GUILayout.Button("x", GUILayout.Width(24f), GUILayout.Height(20f))) drop = ign[i];
                    GUILayout.EndHorizontal();
                }
                Hint(Loc.T("#RDX_HintIgnoredList", "Press x to let that part be painted again."));
                if (drop != null)
                {
                    ModuleReforged.IgnoredParts.Remove(drop);
                    SavePresets();
                    ModuleReforged.RefreshIgnored(drop);
                    ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgUnignored", "<<1>> can be painted again.", PartTitle(drop)), 3f, ScreenMessageStyle.UPPER_CENTER);
                }
            }
            if (cacheBytes < 0 || Time.realtimeSinceStartup >= cacheBytesAt)
            {
                cacheBytes = WeatherCache.DiskCacheBytes();
                cacheBytesAt = Time.realtimeSinceStartup + 5f;
            }
            GUILayout.Label(Loc.T("#RDX_CacheSize", "Paint cache: <<1>>", SizeText(cacheBytes)), builderLabel);
            if (GUILayout.Button(Loc.T("#RDX_ClearCache", "Clear paint cache"), GUILayout.Height(22f)))
            {
                WeatherCache.ClearDiskCache();
                cacheBytes = -1;
                ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgCacheCleared", "Paint cache cleared."), 2f, ScreenMessageStyle.UPPER_CENTER);
            }
            Hint(Loc.T("#RDX_HintCache", "Paint is saved so crafts load faster next time.\nClear it if a painted part ever looks wrong."));
            if (GUILayout.Button(Loc.T("#RDX_CacheLimit", "Cache limit: <<1>>", LimitText(draftCacheLimit)), GUILayout.Height(22f)))
            {
                int li = Array.IndexOf(CacheLimits, draftCacheLimit);
                draftCacheLimit = CacheLimits[(li + 1) % CacheLimits.Length];
            }
            Hint(Loc.T("#RDX_HintCacheLimit", "When the cache gets bigger than this, the oldest\npaint is removed. It is rebuilt when needed."));
            GUILayout.Space(6f);
            if (GUILayout.Button(Loc.T("#RDX_ResetDefaults", "Reset to defaults"), GUILayout.Height(22f)))
            {
                for (int i = 0; i < draftBinds.Length; i++) { draftBinds[i].key = draftBinds[i].defKey; draftBinds[i].ctrl = draftBinds[i].shift = draftBinds[i].alt = false; }
                draftGradual = false;
                draftEven = true;
                draftWeight = true;
                draftHeat = true;
                draftLowVram = false;
                draftCacheLimit = 0;
                capturing = -1;
            }
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint) setContentH = GUILayoutUtility.GetLastRect().height;
            if (scroll) GUILayout.EndScrollView();
            bool changed = SettingsChanged();
            GUILayout.BeginHorizontal();
            GUI.enabled = changed;
            if (GUILayout.Button(Loc.T("#RDX_SaveChanges", "Save changes"), GUILayout.Height(22f))) SaveSettings();
            GUI.enabled = true;
            if (GUILayout.Button(changed ? Loc.T("#RDX_Cancel", "Cancel") : Loc.T("#RDX_Close", "Close"), GUILayout.Height(22f))) CloseSettings();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUI.DragWindow();
        }
        static bool settingsDirty;
        Texture2D brIcon; Color brIconTint = Color.white;
        string comboName = "My combo";
        string newName = "My preset";
        bool buildingFinish, buildingColor;
        float bMetal, bSmooth;
        float pH, pS, pV = 1f, pBright = 1f;
        string bufMetal, bufShine, bufR, bufG, bufB, bufBright, hexBuf;
        Texture2D svTex, hueTex, markTex;
        int svHueKey = -1;
        bool eyedrop;
        bool ignorePick;
        bool meshPick;
        string pickId;
        Part pickPart;
        bool overlayApplied, ovApplied;
        Part lastHovered;
        GUIStyle headerStyle, hintStyle, builderLabel, toggleBold, brushStyle, pressStyle;

        class UndoEntry
        {
            public Part part;
            public bool fin; public float m, s;
            public bool col; public float r, g, b;
            public string stack;
            public string zones, region; public int spot; public float scale;
            public bool keepShiny;
            public int cmode;
            public float pdet;
            public bool tdet;
        }
        readonly List<List<UndoEntry>> undoStack = new List<List<UndoEntry>>();
        readonly List<List<UndoEntry>> redoStack = new List<List<UndoEntry>>();
        const int UndoCap = 12;

        public const string Version = "1.1.2";
        static bool versionLogged;

        void Start()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            if (ApplicationLauncher.Ready) AddButton();
            if (!palLoaded) { LoadPalette(); palLoaded = true; }
            if (!presetsLoaded) { LoadPresets(); presetsLoaded = true; }
            if (!versionLogged) { versionLogged = true; Debug.Log("[ReforgedRedux] Reforged Materials Redux v" + Version + " loaded"); }
            win.width = Mathf.Clamp(savedWinW, 300f, 700f);
            win.height = Mathf.Clamp(savedWinH, 220f, Screen.height / uiScale);
            brushLabel = Loc.T("#RDX_LblNone", "(none yet)");
            newName = Loc.T("#RDX_DefPreset", "My preset");
            comboName = Loc.T("#RDX_DefCombo", "My combo");
        }

        void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            if (button != null && ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(button);
            InputLockManager.RemoveControlLock(LockId);
            InputLockManager.RemoveControlLock(CapLockId);
            ClearOverlay();
        }

        void AddButton()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;
            Texture2D icon = (GameDatabase.Instance != null)
                ? GameDatabase.Instance.GetTexture("ReforgedRedux/icon", false) : null;
            if (icon == null) { icon = new Texture2D(38, 38); }
            button = ApplicationLauncher.Instance.AddModApplication(
                delegate { show = true; },
                delegate { show = false; settingsOpen = false; capturing = -1; diagPick = false; ignorePick = false; draftBinds = null; },
                null, null, null, null,
                ApplicationLauncher.AppScenes.SPH | ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.FLIGHT,
                icon);
        }

        static void LoadPalette()
        {
            palNames.Clear(); palCols.Clear();
            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("REDUX_COLOR");
            for (int i = 0; i < nodes.Length; i++)
            {
                string nm = nodes[i].GetValue("name");
                string cv = nodes[i].GetValue("color");
                if (string.IsNullOrEmpty(nm) || string.IsNullOrEmpty(cv)) continue;
                string[] p = cv.Split(',');
                if (p.Length < 3) continue;
                float r, g, b;
                if (!float.TryParse(p[0], out r) || !float.TryParse(p[1], out g) || !float.TryParse(p[2], out b)) continue;
                palNames.Add(nm);
                palCols.Add(new Color(r / 255f, g / 255f, b / 255f));
            }
            if (palNames.Count == 0)
            {
                string[] fn = { "White", "Grey", "Black", "Red", "Orange", "Yellow", "Green", "Blue" };
                float[][] fc = { new float[]{245,245,245}, new float[]{140,140,140}, new float[]{25,25,25},
                                 new float[]{205,35,35}, new float[]{235,130,30}, new float[]{240,205,50},
                                 new float[]{55,150,65}, new float[]{45,105,210} };
                for (int i = 0; i < fn.Length; i++)
                {
                    palNames.Add(fn[i]);
                    palCols.Add(new Color(fc[i][0] / 255f, fc[i][1] / 255f, fc[i][2] / 255f));
                }
            }
            Debug.Log("[ReforgedRedux] palette: " + palNames.Count + " colours");
        }

        void Update()
        {
            if (capturing >= 0 && draftBinds != null && capturing < draftBinds.Length)
            {
                for (int mb = 1; mb <= 6; mb++)
                {
                    KeyCode mk = (KeyCode)((int)KeyCode.Mouse0 + mb);
                    if (!Input.GetKeyDown(mk)) continue;
                    KeyBind cb = draftBinds[capturing];
                    cb.key = mk;
                    cb.ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                    cb.shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    cb.alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                    capturing = -1;
                    return;
                }
            }
            if (Mouse.HoveredPart != null) lastHovered = Mouse.HoveredPart;
            if (colPrevPending || colPrevWasDrag) FlushColourPreview();
            if (settingsDirty && !Input.GetMouseButton(0)) { settingsDirty = false; SavePresets(); }

            if (show && !regWhole && capturing < 0 && BindDown("pin") && !MouseOverMenus())
            {
                Part hp = Mouse.HoveredPart;
                if (hp != null) ovLocked = (ovLocked == hp) ? null : hp;
            }
            if (regWhole) ovLocked = null;
            DrawRegionOverlay();
            if ((diagPick || ignorePick) && capturing < 0 && BindDown("pick") && !MouseOverMenus())
            {
                bool ig = ignorePick;
                diagPick = false; ignorePick = false;
                if (ig) IgnorePartUnderMouse(); else ReportPartUnderMouse();
                return;
            }
            if (eyedrop && Input.GetMouseButtonDown(0))
            {
                eyedrop = false;
                StartCoroutine(SamplePixel(Input.mousePosition));
                return;
            }
            if (meshPick && capturing < 0 && BindDown("pick") && !MouseOverMenus())
            {
                meshPick = false;
                DoMeshPick(Input.mousePosition);
                return;
            }
            bool hotkeys = capturing < 0 && !TypingInMenu();
            if (hotkeys && BindDown("toggleMenu")) { SetMenuShown(!show); return; }
            if (show && hotkeys && BindDown("undo")) { UndoLast(); return; }
            if (show && hotkeys && BindDown("redo")) { RedoLast(); return; }
            if (show && hotkeys && BindDown("resetBrush")) { brushKind = 6; brushLabel = Loc.T("#RDX_LblResetStock", "Reset to stock"); brIcon = null; return; }
            if (show && hotkeys && BindDown("paint") && brushKind != 0)
            {
                overlayApplied = true;
                if (mode == 0 || (regCraft && !regWhole && pickId == null && !meshPick))
                {
                    ApplyBrush(GetParts(), false);
                }
                else
                {
                    Part target = (pickId != null && pickPart != null) ? pickPart : Mouse.HoveredPart;
                    if (target != null)
                    {
                        List<Part> group = new List<Part>();
                        group.Add(target);
                        if (includeChildren) CollectChildren(target, group);
                        ApplyBrush(group, true);
                    }
                }
            }
        }

        static void CollectChildren(Part p, List<Part> into)
        {
            for (int i = 0; i < p.children.Count; i++)
            {
                into.Add(p.children[i]);
                CollectChildren(p.children[i], into);
            }
        }

        static Material regOverlayMat;
        readonly List<Mesh> ovMeshes = new List<Mesh>();
        readonly List<Transform> ovTrs = new List<Transform>();
        Part ovPart, ovLocked; string ovToken = "~"; float ovStr = -1f;
        float ovBuiltAt; int ovCount = -1;

        static Material RegOverlayMat()
        {
            if (regOverlayMat != null) return regOverlayMat;
            Shader sh = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (sh == null) sh = Shader.Find("Particles/Alpha Blended");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Transparent");
            regOverlayMat = new Material(sh);
            regOverlayMat.mainTexture = Texture2D.whiteTexture;
            if (regOverlayMat.HasProperty("_TintColor")) regOverlayMat.SetColor("_TintColor", Color.white);
            if (regOverlayMat.HasProperty("_Color")) regOverlayMat.SetColor("_Color", Color.white);
            regOverlayMat.renderQueue = 4000;
            return regOverlayMat;
        }

        void ClearOverlay()
        {
            for (int i = 0; i < ovMeshes.Count; i++) if (ovMeshes[i] != null) Destroy(ovMeshes[i]);
            ovMeshes.Clear(); ovTrs.Clear(); ovPart = null; ovToken = "~"; ovStr = -1f;
        }

        static void EdgeInc(Dictionary<long, int> d, int a, int b)
        {
            long key = (a < b) ? ((long)a << 22) | (uint)b : ((long)b << 22) | (uint)a;
            int c; d[key] = d.TryGetValue(key, out c) ? c + 1 : 1;
        }

        void BuildOverlay(Part part, string token)
        {
            ClearOverlay();
            ovPart = part; ovToken = token; ovStr = regStrength;
            if (part == null || token == "whole") return;
            if (token == "pickscan") { BuildPickPreview(part, null); return; }
            if (token.StartsWith("pick|")) { BuildPickPreview(part, token.Substring(5)); return; }
            if (IsCraftTok(token))
            {
                List<Part> ps = GetParts();
                for (int pi = 0; pi < ps.Count; pi++) if (ps[pi] != null) OverlayPart(ps[pi], PartPlace(ps[pi], token));
                return;
            }
            OverlayPart(part, token);
        }

        void OverlayPart(Part part, string token)
        {
            Transform mroot = part.transform.Find("model");
            Renderer[] rs = (mroot != null ? mroot : part.transform).GetComponentsInChildren<Renderer>(true);
            Vector3 wdir, upDir; float cw, cwHeight, fadeW; bool useHeight;
            if (ModuleReforged.IsPlaneTok(token))
            {
                float fhw;
                if (!ModuleReforged.PlaneWorld(part.transform, token, out wdir, out cw, out fadeW, out upDir, out cwHeight, out useHeight, out fhw)) return;
            }
            else
            {
                Vector3 ldir; float lcut, lfade, lheight;
                if (!WeatherCache.RegionAxisDir(token, out ldir, out lcut, out lfade, out lheight)) return;
                wdir = part.transform.TransformDirection(ldir).normalized;
                upDir = part.transform.up;
                float lo = float.MaxValue, hi = float.MinValue, vlo = float.MaxValue, vhi = float.MinValue;
                for (int i = 0; i < rs.Length; i++)
                {
                    if (rs[i] == null || ModuleReforged.SkipRenderer(rs[i]) || !rs[i].enabled || !rs[i].gameObject.activeInHierarchy) continue;
                    Part o = rs[i].GetComponentInParent<Part>(); if (o != null && o != part) continue;
                    Mesh mfm = ModuleReforged.MeshOf(rs[i]);
                    if (mfm == null || !mfm.isReadable) continue;
                    Vector3[] vs = mfm.vertices; Transform t = rs[i].transform;
                    for (int v = 0; v < vs.Length; v++)
                    {
                        Vector3 wp = t.TransformPoint(vs[v]);
                        float c = Vector3.Dot(wp, wdir); if (c < lo) lo = c; if (c > hi) hi = c;
                        float cy = Vector3.Dot(wp, upDir); if (cy < vlo) vlo = cy; if (cy > vhi) vhi = cy;
                    }
                }
                if (hi - lo < 1e-5f) return;
                cw = lo + lcut * (hi - lo);
                useHeight = lheight < 0.999f && vhi - vlo > 1e-5f;
                cwHeight = vlo + lheight * (vhi - vlo);
                fadeW = lfade * Mathf.Max(0.05f, 1f - lcut) * (hi - lo);
            }
            float alpha = 0.05f + 0.09f * Mathf.Clamp01(regStrength);
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null || ModuleReforged.SkipRenderer(rs[i]) || !rs[i].enabled || !rs[i].gameObject.activeInHierarchy) continue;
                Part owner = rs[i].GetComponentInParent<Part>();
                if (owner != null && owner != part) continue;
                Mesh mfm = ModuleReforged.MeshOf(rs[i]);
                if (mfm == null || !mfm.isReadable) continue;
                List<Vector3> V, N; List<Vector4> T; List<Vector2> U; List<int> tris;
                ModuleReforged.MeshToLists(mfm, out V, out N, out T, out U, out tris);
                List<int> keep1 = new List<int>(), drop1 = new List<int>();
                ModuleReforged.ClipSoup(V, N, T, U, rs[i].transform, wdir, cw, tris, keep1, drop1);
                List<int> region = keep1;
                if (useHeight)
                {
                    List<int> keep2 = new List<int>(), d2 = new List<int>();
                    ModuleReforged.ClipSoup(V, N, T, U, rs[i].transform, -upDir, -cwHeight, keep1, keep2, d2);
                    region = keep2;
                }
                if (region.Count == 0) continue;
                Color[] cols = new Color[V.Count];
                for (int v = 0; v < cols.Length; v++)
                {
                    float a = alpha;
                    if (fadeW > 1e-4f)
                    {
                        float d = Vector3.Dot(rs[i].transform.TransformPoint(V[v]), wdir) - cw;
                        a = alpha * (0.15f + 0.85f * Mathf.Clamp01(d / fadeW));
                    }
                    cols[v] = new Color(1f, 0.12f, 0.12f, a);
                }
                Vector3[] ov = new Vector3[V.Count];
                bool lift = N != null && N.Count == V.Count;
                for (int v = 0; v < V.Count; v++) ov[v] = lift ? V[v] + N[v] * 0.004f : V[v];
                if (!overlayApplied)
                {
                    Mesh om = new Mesh();
                    if (V.Count > 65000) om.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    om.vertices = ov;
                    om.triangles = region.ToArray();
                    om.colors = cols;
                    ovMeshes.Add(om); ovTrs.Add(rs[i].transform);
                }

                Dictionary<long, int> ec = new Dictionary<long, int>();
                for (int e = 0; e + 2 < region.Count; e += 3)
                {
                    EdgeInc(ec, region[e], region[e + 1]);
                    EdgeInc(ec, region[e + 1], region[e + 2]);
                    EdgeInc(ec, region[e + 2], region[e]);
                }
                List<int> li = new List<int>();
                foreach (KeyValuePair<long, int> kv in ec)
                    if (kv.Value == 1) { li.Add((int)(kv.Key >> 22)); li.Add((int)(kv.Key & 0x3FFFFF)); }
                if (li.Count > 0)
                {
                    Color[] lc = new Color[V.Count];
                    Color bright = new Color(1f, 0.2f, 0.2f, 0.85f);
                    for (int v = 0; v < lc.Length; v++) lc[v] = bright;
                    Mesh lm = new Mesh();
                    if (V.Count > 65000) lm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    lm.vertices = ov;
                    lm.colors = lc;
                    lm.SetIndices(li.ToArray(), MeshTopology.Lines, 0);
                    ovMeshes.Add(lm); ovTrs.Add(rs[i].transform);
                }
            }
        }

        void DoMeshPick(Vector3 mousePos)
        {
            Part part = Mouse.HoveredPart;
            if (part == null) { ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgPointPart", "Point at a part first, then press <<1>>.", BindName("pick")), 3f, ScreenMessageStyle.UPPER_CENTER); return; }
            Camera cam = Camera.main;
            if (cam == null) return;
            Ray ray = cam.ScreenPointToRay(mousePos);
            Transform mroot = part.transform.Find("model");
            Renderer[] rs = (mroot != null ? mroot : part.transform).GetComponentsInChildren<Renderer>(true);
            float bestT = float.MaxValue; Renderer bestR = null; int bestSub = -1;
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null || ModuleReforged.SkipRenderer(r) || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                Part owner = r.GetComponentInParent<Part>(); if (owner != null && owner != part) continue;
                Mesh mfm = ModuleReforged.MeshOf(r);
                if (mfm == null || !mfm.isReadable) continue;
                Mesh msh = mfm; Vector3[] vs = msh.vertices; Transform t = r.transform;
                for (int sub = 0; sub < msh.subMeshCount; sub++)
                {
                    int[] tr = msh.GetTriangles(sub);
                    for (int k = 0; k + 2 < tr.Length; k += 3)
                    {
                        float tt;
                        if (RayTri(ray, t.TransformPoint(vs[tr[k]]), t.TransformPoint(vs[tr[k + 1]]), t.TransformPoint(vs[tr[k + 2]]), out tt) && tt < bestT)
                        { bestT = tt; bestR = r; bestSub = sub; }
                    }
                }
            }
            if (bestR != null)
            {
                pickId = ModuleReforged.RelPath(bestR.transform, part.transform) + "#" + bestSub;
                pickPart = part; ovLocked = part;
                ReArm();
                ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgPickedPiece", "Picked piece: <<1>>", bestR.name), 3f, ScreenMessageStyle.UPPER_CENTER);
            }
            else ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgNoMesh", "No mesh under the cursor - this part may not be splittable."), 4f, ScreenMessageStyle.UPPER_CENTER);
        }
        static bool RayTri(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            Vector3 e1 = b - a, e2 = c - a, pv = Vector3.Cross(ray.direction, e2);
            float det = Vector3.Dot(e1, pv);
            if (det > -1e-7f && det < 1e-7f) return false;
            float inv = 1f / det; Vector3 tv = ray.origin - a;
            float u = Vector3.Dot(tv, pv) * inv; if (u < 0f || u > 1f) return false;
            Vector3 qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(ray.direction, qv) * inv; if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, qv) * inv; return t > 1e-4f;
        }

        static Color PieceColor(int i)
        {
            Color c = Color.HSVToRGB((i * 0.61803f) % 1f, 0.7f, 1f);
            c.a = 0.34f; return c;
        }
        void AddPieceOutline(Renderer r, Mesh msh, int sub)
        {
            int[] tris = msh.GetTriangles(sub);
            if (tris.Length == 0) return;
            Vector3[] vs = msh.vertices; Vector3[] nrm = msh.normals;
            bool lift = nrm != null && nrm.Length == vs.Length;
            Vector3[] ov = new Vector3[vs.Length];
            for (int v = 0; v < vs.Length; v++) ov[v] = lift ? vs[v] + nrm[v] * 0.006f : vs[v];
            Dictionary<long, int> ec = new Dictionary<long, int>();
            for (int e = 0; e + 2 < tris.Length; e += 3) { EdgeInc(ec, tris[e], tris[e + 1]); EdgeInc(ec, tris[e + 1], tris[e + 2]); EdgeInc(ec, tris[e + 2], tris[e]); }
            List<int> li = new List<int>();
            foreach (KeyValuePair<long, int> kv in ec) if (kv.Value == 1) { li.Add((int)(kv.Key >> 22)); li.Add((int)(kv.Key & 0x3FFFFF)); }
            if (li.Count == 0) return;
            Color[] lc = new Color[vs.Length]; Color bright = new Color(1f, 0.25f, 0.25f, 0.9f);
            for (int v = 0; v < lc.Length; v++) lc[v] = bright;
            Mesh lm = new Mesh();
            if (vs.Length > 65000) lm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            lm.vertices = ov; lm.colors = lc; lm.SetIndices(li.ToArray(), MeshTopology.Lines, 0);
            ovMeshes.Add(lm); ovTrs.Add(r.transform);
        }
        void AddPieceFill(Renderer r, Mesh msh, int sub, Color col)
        {
            int[] tris = msh.GetTriangles(sub);
            if (tris.Length == 0) return;
            Vector3[] vs = msh.vertices; Vector3[] nrm = msh.normals;
            bool lift = nrm != null && nrm.Length == vs.Length;
            Vector3[] ov = new Vector3[vs.Length];
            for (int v = 0; v < vs.Length; v++) ov[v] = lift ? vs[v] + nrm[v] * 0.006f : vs[v];
            Color[] cols = new Color[vs.Length];
            for (int v = 0; v < cols.Length; v++) cols[v] = col;
            Mesh om = new Mesh();
            if (vs.Length > 65000) om.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            om.vertices = ov; om.triangles = tris; om.colors = cols;
            ovMeshes.Add(om); ovTrs.Add(r.transform);
        }

        void BuildPickPreview(Part part, string pickTarget)
        {
            Transform mroot = part.transform.Find("model");
            Renderer[] rs = (mroot != null ? mroot : part.transform).GetComponentsInChildren<Renderer>(true);
            int idx = 0;
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null || ModuleReforged.SkipRenderer(r) || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                Part owner = r.GetComponentInParent<Part>(); if (owner != null && owner != part) continue;
                Mesh mfm = ModuleReforged.MeshOf(r);
                if (mfm == null || !mfm.isReadable) continue;
                Mesh msh = mfm;
                for (int sub = 0; sub < msh.subMeshCount; sub++, idx++)
                {
                    if (pickTarget == null) { AddPieceFill(r, msh, sub, PieceColor(idx)); continue; }
                    bool mtc = ModuleReforged.RelPath(r.transform, part.transform) + "#" + sub == pickTarget;
                    bool show = (regShineSpot == 2) || (regShineSpot == 1 ? !mtc : mtc);
                    if (!show) continue;
                    if (overlayApplied) AddPieceOutline(r, msh, sub);
                    else AddPieceFill(r, msh, sub, new Color(1f, 0.15f, 0.15f, 0.34f));
                }
            }
        }

        void DrawRegionOverlay()
        {
            Part pv; string tok;
            if (pickId != null && pickPart != null) { pv = pickPart; tok = "pick|" + pickId; }
            else if (meshPick) { pv = (ovLocked != null) ? ovLocked : lastHovered; tok = "pickscan"; }
            else { pv = (ovLocked != null) ? ovLocked : lastHovered; tok = CurRegion(); }
            bool craftOv = IsCraftTok(tok) && pickId == null && !meshPick;
            if (craftOv) pv = CraftAnchor();
            bool want = show && showOverlay && pv != null && (pickId != null || meshPick || !regWhole);
            if (!want) { if (ovPart != null) ClearOverlay(); return; }
            if (pv != ovPart || tok != ovToken) overlayApplied = false;
            bool stale = pv != ovPart || tok != ovToken || regStrength != ovStr || overlayApplied != ovApplied;
            if (craftOv && !stale && CraftPartCount() != ovCount) stale = true;
            if (stale && craftOv && ovPart != null && Time.realtimeSinceStartup - ovBuiltAt < 0.25f) stale = false;
            if (stale)
            {
                BuildOverlay(pv, tok); ovApplied = overlayApplied;
                ovBuiltAt = Time.realtimeSinceStartup; ovCount = CraftPartCount();
            }
            Material m = RegOverlayMat();
            for (int i = 0; i < ovMeshes.Count; i++)
                if (ovTrs[i] != null && ovMeshes[i] != null)
                    Graphics.DrawMesh(ovMeshes[i], ovTrs[i].localToWorldMatrix, m, ovTrs[i].gameObject.layer);
        }

        List<Part> GetParts()
        {
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                return new List<Part>(EditorLogic.fetch.ship.parts);
            if (FlightGlobals.ActiveVessel != null)
                return new List<Part>(FlightGlobals.ActiveVessel.parts);
            return new List<Part>();
        }

        void ApplyBrush(List<Part> parts, bool spot)
        {
            if (parts.Count > 0) lastPaintedPart = parts[0];
            if (spot && paintSym)
            {
                List<Part> extra = new List<Part>();
                for (int i = 0; i < parts.Count; i++)
                {
                    Part pt = parts[i];
                    if (pt == null || pt.symmetryCounterparts == null) continue;
                    for (int k = 0; k < pt.symmetryCounterparts.Count; k++)
                    {
                        Part sc = pt.symmetryCounterparts[k];
                        if (sc != null && !parts.Contains(sc) && !extra.Contains(sc)) extra.Add(sc);
                    }
                }
                parts.AddRange(extra);
            }
            Snapshot(parts);
            bool brushColors = brushKind == 2 || (brushKind == 1 && brAlsoColor)
                || (brushKind == 3 && IsColorTok(brTex))
                || (brushKind == 5 && ((brCombo != null && brCombo.hasColor) || StackHasColorTok(brComboStack)));
            bool brushTextures = (brushKind == 3 && !IsColorTok(brTex))
                || (brushKind == 5 && ((brCombo != null && !string.IsNullOrEmpty(brCombo.recipe)) || StackHasTexLayer(brComboStack)));
            string craftTok = (!regWhole && pickId == null) ? CurRegion() : null;
            bool craftCut = IsCraftTok(craftTok) && CraftCut(craftTok);
            int n = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                ModuleReforged mod = parts[i].Modules.GetModule<ModuleReforged>();
                if (mod == null || mod.Excluded) continue;
                if (craftCut && !PartInCraftCut(parts[i])) continue;
                if (pickId != null && pickPart != null && parts[i].partInfo != pickPart.partInfo) continue;
                mod.rdxKeepShiny = uiKeepShiny;
                if (brushColors) mod.rdxPaintDetail = WeatherCache.PaintDetail;
                if (brushTextures) mod.rdxTexDetail = WeatherCache.TexSkipDetail;
                bool wholeFilter = regWhole && pickId == null && !meshPick && mod.Active;
                if (wholeFilter && regShineSpot == 0)
                {
                    bool did = true;
                    switch (brushKind)
                    {
                        case 1: mod.PaintedFinish(brMetal, brSmooth, brAlsoColor, brColor); break;
                        case 2: mod.PaintedColour(brColor); break;
                        case 3: mod.PaintedWeather(brLevel, brTex, brScale); break;
                        case 7: mod.PaintedClearColour(); break;
                        default: did = false; break;
                    }
                    if (did) { n++; continue; }
                }
                if (brushKind != 4 && brushKind != 6 && brushKind != 8)
                    mod.SetRegion(wholeFilter ? (regShineSpot == 1 ? "base" : "whole") : PartPlace(parts[i], CurRegion()));
                mod.rdxShineSpot = (pickId != null && !brForceNew) ? regShineSpot : 0;
                switch (brushKind)
                {
                    case 1:
                        if (brMetal < 0f) mod.ClearFinish();
                        else if (brAlsoColor) mod.SetBoth(brMetal, brSmooth, brColor);
                        else mod.SetFinish(brMetal, brSmooth);
                        n++;
                        break;
                    case 2:
                        mod.SetColour(brColor);
                        n++;
                        break;
                    case 3:
                        mod.AddWeather(brLevel, brTex, PartPlace(parts[i], brPlace), brScale);
                        n++;
                        break;
                    case 4:
                        mod.ClearWeatherAll();
                        n++;
                        break;
                    case 5:
                        if (brCombo != null && !string.IsNullOrEmpty(brCombo.recipe))
                        {
                            mod.ApplyComboLayers(PartPlace(parts[i], CurRegion()), StrengthCode(), brCombo.recipe.Split(';'),
                                brCombo.hasColor, new Color(brCombo.r, brCombo.g, brCombo.b),
                                brCombo.hasFinish, brCombo.metal, brCombo.smooth, regPatScale);
                        }
                        else
                        {
                            mod.AppendStack(IsCraftTok(CurRegion()) ? ModuleReforged.PlaceStack(brComboStack, PartPlace(parts[i], CurRegion())) : brComboStack);
                            if (brCombo != null)
                            {
                                if (brCombo.hasFinish && brCombo.hasColor)
                                    mod.SetBoth(brCombo.metal, brCombo.smooth, new Color(brCombo.r, brCombo.g, brCombo.b));
                                else if (brCombo.hasFinish) mod.SetFinish(brCombo.metal, brCombo.smooth);
                                else if (brCombo.hasColor) mod.SetColour(new Color(brCombo.r, brCombo.g, brCombo.b));
                            }
                        }
                        n++;
                        break;
                    case 6:
                        mod.ClearAll();
                        n++;
                        break;
                    case 7:
                        mod.ClearColourAndTints();
                        n++;
                        break;
                    case 8:
                        mod.RemoveMetal();
                        n++;
                        break;
                }
            }
            ScreenMessages.PostScreenMessage(spot ? Loc.T("#RDX_MsgSpotApplied", "Spot: '<<1>>' -> <<2>> parts", brushLabel, n) : Loc.T("#RDX_MsgVesselApplied", "Vessel: '<<1>>' -> <<2>> parts", brushLabel, n),
                3f, ScreenMessageStyle.UPPER_CENTER);
        }

        void Arm(int kind, string label)
        {
            brushKind = kind; brushLabel = label; brForceNew = false;
        }

        System.Action lastArm;
        void ReArm() { if (lastArm != null) lastArm(); }

        void ArmFinish(string label, float metal, float smooth)
        {
            lastArm = delegate { ArmFinish(label, metal, smooth); };
            brMetal = metal; brSmooth = smooth; brAlsoColor = false;
            brIcon = (metal >= 0f) ? ShineIcon(metal, smooth) : null; brIconTint = Color.white;
            if (buildingFinish && metal >= 0f)
            { bMetal = Mathf.Clamp01(metal); bSmooth = Mathf.Clamp01(smooth); bufMetal = bufShine = null; }
            Arm(1, label);
        }

        void ArmMetal(int i)
        {
            lastArm = delegate { ArmMetal(i); };
            Color col = new Color(MetalRGB[i][0] / 255f, MetalRGB[i][1] / 255f, MetalRGB[i][2] / 255f);
            brIcon = MetalIcon(col); brIconTint = Color.white;
            if (regWhole)
            {
                brMetal = 1f; brSmooth = MetalSmooth; brAlsoColor = true; brColor = col;
                Arm(1, Loc.N(MetalNames[i]));
            }
            else
            {
                Combo c = new Combo();
                c.name = MetalNames[i];
                c.hasFinish = true; c.metal = 1f; c.smooth = MetalSmooth;
                c.stack = (WeatherCache.ColorReplace ? "paint#" : "omul#") + ModuleReforged.HexRGB(col) + ":" + CurRegion() + ":2";
                brCombo = c; brComboStack = c.stack;
                Arm(5, Loc.N(MetalNames[i]) + " / " + Loc.T("#RDX_LblRegion", "region"));
                brForceNew = true;
            }
        }

        void ArmHeatDiscolor(int i)
        {
            lastArm = delegate { ArmHeatDiscolor(i); };
            Combo c = new Combo();
            c.name = HeatNames[i];
            c.stack = HeatFiles[i] + (heatVariant == 1 ? "b" : "") + ":" + CurRegion() + ":" + StrengthCode() + ModuleReforged.ScaleSuffix(HeatFiles[i], regPatScale);
            brIcon = HeatIcon(i, heatVariant == 1); brIconTint = Color.white;
            c.hasFinish = true; c.metal = 1f; c.smooth = HeatSmooth;
            brCombo = c; brComboStack = c.stack;
            Arm(5, Loc.T("#RDX_LblHeat", "Heat: <<1>>", Loc.N(HeatNames[i])) + (regWhole ? "" : " / " + Loc.T("#RDX_LblRegion", "region")));
            brForceNew = true;
        }

        void ArmColour(Color c, string label)
        {
            lastArm = delegate { ArmColour(c, label); };
            LoadPickerColour(c);
            brIcon = Texture2D.whiteTexture; brIconTint = c;
            if (regWhole && pickId == null)
            {
                brColor = c;
                Arm(2, label);
            }
            else
            {
                brLevel = 2;
                brTex = (WeatherCache.ColorReplace ? "paint#" : "omul#") + ModuleReforged.HexRGB(c);
                brPlace = CurRegion();
                Arm(3, label + " / " + Loc.T("#RDX_LblRegion", "region"));
            }
        }

        void ArmWeather(int idx)
        {
            lastArm = delegate { ArmWeather(idx); };
            brIcon = null; brIconTint = Color.white;
            if (idx < 0) { Arm(4, Loc.T("#RDX_LblCleanWeather", "Clean weathering")); return; }
            float dm, ds;
            if (DefaultShine(WeatherFiles[idx], out dm, out ds))
            {
                Combo c = new Combo();
                c.name = WeatherLabels[idx];
                c.stack = WeatherFiles[idx] + ":" + CurRegion() + ":" + StrengthCode() + ModuleReforged.ScaleSuffix(WeatherFiles[idx], regPatScale);
                c.hasFinish = true; c.metal = dm; c.smooth = ds;
                brCombo = c; brComboStack = c.stack;
                Arm(5, Loc.N(WeatherLabels[idx]) + (regWhole ? "" : " / " + Loc.T("#RDX_LblRegion", "region")));
                brForceNew = true;
                return;
            }
            brLevel = StrengthCode();
            brScale = regPatScale;
            brTex = WeatherFiles[idx];
            brPlace = CurRegion();
            Arm(3, Loc.N(WeatherLabels[idx]) + (regWhole ? "" : " / " + Loc.T("#RDX_LblRegion", "region")));
            brForceNew = true;
        }

        static bool DefaultShine(string name, out float metal, out float smooth)
        {
            metal = 0f; smooth = 0f;
            switch (name)
            {
                case "carbon": metal = 0.75f; smooth = 0.82f; return true;
                case "brushed": case "diamond": case "metal3": case "plates14": case "mli":
                case "metalgrid":
                    metal = 0.75f; smooth = 0.82f; return true;
                case "tiles138": metal = 0.40f; smooth = 0.72f; return true;
                case "tiles": case "tilesw": metal = 0.40f; smooth = 0.72f; return true;
                case "plates6": metal = 0.85f; smooth = 0.65f; return true;
                case "msheet": case "tactile1": case "tactile2": case "chip":
                    metal = 0.55f; smooth = 0.90f; return true;
                case "heatd1": case "heatd2": case "heatd3": case "heatd4":
                case "heatd5": case "heatd6": case "heatd7": case "heatd8":
                    metal = 1f; smooth = HeatSmooth; return true;
                default: return false;
            }
        }

        void ArmDirtTint(int i)
        {
            lastArm = delegate { ArmDirtTint(i); };
            brLevel = StrengthCode();
            brTex = "tint#" + string.Format("{0:X2}{1:X2}{2:X2}",
                (int)DirtRGB[i][0], (int)DirtRGB[i][1], (int)DirtRGB[i][2]);
            brPlace = CurRegion();
            Arm(3, Loc.T("#RDX_LblTint", "<<1>> tint", Loc.N(DirtNames[i])) + (regWhole ? "" : " / " + Loc.T("#RDX_LblRegion", "region")));
        }

        void ArmCombo(Combo c) { ArmCombo(c, false); }
        void ArmCombo(Combo c, bool snapScale)
        {
            lastArm = delegate { ArmCombo(c, false); };
            brIcon = null; brIconTint = Color.white;
            brCombo = c;
            if (snapScale && c.scale > 0f) { regPatScale = c.scale; regScaleBuf = null; }
            brComboStack = string.IsNullOrEmpty(c.recipe) ? RemapPlace(c.stack, CurRegion(), StrengthCode(), regPatScale) : "";
            Arm(5, Loc.T("#RDX_LblCombo", "Combo: <<1>>", c.name) + (regWhole ? "" : " / " + Loc.T("#RDX_LblRegion", "region")));
        }

        string CurRegion()
        {
            if (pickId != null) return "pick|" + pickId;
            if (regWhole)
            {
                if (regShineSpot == 2) return "whole";
                if (regShineSpot == 1) return "base";
                return "whole";
            }
            int c = Mathf.Clamp(Mathf.RoundToInt(regCut * 100f), 0, 100);
            int f = Mathf.Clamp(Mathf.RoundToInt(regFade * 100f), 0, 100);
            int h = Mathf.Clamp(Mathf.RoundToInt(regHeight * 100f), 0, 100);
            return (regCraft ? "c" : "g") + regAxis + "_" + c.ToString("00") + "_" + f.ToString("00") + "_" + h.ToString("00");
        }

        static bool IsCraftTok(string t) { return t != null && t.Length > 1 && t[0] == 'c' && char.IsDigit(t[1]); }

        Part CraftAnchor()
        {
            if (HighLogic.LoadedSceneIsEditor) return EditorLogic.RootPart;
            return (FlightGlobals.ActiveVessel != null) ? FlightGlobals.ActiveVessel.rootPart : null;
        }

        int CraftPartCount()
        {
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null) return EditorLogic.fetch.ship.parts.Count;
            return (FlightGlobals.ActiveVessel != null) ? FlightGlobals.ActiveVessel.parts.Count : 0;
        }

        void CraftExtentPart(Part p, ref float lo, ref float hi, ref float vlo, ref float vhi)
        {
            Transform mroot = p.transform.Find("model");
            Renderer[] rs = (mroot != null ? mroot : p.transform).GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null || !r.enabled || ModuleReforged.SkipRenderer(r)) continue;
                Part o = r.GetComponentInParent<Part>(); if (o != null && o != p) continue;
                Mesh cm0 = ModuleReforged.MeshOf(r);
                bool local = cm0 != null;
                Bounds b = local ? cm0.bounds : r.bounds;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((k & 1) == 0 ? -1f : 1f, (k & 2) == 0 ? -1f : 1f, (k & 4) == 0 ? -1f : 1f));
                    Vector3 w = local ? r.transform.TransformPoint(c) : c;
                    float a = Vector3.Dot(w, ccW); if (a < lo) lo = a; if (a > hi) hi = a;
                    float hv = Vector3.Dot(w, ccU); if (hv < vlo) vlo = hv; if (hv > vhi) vhi = hv;
                }
            }
        }

        string craftCutTok; int craftCutFrame = -1; bool ccOk;
        Vector3 ccW, ccU; float ccCw, ccFw, ccH, ccFh; bool ccUseH;

        bool CraftCut(string tok)
        {
            if (tok == craftCutTok && Time.frameCount == craftCutFrame) return ccOk;
            craftCutTok = tok; craftCutFrame = Time.frameCount; ccOk = false;
            Vector3 ldir; float lcut, lfade, lheight;
            if (!WeatherCache.RegionAxisDir("g" + tok.Substring(1), out ldir, out lcut, out lfade, out lheight)) return false;
            List<Part> ps = GetParts();
            if (ps.Count == 0) return false;
            if (HighLogic.LoadedSceneIsEditor) { ccW = ldir.normalized; ccU = Vector3.up; }
            else
            {
                Part root = CraftAnchor();
                if (root == null) return false;
                ccW = root.transform.TransformDirection(ldir).normalized; ccU = root.transform.up;
            }
            float lo = float.MaxValue, hi = float.MinValue, vlo = float.MaxValue, vhi = float.MinValue;
            for (int i = 0; i < ps.Count; i++) if (ps[i] != null) CraftExtentPart(ps[i], ref lo, ref hi, ref vlo, ref vhi);
            if (hi - lo < 1e-4f) return false;
            ccCw = lo + lcut * (hi - lo);
            ccFw = lfade * Mathf.Max(1e-4f, hi - ccCw);
            ccUseH = lheight < 0.999f && vhi - vlo > 1e-4f;
            ccH = vlo + lheight * (vhi - vlo);
            ccFh = lfade * Mathf.Max(1e-4f, ccH - vlo);
            ccOk = true;
            return true;
        }

        bool PartInCraftCut(Part p)
        {
            Transform mroot = p.transform.Find("model");
            Renderer[] rs = (mroot != null ? mroot : p.transform).GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null || !r.enabled || ModuleReforged.SkipRenderer(r)) continue;
                Part o = r.GetComponentInParent<Part>(); if (o != null && o != p) continue;
                Mesh cm0 = ModuleReforged.MeshOf(r);
                bool local = cm0 != null;
                Bounds b = local ? cm0.bounds : r.bounds;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((k & 1) == 0 ? -1f : 1f, (k & 2) == 0 ? -1f : 1f, (k & 4) == 0 ? -1f : 1f));
                    Vector3 w = local ? r.transform.TransformPoint(c) : c;
                    if (Vector3.Dot(w, ccW) >= ccCw && (!ccUseH || Vector3.Dot(w, ccU) <= ccH)) return true;
                }
            }
            return false;
        }

        string PartPlace(Part p, string tok)
        {
            if (p == null || !IsCraftTok(tok)) return tok;
            if (!CraftCut(tok)) return "whole";
            return ModuleReforged.PlaneToken(p.transform, ccW, ccCw, ccFw, ccUseH, ccU, ccH, ccFh);
        }

        bool DrawRegion()
        {
            bool ch = false;
            int rmode = !regWhole ? 1 : ((meshPick || pickId != null) ? 2 : 0);
            int nmode = GUILayout.Toolbar(rmode, new string[] { Loc.T("#RDX_ModeWhole", "Whole part"), Loc.T("#RDX_ModeRegion", "Region"), Loc.T("#RDX_ModePick", "Pick") });
            Hint(Loc.T("#RDX_HintModes", "Whole part: paints all of it. Region: a slice by side\nand height. Pick: one separate piece of the part."));
            if (nmode != rmode)
            {
                pickId = null; pickPart = null; meshPick = false;
                regShineSpot = (nmode == 0) ? 2 : 0;
                regWhole = (nmode != 1);
                if (nmode == 1) { regCut = 0.4f; regHeight = 1f; regFade = 0f; regCutBuf = null; regHeightBuf = null; regFadeBuf = null; if (mode == 0) mode = 1; }
                else if (nmode == 2) meshPick = true;
                ch = true;
            }
            if (nmode != 2 && (pickId != null || meshPick)) { pickId = null; pickPart = null; meshPick = false; ch = true; }
            if (nmode == 1)
            {
                int nc0 = GUILayout.Toolbar(regCraft ? 1 : 0, new string[] { Loc.T("#RDX_RegEachPart", "Each part"), Loc.T("#RDX_RegWholeCraft", "Whole craft") });
                if ((nc0 == 1) != regCraft) { regCraft = nc0 == 1; ch = true; }
                if (regCraft) Hint(Loc.T("#RDX_HintRegCraft", "One slice across the whole craft, like the top half\nof a rocket or one side of a plane."));
                int ns = GUILayout.Toolbar(regAxis, new string[] { Loc.T("#RDX_AxTop", "Top"), Loc.T("#RDX_AxBtm", "Btm"), Loc.T("#RDX_AxFront", "Front"), Loc.T("#RDX_AxBack", "Back"), Loc.T("#RDX_AxLeft", "Left"), Loc.T("#RDX_AxRight", "Right") });
                if (ns != regAxis) { regAxis = ns; ch = true; }
                float nc = SliderNum(Loc.T("#RDX_Cutoff", "Cutoff"), regCut, 0f, 1f, ref regCutBuf);
                if (nc != regCut) { regCut = nc; ch = true; }
                float nh = SliderNum(Loc.T("#RDX_Height", "Height"), regHeight, 0f, 1f, ref regHeightBuf);
                if (nh != regHeight) { regHeight = nh; ch = true; }
                float nf = SliderNum(Loc.T("#RDX_Fade", "Fade"), regFade, 0f, 1f, ref regFadeBuf);
                if (nf != regFade) { regFade = nf; ch = true; }
            }
            else if (nmode == 2)
            {
                if (pickId == null)
                    Hint(Loc.T("#RDX_HintPick", "Point at a coloured piece and press <<1>> to select it, then paint it.\nEach separate piece has its own colour. A part drawn as one piece can't be split.", BindName("pick")));
                else if (GUILayout.Button(Loc.T("#RDX_PickAgain", "Pick a different piece"), GUILayout.Height(20f)))
                { pickId = null; pickPart = null; meshPick = true; ch = true; }
            }
            if (nmode != 0)
            {
                showOverlay = GUILayout.Toggle(showOverlay, "<b>" + Loc.T("#RDX_ShowPreview", "Show preview") + "</b>", toggleBold);
                Hint((showOverlay && ovLocked != null ? Loc.T("#RDX_PreviewPinned", "Preview PINNED.") + " " : "") + Loc.T("#RDX_HintPreview", "Highlights where the paint will land.\nPoint at a part and press <<1>> to pin/unpin the preview.", BindName("pin")));
            }
            return ch;
        }

        bool TargetPainted()
        {
            Part tp = ovLocked;
            if (tp == null) tp = lastPaintedPart;
            if (tp == null) tp = Mouse.HoveredPart;
            if (tp == null) return false;
            ModuleReforged m = tp.Modules.GetModule<ModuleReforged>();
            return m != null && m.Active;
        }
        void DrawRegionTarget()
        {
            if (pickId != null || meshPick) { regShineSpot = 0; return; }
            if (!regWhole) { regShineSpot = 0; return; }
            if (!TargetPainted()) { regShineSpot = 2; return; }
            string[] nm = { Loc.T("#RDX_FilterNew", "New"), Loc.T("#RDX_FilterOriginal", "Original"), Loc.T("#RDX_FilterWhole", "Whole") };
            if (GUILayout.Button(Loc.T("#RDX_ExtraFilters", "Extra filters  (<<1>>)", nm[regShineSpot].ToLowerInvariant()), GUILayout.Height(20f)))
                coversOpen = !coversOpen;
            if (!coversOpen) Hint(Loc.T("#RDX_HintFiltersShort", "Limits paint to your new textures, or only the original one."));
            if (coversOpen)
            {
                regShineSpot = GUILayout.Toolbar(regShineSpot, nm);
                Hint(Loc.T("#RDX_HintFilters", "New = only paints over the new textures you've applied.  Original = only paints over the original texture, not the new ones.  Whole = the whole part."));
            }
        }

        bool DrawStrength()
        {
            bool ch = false;
            float ns = SliderNum(Loc.T("#RDX_Strength", "Strength"), regStrength, 0.1f, 1f, ref regStrBuf);
            if (ns != regStrength) { regStrength = ns; ch = true; }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("#RDX_Low", "Low"), GUILayout.Height(20f))) { regStrength = 0.25f; regStrBuf = null; ch = true; }
            if (GUILayout.Button(Loc.T("#RDX_Med", "Med"), GUILayout.Height(20f))) { regStrength = 0.55f; regStrBuf = null; ch = true; }
            if (GUILayout.Button(Loc.T("#RDX_Max", "Max"), GUILayout.Height(20f))) { regStrength = 1f; regStrBuf = null; ch = true; }
            GUILayout.EndHorizontal();
            return ch;
        }

        bool DrawPatternSize()
        {
            bool ch = false;
            float ns = SliderNum(Loc.T("#RDX_PatternSize", "Pattern size"), regPatScale, 0.25f, 3f, ref regScaleBuf);
            if (ns != regPatScale) { regPatScale = ns; ch = true; }
            if (regPatScale != 1f && GUILayout.Button(Loc.T("#RDX_ResetPatternSize", "Reset pattern size"), GUILayout.Height(18f)))
            { regPatScale = 1f; regScaleBuf = null; ch = true; }
            return ch;
        }
        int StrengthCode() { return Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(12f, 90f, (regStrength - 0.1f) / 0.9f)), 12, 90); }

        static string RemapPlace(string stack, string place, int lvl, float scale)
        {
            if (string.IsNullOrEmpty(stack)) return stack;
            string[] es = stack.Split(';');
            for (int i = 0; i < es.Length; i++)
            {
                string[] f = es[i].Split(':');
                if (f.Length >= 3) es[i] = f[0] + ":" + place + ":" + lvl + ModuleReforged.ScaleSuffix(f[0], scale);
            }
            return string.Join(";", es);
        }

        List<UndoEntry> Capture(List<Part> parts)
        {
            List<UndoEntry> set = new List<UndoEntry>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                ModuleReforged mod = (parts[i] != null) ? parts[i].Modules.GetModule<ModuleReforged>() : null;
                if (mod == null) continue;
                UndoEntry u = new UndoEntry();
                u.part = parts[i];
                u.fin = mod.rdxFinish; u.m = mod.rdxMetal; u.s = mod.rdxSmooth;
                u.col = mod.rdxColored; u.r = mod.rdxR; u.g = mod.rdxG; u.b = mod.rdxB;
                u.stack = mod.rdxStack;
                u.zones = mod.rdxZones; u.region = mod.rdxRegion; u.spot = mod.rdxShineSpot; u.scale = mod.rdxScale;
                u.keepShiny = mod.rdxKeepShiny;
                u.cmode = mod.rdxColorMode;
                u.pdet = mod.rdxPaintDetail;
                u.tdet = mod.rdxTexDetail;
                set.Add(u);
            }
            return set;
        }

        void Snapshot(List<Part> parts)
        {
            undoStack.Add(Capture(parts));
            while (undoStack.Count > UndoCap) undoStack.RemoveAt(0);
            redoStack.Clear();
        }

        static List<Part> PartsOf(List<UndoEntry> set)
        {
            List<Part> ps = new List<Part>(set.Count);
            for (int i = 0; i < set.Count; i++) if (set[i].part != null) ps.Add(set[i].part);
            return ps;
        }

        int RestoreSet(List<UndoEntry> set)
        {
            int n = 0;
            for (int i = 0; i < set.Count; i++)
            {
                UndoEntry u = set[i];
                if (u.part == null) continue;
                ModuleReforged mod = u.part.Modules.GetModule<ModuleReforged>();
                if (mod == null) continue;
                mod.RestoreState(u.fin, u.m, u.s, u.col, u.r, u.g, u.b, u.stack, u.zones, u.region, u.spot, u.scale, u.keepShiny, u.cmode, u.pdet, u.tdet);
                n++;
            }
            return n;
        }

        void UndoLast()
        {
            if (undoStack.Count == 0) { ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgNothingUndo", "Nothing to undo"), 2.5f, ScreenMessageStyle.UPPER_CENTER); return; }
            List<UndoEntry> prev = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            redoStack.Add(Capture(PartsOf(prev)));
            ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgUndo", "Undo (<<1>> parts)", RestoreSet(prev)), 2.5f, ScreenMessageStyle.UPPER_CENTER);
        }

        void RedoLast()
        {
            if (redoStack.Count == 0) { ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgNothingRedo", "Nothing to redo"), 2.5f, ScreenMessageStyle.UPPER_CENTER); return; }
            List<UndoEntry> nxt = redoStack[redoStack.Count - 1];
            redoStack.RemoveAt(redoStack.Count - 1);
            undoStack.Add(Capture(PartsOf(nxt)));
            while (undoStack.Count > UndoCap) undoStack.RemoveAt(0);
            ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgRedo", "Redo (<<1>> parts)", RestoreSet(nxt)), 2.5f, ScreenMessageStyle.UPPER_CENTER);
        }

        const string LockId = "ReforgedReduxWindow";
        bool inputLocked, lockedEyedrop;

        void ManageInputLock()
        {
            Vector2 mpos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / uiScale;
            bool clickPick = eyedrop || diagPick || ignorePick;
            bool over = show && (clickPick || win.Contains(mpos) || (settingsOpen && setWin.Contains(mpos)));

            if (over && (!inputLocked || lockedEyedrop != clickPick))
            {
                if (clickPick)
                    InputLockManager.SetControlLock(ControlTypes.All, LockId);
                else if (HighLogic.LoadedSceneIsEditor)
                    InputLockManager.SetControlLock(
                        ControlTypes.EDITOR_PAD_PICK_PLACE | ControlTypes.EDITOR_ICON_PICK |
                        ControlTypes.EDITOR_ICON_HOVER | ControlTypes.EDITOR_GIZMO_TOOLS | ControlTypes.CAMERACONTROLS, LockId);
                else
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, LockId);
                inputLocked = true; lockedEyedrop = clickPick;
            }
            else if (!over && inputLocked)
            {
                InputLockManager.RemoveControlLock(LockId);
                inputLocked = false; lockedEyedrop = false;
            }
        }

        void OnGUI()
        {
            if (!show) { capturing = -1; SyncCaptureLock(); if (inputLocked) { InputLockManager.RemoveControlLock(LockId); inputLocked = false; } return; }
            if (capturing >= 0 && Event.current.type == EventType.KeyDown && !IsModifierKey(Event.current.keyCode))
            {
                if (draftBinds == null) OpenSettingsDraft();
                KeyBind cb = draftBinds[capturing];
                KeyCode kc = Event.current.keyCode;
                if (kc == KeyCode.Backspace || kc == KeyCode.Delete) { cb.key = KeyCode.None; cb.ctrl = cb.shift = cb.alt = false; }
                else if (kc != KeyCode.Escape)
                {
                    cb.key = kc;
                    cb.ctrl = Event.current.control; cb.shift = Event.current.shift; cb.alt = Event.current.alt;
                }
                capturing = -1;
                Event.current.Use();
            }
            GUI.skin = HighLogic.Skin;
            if (headerStyle == null)
            {
                headerStyle = new GUIStyle(GUI.skin.label);
                headerStyle.fontStyle = FontStyle.Bold;
                headerStyle.normal.textColor = Color.white;
                hintStyle = new GUIStyle(GUI.skin.label);
                hintStyle.fontSize = 13;
                hintStyle.richText = true;
                builderLabel = new GUIStyle(GUI.skin.label);
                builderLabel.fontSize = 13;
                builderLabel.richText = true;
                brushStyle = new GUIStyle(GUI.skin.label);
                brushStyle.fontSize = 19;
                brushStyle.fontStyle = FontStyle.Bold;
                brushStyle.richText = true;
                brushStyle.wordWrap = true;
                brushStyle.normal.textColor = Color.white;
                pressStyle = new GUIStyle(GUI.skin.label);
                pressStyle.fontSize = 15;
                pressStyle.fontStyle = FontStyle.Bold;
                pressStyle.richText = true;
                toggleBold = new GUIStyle(GUI.skin.toggle);
                toggleBold.richText = true;
            }

            if (tinyBtn == null)
            {
                tinyBtn = new GUIStyle(GUI.skin.button);
                tinyBtn.padding = new RectOffset(0, 0, 0, 0);
                tinyBtn.margin = new RectOffset(0, 0, 0, 0);
                tinyBtn.alignment = TextAnchor.MiddleCenter;
                tinyBtn.fontSize = 13;
                tinyBtn.fontStyle = FontStyle.Bold;
            }
            win.width = Mathf.Clamp(win.width, 300f, 700f);
            win.height = Mathf.Clamp(win.height, 220f, Screen.height / uiScale);
            Matrix4x4 prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));
            win = GUILayout.Window(GetInstanceID(), win, DrawWindow, "Reforged Redux v" + Version, GUILayout.Width(win.width), GUILayout.Height(win.height));
            if (settingsOpen)
                setWin = GUILayout.Window(GetInstanceID() + 1, setWin, DrawSettings, Loc.T("#RDX_SettingsTitle", "Reforged Settings"), GUILayout.Width(320f));
            GUI.matrix = prevMatrix;
            if (scaleReq != 0f) { ApplyScale(scaleReq); scaleReq = 0f; }
            ManageInputLock();
            SyncCaptureLock();
        }

        void Header(string t) { GUILayout.Space(3f); GUILayout.Label(t, headerStyle); }

        const float ColW = 166f;
        const float ColWs = 156f;

        float gridW = 360f;
        int GridCols(float colW, out float cellW)
        {
            float avail = gridW - 52f;
            int c = Mathf.FloorToInt(avail / colW);
            if (c < 2) c = 2;
            cellW = avail / c;
            return c;
        }

        static void Swatch(Color c)
        {
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Width(28f), GUILayout.Height(28f));
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill);
            GUI.color = old;
        }

        float SliderNum(string label, float val, float min, float max, ref string buf)
        {
            bool unused;
            return SliderNum(label, val, min, max, ref buf, null, out unused);
        }

        float SliderNum(string label, float val, float min, float max, ref string buf, string resetLabel, out bool resetPressed)
        {
            resetPressed = false;
            if (buf == null) buf = val.ToString("0.00");
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, builderLabel, GUILayout.Width(76f));
            float sv = GUILayout.HorizontalSlider(val, min, max, GUILayout.Width(118f), GUILayout.Height(20f));
            if (sv != val) buf = sv.ToString("0.00");
            buf = GUILayout.TextField(buf, GUILayout.Width(48f));
            if (resetLabel != null && GUILayout.Button(resetLabel, tinyBtn, GUILayout.Width(52f), GUILayout.Height(20f))) resetPressed = true;
            float pv2;
            float result = float.TryParse(buf, out pv2) ? Mathf.Clamp(pv2, min, max) : sv;
            GUILayout.EndHorizontal();
            return result;
        }

        void Hint(string s) { if (showHints) GUILayout.Label(s, hintStyle); }

        void DrawWindow(int id)
        {
            if (Event.current.type == EventType.Layout) gridW = win.width;
            GUILayout.BeginVertical();
            int newTab = GUILayout.Toolbar(tab, new string[] { Loc.T("#RDX_TabFinish", "Finish"), Loc.T("#RDX_TabColour", "Colour"), Loc.T("#RDX_TabMetals", "Metals"), Loc.T("#RDX_TabDirt", "Dirt"), Loc.T("#RDX_TabCombo", "Combo") });
            if (newTab != tab) tab = newTab;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<---", GUILayout.Height(22f))) UndoLast();
            if (GUILayout.Button("--->", GUILayout.Height(22f))) RedoLast();
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            tabScroll = GUILayout.BeginScrollView(tabScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandHeight(true));
            if (tab == 0) DrawFinishTab();
            else if (tab == 1) DrawColourTab();
            else if (tab == 2) DrawMetalsTab();
            else if (tab == 3) DrawDirtTab();
            else DrawComboTab();
            GUILayout.EndScrollView();

            DrawResetRow();
            GUILayout.Space(6f);
            if (mode == 0) GUILayout.Label(Loc.T("#RDX_PressWhole", "Press P to paint the WHOLE vessel."), pressStyle);
            else
            {
                GUILayout.Label(Loc.T("#RDX_PressHovered", "Press P to paint the hovered part."), pressStyle);
                includeChildren = GUILayout.Toggle(includeChildren, "<b>" + Loc.T("#RDX_IncludeAttached", "Include attached parts") + "</b>", toggleBold);
                Hint(Loc.T("#RDX_HintAttached", "Also paints the parts attached onto the hovered part."));
                bool nSym = GUILayout.Toggle(paintSym, "<b>" + Loc.T("#RDX_SymmetryPaint", "Symmetry paint") + "</b>", toggleBold);
                if (nSym != paintSym) { paintSym = nSym; SavePresets(); }
                Hint(Loc.T("#RDX_HintSym", "Also paints the part's symmetry copies."));
            }
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (brIcon != null)
            {
                Rect ir = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Width(40f), GUILayout.Height(40f));
                Color oc = GUI.color; GUI.color = brIconTint;
                GUI.DrawTexture(ir, brIcon, ScaleMode.StretchToFill, true);
                GUI.color = oc;
            }
            GUILayout.Label(brushLabel, brushStyle);
            GUILayout.EndHorizontal();
            if (tab == 0)
            {
                if (brushKind == 1 && brMetal >= 0f)
                    GUILayout.Label(Loc.T("#RDX_LblMetalShine", "Metal: <<1>>    Shine: <<2>>", brMetal.ToString("0.00"), brSmooth.ToString("0.00")), hintStyle);
            }
            else if (tab != 1)
                GUILayout.Label(Loc.T("#RDX_LblStrengthVal", "Strength: <<1>>", regStrength.ToString("0.00")), hintStyle);
            GUILayout.Space(2f);
            bool nShow = GUILayout.Toggle(showHints, Loc.T("#RDX_ShowHelp", "Show Help"));
            if (nShow != showHints) { showHints = nShow; SavePresets(); }
            GUILayout.Space(8f);
            {
                Color _oc = GUI.backgroundColor;
                GUILayout.BeginHorizontal();
                GUI.backgroundColor = (mode == 0) ? new Color(0.90f, 0.25f, 0.25f) : _oc;
                if (GUILayout.Button(Loc.T("#RDX_WholeVessel", "Whole vessel") + (mode == 0 ? " (" + Loc.T("#RDX_On", "ON") + ")" : ""), GUILayout.Height(22f))) mode = 0;
                GUI.backgroundColor = (mode == 1) ? new Color(0.25f, 0.70f, 0.35f) : _oc;
                if (GUILayout.Button(Loc.T("#RDX_SpotBrush", "Spot brush") + (mode == 1 ? " (" + Loc.T("#RDX_On", "ON") + ")" : ""), GUILayout.Height(22f))) mode = 1;
                GUI.backgroundColor = _oc;
                GUILayout.EndHorizontal();
                Hint(Loc.T("#RDX_HintBrushMode", "Whole vessel paints every part. Spot brush paints\nonly the part under the cursor."));
            }
            GUILayout.Space(16f);
            GUILayout.EndVertical();

            if (resizing && !Input.GetMouseButton(0)) resizing = false;
            if (GUI.Button(new Rect(4f, win.height - 20f, 18f, 18f), "-", tinyBtn)) scaleReq = -0.1f;
            if (GUI.Button(new Rect(24f, win.height - 20f, 18f, 18f), "+", tinyBtn)) scaleReq = 0.1f;
            {
                string stxt = Loc.T("#RDX_Settings", "Settings");
                float sw2 = Mathf.Max(70f, tinyBtn.CalcSize(new GUIContent(stxt)).x + 12f);
                if (GUI.Button(new Rect((win.width - sw2) * 0.5f, win.height - 20f, sw2, 18f), stxt, tinyBtn))
                {
                    settingsOpen = !settingsOpen;
                    capturing = -1;
                    if (settingsOpen)
                    {
                        OpenSettingsDraft();
                        setWin.x = win.xMax + 6f; setWin.y = win.y;
                        if (setWin.x + 326f > Screen.width / uiScale) setWin.x = win.x - 326f;
                    }
                }
            }
            Rect grip = new Rect(win.width - 20f, win.height - 20f, 18f, 18f);
            GUI.Box(grip, "::");
            Event ev = Event.current;
            if (ev.type == EventType.MouseDown) { resizing = grip.Contains(ev.mousePosition); if (resizing) ev.Use(); }
            else if (ev.type == EventType.MouseUp) { if (resizing) ev.Use(); resizing = false; }
            if (resizing)
            {
                win.width = Mathf.Clamp(ev.mousePosition.x + 10f, 300f, 700f);
                win.height = Mathf.Clamp(ev.mousePosition.y + 10f, 220f, Screen.height / uiScale);
                savedWinW = win.width; savedWinH = win.height; settingsDirty = true;
                if (ev.type == EventType.MouseDrag) ev.Use();
            }
            if (!resizing) GUI.DragWindow();
        }

        void ApplyScale(float delta)
        {
            float ns = Mathf.Clamp(Mathf.Round((uiScale + delta) * 10f) / 10f, 0.6f, 1.6f);
            if (Mathf.Abs(ns - uiScale) < 0.001f) return;
            win.x = win.x * uiScale / ns;
            win.y = win.y * uiScale / ns;
            uiScale = ns;
            win.height = Mathf.Clamp(win.height, 220f, Screen.height / uiScale);
            settingsDirty = true;
        }

        void ResetColourNow()
        {
            if (mode == 0)
            {
                Snapshot(GetParts());
                List<Part> ps = GetParts();
                for (int i = 0; i < ps.Count; i++)
                {
                    ModuleReforged mod = ps[i].Modules.GetModule<ModuleReforged>();
                    if (mod != null) mod.ClearColourAndTints();
                }
            }
            else
            {
                brIcon = null; brIconTint = Color.white;
                Arm(7, Loc.T("#RDX_LblOriginalColour", "Original colour"));
            }
        }

        void RemoveMetalNow()
        {
            if (mode == 0)
            {
                Snapshot(GetParts());
                List<Part> ps = GetParts();
                for (int i = 0; i < ps.Count; i++)
                {
                    ModuleReforged mod = ps[i].Modules.GetModule<ModuleReforged>();
                    if (mod != null) mod.RemoveMetal();
                }
            }
            else
            {
                brIcon = null; brIconTint = Color.white;
                Arm(8, Loc.T("#RDX_RemoveMetal", "Remove metal"));
            }
        }

        void DrawResetRow()
        {
            GUILayout.Space(4f);
            if (tab == 1 && GUILayout.Button(Loc.T("#RDX_OriginalReset", "Original (reset colour)"), GUILayout.Height(24f))) ResetColourNow();
            if (tab == 2 && GUILayout.Button(Loc.T("#RDX_RemoveMetal", "Remove metal"), GUILayout.Height(24f))) RemoveMetalNow();
            if (tab == 3 && GUILayout.Button(Loc.T("#RDX_CleanOff", "Clean weathering off"), GUILayout.Height(24f))) ArmWeather(-1);
            if (GUILayout.Button(Loc.T("#RDX_ResetAll", "Reset All to stock"), GUILayout.Height(24f)))
            {
                Snapshot(GetParts());
                List<Part> ps = GetParts();
                int n = 0;
                for (int i = 0; i < ps.Count; i++)
                {
                    ModuleReforged mod = ps[i].Modules.GetModule<ModuleReforged>();
                    if (mod != null) { mod.ClearAll(); n++; }
                }
                ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgResetN", "Reset <<1>> parts to stock", n), 3f, ScreenMessageStyle.UPPER_CENTER);
            }
            if (GUILayout.Button(Loc.T("#RDX_ResetBrush", "Reset brush to stock"), GUILayout.Height(22f)))
            { brushKind = 6; brushLabel = Loc.T("#RDX_LblResetStock", "Reset to stock"); }
        }

        static readonly Dictionary<int, Texture2D> shineIcons = new Dictionary<int, Texture2D>();

        static Texture2D ShineIcon(float metal, float smooth)
        {
            int key = (int)(metal * 100) * 1000 + (int)(smooth * 100);
            Texture2D t;
            if (shineIcons.TryGetValue(key, out t)) return t;
            int n = 26;
            t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            Color[] px = new Color[n * n];
            float baseGrey = Mathf.Lerp(0.62f, 0.38f, metal);
            float power = Mathf.Lerp(2f, 24f, smooth);
            float specAmp = Mathf.Lerp(0.15f, 1.0f, smooth);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f;
                    float v = (y + 0.5f) / n * 2f - 1f;
                    float r2 = u * u + v * v;
                    if (r2 > 1f) { px[y * n + x] = Color.clear; continue; }
                    float z = Mathf.Sqrt(1f - r2);
                    float ndl = Mathf.Clamp01(u * -0.4f + v * 0.55f + z * 0.73f);
                    float spec = Mathf.Pow(ndl, power) * specAmp;
                    float g = baseGrey * (0.25f + 0.75f * ndl) + spec;
                    px[y * n + x] = new Color(Mathf.Clamp01(g), Mathf.Clamp01(g), Mathf.Clamp01(g), 1f);
                }
            }
            t.SetPixels(px); t.Apply();
            shineIcons[key] = t;
            return t;
        }

        static void ShineSwatch(float metal, float smooth)
        {
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Width(28f), GUILayout.Height(28f));
            GUI.DrawTexture(r, ShineIcon(metal, smooth), ScaleMode.StretchToFill, true);
        }

        static readonly Dictionary<int, Texture2D> metalIcons = new Dictionary<int, Texture2D>();
        static Texture2D MetalIcon(Color c)
        {
            int key = ((int)(c.r * 255f) << 16) | ((int)(c.g * 255f) << 8) | (int)(c.b * 255f);
            Texture2D t;
            if (metalIcons.TryGetValue(key, out t)) return t;
            int n = 28;
            t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            Color[] px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f;
                    float v = (y + 0.5f) / n * 2f - 1f;
                    float r2 = u * u + v * v;
                    if (r2 > 1f) { px[y * n + x] = Color.clear; continue; }
                    float z = Mathf.Sqrt(1f - r2);
                    float ndl = Mathf.Clamp01(u * -0.4f + v * 0.55f + z * 0.73f);
                    float env = 0.55f + 0.6f * (v * 0.5f + 0.5f);
                    float spec = Mathf.Pow(ndl, 24f) * 0.95f;
                    float br = (0.22f + 0.72f * ndl) * env;
                    px[y * n + x] = new Color(
                        Mathf.Clamp01(c.r * br + spec),
                        Mathf.Clamp01(c.g * br + spec),
                        Mathf.Clamp01(c.b * br + spec), 1f);
                }
            t.SetPixels(px); t.Apply();
            metalIcons[key] = t;
            return t;
        }
        static void MetalSwatch(int i)
        {
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Width(28f), GUILayout.Height(28f));
            GUI.DrawTexture(r, MetalIcon(new Color(MetalRGB[i][0] / 255f, MetalRGB[i][1] / 255f, MetalRGB[i][2] / 255f)), ScaleMode.StretchToFill, true);
        }

        static readonly Color[] TemperStops = {
            new Color(206f/255f, 206f/255f, 210f/255f), new Color(236f/255f, 216f/255f, 158f/255f),
            new Color(226f/255f, 182f/255f,  96f/255f), new Color(182f/255f, 122f/255f,  72f/255f),
            new Color(152f/255f,  96f/255f, 142f/255f), new Color( 72f/255f,  92f/255f, 176f/255f),
            new Color( 86f/255f, 132f/255f, 202f/255f), new Color(112f/255f, 176f/255f, 186f/255f),
            new Color(168f/255f, 192f/255f, 182f/255f) };
        static readonly Color[] GreenStops = {
            new Color(210f/255f, 206f/255f, 150f/255f), new Color(190f/255f, 200f/255f, 116f/255f),
            new Color(150f/255f, 190f/255f, 108f/255f), new Color(108f/255f, 170f/255f, 118f/255f),
            new Color( 92f/255f, 150f/255f, 140f/255f), new Color(128f/255f, 184f/255f, 150f/255f),
            new Color(168f/255f, 200f/255f, 150f/255f) };
        static readonly Color[] RoseStops = {
            new Color(222f/255f, 190f/255f, 182f/255f), new Color(216f/255f, 150f/255f, 172f/255f),
            new Color(204f/255f, 110f/255f, 152f/255f), new Color(176f/255f,  92f/255f, 152f/255f),
            new Color(150f/255f,  92f/255f, 172f/255f), new Color(188f/255f, 122f/255f, 182f/255f),
            new Color(214f/255f, 156f/255f, 192f/255f) };
        static readonly Color[] CopperStops = {
            new Color(120f/255f, 78f/255f, 58f/255f), new Color(168f/255f, 104f/255f, 66f/255f),
            new Color(214f/255f, 132f/255f, 70f/255f), new Color(232f/255f, 150f/255f, 86f/255f),
            new Color(206f/255f, 104f/255f, 78f/255f), new Color(168f/255f, 74f/255f, 92f/255f),
            new Color(128f/255f, 74f/255f, 120f/255f) };
        static readonly Color[] RainbowStops = {
            new Color(196f/255f, 176f/255f, 210f/255f), new Color(150f/255f, 118f/255f, 208f/255f),
            new Color( 92f/255f, 110f/255f, 220f/255f), new Color( 74f/255f, 168f/255f, 210f/255f),
            new Color( 96f/255f, 200f/255f, 150f/255f), new Color(216f/255f, 200f/255f, 110f/255f),
            new Color(226f/255f, 138f/255f,  96f/255f), new Color(206f/255f,  96f/255f, 150f/255f),
            new Color(150f/255f,  92f/255f, 200f/255f) };
        static readonly Color[] OverblueStops = {
            new Color(150f/255f, 170f/255f, 205f/255f), new Color( 92f/255f, 130f/255f, 202f/255f),
            new Color( 68f/255f,  92f/255f, 188f/255f), new Color(112f/255f,  86f/255f, 182f/255f),
            new Color(162f/255f,  92f/255f, 174f/255f), new Color(198f/255f, 112f/255f, 162f/255f),
            new Color(128f/255f, 108f/255f, 200f/255f), new Color( 74f/255f, 140f/255f, 196f/255f),
            new Color( 98f/255f, 178f/255f, 190f/255f) };
        static readonly Color[] WarmStops = {
            new Color(238f/255f, 224f/255f, 176f/255f), new Color(232f/255f, 200f/255f, 130f/255f),
            new Color(226f/255f, 168f/255f,  86f/255f), new Color(216f/255f, 128f/255f,  56f/255f),
            new Color(198f/255f,  92f/255f,  44f/255f), new Color(168f/255f,  60f/255f,  40f/255f),
            new Color(132f/255f,  44f/255f,  38f/255f) };
        static Color[] HeatPalette(int variant)
        {
            if (variant == 1) return GreenStops;
            if (variant == 2) return RoseStops;
            if (variant == 3) return CopperStops;
            if (variant == 4) return RainbowStops;
            if (variant == 5) return OverblueStops;
            if (variant == 7) return WarmStops;
            return TemperStops;
        }
        static Color Temper(float t, Color[] stops)
        {
            t = Mathf.Clamp01(t) * (stops.Length - 1);
            int i = (int)t;
            if (i >= stops.Length - 1) return stops[stops.Length - 1];
            return Color.Lerp(stops[i], stops[i + 1], t - i);
        }
        static readonly Dictionary<int, Texture2D> heatIcons = new Dictionary<int, Texture2D>();
        static Texture2D HeatIcon(int variant, bool linear)
        {
            Texture2D t;
            int key = variant * 2 + (linear ? 1 : 0);
            if (heatIcons.TryGetValue(key, out t)) return t;
            int n = 28;
            float off = variant * 0.9f;
            Color[] stops = HeatPalette(variant);
            t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            Color[] px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f;
                    float v = (y + 0.5f) / n * 2f - 1f;
                    float r2 = u * u + v * v;
                    if (r2 > 1f) { px[y * n + x] = Color.clear; continue; }
                    float z = Mathf.Sqrt(1f - r2);
                    float ndl = Mathf.Clamp01(u * -0.4f + v * 0.55f + z * 0.73f);
                    float temp;
                    if (linear) temp = Mathf.Clamp01(1f - (v * 0.5f + 0.5f));
                    else
                    {
                        temp = 0.5f + 0.5f * Mathf.Sin((u + off) * 3.1f + Mathf.Cos(v * 2.6f + off) * 1.7f + z * 1.9f);
                        temp = Mathf.Clamp01(temp * 0.9f + v * 0.08f + 0.05f);
                    }
                    Color b = Temper(temp, stops) * (0.3f + 0.75f * ndl);
                    float spec = Mathf.Pow(ndl, 22f) * 0.85f;
                    px[y * n + x] = new Color(
                        Mathf.Clamp01(b.r + spec), Mathf.Clamp01(b.g + spec), Mathf.Clamp01(b.b + spec), 1f);
                }
            t.SetPixels(px); t.Apply();
            heatIcons[key] = t;
            return t;
        }
        static void HeatSwatch(int i, bool linear)
        {
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Width(28f), GUILayout.Height(28f));
            GUI.DrawTexture(r, HeatIcon(i, linear), ScaleMode.StretchToFill, true);
        }

        void DrawFinishTab()
        {
            Header(Loc.T("#RDX_HdrFinishes", "Finishes"));
            if (GUILayout.Button(Loc.T("#RDX_Stock", "Stock"), GUILayout.Height(26f))) ArmFinish(Loc.T("#RDX_Stock", "Stock"), -1f, 0f);
            for (int i = 0; i < FinLabels.Length; i++)
            {
                int idx = i;
                GUILayout.BeginHorizontal();
                ShineSwatch(FinVals[i][0], FinVals[i][1]);
                if (GUILayout.Button(Loc.N(FinLabels[i]), GUILayout.Height(28f)))
                    ArmFinish(Loc.N(FinLabels[idx]), FinVals[idx][0], FinVals[idx][1]);
                GUILayout.EndHorizontal();
            }
            Header(Loc.T("#RDX_HdrMyFinishes", "My finishes"));
            DrawUserPresets(userFinish, false);
            if (GUILayout.Button(buildingFinish ? Loc.T("#RDX_CloseBuilder", "Close builder") : Loc.T("#RDX_CreateFinishPreset", "Create finish preset +"), GUILayout.Height(26f)))
                buildingFinish = !buildingFinish;
            if (buildingFinish) DrawFinishBuilder();
            if (DrawRegion()) ReArm();
            DrawRegionTarget();
            DrawKeepShiny();
        }

        void DrawFinishBuilder()
        {
            float m = SliderNum(Loc.T("#RDX_Metal", "Metal"), bMetal, 0f, 1f, ref bufMetal);
            float s = SliderNum(Loc.T("#RDX_Shine", "Shine"), bSmooth, 0f, 1f, ref bufShine);
            if (m != bMetal || s != bSmooth)
            {
                bMetal = m; bSmooth = s;
                PreviewFinishLive();
            }
            newName = GUILayout.TextField(newName);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("#RDX_Apply", "Apply"))) ArmFinish(Loc.T("#RDX_LblCustomFinish", "Custom finish"), bMetal, bSmooth);
            if (GUILayout.Button(Loc.T("#RDX_Save", "Save")))
            {
                Preset pr = new Preset();
                pr.name = string.IsNullOrEmpty(newName) ? Loc.T("#RDX_DefPreset", "My preset") : newName;
                pr.isColor = false; pr.metal = bMetal; pr.smooth = bSmooth;
                for (int i = userFinish.Count - 1; i >= 0; i--) if (userFinish[i].name == pr.name) userFinish.RemoveAt(i);
                userFinish.Add(pr); SavePresets();
            }
            if (GUILayout.Button(Loc.T("#RDX_Reset", "Reset"))) { bMetal = 0f; bSmooth = 0f; bufMetal = bufShine = null; PreviewFinishLive(); }
            GUILayout.EndHorizontal();
            Hint(mode == 0 ? Loc.T("#RDX_HintDragVessel", "Drag to preview on the whole vessel.") : Loc.T("#RDX_HintDragPart", "Point at a part, drag to preview."));
        }

        void PreviewFinishLive()
        {
            List<Part> targets = (mode == 0) ? GetParts() : ((lastHovered != null) ? new List<Part> { lastHovered } : new List<Part>());
            for (int i = 0; i < targets.Count; i++)
            {
                ModuleReforged mod = targets[i].Modules.GetModule<ModuleReforged>();
                if (mod != null) mod.SetFinish(bMetal, bSmooth);
            }
        }

        void DrawUserPresets(List<Preset> list, bool isColor)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Preset pr = list[i];
                GUILayout.BeginHorizontal();
                if (isColor) Swatch(new Color(pr.r, pr.g, pr.b));
                if (GUILayout.Button(pr.name, GUILayout.Height(26f)))
                {
                    if (isColor) ArmColour(new Color(pr.r, pr.g, pr.b), pr.name);
                    else ArmFinish(pr.name, pr.metal, pr.smooth);
                }
                if (GUILayout.Button("X", GUILayout.Width(26f), GUILayout.Height(26f)))
                { list.RemoveAt(i); SavePresets(); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
            }
        }

        bool uiKeepShiny = true;

        void DrawKeepShiny()
        {
            uiKeepShiny = GUILayout.Toggle(uiKeepShiny, "<b>" + Loc.T("#RDX_KeepShiny", "Keep shiny areas original") + "</b>", toggleBold);
            Hint(Loc.T("#RDX_HintKeepShiny", "Shiny spots (windows/chrome) stay unpainted;\ncolour & dirt fill around them."));
        }

        void DrawColourTab()
        {
            if (DrawRegion()) ReArm();
            DrawRegionTarget();
            DrawKeepShiny();
            int cmode = WeatherCache.ColorReplace ? 0 : 1;
            int ncmode = GUILayout.Toolbar(cmode, new string[] { Loc.T("#RDX_Replace", "Replace"), Loc.T("#RDX_Overlay", "Overlay") });
            if (ncmode != cmode)
            {
                WeatherCache.ColorReplace = (ncmode == 0);
                settingsDirty = true;
                ReArm();
            }
            Hint(Loc.T("#RDX_HintColorModes", "Replace: full recolour, can turn a black part any colour.\nOverlay: tints the texture like the old style, cannot\nlighten or recolour dark/black parts."));
            if (WeatherCache.ColorReplace)
            {
                bool resetDetail;
                float nd = SliderNum(Loc.T("#RDX_Detail", "Detail"), WeatherCache.PaintDetail * 100f, 0f, 150f, ref detailBuf, Loc.T("#RDX_ResetShort", "Reset"), out resetDetail);
                if (resetDetail) nd = 100f;
                if (Mathf.RoundToInt(nd) != Mathf.RoundToInt(WeatherCache.PaintDetail * 100f))
                {
                    WeatherCache.PaintDetail = nd / 100f;
                    detailBuf = null;
                    settingsDirty = true;
                }
                Hint(Loc.T("#RDX_HintDetail", "0 = solid colour. Higher keeps hard details\n(panel lines, edges); flat areas stay solid."));
            }
            Header(Loc.T("#RDX_HdrColours", "Colours"));
            float colAvail = Mathf.Clamp(win.height - (buildingColor ? 560f : 310f), 150f, 1200f);
            float colH = (colContentH > 0f) ? Mathf.Min(colContentH + 4f, colAvail) : colAvail;
            colScroll = GUILayout.BeginScrollView(colScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(colH));
            float colCw;
            int colCols = GridCols(ColWs, out colCw);
            for (int i = 0; i < palNames.Count; i += colCols)
            {
                GUILayout.BeginHorizontal();
                for (int k = 0; k < colCols; k++)
                {
                    int idx = i + k;
                    if (idx >= palNames.Count) { GUILayout.Space(colCw); continue; }
                    GUILayout.BeginHorizontal(GUILayout.Width(colCw));
                    Swatch(palCols[idx]);
                    if (GUILayout.Button(Loc.N(palNames[idx]), GUILayout.Height(28f))) ArmColour(palCols[idx], Loc.N(palNames[idx]));
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndHorizontal();
            }
            if (palNames.Count > 0 && Event.current.type == EventType.Repaint) colContentH = GUILayoutUtility.GetLastRect().yMax;
            GUILayout.EndScrollView();
            Header(Loc.T("#RDX_HdrMyColours", "My colours"));
            DrawUserPresets(userColor, true);
            if (GUILayout.Button(buildingColor ? Loc.T("#RDX_CloseBuilder", "Close builder") : Loc.T("#RDX_CreateColourPreset", "Create colour preset +"), GUILayout.Height(26f)))
                buildingColor = !buildingColor;
            if (buildingColor) DrawColorBuilder();
        }

        Color BuilderColor()
        {
            Color c = Color.HSVToRGB(pH, pS, pV);
            return new Color(Mathf.Min(c.r * pBright, 3f), Mathf.Min(c.g * pBright, 3f), Mathf.Min(c.b * pBright, 3f));
        }

        void LoadPickerColour(Color c)
        {
            Color.RGBToHSV(new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b)), out pH, out pS, out pV);
            pBright = Mathf.Max(1f, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
            bufR = bufG = bufB = bufBright = hexBuf = null;
            svHueKey = -1;
        }

        static bool TryHex(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.TrimStart('#');
            if (s.Length != 6) return false;
            try
            {
                int r = Convert.ToInt32(s.Substring(0, 2), 16);
                int g = Convert.ToInt32(s.Substring(2, 2), 16);
                int b = Convert.ToInt32(s.Substring(4, 2), 16);
                c = new Color(r / 255f, g / 255f, b / 255f);
                return true;
            }
            catch { return false; }
        }

        void EnsurePickerTex()
        {
            if (hueTex == null)
            {
                int w = 120, h = 12;
                hueTex = new Texture2D(w, h);
                Color[] px = new Color[w * h];
                for (int x = 0; x < w; x++)
                {
                    Color hc = Color.HSVToRGB(x / (float)(w - 1), 1f, 1f);
                    for (int y = 0; y < h; y++) px[y * w + x] = hc;
                }
                hueTex.SetPixels(px); hueTex.Apply();
            }
            if (markTex == null) { markTex = new Texture2D(1, 1); markTex.SetPixel(0, 0, Color.white); markTex.Apply(); }
            int key = Mathf.RoundToInt(pH * 360f);
            if (svTex == null || key != svHueKey)
            {
                svHueKey = key;
                int nx = 190, ny = 130;
                if (svTex == null) svTex = new Texture2D(nx, ny);
                Color[] px = new Color[nx * ny];
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        px[y * nx + x] = Color.HSVToRGB(pH, x / (float)(nx - 1), y / (float)(ny - 1));
                svTex.SetPixels(px); svTex.Apply();
            }
        }

        float colPrevTime;
        bool colPrevPending;
        bool colPrevWasDrag;
        Color colPrevCol = Color.white;

        void PreviewColourLive()
        {
            colPrevCol = BuilderColor();
            colPrevPending = true;
            FlushColourPreview();
        }

        void FlushColourPreview()
        {
            bool held = Input.GetMouseButton(0);
            float now = Time.realtimeSinceStartup;
            if (colPrevPending && (!held || now - colPrevTime >= 0.12f))
            {
                colPrevTime = now;
                colPrevPending = false;
                colPrevWasDrag = held;
                WeatherCache.SkipDiskWrite = held;
                try { ApplyColourPreview(held && mode == 0); }
                finally { WeatherCache.SkipDiskWrite = false; }
            }
            else if (!held && colPrevWasDrag)
            {
                colPrevWasDrag = false;
                ApplyColourPreview(false);
            }
        }

        static bool IsColorTok(string t)
        {
            return t != null && (t.StartsWith("tint#") || t.StartsWith("paint#") || t.StartsWith("omul#"));
        }
        static bool StackHasColorTok(string st)
        {
            if (string.IsNullOrEmpty(st)) return false;
            string[] es = st.Split(';');
            for (int i = 0; i < es.Length; i++) if (es[i].StartsWith("paint#") || es[i].StartsWith("omul#")) return true;
            return false;
        }
        static bool StackHasTexLayer(string st)
        {
            if (string.IsNullOrEmpty(st)) return false;
            string[] es = st.Split(';');
            for (int i = 0; i < es.Length; i++) if (es[i].Length > 0 && !IsColorTok(es[i])) return true;
            return false;
        }

        void ApplyColourPreview(bool hoveredOnly)
        {
            List<Part> targets = (hoveredOnly || mode != 0)
                ? ((lastHovered != null) ? new List<Part> { lastHovered } : new List<Part>())
                : GetParts();
            for (int i = 0; i < targets.Count; i++)
            {
                ModuleReforged mod = targets[i].Modules.GetModule<ModuleReforged>();
                if (mod != null) { mod.rdxPaintDetail = WeatherCache.PaintDetail; mod.SetColour(colPrevCol); }
            }
        }

        void DrawColorBuilder()
        {
            bool pickerChanged = false;
            EnsurePickerTex();
            GUILayout.BeginHorizontal();
            Swatch(BuilderColor());
            if (GUILayout.Button(eyedrop ? Loc.T("#RDX_ClickBang", "click!") : Loc.T("#RDX_PickBtn", "pick"), GUILayout.Width(48f)))
            {
                eyedrop = true;
                ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgEyedrop", "Eyedropper: click anything on screen"), 3f, ScreenMessageStyle.UPPER_CENTER);
            }
            GUILayout.Label("#", builderLabel, GUILayout.Width(14f));
            string curHex = ModuleReforged.HexRGB(Color.HSVToRGB(pH, pS, pV));
            string shown = (hexBuf != null) ? hexBuf : curHex;
            string hx = GUILayout.TextField(shown, GUILayout.Width(84f));
            if (hx != shown)
            {
                hexBuf = hx;
                Color pc;
                if (TryHex(hx, out pc)) { Color.RGBToHSV(pc, out pH, out pS, out pV); svHueKey = -1; pickerChanged = true; }
            }
            else if (hexBuf != null && shown == curHex) hexBuf = null;
            GUILayout.EndHorizontal();

            float pw = Mathf.Clamp(win.width - 34f, 180f, 660f);
            float ph = pw * 0.6f;
            Rect sq = GUILayoutUtility.GetRect(pw, ph, GUILayout.Width(pw), GUILayout.Height(ph));
            GUI.DrawTexture(sq, svTex, ScaleMode.StretchToFill);
            Rect mark = new Rect(sq.x + pS * sq.width - 3, sq.y + (1f - pV) * sq.height - 3, 6, 6);
            GUI.DrawTexture(mark, markTex);
            Event pev = Event.current;
            if (pev.type == EventType.MouseUp) { svDrag = false; hueDrag = false; }
            if (pev.type == EventType.MouseDown && sq.Contains(pev.mousePosition)) svDrag = true;
            if (svDrag && (pev.type == EventType.MouseDown || pev.type == EventType.MouseDrag))
            {
                pS = Mathf.Clamp01((pev.mousePosition.x - sq.x) / sq.width);
                pV = Mathf.Clamp01(1f - (pev.mousePosition.y - sq.y) / sq.height);
                bufR = bufG = bufB = hexBuf = null;
                pickerChanged = true;
                pev.Use();
            }
            GUILayout.Space(4f);

            Rect hb = GUILayoutUtility.GetRect(pw, 18f, GUILayout.Width(pw), GUILayout.Height(18f));
            GUI.DrawTexture(hb, hueTex, ScaleMode.StretchToFill);
            Rect hm = new Rect(hb.x + pH * hb.width - 1, hb.y, 3, hb.height);
            GUI.DrawTexture(hm, markTex);
            if (pev.type == EventType.MouseDown && hb.Contains(pev.mousePosition)) hueDrag = true;
            if (hueDrag && (pev.type == EventType.MouseDown || pev.type == EventType.MouseDrag))
            {
                pH = Mathf.Clamp01((pev.mousePosition.x - hb.x) / hb.width);
                svHueKey = -1;
                bufR = bufG = bufB = hexBuf = null;
                pickerChanged = true;
                pev.Use();
            }

            Color c2 = Color.HSVToRGB(pH, pS, pV);
            float R = SliderNum("R", c2.r * 255f, 0f, 255f, ref bufR);
            float G = SliderNum("G", c2.g * 255f, 0f, 255f, ref bufG);
            float B = SliderNum("B", c2.b * 255f, 0f, 255f, ref bufB);
            if (Mathf.RoundToInt(R) != Mathf.RoundToInt(c2.r * 255f) ||
                Mathf.RoundToInt(G) != Mathf.RoundToInt(c2.g * 255f) ||
                Mathf.RoundToInt(B) != Mathf.RoundToInt(c2.b * 255f))
            {
                Color.RGBToHSV(new Color(R / 255f, G / 255f, B / 255f), out pH, out pS, out pV);
                hexBuf = null; svHueKey = -1;
                pickerChanged = true;
            }
            float br = SliderNum(Loc.T("#RDX_Brightness", "Brightness"), pBright, 0f, 3f, ref bufBright);
            if (br != pBright) { pBright = br; pickerChanged = true; }
            if (pickerChanged) PreviewColourLive();

            newName = GUILayout.TextField(newName);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("#RDX_Apply", "Apply"))) ArmColour(BuilderColor(), Loc.T("#RDX_LblCustomColour", "Custom colour"));
            if (GUILayout.Button(Loc.T("#RDX_Save", "Save")))
            {
                Preset pr = new Preset();
                pr.name = string.IsNullOrEmpty(newName) ? Loc.T("#RDX_DefPreset", "My preset") : newName;
                pr.isColor = true;
                Color cc = BuilderColor();
                pr.r = cc.r; pr.g = cc.g; pr.b = cc.b;
                for (int i = userColor.Count - 1; i >= 0; i--) if (userColor[i].name == pr.name) userColor.RemoveAt(i);
                userColor.Add(pr); SavePresets();
            }
            if (GUILayout.Button(Loc.T("#RDX_Reset", "Reset"))) { pH = 0f; pS = 0f; pV = 1f; pBright = 1f; bufR = bufG = bufB = bufBright = hexBuf = null; svHueKey = -1; PreviewColourLive(); }
            GUILayout.EndHorizontal();
            Hint(Loc.T("#RDX_HintBrightness", "Brightness above 1 pushes past the texture."));
        }

        System.Collections.IEnumerator SamplePixel(Vector3 mousePos)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                Texture2D px = new Texture2D(1, 1, TextureFormat.RGB24, false);
                px.ReadPixels(new Rect(mousePos.x, mousePos.y, 1, 1), 0, 0);
                px.Apply(false, false);
                Color c = px.GetPixel(0, 0);
                Destroy(px);
                LoadPickerColour(c);
                ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgPickedColour", "Picked #<<1>>", ModuleReforged.HexRGB(c)), 3f, ScreenMessageStyle.UPPER_CENTER);
            }
            catch (Exception e) { Debug.LogWarning("[ReforgedRedux] eyedropper failed: " + e.Message); }
        }

        void DrawMetalsTab()
        {
            Header(Loc.T("#RDX_HdrMetals", "Metals"));
            Hint(Loc.T("#RDX_HintMetals", "Full metal finish + colour in one click."));
            if (DrawRegion()) ReArm();
            DrawKeepShiny();
            DrawDetailThroughTex();
            for (int i = 0; i < MetalNames.Length; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int k = 0; k < 2; k++)
                {
                    int idx = i + k;
                    if (idx >= MetalNames.Length) { GUILayout.Space(ColW); continue; }
                    GUILayout.BeginHorizontal(GUILayout.Width(ColW));
                    MetalSwatch(idx);
                    if (GUILayout.Button(Loc.N(MetalNames[idx]), GUILayout.Height(28f))) ArmMetal(idx);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndHorizontal();
            }
            Header(Loc.T("#RDX_HdrHeat", "Heat Discoloration"));
            Hint(Loc.T("#RDX_HintHeat", "Real temper colours plus a polished finish."));
            int nhv = GUILayout.Toolbar(heatVariant, new string[] { Loc.T("#RDX_Mixed", "Mixed"), Loc.T("#RDX_Linear", "Linear") });
            if (nhv != heatVariant)
            {
                heatVariant = nhv;
                ReArm();
            }
            if (DrawStrength()) ReArm();
            {
                float nhs = SliderNum(Loc.T("#RDX_PatternSize", "Pattern size"), regPatScale, 0.25f, 3f, ref regScaleBuf);
                if (nhs != regPatScale) { regPatScale = nhs; ReArm(); }
            }
            for (int i = 0; i < HeatNames.Length; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int k = 0; k < 2; k++)
                {
                    int idx = i + k;
                    if (idx >= HeatNames.Length) { GUILayout.Space(ColW); continue; }
                    GUILayout.BeginHorizontal(GUILayout.Width(ColW));
                    HeatSwatch(idx, heatVariant == 1);
                    if (GUILayout.Button(Loc.N(HeatNames[idx]), GUILayout.Height(28f))) ArmHeatDiscolor(idx);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndHorizontal();
            }
        }

        void DrawDirtTab()
        {
            EnsureCustomScan();
            Header(Loc.T("#RDX_HdrWeathering", "Weathering"));
            if (DrawStrength()) ReArm();
            if (DrawPatternSize()) ReArm();
            if (DrawRegion()) ReArm();
            DrawKeepShiny();
            DrawDetailThroughTex();
            if (pinned.Count > 0)
            {
                GUILayout.Label("<b>" + Loc.T("#RDX_Favorites", "Favorites") + "</b>", builderLabel);
                float favCw;
                int favCols = GridCols(ColW, out favCw);
                for (int pi = 0; pi < pinned.Count; pi += favCols)
                {
                    GUILayout.BeginHorizontal();
                    for (int k = 0; k < favCols; k++)
                    {
                        int idx = pi + k;
                        if (idx >= pinned.Count) { GUILayout.Space(favCw); continue; }
                        int a = WeatherFiles.IndexOf(pinned[idx]);
                        if (a < 0) { GUILayout.Space(favCw); continue; }
                        if (GUILayout.Button(Loc.N(WeatherLabels[a]), GUILayout.Width(favCw), GUILayout.Height(26f))) ArmWeather(a);
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(4f);
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("#RDX_Search", "Search:"), hintStyle, GUILayout.Width(48f));
            dirtSearch = GUILayout.TextField(dirtSearch, GUILayout.Height(20f));
            if (dirtSearch.Length > 0 && GUILayout.Button("x", GUILayout.Width(22f), GUILayout.Height(20f))) dirtSearch = "";
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("#RDX_AllPatterns", "All patterns (tick = pin a favorite):"), hintStyle);
            float dirtAvail = Mathf.Clamp(win.height - 330f, 150f, 1200f);
            float dirtH = (dirtContentH > 0f) ? Mathf.Min(dirtContentH + 4f, dirtAvail) : dirtAvail;
            dirtScroll = GUILayout.BeginScrollView(dirtScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(dirtH));
            string dq = dirtSearch.ToLowerInvariant();
            int dirtDrawn = 0;
            for (int i = 0; i < WeatherLabels.Count; i++)
            {
                if (dq.Length > 0 && WeatherLabels[i].ToLowerInvariant().IndexOf(dq) < 0 && Loc.N(WeatherLabels[i]).ToLowerInvariant().IndexOf(dq) < 0) continue;
                GUILayout.BeginHorizontal();
                bool wasPin = pinned.Contains(WeatherFiles[i]);
                bool nowPin = GUILayout.Toggle(wasPin, "", GUILayout.Width(20f));
                if (nowPin != wasPin)
                {
                    if (nowPin) pinned.Add(WeatherFiles[i]); else pinned.Remove(WeatherFiles[i]);
                    SavePresets();
                }
                if (GUILayout.Button(Loc.N(WeatherLabels[i]), GUILayout.Height(26f))) ArmWeather(i);
                GUILayout.EndHorizontal();
                dirtDrawn++;
            }
            if (Event.current.type == EventType.Repaint) dirtContentH = (dirtDrawn > 0) ? GUILayoutUtility.GetLastRect().yMax : 24f;
            GUILayout.EndScrollView();
            Hint(Loc.T("#RDX_HintStack", "Effects stack. Colourless textures take any\ntint below or a colour from the Colour tab."));
            GUILayout.Space(4f);
            Header(Loc.T("#RDX_HdrTinted", "Tinted worn looks"));
            float tintCw;
            int tintCols = GridCols(ColW, out tintCw);
            for (int i = 0; i < DirtNames.Length; i += tintCols)
            {
                GUILayout.BeginHorizontal();
                for (int k = 0; k < tintCols; k++)
                {
                    int idx = i + k;
                    if (idx >= DirtNames.Length) { GUILayout.Space(tintCw); continue; }
                    GUILayout.BeginHorizontal(GUILayout.Width(tintCw));
                    Swatch(new Color(DirtRGB[idx][0] / 255f, DirtRGB[idx][1] / 255f, DirtRGB[idx][2] / 255f));
                    if (GUILayout.Button(Loc.N(DirtNames[idx]), GUILayout.Height(28f))) ArmDirtTint(idx);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndHorizontal();
            }
        }

        void DrawDetailThroughTex()
        {
            bool nts = GUILayout.Toggle(WeatherCache.TexSkipDetail, Loc.T("#RDX_DetailThroughTex", "Detail through textures"));
            if (nts != WeatherCache.TexSkipDetail) { WeatherCache.TexSkipDetail = nts; settingsDirty = true; }
            Hint(Loc.T("#RDX_HintDetailTex", "On: panel lines and edges stay visible through\nweathering. Off: textures cover everything."));
        }

        void DrawComboTab()
        {
            Header(Loc.T("#RDX_HdrSavedCombos", "Saved combos"));
            Hint(Loc.T("#RDX_HintCombos", "Set a part up on the other tabs, then save it here."));
            if (DrawRegion()) ReArm();
            DrawRegionTarget();
            DrawKeepShiny();
            DrawDetailThroughTex();
            if (DrawStrength()) ReArm();
            if (DrawPatternSize()) ReArm();
            if (userCombo.Count == 0) GUILayout.Label(Loc.T("#RDX_NoCombos", "No saved combos yet."), hintStyle);
            for (int ci = 0; ci < userCombo.Count; ci++)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(userCombo[ci].name, GUILayout.Height(26f))) ArmCombo(userCombo[ci], true);
                if (GUILayout.Button("X", GUILayout.Width(26f), GUILayout.Height(26f)))
                { userCombo.RemoveAt(ci); SavePresets(); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(6f);
            Header(Loc.T("#RDX_HdrSaveCombo", "Save a new combo"));
            comboName = GUILayout.TextField(comboName);
            if (GUILayout.Button(Loc.T("#RDX_SaveHovered", "Save hovered part's combo +"), GUILayout.Height(26f))) SaveComboFromHovered();
            Hint(Loc.T("#RDX_HintPointPainted", "Point at a painted part first, then click."));
        }

        static string StripToRecipe(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return "";
            string[] es = stack.Split(';');
            List<string> names = new List<string>();
            for (int i = 0; i < es.Length; i++)
            {
                int c = es[i].IndexOf(':');
                string nm = c > 0 ? es[i].Substring(0, c) : es[i];
                if (string.IsNullOrEmpty(nm)) continue;
                names.Add(nm);
            }
            return string.Join(";", names.ToArray());
        }

        void SaveComboFromHovered()
        {
            ModuleReforged mod = (lastHovered != null) ? lastHovered.Modules.GetModule<ModuleReforged>() : null;
            if (mod == null || !mod.Active)
            { ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgPointPainted", "Point at a painted part first"), 3f, ScreenMessageStyle.UPPER_CENTER); return; }
            Combo c = new Combo();
            c.name = string.IsNullOrEmpty(comboName) ? Loc.T("#RDX_DefCombo", "My combo") : comboName;
            c.recipe = mod.ComboRecipe();
            float cm, csm; c.hasFinish = mod.ComboFinish(out cm, out csm); c.metal = cm; c.smooth = csm;
            float rr, gg, bb; c.hasColor = mod.ComboColor(out rr, out gg, out bb); c.r = rr; c.g = gg; c.b = bb;
            c.scale = mod.rdxScale;
            for (int i = userCombo.Count - 1; i >= 0; i--) if (userCombo[i].name == c.name) userCombo.RemoveAt(i);
            userCombo.Add(c); SavePresets();
            comboName = ""; GUIUtility.keyboardControl = 0;
            ScreenMessages.PostScreenMessage(Loc.T("#RDX_MsgSavedCombo", "Saved combo: <<1>>", c.name), 3f, ScreenMessageStyle.UPPER_CENTER);
        }

        static string PresetsPath()
        {
            return KSPUtil.ApplicationRootPath + "GameData/ReforgedRedux/PluginData/Presets.cfg";
        }

        static void AddDefaultFinish()
        {
            if (userFinish.Count > 0) return;
            Preset f = new Preset();
            f.name = "J4COB's most used";
            f.isColor = false; f.metal = 0.75f; f.smooth = 0.82f;
            userFinish.Add(f);
        }
        static void AddDefaultCombos()
        {
            AddDefaultFinish();
            if (userCombo.Count == 0)
            {
                userCombo.Add(new Combo { name = "J4COB's Soot Marks", recipe = "grunge;soot;leak" });
                userCombo.Add(new Combo { name = "J4COB's Reentry Burn", recipe = "grimymetal;burn;scorch;leak;soot" });
            }
        }

        static void LoadPresets()
        {
            try
            {
                string path = PresetsPath();
                if (!System.IO.File.Exists(path)) { AddDefaultCombos(); return; }
                ConfigNode root = ConfigNode.Load(path);
                if (root == null) { AddDefaultCombos(); return; }
                { string sv = root.GetValue("showText"); if (sv != null) showHints = sv != "False"; }
                { float pdv; if (float.TryParse(root.GetValue("paintDetail"), out pdv) && pdv >= 0f) WeatherCache.PaintDetail = Mathf.Clamp(pdv, 0f, 1.5f); }
                { string cr = root.GetValue("colorReplace"); if (cr != null) WeatherCache.ColorReplace = cr != "False"; }
                { string ts = root.GetValue("detailThroughTex"); if (ts != null) WeatherCache.TexSkipDetail = ts == "True"; }
                { float ww; if (float.TryParse(root.GetValue("winW"), out ww) && ww > 0f) savedWinW = Mathf.Clamp(ww, 300f, 700f); }
                { float wh; if (float.TryParse(root.GetValue("winH"), out wh) && wh > 0f) savedWinH = Mathf.Max(220f, wh); }
                { float us; if (float.TryParse(root.GetValue("uiScale"), out us) && us > 0f) uiScale = Mathf.Clamp(us, 0.6f, 1.6f); }
                { string gl = root.GetValue("gradualLoad"); if (gl != null) ModuleReforged.DeferLoadBakes = gl == "True"; }
                { string es = root.GetValue("evenShine"); if (es != null) ModuleReforged.EvenShine = es != "False"; }
                { string tw = root.GetValue("heatTileWeight"); if (tw != null) ModuleReforged.HeatTileWeight = tw != "False"; }
                { string th = root.GetValue("heatTileHeat"); if (th != null) ModuleReforged.HeatTileHeat = th != "False"; }
                { string lv = root.GetValue("lowVram"); if (lv != null) WeatherCache.LowVram = lv == "True"; }
                { int cl; if (int.TryParse(root.GetValue("cacheLimitMB"), out cl) && Array.IndexOf(CacheLimits, cl) >= 0) WeatherCache.CacheLimitMB = cl; }
                { string sy = root.GetValue("symPaint"); if (sy != null) paintSym = sy != "False"; }
                ModuleReforged.IgnoredParts.Clear();
                ConfigNode[] igs = root.GetNodes("REDUX_IGNORE");
                for (int i = 0; i < igs.Length; i++)
                {
                    string ipn = igs[i].GetValue("name");
                    if (!string.IsNullOrEmpty(ipn)) ModuleReforged.IgnoredParts.Add(ipn);
                }
                for (int bi = 0; bi < binds.Length; bi++) BindLoad(binds[bi], root.GetValue("key_" + binds[bi].id));
                userFinish.Clear(); userColor.Clear(); userCombo.Clear(); pinned.Clear();
                ConfigNode[] cs = root.GetNodes("REDUX_COMBO");
                for (int i = 0; i < cs.Length; i++)
                {
                    Combo cb = new Combo();
                    cb.name = cs[i].GetValue("name");
                    cb.recipe = cs[i].GetValue("recipe");
                    if (string.IsNullOrEmpty(cb.recipe)) cb.recipe = StripToRecipe(cs[i].GetValue("stack"));
                    cb.scale = 1f; { float sc; if (float.TryParse(cs[i].GetValue("scale"), out sc) && sc > 0f) cb.scale = sc; }
                    cb.hasFinish = cs[i].GetValue("hasFinish") == "True";
                    float.TryParse(cs[i].GetValue("metal"), out cb.metal);
                    float.TryParse(cs[i].GetValue("smooth"), out cb.smooth);
                    cb.hasColor = cs[i].GetValue("hasColor") == "True";
                    float.TryParse(cs[i].GetValue("r"), out cb.r);
                    float.TryParse(cs[i].GetValue("g"), out cb.g);
                    float.TryParse(cs[i].GetValue("b"), out cb.b);
                    if (!string.IsNullOrEmpty(cb.name) && (!string.IsNullOrEmpty(cb.recipe) || cb.hasFinish || cb.hasColor))
                        userCombo.Add(cb);
                }
                if (cs.Length == 0) AddDefaultCombos();
                ConfigNode[] ns = root.GetNodes("REDUX_PRESET");
                for (int i = 0; i < ns.Length; i++)
                {
                    Preset pr = new Preset();
                    pr.name = ns[i].GetValue("name");
                    pr.isColor = ns[i].GetValue("type") == "color";
                    float.TryParse(ns[i].GetValue("metal"), out pr.metal);
                    float.TryParse(ns[i].GetValue("smooth"), out pr.smooth);
                    float.TryParse(ns[i].GetValue("r"), out pr.r);
                    float.TryParse(ns[i].GetValue("g"), out pr.g);
                    float.TryParse(ns[i].GetValue("b"), out pr.b);
                    if (string.IsNullOrEmpty(pr.name)) continue;
                    if (pr.isColor) userColor.Add(pr); else userFinish.Add(pr);
                }
                AddDefaultFinish();
                ConfigNode[] ps = root.GetNodes("REDUX_PIN");
                for (int i = 0; i < ps.Length; i++)
                {
                    string pn = ps[i].GetValue("name");
                    if (!string.IsNullOrEmpty(pn) && !pinned.Contains(pn)) pinned.Add(pn);
                }
                if (ps.Length == 0) pinned.AddRange(DefaultPins);
            }
            catch (Exception e) { Debug.LogError("[ReforgedRedux] load presets failed: " + e.Message); }
        }

        static void SavePresets()
        {
            try
            {
                ConfigNode root = new ConfigNode();
                for (int i = 0; i < userFinish.Count; i++) AddPresetNode(root, userFinish[i]);
                for (int i = 0; i < userColor.Count; i++) AddPresetNode(root, userColor[i]);
                for (int i = 0; i < userCombo.Count; i++)
                {
                    ConfigNode cn = root.AddNode("REDUX_COMBO");
                    cn.AddValue("name", userCombo[i].name);
                    cn.AddValue("recipe", userCombo[i].recipe);
                    cn.AddValue("scale", userCombo[i].scale);
                    if (userCombo[i].hasFinish)
                    {
                        cn.AddValue("hasFinish", "True");
                        cn.AddValue("metal", userCombo[i].metal);
                        cn.AddValue("smooth", userCombo[i].smooth);
                    }
                    if (userCombo[i].hasColor)
                    {
                        cn.AddValue("hasColor", "True");
                        cn.AddValue("r", userCombo[i].r);
                        cn.AddValue("g", userCombo[i].g);
                        cn.AddValue("b", userCombo[i].b);
                    }
                }
                for (int i = 0; i < pinned.Count; i++)
                    root.AddNode("REDUX_PIN").AddValue("name", pinned[i]);
                root.AddValue("showText", showHints);
                root.AddValue("paintDetail", WeatherCache.PaintDetail);
                root.AddValue("colorReplace", WeatherCache.ColorReplace);
                root.AddValue("detailThroughTex", WeatherCache.TexSkipDetail);
                root.AddValue("winW", savedWinW);
                root.AddValue("winH", savedWinH);
                root.AddValue("uiScale", uiScale);
                root.AddValue("gradualLoad", ModuleReforged.DeferLoadBakes);
                root.AddValue("evenShine", ModuleReforged.EvenShine);
                root.AddValue("heatTileWeight", ModuleReforged.HeatTileWeight);
                root.AddValue("heatTileHeat", ModuleReforged.HeatTileHeat);
                root.AddValue("lowVram", WeatherCache.LowVram);
                root.AddValue("cacheLimitMB", WeatherCache.CacheLimitMB);
                root.AddValue("symPaint", paintSym);
                foreach (string ipn in ModuleReforged.IgnoredParts) root.AddNode("REDUX_IGNORE").AddValue("name", ipn);
                for (int bi = 0; bi < binds.Length; bi++) root.AddValue("key_" + binds[bi].id, BindSave(binds[bi]));
                string path = PresetsPath();
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                root.Save(path);
            }
            catch (Exception e) { Debug.LogError("[ReforgedRedux] save presets failed: " + e.Message); }
        }

        static void AddPresetNode(ConfigNode root, Preset pr)
        {
            ConfigNode n = root.AddNode("REDUX_PRESET");
            n.AddValue("name", pr.name);
            n.AddValue("type", pr.isColor ? "color" : "finish");
            n.AddValue("metal", pr.metal);
            n.AddValue("smooth", pr.smooth);
            n.AddValue("r", pr.r);
            n.AddValue("g", pr.g);
            n.AddValue("b", pr.b);
        }
    }

    internal static class Loc
    {
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        static readonly Dictionary<string, string> nameKeys = new Dictionary<string, string>();

        internal static string T(string key, string en)
        {
            string v;
            if (cache.TryGetValue(key, out v)) return v;
            string found = null;
            try { if (!KSP.Localization.Localizer.TryGetStringByTag(key, out found)) found = null; }
            catch { found = null; }
            v = string.IsNullOrEmpty(found) ? en : found;
            v = v.Replace("\\n", "\n");
            cache[key] = v;
            return v;
        }

        internal static string T(string key, string en, params object[] args)
        {
            string v = T(key, en);
            for (int i = 0; i < args.Length; i++)
                v = v.Replace("<<" + (i + 1) + ">>", args[i] == null ? "" : args[i].ToString());
            return v;
        }

        internal static string N(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string key;
            if (!nameKeys.TryGetValue(name, out key))
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder("#RDX_N_");
                for (int i = 0; i < name.Length; i++) if (char.IsLetterOrDigit(name[i])) sb.Append(name[i]);
                key = sb.ToString();
                nameKeys[name] = key;
            }
            return T(key, name);
        }
    }

    internal static class WeatherCache
    {
        const string OverlayPath = "ReforgedRedux/Weather/";
        const string Tag = "[ReforgedRedux] ";

        static readonly Dictionary<int, Texture> bakedToOrig = new Dictionary<int, Texture>();
        static readonly Dictionary<string, Texture2D> baked = new Dictionary<string, Texture2D>();
        static readonly Dictionary<string, Texture2D> bakedBump = new Dictionary<string, Texture2D>();
        class BakeMem
        {
            public string spec;
            public Color32[] main;
            public Color32[] bump;
            public int w, h;
        }
        static readonly Dictionary<string, BakeMem> lastBake = new Dictionary<string, BakeMem>();
        static readonly List<string> lastOrder = new List<string>();
        const int BakeMemCap = 2;

        static float PatternStrength(string name)
        {
            if (name.StartsWith("camo")) return 1.6f;
            if (name == "soot" || name == "burn" || name == "grime" || name == "grunge") return 1.7f;
            if (name.StartsWith("scorch")) return 1.4f;
            return 1f;
        }
        static readonly Dictionary<int, Texture> bumpToOrig = new Dictionary<int, Texture>();
        static readonly HashSet<int> bumpSynth = new HashSet<int>();
        static readonly Dictionary<string, Color32[]> overlayPix = new Dictionary<string, Color32[]>();
        static readonly Dictionary<string, int> overlayW = new Dictionary<string, int>();
        static readonly Dictionary<string, int> overlayH = new Dictionary<string, int>();
        static readonly Dictionary<string, float[]> masks = new Dictionary<string, float[]>();
        static readonly HashSet<string> maskWarned = new HashSet<string>();
        const int MR = 128;
        internal static float PaintDetail = 1f;
        internal static bool ColorReplace = true;
        internal static bool TexSkipDetail = true;
        internal static float PaintDetailCur = 1f;
        internal static bool TexSkipCur = true;
        internal static bool SkipDiskWrite;

        const int ParMinPixels = 262144;
        static void ForRows(int h, bool parallel, Action<int> row)
        {
            if (parallel) Parallel.For(0, h, row);
            else for (int y = 0; y < h; y++) row(y);
        }

        static float TileMeters(string name)
        {
            switch (name)
            {
                case "fabric":  return 1.2f;
                case "diamond": return 1.2f;
                case "grain":   return 1.5f;
                case "scratch": return 1.6f;
                case "tiles":   return 1.8f;
                case "tilesw":  return 1.8f;
                case "mli":     return 2.5f;
                case "brushed": return 3.0f;
                case "paving1": return 2.5f;
                case "paving2": return 2.0f;
                case "tiles138": return 2.0f;
                case "metal3":  return 3.0f;
                case "plates6": return 2.0f;
                case "plates14": return 3.0f;
                case "ornament": return 1.0f;
                case "tactile1": case "tactile2": return 2.0f;
                case "walkway1": case "walkway2": return 2.5f;
                case "chip":    case "chipc": return 2.0f;
                case "concrete3": return 3.0f;
                case "leather": return 2.0f;
                case "carpet":  return 2.0f;
                case "net":     return 1.5f;
                case "styro":   return 2.5f;
                case "paving3": return 2.5f;
                case "paving4": return 2.5f;
                case "blockwall": return 2.5f;
                case "facade":   return 3.0f;
                case "cladding": return 3.0f;
                case "intiles":  return 2.0f;
                case "planksb":  return 3.0f;
                case "msheet":   return 3.0f;
                case "carbon":   return 2.5f;
                case "ornament2": return 1.0f;
                case "roadlines": case "roadlines2": case "roadlines3": return 4.0f;
                case "camo44":  return 1.5f;
                case "camo1": case "camo54": return 3.0f;
                case "camo10": case "camo13": case "camo26": case "camo22": case "camo55": return 2.5f;
                case "ventsteel": return 1.0f;
                case "wornsteel": return 2.5f;
                case "metalgrid": return 1.5f;
                case "grimymetal": return 2.5f;
                case "hessian": return 1.5f;
                default:        return 5.0f;
            }
        }

        static bool WorldScaled(string name)
        {
            switch (name)
            {
                case "tiles": case "tilesw": case "scratch": case "mli": case "grain":
                case "diamond":
                case "brushed": case "fabric":
                case "paving1": case "paving2":
                case "tiles138": case "metal3":
                case "plates6": case "plates14": case "ornament":
                case "tactile1": case "tactile2":
                case "walkway1": case "walkway2": case "chip": case "chipc":
                case "concrete3": case "leather":
                case "carpet": case "net": case "styro":
                case "paving3": case "paving4": case "ornament2":
                case "roadlines": case "roadlines2": case "roadlines3":
                case "blockwall": case "facade": case "cladding": case "intiles":
                case "planksb": case "msheet": case "carbon":
                case "camo1": case "camo10": case "camo13": case "camo26":
                case "camo54": case "camo22": case "camo44": case "camo55":
                case "ventsteel":
                case "hessian":
                case "wornsteel": case "metalgrid": case "grimymetal":
                case "heatd1": case "heatd2": case "heatd3": case "heatd4":
                case "heatd5": case "heatd6": case "heatd7": case "heatd8":
                    return true;
                default:
                    return false;
            }
        }

        static readonly HashSet<string> DeepRelief = new HashSet<string> {
            "diamond", "metal3", "plates6", "chip", "concrete3", "paving3", "paving4",
            "leak", "grimymetal", "wornsteel", "ventsteel" };

        static Color32 ParseHex(string hx)
        {
            try
            {
                return new Color32(
                    (byte)Convert.ToInt32(hx.Substring(0, 2), 16),
                    (byte)Convert.ToInt32(hx.Substring(2, 2), 16),
                    (byte)Convert.ToInt32(hx.Substring(4, 2), 16), 255);
            }
            catch { return new Color32(255, 255, 255, 255); }
        }

        static readonly List<string> overlayOrder = new List<string>();
        const int OverlayCap = 16;

        static bool GetOverlayPixels(string name, out Color32[] pix, out int w, out int h, bool quiet = false)
        {
            pix = null; w = h = 0;
            if (overlayPix.TryGetValue(name, out pix))
            {
                if (pix == null) return false;
                w = overlayW[name]; h = overlayH[name];
                overlayOrder.Remove(name); overlayOrder.Add(name);
                return true;
            }
            Texture2D tex = (GameDatabase.Instance != null)
                ? GameDatabase.Instance.GetTexture(OverlayPath + name, false) : null;
            if (tex == null)
            {
                if (!quiet) Debug.LogWarning(Tag + "weathering overlay missing: " + name);
                overlayPix[name] = null;
                return false;
            }
            try { pix = tex.GetPixels32(); w = tex.width; h = tex.height; }
            catch
            {
                try
                {
                    RenderTexture rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(tex, rt);
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    Texture2D cp = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                    cp.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                    cp.Apply(false, false);
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    pix = cp.GetPixels32(); w = cp.width; h = cp.height;
                    UnityEngine.Object.Destroy(cp);
                }
                catch (Exception e)
                {
                    Debug.LogError(Tag + "cannot read overlay " + name + ": " + e.Message);
                    overlayPix[name] = null;
                    return false;
                }
            }
            if (name.EndsWith("_n") && pix != null)
                for (int i = 0; i < pix.Length; i++) pix[i].a = pix[i].r;
            overlayPix[name] = pix; overlayW[name] = w; overlayH[name] = h;
            if (pix != null)
            {
                overlayOrder.Remove(name); overlayOrder.Add(name);
                while (overlayOrder.Count > OverlayCap)
                {
                    string old = overlayOrder[0]; overlayOrder.RemoveAt(0);
                    overlayPix.Remove(old); overlayW.Remove(old); overlayH.Remove(old);
                }
            }
            return pix != null;
        }

        static float Smooth(float lo, float hi, float v)
        {
            float t = Mathf.Clamp01((v - lo) / (hi - lo));
            return t * t * (3f - 2f * t);
        }

        static List<Renderer> PartRenderers(Part part)
        {
            List<Renderer> mine = new List<Renderer>();
            if (part == null) return mine;
            Transform root = part.transform.Find("model");
            if (root == null) root = part.transform;
            Renderer[] all = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                if (!all[i].enabled || !all[i].gameObject.activeInHierarchy) continue;
                if (all[i].GetComponentInParent<Camera>() != null) continue;
                Part owner = all[i].GetComponentInParent<Part>();
                if (owner == null || owner == part) mine.Add(all[i]);
            }
            return mine;
        }

        static readonly Dictionary<int, float[]> uvScaleCache = new Dictionary<int, float[]>();

        internal static void ForgetShape(Part part, List<int> meshIds)
        {
            if (part != null) uvScaleCache.Remove(part.GetInstanceID());
            if (meshIds == null || meshIds.Count == 0) return;
            List<string> drop = new List<string>();
            foreach (string k in masks.Keys)
            {
                if (!k.StartsWith("fm|") && !k.StartsWith("fs|")) continue;
                int bar = k.IndexOf('|', 3);
                int id;
                if (bar > 3 && int.TryParse(k.Substring(3, bar - 3), out id) && meshIds.Contains(id)) drop.Add(k);
            }
            for (int i = 0; i < drop.Count; i++) masks.Remove(drop[i]);
        }
        static float[] UvWorldScale(Part part)
        {
            if (part == null) return null;
            int key = part.GetInstanceID();
            float[] cached;
            if (uvScaleCache.TryGetValue(key, out cached)) return cached;
            float[] result = null;
            try
            {
                double su = 0.0, sv = 0.0, sa = 0.0;
                float umin = float.MaxValue, umax = float.MinValue, vmin = float.MaxValue, vmax = float.MinValue;
                List<Renderer> rs = PartRenderers(part);
                for (int i = 0; i < rs.Count; i++)
                {
                    Renderer r = rs[i];
                    if (r == null) continue;
                    Mesh m = ModuleReforged.MeshOf(r);
                    if (m == null || !m.isReadable) continue;
                    Vector3[] vs = m.vertices;
                    Vector2[] uv = m.uv;
                    if (uv == null || vs == null || uv.Length != vs.Length) continue;
                    Vector2 st = Vector2.one;
                    Material sm = r.sharedMaterial;
                    if (sm != null && sm.HasProperty("_MainTex")) st = sm.mainTextureScale;
                    if (Mathf.Abs(st.x) < 1e-4f) st.x = 1f;
                    if (Mathf.Abs(st.y) < 1e-4f) st.y = 1f;
                    Matrix4x4 l2w = r.localToWorldMatrix;
                    int[] tr = m.triangles;
                    for (int t = 0; t + 2 < tr.Length; t += 3)
                    {
                        int a = tr[t], b = tr[t + 1], c = tr[t + 2];
                        Vector3 wa = l2w.MultiplyPoint3x4(vs[a]);
                        Vector3 e1 = l2w.MultiplyPoint3x4(vs[b]) - wa, e2 = l2w.MultiplyPoint3x4(vs[c]) - wa;
                        Vector2 ua = Vector2.Scale(uv[a], st), ub = Vector2.Scale(uv[b], st), uc = Vector2.Scale(uv[c], st);
                        float du1 = ub.x - ua.x, dv1 = ub.y - ua.y, du2 = uc.x - ua.x, dv2 = uc.y - ua.y;
                        float det = du1 * dv2 - du2 * dv1;
                        if (Mathf.Abs(det) < 1e-10f) continue;
                        Vector3 dPdu = (e1 * dv2 - e2 * dv1) / det;
                        Vector3 dPdv = (e2 * du1 - e1 * du2) / det;
                        double area = Mathf.Abs(det) * 0.5;
                        su += dPdu.magnitude * area; sv += dPdv.magnitude * area; sa += area;
                        umin = Mathf.Min(umin, Mathf.Min(ua.x, Mathf.Min(ub.x, uc.x))); umax = Mathf.Max(umax, Mathf.Max(ua.x, Mathf.Max(ub.x, uc.x)));
                        vmin = Mathf.Min(vmin, Mathf.Min(ua.y, Mathf.Min(ub.y, uc.y))); vmax = Mathf.Max(vmax, Mathf.Max(ua.y, Mathf.Max(ub.y, uc.y)));
                    }
                }
                if (sa > 1e-9 && su > 1e-9 && sv > 1e-9)
                    result = new float[] { (float)(su / sa), (float)(sv / sa), (umax - umin) > 1.01f ? 1f : 0f, (vmax - vmin) > 1.01f ? 1f : 0f };
            }
            catch { result = null; }
            uvScaleCache[key] = result;
            return result;
        }

        static float PartMaxDimension(Part part)
        {
            try
            {
                List<Renderer> rs = PartRenderers(part);
                bool got = false; Bounds b = new Bounds();
                for (int i = 0; i < rs.Count; i++)
                {
                    if (!got) { b = rs[i].bounds; got = true; }
                    else b.Encapsulate(rs[i].bounds);
                }
                if (!got) return 2.5f;
                return Mathf.Max(0.3f, Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)));
            }
            catch { return 2.5f; }
        }

        internal static bool RegionAxisDir(string tok, out Vector3 dir, out float cut, out float fade, out float height)
        {
            dir = Vector3.up; cut = 0.4f; fade = 0.2f; height = 1f;
            if (string.IsNullOrEmpty(tok) || tok == "whole") return false;
            if (tok[0] == 'g' && tok.IndexOf('_') > 0)
            {
                string[] f = tok.Substring(1).Split('_');
                int ax, c, fd;
                if (f.Length >= 3 && int.TryParse(f[0], out ax) && int.TryParse(f[1], out c) && int.TryParse(f[2], out fd))
                {
                    switch (ax)
                    {
                        case 0: dir = new Vector3(0f, 1f, 0f); break;
                        case 1: dir = new Vector3(0f, -1f, 0f); break;
                        case 2: dir = new Vector3(0f, 0f, 1f); break;
                        case 3: dir = new Vector3(0f, 0f, -1f); break;
                        case 4: dir = new Vector3(-1f, 0f, 0f); break;
                        default: dir = new Vector3(1f, 0f, 0f); break;
                    }
                    cut = c / 100f; fade = fd / 100f;
                    int hd; if (f.Length >= 4 && int.TryParse(f[3], out hd)) height = hd / 100f;
                    return true;
                }
                return false;
            }
            return false;
        }

        internal static float[] GetMask(Part part, string place)
        {
            if (part == null) return null;
            string pname = (part.partInfo != null) ? part.partInfo.name : part.name;
            List<Renderer> rs = PartRenderers(part);

            int gsig = rs.Count;
            for (int gi = 0; gi < rs.Count; gi++)
            {
                Mesh gm = ModuleReforged.MeshOf(rs[gi]);
                if (gm != null) gsig = gsig * 31 + gm.vertexCount;
            }
            string key = pname + "|" + place + "|" + gsig;
            float[] cached;
            if (masks.TryGetValue(key, out cached)) return cached;

            float[] m = null;
            try
            {
                float rcut, rfade, rheight;
                Vector3 dir;
                if (!RegionAxisDir(place, out dir, out rcut, out rfade, out rheight)) { dir = Vector3.up; rcut = 0.4f; }
                Vector3 qn, qu; float qd, qfl, qhd, qhf; bool quH;
                bool qPlane = ModuleReforged.ParsePlane(place, out qn, out qd, out qfl, out quH, out qu, out qhd, out qhf);
                float lo = float.MaxValue, hi = float.MinValue;
                float vlo = float.MaxValue, vhi = float.MinValue;
                List<Mesh> meshes = new List<Mesh>(); List<Transform> trs = new List<Transform>();
                for (int i = 0; i < rs.Count; i++)
                {
                    Mesh mfm = ModuleReforged.MeshOf(rs[i]);
                    if (mfm == null || !mfm.isReadable) continue;
                    Mesh mesh = mfm;
                    Vector3[] vs = mesh.vertices;
                    for (int v = 0; v < vs.Length; v++)
                    {
                        Vector3 pl = part.transform.InverseTransformPoint(rs[i].transform.TransformPoint(vs[v]));
                        float c = Vector3.Dot(pl, dir);
                        if (c < lo) lo = c;
                        if (c > hi) hi = c;
                        if (pl.y < vlo) vlo = pl.y;
                        if (pl.y > vhi) vhi = pl.y;
                    }
                    meshes.Add(mesh); trs.Add(rs[i].transform);
                }
                if (meshes.Count == 0 || hi - lo < 1e-4f)
                {
                    if (maskWarned.Add(pname))
                        Debug.LogWarning(Tag + "no readable mesh on " + pname + " - coverage falls back to whole part");
                    masks[key] = null;
                    return null;
                }
                m = new float[MR * MR];
                int[] cnt = new int[MR * MR];
                for (int mi = 0; mi < meshes.Count; mi++)
                {
                    Mesh mesh = meshes[mi];
                    Vector3[] vs = mesh.vertices;
                    Vector2[] uv = mesh.uv;
                    int[] tris = mesh.triangles;
                    if (uv == null || uv.Length != vs.Length) continue;
                    bool useH = rheight < 0.999f && (vhi - vlo) > 1e-5f;

                    float bandC = Mathf.Max(0.02f, rfade * Mathf.Max(0.05f, 1f - rcut));
                    float bandH = Mathf.Max(0.02f, rfade * Mathf.Max(0.05f, rheight));
                    float[] wv = new float[vs.Length];
                    for (int v = 0; v < vs.Length; v++)
                    {
                        Vector3 pl = part.transform.InverseTransformPoint(trs[mi].TransformPoint(vs[v]));
                        if (qPlane) { wv[v] = ModuleReforged.PlaneWeight(pl, qn, qd, qfl, quH, qu, qhd, qhf); continue; }
                        float cA = (Vector3.Dot(pl, dir) - lo) / (hi - lo);
                        float w = Smooth(rcut, rcut + bandC, cA);
                        if (useH)
                        {
                            float hN = (pl.y - vlo) / (vhi - vlo);
                            w *= 1f - Smooth(rheight - bandH, rheight, hN);
                        }
                        wv[v] = w;
                    }
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                        RasterTri(m, cnt, uv[tris[t]], uv[tris[t + 1]], uv[tris[t + 2]], wv[tris[t]], wv[tris[t + 1]], wv[tris[t + 2]]);
                }

                for (int i2 = 0; i2 < m.Length; i2++)
                    if (cnt[i2] > 0) m[i2] /= cnt[i2];
                SmoothMask(m, cnt);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Tag + "coverage mask failed on " + pname + ": " + e.Message);
                m = null;
            }
            masks[key] = m;
            return m;
        }

        static readonly Dictionary<string, Texture2D> metalMasks = new Dictionary<string, Texture2D>();
        public static Texture2D MetallicMask(Part part, string place, float metal, float smooth, float[] maskOverride)
        {
            float[] mask; string key = null;
            if (maskOverride != null) mask = maskOverride;
            else
            {
                if (part == null) return null;
                string pname = (part.partInfo != null) ? part.partInfo.name : part.name;
                key = pname + "|" + place + "|m" + metal.ToString("0.00") + "|g" + smooth.ToString("0.00");
                Texture2D ct;
                if (metalMasks.TryGetValue(key, out ct) && ct != null) return ct;
                mask = GetMask(part, place);
            }
            if (mask == null) return null;
            Texture2D t = new Texture2D(MR, MR, TextureFormat.RGBA32, true);
            Color32[] px = new Color32[MR * MR];
            for (int i = 0; i < px.Length; i++)
            {
                float w = Mathf.Clamp01(mask[i]);
                byte met = (byte)(Mathf.Clamp01(metal * w) * 255f);
                byte gl = (byte)(Mathf.Clamp01(smooth * w) * 255f);
                px[i] = new Color32(met, met, met, gl);
            }
            t.SetPixels32(px); t.Apply(true, false);
            if (key != null) metalMasks[key] = t;
            return t;
        }

        internal static float[] RasterMask(List<Vector2> uv, List<float> wv, List<int> tris)
        {
            float[] m = new float[MR * MR];
            int[] cnt = new int[MR * MR];
            for (int t = 0; t + 2 < tris.Count; t += 3)
                RasterTri(m, cnt, uv[tris[t]], uv[tris[t + 1]], uv[tris[t + 2]], wv[tris[t]], wv[tris[t + 1]], wv[tris[t + 2]]);
            for (int i = 0; i < m.Length; i++) if (cnt[i] > 0) m[i] /= cnt[i];
            SmoothMask(m, cnt);
            return m;
        }

        static void SmoothMask(float[] m, int[] cnt)
        {
            bool[] has = new bool[m.Length];
            for (int i = 0; i < m.Length; i++) has[i] = cnt[i] > 0;
            for (int pass = 0; pass < 4; pass++)
            {
                bool[] nhas = (bool[])has.Clone();
                for (int y = 0; y < MR; y++)
                {
                    for (int x = 0; x < MR; x++)
                    {
                        int idx = y * MR + x;
                        if (has[idx]) continue;
                        float sum = 0f; int n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = ((y + dy) % MR + MR) % MR;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = ((x + dx) % MR + MR) % MR;
                                int j = yy * MR + xx;
                                if (has[j]) { sum += m[j]; n++; }
                            }
                        }
                        if (n > 0) { m[idx] = sum / n; nhas[idx] = true; }
                    }
                }
                has = nhas;
            }
            float[] tmp = new float[m.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int y = 0; y < MR; y++)
                {
                    for (int x = 0; x < MR; x++)
                    {
                        float sum = 0f;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = ((y + dy) % MR + MR) % MR;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = ((x + dx) % MR + MR) % MR;
                                sum += m[yy * MR + xx];
                            }
                        }
                        tmp[y * MR + x] = sum / 9f;
                    }
                }
                Array.Copy(tmp, m, m.Length);
            }
        }

        static void RasterTri(float[] m, int[] cnt, Vector2 a, Vector2 b, Vector2 c, float wa, float wb, float wc)
        {
            float ax = a.x * MR, ay = a.y * MR, bx = b.x * MR, by = b.y * MR, cx = c.x * MR, cy = c.y * MR;
            float minx = Mathf.Min(ax, Mathf.Min(bx, cx)), maxx = Mathf.Max(ax, Mathf.Max(bx, cx));
            float miny = Mathf.Min(ay, Mathf.Min(by, cy)), maxy = Mathf.Max(ay, Mathf.Max(by, cy));
            if ((maxx - minx) * (maxy - miny) > MR * MR * 9f) return;
            float den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (Mathf.Abs(den) < 1e-6f) return;
            int x0 = Mathf.FloorToInt(minx), x1 = Mathf.CeilToInt(maxx);
            int y0 = Mathf.FloorToInt(miny), y1 = Mathf.CeilToInt(maxy);
            for (int py = y0; py <= y1; py++)
            {
                for (int px = x0; px <= x1; px++)
                {
                    float fx = px + 0.5f, fy = py + 0.5f;
                    float l1 = ((by - cy) * (fx - cx) + (cx - bx) * (fy - cy)) / den;
                    float l2 = ((cy - ay) * (fx - cx) + (ax - cx) * (fy - cy)) / den;
                    float l3 = 1f - l1 - l2;
                    if (l1 < -0.02f || l2 < -0.02f || l3 < -0.02f) continue;
                    float w = l1 * wa + l2 * wb + l3 * wc;
                    int ix = ((px % MR) + MR) % MR, iy = ((py % MR) + MR) % MR;
                    int idx = iy * MR + ix;
                    m[idx] += w; cnt[idx]++;
                }
            }
        }

        internal static float SampleMask(float[] m, float u, float v)
        {
            float fx = u * MR - 0.5f, fy = v * MR - 0.5f;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            x0 = ((x0 % MR) + MR) % MR; x1 = ((x1 % MR) + MR) % MR;
            y0 = ((y0 % MR) + MR) % MR; y1 = ((y1 % MR) + MR) % MR;
            float a = m[y0 * MR + x0] * (1 - tx) + m[y0 * MR + x1] * tx;
            float b = m[y1 * MR + x0] * (1 - tx) + m[y1 * MR + x1] * tx;
            return a * (1 - ty) + b * ty;
        }

        static readonly Dictionary<string, Texture2D> winBaked = new Dictionary<string, Texture2D>();
        static readonly Dictionary<int, string> diskKeyOf = new Dictionary<int, string>();
        static readonly HashSet<int> maskDiskTried = new HashSet<int>();
        static string cacheDirPath;
        const int DiskMagic = 0x31584452;

        static string CacheDir()
        {
            if (cacheDirPath != null) return cacheDirPath;
            cacheDirPath = KSPUtil.ApplicationRootPath + "GameData/ReforgedRedux/PluginData/BakeCache/";
            try
            {
                if (!System.IO.Directory.Exists(cacheDirPath)) System.IO.Directory.CreateDirectory(cacheDirPath);
                foreach (string f in System.IO.Directory.GetFiles(cacheDirPath, "*.tmp")) { try { System.IO.File.Delete(f); } catch { } }
                string vf = cacheDirPath + "version.txt";
                string old = System.IO.File.Exists(vf) ? System.IO.File.ReadAllText(vf).Trim() : "";
                if (old != ReforgedPainter.Version)
                {
                    foreach (string f in System.IO.Directory.GetFiles(cacheDirPath, "*.bin")) { try { System.IO.File.Delete(f); } catch { } }
                    System.IO.File.WriteAllText(vf, ReforgedPainter.Version);
                }
            }
            catch { cacheDirPath = ""; }
            return cacheDirPath;
        }

        internal static int ClearDiskCache()
        {
            string dir = CacheDir();
            int n = 0;
            if (dir == "") return 0;
            try
            {
                foreach (string f in System.IO.Directory.GetFiles(dir, "*.bin")) { try { System.IO.File.Delete(f); n++; } catch { } }
            }
            catch { }
            maskDiskTried.Clear();
            return n;
        }

        static string StableTex(Texture t)
        {
            if (t == null) return "-";
            string n = t.name;
            if (string.IsNullOrEmpty(n) || n.IndexOf('/') < 0 || n.IndexOf("_RDX_", StringComparison.Ordinal) >= 0) return null;
            return n + "#" + t.width + "x" + t.height;
        }

        static string HashKey(string s)
        {
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hb = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
                System.Text.StringBuilder sb = new System.Text.StringBuilder(32);
                for (int i = 0; i < hb.Length; i++) sb.Append(hb[i].ToString("x2"));
                return sb.ToString();
            }
        }

        internal static int CacheLimitMB = 0;
        static int writesSinceTrim;
        static int trimming;

        internal static long DiskCacheBytes()
        {
            string dir = CacheDir();
            if (dir == "") return 0;
            long n = 0;
            try
            {
                foreach (string f in System.IO.Directory.GetFiles(dir, "*.bin"))
                {
                    try { n += new System.IO.FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return n;
        }

        internal static void TrimDiskCache()
        {
            long lim = CacheLimitMB * 1048576L;
            if (lim <= 0) return;
            string dir = CacheDir();
            if (dir == "") return;
            if (System.Threading.Interlocked.Exchange(ref trimming, 1) == 1) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                try
                {
                    System.IO.FileInfo[] fs = new System.IO.DirectoryInfo(dir).GetFiles("*.bin");
                    long total = 0;
                    for (int i = 0; i < fs.Length; i++) total += fs[i].Length;
                    if (total > lim)
                    {
                        Array.Sort(fs, delegate(System.IO.FileInfo a, System.IO.FileInfo b) { return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); });
                        long target = lim / 10 * 9;
                        for (int i = 0; i < fs.Length && total > target; i++)
                        {
                            try { long len = fs[i].Length; fs[i].Delete(); total -= len; } catch { }
                        }
                    }
                }
                catch { }
                finally { System.Threading.Interlocked.Exchange(ref trimming, 0); }
            });
        }

        static void DiskWrite(string file, byte[] payload)
        {
            string dir = CacheDir();
            if (dir == "" || payload == null) return;
            if (++writesSinceTrim >= 16) { writesSinceTrim = 0; TrimDiskCache(); }
            string path = dir + file + ".bin";
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                string tmp = path + "." + System.Threading.Thread.CurrentThread.ManagedThreadId + ".tmp";
                try
                {
                    System.IO.File.WriteAllBytes(tmp, payload);
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    System.IO.File.Move(tmp, path);
                }
                catch { try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { } }
            });
        }

        static byte[] DiskRead(string file)
        {
            string dir = CacheDir();
            if (dir == "") return null;
            string path = dir + file + ".bin";
            try
            {
                if (!System.IO.File.Exists(path)) return null;
                byte[] d = System.IO.File.ReadAllBytes(path);
                if (CacheLimitMB > 0) { try { System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { } }
                return d;
            }
            catch { return null; }
        }

        static void DiskDrop(string file)
        {
            string dir = CacheDir();
            if (dir == "") return;
            try { System.IO.File.Delete(dir + file + ".bin"); } catch { }
        }

        static void PutTex(System.IO.BinaryWriter bw, Texture2D t)
        {
            byte[] raw = t.GetRawTextureData();
            bw.Write(t.width); bw.Write(t.height); bw.Write((int)t.format); bw.Write(t.mipmapCount); bw.Write(raw.Length); bw.Write(raw);
        }

        static Texture2D GetTex(byte[] d, ref int pos, string name)
        {
            int w = BitConverter.ToInt32(d, pos), h = BitConverter.ToInt32(d, pos + 4);
            int fmt = BitConverter.ToInt32(d, pos + 8), mips = BitConverter.ToInt32(d, pos + 12), len = BitConverter.ToInt32(d, pos + 16);
            pos += 20;
            if (w <= 0 || h <= 0 || len <= 0 || pos + len > d.Length) throw new Exception("bad cache entry");
            byte[] raw = new byte[len];
            Buffer.BlockCopy(d, pos, raw, 0, len);
            pos += len;
            Texture2D t = new Texture2D(w, h, (TextureFormat)fmt, mips > 1);
            try { t.LoadRawTextureData(raw); t.Apply(false, true); }
            catch { UnityEngine.Object.Destroy(t); throw; }
            t.name = name;
            return t;
        }

        static void SaveTexFile(string file, Texture2D main, Texture2D bump)
        {
            if (main == null) return;
            try
            {
                System.IO.MemoryStream ms = new System.IO.MemoryStream();
                System.IO.BinaryWriter bw = new System.IO.BinaryWriter(ms);
                bw.Write(DiskMagic); bw.Write(bump != null ? 1 : 0);
                PutTex(bw, main);
                if (bump != null) PutTex(bw, bump);
                bw.Flush();
                DiskWrite(file, ms.ToArray());
            }
            catch { }
        }

        static bool LoadTexFile(string file, string name, out Texture2D main, out Texture2D bump)
        {
            main = null; bump = null;
            byte[] d = DiskRead(file);
            if (d == null) return false;
            try
            {
                if (d.Length < 8 || BitConverter.ToInt32(d, 0) != DiskMagic) throw new Exception("bad cache file");
                bool hasBump = BitConverter.ToInt32(d, 4) == 1;
                int pos = 8;
                main = GetTex(d, ref pos, name);
                if (hasBump) bump = GetTex(d, ref pos, name + "_n");
                return true;
            }
            catch
            {
                if (main != null) UnityEngine.Object.Destroy(main);
                if (bump != null) UnityEngine.Object.Destroy(bump);
                main = null; bump = null;
                DiskDrop(file);
                return false;
            }
        }

        static bool LoadMaskDisk(Texture orig)
        {
            int id = orig.GetInstanceID();
            if (!maskDiskTried.Add(id)) return false;
            string st = StableTex(orig);
            if (st == null || st == "-") return false;
            string file = HashKey("k|" + st) + "k";
            byte[] d = DiskRead(file);
            if (d == null || d.Length < 20) return false;
            try
            {
                if (BitConverter.ToInt32(d, 0) != DiskMagic) throw new Exception("bad mask file");
                bool shiny = BitConverter.ToInt32(d, 4) == 1;
                int w = BitConverter.ToInt32(d, 8), h = BitConverter.ToInt32(d, 12), len = BitConverter.ToInt32(d, 16);
                if (len != d.Length - 20) throw new Exception("bad mask size");
                if (shiny)
                {
                    if (len != w * h || len == 0) throw new Exception("bad mask dims");
                    byte[] mb = new byte[len];
                    Buffer.BlockCopy(d, 20, mb, 0, len);
                    winMaskC[id] = mb; winMaskWC[id] = w; winMaskHC[id] = h;
                }
                hasShinyC[id] = shiny;
                return true;
            }
            catch { DiskDrop(file); return false; }
        }

        static void SaveMaskDisk(Texture orig, bool shiny)
        {
            string st = StableTex(orig);
            if (st == null || st == "-") return;
            int id = orig.GetInstanceID();
            byte[] mb = null; int w = 0, h = 0;
            if (shiny && winMaskC.TryGetValue(id, out mb) && mb != null) { w = winMaskWC[id]; h = winMaskHC[id]; }
            else if (shiny) return;
            try
            {
                System.IO.MemoryStream ms = new System.IO.MemoryStream();
                System.IO.BinaryWriter bw = new System.IO.BinaryWriter(ms);
                bw.Write(DiskMagic); bw.Write(shiny ? 1 : 0); bw.Write(w); bw.Write(h);
                bw.Write(mb != null ? mb.Length : 0);
                if (mb != null) bw.Write(mb);
                bw.Flush();
                DiskWrite(HashKey("k|" + st) + "k", ms.ToArray());
            }
            catch { }
        }
        static Texture2D lastMainTex, lastBumpTex;
        static Color32[] lastMainPix, lastBumpPix;
        static readonly Dictionary<int, Color32[]> origPixC = new Dictionary<int, Color32[]>();
        static readonly Dictionary<int, int> origWC = new Dictionary<int, int>();
        static readonly Dictionary<int, int> origHC = new Dictionary<int, int>();
        static readonly List<int> origOrder = new List<int>();

        static readonly Dictionary<int, bool> hasShinyC = new Dictionary<int, bool>();
        public static bool HasShiny(Texture orig)
        {
            if (orig == null) return false;
            int id = orig.GetInstanceID();
            bool v;
            if (hasShinyC.TryGetValue(id, out v)) return v;
            if (LoadMaskDisk(orig) && hasShinyC.TryGetValue(id, out v)) return v;
            int w, h; Color32[] px = OrigPix(orig, out w, out h);
            v = false;
            if (px != null)
            {
                int need = Mathf.Max(24, px.Length / 1000), cnt = 0;
                int thr = Mathf.CeilToInt((ShinyLow(px) + 0.075f) * 255f);
                if (thr <= 255)
                    for (int i = 0; i < px.Length; i++)
                        if (px[i].a >= thr && ++cnt >= need) break;
                if (cnt >= need)
                {
                    int mw, mh; byte[] mk = WindowMask(orig, out mw, out mh);
                    cnt = 0;
                    if (mk != null)
                        for (int i = 0; i < mk.Length; i++)
                            if (mk[i] > 127 && ++cnt >= need) { v = true; break; }
                }
            }
            hasShinyC[id] = v;
            if (px != null) SaveMaskDisk(orig, v);
            return v;
        }

        static float ShinyLow(Color32[] px)
        {
            int[] hist = new int[256];
            for (int i = 0; i < px.Length; i++) hist[px[i].a]++;
            int half = px.Length / 2, acc = 0, med = 0;
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc > half) { med = i; break; } }
            return Mathf.Max(med / 255f + 0.18f, 0.40f);
        }

        static void MinMaxFilter(float[] a, int w, int h, int r, bool takeMin)
        {
            float[] t = new float[a.Length];
            bool par = a.Length >= ParMinPixels;
            ForRows(h, par, delegate(int y)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float v = a[row + x];
                    for (int d = -r; d <= r; d++)
                    {
                        int xx = x + d; if (xx < 0 || xx >= w) continue;
                        float u = a[row + xx];
                        if (takeMin ? u < v : u > v) v = u;
                    }
                    t[row + x] = v;
                }
            });
            ForRows(w, par, delegate(int x)
            {
                for (int y = 0; y < h; y++)
                {
                    float v = t[y * w + x];
                    for (int d = -r; d <= r; d++)
                    {
                        int yy = y + d; if (yy < 0 || yy >= h) continue;
                        float u = t[yy * w + x];
                        if (takeMin ? u < v : u > v) v = u;
                    }
                    a[y * w + x] = v;
                }
            });
        }

        static Color32[] OrigPix(Texture src, out int w, out int h)
        {
            w = 0; h = 0;
            if (src == null) return null;
            int id = src.GetInstanceID();
            Color32[] px;
            if (origPixC.TryGetValue(id, out px) && px != null) { w = origWC[id]; h = origHC[id]; return px; }

            int sw = src.width, sh = src.height;
            if (sw > 1024 || sh > 1024) { float sc = 1024f / Mathf.Max(sw, sh); sw = Mathf.Max(1, (int)(sw * sc)); sh = Mathf.Max(1, (int)(sh * sc)); }
            try
            {
                RenderTexture rt = RenderTexture.GetTemporary(sw, sh, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt);
                RenderTexture prev = RenderTexture.active; RenderTexture.active = rt;
                Texture2D tmp = new Texture2D(sw, sh, TextureFormat.RGBA32, false);
                tmp.ReadPixels(new Rect(0, 0, sw, sh), 0, 0); tmp.Apply(false, false);
                RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                px = tmp.GetPixels32(); UnityEngine.Object.Destroy(tmp);
                w = sw; h = sh;
            }
            catch { return null; }
            origPixC[id] = px; origWC[id] = w; origHC[id] = h; origOrder.Add(id);
            while (origOrder.Count > 12)
            {
                int o = origOrder[0]; origOrder.RemoveAt(0);
                origPixC.Remove(o); origWC.Remove(o); origHC.Remove(o);
            }
            return px;
        }

        static readonly Dictionary<string, Texture2D[]> tuFlatC = new Dictionary<string, Texture2D[]>();

        static string Tid(Texture t) { return t == null ? "-" : t.GetInstanceID().ToString(); }
        static string Q4(Vector4 v) { return v.x.ToString("0.000") + "," + v.y.ToString("0.000") + "," + v.z.ToString("0.000") + "," + v.w.ToString("0.000") + ";"; }
        static byte B01(float f) { int i = (int)(f * 255f + 0.5f); return (byte)(i < 0 ? 0 : (i > 255 ? 255 : i)); }

        static Color32 SampleWrap(Color32[] px, int w, int h, float u, float v)
        {
            float fx = u * w - 0.5f, fy = v * h - 0.5f;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            x0 = ((x0 % w) + w) % w; x1 = ((x1 % w) + w) % w; y0 = ((y0 % h) + h) % h; y1 = ((y1 % h) + h) % h;
            Color32 a = px[y0 * w + x0], b = px[y0 * w + x1], c = px[y1 * w + x0], d = px[y1 * w + x1];
            float r = (a.r + (b.r - a.r) * tx) * (1f - ty) + (c.r + (d.r - c.r) * tx) * ty;
            float g = (a.g + (b.g - a.g) * tx) * (1f - ty) + (c.g + (d.g - c.g) * tx) * ty;
            float bb = (a.b + (b.b - a.b) * tx) * (1f - ty) + (c.b + (d.b - c.b) * tx) * ty;
            float al = (a.a + (b.a - a.a) * tx) * (1f - ty) + (c.a + (d.a - c.a) * tx) * ty;
            return new Color32((byte)(r + 0.5f), (byte)(g + 0.5f), (byte)(bb + 0.5f), (byte)(al + 0.5f));
        }

        static float TuMix(float sample, float mr, float mg, float mb, float norm, float detailMult, float u1, float u2, float u3)
        {
            float mix = 1f - (mr + mg + mb); mix = mix < 0f ? 0f : (mix > 1f ? 1f : mix);
            float user = mr * u1 + mg * u2 + mb * u3;
            float up = user * (sample - norm) * (1f - mix) * detailMult + user;
            up = up < 0f ? 0f : (up > 1f ? 1f : up);
            return up + mix * sample;
        }

        internal static bool FlattenTu(Texture main, Texture mask, Texture mg, Texture normMask, Texture inMask,
            bool norm, bool input, bool stockSpec, Color c1, Color c2, Color c3,
            Vector4 mm, Vector4 dn, Vector4 mn, Vector4 sn, Vector4 dmv, out Texture2D albedo, out Texture2D gloss)
        {
            albedo = null; gloss = null;
            if (main == null) return false;
            string key = main.GetInstanceID() + "|" + Tid(mask) + "|" + Tid(mg) + "|" + Tid(normMask) + "|" + Tid(inMask) + "|"
                + (norm ? "n" : "") + (input ? "i" : "") + (stockSpec ? "s" : "") + "|"
                + Q4(c1) + Q4(c2) + Q4(c3) + Q4(mm) + Q4(dn) + Q4(mn) + Q4(sn) + Q4(dmv);
            Texture2D[] hit;
            if (tuFlatC.TryGetValue(key, out hit) && hit != null && hit[0] != null && hit[1] != null) { albedo = hit[0]; gloss = hit[1]; return true; }
            int w, h; Color32[] mp = ReadTexWC(main, out w, out h);
            if (mp == null || w < 1 || h < 1) return false;
            int kw = 0, kh = 0, gw = 0, gh = 0, nw = 0, nh = 0, iw = 0, ih = 0;
            Color32[] kp = (mask != null) ? ReadTexWC(mask, out kw, out kh) : null;
            Color32[] gp = (mg != null) ? ReadTexWC(mg, out gw, out gh) : null;
            Color32[] np = (norm && normMask != null) ? ReadTexWC(normMask, out nw, out nh) : null;
            Color32[] ip = (input && inMask != null) ? ReadTexWC(inMask, out iw, out ih) : null;
            Color32[] outA = new Color32[w * h], outG = new Color32[w * h];
            ForRows(h, w * h >= ParMinPixels, delegate(int y)
            {
                float v = (y + 0.5f) / h;
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w;
                    Color32 c = mp[y * w + x];
                    Color32 k = (kp != null) ? SampleWrap(kp, kw, kh, u, v) : new Color32(255, 0, 0, 255);
                    Color32 g = (gp != null) ? SampleWrap(gp, gw, gh, u, v) : new Color32(255, 255, 255, 255);
                    float mr = k.r / 255f, mgc = k.g / 255f, mb = k.b / 255f;
                    float dNorm = mr * dn.x + mgc * dn.y + mb * dn.z;
                    float mNorm = mr * mn.x + mgc * mn.y + mb * mn.z;
                    float sNorm = mr * sn.x + mgc * sn.y + mb * sn.z;
                    float dMul = mr * dmv.x + mgc * dmv.y + mb * dmv.z;
                    if (norm)
                    {
                        dNorm += k.a / 255f;
                        Color32 nq = (np != null) ? SampleWrap(np, nw, nh, u, v) : new Color32(0, 0, 0, 0);
                        mNorm += nq.r / 255f; sNorm += nq.a / 255f;
                    }
                    float mf = 1f, sf = 1f;
                    if (input)
                    {
                        Color32 iq = (ip != null) ? SampleWrap(ip, iw, ih, u, v) : new Color32(255, 255, 255, 255);
                        mf = iq.r / 255f; sf = iq.a / 255f;
                    }
                    float cr = c.r / 255f, cg = c.g / 255f, cb = c.b / 255f;
                    float mix = 1f - (mr + mgc + mb); mix = mix < 0f ? 0f : (mix > 1f ? 1f : mix);
                    float det = (cr * 0.22f + cg * 0.707f + cb * 0.071f - dNorm) * (1f - mix) * dMul;
                    float ur = mr * c1.r + mgc * c2.r + mb * c3.r;
                    float ug = mr * c1.g + mgc * c2.g + mb * c3.g;
                    float ub = mr * c1.b + mgc * c2.b + mb * c3.b;
                    float orr = ur * det + ur, og = ug * det + ug, ob = ub * det + ub;
                    orr = (orr < 0f ? 0f : (orr > 1f ? 1f : orr)) + mix * cr;
                    og = (og < 0f ? 0f : (og > 1f ? 1f : og)) + mix * cg;
                    ob = (ob < 0f ? 0f : (ob > 1f ? 1f : ob)) + mix * cb;
                    float metal = g.r / 255f;
                    float smooth = stockSpec ? c.a / 255f : g.a / 255f;
                    float om = TuMix(metal, mr * mf, mgc * mf, mb * mf, mNorm, dMul, mm.x, mm.y, mm.z);
                    float os = TuMix(smooth, mr * sf, mgc * sf, mb * sf, sNorm, dMul, c1.a, c2.a, c3.a);
                    outA[y * w + x] = new Color32(B01(orr), B01(og), B01(ob), c.a);
                    byte bm = B01(om);
                    outG[y * w + x] = new Color32(bm, bm, bm, B01(os));
                }
            });
            Texture2D ta = new Texture2D(w, h, TextureFormat.RGBA32, true);
            ta.SetPixels32(outA); ta.Apply(true, false);
            ta.name = main.name + "_RDX_tu";
            Texture2D tg = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tg.SetPixels32(outG); tg.Apply(true, false);
            tg.name = main.name + "_RDX_tugloss";
            try { if (w % 4 == 0 && h % 4 == 0) tg.Compress(false); } catch { }
            tuFlatC[key] = new Texture2D[] { ta, tg };
            albedo = ta; gloss = tg;
            return true;
        }

        static Color32[] ReadTexWC(Texture src, out int w, out int h)
        {
            w = 0; h = 0;
            if (src == null) return null;
            Texture2D t2 = src as Texture2D;
            if (t2 != null && t2.isReadable) { w = t2.width; h = t2.height; return t2.GetPixels32(); }
            try
            {
                int mw = src.width, mh = src.height;
                RenderTexture rt = RenderTexture.GetTemporary(mw, mh, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt);
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                Texture2D tmp = new Texture2D(mw, mh, TextureFormat.RGBA32, false);
                tmp.ReadPixels(new Rect(0, 0, mw, mh), 0, 0);
                tmp.Apply(false, false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                Color32[] px = tmp.GetPixels32();
                UnityEngine.Object.Destroy(tmp);
                w = mw; h = mh;
                return px;
            }
            catch { return null; }
        }

        static readonly Dictionary<int, byte[]> winMaskC = new Dictionary<int, byte[]>();
        static readonly Dictionary<int, int> winMaskWC = new Dictionary<int, int>();
        static readonly Dictionary<int, int> winMaskHC = new Dictionary<int, int>();

        static byte[] WindowMask(Texture orig, out int w, out int h)
        {
            w = 0; h = 0;
            if (orig == null) return null;
            int id = orig.GetInstanceID();
            byte[] mb;
            if (winMaskC.TryGetValue(id, out mb) && mb != null) { w = winMaskWC[id]; h = winMaskHC[id]; return mb; }
            if (LoadMaskDisk(orig) && winMaskC.TryGetValue(id, out mb) && mb != null) { w = winMaskWC[id]; h = winMaskHC[id]; return mb; }
            int ow, oh; Color32[] op = OrigPix(orig, out ow, out oh);
            if (op == null) return null;
            float low = ShinyLow(op), high = low + 0.15f;
            float[] mk = new float[op.Length];
            bool par = op.Length >= ParMinPixels;
            ForRows(oh, par, delegate(int y)
            {
                int row = y * ow;
                for (int x = 0; x < ow; x++)
                {
                    float t = (op[row + x].a / 255f - low) / (high - low); t = t < 0f ? 0f : (t > 1f ? 1f : t);
                    mk[row + x] = t * t * (3f - 2f * t);
                }
            });
            MinMaxFilter(mk, ow, oh, 2, true);
            MinMaxFilter(mk, ow, oh, 2, false);
            const int r = 3;
            float[] tmp = new float[mk.Length];
            ForRows(oh, par, delegate(int y) { int row = y * ow; for (int x = 0; x < ow; x++) { float sum = 0f; int c = 0; for (int dx = -r; dx <= r; dx++) { int xx = x + dx; if (xx < 0 || xx >= ow) continue; sum += mk[row + xx]; c++; } tmp[row + x] = sum / c; } });
            ForRows(ow, par, delegate(int x) { for (int y = 0; y < oh; y++) { float sum = 0f; int c = 0; for (int dy = -r; dy <= r; dy++) { int yy = y + dy; if (yy < 0 || yy >= oh) continue; sum += tmp[yy * ow + x]; c++; } mk[y * ow + x] = sum / c; } });
            mb = new byte[mk.Length];
            for (int i = 0; i < mk.Length; i++) mb[i] = (byte)(mk[i] * 255f);
            winMaskC[id] = mb; winMaskWC[id] = ow; winMaskHC[id] = oh; w = ow; h = oh;
            return mb;
        }

        static readonly Dictionary<int, Texture2D> shinyMetalC = new Dictionary<int, Texture2D>();
        internal static Texture ShinyMetalMask(Texture origAlbedo)
        {
            if (origAlbedo == null) return null;
            int id = origAlbedo.GetInstanceID();
            Texture2D cached;
            if (shinyMetalC.TryGetValue(id, out cached)) return cached;
            int w, h; byte[] mk = WindowMask(origAlbedo, out w, out h);
            if (mk == null || w < 1 || h < 1) { shinyMetalC[id] = null; return null; }
            Color32[] px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) { byte v = (byte)(255 - mk[i]); px[i] = new Color32(v, v, v, v); }
            Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, true);
            t.SetPixels32(px); t.Apply(true, false);
            shinyMetalC[id] = t;
            return t;
        }

        static readonly Dictionary<int, Texture2D> stockGlossC = new Dictionary<int, Texture2D>();
        internal static Texture StockGlossMask(Texture origAlbedo, float metal, float smooth, float winSmooth)
        {
            if (origAlbedo == null) return null;
            int id = origAlbedo.GetInstanceID();
            Texture2D cached;
            if (stockGlossC.TryGetValue(id, out cached)) return cached;
            int w, h; byte[] mk = WindowMask(origAlbedo, out w, out h);
            if (mk == null || w < 1 || h < 1) { stockGlossC[id] = null; return null; }
            Color32[] px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                float k = mk[i] / 255f;
                byte mt = (byte)(Mathf.Clamp01(metal * (1f - k)) * 255f);
                byte sm = (byte)(Mathf.Clamp01(smooth + (winSmooth - smooth) * k) * 255f);
                px[i] = new Color32(mt, mt, mt, sm);
            }
            Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, true);
            t.SetPixels32(px); t.Apply(true, false);
            stockGlossC[id] = t;
            return t;
        }

        public static Texture MaskWindows(Texture baked, Texture origAlbedo)
        {
            if (baked == null || origAlbedo == null) return baked;
            string key = "win|" + baked.GetInstanceID() + "|" + origAlbedo.GetInstanceID();
            Texture2D cached;
            if (winBaked.TryGetValue(key, out cached)) { if (cached != null) return cached; winBaked.Remove(key); }
            string diskFile = null, dk;
            if (diskKeyOf.TryGetValue(baked.GetInstanceID(), out dk)) diskFile = dk + "w";
            if (diskFile != null)
            {
                Texture2D dm, dn;
                if (LoadTexFile(diskFile, baked.name, out dm, out dn)) { if (dn != null) UnityEngine.Object.Destroy(dn); winBaked[key] = dm; return dm; }
            }
            int w, h; Color32[] bp;
            if (ReferenceEquals(baked, lastMainTex) && lastMainPix != null) { w = baked.width; h = baked.height; bp = (Color32[])lastMainPix.Clone(); }
            else bp = ReadTexWC(baked, out w, out h);
            if (bp == null || bp.Length != w * h) return baked;
            Color32[] op;
            try { op = ReadBase(origAlbedo, w, h); } catch { return baked; }
            int mkw, mkh; byte[] mk = WindowMask(origAlbedo, out mkw, out mkh);
            if (mk == null) return baked;
            for (int y = 0; y < h; y++)
            {
                int my = (mkh == h) ? y : Mathf.Min(mkh - 1, (int)((y + 0.5f) / h * mkh));
                int row = y * w, mrow = my * mkw;
                for (int x = 0; x < w; x++)
                {
                    int mx = (mkw == w) ? x : Mathf.Min(mkw - 1, (int)((x + 0.5f) / w * mkw));
                    float mask = mk[mrow + mx] / 255f;
                    if (mask <= 0.004f) continue;
                    Color32 o = op[row + x];
                    Color32 b = bp[row + x];
                    float fr = b.r + (o.r - b.r) * mask, fg = b.g + (o.g - b.g) * mask, fb = b.b + (o.b - b.b) * mask;
                    bp[row + x] = new Color32(
                        (byte)(fr < 0f ? 0f : (fr > 255f ? 255f : fr)),
                        (byte)(fg < 0f ? 0f : (fg > 255f ? 255f : fg)),
                        (byte)(fb < 0f ? 0f : (fb > 255f ? 255f : fb)), b.a);
                }
            }
            Texture2D outT = new Texture2D(w, h, TextureFormat.RGBA32, true);
            outT.SetPixels32(bp); outT.Apply(true, false);
            try { if (w % 4 == 0 && h % 4 == 0) outT.Compress(false); } catch { }
            if (diskFile != null && !SkipDiskWrite) SaveTexFile(diskFile, outT, null);
            winBaked[key] = outT;
            return outT;
        }

        public static Texture MaskWindowsBump(Texture bakedBump, Texture origBump, Texture maskAlbedo)
        {
            if (bakedBump == null || maskAlbedo == null) return bakedBump;
            string key = "winB|" + bakedBump.GetInstanceID() + "|" + ((origBump != null) ? origBump.GetInstanceID() : 0) + "|" + maskAlbedo.GetInstanceID();
            Texture2D cached;
            if (winBaked.TryGetValue(key, out cached)) { if (cached != null) return cached; winBaked.Remove(key); }
            string diskFile = null, dk;
            if (diskKeyOf.TryGetValue(bakedBump.GetInstanceID(), out dk)) diskFile = dk + "x";
            if (diskFile != null)
            {
                Texture2D dm, dn;
                if (LoadTexFile(diskFile, bakedBump.name, out dm, out dn)) { if (dn != null) UnityEngine.Object.Destroy(dn); winBaked[key] = dm; return dm; }
            }
            int w, h; Color32[] bp;
            if (ReferenceEquals(bakedBump, lastBumpTex) && lastBumpPix != null) { w = bakedBump.width; h = bakedBump.height; bp = (Color32[])lastBumpPix.Clone(); }
            else bp = ReadTexWC(bakedBump, out w, out h);
            if (bp == null || bp.Length != w * h) return bakedBump;
            Color32[] obp = null;
            if (origBump != null) { try { obp = ReadBase(origBump, w, h); } catch { obp = null; } }
            int aw, ah; byte[] mk = WindowMask(maskAlbedo, out aw, out ah);
            if (mk == null) return bakedBump;
            Color32 flat = new Color32(128, 128, 255, 255);
            for (int y = 0; y < h; y++)
            {
                int ay = (ah == h) ? y : Mathf.Min(ah - 1, (int)((y + 0.5f) / h * ah));
                int row = y * w, arow = ay * aw;
                for (int x = 0; x < w; x++)
                {
                    int ax = (aw == w) ? x : Mathf.Min(aw - 1, (int)((x + 0.5f) / w * aw));
                    float mask = mk[arow + ax] / 255f;
                    if (mask <= 0.004f) continue;
                    Color32 o = (obp != null) ? obp[row + x] : flat;
                    Color32 b = bp[row + x];
                    bp[row + x] = new Color32(
                        (byte)(b.r + (o.r - b.r) * mask),
                        (byte)(b.g + (o.g - b.g) * mask),
                        (byte)(b.b + (o.b - b.b) * mask),
                        (byte)(b.a + (o.a - b.a) * mask));
                }
            }
            Texture2D outT = new Texture2D(w, h, TextureFormat.RGBA32, true);
            outT.SetPixels32(bp); outT.Apply(true, false);
            try { if (w % 4 == 0 && h % 4 == 0) outT.Compress(false); } catch { }
            if (diskFile != null && !SkipDiskWrite) SaveTexFile(diskFile, outT, null);
            winBaked[key] = outT;
            return outT;
        }

        static void BilinTab(float[] uv, int n, int dim, out int[] i0, out int[] i1, out float[] frac)
        {
            i0 = new int[n]; i1 = new int[n]; frac = new float[n];
            for (int i = 0; i < n; i++)
            {
                float f = uv[i] * dim - 0.5f;
                int a = (int)Mathf.Floor(f);
                float fr = f - a;
                if (a < 0) { a = 0; fr = 0f; }
                if (a > dim - 1) a = dim - 1;
                int b = a + 1; if (b > dim - 1) b = dim - 1;
                i0[i] = a; i1[i] = b; frac[i] = fr;
            }
        }
        static readonly Dictionary<int, Texture2D> blitCopies = new Dictionary<int, Texture2D>();

        static int lastSweepCount;

        internal static bool LowVram;
        static float bakeShrink = 1f;

        const float SmallSpanM = 2f;

        internal static bool SmallPaintPart(Part part)
        {
            if (part == null) return false;
            float[] us = UvWorldScale(part);
            return us != null && Mathf.Max(us[0], us[1]) < SmallSpanM;
        }

        static void BakeDims(Texture orig, bool small, out int w, out int h, out float shrink)
        {
            int scale = 1;
            while (Mathf.Max(orig.width, orig.height) * scale < 2048 && scale < 4) scale *= 2;
            w = Mathf.Max(4, orig.width * scale); h = Mathf.Max(4, orig.height * scale); shrink = 1f;
            if (!LowVram && !small) return;
            while (Mathf.Max(w, h) > 1024 && w >= 8 && h >= 8) { w /= 2; h /= 2; shrink *= 0.5f; }
        }
        public static bool IsBaked(Texture t) { return t != null && bakedToOrig.ContainsKey(t.GetInstanceID()); }
        public static bool TryFadeMask(string key, out float[] m) { return masks.TryGetValue(key, out m); }
        public static void PutFadeMask(string key, float[] m) { masks[key] = m; }

        public static bool NeedSweep() { int n = baked.Count + winBaked.Count; return n > 48 && n > lastSweepCount + 16; }

        static int KeyId(string key, int field)
        {
            string[] f = key.Split('|');
            int v;
            return (f.Length > field && int.TryParse(f[field], out v)) ? v : 0;
        }

        public static void EvictExcept(HashSet<int> live)
        {
            HashSet<int> keepOrig = new HashSet<int>();
            List<string> deadW = new List<string>();
            foreach (KeyValuePair<string, Texture2D> kv in winBaked)
            {
                if (kv.Value != null && live.Contains(kv.Value.GetInstanceID()))
                {
                    live.Add(KeyId(kv.Key, 1));
                    keepOrig.Add(KeyId(kv.Key, 2));
                }
                else deadW.Add(kv.Key);
            }
            for (int i = 0; i < deadW.Count; i++)
            {
                Texture2D wt;
                if (winBaked.TryGetValue(deadW[i], out wt) && wt != null) UnityEngine.Object.Destroy(wt);
                winBaked.Remove(deadW[i]);
            }
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, Texture2D> kv in baked)
                if (kv.Value == null || !live.Contains(kv.Value.GetInstanceID())) dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++)
            {
                string k = dead[i];
                Texture2D t; if (baked.TryGetValue(k, out t) && t != null) { bakedToOrig.Remove(t.GetInstanceID()); diskKeyOf.Remove(t.GetInstanceID()); UnityEngine.Object.Destroy(t); }
                baked.Remove(k);
                Texture2D bt; if (bakedBump.TryGetValue(k, out bt)) { if (bt != null) { bumpToOrig.Remove(bt.GetInstanceID()); bumpSynth.Remove(bt.GetInstanceID()); diskKeyOf.Remove(bt.GetInstanceID()); UnityEngine.Object.Destroy(bt); } bakedBump.Remove(k); }
            }
            List<string> deadM = new List<string>();
            foreach (KeyValuePair<string, Texture2D> kv in metalMasks)
                if (kv.Value == null || !live.Contains(kv.Value.GetInstanceID())) deadM.Add(kv.Key);
            for (int i = 0; i < deadM.Count; i++)
            {
                string k = deadM[i]; Texture2D mt;
                if (metalMasks.TryGetValue(k, out mt) && mt != null) UnityEngine.Object.Destroy(mt);
                metalMasks.Remove(k);
            }
            foreach (KeyValuePair<string, Texture2D> kv in baked)
            {
                Texture o;
                if (kv.Value != null && bakedToOrig.TryGetValue(kv.Value.GetInstanceID(), out o) && o != null) keepOrig.Add(o.GetInstanceID());
            }
            List<string> deadF = new List<string>();
            foreach (KeyValuePair<string, Texture2D[]> kv in tuFlatC)
            {
                Texture2D[] fv = kv.Value;
                bool used = fv != null && fv.Length == 2 && fv[0] != null && fv[1] != null
                    && (live.Contains(fv[0].GetInstanceID()) || live.Contains(fv[1].GetInstanceID()) || keepOrig.Contains(fv[0].GetInstanceID()));
                if (!used) deadF.Add(kv.Key);
            }
            for (int i = 0; i < deadF.Count; i++)
            {
                Texture2D[] fv;
                if (tuFlatC.TryGetValue(deadF[i], out fv) && fv != null)
                    for (int j = 0; j < fv.Length; j++) if (fv[j] != null) UnityEngine.Object.Destroy(fv[j]);
                tuFlatC.Remove(deadF[i]);
            }
            lastSweepCount = baked.Count + winBaked.Count;
        }

        public static void ClearAllBakes()
        {
            foreach (KeyValuePair<string, Texture2D> kv in baked) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            foreach (KeyValuePair<string, Texture2D> kv in bakedBump) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            foreach (KeyValuePair<string, Texture2D> kv in metalMasks) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            foreach (KeyValuePair<int, Texture2D> kv in blitCopies) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            foreach (KeyValuePair<string, Texture2D> kv in winBaked) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            baked.Clear(); bakedBump.Clear(); metalMasks.Clear(); blitCopies.Clear(); winBaked.Clear();
            bakedToOrig.Clear(); bumpToOrig.Clear(); bumpSynth.Clear();
            lastBake.Clear(); lastOrder.Clear(); masks.Clear();
            origPixC.Clear(); origWC.Clear(); origHC.Clear(); origOrder.Clear();
            winMaskC.Clear(); winMaskWC.Clear(); winMaskHC.Clear();
            edgeCache.Clear(); edgeOrder.Clear();
            basePixC.Clear(); baseOrder.Clear();
            foreach (KeyValuePair<int, Texture2D> kv in shinyMetalC) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            shinyMetalC.Clear();
            foreach (KeyValuePair<int, Texture2D> kv in stockGlossC) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            stockGlossC.Clear();
            foreach (KeyValuePair<string, Texture2D[]> kv in tuFlatC)
                if (kv.Value != null) for (int i = 0; i < kv.Value.Length; i++) if (kv.Value[i] != null) UnityEngine.Object.Destroy(kv.Value[i]);
            tuFlatC.Clear();
            lastMainTex = null; lastMainPix = null; lastBumpTex = null; lastBumpPix = null;
            diskKeyOf.Clear(); maskDiskTried.Clear();
            overlayPix.Clear(); overlayW.Clear(); overlayH.Clear(); overlayOrder.Clear();
            uvScaleCache.Clear();
            hasShinyC.Clear();
            lastSweepCount = 0;
        }

        const int PaintBlurRadius = 5;
        const float PaintEdgeLo = 6f;
        const float PaintEdgeHi = 35f;
        static readonly Dictionary<string, byte[]> edgeCache = new Dictionary<string, byte[]>();
        static readonly List<string> edgeOrder = new List<string>();

        internal static byte[] PaintEdgeMask(Color32[] bp, int w, int h, string keyBase)
        {
            return PaintEdgeMask(bp, w, h, keyBase, PaintEdgeLo, PaintEdgeHi);
        }
        internal static byte[] PaintEdgeMask(Color32[] bp, int w, int h, string keyBase, float edgeLo, float edgeHi)
        {
            byte[] cached;
            if (edgeCache.TryGetValue(keyBase, out cached) && cached != null && cached.Length == w * h) return cached;
            int n = w * h;
            bool par = n >= ParMinPixels;
            byte[] lum = new byte[n];
            ForRows(h, par, delegate(int y)
            {
                int row = y * w;
                for (int x = 0; x < w; x++) { Color32 c = bp[row + x]; lum[row + x] = (byte)(c.r * 0.299f + c.g * 0.587f + c.b * 0.114f); }
            });
            byte[] tmp = new byte[n];
            int r = Mathf.Max(2, Mathf.RoundToInt(PaintBlurRadius * bakeShrink));
            ForRows(h, par, delegate(int y)
            {
                int row = y * w;
                int sum = 0, cnt = 0;
                for (int x = 0; x <= r && x < w; x++) { sum += lum[row + x]; cnt++; }
                for (int x = 0; x < w; x++)
                {
                    tmp[row + x] = (byte)(sum / cnt);
                    int add = x + r + 1, rem = x - r;
                    if (add < w) { sum += lum[row + add]; cnt++; }
                    if (rem >= 0) { sum -= lum[row + rem]; cnt--; }
                }
            });
            byte[] blur = new byte[n];
            ForRows(w, par, delegate(int x)
            {
                int sum = 0, cnt = 0;
                for (int y = 0; y <= r && y < h; y++) { sum += tmp[y * w + x]; cnt++; }
                for (int y = 0; y < h; y++)
                {
                    blur[y * w + x] = (byte)(sum / cnt);
                    int add = y + r + 1, rem = y - r;
                    if (add < h) { sum += tmp[add * w + x]; cnt++; }
                    if (rem >= 0) { sum -= tmp[rem * w + x]; cnt--; }
                }
            });
            byte[] edge = new byte[n];
            float span = edgeHi - edgeLo; if (span < 1f) span = 1f;
            ForRows(h, par, delegate(int y)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    float hp = lum[i] - blur[i]; if (hp < 0f) hp = -hp;
                    float e = (hp - edgeLo) / span;
                    if (e < 0f) e = 0f; else if (e > 1f) e = 1f;
                    edge[i] = (byte)(e * 255f);
                }
            });
            edgeCache[keyBase] = edge;
            edgeOrder.Add(keyBase);
            while (edgeOrder.Count > 4) { string old = edgeOrder[0]; edgeOrder.RemoveAt(0); edgeCache.Remove(old); }
            return edge;
        }

        public static Texture BlitCopy(Texture orig)
        {
            if (orig == null) return null;
            Texture2D t;
            int bk = orig.GetInstanceID() ^ (LowVram ? 0x40000000 : 0);
            if (blitCopies.TryGetValue(bk, out t) && t != null) return t;
            int w, h; float sk;
            BakeDims(orig, false, out w, out h, out sk);
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(orig, rt);
            RenderTexture prev = RenderTexture.active; RenderTexture.active = rt;
            t = new Texture2D(w, h, TextureFormat.RGBA32, true);
            t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            t.Apply(true, false);
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            blitCopies[bk] = t;
            return t;
        }
        static int MaskHash(float[] m)
        {
            int h = 17;
            for (int i = 0; i < m.Length; i += 13) h = h * 31 + (int)(m[i] * 255f);
            return h;
        }
        static readonly Dictionary<string, Color32[]> basePixC = new Dictionary<string, Color32[]>();
        static readonly List<string> baseOrder = new List<string>();
        static Color32[] ReadBase(Texture src, int w, int h)
        {
            string k = src.GetInstanceID() + "|" + w + "x" + h;
            Color32[] px;
            if (basePixC.TryGetValue(k, out px) && px != null) return (Color32[])px.Clone();
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tmp = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tmp.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            px = tmp.GetPixels32();
            UnityEngine.Object.Destroy(tmp);
            basePixC[k] = px;
            baseOrder.Add(k);
            while (baseOrder.Count > 3) { basePixC.Remove(baseOrder[0]); baseOrder.RemoveAt(0); }
            return (Color32[])px.Clone();
        }

        public static Texture Baked(Texture orig, Texture origBump, string stackSpec, Part part, float patScale, out Texture bumpOut)
        { return Baked(orig, origBump, stackSpec, part, patScale, out bumpOut, null); }
        public static Texture Baked(Texture orig, Texture origBump, string stackSpec, Part part, float patScale, out Texture bumpOut, float[] maskOverride)
        {
            bumpOut = null;
            if (patScale <= 0.01f || float.IsNaN(patScale)) patScale = 1f;
            if (orig == null) return null;
            if (string.IsNullOrEmpty(stackSpec)) return orig;
            bool hasPaint = stackSpec.IndexOf("paint#", StringComparison.Ordinal) >= 0;
            bool hasColorTok = hasPaint || stackSpec.IndexOf("omul#", StringComparison.Ordinal) >= 0;
            if (hasColorTok && stackSpec.IndexOf(';') >= 0)
            {
                string[] parts0 = stackSpec.Split(';');
                List<string> nonp = new List<string>(), pnt = new List<string>();
                for (int i = 0; i < parts0.Length; i++)
                {
                    if (parts0[i].StartsWith("paint#") || parts0[i].StartsWith("omul#")) pnt.Add(parts0[i]); else nonp.Add(parts0[i]);
                }
                if (pnt.Count > 0 && nonp.Count > 0) { nonp.AddRange(pnt); stackSpec = string.Join(";", nonp.ToArray()); }
            }
            int _pIdx = stackSpec.IndexOf("paint#", StringComparison.Ordinal);
            int _oIdx = stackSpec.IndexOf("omul#", StringComparison.Ordinal);
            if (_pIdx < 0 || (_oIdx >= 0 && _oIdx < _pIdx)) _pIdx = _oIdx;
            string weatherSig = (_pIdx > 0) ? stackSpec.Substring(0, _pIdx) : "";
            string pname = (part != null) ? ((part.partInfo != null) ? part.partInfo.name : part.name) : "?";
            bool mir = part != null && part.transform != null && part.transform.localToWorldMatrix.determinant < 0f;
            bool hasTexLayer = false, allWhole = true, anyWorld = false;
            {
                string[] sp = stackSpec.Split(';');
                for (int i = 0; i < sp.Length; i++)
                {
                    string[] fz = sp[i].Split(':');
                    if (fz.Length < 3) continue;
                    if (!fz[0].StartsWith("tint#") && !fz[0].StartsWith("paint#") && !fz[0].StartsWith("omul#")) hasTexLayer = true;
                    if (fz[1] != "whole") allWhole = false;
                    if (WorldScaled(fz[0])) anyWorld = true;
                }
            }
            bool partFree = !hasTexLayer && allWhole && maskOverride == null;
            float[] uvs = null;
            if (anyWorld)
            {
                uvs = UvWorldScale(part);
                if (uvs == null) { float dm0 = PartMaxDimension(part); uvs = new float[] { dm0, dm0, 0f, 0f }; }
            }
            string sig = "";
            if (maskOverride != null) sig += "|k" + MaskHash(maskOverride);
            if (hasPaint) sig += "|pd" + PaintDetailCur.ToString("0.00");
            if (TexSkipCur && hasTexLayer) sig += "|ts";
            bool smallRes = !LowVram && SmallPaintPart(part);
            if (LowVram) sig += "|lv";
            else if (smallRes) sig += "|sr";
            string pkey = partFree ? "*" : (pname + "|s" + patScale.ToString("0.00") + (mir ? "|m" : "") + (uvs != null ? "|t" + uvs[0].ToString("0.000") + "," + uvs[1].ToString("0.000") + (uvs[2] > 0.5f ? "w" : "") + (uvs[3] > 0.5f ? "h" : "") : ""));
            string key = orig.GetInstanceID() + "|" + stackSpec + "|" + pkey + sig;
            string memKey = orig.GetInstanceID() + "|" + pkey + sig;
            Texture2D t;
            if (baked.TryGetValue(key, out t))
            {
                if (t != null)
                {
                    Texture2D btc;
                    bool haveBump = bakedBump.TryGetValue(key, out btc);

                    if (!haveBump || ReferenceEquals(btc, null)) return t;
                    if (btc != null) { bumpOut = btc; return t; }
                }

                baked.Remove(key); bakedBump.Remove(key);
                lastBake.Remove(memKey); lastOrder.Remove(memKey);
            }
            string diskFile = null;
            {
                string so = StableTex(orig), sbm = StableTex(origBump);
                if (so != null && so != "-" && sbm != null) diskFile = HashKey("b|" + so + "|" + sbm + "|" + stackSpec + "|" + pkey + sig);
            }
            if (diskFile != null)
            {
                Texture2D dm, dn;
                if (LoadTexFile(diskFile, orig.name + "_RDX_weathered", out dm, out dn))
                {
                    baked[key] = dm;
                    bakedToOrig[dm.GetInstanceID()] = orig;
                    diskKeyOf[dm.GetInstanceID()] = diskFile;
                    if (dn != null)
                    {
                        bakedBump[key] = dn;
                        diskKeyOf[dn.GetInstanceID()] = diskFile;
                        if (origBump != null) bumpToOrig[dn.GetInstanceID()] = origBump;
                        else bumpSynth.Add(dn.GetInstanceID());
                        bumpOut = dn;
                    }
                    else bakedBump[key] = null;
                    return dm;
                }
            }
            try
            {
                int w, h;
                BakeDims(orig, smallRes, out w, out h, out bakeShrink);

                string[] entries = stackSpec.Split(';');

                bool wantBump = false;
                for (int e2 = 0; e2 < entries.Length; e2++)
                {
                    string[] f2 = entries[e2].Split(':');
                    if (f2.Length < 3 || f2[0].StartsWith("tint#") || f2[0].StartsWith("paint#") || f2[0].StartsWith("omul#")) continue;
                    Color32[] tst; int tw, th;
                    if (GetOverlayPixels(f2[0] + "_n", out tst, out tw, out th, true)) { wantBump = true; break; }
                }

                Color32[] bp = null, nbase = null;
                int firstEntry = 0;
                BakeMem bm;
                if (lastBake.TryGetValue(memKey, out bm) && bm.w == w && bm.h == h
                    && stackSpec.StartsWith(bm.spec)
                    && (stackSpec.Length == bm.spec.Length || stackSpec[bm.spec.Length] == ';'))
                {
                    bp = (Color32[])bm.main.Clone();
                    if (wantBump && bm.bump != null) nbase = (Color32[])bm.bump.Clone();
                    firstEntry = bm.spec.Split(';').Length;
                }

                if (bp == null) { bp = ReadBase(orig, w, h); firstEntry = 0; }
                if (wantBump && nbase == null)
                {
                    if (origBump != null) nbase = ReadBase(origBump, w, h);
                    else
                    {
                        nbase = new Color32[w * h];
                        Color32 flat = new Color32(128, 128, 255, 128);
                        for (int i2 = 0; i2 < nbase.Length; i2++) nbase[i2] = flat;
                    }
                }

                int hsh = pname.GetHashCode();
                float partU = ((hsh >> 2) & 0x3FF) / 1024f;
                float partV = ((hsh >> 12) & 0x3FF) / 1024f;
                bool flipX = ((hsh >> 22) & 1) == 1;
                bool flipY = ((hsh >> 23) & 1) == 1;
                if (mir) flipX = !flipX;
                float[] colF = new float[w];
                float[] rowF = new float[h];
                byte[] wgtBuf = null;
                byte[] texEdgeShared = null;

                for (int e = firstEntry; e < entries.Length; e++)
                {
                    string[] f = entries[e].Split(':');
                    if (f.Length < 3) continue;
                    string name = f[0], place = f[1];
                    int lvl; if (!int.TryParse(f[2], out lvl)) lvl = 1;
                    bool isTint = name.StartsWith("tint#");
                    bool isPaint = name.StartsWith("paint#");
                    bool isOverlay = name.StartsWith("omul#");
                    bool isColorTok = isTint || isPaint || isOverlay;

                    if (!isColorTok && ((hsh >> 24) & 1) == 1)
                    {
                        Color32[] tvb; int tvw, tvh;
                        if (GetOverlayPixels(name + "_b", out tvb, out tvw, out tvh, true)) name = name + "_b";
                    }
                    Color32 tintC = new Color32(255, 255, 255, 255);
                    Color32[] ov = null; int ow = 0, oh = 0;
                    if (isTint) tintC = ParseHex(name.Substring(5));
                    else if (isPaint) tintC = ParseHex(name.Substring(6));
                    else if (isOverlay) tintC = ParseHex(name.Substring(5));
                    else if (!GetOverlayPixels(name, out ov, out ow, out oh)) continue;
                    Color32[] ovn = null; int ownw = 0, ownh = 0;
                    if (!isColorTok && nbase != null) GetOverlayPixels(name + "_n", out ovn, out ownw, out ownh, true);
                    bool isEmboss = name.StartsWith("grain");
                    float[] mask = (place == "whole") ? null : (maskOverride != null ? maskOverride : GetMask(part, place));
                    float strengthMul = (lvl <= 5) ? ((lvl >= 2) ? 2.5f : 0.6f) : lvl / 100f;
                    float strength = strengthMul * PatternStrength(name);
                    float layerScale = patScale;
                    if (f.Length >= 4)
                    {
                        float lsv;
                        if (float.TryParse(f[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lsv) && lsv > 0.01f) layerScale = lsv;
                    }
                    float tilesU = 1f, tilesV = 1f;
                    if (WorldScaled(name) && uvs != null)
                    {
                        float tm = TileMeters(name) * layerScale;
                        tilesU = Mathf.Max(0.01f, uvs[0] / tm);
                        tilesV = Mathf.Max(0.01f, uvs[1] / tm);
                        float lim = Mathf.Min(1f, Mathf.Min(w / bakeShrink / 160f / tilesU, h / bakeShrink / 160f / tilesV));
                        tilesU = Mathf.Clamp(tilesU * lim, 0.2f, 24f);
                        tilesV = Mathf.Clamp(tilesV * lim, 0.2f, 24f);
                        if (uvs[2] > 0.5f) tilesU = Mathf.Max(1f, Mathf.Round(tilesU));
                        if (uvs[3] > 0.5f) tilesV = Mathf.Max(1f, Mathf.Round(tilesV));
                    }

                    bool eFlipX = flipX ^ ((e & 1) == 1);
                    bool eFlipY = flipY ^ ((e & 2) == 2);
                    float uShift = partU * 3f + e * 0.41f;
                    for (int x = 0; x < w; x++)
                    {
                        float su = (x + 0.5f) / w * tilesU + uShift;
                        int txi = Mathf.FloorToInt(su);
                        float fu = su - txi;
                        if ((txi & 1) == 1) fu = 1f - fu;
                        if (eFlipX) fu = 1f - fu;
                        colF[x] = fu;
                    }
                    for (int y = 0; y < h; y++)
                    {
                        float sv = (y + 0.5f) / h * tilesV + partV * 3f + e * 0.29f;
                        int tyi = Mathf.FloorToInt(sv);
                        float fv = sv - tyi;
                        if ((tyi & 1) == 1) fv = 1f - fv;
                        if (eFlipY) fv = 1f - fv;
                        rowF[y] = fv;
                    }

                    int[] ax0 = null, ax1 = null, ay0 = null, ay1 = null;
                    int[] bx0 = null, bx1 = null, by0 = null, by1 = null;
                    float[] axf = null, ayf = null, bxf = null, byf = null;
                    if (!isColorTok)
                    {
                        BilinTab(colF, w, ow, out ax0, out ax1, out axf);
                        BilinTab(rowF, h, oh, out ay0, out ay1, out ayf);
                        if (ovn != null)
                        {
                            BilinTab(colF, w, ownw, out bx0, out bx1, out bxf);
                            BilinTab(rowF, h, ownh, out by0, out by1, out byf);
                        }
                    }

                    byte[] wgt = null;
                    if (mask != null)
                    {
                        if (wgtBuf == null) wgtBuf = new byte[w * h];
                        wgt = wgtBuf;
                        ForRows(h, w * h >= ParMinPixels, delegate(int y)
                        {
                            float v01 = (y + 0.5f) / h;
                            int off = y * w;
                            for (int x = 0; x < w; x++)
                                wgt[off + x] = (byte)(SampleMask(mask, (x + 0.5f) / w, v01) * 255f);
                        });
                    }

                    float s255 = strength / 255f;
                    const float inv255 = 1f / 255f;
                    string rbn = name.EndsWith("_b") ? name.Substring(0, name.Length - 2) : name;
                    float reliefMul;
                    if (name.StartsWith("heatd")) reliefMul = 0.14f;
                    else if (name.StartsWith("tiles")) reliefMul = 0.85f;
                    else if (DeepRelief.Contains(rbn)) reliefMul = 0.74f;
                    else reliefMul = 0.52f;
                    byte[] edgeMask = null;
                    if (isPaint)
                        edgeMask = PaintEdgeMask(bp, w, h, orig.GetInstanceID() + "|" + (weatherSig == "" ? "" : weatherSig + "|s" + patScale.ToString("0.00") + (mir ? "m" : "")) + "|" + w + "x" + h);
                    bool texLayer = !isColorTok;
                    byte[] texEdge = null;
                    if (texLayer && TexSkipCur)
                    {
                        if (texEdgeShared == null)
                            texEdgeShared = PaintEdgeMask(bp, w, h, orig.GetInstanceID() + "|texskip|s" + patScale.ToString("0.00") + (mir ? "m" : "") + "|" + w + "x" + h, 4f, 22f);
                        texEdge = texEdgeShared;
                    }
                    ForRows(h, w * h >= ParMinPixels, delegate(int y)
                    {
                        int rowOff = y * w;
                        int ayO0 = texLayer ? ay0[y] * ow : 0, ayO1 = texLayer ? ay1[y] * ow : 0;
                        int byO0 = (ovn != null) ? by0[y] * ownw : 0, byO1 = (ovn != null) ? by1[y] * ownw : 0;
                        float ayfy = texLayer ? ayf[y] : 0f, byfy = (ovn != null) ? byf[y] : 0f;
                        for (int x = 0; x < w; x++)
                        {
                            int idx = rowOff + x;
                            float a;
                            Color32 o;
                            if (isTint) { o = tintC; a = 0.85f * strength; }
                            else if (isPaint || isOverlay) { o = tintC; a = 1f; }
                            else
                            {
                                o = ov[(ayfy >= 0.5f ? ayO1 : ayO0) + (axf[x] >= 0.5f ? ax1[x] : ax0[x])];
                                if (o.a == 0 && ovn == null) continue;
                                a = o.a * s255;
                            }
                            if (!isPaint && !isOverlay && a > 0.95f) a = 0.95f;
                            if (wgt != null) a *= wgt[idx] * inv255;
                            if (texEdge != null) a *= 1f - texEdge[idx] * inv255;
                            if (a <= 0.004f) continue;
                            if (ovn != null)
                            {
                                Color32 on = ovn[(byfy >= 0.5f ? byO1 : byO0) + (bxf[x] >= 0.5f ? bx1[x] : bx0[x])];
                                Color32 nB = nbase[idx];
                                float na = a * reliefMul;
                                nbase[idx] = new Color32(
                                    (byte)(nB.r + (on.r - nB.r) * na),
                                    (byte)(nB.g + (on.g - nB.g) * na),
                                    (byte)(nB.b + (on.b - nB.b) * na),
                                    (byte)(nB.a + (on.a - nB.a) * na));
                            }
                            Color32 b = bp[idx];
                            if (isEmboss)
                            {
                                int nr = b.r + (int)((b.r * o.r / 128 - b.r) * a);
                                int ng = b.g + (int)((b.g * o.g / 128 - b.g) * a);
                                int nb2 = b.b + (int)((b.b * o.b / 128 - b.b) * a);
                                bp[idx] = new Color32(
                                    (byte)(nr < 0 ? 0 : (nr > 255 ? 255 : nr)),
                                    (byte)(ng < 0 ? 0 : (ng > 255 ? 255 : ng)),
                                    (byte)(nb2 < 0 ? 0 : (nb2 > 255 ? 255 : nb2)),
                                    b.a);
                            }
                            else if (isPaint)
                            {
                                float keep = (edgeMask[idx] * inv255) * PaintDetailCur;
                                if (keep > 1f) keep = 1f;
                                float rr = o.r + (b.r - o.r) * keep;
                                float gg = o.g + (b.g - o.g) * keep;
                                float bb3 = o.b + (b.b - o.b) * keep;
                                bp[idx] = new Color32(
                                    (byte)(b.r + (rr - b.r) * a),
                                    (byte)(b.g + (gg - b.g) * a),
                                    (byte)(b.b + (bb3 - b.b) * a),
                                    b.a);
                            }
                            else if (isTint || isOverlay)
                                bp[idx] = new Color32(
                                    (byte)(b.r + (b.r * o.r / 255 - b.r) * a),
                                    (byte)(b.g + (b.g * o.g / 255 - b.g) * a),
                                    (byte)(b.b + (b.b * o.b / 255 - b.b) * a),
                                    b.a);
                            else
                                bp[idx] = new Color32(
                                    (byte)(b.r + (o.r - b.r) * a),
                                    (byte)(b.g + (o.g - b.g) * a),
                                    (byte)(b.b + (o.b - b.b) * a),
                                    b.a);
                        }
                    });
                }

                t = new Texture2D(w, h, TextureFormat.RGBA32, true);
                t.SetPixels32(bp);
                t.Apply(true, false);
                t.name = orig.name + "_RDX_weathered";
                try { if (w % 4 == 0 && h % 4 == 0) t.Compress(false); } catch { }
                baked[key] = t;
                bakedToOrig[t.GetInstanceID()] = orig;
                lastMainTex = t; lastMainPix = bp;
                lastBumpTex = null; lastBumpPix = null;
                if (nbase != null)
                {
                    Texture2D bt = new Texture2D(w, h, TextureFormat.RGBA32, true);
                    bt.SetPixels32(nbase);
                    bt.Apply(true, false);
                    bt.name = orig.name + "_RDX_weathered_n";
                    try { if (w % 4 == 0 && h % 4 == 0) bt.Compress(false); } catch { }
                    bakedBump[key] = bt;
                    lastBumpTex = bt; lastBumpPix = nbase;
                    if (origBump != null) bumpToOrig[bt.GetInstanceID()] = origBump;
                    else bumpSynth.Add(bt.GetInstanceID());
                    bumpOut = bt;
                }
                else bakedBump[key] = null;
                if (diskFile != null)
                {
                    Texture2D sb2;
                    bakedBump.TryGetValue(key, out sb2);
                    diskKeyOf[t.GetInstanceID()] = diskFile;
                    if (sb2 != null) diskKeyOf[sb2.GetInstanceID()] = diskFile;
                    if (!SkipDiskWrite) SaveTexFile(diskFile, t, sb2);
                }
                if (!lastBake.ContainsKey(memKey)) lastOrder.Add(memKey);
                BakeMem nm = new BakeMem();
                nm.spec = stackSpec; nm.main = bp; nm.bump = nbase; nm.w = w; nm.h = h;
                lastBake[memKey] = nm;
                while (lastOrder.Count > BakeMemCap)
                {
                    lastBake.Remove(lastOrder[0]);
                    lastOrder.RemoveAt(0);
                }
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "weathering bake failed on " + orig.name + ": " + e.Message);
                return orig;
            }
            return t;
        }
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class ReforgedCacheJanitor : MonoBehaviour
    {
        void Awake() { DontDestroyOnLoad(gameObject); GameEvents.onGameSceneLoadRequested.Add(OnScene); }
        void OnDestroy() { GameEvents.onGameSceneLoadRequested.Remove(OnScene); }
        void OnScene(GameScenes s)
        {
            ModuleReforged.ClearLoadQueue();
            WeatherCache.ClearAllBakes();
            WeatherCache.TrimDiskCache();
        }

        float nextDecalCheck;

        void Update()
        {
            ModuleReforged.FlushMassChange();
            if (Time.realtimeSinceStartup >= nextDecalCheck)
            {
                nextDecalCheck = Time.realtimeSinceStartup + 0.5f;
                ModuleReforged.FixAllDecals();
                ModuleReforged.SweepOrphans();
            }
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            do { if (!ModuleReforged.PumpLoadQueue()) break; } while (sw.ElapsedMilliseconds < 8);
        }
    }
}
