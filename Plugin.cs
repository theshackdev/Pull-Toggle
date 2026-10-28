using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TheShack.PullToggle
{
    // OttoFuel refills fires, torches, smelters, kilns, ovens and shield generators from nearby chests, and
    // CraftFromContainers (CFC) uses nearby chests for building, crafting and cooking. Both pull from every chest in
    // range. This plugin adds a per-object switch:
    //
    //   - Chests: "Pulling: OFF" means neither mod takes anything from that chest. Switched with a button in the open
    //     chest window, or the toggle key (I) while looking at the chest or with it open.
    //   - Machines (anything OttoFuel refuels, plus cooking stations CFC loads): "Pulling: OFF" means the machine is
    //     not refilled from chests. Switched with the toggle key while looking at it. Filling it by hand still works.
    //
    // The switch is a bool on the object's ZDO, so it's part of the world save and the same for every player. Only
    // players with ward access can change it. Nothing changes until someone switches something off.
    //
    // Each mod looks up chests in one function (OttoFuel TastyUtils.GetNearbyContainers, CFC
    // BepInExPlugin.GetNearbyContainers); switched-off chests are removed from their results. OttoFuel refuels each
    // machine type from one function (RefuelTorch, RefuelSmelter, RefuelOven, RefuelGen), and CFC loads cooking
    // stations from one postfix (CookingStation_FindCookableItem_Patch); those are skipped for switched-off machines.
    //
    // Both mods are reached only through reflection and are optional: whatever is missing is logged and skipped.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(OttoFuelGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(CfcGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "theshack.pulltoggle";
        public const string PluginName = "Pull Toggle";
        public const string PluginVersion = "1.0.0";

        private const string OttoFuelGuid = "potto007.OttoFuel";
        private const string CfcGuid = "aedenthorn.CraftFromContainers";

        private const string RpcName = "TheShack_PullToggle_Set";
        private static readonly int OffKey = "TheShack_PullOff".GetStableHashCode();

        private const string OnText = "<color=#8BE28B>ON</color>";
        private const string OffText = "<color=#FF8A65>OFF</color>";

        private static ManualLogSource log;
        private static Plugin instance;
        private static ConfigEntry<KeyboardShortcut> toggleKey;
        private static bool active;

        private static FieldInfo cfcLastPosition; // CFC caches its chest list until the player moves; reset on a switch

        private static readonly AccessTools.FieldRef<InventoryGui, Container> currentContainer =
            AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
        private static readonly Dictionary<Type, FieldInfo> nviewFields = new Dictionary<Type, FieldInfo>();
        private static readonly ConditionalWeakTable<ZNetView, object> registered = new ConditionalWeakTable<ZNetView, object>();
        private static readonly object Registered = new object();

        private static Button chestButton;
        private static TMP_Text chestButtonText;

        private void Awake()
        {
            instance = this;
            log = Logger;
            toggleKey = Config.Bind("General", "Toggle Key", new KeyboardShortcut(KeyCode.I),
                "Switches resource pulling on or off for the chest or machine you are looking at, or the chest you have open.");

            Harmony harmony = new Harmony(PluginGuid);
            bool otto = TryPatch("OttoFuel", () => PatchOttoFuel(harmony));
            bool cfc = TryPatch("CraftFromContainers", () => PatchCraftFromContainers(harmony));
            active = otto || cfc;
            if (!active)
            {
                log.LogWarning("neither OttoFuel nor CraftFromContainers is hooked; nothing to switch");
                return;
            }
            try
            {
                harmony.PatchAll(typeof(Plugin).Assembly);
            }
            catch (Exception e)
            {
                log.LogError("could not apply the key, hover and chest-button hooks: " + e);
            }
            log.LogInfo($"ready (OttoFuel {(otto ? "hooked" : "not found")}, CraftFromContainers {(cfc ? "hooked" : "not found")}), key {toggleKey.Value}");
        }

        // Valheim methods that a game update removed are skipped (and logged) instead of failing the whole patch class.
        private static IEnumerable<MethodBase> Existing(params MethodBase[] methods)
        {
            foreach (MethodBase m in methods)
            {
                if (m != null)
                {
                    yield return m;
                }
            }
        }

        private static bool TryPatch(string mod, Func<bool> patch)
        {
            try
            {
                return patch();
            }
            catch (Exception e)
            {
                log.LogWarning($"could not hook {mod}: {e}");
                return false;
            }
        }

        // ---------------------------------------------------------------- hooks into the pulling mods

        private static bool PatchOttoFuel(Harmony harmony)
        {
            Type utils = AccessTools.TypeByName("OttoFuel.TastyUtils");
            MethodInfo find = utils == null ? null :
                AccessTools.Method(utils, "GetNearbyContainers", new[] { typeof(Vector3), typeof(float) });
            if (find == null)
            {
                return false;
            }
            harmony.Patch(find, postfix: new HarmonyMethod(typeof(Plugin), nameof(DropSwitchedOffChests)));

            (string type, string method)[] refuels =
            {
                ("OttoFuel.GameClasses.Fireplace_Patches", "RefuelTorch"),
                ("OttoFuel.GameClasses.Smelters", "RefuelSmelter"),
                ("OttoFuel.GameClasses.Oven_Patches", "RefuelOven"),
                ("OttoFuel.GameClasses.Sgenerator_Patches", "RefuelGen"),
            };
            foreach ((string type, string method) in refuels)
            {
                Type t = AccessTools.TypeByName(type);
                MethodInfo m = t == null ? null : AccessTools.Method(t, method);
                if (m == null)
                {
                    log.LogWarning($"OttoFuel {type}.{method} not found; those machines can't be switched off");
                    continue;
                }
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(SkipSwitchedOffMachine)));
            }
            return true;
        }

        private static bool PatchCraftFromContainers(Harmony harmony)
        {
            Type cfc = AccessTools.TypeByName("CraftFromContainers.BepInExPlugin");
            MethodInfo find = cfc == null ? null : AccessTools.Method(cfc, "GetNearbyContainers", new[] { typeof(Vector3) });
            if (find == null)
            {
                return false;
            }
            harmony.Patch(find, postfix: new HarmonyMethod(typeof(Plugin), nameof(DropSwitchedOffChests)));
            cfcLastPosition = AccessTools.Field(cfc, "lastPosition");

            Type cook = AccessTools.Inner(cfc, "CookingStation_FindCookableItem_Patch");
            MethodInfo cookPostfix = cook == null ? null : AccessTools.Method(cook, "Postfix");
            if (cookPostfix == null)
            {
                log.LogWarning("CraftFromContainers' cooking-station pull not found; cooking stations can't be switched off for it");
            }
            else
            {
                harmony.Patch(cookPostfix, prefix: new HarmonyMethod(typeof(Plugin), nameof(SkipSwitchedOffMachine)));
            }
            return true;
        }

        // Postfix on both mods' chest lookups. CFC returns its cached list object, so this also filters its cache.
        private static void DropSwitchedOffChests(List<Container> __result)
        {
            __result?.RemoveAll(c => c != null && IsOff(c));
        }

        // Prefix on the refuel/load methods; their first argument is the machine.
        private static bool SkipSwitchedOffMachine(object __0) => !(__0 is Component machine && machine != null && IsOff(machine));

        // ---------------------------------------------------------------- state

        private static ZNetView ViewOf(Component c)
        {
            Type t = c.GetType();
            if (!nviewFields.TryGetValue(t, out FieldInfo field))
            {
                field = AccessTools.Field(t, "m_nview");
                nviewFields[t] = field;
            }
            ZNetView view = field?.GetValue(c) as ZNetView;
            return view != null ? view : c.GetComponentInParent<ZNetView>();
        }

        private static bool IsOff(Component c)
        {
            ZNetView view = ViewOf(c);
            return view != null && view.IsValid() && view.GetZDO().GetBool(OffKey);
        }

        // Every machine and chest registers the RPC, so whoever owns the object can apply a switch from anyone.
        // Hooked where each class sets m_nview (ShieldGenerator does it in Start, the others in Awake).
        [HarmonyPatch]
        private static class RegisterRpcPatch
        {
            private static IEnumerable<MethodBase> TargetMethods() => Existing(
                AccessTools.Method(typeof(Container), "Awake"),
                AccessTools.Method(typeof(Fireplace), "Awake"),
                AccessTools.Method(typeof(Smelter), "Awake"),
                AccessTools.Method(typeof(CookingStation), "Awake"),
                AccessTools.Method(typeof(ShieldGenerator), "Start"));

            private static void Postfix(object __instance)
            {
                if (!(__instance is Component c))
                {
                    return;
                }
                ZNetView view = ViewOf(c);
                if (view == null || view.GetZDO() == null || registered.TryGetValue(view, out _))
                {
                    return;
                }
                registered.Add(view, Registered);
                view.Register<bool>(RpcName, (sender, off) =>
                {
                    if (view != null && view.IsValid() && view.IsOwner())
                    {
                        view.GetZDO().Set(OffKey, off);
                    }
                });
            }
        }

        // The owner applies the change. If the owner isn't a player who's online (the server, or someone who left),
        // the local player takes ownership and applies it.
        private static bool SetOff(Component target, bool off)
        {
            ZNetView view = ViewOf(target);
            if (view == null || !view.IsValid())
            {
                return false;
            }
            ZDO zdo = view.GetZDO();
            if (view.IsOwner())
            {
                zdo.Set(OffKey, off);
            }
            else if (IsOnlinePlayer(zdo.GetOwner()))
            {
                view.InvokeRPC(RpcName, off);
            }
            else
            {
                view.ClaimOwnership();
                zdo.Set(OffKey, off);
            }
            cfcLastPosition?.SetValue(null, Vector3.positiveInfinity);
            return true;
        }

        private static bool IsOnlinePlayer(long uid)
        {
            if (uid == 0L || ZNet.instance == null)
            {
                return false;
            }
            foreach (ZNet.PlayerInfo player in ZNet.instance.GetPlayerList())
            {
                if (player.m_characterID.UserID == uid)
                {
                    return true;
                }
            }
            return false;
        }

        private static void Toggle(Component target)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }
            if (!PrivateArea.CheckAccess(target.transform.position, 0f, true, false))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_privatezone");
                return;
            }
            bool off = !IsOff(target);
            if (!SetOff(target, off))
            {
                return;
            }
            string name = Localization.instance.Localize(target.GetComponentInParent<Piece>()?.m_name ?? "");
            player.Message(MessageHud.MessageType.Center, $"{name}{(name.Length > 0 ? ": " : "")}pulling {(off ? OffText : OnText)}");
        }

        // ---------------------------------------------------------------- what can be switched

        private static bool IsChest(Container c) =>
            c != null && (c.GetComponentInParent<Piece>() != null || c.m_wagon != null) && c.GetComponentInParent<TombStone>() == null;

        private static Component MachineAt(GameObject hover)
        {
            Smelter smelter = hover.GetComponentInParent<Smelter>();
            if (smelter != null)
            {
                return smelter;
            }
            CookingStation station = hover.GetComponentInParent<CookingStation>();
            if (station != null)
            {
                return station;
            }
            ShieldGenerator shield = hover.GetComponentInParent<ShieldGenerator>();
            if (shield != null)
            {
                return shield;
            }
            Fireplace fire = hover.GetComponentInParent<Fireplace>();
            return fire != null && !fire.m_infiniteFuel ? fire : null;
        }

        private static Component CurrentTarget()
        {
            if (InventoryGui.IsVisible())
            {
                Container open = InventoryGui.instance == null ? null : currentContainer(InventoryGui.instance);
                return IsChest(open) ? open : null;
            }
            GameObject hover = Player.m_localPlayer.GetHoverObject();
            if (hover == null)
            {
                return null;
            }
            Container chest = hover.GetComponentInParent<Container>();
            if (chest != null)
            {
                return IsChest(chest) ? chest : null;
            }
            return MachineAt(hover);
        }

        // ---------------------------------------------------------------- key, hover text, chest button

        private void Update()
        {
            if (!active || Player.m_localPlayer == null || !toggleKey.Value.IsDown() || IsTyping())
            {
                return;
            }
            Component target = CurrentTarget();
            if (target != null)
            {
                Toggle(target);
            }
        }

        private static bool IsTyping()
        {
            if ((Chat.instance != null && Chat.instance.HasFocus()) || Console.IsVisible() || TextInput.IsVisible() ||
                Menu.IsVisible() || Minimap.IsOpen())
            {
                return true;
            }
            GameObject selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            return selected != null && selected.GetComponent<TMP_InputField>() != null;
        }

        private static string HoverLine(bool off) =>
            $"\n[<color=yellow><b>{toggleKey.Value}</b></color>] Pulling: {(off ? OffText : OnText)}";

        [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
        private static class ChestHoverPatch
        {
            private static void Postfix(Container __instance, ref string __result)
            {
                if (active && !string.IsNullOrEmpty(__result) && IsChest(__instance))
                {
                    __result += HoverLine(IsOff(__instance));
                }
            }
        }

        [HarmonyPatch]
        private static class MachineHoverPatch
        {
            private static IEnumerable<MethodBase> TargetMethods() => Existing(
                AccessTools.Method(typeof(Fireplace), nameof(Fireplace.GetHoverText)),
                AccessTools.Method(typeof(CookingStation), nameof(CookingStation.GetHoverText)),
                AccessTools.Method(typeof(ShieldGenerator), nameof(ShieldGenerator.GetHoverText)),
                AccessTools.Method(typeof(Smelter), "OnHoverAddFuel"),
                AccessTools.Method(typeof(Smelter), "OnHoverAddOre"));

            private static void Postfix(object __instance, ref string __result)
            {
                if (!active || string.IsNullOrEmpty(__result) || !(__instance is Component c))
                {
                    return;
                }
                if (c is Fireplace fire && fire.m_infiniteFuel)
                {
                    return;
                }
                __result += HoverLine(IsOff(c));
            }
        }

        [HarmonyPatch(typeof(InventoryGui), "Update")]
        private static class ChestButtonPatch
        {
            private static void Postfix(InventoryGui __instance)
            {
                Container open = currentContainer(__instance);
                bool show = active && InventoryGui.IsVisible() && IsChest(open);
                if (chestButton == null)
                {
                    if (!show || !CreateChestButton(__instance))
                    {
                        return;
                    }
                }
                if (chestButton.gameObject.activeSelf != show)
                {
                    chestButton.gameObject.SetActive(show);
                }
                if (show)
                {
                    chestButtonText.text = $"Pulling: {(IsOff(open) ? OffText : OnText)}";
                }
            }
        }

        // A copy of the Take All button, pinned just above the chest window's top-right corner. Quick Stack moves the
        // Take All and Stack buttons around the bottom of the window, so the top is left to this one.
        private static bool CreateChestButton(InventoryGui gui)
        {
            if (gui.m_takeAllButton == null || gui.m_container == null)
            {
                return false;
            }
            GameObject go = UnityEngine.Object.Instantiate(gui.m_takeAllButton.gameObject, gui.m_container);
            go.name = "TheShack_PullToggleButton";
            foreach (UIGamePad pad in go.GetComponentsInChildren<UIGamePad>(true))
            {
                UnityEngine.Object.Destroy(pad); // no controller shortcut or hint copied from Take All
            }
            foreach (Localize localize in go.GetComponentsInChildren<Localize>(true))
            {
                UnityEngine.Object.Destroy(localize); // would reset the label to "Take All"
            }
            chestButton = go.GetComponent<Button>();
            chestButtonText = go.GetComponentInChildren<TMP_Text>(true);
            if (chestButton == null || chestButtonText == null)
            {
                UnityEngine.Object.Destroy(go);
                chestButton = null;
                return false;
            }
            chestButton.onClick = new Button.ButtonClickedEvent(); // drop Take All's own click handler
            chestButton.onClick.AddListener(() =>
            {
                Container open = InventoryGui.instance == null ? null : currentContainer(InventoryGui.instance);
                if (IsChest(open))
                {
                    Toggle(open);
                }
            });
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(0f, 6f);
            rect.sizeDelta = new Vector2(Mathf.Max(rect.sizeDelta.x, 150f), rect.sizeDelta.y);
            return true;
        }
    }
}
