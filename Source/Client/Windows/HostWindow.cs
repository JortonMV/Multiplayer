﻿#region

using System.Net;
using Multiplayer.Common;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Profile;
using Verse.Sound;
using Verse.Steam;

#endregion

namespace Multiplayer.Client
{
    [HotSwappable]
    public class HostWindow : Window
    {
        private readonly SaveFile file;

        private readonly ServerSettings settings = new ServerSettings();
        private readonly bool withSimulation;
        private bool asyncTime;
        private bool debugMode;

        private float height;
        private bool lan, direct;

        private string maxPlayersBuffer, autosaveBuffer, addressBuffer;
        public bool returnToServerBrowser;

        public HostWindow(SaveFile file = null, bool withSimulation = false)
        {
            closeOnAccept = false;
            doCloseX = true;

            this.withSimulation = withSimulation;
            this.file = file;
            settings.gameName = file?.gameName ?? Multiplayer.session?.gameName ?? $"{Multiplayer.username}'s game";

            string localAddr = MpUtil.GetLocalIpAddress() ?? "127.0.0.1";
            settings.lanAddress = localAddr;
            addressBuffer = localAddr;

            lan = true;
            settings.arbiter = true;

            if (MpVersion.IsDebug)
                debugMode = true;
        }

        public override Vector2 InitialSize => new Vector2(450f, height + 45f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.UpperCenter;
            string title;

            if (file == null)
                title = "MpHostIngame".Translate();
            else if (file.replay)
                title = "MpHostReplay".Translate();
            else
                title = "MpHostSavefile".Translate();

            Widgets.Label(inRect.Down(0), title);
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;

            Rect entry = new Rect(0, 45, inRect.width, 30f);

            float labelWidth = 110f;

            settings.gameName =
                TextEntryLabeled(entry, $"{"MpGameName".Translate()}:  ", settings.gameName, labelWidth);
            entry = entry.Down(40);

            TextFieldNumericLabeled(entry.Width(labelWidth + 30f), $"{"MpMaxPlayers".Translate()}:  ",
                ref settings.maxPlayers, ref maxPlayersBuffer, labelWidth, 0, 999);

            TextFieldNumericLabeled(entry.Right(150f).Width(labelWidth + 85f), $"{"MpAutosaveEvery".Translate()} ",
                ref settings.autosaveInterval, ref autosaveBuffer, labelWidth + 50f, 0, 999);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(entry.Right(200f).Right(labelWidth + 35f), $" {"MpAutosaveMinutes".Translate()}");
            Text.Anchor = TextAnchor.UpperLeft;
            entry = entry.Down(40);

            /*const char passChar = '\u2022';
            if (Event.current.type == EventType.Repaint || Event.current.isMouse)
                TextEntryLabeled(entry.Width(200), "Password:  ", new string(passChar, password.Length), labelWidth);
            else
                password = TextEntryLabeled(entry.Width(200), "Password:  ", password, labelWidth);
            entry = entry.Down(40);*/

            float checkboxWidth = labelWidth + 30f;

            string directLabel = $"{"MpDirect".Translate()}:  ";
            float directLabelWidth = Text.CalcSize(directLabel).x;
            CheckboxLabeled(entry.Width(checkboxWidth), directLabel, ref direct, placeTextNearCheckbox: true);
            if (direct)
                addressBuffer = Widgets.TextField(entry.Width(checkboxWidth + 10).Right(checkboxWidth + 10),
                    addressBuffer);

            entry = entry.Down(30);

            Rect lanRect = entry.Width(checkboxWidth);
            CheckboxLabeled(lanRect, $"{"MpLan".Translate()}:  ", ref lan, placeTextNearCheckbox: true);
            TooltipHandler.TipRegion(lanRect,
                $"{"MpLanDesc1".Translate()}\n\n{"MpLanDesc2".Translate(settings.lanAddress)}");

            entry = entry.Down(30);

            if (SteamManager.Initialized)
            {
                CheckboxLabeled(entry.Width(checkboxWidth), $"{"MpSteam".Translate()}:  ", ref settings.steam,
                    placeTextNearCheckbox: true);
                entry = entry.Down(30);
            }

            TooltipHandler.TipRegion(entry.Width(checkboxWidth), "MpArbiterDesc".Translate());
            CheckboxLabeled(entry.Width(checkboxWidth), "The Arbiter:  ", ref settings.arbiter,
                placeTextNearCheckbox: true);
            entry = entry.Down(30);

            /*if (MpVersion.IsDebug)
            {
                TooltipHandler.TipRegion(entry.Width(checkboxWidth), $"{"MpAsyncTimeDesc".Translate()}\n\n{"MpExperimentalFeature".Translate()}");
                CheckboxLabeled(entry.Width(checkboxWidth), "Async time:  ", ref asyncTime, placeTextNearCheckbox: true);
                entry = entry.Down(30);
            }*/

            if (Prefs.DevMode)
            {
                CheckboxLabeled(entry.Width(checkboxWidth), "Debug mode:  ", ref debugMode,
                    placeTextNearCheckbox: true);
                entry = entry.Down(30);
            }

            if (Event.current.type == EventType.layout && height != entry.yMax)
            {
                height = entry.yMax;
                SetInitialSizeAndPosition();
            }

            Rect buttonRect = new Rect((inRect.width - 100f) / 2f, inRect.height - 35f, 100f, 35f);

            if (Widgets.ButtonText(buttonRect, "MpHostButton".Translate()))
                TryHost();
        }

        private void TryHost()
        {
            if (direct && !TryParseIp(addressBuffer, out settings.bindAddress, out settings.bindPort))
                return;

            if (settings.gameName.NullOrEmpty())
            {
                Messages.Message("MpInvalidGameName".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (!direct)
                settings.bindAddress = null;

            if (!lan)
                settings.lanAddress = null;

            if (file?.replay ?? Multiplayer.IsReplay)
                HostFromReplay(settings);
            else if (file == null)
                ClientUtil.HostServer(settings, false, debugMode: debugMode);
            else
                HostFromSave(settings);

            Close(true);
        }

        private bool TryParseIp(string ip, out string addr, out int port)
        {
            port = MultiplayerServer.DefaultPort;
            addr = null;

            string[] parts = ip.Split(':');

            if (!IPAddress.TryParse(parts[0], out IPAddress ipAddr))
            {
                Messages.Message("MpInvalidAddress".Translate(), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            addr = parts[0];

            if (parts.Length >= 2 && (!int.TryParse(parts[1], out port) || port < 0 || port > ushort.MaxValue))
            {
                Messages.Message("MpInvalidPort".Translate(), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            return true;
        }

        public static void CheckboxLabeled(Rect rect, string label, ref bool checkOn, bool disabled = false,
            Texture2D texChecked = null, Texture2D texUnchecked = null, bool placeTextNearCheckbox = false)
        {
            TextAnchor anchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;

            if (placeTextNearCheckbox)
            {
                float textWidth = Text.CalcSize(label).x;
                rect.x = rect.xMax - textWidth - 24f - 5f;
                rect.width = textWidth + 24f + 5f;
            }

            Widgets.Label(rect, label);

            if (!disabled && Widgets.ButtonInvisible(rect, false))
            {
                checkOn = !checkOn;
                if (checkOn)
                    SoundDefOf.Checkbox_TurnedOn.PlayOneShotOnCamera(null);
                else
                    SoundDefOf.Checkbox_TurnedOff.PlayOneShotOnCamera(null);
            }

            Widgets.CheckboxDraw(rect.x + rect.width - 24f, rect.y, checkOn, disabled, 24f, null, null);
            Text.Anchor = anchor;
        }

        public static string TextEntryLabeled(Rect rect, string label, string text, float labelWidth)
        {
            Rect labelRect = rect.Rounded();
            labelRect.width = labelWidth;
            Rect fieldRect = rect;
            fieldRect.xMin += labelWidth;
            TextAnchor anchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(labelRect, label);
            Text.Anchor = anchor;
            return Widgets.TextField(fieldRect, text);
        }

        public static void TextFieldNumericLabeled(Rect rect, string label, ref int val, ref string buffer,
            float labelWidth, float min = 0, float max = float.MaxValue)
        {
            Rect labelRect = rect;
            labelRect.width = labelWidth;
            Rect fieldRect = rect;
            fieldRect.xMin += labelWidth;
            TextAnchor anchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(labelRect, label);
            Text.Anchor = anchor;
            Widgets.TextFieldNumeric(fieldRect, ref val, ref buffer, min, max);
        }

        public override void PostClose()
        {
            if (returnToServerBrowser)
                Find.WindowStack.Add(new ServerBrowser());
        }

        private void HostFromSave(ServerSettings settings)
        {
            LongEventHandler.QueueLongEvent(() =>
            {
                MemoryUtility.ClearAllMapsAndWorld();
                Current.Game = new Game();
                Current.Game.InitData = new GameInitData();
                Current.Game.InitData.gameToLoad = file.displayName;

                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    LongEventHandler.QueueLongEvent(
                        () => ClientUtil.HostServer(settings, false, debugMode: debugMode), "MpLoading", false,
                        null);
                });
            }, "Play", "LoadingLongEvent", true, null);
        }

        private void HostFromReplay(ServerSettings settings)
        {
            void ReplayLoaded()
            {
                ClientUtil.HostServer(settings, true, withSimulation, debugMode);
            }

            if (file != null)
                Replay.LoadReplay(
                    file.file,
                    true,
                    ReplayLoaded,
                    GenScene.GoToMainMenu,
                    "MpSimulatingServer"
                );
            else
                ReplayLoaded();
        }
    }
}