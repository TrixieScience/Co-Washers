using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using com.gatordragongames.washnwalk.tools;

namespace DragNWashCoop;

/// <summary>
/// The co-op menu. Laid out like R.E.P.O.'s lobby (a big title, one strip per player with a status bar and
/// their avatar, the actions along the bottom) and drawn like the game's own menu stickers. On the main menu
/// it takes the place of the game's buttons while open. Runtime Unity UI on the game's EventSystem, so mouse,
/// keyboard and controllers all work.
/// </summary>
internal sealed class CoopUI : IDisposable
{
    private const float Left = 96f, RowTop = -300f, RowPitch = 86f, RowWidth = 800f, RowHeight = 74f;
    private enum PlayerState { Ready, NotReady, Joining, InLobby, Playing, Hosting }
    private readonly struct Player
    {
        internal readonly ulong Id;
        internal readonly string Name;
        internal readonly bool IsHost, IsSelf;
        internal readonly PlayerState State;
        internal readonly Peer? Kick;   // host only: guests can be kicked

        internal Player(ulong id, string name, bool isHost, bool isSelf, PlayerState state, Peer? kick)
        {
            Id = id; Name = name; IsHost = isHost; IsSelf = isSelf; State = state; Kick = kick;
        }
    }

    private readonly CoopPlugin owner;
    private GameObject? root;
    private CanvasGroup? group;
    private RectTransform? page;                  // rebuilt whenever what it shows changes
    private readonly List<Button> pageButtons = new();
    private readonly List<(RectTransform Fill, float Width)> joiningBars = new();
    private TicketLock.Ticket cursorTicket = null!;
    private bool hasCursorTicket;
    private float nextRefresh, openedAt;
    private string lastUiKey = string.Empty;
    private bool toolPage;
    private bool ready;
    // the game's main menu buttons and logo, hidden while this menu is open over them
    private readonly List<GameObject> hiddenMenu = new();
    private static readonly System.Reflection.FieldInfo? MenuLogo = AccessTools.Field(typeof(MenuMain), "titleAnimation");
    private static readonly System.Reflection.FieldInfo? MenuButtons = AccessTools.Field(typeof(MenuMain), "mainSelectPanel");
    // Steam avatars; one still downloading is looked for again while the menu stays open
    private readonly Dictionary<ulong, Texture2D?> avatars = new();
    private readonly HashSet<ulong> avatarsRequested = new();
    private readonly List<(ulong Id, RectTransform Picture, TextMeshProUGUI Initial)> avatarSlots = new();
    private bool avatarPending;
    private float avatarRetryUntil;
    internal bool Opened => root != null && root.activeSelf;

    internal CoopUI(CoopPlugin owner) => this.owner = owner;

    internal void Toggle() { if (Opened) Close(); else Open(); }

    /// <summary>Guest: tell the host whether this player is ready (the Ready up button).</summary>
    internal void SetReady(bool value)
    {
        ready = value;
        owner.Session.SendToHost(PacketKind.Ping, 0, w => { w.Write((byte)2); w.Write(ready); });
        lastUiKey = string.Empty;
        if (Opened) Refresh();
    }

    internal void Open()
    {
        EnsureUi();
        bool wasOpen = Opened;
        root!.SetActive(true);
        if (!wasOpen)
        {
            openedAt = Time.unscaledTime;
            avatarRetryUntil = Time.unscaledTime + 20f;
        }
        lastUiKey = string.Empty;
        HideMainMenu();
        if (!hasCursorTicket && GameStateManager.Instance != null && SceneManager.GetActiveScene().name != "StartScene")
        {
            cursorTicket = GameStateManager.RequestCursorUnlock(owner, true);
            hasCursorTicket = true;
        }
        Refresh();
    }

    internal void Close()
    {
        if (root != null) root.SetActive(false);
        toolPage = false;
        bool menuBack = hiddenMenu.Count > 0;
        foreach (var part in hiddenMenu) if (part != null) part.SetActive(true);
        hiddenMenu.Clear();
        if (menuBack && Gamepad.current != null && MainMenuCoopButton.Button is { } multiplayer)
            EventSystem.current?.SetSelectedGameObject(multiplayer.gameObject);
        if (hasCursorTicket)
        {
            GameStateManager.ReleaseCursorUnlock(ref cursorTicket);
            hasCursorTicket = false;
        }
    }

    /// <summary>Esc, or the Back button: from the tools page to the lobby, otherwise out of the menu.</summary>
    internal void Back()
    {
        if (!toolPage) { Close(); return; }
        toolPage = false;
        lastUiKey = string.Empty;
        Refresh();
    }

    internal void Tick()
    {
        if (owner.Session.Role != CoopRole.Guest) ready = false;   // a new session starts not ready
        if (!Opened) return;
        float since = Time.unscaledTime - openedAt;
        if (group != null) group.alpha = Mathf.Clamp01(since / .15f);
        if (page != null) page.anchoredPosition = new Vector2(-30f * Mathf.Pow(1f - Mathf.Clamp01(since / .3f), 3f), 0f);
        foreach (var (fill, width) in joiningBars)
            if (fill != null) fill.sizeDelta = new Vector2(width * (.12f + .76f * Mathf.PingPong(Time.unscaledTime * .9f, 1f)), 0f);
        if (Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + .4f;
        HideMainMenu();   // e.g. a main menu that came back while this stayed open
        if (avatarPending && Time.unscaledTime < avatarRetryUntil)
        {
            avatarPending = false;
            foreach (var (id, picture, initial) in avatarSlots)
                if (picture != null && initial != null && initial.gameObject.activeSelf) ShowAvatar(id, picture, initial);
        }
        var session = owner.Session;
        var key = string.Join("|", session.Role, session.Status, session.IsReady, ready, toolPage, owner.World.Started,
                              SceneManager.GetActiveScene().name, LobbyMemberCount(), session.NameOf((ulong)session.HostId), PlayerLooks.Version,
                              string.Join(",", session.Peers.Select(p => p.SteamId + ":" + p.Authenticated + ":" + p.Ready +
                                                                          ":" + session.NameOf((ulong)p.SteamId))));
        if (lastUiKey != key) { lastUiKey = key; Refresh(); }
    }

    private void EnsureUi()
    {
        if (root != null) return;
        root = new GameObject("DragNWashCoopUI");
        UnityEngine.Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32760;
        root.AddComponent<GraphicRaycaster>();
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;   // laid out top to bottom; a wider screen shows more of the scenery
        group = root.AddComponent<CanvasGroup>();
        var shade = Stretch("Shade", root.transform).gameObject.AddComponent<RawImage>();
        shade.texture = LobbyLook.Shade;
        page = Stretch("Page", root.transform);
        root.SetActive(false);
    }

    private void HideMainMenu()
    {
        if (hiddenMenu.Count > 0 && hiddenMenu.All(part => part != null)) return;
        hiddenMenu.Clear();
        if (SceneManager.GetActiveScene().name != "StartScene") return;
        var menu = UnityEngine.Object.FindFirstObjectByType<MenuMain>();
        if (menu == null) return;
        foreach (var field in new[] { MenuLogo, MenuButtons })
            if (field?.GetValue(menu) is GameObject part && part.activeSelf) { part.SetActive(false); hiddenMenu.Add(part); }
    }

    private void Refresh()
    {
        if (page == null) return;
        foreach (Transform child in page) UnityEngine.Object.Destroy(child.gameObject);
        pageButtons.Clear();
        joiningBars.Clear();
        avatarSlots.Clear();
        avatarPending = false;
        if (toolPage) ToolsPage();
        else if (owner.Session.Role == CoopRole.None) StartPage();
        else LobbyPage();
        // a controller needs something focused; the old page's buttons are about to go
        var events = EventSystem.current;
        var selected = events != null ? events.currentSelectedGameObject : null;
        if (events == null || (selected != null && selected.activeInHierarchy && !selected.transform.IsChildOf(page))) return;
        var first = Gamepad.current != null ? pageButtons.FirstOrDefault(b => b.interactable) : null;
        events.SetSelectedGameObject(first != null ? first.gameObject : null);
    }

    // ---- pages ----

    private void StartPage()
    {
        var session = owner.Session;
        bool steam = session.IsReady;
        string? note = !steam ? "Steam isn't running. Start Steam to play online."
            : session.Status is "Steam ready" or "Not connected" or "Steam is starting" ? null : session.Status;
        Header("MULTIPLAYER", "Play the campaign together: one of you hosts, up to 3 friends join.", note,
               steam ? null : LobbyLook.Pink, "title_multiplayer");
        float width = ArtSticker("host", "Host a game", Left, -292f, 124f, LobbyLook.Green, session.Host, steam);
        Caption("Opens a lobby your Steam friends can join, or you can invite them.", Left + width + 36f, -330f);
        width = ArtSticker("join", "Join a game", Left, -432f, 124f, LobbyLook.Blue,
                           () => SteamFriends.ActivateGameOverlay("friends"), steam);
        Caption("Opens Steam: accept an invite, or pick Join Game on a friend who is hosting.", Left + width + 36f, -456f, 600f);
        if (owner.LocalTest)
        {
            var heading = Label(page!, "Local test", 34, LobbyLook.Fade(LobbyLook.Cream, .8f), TextAlignmentOptions.TopLeft,
                                LobbyLook.LabelMaterial);
            Place(heading.rectTransform, Left + 4f, -588f, 400f, 44f);
            Sticker("Host local test", Left, -640f, 330f, 84f, LobbyLook.Green, -1f, session.HostLocal);
            Sticker("Join local test", Left + 370f, -640f, 330f, 84f, LobbyLook.Teal, 1f, session.JoinLocal);
        }
        BottomBar(("Back", Close, null));
    }

    private void LobbyPage()
    {
        var session = owner.Session;
        bool host = session.Role == CoopRole.Host, started = owner.World.Started;
        string scene = SceneManager.GetActiveScene().name;
        var players = Roster();
        string where = session.IsLocal ? $"Local test on port {session.LocalPort}" : "Friends-only Steam lobby";
        Header("LOBBY", $"{where}  ·  {Math.Min(players.Count, SteamSession.Capacity)}/{SteamSession.Capacity} players", session.Status,
               art: "title_lobby", letters: 160f);
        for (int i = 0; i < SteamSession.Capacity; i++)
        {
            float y = RowTop - i * RowPitch;
            if (i < players.Count) PlayerRow(y, i, players[i]);
            else EmptyRow(y, host && !session.IsLocal);
        }
        float section = RowTop - SteamSession.Capacity * RowPitch - 24f;
        if (host && !started && scene == "StartScene") SaveCards(section);
        else if (!host && !started)
        {
            // says what clicking does; the state shows on your strip above
            Sticker(ready ? "Not ready" : "Ready up", Left, section - 30f, 330f, 96f, ready ? LobbyLook.Pink : LobbyLook.Green, 1.5f,
                    () => SetReady(!ready));
            Caption(ready ? "You're ready! Waiting for the host to pick a save."
                          : "Ready up so the host can start. The host picks the save; your own saves stay untouched.",
                    Left + 370f, section - 50f, 560f);
        }
        else if (started && scene == "PlayGame")
        {
            Sticker("Your tools", Left, section - 30f, 330f, 96f, LobbyLook.Teal, -1.5f, () =>
            {
                toolPage = true;
                lastUiKey = string.Empty;
                Refresh();
            });
            Caption(host ? "Friends can still join. Invite them anytime. Press Y to yip!"
                         : "Take a copy of any tool the host has unlocked. Press Y to yip!",
                    Left + 370f, section - 62f, 560f);
        }
        else Caption(started ? "Campaign in progress." : "Go back to the main menu to start the campaign.", Left + 4f, section - 10f);
        var actions = new List<(string, Action, Color?)> { ("Leave", () => { session.Leave(); Close(); }, LobbyLook.Pink) };
        if (host && !session.IsLocal) actions.Add(("Invite friends", session.InviteFriends, null));
        actions.Add((scene == "StartScene" ? "Back" : "Close", Close, null));
        BottomBar(actions.ToArray());
    }

    /// <summary>Host, before the campaign: the three saves as stickers, coloured like the game's Slot 1-3 stickers.</summary>
    private void SaveCards(float top)
    {
        bool waiting = owner.Session.Peers.Any(p => p.Authenticated && !p.Ready);   // what StartCampaign waits for
        var heading = Label(page!, "Pick a save to play", 40, LobbyLook.Yellow, TextAlignmentOptions.TopLeft, LobbyLook.LabelMaterial);
        Place(heading.rectTransform, Left + 4f, top, 600f, 52f);
        Caption(waiting ? "Waiting for everyone to ready up..." : "Guests' own saves stay untouched.",
                Left + 30f + heading.GetPreferredValues().x, top - 8f);
        Color[] colours = { LobbyLook.Blue, LobbyLook.Green, LobbyLook.Orange };
        float[] tilts = { -2f, 1.5f, -1f };
        for (int i = 0; i < 3; i++)
        {
            int slot = i + 1;
            int progress = SaveManagerV1.GetSlotProgress(slot);
            const float height = 112f;
            var (card, label) = CardSticker("slot_card", $"Slot {slot}", Left + i * 305f, top - 66f, height, colours[i], tilts[i],
                                            () => owner.World.StartCampaign(slot), !waiting, 46f);
            label.rectTransform.offsetMin = new Vector2(0f, 56f);
            // the game counts a finished save past 14 (its own menu shows "15 / 14")
            string done = progress >= 14 ? "Complete" : progress > 0 ? $"Progress {progress}/14" : "New game";
            var detail = Label(card, done, 26,
                               LobbyLook.Fade(LobbyLook.Ink, waiting ? .45f : .8f), TextAlignmentOptions.Center);
            Stretch(detail.rectTransform, new Vector2(0f, 34f), new Vector2(0f, -(height - 60f)));
            var bar = Node("Progress", card);   // above the card's darker bottom edge
            Stretch(bar, new Vector2(26f, 17f), new Vector2(-26f, -(height - 27f)));
            Shape(bar, LobbyLook.Fade(LobbyLook.Ink, .22f), 0f, Vector2.zero, 5f);
            if (progress <= 0) continue;
            var fill = Node("Done", bar);
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = new Vector2(Mathf.Clamp01(progress / 14f), 1f);
            fill.offsetMin = fill.offsetMax = Vector2.zero;
            Shape(fill, LobbyLook.Fade(LobbyLook.Ink, .7f), 0f, Vector2.zero, 5f);
        }
    }

    private void ToolsPage()
    {
        Header("YOUR TOOLS", "Take a copy of any tool unlocked in the host's campaign.");
        Sticker("Empty hands", Left, -300f, 360f, 90f, LobbyLook.Pink, -1.5f, () => { owner.World.Racks.ReturnHeldTool(); Close(); });
        Caption("Puts the tool you're holding back.", Left + 400f, -322f);
        var seen = new HashSet<string>();
        int count = 0;
        foreach (var equip in UnityEngine.Object.FindObjectsByType<InteractableToolEquip>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!equip.gameObject.activeInHierarchy) continue;
            var tool = AccessTools.Field(typeof(InteractableToolEquip), "tool").GetValue(equip) as Tool;
            var flag = AccessTools.Field(typeof(InteractableToolEquip), "requiredFlag").GetValue(equip) as string;
            if (tool == null || !seen.Add(tool.name) ||
                (!string.IsNullOrEmpty(flag) && !WalkNWashSceneState.GetFlag(flag))) continue;
            string name = tool.name;
            TextButton(page!, Pretty(name), Left + 44f + count / 8 * 440f, -430f - count % 8 * 62f, 44f,
                       () => { owner.World.EquipPersonalTool(name); Close(); });
            if (++count >= 16) break;
        }
        if (count == 0) Caption("No tools are unlocked here yet.", Left + 4f, -430f);
        BottomBar(("Back", Back, null));
    }

    private static string Pretty(string toolName) =>
        Regex.Replace(toolName.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ").Trim();

    // ---- players ----

    private List<Player> Roster()
    {
        var session = owner.Session;
        bool started = owner.World.Started;
        var players = new List<Player>();
        if (session.Role == CoopRole.Host)
        {
            players.Add(new Player(session.SelfId, session.NameOf(session.SelfId), true, true,
                                   started ? PlayerState.Playing : PlayerState.Hosting, null));
            foreach (var peer in session.Peers)
            {
                ulong id = (ulong)peer.SteamId;
                var state = !peer.Authenticated ? PlayerState.Joining
                          : started ? PlayerState.Playing : peer.Ready ? PlayerState.Ready : PlayerState.NotReady;
                players.Add(new Player(id, peer.Authenticated ? session.NameOf(id) : "Joining...", false, false, state, peer));
            }
            return players;
        }
        ulong hostId = (ulong)session.HostId;
        bool connected = session.Peers.Any(p => p.Authenticated);
        players.Add(new Player(hostId, connected ? session.NameOf(hostId) : "Host", true, false,
                               !connected ? PlayerState.Joining : started ? PlayerState.Playing : PlayerState.Hosting, null));
        players.Add(new Player(session.SelfId, session.NameOf(session.SelfId), false, true,
                               !connected ? PlayerState.Joining : started ? PlayerState.Playing
                               : ready ? PlayerState.Ready : PlayerState.NotReady, null));
        // other guests: only Steam lobbies list them (their ready state is the host's to know)
        if (!session.IsLocal && session.IsReady && session.Lobby != CSteamID.Nil)
            for (int i = 0; i < SteamMatchmaking.GetNumLobbyMembers(session.Lobby); i++)
            {
                ulong member = (ulong)SteamMatchmaking.GetLobbyMemberByIndex(session.Lobby, i);
                if (member != hostId && member != session.SelfId)
                    players.Add(new Player(member, session.NameOf(member), false, false, PlayerState.InLobby, null));
            }
        return players;
    }

    private int LobbyMemberCount()
    {
        var session = owner.Session;
        return !session.IsLocal && session.IsReady && session.Lobby != CSteamID.Nil ? SteamMatchmaking.GetNumLobbyMembers(session.Lobby) : 0;
    }

    /// <summary>A player's strip: status bar (R.E.P.O.'s loading bar), name, [HOST], avatar.</summary>
    private void PlayerRow(float y, int index, Player player)
    {
        var row = Node("Player", page!);
        Place(row, Left, y, RowWidth, RowHeight);
        Shape(row, LobbyLook.RowBack, 0f, Vector2.zero, 14f);
        const float barWidth = 210f;
        var bar = Node("Status", row);
        Place(bar, 14f, -18f, barWidth, 38f);
        Shape(bar, new Color(0f, 0f, 0f, .5f), 0f, Vector2.zero, 10f);
        var (colour, amount, text) = player.State switch
        {
            PlayerState.Ready => (LobbyLook.Green, 1f, "READY"),
            PlayerState.Playing => (LobbyLook.Teal, 1f, "PLAYING"),
            PlayerState.InLobby => (LobbyLook.Blue, 1f, "IN LOBBY"),
            PlayerState.Hosting => (LobbyLook.Yellow, 1f, "HOSTING"),
            PlayerState.Joining => (LobbyLook.Orange, .5f, "JOINING"),
            _ => (LobbyLook.Cream, 0f, "NOT READY"),
        };
        if (amount > 0f)
        {
            var fill = Node("Fill", bar);
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = new Vector2(0f, 1f);
            fill.pivot = new Vector2(0f, .5f);
            fill.anchoredPosition = Vector2.zero;
            fill.sizeDelta = new Vector2(barWidth * amount, 0f);
            Shape(fill, colour, 0f, Vector2.zero, 10f);
            if (player.State == PlayerState.Joining) joiningBars.Add((fill, barWidth));
        }
        bool full = amount >= 1f;
        var status = Label(bar, text, 26, full ? LobbyLook.Ink : LobbyLook.Fade(LobbyLook.Cream, .7f), TextAlignmentOptions.Center,
                           full ? null : LobbyLook.LabelMaterial);
        Stretch(status.rectTransform, Vector2.zero, Vector2.zero);
        if (player.IsSelf && player.State is PlayerState.Ready or PlayerState.NotReady && owner.Session.Role == CoopRole.Guest)
        {
            // your own status: click it to ready up or back out, like the Ready up sticker
            bar.pivot = new Vector2(.5f, .5f);
            bar.anchoredPosition += new Vector2(barWidth / 2f, -19f);
            var back = bar.GetChild(0).GetComponent<Image>();
            back.raycastTarget = true;
            var toggle = bar.gameObject.AddComponent<Button>();
            toggle.targetGraphic = back;
            toggle.transition = Selectable.Transition.None;
            toggle.onClick.AddListener(() => SetReady(!ready));
            bar.gameObject.AddComponent<LobbyHover>().Grow = 1.08f;
            pageButtons.Add(toggle);
        }
        var chevron = Label(row, ">", 34, LobbyLook.Fade(LobbyLook.Cream, .4f), TextAlignmentOptions.Center);
        Place(chevron.rectTransform, 232f, -12f, 30f, 50f);
        string tag = player.IsHost ? " <size=70%><color=#EFA962>[HOST]</color></size>" : "";
        var name = Label(row, "<noparse>" + player.Name.Replace("</noparse>", "") + "</noparse>" + tag, 40,
                         player.IsSelf ? LobbyLook.Yellow : LobbyLook.Cream, TextAlignmentOptions.MidlineRight, LobbyLook.LabelMaterial);
        name.overflowMode = TextOverflowModes.Ellipsis;
        Place(name.rectTransform, 266f, -8f, 430f, 58f);
        Avatar(row, 714f, 60f, player, index);
        if (player.Kick is { } peer)
            TextButton(page!, "Kick", Left + RowWidth + 44f, y - 12f, 32f, () => owner.Session.Kick(peer), LobbyLook.Pink);
    }

    private void EmptyRow(float y, bool invite)
    {
        var row = Node("Open slot", page!);
        Place(row, Left, y, RowWidth, RowHeight);
        var back = Shape(row, LobbyLook.Fade(LobbyLook.RowBack, .35f), 0f, Vector2.zero, 14f);
        var label = Label(row, invite ? "+ Invite a friend" : "Open slot", 34,
                          LobbyLook.Fade(LobbyLook.Cream, invite ? .75f : .3f), TextAlignmentOptions.MidlineLeft,
                          invite ? LobbyLook.LabelMaterial : null);
        Place(label.rectTransform, 28f, -8f, 500f, 58f);
        if (!invite) return;
        back.raycastTarget = true;
        var button = row.gameObject.AddComponent<Button>();
        button.targetGraphic = back;
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(owner.Session.InviteFriends);
        var hover = row.gameObject.AddComponent<LobbyHover>();
        hover.Tint = label;
        hover.Normal = label.color;
        hover.Hot = LobbyLook.Yellow;
        hover.Slide = label.rectTransform;
        hover.SlideBy = 10f;
        pageButtons.Add(button);
    }

    /// <summary>
    /// Steam avatar in a ring of the colour of the player's kobold (the same on every screen); a number or initial
    /// until (or unless) it arrives.
    /// </summary>
    private void Avatar(RectTransform row, float x, float size, Player player, int index)
    {
        var colour = PlayerLooks.Swatch(PlayerLooks.SlotOf(player.Id, (ulong)owner.Session.HostId));
        var ring = Node("Avatar", row);
        Place(ring, x, -(RowHeight - size) / 2f, size, size);
        var ringImage = ring.gameObject.AddComponent<Image>();
        ringImage.sprite = LobbyLook.Circle;
        ringImage.color = colour;
        ringImage.raycastTarget = false;
        var picture = Node("Picture", ring);
        Stretch(picture, new Vector2(4f, 4f), new Vector2(-4f, -4f));
        var pictureImage = picture.gameObject.AddComponent<Image>();
        pictureImage.sprite = LobbyLook.Circle;
        pictureImage.color = Color.Lerp(colour, LobbyLook.Ink, .35f);
        pictureImage.raycastTarget = false;
        picture.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        string initial = SteamSession.IsLocalTestId(player.Id) || player.Name.Length == 0
            ? (index + 1).ToString() : player.Name.Substring(0, 1).ToUpperInvariant();
        var letter = Label(picture, initial, size * .55f, LobbyLook.Cream, TextAlignmentOptions.Center, LobbyLook.LabelMaterial);
        Stretch(letter.rectTransform, Vector2.zero, Vector2.zero);
        avatarSlots.Add((player.Id, picture, letter));
        ShowAvatar(player.Id, picture, letter);
    }

    private void ShowAvatar(ulong id, RectTransform picture, TextMeshProUGUI initial)
    {
        var texture = AvatarOf(id);
        if (texture == null) return;
        var image = Node("Steam avatar", picture).gameObject.AddComponent<RawImage>();
        Stretch(image.rectTransform, Vector2.zero, Vector2.zero);
        image.texture = texture;
        image.raycastTarget = false;
        initial.gameObject.SetActive(false);
    }

    private Texture2D? AvatarOf(ulong id)
    {
        if (id == 0 || SteamSession.IsLocalTestId(id) || !owner.Session.IsReady) return null;
        if (avatars.TryGetValue(id, out var known)) return known;
        try
        {
            var steamId = new CSteamID(id);
            if (avatarsRequested.Add(id)) SteamFriends.RequestUserInformation(steamId, false);
            int handle = SteamFriends.GetMediumFriendAvatar(steamId);
            // -1: still downloading; 0: none, or not known yet for someone who isn't a friend
            if (handle <= 0 || !SteamUtils.GetImageSize(handle, out uint width, out uint height) || width == 0 || height == 0)
            {
                avatarPending = true;
                return null;
            }
            var rgba = new byte[width * height * 4];
            if (!SteamUtils.GetImageRGBA(handle, rgba, rgba.Length)) { avatarPending = true; return null; }
            // Steam's rows run top to bottom, a texture's bottom to top
            int stride = (int)width * 4;
            var flipped = new byte[rgba.Length];
            for (int row = 0; row < height; row++)
                Buffer.BlockCopy(rgba, row * stride, flipped, ((int)height - 1 - row) * stride, stride);
            var texture = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            texture.LoadRawTextureData(flipped);
            texture.Apply();
            avatars[id] = texture;
            return texture;
        }
        catch (Exception e)
        {
            owner.Log.LogWarning($"Steam avatar for {id} unavailable: {e.Message}");
            avatars[id] = null;
            return null;
        }
    }

    // ---- building blocks ----

    private void Header(string title, string subtitle, string? note = null, Color? noteColour = null, string? art = null,
                        float letters = 150f)
    {
        // title art (Assets/<art>.png: 260 px lettering with 40 px of margin for its edge and shadow), shown
        // `letters` units tall, or drawn lettering
        var sprite = art != null ? LobbyLook.Art(art + ".png") : null;
        float top = -198f;
        if (sprite != null)
        {
            float margin = letters * 40f / 260f;
            var image = Node(title, page!).gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.raycastTarget = false;
            float height = letters + 2f * margin;
            Place(image.rectTransform, Left - margin, -26f + margin, height * sprite.rect.width / sprite.rect.height, height);
            top = Mathf.Min(top, -26f - letters - 16f);   // below a tall title's descenders
        }
        else
        {
            var heading = Label(page!, title, 150, LobbyLook.Yellow, TextAlignmentOptions.BottomLeft, LobbyLook.TitleMaterial);
            Place(heading.rectTransform, Left, -18f, 1500f, 178f);
            heading.rectTransform.localEulerAngles = new Vector3(0f, 0f, 1.5f);
        }
        var line = Label(page!, subtitle, 32, LobbyLook.Fade(LobbyLook.Cream, .92f), TextAlignmentOptions.TopLeft, LobbyLook.LabelMaterial);
        Place(line.rectTransform, Left + 8f, top, 1500f, 44f);
        if (string.IsNullOrEmpty(note)) return;
        var extra = Label(page!, note!, 28, noteColour ?? LobbyLook.Fade(LobbyLook.Cream, .6f), TextAlignmentOptions.TopLeft,
                          LobbyLook.LabelMaterial);
        Place(extra.rectTransform, Left + 8f, top - 42f, 1500f, 40f);
    }

    private void Caption(string text, float x, float y, float width = 700f)
    {
        var caption = Label(page!, text, 28, LobbyLook.Fade(LobbyLook.Cream, .78f), TextAlignmentOptions.TopLeft, LobbyLook.LabelMaterial);
        caption.textWrappingMode = TextWrappingModes.Normal;
        Place(caption.rectTransform, x, y, width, 90f);
    }

    /// <summary>A button drawn like the game's stickers: coloured paper, ink lettering, grows with a white edge on hover.</summary>
    private (RectTransform Sticker, TextMeshProUGUI Label) Sticker(string text, float x, float y, float width, float height,
        Color colour, float tilt, Action action, bool enabled = true, float textSize = 0f)
    {
        var sticker = Node(text, page!);
        sticker.pivot = new Vector2(.5f, .5f);
        sticker.sizeDelta = new Vector2(width, height);
        sticker.anchoredPosition = new Vector2(x + width / 2f, y - height / 2f);
        sticker.localEulerAngles = new Vector3(0f, 0f, tilt);
        if (!enabled) colour = Color.Lerp(colour, new Color(.45f, .43f, .42f), .65f);
        var edge = Shape(sticker, LobbyLook.Fade(Color.white, 0f), -9f, Vector2.zero, 20f);
        Shape(sticker, new Color(0f, 0f, 0f, .3f), 0f, new Vector2(5f, -10f), 16f);
        Shape(sticker, colour * new Color(.76f, .76f, .76f, 1f), 0f, new Vector2(0f, -6f), 16f);
        var face = Shape(sticker, colour, 0f, Vector2.zero, 16f);
        face.raycastTarget = true;
        var label = Label(sticker, text, textSize > 0f ? textSize : height * .5f, LobbyLook.Fade(LobbyLook.Ink, enabled ? 1f : .5f),
                          TextAlignmentOptions.Center);
        Stretch(label.rectTransform, Vector2.zero, Vector2.zero);
        var button = sticker.gameObject.AddComponent<Button>();
        button.targetGraphic = face;
        button.transition = Selectable.Transition.None;
        button.interactable = enabled;
        button.onClick.AddListener(() => action());
        var hover = sticker.gameObject.AddComponent<LobbyHover>();
        hover.Show = edge;
        hover.Grow = 1.06f;
        pageButtons.Add(button);
        return (sticker, label);
    }

    /// <summary>
    /// A button from sticker art (Assets/&lt;art&gt;.png, and &lt;art&gt;_hover.png with the game's white edge), which
    /// grows on hover like the game's; a drawn sticker if the art is missing. Returns its width.
    /// </summary>
    private float ArtSticker(string art, string text, float x, float y, float height, Color colour, Action action, bool enabled)
    {
        var normal = LobbyLook.Art(art + ".png");
        var hot = LobbyLook.Art(art + "_hover.png");
        if (normal == null || hot == null)
        {
            Sticker(text, x, y + 8f, height * 3.4f, height - 20f, colour, 0f, action, enabled);
            return height * 3.4f;
        }
        float width = height * normal.rect.width / normal.rect.height;
        var sticker = Node(text, page!);
        sticker.pivot = new Vector2(.5f, .5f);
        sticker.sizeDelta = new Vector2(width, height);
        sticker.anchoredPosition = new Vector2(x + width / 2f, y - height / 2f);
        var face = sticker.gameObject.AddComponent<Image>();
        face.sprite = normal;
        if (!enabled) face.color = new Color(.55f, .55f, .55f, .8f);
        var edge = Node("Hover", sticker).gameObject.AddComponent<Image>();
        Stretch(edge.rectTransform, Vector2.zero, Vector2.zero);
        edge.sprite = hot;
        edge.color = LobbyLook.Fade(Color.white, 0f);
        edge.raycastTarget = false;
        var button = sticker.gameObject.AddComponent<Button>();
        button.targetGraphic = face;
        button.transition = Selectable.Transition.None;
        button.interactable = enabled;
        button.onClick.AddListener(() => action());
        var hover = sticker.gameObject.AddComponent<LobbyHover>();
        hover.Show = edge;
        hover.Grow = 1.06f;
        pageButtons.Add(button);
        return width;
    }

    /// <summary>
    /// A button on blank card art: Assets/&lt;art&gt;.png is white so it takes any colour, &lt;art&gt;_edge.png is its
    /// white hover edge. Coloured and labelled here; grows on hover like the game's stickers. A drawn sticker if
    /// the art is missing.
    /// </summary>
    private (RectTransform Card, TextMeshProUGUI Label) CardSticker(string art, string text, float x, float y, float height,
        Color colour, float tilt, Action action, bool enabled, float textSize)
    {
        var face = LobbyLook.Art(art + ".png");
        var edge = LobbyLook.Art(art + "_edge.png");
        if (face == null || edge == null) return Sticker(text, x, y, height * 2f, height, colour, tilt, action, enabled, textSize);
        float width = height * face.rect.width / face.rect.height;
        float margin = (edge.rect.height - face.rect.height) / 2f * height / face.rect.height;   // the edge image's extra room
        var card = Node(text, page!);
        card.pivot = new Vector2(.5f, .5f);
        card.sizeDelta = new Vector2(width, height);
        card.anchoredPosition = new Vector2(x + width / 2f, y - height / 2f);
        card.localEulerAngles = new Vector3(0f, 0f, tilt);
        if (!enabled) colour = Color.Lerp(colour, new Color(.45f, .43f, .42f), .65f);
        var shine = ArtImage(card, edge, LobbyLook.Fade(Color.white, 0f), -margin, Vector2.zero);
        ArtImage(card, face, new Color(0f, 0f, 0f, .28f), 0f, new Vector2(5f, -8f));
        var body = ArtImage(card, face, colour, 0f, Vector2.zero);
        body.raycastTarget = true;
        var label = Label(card, text, textSize, LobbyLook.Fade(LobbyLook.Ink, enabled ? 1f : .5f), TextAlignmentOptions.Center);
        Stretch(label.rectTransform, Vector2.zero, Vector2.zero);
        var button = card.gameObject.AddComponent<Button>();
        button.targetGraphic = body;
        button.transition = Selectable.Transition.None;
        button.interactable = enabled;
        button.onClick.AddListener(() => action());
        var hover = card.gameObject.AddComponent<LobbyHover>();
        hover.Show = shine;
        hover.Grow = 1.06f;
        pageButtons.Add(button);
        return (card, label);
    }

    private static Image ArtImage(RectTransform parent, Sprite sprite, Color colour, float inset, Vector2 offset)
    {
        var image = Node(sprite.name, parent).gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.color = colour;
        image.raycastTarget = false;
        Stretch(image.rectTransform, new Vector2(inset, inset) + offset, new Vector2(-inset, -inset) + offset);
        return image;
    }

    /// <summary>R.E.P.O.-style text button: lights up, slides right and shows an arrow on hover. Returns its width.</summary>
    private float TextButton(Transform parent, string text, float x, float y, float size, Action action, Color? hot = null)
    {
        var holder = Node(text, parent);
        var label = Label(holder, text, size, LobbyLook.Cream, TextAlignmentOptions.MidlineLeft, LobbyLook.LabelMaterial);
        float width = label.GetPreferredValues(text).x + 10f, height = size * 1.3f;
        Place(holder, x, y, width + 24f, height);
        Place(label.rectTransform, 0f, 0f, width, height);
        label.raycastTarget = true;
        var arrow = Label(holder, ">", size * .8f, LobbyLook.Fade(hot ?? LobbyLook.Yellow, 0f), TextAlignmentOptions.MidlineLeft,
                          LobbyLook.LabelMaterial);
        Place(arrow.rectTransform, -size * .7f, 0f, size, height);
        var button = holder.gameObject.AddComponent<Button>();
        button.targetGraphic = label;
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(() => action());
        var hover = holder.gameObject.AddComponent<LobbyHover>();
        hover.Tint = label;
        hover.Normal = LobbyLook.Cream;
        hover.Hot = hot ?? LobbyLook.Yellow;
        hover.Show = arrow;
        hover.Slide = label.rectTransform;
        hover.SlideBy = 12f;
        pageButtons.Add(button);
        return width + 24f;
    }

    /// <summary>The page's actions in a row along the bottom, like R.E.P.O.'s Leave / Settings / Invite.</summary>
    private void BottomBar(params (string Text, Action Action, Color? Hot)[] items)
    {
        var bar = Node("Actions", page!);
        bar.anchorMin = bar.anchorMax = Vector2.zero;
        bar.pivot = Vector2.zero;
        bar.anchoredPosition = new Vector2(Left + 30f, 44f);
        bar.sizeDelta = new Vector2(1500f, 64f);
        float x = 0f;
        foreach (var (text, action, hot) in items) x += TextButton(bar, text, x, 0f, 48f, action, hot) + 56f;
    }

    private static TextMeshProUGUI Label(Transform parent, string text, float size, Color colour, TextAlignmentOptions align,
                                         Material? material = null)
    {
        var label = Node("Text", parent).gameObject.AddComponent<TextMeshProUGUI>();
        if (LobbyLook.Font != null) label.font = LobbyLook.Font;
        if (material != null) label.fontSharedMaterial = material;
        label.text = text;
        label.fontSize = size;
        label.color = colour;
        label.alignment = align;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        return label;
    }

    /// <summary>A rounded shape filling its parent, grown by -inset and moved by offset.</summary>
    private static Image Shape(RectTransform parent, Color colour, float inset, Vector2 offset, float radius)
    {
        var image = Node("Shape", parent).gameObject.AddComponent<Image>();
        image.sprite = LobbyLook.Rounded;
        image.type = Image.Type.Sliced;
        image.pixelsPerUnitMultiplier = 24f / radius;
        image.color = colour;
        image.raycastTarget = false;
        Stretch(image.rectTransform, new Vector2(inset, inset) + offset, new Vector2(-inset, -inset) + offset);
        return image;
    }

    private static RectTransform Node(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        return rt;
    }

    private static RectTransform Stretch(string name, Transform parent)
    {
        var rt = Node(name, parent);
        Stretch(rt, Vector2.zero, Vector2.zero);
        return rt;
    }

    private static void Stretch(RectTransform rt, Vector2 offsetMin, Vector2 offsetMax)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(.5f, .5f);
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
    }

    /// <summary>Top-left corner at (x, y) below the parent's top-left corner.</summary>
    private static void Place(RectTransform rt, float x, float y, float width, float height)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(width, height);
        rt.anchoredPosition = new Vector2(x, y);
    }

    public void Dispose()
    {
        Close();
        if (root != null) UnityEngine.Object.Destroy(root);
    }
}

/// <summary>While the co-op menu covers the main menu, the main menu's own input goes to it: Esc backs out of it.</summary>
[HarmonyPatch(typeof(MenuMain), nameof(MenuMain.OnEvent))]
internal static class MainMenuUnderCoopMenu
{
    private static bool Prefix(MenuEvent e, ref MenuResponse __result)
    {
        var ui = CoopPlugin.Instance != null ? CoopPlugin.Instance.Ui : null;
        if (ui == null || !ui.Opened || e is not MenuEventUserIntent intent) return true;
        if (intent.name == "Cancel") ui.Back();
        __result = new MenuResponseIgnored();
        return false;
    }
}

/// <summary>
/// A "Multiplayer" sticker on the main menu, after New Game, in the game's own button style: a copy of
/// the New Game button with our art in its normal and hover images. It opens the co-op menu.
/// </summary>
[HarmonyPatch(typeof(MenuMain), "OnShow")]
internal static class MainMenuCoopButton
{
    private const string WrapperName = "DragNWashCoopButton";
    private const float OurOffset = 96f;     // right of the middle, clear of New Game on the left
    // New Game's lettering sits low in its sticker and ours a little high, so at even spacing ours looked
    // tucked under it; this lowers ours in its row to even out the visible gaps above and below
    private const float OurDrop = -20f;
    private static Sprite? normalSprite, hoverSprite;
    // each button's own sideways offset in the game's layout, before we alternate them
    private static readonly Dictionary<RectTransform, float> originalX = new();

    /// <summary>The menu's button column, once shown (for the -coop-menu-shot developer option).</summary>
    internal static Transform? Column;

    /// <summary>The Multiplayer button, while the main menu exists.</summary>
    internal static Button? Button
    {
        get
        {
            var wrapper = Column != null ? Column.Find(WrapperName) : null;
            return wrapper != null ? wrapper.GetComponentInChildren<Button>(true) : null;
        }
    }

    private static void Postfix(MenuMain __instance)
    {
        if (CoopPlugin.Instance == null) return;
        var load = AccessTools.Field(typeof(MenuMain), "loadButton").GetValue(__instance) as Button;
        if (load == null) return;
        var list = load.transform.parent.parent;
        Column = list;
        if (list.Find(WrapperName) == null) AddButton(list, load);
        // every time: which buttons show depends on the saves (Continue, Load Game, Gallery)
        Space(list);
        Zigzag(list);
    }

    // where the column goes, as fractions of the screen height from the top: the first button a bit below the
    // logo, Quit just clear of the bottom edge; buttons at most MaxPitch apart
    private const float TopLimit = 560f / 1080f, BottomLimit = 995f / 1080f, MaxPitch = 76f, RowHeight = 80f;

    /// <summary>
    /// Even spacing. The column is a vertical layout of 80-unit rows centred 30% up the screen, so each
    /// extra row grew it towards the logo; each button also had its own nudge up or down, made for its
    /// original neighbours. Hidden buttons' rows are hidden too (the game hides only the button, which
    /// left a gap), the nudges go, and the spacing and height fit the buttons that show.
    /// </summary>
    // rows this mod hid because the game hid their button; only these are ever shown again by the mod
    private static readonly HashSet<Transform> hiddenRows = new();

    private static void Space(Transform list)
    {
        var rows = new List<RectTransform>();
        for (int i = 0; i < list.childCount; i++)
        {
            var row = list.GetChild(i);
            var button = row.GetComponentInChildren<Button>(true);
            if (button == null || button.transform is not RectTransform rect) continue;
            // the game hides some buttons (Continue, Load Game) but not their row, which would leave a gap;
            // rows the game hid itself (e.g. a locked Gallery) stay hidden
            if (row.gameObject.activeSelf && !button.gameObject.activeSelf) { row.gameObject.SetActive(false); hiddenRows.Add(row); }
            else if (!row.gameObject.activeSelf && button.gameObject.activeSelf && hiddenRows.Remove(row)) row.gameObject.SetActive(true);
            if (!row.gameObject.activeSelf) continue;
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, button.name == "CoopMultiplayer" ? OurDrop : 0f);
            // rows are 80 tall except Options (100) and Gallery (50), which made their gaps uneven
            var rowRect = (RectTransform)row;
            rowRect.sizeDelta = new Vector2(rowRect.sizeDelta.x, RowHeight);
            rows.Add(rowRect);
        }
        if (rows.Count < 2 || list.GetComponent<VerticalLayoutGroup>() is not { } layout ||
            list is not RectTransform column || column.parent is not RectTransform frame) return;
        float height = frame.rect.height;
        float top = TopLimit * height, bottom = BottomLimit * height;
        float pitch = Mathf.Min(MaxPitch, (bottom - top) / (rows.Count - 1));
        layout.spacing = pitch - RowHeight;
        float centre = bottom - (rows.Count - 1) * pitch / 2f;                     // from the top of the screen
        column.anchoredPosition = new Vector2(column.anchoredPosition.x, (1f - column.anchorMin.y) * height - centre);
    }

    private static void AddButton(Transform list, Button load)
    {
        var source = list.Find("NewGame") ?? load.transform.parent;
        var go = UnityEngine.Object.Instantiate(source.gameObject, list);
        go.name = WrapperName;
        go.SetActive(true);
        go.transform.SetSiblingIndex(source.GetSiblingIndex() + 1);
        var button = go.GetComponentInChildren<Button>(true);
        if (button == null) return;
        // the menu turns each button's name into its action; a copy still called "NewGame" would start one
        button.gameObject.name = "CoopMultiplayer";
        button.gameObject.SetActive(true);
        button.onClick.RemoveAllListeners();
        for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
            button.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
        button.onClick.AddListener(() => CoopPlugin.Instance?.Ui.Open());
        if (LoadSprites())
        {
            SetSprite(button.transform.Find("Images/DefaultImage"), normalSprite!);
            SetSprite(button.transform.Find("Images/HoverImage"), hoverSprite!);
        }
        foreach (var label in go.GetComponentsInChildren<TMP_Text>(true)) label.gameObject.SetActive(false);
        if (button.transform is RectTransform rect) originalX[rect] = OurOffset;
        LinkNavigation(list, go.transform, button);
        CoopPlugin.Instance.Log.LogInfo($"Added the Multiplayer button to the main menu ({(normalSprite != null ? "sticker art" : "no art found")})");
    }

    /// <summary>
    /// The menu staggers its buttons left and right, each at its own offset. With ours added (and some
    /// hidden), the visible ones alternate: the first keeps its side, each next one takes the other side
    /// at its own distance from the middle. Quit stays in the middle.
    /// </summary>
    private static void Zigzag(Transform list)
    {
        bool? previousLeft = null;
        for (int i = 0; i < list.childCount; i++)
        {
            var row = list.GetChild(i);
            if (row.name == "Quit" || !row.gameObject.activeSelf) continue;
            var button = row.GetComponentInChildren<Button>(false);
            if (button == null || button.transform is not RectTransform rect) continue;
            if (!originalX.TryGetValue(rect, out float x)) originalX[rect] = x = rect.anchoredPosition.x;
            bool left = previousLeft.HasValue ? !previousLeft.Value : x < 0f;
            rect.anchoredPosition = new Vector2(left ? -Mathf.Abs(x) : Mathf.Abs(x), rect.anchoredPosition.y);
            previousLeft = left;
        }
    }

    private static void SetSprite(Transform? slot, Sprite sprite)
    {
        if (slot != null && slot.GetComponent<Image>() is { } image) image.sprite = sprite;
    }

    /// <summary>Up/down with keys or a controller goes through the new button when the menu wires them by hand.</summary>
    private static void LinkNavigation(Transform list, Transform mine, Button button)
    {
        if (button.navigation.mode != Navigation.Mode.Explicit) return;   // automatic navigation finds it by position
        Button? Neighbour(int step)
        {
            for (int i = mine.GetSiblingIndex() + step; i >= 0 && i < list.childCount; i += step)
                if (list.GetChild(i).gameObject.activeSelf && list.GetChild(i).GetComponentInChildren<Button>() is { } b) return b;
            return null;
        }
        var up = Neighbour(-1);
        var down = Neighbour(1);
        var nav = button.navigation;
        nav.selectOnUp = up;
        nav.selectOnDown = down;
        button.navigation = nav;
        if (up != null) { var n = up.navigation; n.selectOnDown = button; up.navigation = n; }
        if (down != null) { var n = down.navigation; n.selectOnUp = button; down.navigation = n; }
    }

    /// <summary>The sticker art is embedded in this DLL (Assets/multiplayer*.png), so installing stays one file.</summary>
    private static bool LoadSprites()
    {
        if (normalSprite != null && hoverSprite != null) return true;
        normalSprite = LobbyLook.Art("multiplayer.png");
        hoverSprite = LobbyLook.Art("multiplayer_hover.png");
        return normalSprite != null && hoverSprite != null;
    }
}
