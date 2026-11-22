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
    /// Multiplayer lobby window, built in the game's own menu style
    /// (MenuStyles palette, MetaUi fonts, SlabFrame borders).
    /// </summary>
    public static class MultiplayerLobby
    {
        private static VisualElement _modal;
        private static VisualElement _panel;
        private static Label _status;
        private static VisualElement _crewHost;
        private static VisualElement _friendsHost;
        private static VisualElement _contractsHost;
        private static Label _contractName;
        private static Label _contractDesc;
        private static VisualElement _leaveBtn;
        private static Label _leaveLabel;
        private static VisualElement _startBtn;
        private static VisualElement _inviteBtn;
        private static IVisualElementScheduledItem _pump;
        private static float _scale = 1f;
        private static bool _subscribed;

        private static readonly Dictionary<ulong, Texture2D> _avatars = new Dictionary<ulong, Texture2D>();
        private static string _friendsSignature;

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
            _modal.style.backgroundColor = (Color)new Color32(3, 3, 6, 170);
            ConsoleBlur.Behind(_modal);

            _panel = new VisualElement();
            _panel.style.width = P(1180f);
            _panel.style.height = P(880f);
            _panel.style.flexDirection = FlexDirection.Column;
            PaintPanel(_panel);
            _modal.Add(_panel);
            host.Add(_modal);

            BuildHeader();
            BuildBody();
            BuildContractSection();
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

            _pump = _modal.schedule.Execute(RefreshFriendsOnly).Every(2000);
            Refresh();
        }

        /// <summary>Light periodic pump: friends/status only, so lists don't jump or flicker.</summary>
        private static void RefreshFriendsOnly()
        {
            if (_modal == null || _panel == null) return;
            bool steam = SteamReady();

            if (_status != null)
            {
                if (!steam)
                {
                    _status.text = "STEAM UNAVAILABLE — LAUNCH THE GAME THROUGH STEAM TO GO ONLINE.";
                    _status.style.color = MenuStyles.Crimson;
                }
                else if (CoopSession.HasVersionMismatch)
                {
                    _status.text = MismatchText();
                    _status.style.color = MenuStyles.Crimson;
                }
                else if (CoopSession.Mode == SessionMode.Off)
                {
                    _status.text = "STARTING LOBBY…";
                    _status.style.color = MenuStyles.Dim;
                }
                else if (CoopSession.IsHost)
                {
                    int players = Mathf.Max(1, CoopSession.Players.Count);
                    _status.text = $"HOSTING · {players}/4 · INVITE FRIENDS TO JOIN";
                    _status.style.color = MetaUi.TealLt;
                }
                else
                {
                    int players = Mathf.Max(1, CoopSession.Players.Count);
                    _status.text = $"IN LOBBY · {players}/4 · HOST CONTROLS THE CONTRACT";
                    _status.style.color = MenuStyles.Dim;
                }
            }

            if (_inviteBtn != null)
            {
                bool on = steam && CoopSession.Mode != SessionMode.Off;
                _inviteBtn.SetEnabled(on);
                _inviteBtn.style.opacity = on ? 1f : 0.45f;
            }

            RebuildFriends(steam);
        }

        public static void Close()
        {
            try { _pump?.Pause(); } catch { }
            _pump = null;

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
            _crewHost = _friendsHost = _contractsHost = null;
            _startBtn = _inviteBtn = _leaveBtn = null;
            _leaveLabel = null;
            _friendsSignature = null;
        }

        private static void OnContractChanged(string id) => Refresh();

        // ------------------------------------------------------------- chrome

        private static void BuildHeader()
        {
            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;
            head.style.marginBottom = P(10f);

            var title = MetaUi.Text("MULTIPLAYER", MetaUi.Face.Display, 40f, MetaUi.OrangeTxt, 1f);
            head.Add(title);

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
            rule.style.marginBottom = P(18f);
            _panel.Add(rule);
        }

        private static void BuildBody()
        {
            var body = new VisualElement();
            body.style.flexDirection = FlexDirection.Row;
            body.style.height = P(392f);
            body.style.flexShrink = 0f;
            body.style.minHeight = 0f;
            _panel.Add(body);

            // ---- crew ----
            var crew = Section("CREW", "WHO IS IN THIS SESSION");
            crew.style.width = Length.Percent(42f);
            crew.style.marginRight = P(18f);
            body.Add(crew);

            _crewHost = new VisualElement();
            _crewHost.style.marginTop = P(10f);
            crew.Add(_crewHost);

            // ---- friends ----
            var friends = Section("FRIENDS", "ONLINE NOW");
            friends.style.flexGrow = 1f;
            body.Add(friends);

            _inviteBtn = MakeButton("INVITE OTHERS", InviteOthers, MetaUi.OrangeTxt, 250f, 46f, 18f);
            _inviteBtn.style.alignSelf = Align.FlexEnd;
            _inviteBtn.style.marginTop = P(-4f);
            friends.Add(_inviteBtn);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            scroll.style.marginTop = P(12f);
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            MetaUi.AccentScroll(scroll, Fade(MetaUi.Orange, 0.2f), MetaUi.Orange, MetaUi.OrangeTxt);
            scroll.contentContainer.style.paddingRight = P(16f);
            scroll.contentContainer.style.paddingBottom = P(6f);
            friends.Add(scroll);

            _friendsHost = scroll.contentContainer;
        }

        private static void BuildContractSection()
        {
            var section = Section("CONTRACT", "THE MAP THIS SESSION WILL RUN");
            section.style.marginTop = P(14f);
            section.style.flexGrow = 1f;
            section.style.minHeight = P(200f);
            _panel.Add(section);

            var summary = new VisualElement();
            summary.style.flexDirection = FlexDirection.Row;
            summary.style.alignItems = Align.Center;
            summary.style.marginTop = P(8f);
            summary.style.marginBottom = P(8f);
            section.Add(summary);

            var names = new VisualElement { style = { flexGrow = 1f } };
            _contractName = MetaUi.Text("NO CONTRACT SELECTED", MetaUi.Face.DisplayBold, 24f, MetaUi.OrangeTxt);
            _contractDesc = MetaUi.Text("The host picks the contract for every player.", MetaUi.Face.Body, 14f, MenuStyles.Dim);
            names.Add(_contractName);
            names.Add(_contractDesc);
            summary.Add(names);

            var list = new ScrollView(ScrollViewMode.Vertical);
            list.style.flexGrow = 1f;
            list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            MetaUi.AccentScroll(list, Fade(MetaUi.Orange, 0.2f), MetaUi.Orange, MetaUi.OrangeTxt);
            list.contentContainer.style.paddingRight = P(16f);
            list.contentContainer.style.paddingBottom = P(8f);
            section.Add(list);
            _contractsHost = list.contentContainer;
        }

        private static void BuildFooter()
        {
            var foot = new VisualElement();
            foot.style.flexDirection = FlexDirection.Row;
            foot.style.alignItems = Align.Center;
            foot.style.marginTop = P(18f);
            foot.style.paddingTop = P(4f);
            foot.style.paddingBottom = P(2f);
            _panel.Add(foot);

            _status = MetaUi.Text("", MetaUi.Face.Body, 15f, MenuStyles.Dim, 1f);
            _status.style.flexGrow = 1f;
            _status.style.whiteSpace = WhiteSpace.Normal;
            foot.Add(_status);

            _leaveBtn = MakeButton("LEAVE", Leave, MenuStyles.Crimson, 160f, 44f, 17f);
            _leaveBtn.style.marginRight = P(12f);
            _leaveLabel = _leaveBtn.Q<Label>();
            foot.Add(_leaveBtn);

            _startBtn = MakeButton("START", StartSession, MetaUi.Green, 160f, 44f, 18f);
            foot.Add(_startBtn);
        }

        // ------------------------------------------------------------- sections

        private static VisualElement Section(string title, string subtitle)
        {
            var box = new VisualElement();
            box.style.flexDirection = FlexDirection.Column;
            box.style.minHeight = 0f;
            box.style.paddingLeft = P(16f);
            box.style.paddingRight = P(20f);
            box.style.paddingTop = P(12f);
            box.style.paddingBottom = P(12f);
            MetaUi.Slab(box, MetaUi.Orange, false, PF(14f), PF(2f));

            box.Add(MetaUi.Text(title, MetaUi.Face.DisplayBold, 20f, MetaUi.OrangeTxt, 1.5f));
            var sub = MetaUi.Text(subtitle, MetaUi.Face.Body, 12f, MenuStyles.Dim2, 1f);
            sub.style.marginTop = P(2f);
            box.Add(sub);
            return box;
        }

        private static void PaintPanel(VisualElement panel)
        {
            panel.style.paddingLeft = P(26f);
            panel.style.paddingRight = P(26f);
            panel.style.paddingTop = P(20f);
            panel.style.paddingBottom = P(20f);
            var slab = new SlabFrame
            {
                Notch = PF(26f),
                Stroke = PF(3f),
                StrokeColor = MetaUi.Orange,
                FillColor = MetaUi.Hex(0x0A0A0E),
                TintColor = Color.clear
            };
            slab.Behind(panel);
        }

        private static VisualElement MakeButton(string text, Action onClick, Color family, float w, float h, float size)
        {
            var b = new VisualElement { focusable = false };
            b.style.width = P(w);
            b.style.height = P(h);
            b.style.paddingLeft = 0;
            b.style.paddingRight = 0;
            b.style.paddingTop = 0;
            b.style.paddingBottom = 0;
            b.style.marginLeft = 0;
            b.style.marginRight = 0;
            b.style.backgroundColor = MetaUi.Hex(0x14141C);
            b.style.backgroundImage = new StyleBackground();
            b.style.borderLeftWidth = PF(2f);
            b.style.borderRightWidth = PF(2f);
            b.style.borderTopWidth = PF(2f);
            b.style.borderBottomWidth = PF(2f);
            b.style.borderLeftColor = family;
            b.style.borderRightColor = family;
            b.style.borderTopColor = family;
            b.style.borderBottomColor = family;

            var label = MetaUi.Text(text, MetaUi.Face.Display, size, family, 1f);
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.flexGrow = 1f;
            b.Add(label);

            b.AddManipulator(new Clickable(onClick));
            return b;
        }

        // ------------------------------------------------------------- refresh

        private static void Refresh()
        {
            if (_modal == null || _panel == null) return;

            bool steam = SteamReady();
            bool inLobby = CoopSession.Mode != SessionMode.Off;
            int players = CoopSession.Mode == SessionMode.Off ? 1 : Mathf.Max(1, CoopSession.Players.Count);

            // status line
            if (!steam)
            {
                _status.text = "STEAM UNAVAILABLE — LAUNCH THE GAME THROUGH STEAM TO GO ONLINE.";
                _status.style.color = MenuStyles.Crimson;
            }
            else if (CoopSession.HasVersionMismatch)
            {
                _status.text = MismatchText();
                _status.style.color = MenuStyles.Crimson;
            }
            else if (CoopSession.Mode == SessionMode.Off)
            {
                _status.text = "STARTING LOBBY…";
                _status.style.color = MenuStyles.Dim;
            }
            else if (CoopSession.IsHost)
            {
                _status.text = $"HOSTING · {players}/4 · INVITE FRIENDS TO JOIN";
                _status.style.color = MetaUi.TealLt;
            }
            else
            {
                _status.text = $"IN LOBBY · {players}/4 · HOST CONTROLS THE CONTRACT";
                _status.style.color = MenuStyles.Dim;
            }

            if (_inviteBtn != null)
            {
                bool on = steam && CoopSession.Mode != SessionMode.Off;
                _inviteBtn.SetEnabled(true);
                _inviteBtn.style.opacity = on ? 1f : 0.45f;
            }

            if (_startBtn != null)
            {
                bool canStart = steam && CoopSession.IsHost &&
                                !string.IsNullOrEmpty(CoopSession.SelectedContractId) &&
                                !CoopSession.HasVersionMismatch;
                // Do not use SetEnabled(false): the theme's disabled style fills
                // the button green. Keep it enabled and gate the action instead.
                _startBtn.SetEnabled(true);
                _startBtn.style.opacity = canStart ? 1f : 0.55f;
                _startBtn.style.display = CoopSession.IsHost ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (_leaveLabel != null)
            {
                _leaveLabel.text = CoopSession.IsHost ? "END SESSION" : "LEAVE";
            }

            RebuildCrew();
            RebuildFriends(steam);
            RebuildContracts(steam);

            string selectedId = CoopSession.SelectedContractId;
            var def = string.IsNullOrEmpty(selectedId) ? null : StructureCatalog.Resolve(selectedId);
            if (def != null)
            {
                _contractName.text = def.DisplayName.ToUpperInvariant();
                _contractDesc.text = $"{def.cellCount} cells · {def.DisplayDescription}";
            }
            else
            {
                _contractName.text = "NO CONTRACT SELECTED";
                _contractDesc.text = CoopSession.IsHost
                    ? "Pick a contract below; every player will run it."
                    : "Waiting for the host to pick a contract.";
            }
        }

        private static string MismatchText()
        {
            string detail = CoopSession.IsHost
                ? "a client is on a different build."
                : $"host {CoopSession.HostBuild ?? "?"}, you {CoopPlugin.BuildTag}.";
            return "BUILD MISMATCH — " + detail + " Both players must install the same build.";
        }

        private static void RebuildCrew()
        {
            if (_crewHost == null) return;
            _crewHost.Clear();

            var roster = CoopSession.Players;
            int count = CoopSession.Mode == SessionMode.Off ? 0 : roster.Count;

            for (int i = 0; i < 4; i++)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.height = P(52f);
                row.style.marginBottom = P(8f);
                row.style.paddingLeft = P(10f);
                row.style.paddingRight = P(10f);
                bool filled = i < count;
                MetaUi.Slab(row, filled ? MetaUi.Orange : MenuStyles.Dim2, !filled, PF(8f), PF(1.5f));

                var avatar = Avatar(i < count ? roster[i].id : 0, filled, PF(32f));
                avatar.style.marginRight = P(10f);
                row.Add(avatar);

                string name = filled ? roster[i].name : "OPEN SLOT";
                var label = MetaUi.Text(name, MetaUi.Face.Display, 17f, filled ? MenuStyles.Light : MenuStyles.Dim2);
                label.style.flexGrow = 1f;
                row.Add(label);

                if (filled)
                {
                    string tag = roster[i].id == CoopSession.LocalPeer.Value ? "YOU" : (roster[i].slot == 0 ? "HOST" : "");
                    if (!string.IsNullOrEmpty(tag))
                    {
                        var t = MetaUi.Text(tag, MetaUi.Face.Body, 12f, tag == "HOST" ? MetaUi.OrangeTxt : MenuStyles.Dim, 1.5f);
                        row.Add(t);
                    }
                }

                _crewHost.Add(row);
            }
        }

        private static void RebuildFriends(bool steam)
        {
            if (_friendsHost == null) return;

            if (!steam)
            {
                if (_friendsSignature == "!steam" && _friendsHost.childCount > 0) return;
                _friendsSignature = "!steam";
                _friendsHost.Clear();
                _friendsHost.Add(MetaUi.Text("Steam is not running.", MetaUi.Face.Body, 15f, MenuStyles.Dim2));
                return;
            }

            List<(CSteamID id, string name, string state, int rank)> friends;
            try { friends = OnlineFriends(); }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[lobby] friends list failed: " + ex.Message);
                friends = new List<(CSteamID, string, string, int)>();
            }

            string signature = SteamLobby.HasLobby + "|" +
                string.Join("|", friends.Select(f => f.id.m_SteamID + ":" + f.state + ":" + f.name));
            if (signature == _friendsSignature && _friendsHost.childCount > 0) return;
            _friendsSignature = signature;

            _friendsHost.Clear();

            if (friends.Count == 0)
            {
                _friendsHost.Add(MetaUi.Text("No friends online right now.", MetaUi.Face.Body, 15f, MenuStyles.Dim2));
                return;
            }

            foreach (var f in friends)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.height = P(48f);
                row.style.marginBottom = P(6f);
                row.style.paddingLeft = P(8f);
                row.style.paddingRight = P(8f);
                MetaUi.Slab(row, MenuStyles.Dim2, true, PF(7f), PF(1.2f));

                var avatar = Avatar(f.id.m_SteamID, true, PF(30f));
                avatar.style.marginRight = P(10f);
                row.Add(avatar);

                var names = new VisualElement { style = { flexGrow = 1f } };
                names.Add(MetaUi.Text(f.name, MetaUi.Face.Display, 16f, MenuStyles.Light));
                names.Add(MetaUi.Text(f.state, MetaUi.Face.Body, 11f, f.state == "IN GAME" ? MetaUi.TealLt : MenuStyles.Dim2, 1f));
                row.Add(names);

                var invite = MakeButton("INVITE", () => InviteFriend(f.id), MetaUi.OrangeTxt, 108f, 34f, 14f);
                invite.SetEnabled(true);
                invite.style.opacity = SteamLobby.HasLobby ? 1f : 0.4f;
                row.Add(invite);

                _friendsHost.Add(row);
            }
        }

        private static void RebuildContracts(bool steam)
        {
            if (_contractsHost == null) return;
            _contractsHost.Clear();

            var catalog = StructureCatalog.I;
            var defs = catalog != null ? catalog.Contracts : null;
            if (defs == null || defs.Length == 0)
            {
                _contractsHost.Add(MetaUi.Text("No contracts available.", MetaUi.Face.Body, 15f, MenuStyles.Dim2));
                return;
            }

            foreach (StructureDef def in defs)
            {
                if (def == null) continue;
                bool selected = def.id == CoopSession.SelectedContractId;
                bool canPick = CoopSession.IsHost && steam;

                var row = new Button { text = "" };
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.height = P(42f);
                row.style.marginBottom = P(6f);
                row.style.paddingLeft = P(10f);
                row.style.paddingRight = P(10f);
                row.style.backgroundColor = Color.clear;
                row.style.borderLeftWidth = 0;
                row.style.borderRightWidth = 0;
                row.style.borderTopWidth = 0;
                row.style.borderBottomWidth = 0;
                MetaUi.Slab(row, selected ? MetaUi.Green : MetaUi.Orange, !canPick, PF(7f), selected ? PF(2f) : PF(1.2f));

                var name = MetaUi.Text(def.DisplayName.ToUpperInvariant(), MetaUi.Face.Display, 16f,
                    selected ? MetaUi.Green : MenuStyles.Light);
                name.style.flexGrow = 1f;
                row.Add(name);

                var cells = MetaUi.Text($"{def.cellCount} CELLS", MetaUi.Face.Body, 12f, MenuStyles.Dim2, 1f);
                if (selected)
                {
                    cells.text = "SELECTED";
                    cells.style.color = MetaUi.Green;
                }
                row.Add(cells);

                if (canPick)
                {
                    string id = def.id;
                    row.clicked += () => CoopSession.SetContract(id, broadcast: true);
                }
                else
                {
                    row.SetEnabled(false);
                }

                _contractsHost.Add(row);
            }
        }

        // ------------------------------------------------------------- actions

        private static void InviteOthers()
        {
            try
            {
                if (!SteamLobby.HasLobby) return;
                SteamLobby.OpenInviteDialog();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[lobby] invite overlay failed: " + ex.Message);
            }
        }

        private static void InviteFriend(CSteamID friend)
        {
            try
            {
                if (!SteamLobby.HasLobby) return;
                string connect = SteamLobby.ConnectString();
                bool ok = SteamFriends.InviteUserToGame(friend, connect);
                CoopPlugin.Log.LogInfo(
                    $"[lobby] invited {friend.m_SteamID} ok={ok} connect='{connect}'");
                Refresh();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[lobby] direct invite failed: " + ex.Message);
            }
        }

        private static void StartSession()
        {
            if (CoopSession.HasVersionMismatch)
            {
                if (_status != null)
                {
                    _status.text = MismatchText();
                    _status.style.color = MenuStyles.Crimson;
                }
                return;
            }
            if (string.IsNullOrEmpty(CoopSession.SelectedContractId))
            {
                if (_status != null)
                {
                    _status.text = "SELECT A CONTRACT BEFORE STARTING.";
                    _status.style.color = MenuStyles.Crimson;
                }
                return;
            }

            if (GameFlow.TryStartHostGame(out string error))
            {
                Close();
            }
            else if (_status != null)
            {
                _status.text = "CANNOT START · " + error.ToUpperInvariant();
                _status.style.color = MenuStyles.Crimson;
            }
        }

        private static void Leave()
        {
            CoopSession.Leave();
            Close();
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

        // ------------------------------------------------------------- utils

        private static Length P(float v) => v * _scale;

        private static float PF(float v) => v * _scale;

        private static Color Fade(Color c, float a) => new Color(c.r, c.g, c.b, a);
    }
}
