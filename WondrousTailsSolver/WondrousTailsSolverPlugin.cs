﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Plugin;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Sheets = Lumina.Excel.GeneratedSheets;

namespace WondrousTailsSolver
{
    /// <summary>
    /// Main plugin implementation.
    /// </summary>
    public sealed unsafe partial class WondrousTailsSolverPlugin : IDalamudPlugin
    {
        private static readonly UIGlowPayload GoldenGlow = new(2);
        private static readonly UIForegroundPayload SecretDelimiter = new(51);
        private static readonly UIForegroundPayload HappyGreen = new(67);
        private static readonly UIForegroundPayload MellowYellow = new(66);
        private static readonly UIForegroundPayload PinkSalmon = new(561);
        private static readonly UIForegroundPayload AngryRed = new(704);
        private static readonly UIForegroundPayload ColorOff = UIForegroundPayload.UIForegroundOff;
        private static readonly UIGlowPayload GlowOff = UIGlowPayload.UIGlowOff;

        private static readonly TextPayload ErrorText = new("error");
        private static readonly TextPayload ChancesText = new("Line Chances: ");
        private static readonly TextPayload ShuffleText = new("\rShuffle Average: ");

        private readonly PerfectTails perfectTails = new();
        private readonly bool[] gameState = new bool[16];

        [Signature("88 05 ?? ?? ?? ?? 8B 43 18", ScanType = ScanType.StaticAddress)]
        private readonly WondrousTails* wondrousTailsData = null!;

        [Signature("48 89 6C 24 ?? 48 89 74 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B F9 41 0F B6 E8")]
        private readonly delegate* unmanaged<AgentInterface*, uint, byte, IntPtr> openRegularDuty = null!;

        [Signature("E9 ?? ?? ?? ?? 8B 93 ?? ?? ?? ?? 48 83 C4 20")]
        private readonly delegate* unmanaged<AgentInterface*, byte, byte, IntPtr> openRouletteDuty = null!;

        [Signature("40 53 48 83 EC 30 F6 81 ?? ?? ?? ?? ?? 48 8B D9 0F 29 74 24 ?? 0F 28 F1 0F 84 ?? ?? ?? ?? 80 B9 ?? ?? ?? ?? ?? 48 89 6C 24 ??", DetourName = nameof(AddonUpdateDetour))]
        private readonly Hook<AddonUpdateDelegate> addonUpdateHook = null!;

        private Hook<DutyReceiveEventDelegate>? addonDutyReceiveEventHook = null;

        private SeString? lastCalculatedChancesSeString;

        /// <summary>
        /// Initializes a new instance of the <see cref="WondrousTailsSolverPlugin"/> class.
        /// </summary>
        /// <param name="pluginInterface">Dalamud plugin interface.</param>
        public WondrousTailsSolverPlugin(DalamudPluginInterface pluginInterface)
        {
            pluginInterface.Create<Service>();

            FFXIVClientStructs.Resolver.Initialize();
            SignatureHelper.Initialise(this, true);
        }

        private delegate void AddonUpdateDelegate(IntPtr addonPtr, float deltaLastUpdate);

        private delegate void DutyReceiveEventDelegate(IntPtr addonPtr, ushort a2, uint a3, IntPtr a4, IntPtr a5);

        /// <inheritdoc/>
        public string Name => "ezWondrousTails";

        /// <inheritdoc/>
        public void Dispose()
        {
            this.addonDutyReceiveEventHook?.Dispose();
        }

        private unsafe void AddonUpdateDetour(IntPtr addonPtr, float deltaLastUpdate)
        {
            this.addonUpdateHook.Original(addonPtr, deltaLastUpdate);

            try
            {
                var addon = (AddonWeeklyBingo*)addonPtr;

                if (!addon->AtkUnitBase.IsVisible || addon->AtkUnitBase.UldManager.LoadedState != 3)
                {
                    PluginLog.Debug("Addon not ready yet");
                    this.lastCalculatedChancesSeString = null;
                    return;
                }

                if (this.addonDutyReceiveEventHook == null)
                {
                    var dutyReceiveEvent = (IntPtr)addon->DutySlotList.DutySlot1.vtbl[2];
                    this.addonDutyReceiveEventHook = Hook<DutyReceiveEventDelegate>.FromAddress(dutyReceiveEvent, this.AddonDutyReceiveEventDetour);
                    this.addonDutyReceiveEventHook.Enable();
                }

                var stateChanged = this.UpdateGameState(addon);

                if (stateChanged)
                {
                    var placedStickers = this.gameState.Count(b => b);
                    if (placedStickers == 0 || placedStickers == 16 || placedStickers > 7)
                    {
                        // 0 and 16 are seen when the addon is loading. > 7 shuffling is disabled
                        this.lastCalculatedChancesSeString = null;
                        return;
                    }

                    var sb = new StringBuilder();
                    for (int i = 0; i < this.gameState.Length; i++)
                    {
                        sb.Append(this.gameState[i] ? "X" : "O");
                        if ((i + 1) % 4 == 0) sb.Append(' ');
                    }

                    PluginLog.Debug($"State has changed: {sb}");

                    var textNode = addon->StringThing.TextNode;
                    var existingBytes = this.ReadSeStringBytes(textNode);
                    var existingSeString = SeString.Parse(existingBytes);

                    this.RemoveProbabilityString(existingSeString);

                    var probString = this.lastCalculatedChancesSeString = this.SolveAndGetProbabilitySeString();
                    existingSeString.Append(probString);

                    textNode->SetText(existingSeString.Encode());
                }
                else if (this.lastCalculatedChancesSeString != null)
                {
                    var textNode = addon->StringThing.TextNode;
                    var existingBytes = this.ReadSeStringBytes(textNode);
                    var existingSeString = SeString.Parse(existingBytes);

                    // Check for the Chances textPayload, if it doesn't exist we add the last known probString
                    if (!this.SeStringContainsProbabilityString(existingSeString))
                    {
                        existingSeString.Append(this.lastCalculatedChancesSeString);
                        textNode->SetText(existingSeString.Encode());
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Boom");
            }
        }

        private void AddonDutyReceiveEventDetour(IntPtr dutyPtr, ushort a2, uint a3, IntPtr a4, IntPtr a5)
        {
            this.addonDutyReceiveEventHook?.Original(dutyPtr, a2, a3, a4, a5);

            try
            {
                if (this.wondrousTailsData == null)
                    return;

                var duty = (DutySlot*)dutyPtr;
                var status = this.wondrousTailsData->TaskStatus(duty->index);
                if (status == ButtonState.Completable)
                {
                    var orderDataID = this.wondrousTailsData->Tasks[duty->index];
                    var orderDataSheet = Service.DataManager.GetExcelSheet<Sheets.WeeklyBingoOrderData>()!;
                    var orderDataRow = orderDataSheet.GetRow(orderDataID)!;

                    var contentID = orderDataRow.Data;
                    if (contentID < 1000)
                    {
                        uint cfcID = orderDataID switch
                        {
                            // CFC => WBO
                            001 => 004, // Dungeons (Lv. 1-49)  => Sastasha
                            002 => 010, // Dungeons (Lv. 50)    => Wanderer's Palace
                            003 => 036, // Dungeons (Lv. 51-59) => Dusk Vigil
                            004 => 038, // Dungeons (Lv. 60)    => Aetherochemical Research Facility
                            059 => 238, // Dungeons (Lv. 61-69) => Sirensong Sea
                            060 => 247, // Dungeons (Lv. 70)    => Ala Mhigo
                            085 => 676, // Dungeons (Lv. 71-79) => Holminster Switch
                            086 => 652, // Dungeons (Lv. 80)    => Amaurot
                            108 => 783, // Dungeons (Lv. 81-89) => Tower of Zot
                            109 => 792, // Dungeons (Lv. 90)    => Dead Ends
                            046 => 000, // Maps
                            053 => 000, // PotD/HoH
                            052 => 862, // Crystalline Conflict => Crystalline Conflict (Custom Match - The Palaistra)
                            054 => 127, // Frontline            => Frontline: Borderland Ruins
                            067 => 277, // Rival Wings          => Rival Wings: Astralagos
                            121 => 093, // Binding Coil of Bahamut
                            122 => 098, // Second Coil of Bahamut
                            123 => 107, // Final Coil of Bahamut
                            124 => 112, // Alexander: Gordias
                            125 => 136, // Alexander: Midas
                            126 => 186, // Alexander: The Creator
                            127 => 252, // Omega: Deltascape
                            128 => 286, // Omega: Sigmascape
                            129 => 587, // Omega: Alphascape
                            _ => 0,
                        };

                        if (cfcID == 0)
                            return;

                        this.OpenRegularDuty(cfcID);
                    }
                    else
                    {
                        var cfcSheet = Service.DataManager.GetExcelSheet<Sheets.ContentFinderCondition>()!;
                        var contents = cfcSheet.FirstOrDefault(row => row.Content == contentID);
                        if (contents == default)
                            return;

                        var cfcID = contents.RowId;
                        this.OpenRegularDuty(cfcID);
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Boom");
            }
        }

        private unsafe bool UpdateGameState(AddonWeeklyBingo* addon)
        {
            if (this.wondrousTailsData == null)
                return false;

            var stateChanged = false;
            for (var i = 0; i < 16; i++)
            {
                var imageNode = (AtkImageNode*)addon->StickerSlotList[i].StickerSidebarResNode->ChildNode;

                if (imageNode == null)
                    return false;

                var state = imageNode->AtkResNode.Alpha_2 == 0;
                stateChanged |= this.gameState[i] != state;
                this.gameState[i] = state;
            }

            return stateChanged;
        }

        private byte[] ReadSeStringBytes(AtkTextNode* node)
        {
            var utf8str = &node->NodeText;

            if (node == null || utf8str->StringPtr == null || utf8str->BufUsed <= 1)
                return Array.Empty<byte>();

            var span = new Span<byte>(utf8str->StringPtr, (int)(utf8str->BufUsed - 1));
            return span.ToArray();
        }

        private SeString SolveAndGetProbabilitySeString()
        {
            var stickersPlaced = this.gameState.Count(s => s);

            // > 9 returns Error {-1,-1,-1} by the solver
            var values = this.perfectTails.Solve(this.gameState);

            double[]? samples = null;
            if (stickersPlaced > 0 && stickersPlaced <= 7)
                samples = this.perfectTails.GetSample(stickersPlaced);

            if (values == PerfectTails.Error)
            {
                var seString = new SeString(new List<Payload>())
                    .Append(SecretDelimiter).Append("\r").Append(ColorOff)
                    .Append(ChancesText)
                    .Append(AngryRed).Append(ErrorText).Append(ColorOff).Append("  ")
                    .Append(AngryRed).Append(ErrorText).Append(ColorOff).Append("  ")
                    .Append(AngryRed).Append(ErrorText).Append(ColorOff);
                return seString;
            }
            else
            {
                var valuePayloads = this.StringFormatDoubles(values);
                var seString = new SeString(new List<Payload>())
                    .Append(SecretDelimiter).Append("\r").Append(ColorOff)
                    .Append(ChancesText);

                if (samples != null)
                {
                    foreach (var (value, sample, valuePayload) in Enumerable.Range(0, values.Length).Select(i => (values[i], samples[i], valuePayloads[i])))
                    {
                        var bound = 0.05;
                        var sampleBoundLower = Math.Max(0, sample - bound);
                        var sampleBoundUpper = Math.Min(1, sample + bound);

                        if (value == 1)
                        {
                            seString.Append(GoldenGlow).Append(valuePayload).Append(GlowOff);
                        }
                        else if (value < 1 && value >= sample)
                        {
                            seString.Append(HappyGreen).Append(valuePayload).Append(ColorOff);
                        }
                        else if (sample > value && value > sampleBoundLower)
                        {
                            seString.Append(MellowYellow).Append(valuePayload).Append(ColorOff);
                        }
                        else if (sampleBoundLower > value && value > 0)
                        {
                            seString.Append(PinkSalmon).Append(valuePayload).Append(ColorOff);
                        }
                        else if (value == 0)
                        {
                            seString.Append(AngryRed).Append(valuePayload).Append(ColorOff);
                        }
                        else
                        {
                            // Just incase
                            seString.Append(valuePayload);
                        }

                        seString.Append("  ");
                    }

                    seString.Append(ShuffleText);
                    var sampleStrings = this.StringFormatDoubles(samples);
                    foreach (var sampleString in sampleStrings)
                        seString.Append(sampleString).Append("  ");
                }
                else
                {
                    foreach (var valueString in valuePayloads)
                        seString.Append(valueString).Append("  ");
                }

                return seString;
            }
        }

        private TextPayload[] StringFormatDoubles(double[] values)
            => values.Select(v => new TextPayload($"{v * 100:F2}%")).ToArray();

        private bool SeStringTryFindDelimiter(SeString seString, out int index)
        {
            var secretBytes = SecretDelimiter.Encode();
            for (var i = 0; i < seString.Payloads.Count; i++)
            {
                var payload = seString.Payloads[i];
                if (payload is UIForegroundPayload)
                {
                    if (Enumerable.SequenceEqual(payload.Encode(), secretBytes))
                    {
                        index = i;
                        return true;
                    }
                }
            }

            index = -1;
            return false;
        }

        private bool SeStringContainsProbabilityString(SeString seString)
            => this.SeStringTryFindDelimiter(seString, out var _);

        private SeString RemoveProbabilityString(SeString seString)
        {
            if (this.SeStringTryFindDelimiter(seString, out var index))
            {
                var removeCount = seString.Payloads.Count - index;
                try
                {
                    seString.Payloads.RemoveRange(index, removeCount);
                }
                catch (ArgumentException)
                {
                    PluginLog.Warning($"ArgExc during RemoveProbabilityString, count={seString.Payloads.Count} index={index} removeCount={removeCount}");
                }
            }

            return seString;
        }

        private AgentInterface* GetAgentContentsFinder()
        {
            var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            var uiModule = framework->GetUiModule();
            var agentModule = uiModule->GetAgentModule();
            return agentModule->GetAgentByInternalId(AgentId.ContentsFinder);
        }

        private void OpenRegularDuty(uint contentFinderCondition)
        {
            var agent = this.GetAgentContentsFinder();
            PluginLog.Debug($"OpenRegularDuty 0x{(IntPtr)agent:X} #{contentFinderCondition}");
            this.openRegularDuty(agent, contentFinderCondition, 0);
        }

        [StructLayout(LayoutKind.Explicit)]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1202:Elements should be ordered by access", Justification = "Offset ordering")]
        private struct WondrousTails
        {
            [FieldOffset(0x06)]
            public fixed byte Tasks[16];

            [FieldOffset(0x16)]
            public uint Rewards;

            [FieldOffset(0x1A)]
            private readonly ushort stickerBits;

            [FieldOffset(0x1E)]
            public ushort WeeklyKey;

            [FieldOffset(0x20)]
            private readonly ushort secondChanceBits;

            [FieldOffset(0x22)]
            private fixed byte taskStatus[4];

            public int Stickers
                => CountSetBits(this.stickerBits);

            public int SecondChance
                => (this.secondChanceBits >> 7) & 0b1111;

            public ButtonState TaskStatus(int idx)
                => (ButtonState)((this.taskStatus[idx >> 2] >> ((idx & 0b11) * 2)) & 0b11);

            private static int CountSetBits(int n)
            {
                int count = 0;
                while (n > 0)
                {
                    count += n & 1;
                    n >>= 1;
                }

                return count;
            }
        }
    }
}
