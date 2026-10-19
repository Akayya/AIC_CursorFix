using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CursorFix
{
    /// <summary>
    /// Keeps a usable mouse pointer while a UnityModBase / BetterExperience GUI window is open.
    ///
    /// How the game does its cursor (XX.CURS in unsafeAssem.dll):
    ///   * it hides the OS cursor (Cursor.visible = false) and draws its own cursor sprite as a
    ///     mesh that lives in the world (follow_mouse / gui_draw_ state machine);
    ///   * XX.CURS.setTemporaryNormalMouse(true) is the game's own "give me a normal OS mouse"
    ///     switch: Cursor.SetCursor(null) + Cursor.visible = true + Gob.SetActive(false).
    ///
    /// The mod GUI (F1) is IMGUI and is drawn on top of the mesh cursor, so with the OS cursor
    /// hidden and the mesh hidden behind the window the player sees no pointer at all.
    ///
    /// Fix: while any UnityModBase GuiHostBase reports IsVisible, switch the game into its
    /// temporary-normal-mouse mode and keep Cursor.visible / Cursor.lockState fixed. On close the
    /// game's own mode is restored (setTemporaryNormalMouse(false)), so the in-game cursor returns.
    /// </summary>
    [BepInPlugin(Guid, "CursorFix", "1.2.0")]
    [BepInDependency("com.buele.betterexperience", BepInDependency.DependencyFlags.HardDependency)]
    public class CursorFixPlugin : BaseUnityPlugin
    {
        public const string Guid = "aic.cursorfix";

        internal static CursorFixPlugin Instance;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> AlsoUnlockCursor;
        internal static ConfigEntry<bool> HideGameCursor;

        // Static logging helpers: patch helpers are static, so they cannot use the instance Logger.
        internal static void LogInfo(string m) { if (Instance != null) Instance.Logger.LogInfo(m); }
        internal static void LogWarn(string m) { if (Instance != null) Instance.Logger.LogWarning(m); }
        internal static void LogError(string m) { if (Instance != null) Instance.Logger.LogError(m); }

        static readonly HashSet<object> VisibleHosts = new HashSet<object>();
        static PropertyInfo _isVisibleProp;

        static bool _guiHostPatched;
        static bool _gameCursorPatched;
        static bool _reportedGameCursor;

        static bool _overrideOn;
        static bool _saved;
        static bool _savedVisible;
        static CursorLockMode _savedLock = CursorLockMode.None;
        static bool _helperApplied;

        static FieldInfo _gobField;
        static bool _gobWasActive;
        static bool _gobTouched;
        static Type _cursType;
        static MethodInfo _setTemporaryNormalMouse;
        static bool _cursLookupDone;

        static Harmony _harmony;

        void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("General", "Enabled", true,
                "Show a usable mouse pointer while a UnityModBase (BetterExperience) GUI window is open.");
            AlsoUnlockCursor = Config.Bind("General", "AlsoUnlockCursor", true,
                "Also force Cursor.lockState = None while the GUI is open.");
            HideGameCursor = Config.Bind("General", "HideGameCursor", true,
                "While the GUI is open, also hide the game's own drawn cursor sprite (XX.CURS.Gob) " +
                "so it does not show up next to the system pointer.");

            int hosts = 0;
            int curs = 0;
            try
            {
                _harmony = new Harmony(Guid);
                hosts = PatchGuiHosts();
                curs = PatchGameCursor();
            }
            catch (Exception e)
            {
                LogError("CursorFix: failed to install patches: " + e);
            }

            LogInfo("CursorFix: patches installed = " + (hosts + curs) +
                           " (guiHost=" + hosts + ", gameCursor=" + curs + "), UnityModBase=" +
                           (FindAssembly("UnityModBase") != null));
        }

        void Start()
        {
            if (Enabled != null && Enabled.Value)
                StartCoroutine(PatchLoop());
        }

        IEnumerator PatchLoop()
        {
            var wait = new WaitForSecondsRealtime(0.5f);
            for (int i = 0; i < 60; i++)
            {
                yield return wait;
                if (_harmony == null) continue;
                try
                {
                    int before = (_guiHostPatched ? 1 : 0) + (_gameCursorPatched ? 1 : 0);
                    PatchGuiHosts();
                    PatchGameCursor();
                    int after = (_guiHostPatched ? 1 : 0) + (_gameCursorPatched ? 1 : 0);
                    if (after > before)
                        LogInfo("CursorFix: late patch succeeded (guiHost=" + (_guiHostPatched ? "yes" : "no") +
                                       ", gameCursor=" + (_gameCursorPatched ? "yes" : "no") + ")");
                    if (_guiHostPatched && _gameCursorPatched) yield break;
                }
                catch (Exception e)
                {
                    LogWarn("CursorFix: late patch attempt failed: " + e.Message);
                }
            }
            LogWarn("CursorFix: could not patch everything (guiHost=" + _guiHostPatched +
                              ", gameCursor=" + _gameCursorPatched + ").");
        }

        static Assembly FindAssembly(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == name) return asm;
            }
            try { return Assembly.Load(name); }
            catch { return null; }
        }

        // ---------------- UnityModBase: "is a GUI window open?" ----------------

        static int PatchGuiHosts()
        {
            if (_guiHostPatched) return 0;
            var asm = FindAssembly("UnityModBase");
            if (asm == null) return 0;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var setterPostfix = new HarmonyMethod(typeof(CursorFixPlugin).GetMethod("OnIsVisibleSet",
                BindingFlags.Static | BindingFlags.NonPublic));
            var updatePostfix = new HarmonyMethod(typeof(CursorFixPlugin).GetMethod("OnHostUpdate",
                BindingFlags.Static | BindingFlags.NonPublic));

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }

            int patched = 0;
            foreach (var t in types)
            {
                if (t == null || t.FullName == null) continue;
                if (t.FullName.IndexOf("GuiHost", StringComparison.Ordinal) < 0) continue;

                var prop = t.GetProperty("IsVisible", flags | BindingFlags.FlattenHierarchy);
                if (prop != null && prop.PropertyType == typeof(bool))
                {
                    if (_isVisibleProp == null) _isVisibleProp = prop;
                    var setter = prop.GetSetMethod(true);
                    if (setter != null)
                    {
                        try { _harmony.Patch(setter, postfix: setterPostfix); patched++; }
                        catch { /* best effort */ }
                    }
                }

                // Safety net: resync whenever the host ticks (covers direct field writes).
                MethodInfo update = null;
                try { update = t.GetMethod("Update", flags, null, Type.EmptyTypes, null); }
                catch { /* ignore */ }
                if (update != null)
                {
                    try { _harmony.Patch(update, postfix: updatePostfix); patched++; }
                    catch { /* best effort */ }
                }

                // Second safety net: OnGUI runs every frame while the component is enabled, so
                // reading IsVisible there catches anything Update's patch could miss.
                MethodInfo onGui = null;
                try { onGui = t.GetMethod("OnGUI", flags, null, Type.EmptyTypes, null); }
                catch { /* ignore */ }
                if (onGui != null && onGui.ReturnType == typeof(void))
                {
                    try { _harmony.Patch(onGui, postfix: updatePostfix); patched++; }
                    catch { /* best effort */ }
                }
            }

            if (patched > 0) _guiHostPatched = true;
            return patched;
        }

        static void OnIsVisibleSet(object __instance, bool __0)
        {
            Track(__instance, __0);
        }

        static void OnHostUpdate(object __instance)
        {
            if (__instance == null || _isVisibleProp == null) return;
            try { Track(__instance, (bool)_isVisibleProp.GetValue(__instance, null)); }
            catch { /* ignore */ }
        }

        static void Track(object host, bool visible)
        {
            if (host == null) return;
            if (visible) VisibleHosts.Add(host);
            else VisibleHosts.Remove(host);
        }

        static bool AnyWindowOpen()
        {
            if (VisibleHosts.Count == 0) return false;
            List<object> dead = null;
            bool open = false;
            foreach (var h in VisibleHosts)
            {
                var c = h as Component;
                // Unity's fake-null also matches destroyed components: prune those.
                if (c == null)
                {
                    if (dead == null) dead = new List<object>();
                    dead.Add(h);
                    continue;
                }
                if (!c.gameObject.activeInHierarchy) continue;
                var b = c as Behaviour;
                if (b != null && !b.enabled) continue;
                open = true;
            }
            if (dead != null)
                foreach (var d in dead) VisibleHosts.Remove(d);
            return open;
        }

        static string DescribeOpenWindows()
        {
            var sb = new StringBuilder();
            foreach (var h in VisibleHosts)
            {
                var c = h as Component;
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(c.GetType().FullName);
            }
            return sb.ToString();
        }

        // ---------------- the game's own cursor (XX.CURS) ----------------

        static int PatchGameCursor()
        {
            if (_gameCursorPatched) return 0;
            var asm = FindAssembly("unsafeAssem");
            if (asm == null) return 0;
            var t = asm.GetType("XX.CURS", false);
            if (t == null) return 0;

            _cursType = t;
            var postfix = new HarmonyMethod(typeof(CursorFixPlugin).GetMethod("OnGameCursorWrite",
                BindingFlags.Static | BindingFlags.NonPublic));

            // Methods that (re)hide the OS cursor or lock it: after they run, re-assert our state.
            var names = new HashSet<string>(StringComparer.Ordinal)
            {
                "fine", "fineMouse2", "fineCursorVisible", "setActive", "setTemporaryNormalMouse", "set_raw_cursor_draw"
            };

            int patched = 0;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            MethodInfo[] methods;
            try { methods = t.GetMethods(flags); }
            catch { methods = new MethodInfo[0]; }
            foreach (var md in methods)
            {
                if (!names.Contains(md.Name)) continue;
                try { _harmony.Patch(md, postfix: postfix); patched++; }
                catch { /* best effort */ }
            }

            if (_gobField == null)
            {
                try { _gobField = t.GetField("Gob", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static); }
                catch { /* ignore */ }
            }

            if (_setTemporaryNormalMouse == null)
            {
                try
                {
                    _setTemporaryNormalMouse = t.GetMethod("setTemporaryNormalMouse",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                        new[] { typeof(bool) }, null);
                }
                catch { /* ignore */ }
            }

            if (patched > 0)
            {
                _gameCursorPatched = true;
                _cursLookupDone = true;
                if (!_reportedGameCursor)
                {
                    _reportedGameCursor = true;
                    LogInfo("CursorFix: hooked XX.CURS (" + patched + " methods), setTemporaryNormalMouse=" +
                                   (_setTemporaryNormalMouse != null));
                }
            }
            return patched;
        }

        // Runs after the game (re)writes the OS cursor while our override is on.
        static void OnGameCursorWrite()
        {
            if (!_overrideOn) return;
            KeepCursorState();
        }

        // Called by the game while our override is active: keep the OS mouse usable.
        static void KeepCursorState()
        {
            try
            {
                if (!Cursor.visible) Cursor.visible = true;
                if (AlsoUnlockCursor != null && AlsoUnlockCursor.Value && Cursor.lockState != CursorLockMode.None)
                    Cursor.lockState = CursorLockMode.None;
            }
            catch { /* ignore */ }
        }

        // ---------------- runtime ----------------

        void LateUpdate()
        {
            Reassert();
        }

        void OnEnable()
        {
            StartCoroutine(EndOfFrameLoop());
        }

        // The game may touch the cursor later than LateUpdate (render callbacks / coroutines),
        // so re-assert once more when the frame is over.
        IEnumerator EndOfFrameLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                Reassert();
            }
        }

        void Reassert()
        {
            bool want = Enabled != null && Enabled.Value && AnyWindowOpen();

            if (want)
            {
                if (!_overrideOn) Open();
                Keep();
            }
            else if (_overrideOn)
            {
                Close();
            }
        }

        void Open()
        {
            _overrideOn = true;
            _saved = true;
            _savedVisible = Cursor.visible;
            _savedLock = Cursor.lockState;

            LogInfo("CursorFix: GUI opened (visible=" + _savedVisible + ", lockState=" + _savedLock +
                           ", window=" + DescribeOpenWindows() + ")");

            // Remember the game's sprite cursor state before the helper touches it.
            _gobWasActive = GameCursorActive();
            _gobTouched = false;

            _helperApplied = UseGameNormalMouse(true);
            if (!_helperApplied)
            {
                // Fallback: plain Unity cursor state.
                try
                {
                    Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
                    Cursor.visible = true;
                }
                catch (Exception e) { LogWarn("CursorFix: cursor update failed: " + e.Message); }
            }
            KeepCursorState();
            HideGameCursorIfNeeded();

            LogInfo("CursorFix: override on -> cursor.visible=" + Cursor.visible + ", lockState=" + Cursor.lockState +
                    ", gameCursorActive=" + GameCursorActive());
        }

        void Keep()
        {
            // The game may drop our state again (e.g. it calls setTemporaryNormalMouse(false)
            // while a scene changes), so re-arm and re-assert every frame.
            if (!_helperApplied)
                _helperApplied = UseGameNormalMouse(true);
            KeepCursorState();
            HideGameCursorIfNeeded();
        }

        void Close()
        {
            _overrideOn = false;
            bool restoredByGame = false;

            if (_helperApplied)
                restoredByGame = UseGameNormalMouse(false);

            _helperApplied = false;

            try
            {
                // Restore exactly what we found. (setTemporaryNormalMouse(false) is a no-op while
                // the game cursor is inactive, so trust our own snapshot rather than the game.)
                if (_saved)
                {
                    Cursor.visible = _savedVisible;
                    Cursor.lockState = _savedLock;
                }
            }
            catch (Exception e) { LogWarn("CursorFix: cursor restore failed: " + e.Message); }

            if (_gobTouched && _gobWasActive)
            {
                var go = GameCursorObject();
                if (go != null && !go.activeSelf)
                {
                    try { go.SetActive(true); }
                    catch (Exception e) { LogWarn("CursorFix: restoring the game cursor sprite failed: " + e.Message); }
                }
            }
            _gobTouched = false;
            _gobWasActive = false;

            _saved = false;
            LogInfo("CursorFix: GUI closed (visible=" + Cursor.visible + ", lockState=" + Cursor.lockState +
                           ", restoredByGame=" + restoredByGame + ")");
        }

        // The game's own cursor sprite ("Gob" in XX.CURS, an active GameObject while the game
        // draws its own pointer). Hiding it avoids a second pointer next to the system one.
        static GameObject GameCursorObject()
        {
            if (_gobField == null) return null;
            try { return _gobField.GetValue(null) as GameObject; }
            catch { return null; }
        }

        static bool GameCursorActive()
        {
            var go = GameCursorObject();
            try { return go != null && go.activeSelf; }
            catch { return false; }
        }

        static void HideGameCursorIfNeeded()
        {
            if (HideGameCursor == null || !HideGameCursor.Value) return;
            var go = GameCursorObject();
            if (go == null) return;
            try
            {
                if (go.activeSelf)
                {
                    _gobTouched = true;
                    go.SetActive(false);
                }
            }
            catch { /* ignore */ }
        }

        static bool UseGameNormalMouse(bool on)
        {
            if (_setTemporaryNormalMouse == null) return false;
            try
            {
                _setTemporaryNormalMouse.Invoke(null, new object[] { on });
                return true;
            }
            catch (Exception e)
            {
                if (Instance != null) LogWarn("CursorFix: setTemporaryNormalMouse(" + on + ") failed: " + e.Message);
                return false;
            }
        }
    }
}




