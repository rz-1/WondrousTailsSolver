﻿using Dalamud.Plugin;
using System;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.Internal;

namespace WondrousTailsSolver
{
    public sealed class WondrousTailsSolverPlugin : IDalamudPlugin
    {
        public string Name => "ezWondrousTails";

        internal DalamudPluginInterface Interface;
        internal PluginAddressResolver Address;

        private AtkTextNode_SetText_Delegate AtkTextNode_SetText;

        public void Initialize(DalamudPluginInterface pluginInterface)
        {
            Interface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface), "DalamudPluginInterface cannot be null");

            Address = new PluginAddressResolver();
            Address.Setup(Interface.TargetModuleScanner);

            AtkTextNode_SetText = Marshal.GetDelegateForFunctionPointer<AtkTextNode_SetText_Delegate>(Address.AtkTextNode_SetText_Address);

            Interface.Framework.OnUpdateEvent += GameUpdater;
        }

        public void Dispose()
        {
            Interface.Framework.OnUpdateEvent -= GameUpdater;
        }

        private Task GameTask;
        private readonly bool[] GameState = new bool[16];
        private readonly PerfectTails PerfectTails = new PerfectTails();

        private void GameUpdater(Framework framework)
        {
            try
            {
                if (GameTask == null || GameTask.IsCompleted || GameTask.IsFaulted || GameTask.IsCanceled)
                {
                    GameTask = new Task(GameUpdater);
                    GameTask.Start();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Updater has crashed");
                Interface.Framework.Gui.Chat.PrintError($"{Name} has encountered a critical error");
            }
        }

        private unsafe void GameUpdater()
        {
            var addonPtr = Interface.Framework.Gui.GetUiObjectByName("WeeklyBingo", 1);
            if (addonPtr == IntPtr.Zero)
                return;

            var addon = (AddonWeeklyBingo*)addonPtr;
            if (addon == null)
                return;

            if (!addon->AtkUnitBase.IsVisible || addon->AtkUnitBase.ULDData.LoadedState != 3 || addon->AtkUnitBase.ULDData.NodeListCount < 96)
                return;

            var textNode = (AtkTextNode*)addon->AtkUnitBase.ULDData.NodeList[96];
            if (textNode == null)
                return;

            for (int i = 0; i < GameState.Length; i++)
                GameState[i] = true;

            var stateChanged = UpdateGameState(addon);
            var currentText = Marshal.PtrToStringAnsi(new IntPtr(textNode->NodeText.StringPtr));
            if (stateChanged || !currentText.Contains(Delimiter))
            {
                var newText = FormatProbabilityString(currentText);
                if (currentText != newText)
                {
                    SetNodeText(textNode, newText);
                }
            }
        }

        private unsafe bool UpdateGameState(AddonWeeklyBingo* addon)
        {
            var stateChanged = false;
            for (var i = 0; i < 16; i++)
            {
                var imageNode = (AtkImageNode*)addon->StickerSlotList[i].StickerSidebarResNode->ChildNode;

                if (imageNode == null)
                    return false;

                var state = imageNode->AtkResNode.Alpha_2 == 0x0;
                stateChanged |= GameState[i] != state;
                GameState[i] = state;
            }
            return stateChanged;
        }

        private string[] FormatDoubles(double[] values)
        {
            if (values == null)
                return null;

            if (values == PerfectTails.Error)
            {
                return new string[] { ErrorText, ErrorText, ErrorText };
            }
            else
            {
                return values.Select(v => $"{v * 100:F2}%").ToArray();
            }
        }

        private const string Delimiter = "      ";

        private const string ErrorText = "error";
        private const string ChancesTextBase = "1 Line: {0}\n2 Lines: {1}\n3 Lines: {2}";
        private const string ChancesShortTextBase = "Line Chances: {0}   {1}   {2}";
        private const string AverageTextBase = "Shuffle Average: {0}   {1}   {2}";

        private string FormatProbabilityString(string currentText)
        {
            var stickersPlaced = GameState.Count(s => s);

            // > 9 returns Error {-1,-1,-1} by the solver
            var probs = PerfectTails.Solve(GameState);

            double[] samples = null;
            if (stickersPlaced > 0 && stickersPlaced <= 7)
                samples = PerfectTails.GetSample(stickersPlaced);

            var delimIndex = currentText.IndexOf(Delimiter);
            if (delimIndex > 0)
                currentText = currentText.Substring(0, delimIndex);

            var sb = new StringBuilder();

            var newlines = currentText.Split('').Length - 1;  // SQEx newline contraption
            if (newlines > 2)
                sb.AppendLine(string.Format(ChancesShortTextBase, FormatDoubles(probs)));
            else
                sb.AppendLine(string.Format(ChancesTextBase, FormatDoubles(probs)));

            if (samples != null)
            {
                sb.AppendLine(string.Format(AverageTextBase, FormatDoubles(samples)));
            }

            return $"{currentText}{Delimiter}\n{sb}";
        }

        private unsafe void SetNodeText(AtkTextNode* textNode, string text)
        {
            var textNodePtr = new IntPtr(textNode);
            var textPtr = Marshal.StringToHGlobalAnsi(text);

            AtkTextNode_SetText(textNodePtr, textPtr, IntPtr.Zero);

            Marshal.FreeHGlobal(textPtr);
        }
    }
}
