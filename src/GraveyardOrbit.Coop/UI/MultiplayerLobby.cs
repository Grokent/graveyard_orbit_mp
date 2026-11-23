using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using GraveyardOrbit.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace GraveyardOrbit.Coop
{
    /// <summary>
    /// Multiplayer lobby, built in the game's own menu style (MenuStyles palette,
    /// MetaUi fonts, SlabFrame borders) with full controller navigation.
    ///
    /// Navigation model — four stops switched with LB/RB, so the D-pad/stick only
    /// ever moves inside one stop:
    ///   CREW      roster + leave session
    ///   FRIENDS   steam invites
    ///   CONTRACT  map grid
    ///   START     the pinned launch button (kept above the map pane, so it is
    ///             visible and reachable from any contract)
    /// Inside a stop, up/down moves between rows, A acts, B closes the lobby.
    ///
    /// UI-scaling mod compatibility (Graveyard Orbit UI Scaling, text 100-200%):
    ///  - every row uses minHeight, never a fixed height, so larger type grows it;
    ///  - long strings ellipsize instead of overrunning their container;
    ///  - font sizes are set once at creation and never reassigned, because that
    ///    mod rebases its baseline whenever it sees an element's size change;
    ///  - the map pane scrolls (right stick), so a 200% description stays readable.
    /// </summary>
    public static class MultiplayerLobby
    {
        private const float PanelW = 1720f;
        private const float PanelH = 1000f;

        private static readonly string[] StopNames = { "SESSION", "CONTRACT", "START" };
        private const int StopSession = 0;
        private const int StopContract = 1;
        private const int StopStart = 2;

        private static VisualElement _modal;
        private static VisualElement _panel;
        private static Label _status;
        private static VisualElement _hints;

        private static VisualElement _tabStrip;
        private static readonly Label[] _chipLabels = new Label[3];
        private static readonly VisualElement[] _tabs = new VisualElement[3];

        private static VisualElement _crewHost;
        private static VisualElement _friendsHost;
        private static VisualElement _contractsHost;

        private static VisualElement _detailHost;
        private static VisualElement _detailFriends;   // friends invites, beside the roster
        private static VisualElement _detailContract;  // map pane
        private static ScrollView _detailScroll;

        private static VisualElement _startBtn;
        private static Label _startLabel;
        private static Label _launchMap, _launchInfo;
        private static VisualElement _leaveBtn;
        private static Label _leaveLabel;
        private static VisualElement _inviteOthersRow;

        private static VisualElement _mapThumbHost;
        private static Label _mapName, _mapDesc, _mapStatus;
        private static readonly VisualElement[] _statChips = new VisualElement[3];
        private static readonly Label[] _statLabels = new Label[3];
        private static VisualElement _layersRow;
        private static VisualElement _layersDots;

        private static IVisualElementScheduledItem _pump;
        private static IVisualElementScheduledItem _tick;
        private static float _scale = 1f;
        private static bool _subscribed;
        private static int _stop;

        private static readonly Dictionary<ulong, Texture2D> _avatars = new Dictionary<ulong, Texture2D>();
        private static string _friendsSignature;

        // ---- contract cards + map pane ----
        private struct ContractCard
        {
            public VisualElement row;
            public Label name;
            public Label cells;
            public SlabFrame slab;
            public string countText;
        }

        private static readonly Dictionary<string, ContractCard> _contractRows =
            new Dictionary<string, ContractCard>();
        private static readonly Dictionary<ulong, VisualElement> _friendRows =
            new Dictionary<ulong, VisualElement>();
        private static readonly Dictionary<string, VisualElement> _thumbs =
            new Dictionary<string, VisualElement>();
        private static List<(CSteamID id, string name, string state, int rank)> _friendsCache =
            new List<(CSteamID, string, string, int)>();

        private static string _contractsSignature;
        private static string _detailContractId;   // follows focus, never sent to peers

        public static bool IsOpen => _modal != null && _modal.panel != null;

        /// <summary>Open the lobby automatically when a friend's invite drops us into a session.</summary>
        public static void TryAutoOpen()
        {
            if (IsOpen) return;
            if (CoopSession.Mode == SessionMode.Off) return;
            VisualElement root = MainMenuBuildUiPatch.LastMenuRoot;
            if (root == null || root.panel == null) return;
            Open(root);
        }

        // ------------------------------------------------------------- open/close

        public static void Open(VisualElement host)
        {
            if (host == null) return;
            if (IsOpen) { Refresh(); return; }

            _modal = host.Q<VisualElement>("coop-lobby");
            if (_modal != null) { Refresh(); return; }

            float h = host.panel != null ? host.panel.visualTree.layout.height : host.layout.height;
            _scale = (float.IsNaN(h) || h < 10f) ? 1f : h / 1080f;

            _modal = new VisualElement { name = "coop-lobby" };
            _modal.style.position = Position.Absolute;
            _modal.style.left = 0; _modal.style.top = 0; _modal.style.right = 0; _modal.style.bottom = 0;
            _modal.style.alignItems = Align.Center;
            _modal.style.justifyContent = Justify.Center;
            _modal.style.backgroundColor = (Color)new Color32(3, 3, 6, 180);
            ConsoleBlur.Behind(_modal);

            // Scopes pad navigation to this window (main menu entries behind the
            // scrim stop being reachable) and makes B close it.
            _modal.AddToClassList("pad-modal");

            _contractsSignature = null;
            _contractRows.Clear();
            _friendRows.Clear();
            _thumbs.Clear();
            _friendsCache = new List<(CSteamID, string, string, int)>();
            _detailContractId = null;

            _panel = new VisualElement();
            _panel.style.width = P(PanelW);
            _panel.style.height = P(PanelH);
            _panel.style.flexDirection = FlexDirection.Column;
            _panel.style.flexShrink = 0f;
            PaintPanel(_panel);
            _modal.Add(_panel);
            host.Add(_modal);
            PadNav.SetModalBack(_modal, Close);

            BuildHeader();
            BuildTabs();
            BuildBody();
            BuildFooter();

            if (!_subscribed)
            {
                CoopSession.SessionChanged += Refresh;
                CoopSession.RosterChanged += Refresh;
                CoopSession.ContractChanged += OnContractChanged;
                _subscribed = true;
            }

            // Opening the window is the "go multiplayer" intent: host a lobby.
            if (CoopSession.Mode == SessionMode.Off && SteamReady())
            {
                CoopSession.HostSteam();
            }

            ShowStop(_stop, focus: false);
            _pump = _modal.schedule.Execute(RefreshFriendsOnly).Every(2000);
            _tick = _modal.schedule.Execute(PollInput).Every(0L);
            Refresh();

            if (PadNav.Active || In.PadLatched) FocusLater(StopEntry());
        }

        /// <summary>Light periodic pump: friends/status only, so lists don't jump or flicker.</summary>
        private static void RefreshFriendsOnly()
        {
            if (_modal == null || _panel == null) return;
            bool steam = SteamReady();

            if (_status != null && Time.unscaledTime >= _hintUntil)
            {
                string text; Color color;
                StatusText(steam, out text, out color);
                _status.text = text;
                _status.style.color = color;
            }

            if (_inviteOthersRow != null) SetRowEnabled(_inviteOthersRow, steam && CoopSession.Mode != SessionMode.Off);
            RebuildFriends(steam);
        }

        public static void Close()
        {
            try { _pump?.Pause(); } catch { }
            try { _tick?.Pause(); } catch { }
            _pump = null;
            _tick = null;

            if (_subscribed)
            {
                CoopSession.SessionChanged -= Refresh;
                CoopSession.RosterChanged -= Refresh;
                CoopSession.ContractChanged -= OnContractChanged;
                _subscribed = false;
            }

            if (_modal != null)
            {
                _modal.RemoveFromHierarchy();
                _modal = null;
            }

            _panel = null;
            _tabStrip = null;
            _crewHost = _friendsHost = _contractsHost = null;
            _detailHost = _detailFriends = _detailContract = null;
            _detailScroll = null;
            _startBtn = _leaveBtn = _inviteOthersRow = null;
            _startLabel = _leaveLabel = _launchMap = _launchInfo = null;
            _mapThumbHost = null;
            _mapName = _mapDesc = _mapStatus = null;
            _layersRow = _layersDots = null;
            for (int i = 0; i < _chipLabels.Length; i++) _chipLabels[i] = null;
            for (int i = 0; i < 3; i++) { _tabs[i] = null; _statChips[i] = null; _statLabels[i] = null; }
            _hints = null;
            _status = null;
            _friendsSignature = null;
            _contractsSignature = null;
            _contractRows.Clear();
            _friendRows.Clear();
            _thumbs.Clear();
            _friendsCache = new List<(CSteamID, string, string, int)>();
            _detailContractId = null;
            _hintUntil = 0f;
        }

        private static void OnContractChanged(string id) => Refresh();

        // ------------------------------------------------------------- chrome

        private static void BuildHeader()
        {
            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;
            head.style.marginBottom = P(8f);
            head.style.flexShrink = 0f;

            head.Add(MetaUi.Text("MULTIPLAYER", MetaUi.Face.Display, 44f, MetaUi.OrangeTxt, 1f));

            var spacer = new VisualElement { style = { flexGrow = 1f } };
            head.Add(spacer);

            var build = MetaUi.Text(CoopPlugin.BuildLabel, MetaUi.Face.Display, 16f, MenuStyles.Dim, 1.5f);
            build.style.unityTextAlign = TextAnchor.MiddleRight;
            head.Add(build);

            _panel.Add(head);

            var rule = new VisualElement();
            rule.style.height = P(2f);
            rule.style.backgroundColor = Fade(MetaUi.Orange, 0.35f);
            rule.style.marginTop = P(2f);
            rule.style.marginBottom = P(12f);
            rule.style.flexShrink = 0f;
            _panel.Add(rule);
        }

        /// <summary>Stop strip. LB/RB switch stops; the chips also click with a mouse.</summary>
        private static void BuildTabs()
        {
            _tabStrip = new VisualElement();
            _tabStrip.style.flexDirection = FlexDirection.Row;
            _tabStrip.style.alignItems = Align.Center;
            _tabStrip.style.minHeight = P(44f);
            _tabStrip.style.flexShrink = 0f;
            _tabStrip.style.marginBottom = P(12f);
            _panel.Add(_tabStrip);

            _tabStrip.Add(HintPair("lb", "rb", "SECTION"));

            var gap = new VisualElement();
            gap.style.width = P(18f);
            _tabStrip.Add(gap);

            for (int i = 0; i < StopNames.Length; i++) _chipLabels[i] = TabChip(i, StopNames[i]);

            var spacer = new VisualElement { style = { flexGrow = 1f } };
            _tabStrip.Add(spacer);

            var tip = MetaUi.Text("A · SELECT   B · CLOSE", MetaUi.Face.Body, 13f, MenuStyles.Dim2, 1.2f);
            tip.style.unityTextAlign = TextAnchor.MiddleRight;
            PadNav.PadOnly(tip);
            _tabStrip.Add(tip);
        }

        private static Label TabChip(int index, string name)
        {
            var chip = new VisualElement();
            chip.style.flexDirection = FlexDirection.Row;
            chip.style.alignItems = Align.Center;
            chip.style.justifyContent = Justify.Center;
            chip.style.minHeight = P(40f);
            chip.style.minWidth = P(170f);
            chip.style.marginRight = P(10f);
            chip.style.paddingLeft = P(18f);
            chip.style.paddingRight = P(18f);
            chip.style.paddingTop = P(4f);
            chip.style.paddingBottom = P(4f);
            chip.style.backgroundColor = MetaUi.Hex(0x12121A);
            MetaUi.Slab(chip, index == StopStart ? MetaUi.Green : MetaUi.Orange, true, PF(8f), PF(1.5f));

            var label = MetaUi.Text(name, MetaUi.Face.Display, 18f, MenuStyles.Dim, 1.5f);
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            chip.Add(label);

            chip.AddManipulator(new Clickable(() => ShowStop(index, focus: true)));
            _tabStrip.Add(chip);
            return label;
        }

        private static void BuildBody()
        {
            var body = new VisualElement();
            body.style.flexDirection = FlexDirection.Row;
            body.style.flexGrow = 1f;
            body.style.minHeight = 0f;
            _panel.Add(body);

            // ---- stop content (left) ----
            var content = new VisualElement();
            content.style.width = Length.Percent(60f);
            content.style.flexShrink = 0f;
            content.style.minHeight = 0f;
            content.style.marginRight = P(22f);
            body.Add(content);

            _tabs[StopSession] = BuildCrewTab();
            _tabs[StopContract] = BuildContractTab();
            _tabs[StopStart] = BuildLaunchTab();
            for (int i = 0; i < _tabs.Length; i++) content.Add(_tabs[i]);

            // ---- detail (right) ----
            _detailHost = new VisualElement();
            _detailHost.style.flexGrow = 1f;
            _detailHost.style.minWidth = 0f;
            _detailHost.style.minHeight = 0f;
            _detailHost.style.paddingLeft = P(26f);
            _detailHost.style.borderLeftWidth = PF(1f);
            _detailHost.style.borderLeftColor = Fade(MetaUi.Orange, 0.22f);
            body.Add(_detailHost);

            _detailFriends = BuildFriendsPane();
            _detailContract = DetailPane();
            BuildContractDetail(_detailContract);

            _detailHost.Add(_detailFriends);
            _detailHost.Add(_detailContract);
        }

        private static VisualElement DetailPane()
        {
            var pane = new VisualElement();
            pane.style.flexDirection = FlexDirection.Column;
            pane.style.flexGrow = 1f;
            pane.style.minHeight = 0f;
            return pane;
        }

        private static VisualElement BuildCrewTab()
        {
            var tab = Section();
            tab.Add(MetaUi.Text("CREW", MetaUi.Face.DisplayBold, 20f, MetaUi.OrangeTxt, 1.5f));
            var sub = MetaUi.Text("WHO IS IN THIS SESSION", MetaUi.Face.Body, 12f, MenuStyles.Dim2, 1f);
            sub.style.marginBottom = P(14f);
            tab.Add(sub);

            _crewHost = new VisualElement();
            tab.Add(_crewHost);

            tab.Add(new VisualElement { style = { flexGrow = 1f } });

            _leaveBtn = MakeRow("LEAVE SESSION", Leave, MenuStyles.Crimson);
            _leaveLabel = _leaveBtn.Q<Label>();
            Pad(_leaveBtn, Leave);
            tab.Add(_leaveBtn);
            return tab;
        }

        /// <summary>Invites live beside the roster: one screen for "who is here / who to add".</summary>
        private static VisualElement BuildFriendsPane()
        {
            var pane = DetailPane();
            pane.Add(MetaUi.Text("FRIENDS", MetaUi.Face.DisplayBold, 20f, MetaUi.OrangeTxt, 1.5f));
            var sub = MetaUi.Text("ONLINE NOW · INVITE STRAIGHT INTO THIS SESSION", MetaUi.Face.Body, 12f, MenuStyles.Dim2, 1f, wrap: true);
            sub.style.marginBottom = P(12f);
            pane.Add(sub);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            scroll.style.minHeight = 0f;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            MetaUi.AccentScroll(scroll, Fade(MetaUi.Orange, 0.2f), MetaUi.Orange, MetaUi.OrangeTxt);
            scroll.contentContainer.style.paddingRight = P(10f);
            scroll.contentContainer.style.paddingBottom = P(6f);
            pane.Add(scroll);

            _inviteOthersRow = MakeRow("INVITE OTHERS…", InviteOthers, MetaUi.OrangeTxt);
            Pad(_inviteOthersRow, InviteOthers);
            scroll.Add(_inviteOthersRow);

            _friendsHost = new VisualElement();
            scroll.Add(_friendsHost);
            return pane;
        }

        private static VisualElement BuildLaunchTab()
        {
            var tab = Section();
            tab.Add(MetaUi.Text("LAUNCH", MetaUi.Face.DisplayBold, 20f, MetaUi.OrangeTxt, 1.5f));
            var sub = MetaUi.Text("SEND THE CREW IN", MetaUi.Face.Body, 12f, MenuStyles.Dim2, 1f);
            sub.style.marginBottom = P(18f);
            tab.Add(sub);

            _launchMap = MetaUi.Text("", MetaUi.Face.DisplayBold, 34f, MenuStyles.Light, 0.5f);
            _launchMap.style.whiteSpace = WhiteSpace.Normal;
            tab.Add(_launchMap);

            _launchInfo = MetaUi.Text("", MetaUi.Face.Body, 15f, MenuStyles.Dim, 0.3f, wrap: true);
            _launchInfo.style.whiteSpace = WhiteSpace.Normal;
            _launchInfo.style.marginTop = P(8f);
            tab.Add(_launchInfo);

            tab.Add(new VisualElement { style = { flexGrow = 1f } });

            _startBtn = MakeRow("START SESSION", StartSession, MetaUi.Green);
            _startLabel = _startBtn.Q<Label>();
            _startLabel.style.fontSize = 24f;
            _startBtn.style.minHeight = P(88f);
            _startBtn.style.marginTop = P(12f);
            Pad(_startBtn, StartSession);
            tab.Add(_startBtn);
            return tab;
        }

        private static VisualElement BuildContractTab()
        {
            var tab = Section();
            tab.Add(MetaUi.Text("CONTRACT", MetaUi.Face.DisplayBold, 20f, MetaUi.OrangeTxt, 1.5f));
            var sub = MetaUi.Text("PICK THE MAP THIS SESSION WILL RUN · A SELECTS", MetaUi.Face.Body, 12f, MenuStyles.Dim2, 1f);
            sub.style.marginBottom = P(12f);
            tab.Add(sub);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            scroll.style.minHeight = 0f;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            MetaUi.AccentScroll(scroll, Fade(MetaUi.Orange, 0.2f), MetaUi.Orange, MetaUi.OrangeTxt);
            scroll.contentContainer.style.paddingRight = P(14f);
            scroll.contentContainer.style.paddingBottom = P(6f);
            tab.Add(scroll);

            // Map cards flow two per line: the old full-width rows wasted space.
            _contractsHost = new VisualElement();
            _contractsHost.style.flexDirection = FlexDirection.Row;
            _contractsHost.style.flexWrap = Wrap.Wrap;
            _contractsHost.style.alignItems = Align.Stretch;
            scroll.Add(_contractsHost);
            return tab;
        }

        private static void PaintPanel(VisualElement panel)
        {
            panel.style.paddingLeft = P(30f);
            panel.style.paddingRight = P(30f);
            panel.style.paddingTop = P(22f);
            panel.style.paddingBottom = P(22f);
            var slab = new SlabFrame
            {
                Notch = PF(30f),
                Stroke = PF(3f),
                StrokeColor = MetaUi.Orange,
                FillColor = MetaUi.Hex(0x0A0A0E),
                TintColor = Color.clear
            };
            slab.Behind(panel);
        }

        /// <summary>
        /// A full-width focusable row. Height is a minimum, never fixed, so the
        /// UI-scaling mod can grow the text without clipping it.
        /// </summary>
        private static VisualElement MakeRow(string text, Action action, Color family)
        {
            var row = new VisualElement();
            row.style.minHeight = P(62f);
            row.style.flexShrink = 0f;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = P(16f);
            row.style.paddingRight = P(16f);
            row.style.paddingTop = P(8f);
            row.style.paddingBottom = P(8f);
            row.style.marginBottom = P(8f);
            row.style.backgroundColor = MetaUi.Hex(0x14141C);
            MetaUi.Slab(row, family, false, PF(9f), PF(1.5f));

            if (!string.IsNullOrEmpty(text))
            {
                var label = MetaUi.Text(text, MetaUi.Face.Display, 18f, family, 1f);
                label.style.flexGrow = 1f;
                label.style.minWidth = 0f;
                Elide(label);
                row.Add(label);
            }

            row.AddManipulator(new Clickable(action));
            return row;
        }

        private static VisualElement Section()
        {
            var box = new VisualElement();
            box.style.flexDirection = FlexDirection.Column;
            box.style.flexGrow = 1f;
            box.style.minHeight = 0f;
            box.style.paddingLeft = P(20f);
            box.style.paddingRight = P(18f);
            box.style.paddingTop = P(16f);
            box.style.paddingBottom = P(16f);
            MetaUi.Slab(box, MetaUi.Orange, true, PF(16f), PF(1.5f));
            return box;
        }

        private static void BuildFooter()
        {
            var foot = new VisualElement();
            foot.style.flexDirection = FlexDirection.Row;
            foot.style.alignItems = Align.Center;
            foot.style.marginTop = P(14f);
            foot.style.flexShrink = 0f;
            _panel.Add(foot);

            _status = MetaUi.Text("", MetaUi.Face.Body, 15f, MenuStyles.Dim, 1f);
            _status.style.flexGrow = 1f;
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.minWidth = 0f;

            _hints = new VisualElement { pickingMode = PickingMode.Ignore };
            _hints.style.flexDirection = FlexDirection.Row;
            _hints.style.alignItems = Align.Center;
            _hints.style.flexShrink = 0f;
            _hints.Add(Hint("dpad_all", "NAVIGATE"));
            _hints.Add(Hint("button_a", "SELECT"));
            _hints.Add(HintPair("lb", "rb", "SECTION"));
            _hints.Add(Hint("button_b", "CLOSE"));
            PadNav.PadOnly(_hints);

            foot.Add(_status);
            foot.Add(_hints);
        }

        // ------------------------------------------------------------- map pane

        private static void BuildContractDetail(VisualElement pane)
        {
            _detailScroll = new ScrollView(ScrollViewMode.Vertical);
            _detailScroll.style.flexGrow = 1f;
            _detailScroll.style.minHeight = 0f;
            _detailScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _detailScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            MetaUi.AccentScroll(_detailScroll, Fade(MetaUi.Orange, 0.2f), MetaUi.Orange, MetaUi.OrangeTxt);
            _detailScroll.contentContainer.style.paddingRight = P(10f);
            _detailScroll.contentContainer.style.paddingBottom = P(10f);
            pane.Add(_detailScroll);

            var body = _detailScroll.contentContainer;
            body.style.flexDirection = FlexDirection.Column;

            _mapThumbHost = new VisualElement();
            _mapThumbHost.style.flexDirection = FlexDirection.Row;
            _mapThumbHost.style.alignItems = Align.Center;
            _mapThumbHost.style.justifyContent = Justify.Center;
            _mapThumbHost.style.minHeight = P(240f);
            _mapThumbHost.style.marginBottom = P(16f);
            MetaUi.Slab(_mapThumbHost, MetaUi.Orange, true, PF(12f), PF(1.2f));
            body.Add(_mapThumbHost);

            _mapName = MetaUi.Text("", MetaUi.Face.DisplayBold, 30f, MetaUi.OrangeTxt);
            body.Add(_mapName);

            _mapDesc = MetaUi.Text("", MetaUi.Face.Body, 17f, MenuStyles.Light, 0.3f, wrap: true);
            _mapDesc.style.whiteSpace = WhiteSpace.Normal;
            _mapDesc.style.marginTop = P(10f);
            _mapDesc.style.marginBottom = P(16f);
            body.Add(_mapDesc);

            var stats = new VisualElement();
            stats.style.flexDirection = FlexDirection.Row;
            stats.style.flexWrap = Wrap.Wrap;
            stats.style.alignItems = Align.Center;
            body.Add(stats);

            for (int i = 0; i < _statChips.Length; i++)
            {
                _statChips[i] = StatChip(stats, out _statLabels[i]);
            }

            _layersRow = new VisualElement();
            _layersRow.style.flexDirection = FlexDirection.Row;
            _layersRow.style.alignItems = Align.Center;
            _layersRow.style.marginTop = P(14f);
            var caption = MetaUi.Text("LAYERS", MetaUi.Face.Body, 12f, MenuStyles.Dim2, 1.5f);
            caption.style.marginRight = P(12f);
            _layersRow.Add(caption);
            _layersDots = new VisualElement();
            _layersDots.style.flexDirection = FlexDirection.Row;
            _layersDots.style.alignItems = Align.Center;
            _layersRow.Add(_layersDots);
            body.Add(_layersRow);

            _mapStatus = MetaUi.Text("", MetaUi.Face.Body, 12f, MetaUi.OrangeTxt, 1.5f);
            _mapStatus.style.marginTop = P(16f);
            body.Add(_mapStatus);
        }

        /// <summary>The map pane: real cell silhouette, name, wrapped description, stats.</summary>
        private static void UpdateContractDetail(string id)
        {
            if (_mapName == null) return;

            string shown = id ?? CoopSession.SelectedContractId;
            bool changed = _detailContractId != id;
            _detailContractId = id;
            var def = string.IsNullOrEmpty(shown) ? null : StructureCatalog.Resolve(shown);

            _mapThumbHost.Clear();
            if (def == null)
            {
                _mapName.text = "NO CONTRACT SELECTED";
                _mapDesc.text = CoopSession.IsHost
                    ? "Pick a contract from the list. Every player in the session will run it."
                    : "Waiting for the host to pick a contract.";
                _mapDesc.style.color = MenuStyles.Dim;
                for (int i = 0; i < _statChips.Length; i++) _statChips[i].style.display = DisplayStyle.None;
                _layersRow.style.display = DisplayStyle.None;
                _mapStatus.text = "";
                return;
            }

            if (!_thumbs.TryGetValue(shown, out VisualElement thumb))
            {
                thumb = StructureSilhouette.ThumbLazy(def, PF(230f));
                if (thumb != null) _thumbs[shown] = thumb;
            }
            if (thumb != null) _mapThumbHost.Add(thumb);

            _mapName.text = def.DisplayName.ToUpperInvariant();
            _mapDesc.text = def.DisplayDescription;
            _mapDesc.style.color = MenuStyles.Light;

            SetStat(0, $"{def.cellCount} CELLS");
            SetStat(1, def.startingMoney > 0 ? $"${def.startingMoney} START" : null);
            SetStat(2, def.glyphCount > 0 ? $"{def.glyphCount} GLYPHS" : null);

            _layersDots.Clear();
            bool hasBands = def.bands != null && def.bands.Length > 0;
            _layersRow.style.display = hasBands ? DisplayStyle.Flex : DisplayStyle.None;
            if (hasBands)
            {
                foreach (BandDef band in def.bands)
                {
                    var dot = new VisualElement();
                    dot.style.width = P(14f);
                    dot.style.height = P(14f);
                    dot.style.marginRight = P(6f);
                    dot.style.backgroundColor = StructureSilhouette.LayerColour(band.layer);
                    _layersDots.Add(dot);
                }
            }

            if (CoopSession.SelectedContractId == shown)
            {
                _mapStatus.text = "SELECTED FOR THIS SESSION";
                _mapStatus.style.color = MetaUi.Green;
            }
            else if (CoopSession.IsHost)
            {
                _mapStatus.text = "PRESS A TO SELECT";
                _mapStatus.style.color = MetaUi.OrangeTxt;
            }
            else
            {
                _mapStatus.text = "THE HOST PICKS THE CONTRACT";
                _mapStatus.style.color = MenuStyles.Dim2;
            }

            if (changed) _detailScroll.scrollOffset = Vector2.zero;
        }

        private static void SetStat(int index, string text)
        {
            if (index < 0 || index >= _statChips.Length) return;
            bool on = !string.IsNullOrEmpty(text);
            _statChips[index].style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            if (on) _statLabels[index].text = text;
        }

        private static void UpdateLaunch()
        {
            if (_startBtn == null) return;

            string map = SelectedName();
            _startBtn.style.opacity = CanStart(SteamReady()) ? 1f : 0.55f;

            if (_launchMap != null)
            {
                _launchMap.text = string.IsNullOrEmpty(map) ? "NO CONTRACT SELECTED" : map;
                _launchMap.style.color = string.IsNullOrEmpty(map) ? MenuStyles.Dim : MenuStyles.Light;
            }

            if (_launchInfo != null)
            {
                int players = Mathf.Max(1, CoopSession.Players.Count);
                if (CoopSession.HasVersionMismatch)
                {
                    _launchInfo.text = MismatchText();
                    _launchInfo.style.color = MenuStyles.Crimson;
                }
                else if (string.IsNullOrEmpty(map))
                {
                    _launchInfo.text = "Pick a contract first — the CONTRACT section has the map list.";
                    _launchInfo.style.color = MenuStyles.Dim;
                }
                else
                {
                    _launchInfo.text = $"{players}/4 crew ready. Everyone runs this map on the host's save.";
                    _launchInfo.style.color = MenuStyles.Dim;
                }
            }
        }

        // ------------------------------------------------------------- stops

        private static void PollInput()
        {
            if (_modal == null || _panel == null) return;

            // ESC does what B does here: hide the window, keep the session.
            if (In.KeyDown(KeyCode.Escape))
            {
                Close();
                return;
            }

            // LB/RB switch stops, so the D-pad never has to leave one stop.
            if (In.PadLBDown) ShowStop(_stop - 1, focus: true);
            else if (In.PadRBDown) ShowStop(_stop + 1, focus: true);

            // Right stick scrolls the map pane (readable at 150-200% text).
            if (_detailScroll != null && (_stop == StopContract || _stop == StopStart))
            {
                float y = In.PadRightStick.y;
                if (Mathf.Abs(y) > 0.2f)
                {
                    _detailScroll.scrollOffset += new Vector2(0f, (0f - y) * 900f * Time.unscaledDeltaTime);
                }
            }
        }

        private static void ShowStop(int index, bool focus)
        {
            int n = CoopSession.IsHost ? StopNames.Length : StopNames.Length - 1;
            _stop = ((index % n) + n) % n;

            for (int i = 0; i < _tabs.Length; i++)
            {
                if (_tabs[i] != null) _tabs[i].style.display = i == _stop ? DisplayStyle.Flex : DisplayStyle.None;
            }

            bool session = _stop == StopSession;
            if (_detailFriends != null) _detailFriends.style.display = session ? DisplayStyle.Flex : DisplayStyle.None;
            if (_detailContract != null) _detailContract.style.display = session ? DisplayStyle.None : DisplayStyle.Flex;

            PaintChips();

            if (!session) UpdateContractDetail(_detailContractId ?? CoopSession.SelectedContractId);
            UpdateLaunch();

            VisualElement entry = StopEntry();
            SetDefault(entry);
            if (focus) FocusLater(entry);
        }

        private static VisualElement StopEntry()
        {
            if (_stop == StopSession) return _inviteOthersRow ?? FirstFriendRow() ?? _leaveBtn;
            if (_stop == StopStart) return _startBtn ?? FirstContractRow();
            return FirstContractRow() ?? _startBtn;
        }

        private static void PaintChips()
        {
            for (int i = 0; i < _chipLabels.Length; i++) PaintChip(_chipLabels[i], i);

            // A client cannot start, so the START stop is host-only.
            if (_chipLabels[StopStart] != null && _chipLabels[StopStart].parent != null)
            {
                _chipLabels[StopStart].parent.style.display = CoopSession.IsHost ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (_chipLabels[StopSession] != null)
            {
                int players = CoopSession.Mode == SessionMode.Off ? 1 : Mathf.Max(1, CoopSession.Players.Count);
                _chipLabels[StopSession].text = $"SESSION  {players}/4";
            }
            if (_chipLabels[StopContract] != null && _contractRows.Count > 0)
            {
                _chipLabels[StopContract].text = $"CONTRACT  {_contractRows.Count}";
            }
        }

        private static void PaintChip(Label label, int index)
        {
            if (label == null) return;
            bool on = index == _stop;
            label.style.color = on ? MetaUi.Hex(0x101014) : MenuStyles.Dim;

            VisualElement chip = label.parent;
            if (chip == null) return;
            chip.style.backgroundColor = on ? (index == StopStart ? MetaUi.Green : MetaUi.Orange) : MetaUi.Hex(0x12121A);

            SlabFrame slab = chip.Q<SlabFrame>();
            if (slab != null)
            {
                MetaUi.PaintSlab(slab, index == StopStart ? MetaUi.Green : MetaUi.Orange, !on, on ? 1f : 0.6f);
            }
        }

        // ------------------------------------------------------------- refresh

        private static void Refresh()
        {
            if (_modal == null || _panel == null) return;

            bool steam = SteamReady();

            if (_status != null && Time.unscaledTime >= _hintUntil)
            {
                string text; Color color;
                StatusText(steam, out text, out color);
                _status.text = text;
                _status.style.color = color;
            }

            if (_inviteOthersRow != null) SetRowEnabled(_inviteOthersRow, steam && CoopSession.Mode != SessionMode.Off);

            if (_leaveLabel != null) _leaveLabel.text = CoopSession.IsHost ? "END SESSION" : "LEAVE SESSION";

            RebuildCrew();
            RebuildFriends(steam);
            EnsureContracts();
            PaintContracts();
            PaintChips();

            if (_stop == StopStart && !CoopSession.IsHost) ShowStop(StopContract, focus: false);
            else if (_stop != StopSession) UpdateContractDetail(_detailContractId ?? CoopSession.SelectedContractId);

            UpdateLaunch();
            SetDefault(StopEntry());
        }

        private static void StatusText(bool steam, out string text, out Color color)
        {
            int players = CoopSession.Mode == SessionMode.Off ? 1 : Mathf.Max(1, CoopSession.Players.Count);

            if (!steam)
            {
                text = "STEAM UNAVAILABLE — LAUNCH THE GAME THROUGH STEAM TO GO ONLINE.";
                color = MenuStyles.Crimson;
            }
            else if (CoopSession.HasVersionMismatch)
            {
                text = MismatchText();
                color = MenuStyles.Crimson;
            }
            else if (CoopSession.Mode == SessionMode.Off)
            {
                text = "STARTING LOBBY…";
                color = MenuStyles.Dim;
            }
            else if (CoopSession.IsHost)
            {
                text = $"HOSTING · {players}/4 · INVITE FRIENDS TO JOIN";
                color = MetaUi.TealLt;
            }
            else
            {
                text = $"IN LOBBY · {players}/4 · HOST CONTROLS THE CONTRACT";
                color = MenuStyles.Dim;
            }
        }

        private static string MismatchText()
        {
            string detail = CoopSession.IsHost
                ? "a client is on a different build."
                : $"host {CoopSession.HostBuild ?? "?"}, you {CoopPlugin.BuildTag}.";
            return "BUILD MISMATCH — " + detail + " Both players must install the same build.";
        }

        // ------------------------------------------------------------- crew

        private static void RebuildCrew()
        {
            if (_crewHost == null) return;
            _crewHost.Clear();

            var roster = CoopSession.Players;
            int count = CoopSession.Mode == SessionMode.Off ? 0 : roster.Count;

            for (int i = 0; i < 4; i++)
            {
                bool filled = i < count;
                var card = new VisualElement();
                card.style.flexDirection = FlexDirection.Row;
                card.style.alignItems = Align.Center;
                card.style.minHeight = P(84f);
                card.style.marginBottom = P(10f);
                card.style.paddingLeft = P(14f);
                card.style.paddingRight = P(16f);
                card.style.paddingTop = P(8f);
                card.style.paddingBottom = P(8f);
                MetaUi.Slab(card, filled ? MetaUi.Orange : MenuStyles.Dim2, !filled, PF(10f), PF(1.5f));

                var avatar = Avatar(filled ? roster[i].id : 0, filled, PF(56f));
                avatar.style.marginRight = P(16f);
                card.Add(avatar);

                var names = new VisualElement { style = { flexGrow = 1f, minWidth = 0f } };
                string name = filled ? roster[i].name : "OPEN SLOT";
                var nameLabel = MetaUi.Text(name, MetaUi.Face.Display, 22f, filled ? MenuStyles.Light : MenuStyles.Dim2);
                Elide(nameLabel);
                names.Add(nameLabel);

                string role = filled
                    ? (roster[i].id == CoopSession.LocalPeer.Value ? "YOU" : (roster[i].slot == 0 ? "HOST" : "CREW"))
                    : "WAITING FOR A FRIEND";
                var sub = MetaUi.Text(role, MetaUi.Face.Body, 12f,
                    filled && roster[i].slot == 0 ? MetaUi.OrangeTxt : MenuStyles.Dim2, 1.2f);
                names.Add(sub);
                card.Add(names);

                _crewHost.Add(card);
            }
        }

        // ------------------------------------------------------------- friends

        private static void RebuildFriends(bool steam)
        {
            if (_friendsHost == null) return;

            if (!steam)
            {
                if (_friendsSignature == "!steam" && _friendsHost.childCount > 0) return;
                _friendsSignature = "!steam";
                _friendsCache = new List<(CSteamID, string, string, int)>();
                _friendsHost.Clear();
                _friendRows.Clear();
                _friendsHost.Add(EmptyNote("Steam is not running."));
                PaintChips();
                return;
            }

            List<(CSteamID id, string name, string state, int rank)> friends;
            try { friends = OnlineFriends(); }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[lobby] friends list failed: " + ex.Message);
                friends = new List<(CSteamID, string, string, int)>();
            }

            _friendsCache = friends;

            string signature = SteamLobby.HasLobby + "|" +
                string.Join("|", friends.Select(f => f.id.m_SteamID + ":" + f.state + ":" + f.name));
            if (signature == _friendsSignature && _friendsHost.childCount > 0) return;
            _friendsSignature = signature;

            // Rebuilding destroys the focused row; remember it and put the ring back
            // on the same friend afterwards so an "online" blip doesn't move focus.
            ulong keepId = 0;
            if (_padFocus != null && _friendsHost.Contains(_padFocus))
            {
                foreach (KeyValuePair<ulong, VisualElement> kv in _friendRows)
                {
                    if (kv.Value == _padFocus) { keepId = kv.Key; break; }
                }
            }

            _friendsHost.Clear();
            _friendRows.Clear();

            if (friends.Count == 0)
            {
                _friendsHost.Add(EmptyNote("No friends online right now."));
                PaintChips();
                return;
            }

            foreach (var f in friends)
            {
                var row = MakeRow("", () => InviteFriend(f.id), MenuStyles.Dim2);
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;

                var avatar = Avatar(f.id.m_SteamID, true, PF(44f));
                avatar.style.marginRight = P(14f);
                row.Add(avatar);

                var names = new VisualElement { style = { flexGrow = 1f, minWidth = 0f } };
                var nameLabel = MetaUi.Text(f.name, MetaUi.Face.Display, 19f, MenuStyles.Light);
                Elide(nameLabel);
                names.Add(nameLabel);
                names.Add(MetaUi.Text(f.state, MetaUi.Face.Body, 12f, f.state == "IN GAME" ? MetaUi.TealLt : MenuStyles.Dim2, 1f));
                row.Add(names);

                var action = MetaUi.Text("INVITE", MetaUi.Face.Display, 14f, MetaUi.OrangeTxt, 1.5f);
                action.style.flexShrink = 0f;
                row.Add(action);

                ulong id = f.id.m_SteamID;
                Pad(row, () => InviteFriend(f.id));

                _friendRows[id] = row;
                _friendsHost.Add(row);
            }

            PaintChips();

            if (keepId != 0 && _friendRows.TryGetValue(keepId, out VisualElement again))
            {
                FocusLater(again);
            }
        }

        // ------------------------------------------------------------- contracts

        /// <summary>
        /// Builds one card per contract and keeps it. Cards are never torn down on
        /// a refresh: PadNav holds the focused element, and destroying it mid-browse
        /// (when the host's selection syncs, or a friend comes online) would throw
        /// the focus ring somewhere else.
        /// </summary>
        private static void EnsureContracts()
        {
            if (_contractsHost == null) return;

            var catalog = StructureCatalog.I;
            var defs = catalog != null ? catalog.Contracts : null;
            var usable = new List<StructureDef>();
            if (defs != null)
            {
                foreach (StructureDef d in defs) if (d != null) usable.Add(d);
            }

            string sig = string.Join("|", usable.Select(d => d.id));
            if (sig == _contractsSignature && _contractRows.Count > 0) return;

            string keepId = null;
            if (_padFocus != null && _contractsHost.Contains(_padFocus))
            {
                foreach (KeyValuePair<string, ContractCard> kv in _contractRows)
                {
                    if (kv.Value.row == _padFocus) { keepId = kv.Key; break; }
                }
            }
            _contractsSignature = sig;
            _contractRows.Clear();
            _contractsHost.Clear();

            if (usable.Count == 0)
            {
                _contractsHost.Add(EmptyNote("No contracts available."));
                return;
            }

            foreach (StructureDef def in usable)
            {
                string id = def.id;

                var card = new VisualElement();
                card.style.flexDirection = FlexDirection.Row;
                card.style.alignItems = Align.Center;
                card.style.width = Length.Percent(48f);
                card.style.marginRight = Length.Percent(1.5f);
                card.style.marginBottom = P(10f);
                card.style.minHeight = P(104f);
                card.style.paddingLeft = P(12f);
                card.style.paddingRight = P(12f);
                card.style.paddingTop = P(8f);
                card.style.paddingBottom = P(8f);
                card.style.backgroundColor = MetaUi.Hex(0x14141C);
                MetaUi.Slab(card, MetaUi.Orange, false, PF(9f), PF(1.5f));
                card.AddManipulator(new Clickable(() => PickContract(id)));

                VisualElement thumb = StructureSilhouette.ThumbLazy(def, PF(62f));
                if (thumb != null)
                {
                    thumb.style.marginRight = P(12f);
                    card.Add(thumb);
                }

                var names = new VisualElement { style = { flexGrow = 1f, minWidth = 0f } };
                var name = MetaUi.Text(def.DisplayName.ToUpperInvariant(), MetaUi.Face.Display, 17f, MenuStyles.Light);
                Elide(name);
                names.Add(name);
                string countText = $"{def.cellCount} CELLS";
                var cells = MetaUi.Text(countText, MetaUi.Face.Body, 12f, MenuStyles.Dim2, 1f);
                names.Add(cells);
                card.Add(names);

                _contractRows[id] = new ContractCard
                {
                    row = card,
                    name = name,
                    cells = cells,
                    slab = card.Q<SlabFrame>(),
                    countText = countText
                };

                _contractsHost.Add(card);
                Pad(card, () => PickContract(id), () => UpdateContractDetail(id));
            }

            if (keepId != null && _contractRows.TryGetValue(keepId, out ContractCard again))
            {
                FocusLater(again.row);
            }
        }

        private static void PaintContracts()
        {
            string committed = CoopSession.SelectedContractId;

            foreach (KeyValuePair<string, ContractCard> kv in _contractRows)
            {
                ContractCard r = kv.Value;
                if (r.row == null) continue;

                bool isCommitted = kv.Key == committed;
                Color family = isCommitted ? MetaUi.Green : MetaUi.Orange;

                if (r.slab != null)
                {
                    MetaUi.PaintSlab(r.slab, family, false, isCommitted ? 1f : 0.8f);
                    r.slab.Stroke = isCommitted ? PF(2f) : PF(1.4f);
                }

                // Colour only: never reassign font sizes after creation, or the
                // UI-scaling mod rebases its baseline and text visibly jumps.
                r.name.style.color = isCommitted ? MetaUi.Green
                    : CoopSession.IsHost ? MenuStyles.Light : MenuStyles.Dim;

                r.cells.text = isCommitted ? "SELECTED" : r.countText;
                r.cells.style.color = isCommitted ? MetaUi.Green : MenuStyles.Dim2;
            }
        }

        private static void PickContract(string id)
        {
            if (!CoopSession.IsHost)
            {
                ShowStatus("HOST CONTROLS THE CONTRACT.", MenuStyles.Dim, 2.5f);
                return;
            }
            if (!SteamReady())
            {
                ShowStatus("STEAM UNAVAILABLE — CANNOT SELECT A CONTRACT.", MenuStyles.Crimson, 3f);
                return;
            }

            CoopSession.SetContract(id, broadcast: true);
            _detailContractId = id;
            UpdateContractDetail(id);
        }

        // ------------------------------------------------------------- actions

        private static void InviteOthers()
        {
            try
            {
                if (!SteamLobby.HasLobby)
                {
                    ShowStatus("STEAM LOBBY IS NOT READY YET — GIVE IT A MOMENT.", MenuStyles.Crimson, 3f);
                    return;
                }
                SteamLobby.OpenInviteDialog();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[lobby] invite overlay failed: " + ex.Message);
                ShowStatus("COULD NOT OPEN THE INVITE OVERLAY.", MenuStyles.Crimson, 3f);
            }
        }

        private static void InviteFriend(CSteamID friend)
        {
            try
            {
                if (!SteamLobby.HasLobby)
                {
                    ShowStatus("STEAM LOBBY IS NOT READY YET — GIVE IT A MOMENT.", MenuStyles.Crimson, 3f);
                    return;
                }
                string connect = SteamLobby.ConnectString();
                bool ok = SteamFriends.InviteUserToGame(friend, connect);
                CoopPlugin.Log.LogInfo(
                    $"[lobby] invited {friend.m_SteamID} ok={ok} connect='{connect}'");
                ShowStatus(ok ? "INVITE SENT." : "INVITE FAILED — TRY INVITE OTHERS.", ok ? MetaUi.TealLt : MenuStyles.Crimson, 3f);
                Refresh();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[lobby] direct invite failed: " + ex.Message);
                ShowStatus("INVITE FAILED — TRY INVITE OTHERS.", MenuStyles.Crimson, 3f);
            }
        }

        private static void StartSession()
        {
            if (CoopSession.HasVersionMismatch)
            {
                ShowStatus(MismatchText(), MenuStyles.Crimson, 4f);
                return;
            }
            if (string.IsNullOrEmpty(CoopSession.SelectedContractId))
            {
                ShowStatus("SELECT A CONTRACT BEFORE STARTING.", MenuStyles.Crimson, 3.5f);
                return;
            }

            if (GameFlow.TryStartHostGame(out string error))
            {
                Close();
            }
            else
            {
                ShowStatus("CANNOT START · " + error.ToUpperInvariant(), MenuStyles.Crimson, 4f);
            }
        }

        private static void Leave()
        {
            CoopSession.Leave();
            Close();
        }

        private static bool CanStart(bool steam)
        {
            return steam && CoopSession.IsHost &&
                   !string.IsNullOrEmpty(CoopSession.SelectedContractId) &&
                   !CoopSession.HasVersionMismatch;
        }

        private static string SelectedName()
        {
            var def = string.IsNullOrEmpty(CoopSession.SelectedContractId)
                ? null
                : StructureCatalog.Resolve(CoopSession.SelectedContractId);
            return def?.DisplayName?.ToUpperInvariant();
        }

        // ------------------------------------------------------------- detail kit

        private static VisualElement StatChip(VisualElement host, out Label label)
        {
            var chip = new VisualElement();
            chip.style.paddingLeft = P(10f);
            chip.style.paddingRight = P(10f);
            chip.style.paddingTop = P(5f);
            chip.style.paddingBottom = P(5f);
            chip.style.marginRight = P(8f);
            chip.style.marginBottom = P(8f);
            MetaUi.Slab(chip, MetaUi.Orange, true, PF(5f), PF(1f));
            label = MetaUi.Text("", MetaUi.Face.Body, 12f, MenuStyles.Dim, 1.3f);
            chip.Add(label);
            host.Add(chip);
            return chip;
        }

        private static Label EmptyNote(string text)
        {
            var label = MetaUi.Text(text, MetaUi.Face.Body, 15f, MenuStyles.Dim2, 0.3f, wrap: true);
            label.style.marginTop = P(6f);
            return label;
        }

        /// <summary>Truncate instead of overrunning a container at large text sizes.</summary>
        private static void Elide(Label label)
        {
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            label.style.minWidth = 0f;
            label.style.flexShrink = 1f;
        }

        // ------------------------------------------------------------- steam helpers

        private static bool SteamReady()
        {
            try
            {
                GraveyardOrbit.SteamBridge.EnsureInit();
                return GraveyardOrbit.SteamBridge.Initialized;
            }
            catch { return false; }
        }

        private static List<(CSteamID id, string name, string state, int rank)> OnlineFriends()
        {
            var list = new List<(CSteamID, string, string, int)>();
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                CSteamID id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                EPersonaState state = SteamFriends.GetFriendPersonaState(id);
                if (state == EPersonaState.k_EPersonaStateOffline) continue;

                string name = SteamFriends.GetFriendPersonaName(id);
                if (string.IsNullOrEmpty(name)) name = id.ToString();

                bool inGame = SteamFriends.GetFriendGamePlayed(id, out FriendGameInfo_t info) && info.m_gameID.IsValid();
                string stateText = inGame ? "IN GAME" : StateName(state);
                int rank = inGame ? 0 : (state == EPersonaState.k_EPersonaStateOnline ? 1 : 2);
                list.Add((id, name, stateText, rank));
            }
            return list.OrderBy(f => f.Item4).ThenBy(f => f.Item2).ToList();
        }

        private static string StateName(EPersonaState s)
        {
            switch (s)
            {
                case EPersonaState.k_EPersonaStateOnline: return "ONLINE";
                case EPersonaState.k_EPersonaStateAway: return "AWAY";
                case EPersonaState.k_EPersonaStateBusy: return "BUSY";
                case EPersonaState.k_EPersonaStateSnooze: return "SNOOZE";
                default: return "ONLINE";
            }
        }

        private static VisualElement Avatar(ulong steamId, bool filled, float size)
        {
            var el = new VisualElement();
            el.style.width = size;
            el.style.height = size;
            el.style.flexShrink = 0f;
            el.style.backgroundColor = MetaUi.Hex(0x1A1A22);
            el.style.borderLeftWidth = 1; el.style.borderRightWidth = 1;
            el.style.borderTopWidth = 1; el.style.borderBottomWidth = 1;
            var border = filled ? Fade(MetaUi.Orange, 0.7f) : Fade(MenuStyles.Dim2, 0.5f);
            el.style.borderLeftColor = border; el.style.borderRightColor = border;
            el.style.borderTopColor = border; el.style.borderBottomColor = border;

            if (filled && steamId != 0)
            {
                Texture2D tex = GetAvatar(steamId);
                if (tex != null)
                {
                    el.style.backgroundImage = new StyleBackground(tex);
                    el.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                }
            }
            return el;
        }

        private static Texture2D GetAvatar(ulong id)
        {
            if (_avatars.TryGetValue(id, out Texture2D cached)) return cached;

            Texture2D tex = null;
            try
            {
                int handle = SteamFriends.GetMediumFriendAvatar(new CSteamID(id));
                if (handle > 0 &&
                    SteamUtils.GetImageSize(handle, out uint w, out uint h) && w > 0 && h > 0)
                {
                    byte[] rgba = new byte[w * h * 4];
                    if (SteamUtils.GetImageRGBA(handle, rgba, rgba.Length))
                    {
                        int stride = (int)w * 4;
                        byte[] flipped = new byte[rgba.Length];
                        for (int y = 0; y < h; y++)
                        {
                            Buffer.BlockCopy(rgba, y * stride, flipped, (int)(h - 1 - y) * stride, stride);
                        }
                        tex = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, false);
                        tex.LoadRawTextureData(flipped);
                        tex.Apply();
                    }
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogDebug("[lobby] avatar failed: " + ex.Message);
            }

            _avatars[id] = tex;
            return tex;
        }

        // ------------------------------------------------------------- pad helpers

        /// <summary>
        /// Register a control with the game's pad layer and give it the animated
        /// focus ring. PadNav only discovers Buttons by itself, and these rows are
        /// painted VisualElements so their theme can't turn green.
        /// </summary>
        private static void Pad(VisualElement el, Action submit, Action enter = null)
        {
            if (el == null) return;
            el.AddToClassList("pad-ring");
            PadNav.Register(el, submit, onEnter: () =>
            {
                _padFocus = el;
                enter?.Invoke();
            });
        }

        private static void SetDefault(VisualElement el)
        {
            if (_padDefault == el) return;
            _padDefault?.RemoveFromClassList("pad-default");
            _padDefault = el;
            _padDefault?.AddToClassList("pad-default");
        }

        /// <summary>Focus a freshly built element once it has a layout (and only if a pad is in use).</summary>
        private static void FocusLater(VisualElement el)
        {
            if (el == null || el.panel == null) return;
            el.schedule.Execute(() =>
            {
                if (!PadNav.Active && !In.PadLatched) return;
                if (el.panel == null || el.worldBound.width < 2f) return;
                PadNav.FocusOn(el);
            }).ExecuteLater(16);
        }

        private static VisualElement FirstContractRow()
        {
            string id = CoopSession.SelectedContractId;
            if (id != null && _contractRows.TryGetValue(id, out ContractCard sel) && sel.row != null) return sel.row;
            foreach (ContractCard r in _contractRows.Values) if (r.row != null) return r.row;
            return null;
        }

        private static VisualElement FirstFriendRow()
        {
            foreach (VisualElement row in _friendRows.Values) return row;
            return null;
        }

        /// <summary>Short-lived message that outranks the 2 s status pump.</summary>
        private static void ShowStatus(string text, Color color, float seconds = 3f)
        {
            if (_status == null) return;
            _status.text = text;
            _status.style.color = color;
            _hintUntil = Time.unscaledTime + seconds;
        }

        private static void SetRowEnabled(VisualElement row, bool on)
        {
            if (row == null) return;
            row.SetEnabled(on);
            row.style.opacity = on ? 1f : 0.45f;
        }

        private static VisualElement Hint(string glyph, string word)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginRight = P(18f);
            AddGlyph(row, glyph);
            AddHintWord(row, word);
            return row;
        }

        private static VisualElement HintPair(string glyphA, string glyphB, string word)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginRight = P(18f);
            AddGlyph(row, glyphA);
            AddGlyph(row, glyphB);
            AddHintWord(row, word);
            return row;
        }

        private static void AddGlyph(VisualElement row, string glyph)
        {
            Texture2D tex = PadNav.Glyph(glyph);
            if (tex == null) return;
            var icon = new Image { image = tex, pickingMode = PickingMode.Ignore };
            icon.style.width = P(22f);
            icon.style.height = P(22f);
            icon.style.flexShrink = 0f;
            icon.style.marginLeft = P(2f);
            row.Add(icon);
        }

        private static void AddHintWord(VisualElement row, string word)
        {
            if (string.IsNullOrEmpty(word)) return;
            var label = MetaUi.Text(word, MetaUi.Face.Body, 12f, MenuStyles.Dim, 1.2f);
            label.style.marginLeft = P(6f);
            row.Add(label);
        }

        // ------------------------------------------------------------- utils

        private static float _hintUntil;
        private static VisualElement _padDefault;
        private static VisualElement _padFocus;   // element PadNav last handed the ring to

        private static Length P(float v) => v * _scale;

        private static float PF(float v) => v * _scale;

        private static Color Fade(Color c, float a) => new Color(c.r, c.g, c.b, a);
    }
}
