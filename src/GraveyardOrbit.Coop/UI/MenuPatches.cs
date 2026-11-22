using GraveyardOrbit.Audio;
using GraveyardOrbit.UI;
using HarmonyLib;
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Injects a MULTIPLAYER entry into the title menu, between PLAY/NEW GAME
    /// and CONTRACTS, reusing the exact menu item styling.
    /// </summary>
    [HarmonyPatch(typeof(MainMenuController), "BuildUI")]
    internal static class MainMenuBuildUiPatch
    {
        private const string ButtonName = "MultiplayerBtn";

        internal static VisualElement LastMenuRoot;

        private static void Postfix(MainMenuController __instance)
        {
            try { Inject(__instance); }
            catch (Exception ex) { CoopPlugin.Log.LogError("[menu] multiplayer button injection failed: " + ex); }
        }

        private static void Inject(MainMenuController menu)
        {
            CombinedContract.EnsureInjected();

            VisualElement root = menu.Root;
            if (root == null) return;
            LastMenuRoot = root;
            if (root.Q<Button>(ButtonName) != null) return;

            var contractsBtn = root.Q<Button>("NewGameBtn");
            var settingsBtn = root.Q<Button>("SettingsBtn");
            var anchor = contractsBtn ?? settingsBtn;
            if (anchor == null || anchor.parent == null) return;

            var button = new Button { name = ButtonName, text = "" };

            // Inherit the UXML classes and inline layout from a real menu entry.
            foreach (string cls in anchor.GetClasses()) button.AddToClassList(cls);
            button.style.width = anchor.style.width;
            button.style.height = anchor.style.height;
            button.style.marginLeft = anchor.style.marginLeft;
            button.style.marginRight = anchor.style.marginRight;
            button.style.marginTop = anchor.style.marginTop;
            button.style.marginBottom = anchor.style.marginBottom;
            button.style.alignSelf = anchor.style.alignSelf;
            button.style.flexGrow = anchor.style.flexGrow;
            button.RemoveFromClassList("menu-item--on");
            button.AddToClassList("menu-item");
            button.AddToClassList("sfx-owns-hover");

            var title = new Label("MULTIPLAYER") { pickingMode = PickingMode.Ignore };
            title.AddToClassList("menu-item__title");
            var subtitleText = "STEAM CO-OP · " + CoopPlugin.BuildLabel;
            var subtitle = new Label(subtitleText) { pickingMode = PickingMode.Ignore };
            subtitle.AddToClassList("menu-item__sub");
            subtitle.style.paddingRight = Mathf.Max(0, subtitleText.Length - 1) * 2f;
            button.Add(title);
            button.Add(subtitle);

            var slab = new SlabFrame
            {
                Notch = 20f,
                Stroke = 3f,
                StrokeColor = MetaUi.Hex(16032824),
                FillColor = MetaUi.Hex(3679050),
                TintColor = Color.clear
            };
            slab.Behind(button);
            slab.style.display = DisplayStyle.None;

            RegisterItem(menu, button, slab);
            WireHover(menu, button);

            int index = anchor.parent.IndexOf(anchor);
            anchor.parent.Insert(index, button);

            button.clicked += delegate { MultiplayerLobby.Open(menu.Root); };

            if (CoopPlugin.Cfg.DebugOpenLobbyOnMenu.Value)
            {
                menu.Root.schedule.Execute(() => MultiplayerLobby.Open(menu.Root)).ExecuteLater(1200);
            }

            CoopPlugin.Log.LogInfo("[menu] MULTIPLAYER entry injected.");
        }

        /// <summary>Add to the controller's private _items list so selection/hover logic works.</summary>
        private static void RegisterItem(MainMenuController menu, Button button, SlabFrame slab)
        {
            var field = AccessTools.Field(typeof(MainMenuController), "_items");
            if (field == null) return;
            var list = field.GetValue(menu) as IList;
            if (list == null) return;

            Type tupleType = field.FieldType.GetGenericArguments()[0];
            object tuple = Activator.CreateInstance(tupleType, button, slab);
            list.Add(tuple);
        }

        private static void WireHover(MainMenuController menu, Button button)
        {
            var select = AccessTools.Method(typeof(MainMenuController), "SelectItem");
            if (select == null) return;

            void Hover()
            {
                if (button.ClassListContains("menu-item--on")) return;
                select.Invoke(menu, new object[] { button });
                try { Sfx.Play("button_hover"); } catch { }
            }

            button.RegisterCallback<PointerEnterEvent>(_ => Hover());
            button.RegisterCallback<PointerMoveEvent>(_ => Hover());
        }
    }
}
